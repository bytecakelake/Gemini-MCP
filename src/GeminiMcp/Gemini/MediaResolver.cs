using System.Text.Json.Nodes;
using ModelContextProtocol;

namespace GeminiMcp.Gemini;

/// <summary>
/// 로컬 경로 / http(s) URL / YouTube URL / Gemini 파일 URI 를 Gemini "part" 로 변환합니다.
/// 작은 파일은 inlineData(base64), 큰 파일은 Files API 업로드 후 fileData 로 보냅니다.
/// </summary>
public sealed class MediaResolver(GeminiClient gemini, HttpClient http, GeminiOptions options)
{
    private const long MaxDownloadBytes = 2L * 1024 * 1024 * 1024;

    public async Task<JsonObject> ToPartAsync(string source, CancellationToken ct)
    {
        source = source.Trim().Trim('"');
        if (source.Length == 0)
            throw new McpException("빈 파일 경로/URL 이 전달되었습니다.");

        if (IsYouTube(source))
            return FileDataPart(source, mimeType: null);

        if (source.StartsWith($"{options.BaseUrl}/v1beta/files/", StringComparison.OrdinalIgnoreCase))
            return FileDataPart(source, mimeType: null);

        if (Uri.TryCreate(source, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            return await FromUrlAsync(uri, ct);

        return await FromLocalFileAsync(source, ct);
    }

    /// <summary>
    /// Interactions API 입력 형식({type, data|uri, mime_type})으로 변환합니다. YouTube URL 은 지원하지 않습니다.
    /// </summary>
    public async Task<JsonObject> ToInteractionContentAsync(string source, CancellationToken ct)
    {
        if (IsYouTube(source.Trim().Trim('"')))
            throw new McpException("영상 생성 모델 입력으로 YouTube URL 은 지원하지 않습니다. 파일 경로나 직접 다운로드 URL 을 사용하세요.");

        var part = await ToPartAsync(source, ct);
        var (mimeType, content) = part["inlineData"] is JsonObject inline
            ? (inline["mimeType"]!.GetValue<string>(), new JsonObject { ["data"] = inline["data"]!.GetValue<string>() })
            : (part["fileData"]!["mimeType"]?.GetValue<string>() ?? MimeTypes.FromPath(source),
                new JsonObject { ["uri"] = part["fileData"]!["fileUri"]!.GetValue<string>() });

        var type = mimeType.Split('/')[0] switch
        {
            "image" => "image",
            "video" => "video",
            "audio" => "audio",
            _ => throw new McpException($"영상 생성 입력으로 쓸 수 없는 형식입니다 ({mimeType}): {source}"),
        };
        content["type"] = type;
        content["mime_type"] = mimeType;
        return content;
    }

    private async Task<JsonObject> FromLocalFileAsync(string path, CancellationToken ct)
    {
        var fullPath = Path.GetFullPath(options.Paths.ToLocal(Environment.ExpandEnvironmentVariables(path)));
        var info = new FileInfo(fullPath);
        if (!info.Exists)
        {
            var hint = options.Paths.IsEnabled
                ? "컨테이너에서 실행 중이므로 GEMINI_MCP_PATH_MAPS 로 공유된 폴더 아래의 파일만 읽을 수 있습니다"
                : "경로는 MCP 서버가 실행 중인 컴퓨터 기준입니다";
            throw new McpException($"파일을 찾을 수 없습니다: {path} ({hint})");
        }

        var mimeType = MimeTypes.FromPath(fullPath);
        if (info.Length <= options.InlineLimitBytes)
            return InlinePart(await File.ReadAllBytesAsync(fullPath, ct), mimeType);

        await using var stream = info.OpenRead();
        var file = await gemini.UploadFileAsync(stream, info.Length, mimeType, info.Name, ct);
        return FileDataPart(file["uri"]!.GetValue<string>(), file["mimeType"]?.GetValue<string>() ?? mimeType);
    }

    private async Task<JsonObject> FromUrlAsync(Uri uri, CancellationToken ct)
    {
        using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
            throw new McpException($"URL 다운로드 실패 ({(int)response.StatusCode}): {uri}");

        var length = response.Content.Headers.ContentLength;
        if (length > MaxDownloadBytes)
            throw new McpException($"파일이 너무 큽니다 ({length} bytes): {uri}");

        var mimeType = response.Content.Headers.ContentType?.MediaType;
        if (string.IsNullOrEmpty(mimeType) || mimeType == "application/octet-stream")
            mimeType = MimeTypes.FromPath(uri.AbsolutePath);

        var bytes = await response.Content.ReadAsByteArrayAsync(ct);
        if (bytes.LongLength <= options.InlineLimitBytes)
            return InlinePart(bytes, mimeType);

        using var stream = new MemoryStream(bytes, writable: false);
        var name = Path.GetFileName(uri.AbsolutePath);
        var file = await gemini.UploadFileAsync(stream, bytes.LongLength, mimeType, string.IsNullOrEmpty(name) ? "download" : name, ct);
        return FileDataPart(file["uri"]!.GetValue<string>(), file["mimeType"]?.GetValue<string>() ?? mimeType);
    }

    private static bool IsYouTube(string source) =>
        Uri.TryCreate(source, UriKind.Absolute, out var uri)
        && (uri.Host.EndsWith("youtube.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.Equals("youtu.be", StringComparison.OrdinalIgnoreCase));

    public static JsonObject InlinePart(byte[] bytes, string mimeType) => new()
    {
        ["inlineData"] = new JsonObject
        {
            ["mimeType"] = mimeType,
            ["data"] = Convert.ToBase64String(bytes),
        },
    };

    private static JsonObject FileDataPart(string uri, string? mimeType)
    {
        var fileData = new JsonObject { ["fileUri"] = uri };
        if (mimeType is not null)
            fileData["mimeType"] = mimeType;
        return new JsonObject { ["fileData"] = fileData };
    }
}

public static class MimeTypes
{
    private static readonly Dictionary<string, string> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        // 이미지
        [".png"] = "image/png", [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg", [".webp"] = "image/webp",
        [".heic"] = "image/heic", [".heif"] = "image/heif", [".gif"] = "image/gif", [".bmp"] = "image/bmp",
        // 영상
        [".mp4"] = "video/mp4", [".mpeg"] = "video/mpeg", [".mpg"] = "video/mpg", [".mov"] = "video/mov",
        [".avi"] = "video/avi", [".flv"] = "video/x-flv", [".webm"] = "video/webm", [".wmv"] = "video/wmv",
        [".3gp"] = "video/3gpp", [".mkv"] = "video/x-matroska",
        // 오디오
        [".mp3"] = "audio/mp3", [".wav"] = "audio/wav", [".aiff"] = "audio/aiff", [".aac"] = "audio/aac",
        [".ogg"] = "audio/ogg", [".flac"] = "audio/flac", [".m4a"] = "audio/mp4", [".opus"] = "audio/ogg",
        // 문서
        [".pdf"] = "application/pdf", [".html"] = "text/html", [".htm"] = "text/html", [".css"] = "text/css",
        [".csv"] = "text/csv", [".xml"] = "text/xml", [".md"] = "text/markdown", [".rtf"] = "text/rtf",
        [".json"] = "application/json",
    };

    private static readonly HashSet<string> TextLike = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".log", ".cs", ".js", ".ts", ".tsx", ".jsx", ".py", ".java", ".kt", ".go", ".rs", ".c", ".cpp", ".h",
        ".hpp", ".sql", ".yaml", ".yml", ".toml", ".ini", ".sh", ".ps1", ".xaml", ".csproj", ".swift", ".rb", ".php",
    };

    public static string FromPath(string path)
    {
        var ext = Path.GetExtension(path);
        if (Map.TryGetValue(ext, out var mime))
            return mime;
        return TextLike.Contains(ext) ? "text/plain" : "application/octet-stream";
    }

    public static string ToExtension(string mimeType) => mimeType.Split(';')[0].Trim().ToLowerInvariant() switch
    {
        "image/png" => ".png",
        "image/jpeg" => ".jpg",
        "image/webp" => ".webp",
        "image/gif" => ".gif",
        "audio/wav" or "audio/x-wav" => ".wav",
        "audio/mpeg" or "audio/mp3" => ".mp3",
        "video/mp4" => ".mp4",
        "video/webm" => ".webm",
        "video/quicktime" or "video/mov" => ".mov",
        _ => ".bin",
    };
}
