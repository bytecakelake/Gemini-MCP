using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace GeminiMcp.Gemini;

/// <summary>S3 호환 객체 저장소 설정 (AWS S3, Cloudflare R2, SeaweedFS, Garage 등).</summary>
public sealed record S3Settings(
    Uri Endpoint, string Bucket, string Region, string AccessKey, string SecretKey, string Prefix, bool PathStyle);

/// <summary>
/// S3 호환 저장소. AWS SDK 대신 Signature V4 를 직접 구현해 Native AOT 에서도 동작합니다.
/// 만료 판단은 객체의 LastModified 기준이며, 정리는 ListObjectsV2 + DeleteObject 로 합니다 (수명 주기 규칙 불필요).
/// </summary>
public sealed class S3MediaBackend(S3Settings settings, HttpClient http) : IMediaBackend
{
    private const string EmptyPayloadHash = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";
    private static readonly XNamespace S3Xml = "http://s3.amazonaws.com/doc/2006-03-01/";

    public string Description => $"S3 {settings.Endpoint.GetLeftPart(UriPartial.Authority)} bucket={settings.Bucket} prefix={settings.Prefix}";

    /// <summary>버킷이 없으면 만듭니다 (compose 의 내장 S3 처럼 비어 있는 저장소용).</summary>
    public async Task InitializeAsync(CancellationToken ct)
    {
        using var head = await SendAsync(HttpMethod.Head, BucketUri(), null, null, ct);
        if (head.IsSuccessStatusCode)
            return;
        if (head.StatusCode != HttpStatusCode.NotFound)
            throw new HttpRequestException($"S3 버킷 확인 실패 ({(int)head.StatusCode}): {settings.Bucket}", null, head.StatusCode);

        var body = settings.Region == "us-east-1"
            ? null
            : Encoding.UTF8.GetBytes(
                $"<CreateBucketConfiguration xmlns=\"{S3Xml.NamespaceName}\"><LocationConstraint>{settings.Region}</LocationConstraint></CreateBucketConfiguration>");
        using var create = await SendAsync(HttpMethod.Put, BucketUri(), body, body is null ? null : "application/xml", ct);
        await EnsureSuccessAsync(create, $"S3 버킷 생성 실패: {settings.Bucket}", ct);
    }

    public async Task PutAsync(string name, byte[] data, string mimeType, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Put, ObjectUri(name), data, mimeType, ct);
        await EnsureSuccessAsync(response, $"S3 업로드 실패: {name}", ct);
    }

    public async Task<MediaObject?> OpenAsync(string name, CancellationToken ct)
    {
        var response = await SendAsync(HttpMethod.Get, ObjectUri(name), null, null, ct, HttpCompletionOption.ResponseHeadersRead);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            response.Dispose();
            return null;
        }
        try
        {
            await EnsureSuccessAsync(response, $"S3 다운로드 실패: {name}", ct);
            var headers = response.Content.Headers;
            var mime = headers.ContentType?.MediaType is { Length: > 0 } type && type != "application/octet-stream"
                ? type
                : MimeTypes.FromPath(name);
            var lastModified = headers.LastModified?.UtcDateTime ?? DateTime.UtcNow;
            var stream = await response.Content.ReadAsStreamAsync(ct);
            return new MediaObject(stream, headers.ContentLength, mime, lastModified, response);
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    public async IAsyncEnumerable<(string Name, DateTime LastModifiedUtc)> ListAsync([EnumeratorCancellation] CancellationToken ct)
    {
        string? token = null;
        do
        {
            var query = new SortedDictionary<string, string>(StringComparer.Ordinal) { ["list-type"] = "2", ["prefix"] = settings.Prefix };
            if (token is not null)
                query["continuation-token"] = token;

            using var response = await SendAsync(HttpMethod.Get, BucketUri(query), null, null, ct);
            await EnsureSuccessAsync(response, "S3 목록 조회 실패", ct);
            var xml = XDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var root = xml.Root!;
            // 일부 호환 서버는 네임스페이스 없이 응답하므로 로컬 이름으로 찾습니다.
            foreach (var item in root.Elements().Where(e => e.Name.LocalName == "Contents"))
            {
                var key = Child(item, "Key");
                var modified = Child(item, "LastModified");
                if (key is null || modified is null || !key.StartsWith(settings.Prefix, StringComparison.Ordinal))
                    continue;
                var name = key[settings.Prefix.Length..];
                if (name.Length > 0 && !name.Contains('/'))
                    yield return (name, DateTime.Parse(modified, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal));
            }
            token = Child(root, "IsTruncated") == "true" ? Child(root, "NextContinuationToken") : null;
        }
        while (token is not null);
    }

    public async Task DeleteAsync(string name, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Delete, ObjectUri(name), null, null, ct);
        if (response.StatusCode != HttpStatusCode.NotFound)
            await EnsureSuccessAsync(response, $"S3 삭제 실패: {name}", ct);
    }

    public string? LocalPath(string name) => null;

    private static string? Child(XElement parent, string localName) =>
        parent.Elements().FirstOrDefault(e => e.Name.LocalName == localName)?.Value;

    private Uri ObjectUri(string name) => Build("/" + Escape(settings.Prefix + name), null);

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
        HttpMethod method, Uri uri, byte[]? body, string? contentType, CancellationToken ct,
        HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead)
    {
        using var request = new HttpRequestMessage(method, uri);
        if (body is not null)
        {
            request.Content = new ByteArrayContent(body);
            if (contentType is not null)
                request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        }
        Sign(request, uri, body is null ? EmptyPayloadHash : Hex(SHA256.HashData(body)));
        return await http.SendAsync(request, completion, ct);
    }

    /// <summary>AWS Signature Version 4 (헤더 방식). host, x-amz-content-sha256, x-amz-date 만 서명합니다.</summary>
    private void Sign(HttpRequestMessage request, Uri uri, string payloadHash)
    {
        var now = DateTime.UtcNow;
        var amzDate = now.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var date = now.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var host = uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}";
        const string signedHeaders = "host;x-amz-content-sha256;x-amz-date";

        var canonicalRequest = string.Join('\n',
            request.Method.Method,
            uri.AbsolutePath,
            uri.Query.TrimStart('?'),
            $"host:{host}",
            $"x-amz-content-sha256:{payloadHash}",
            $"x-amz-date:{amzDate}",
            "",
            signedHeaders,
            payloadHash);

        var scope = $"{date}/{settings.Region}/s3/aws4_request";
        var stringToSign = $"AWS4-HMAC-SHA256\n{amzDate}\n{scope}\n{Hex(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalRequest)))}";

        var key = Hmac(Encoding.UTF8.GetBytes("AWS4" + settings.SecretKey), date);
        key = Hmac(key, settings.Region);
        key = Hmac(key, "s3");
        key = Hmac(key, "aws4_request");
        var signature = Hex(Hmac(key, stringToSign));

        request.Headers.TryAddWithoutValidation("x-amz-date", amzDate);
        request.Headers.TryAddWithoutValidation("x-amz-content-sha256", payloadHash);
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
