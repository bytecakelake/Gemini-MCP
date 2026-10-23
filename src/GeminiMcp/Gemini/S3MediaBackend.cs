using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace GeminiMcp.Gemini;

/// <summary>S3 호환 객체 저장소 설정 (AWS S3, Cloudflare R2, SeaweedFS, Garage 등).</summary>
public sealed record S3Settings(
    Uri Endpoint, string Bucket, string Region, string AccessKey, string SecretKey, bool PathStyle);

/// <summary>
/// S3 호환 저장소 클라이언트. AWS SDK 대신 Signature V4 를 직접 구현해 Native AOT 에서도 동작합니다.
/// 메타데이터는 x-amz-meta-* 헤더에 퍼센트 인코딩해 둡니다 (S3 사용자 메타데이터는 US-ASCII 만 허용).
/// </summary>
public sealed class S3MediaBackend(S3Settings settings, HttpClient http)
{
    private const string EmptyPayloadHash = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";
    private const string MetaPrefix = "x-amz-meta-";
    /// <summary>프롬프트 메타데이터 최대 길이 (인코딩 후 바이트). S3 사용자 메타데이터 전체 한도는 2KB 입니다.</summary>
    private const int MaxPromptBytes = 1200;
    private static readonly XNamespace S3Xml = "http://s3.amazonaws.com/doc/2006-03-01/";

    public string Description => $"S3 {settings.Endpoint.GetLeftPart(UriPartial.Authority)} bucket={settings.Bucket}";

    /// <summary>버킷이 없으면 만듭니다 (compose 의 내장 S3 처럼 비어 있는 저장소용).</summary>
    public async Task InitializeAsync(CancellationToken ct)
    {
        using var head = await SendAsync(HttpMethod.Head, BucketUri(), null, null, null, ct);
        if (head.IsSuccessStatusCode)
            return;
        if (head.StatusCode != HttpStatusCode.NotFound)
            throw new HttpRequestException($"S3 버킷 확인 실패 ({(int)head.StatusCode}): {settings.Bucket}", null, head.StatusCode);

        var body = settings.Region == "us-east-1"
            ? null
            : Encoding.UTF8.GetBytes(
                $"<CreateBucketConfiguration xmlns=\"{S3Xml.NamespaceName}\"><LocationConstraint>{settings.Region}</LocationConstraint></CreateBucketConfiguration>");
        using var create = await SendAsync(HttpMethod.Put, BucketUri(), body, body is null ? null : "application/xml", null, ct);
        await EnsureSuccessAsync(create, $"S3 버킷 생성 실패: {settings.Bucket}", ct);
    }

    public async Task PutAsync(string name, byte[] data, string mimeType, MediaMetadata metadata, CancellationToken ct)
    {
        var meta = new SortedDictionary<string, string>(StringComparer.Ordinal);
        void Add(string key, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
                meta[MetaPrefix + key] = Uri.EscapeDataString(value.Trim());
        }
        Add("tool", metadata.Tool);
        Add("model", metadata.Model);
        Add("consumer", metadata.Consumer);
        Add("interaction-id", metadata.InteractionId);
        if (metadata.Prompt is { } prompt)
            meta[MetaPrefix + "prompt"] = EncodeTruncated(prompt.Trim(), MaxPromptBytes);

        using var response = await SendAsync(HttpMethod.Put, ObjectUri(name), data, mimeType, meta, ct);
        await EnsureSuccessAsync(response, $"S3 업로드 실패: {name}", ct);
    }

    public async Task<MediaObject?> OpenAsync(string name, CancellationToken ct)
    {
        var response = await SendAsync(HttpMethod.Get, ObjectUri(name), null, null, null, ct, HttpCompletionOption.ResponseHeadersRead);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            response.Dispose();
            return null;
        }
        try
        {
            await EnsureSuccessAsync(response, $"S3 다운로드 실패: {name}", ct);
            var headers = response.Content.Headers;
            var stream = await response.Content.ReadAsStreamAsync(ct);
            return new MediaObject(stream, headers.ContentLength, MimeOf(headers, name), headers.LastModified?.UtcDateTime ?? DateTime.UtcNow, response);
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    /// <summary>ListObjectsV2 한 페이지 + 객체별 HEAD(메타데이터). cursor 는 S3 continuation token 입니다.</summary>
    public async Task<MediaPage> ListAsync(string? cursor, int limit, CancellationToken ct)
    {
        var (objects, next) = await ListObjectsAsync(cursor, limit, ct);
        var items = new MediaInfo?[objects.Count];
        await Parallel.ForEachAsync(Enumerable.Range(0, objects.Count), new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = ct },
            async (i, token) => items[i] = await HeadAsync(objects[i].Name, objects[i], token));
        return new MediaPage(items.OfType<MediaInfo>().ToList(), next);
    }

    public async Task<MediaStats> StatsAsync(CancellationToken ct)
    {
        long count = 0, bytes = 0;
        string? cursor = null;
        do
        {
            var (objects, next) = await ListObjectsAsync(cursor, 1000, ct);
            count += objects.Count;
            bytes += objects.Sum(o => o.Size);
            cursor = next;
        }
        while (cursor is not null);
        return new MediaStats(count, bytes, Description);
    }

    /// <returns>지웠으면 true, 없으면 false (S3 DELETE 는 없는 키에도 204 를 주므로 HEAD 로 먼저 확인합니다).</returns>
    public async Task<bool> DeleteAsync(string name, CancellationToken ct)
    {
        using (var head = await SendAsync(HttpMethod.Head, ObjectUri(name), null, null, null, ct))
        {
            if (head.StatusCode == HttpStatusCode.NotFound)
                return false;
            await EnsureSuccessAsync(head, $"S3 조회 실패: {name}", ct);
        }
        using var response = await SendAsync(HttpMethod.Delete, ObjectUri(name), null, null, null, ct);
        await EnsureSuccessAsync(response, $"S3 삭제 실패: {name}", ct);
        return true;
    }

    private sealed record S3Object(string Name, long Size, DateTimeOffset LastModified);

    private async Task<(List<S3Object> Objects, string? Next)> ListObjectsAsync(string? cursor, int limit, CancellationToken ct)
    {
        var query = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["list-type"] = "2",
            ["max-keys"] = limit.ToString(CultureInfo.InvariantCulture),
        };
        if (!string.IsNullOrEmpty(cursor))
            query["continuation-token"] = cursor;

        using var response = await SendAsync(HttpMethod.Get, BucketUri(query), null, null, null, ct);
        await EnsureSuccessAsync(response, "S3 목록 조회 실패", ct);
        var root = XDocument.Parse(await response.Content.ReadAsStringAsync(ct)).Root!;

        var objects = new List<S3Object>();
        // 일부 호환 서버는 네임스페이스 없이 응답하므로 로컬 이름으로 찾습니다.
        foreach (var item in root.Elements().Where(e => e.Name.LocalName == "Contents"))
        {
            // 미디어 이름 형식이 아닌 객체(버킷을 함께 쓰는 다른 파일)는 건너뜁니다.
            var name = Child(item, "Key");
            if (name is null || MediaStore.ToName(name) != name)
                continue;
            var size = long.TryParse(Child(item, "Size"), CultureInfo.InvariantCulture, out var s) ? s : 0;
            var modified = DateTimeOffset.TryParse(Child(item, "LastModified"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var m)
                ? m
                : DateTimeOffset.UtcNow;
            objects.Add(new S3Object(name, size, modified));
        }
        var next = Child(root, "IsTruncated") == "true" ? Child(root, "NextContinuationToken") : null;
        return (objects, string.IsNullOrEmpty(next) ? null : next);
    }

    private async Task<MediaInfo?> HeadAsync(string name, S3Object listed, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Head, ObjectUri(name), null, null, null, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null; // 목록 조회와 HEAD 사이에 지워진 객체
        await EnsureSuccessAsync(response, $"S3 조회 실패: {name}", ct);

        string? Meta(string key) =>
            response.Headers.TryGetValues(MetaPrefix + key, out var values) && values.FirstOrDefault() is { Length: > 0 } value
                ? Decode(value)
                : null;

        var headers = response.Content.Headers;
        return new MediaInfo(
            name,
            headers.ContentLength ?? listed.Size,
            MimeOf(headers, name),
            headers.LastModified ?? listed.LastModified,
            Meta("tool"), Meta("model"), Meta("prompt"), Meta("consumer"), Meta("interaction-id"));
    }

    private static string MimeOf(HttpContentHeaders headers, string name) =>
        headers.ContentType?.MediaType is { Length: > 0 } type && type != "application/octet-stream" ? type : MimeTypes.FromPath(name);

    private static string Decode(string value)
    {
        try
        {
            return Uri.UnescapeDataString(value);
        }
        catch (UriFormatException)
        {
            return value;
        }
    }

    /// <summary>퍼센트 인코딩 결과가 maxBytes 를 넘지 않도록 앞에서부터 잘라 인코딩합니다 (서로게이트 쌍은 나누지 않음).</summary>
    private static string EncodeTruncated(string value, int maxBytes)
    {
        var encoded = Uri.EscapeDataString(value);
        if (encoded.Length <= maxBytes)
            return encoded;

        var result = new StringBuilder();
        const string ellipsis = "%E2%80%A6"; // …
        for (var i = 0; i < value.Length; i++)
        {
            var length = char.IsHighSurrogate(value[i]) && i + 1 < value.Length ? 2 : 1;
            var piece = Uri.EscapeDataString(value.Substring(i, length));
            if (result.Length + piece.Length + ellipsis.Length > maxBytes)
                break;
            result.Append(piece);
            i += length - 1;
        }
        return result.Append(ellipsis).ToString();
    }

    private static string? Child(XElement parent, string localName) =>
        parent.Elements().FirstOrDefault(e => e.Name.LocalName == localName)?.Value;

    private Uri ObjectUri(string name) => Build("/" + Escape(name), null);

    private Uri BucketUri(SortedDictionary<string, string>? query = null) => Build("/", query);

    /// <summary>path-style(https://host/bucket/key) 또는 virtual-hosted(https://bucket.host/key) 주소.</summary>
    private Uri Build(string path, SortedDictionary<string, string>? query)
    {
        var endpoint = settings.Endpoint;
        var basePath = endpoint.AbsolutePath.TrimEnd('/');
        var builder = new UriBuilder(endpoint);
        if (settings.PathStyle)
            builder.Path = $"{basePath}/{settings.Bucket}{(path == "/" ? "" : path)}";
        else
        {
            builder.Host = $"{settings.Bucket}.{endpoint.Host}";
            builder.Path = basePath + path;
        }
        builder.Query = query is null ? "" : CanonicalQuery(query);
        return builder.Uri;
    }

    /// <summary>S3 키 인코딩: '/' 는 그대로, 나머지는 RFC 3986 unreserved 외 모두 퍼센트 인코딩.</summary>
    private static string Escape(string key) => string.Join('/', key.Split('/').Select(Uri.EscapeDataString));

    private static string CanonicalQuery(SortedDictionary<string, string> query) =>
        string.Join('&', query.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method, Uri uri, byte[]? body, string? contentType, SortedDictionary<string, string>? amzHeaders, CancellationToken ct,
        HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead)
    {
        using var request = new HttpRequestMessage(method, uri);
        if (body is not null)
        {
            request.Content = new ByteArrayContent(body);
            if (contentType is not null)
                request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        }
        Sign(request, uri, body is null ? EmptyPayloadHash : Hex(SHA256.HashData(body)), amzHeaders);
        return await http.SendAsync(request, completion, ct);
    }

    /// <summary>
    /// AWS Signature Version 4 (헤더 방식). host, x-amz-content-sha256, x-amz-date 와
    /// 요청에 넣는 모든 x-amz-meta-* 헤더를 서명합니다 (S3 는 x-amz-* 헤더의 서명을 요구).
    /// </summary>
    private void Sign(HttpRequestMessage request, Uri uri, string payloadHash, SortedDictionary<string, string>? amzHeaders)
    {
        var now = DateTime.UtcNow;
        var amzDate = now.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var date = now.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var host = uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}";

        // 정렬 순서: host < x-amz-content-sha256 < x-amz-date < x-amz-meta-*
        var headers = new List<(string Name, string Value)>
        {
            ("host", host),
            ("x-amz-content-sha256", payloadHash),
            ("x-amz-date", amzDate),
        };
        if (amzHeaders is not null)
            headers.AddRange(amzHeaders.Select(kv => (kv.Key.ToLowerInvariant(), kv.Value.Trim())));
        headers.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));

        var signedHeaders = string.Join(';', headers.Select(h => h.Name));
        var canonicalRequest = string.Join('\n',
            request.Method.Method,
            uri.AbsolutePath,
            uri.Query.TrimStart('?'),
            string.Concat(headers.Select(h => $"{h.Name}:{h.Value}\n")),
            signedHeaders,
            payloadHash);

        var scope = $"{date}/{settings.Region}/s3/aws4_request";
        var stringToSign = $"AWS4-HMAC-SHA256\n{amzDate}\n{scope}\n{Hex(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalRequest)))}";

        var key = Hmac(Encoding.UTF8.GetBytes("AWS4" + settings.SecretKey), date);
        key = Hmac(key, settings.Region);
        key = Hmac(key, "s3");
        key = Hmac(key, "aws4_request");
        var signature = Hex(Hmac(key, stringToSign));

        foreach (var (name, value) in headers.Where(h => h.Name != "host"))
            request.Headers.TryAddWithoutValidation(name, value);
        request.Headers.TryAddWithoutValidation("Authorization",
            $"AWS4-HMAC-SHA256 Credential={settings.AccessKey}/{scope}, SignedHeaders={signedHeaders}, Signature={signature}");
    }

    private static byte[] Hmac(byte[] key, string data) => HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(data));

    private static string Hex(byte[] bytes) => Convert.ToHexStringLower(bytes);

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, string message, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
            return;
        var body = response.Content is null ? "" : await response.Content.ReadAsStringAsync(ct);
        // S3 오류 본문(<Code>...</Code>)만 짧게 남깁니다. 서명 정보는 응답에 포함되지 않습니다.
        var code = body.Contains("<Code>") ? body[(body.IndexOf("<Code>") + 6)..].Split('<')[0] : "";
        throw new HttpRequestException($"{message} ({(int)response.StatusCode} {code})".Trim(), null, response.StatusCode);
    }
}
