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

        return new GeminiOptions
        {
            ApiKey = Get("GEMINI_API_KEY", "GOOGLE_API_KEY", "Gemini:ApiKey"),
            BaseUrl = (Get("GEMINI_BASE_URL", "Gemini:BaseUrl") ?? "https://generativelanguage.googleapis.com").TrimEnd('/'),
            OutputDirectory = Get("GEMINI_MCP_OUTPUT_DIR", "Gemini:OutputDirectory") ?? defaultOutput,
            InlineLimitBytes = long.TryParse(Get("GEMINI_INLINE_LIMIT_BYTES"), out var limit) ? limit : 15 * 1024 * 1024,
            RequestTimeout = TimeSpan.FromSeconds(
                int.TryParse(Get("GEMINI_TIMEOUT_SECONDS"), out var seconds) ? seconds : 600),
            Paths = new PathMapper(Get("GEMINI_MCP_PATH_MAPS")),
        };
    }
}
