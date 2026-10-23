using System.Globalization;
using GeminiMcp.Gemini;

namespace GeminiMcp;

/// <summary>
/// 서버 설정. 환경 변수 / 명령줄 인수(--Key=Value) / appsettings.json 순으로 읽습니다.
/// AOT 호환을 위해 리플렉션 바인딩 대신 키를 직접 읽습니다.
/// </summary>
public sealed class GeminiOptions
{
    public required string? ApiKey { get; init; }
    public required string BaseUrl { get; init; }
    public required string OutputDirectory { get; init; }
    public required long InlineLimitBytes { get; init; }
    public required TimeSpan RequestTimeout { get; init; }
    public required PathMapper Paths { get; init; }
    /// <summary>생성 미디어 보관 기간. 기본 48시간(Gemini Files API 와 같음).</summary>
    public required TimeSpan MediaTtl { get; init; }
    /// <summary>로컬 디스크 저장소 폴더 (S3 를 쓰지 않을 때).</summary>
    public required string MediaDirectory { get; init; }
    /// <summary>S3 호환 저장소 설정. null 이면 로컬 디스크에 보관합니다.</summary>
    public required S3Settings? S3 { get; init; }
    /// <summary>미디어 다운로드 URL 의 기준 주소 (예: https://mcp.example.com). 없으면 요청의 Host 로 만듭니다.</summary>
    public required string? PublicUrl { get; init; }

    public static GeminiOptions From(IConfiguration config)
    {
        string? Get(params string[] keys)
        {
            foreach (var key in keys)
            {
                var value = config[key];
                if (!string.IsNullOrWhiteSpace(value))
                    return value.Trim();
            }
            return null;
        }

        var defaultOutput = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "gemini-mcp-output");

        var ttlHours = Get("GEMINI_MCP_MEDIA_TTL_HOURS");
        var mediaTtl = MediaStore.DefaultTtl;
        if (ttlHours is not null)
        {
            if (!double.TryParse(ttlHours, NumberStyles.Float, CultureInfo.InvariantCulture, out var hours) || hours <= 0)
                throw new FormatException($"GEMINI_MCP_MEDIA_TTL_HOURS 는 0 보다 큰 숫자(시간)여야 합니다: '{ttlHours}'");
            mediaTtl = TimeSpan.FromHours(hours);
        }

        return new GeminiOptions
        {
            ApiKey = Get("GEMINI_API_KEY", "GOOGLE_API_KEY", "Gemini:ApiKey"),
            BaseUrl = (Get("GEMINI_BASE_URL", "Gemini:BaseUrl") ?? "https://generativelanguage.googleapis.com").TrimEnd('/'),
            OutputDirectory = Get("GEMINI_MCP_OUTPUT_DIR", "Gemini:OutputDirectory") ?? defaultOutput,
            InlineLimitBytes = long.TryParse(Get("GEMINI_INLINE_LIMIT_BYTES"), out var limit) ? limit : 15 * 1024 * 1024,
            RequestTimeout = TimeSpan.FromSeconds(
                int.TryParse(Get("GEMINI_TIMEOUT_SECONDS"), out var seconds) ? seconds : 600),
            Paths = new PathMapper(Get("GEMINI_MCP_PATH_MAPS")),
            MediaTtl = mediaTtl,
            MediaDirectory = Get("GEMINI_MCP_MEDIA_DIR", "Gemini:MediaDirectory") ?? Path.Combine(Path.GetTempPath(), "gemini-mcp-media"),
            S3 = ReadS3(Get),
            PublicUrl = Get("GEMINI_MCP_PUBLIC_URL")?.TrimEnd('/'),
        };
    }

    /// <summary>GEMINI_MCP_S3_BUCKET 이 있으면 S3 저장소를 씁니다. 엔드포인트를 생략하면 AWS S3.</summary>
    private static S3Settings? ReadS3(Func<string[], string?> get)
    {
        string? Get(params string[] keys) => get(keys);

        var bucket = Get("GEMINI_MCP_S3_BUCKET");
        if (bucket is null)
            return null;

        var region = Get("GEMINI_MCP_S3_REGION", "AWS_REGION") ?? "us-east-1";
        var endpointText = Get("GEMINI_MCP_S3_ENDPOINT");
        if (!Uri.TryCreate(endpointText ?? $"https://s3.{region}.amazonaws.com", UriKind.Absolute, out var endpoint)
            || (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps))
            throw new FormatException($"GEMINI_MCP_S3_ENDPOINT 는 http(s) 주소여야 합니다: '{endpointText}'");

        var accessKey = Get("GEMINI_MCP_S3_ACCESS_KEY", "AWS_ACCESS_KEY_ID")
            ?? throw new FormatException("GEMINI_MCP_S3_BUCKET 을 쓰려면 GEMINI_MCP_S3_ACCESS_KEY 가 필요합니다.");
        var secretKey = Get("GEMINI_MCP_S3_SECRET_KEY", "AWS_SECRET_ACCESS_KEY")
            ?? throw new FormatException("GEMINI_MCP_S3_BUCKET 을 쓰려면 GEMINI_MCP_S3_SECRET_KEY 가 필요합니다.");

        var prefix = (Get("GEMINI_MCP_S3_PREFIX") ?? "gemini-mcp-media/").TrimStart('/');
        if (prefix.Length > 0 && !prefix.EndsWith('/'))
            prefix += "/";

        // 사용자 지정 엔드포인트(SeaweedFS, MinIO 등)는 보통 path-style 만 지원합니다.
        var pathStyle = Get("GEMINI_MCP_S3_PATH_STYLE") is { } style
            ? style.Equals("true", StringComparison.OrdinalIgnoreCase) || style == "1"
            : endpointText is not null;

        return new S3Settings(endpoint, bucket, region, accessKey, secretKey, prefix, pathStyle);
    }
}
