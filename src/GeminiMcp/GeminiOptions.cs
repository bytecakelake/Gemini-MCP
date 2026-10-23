namespace GeminiMcp;

/// <summary>
/// 서버 설정. 환경 변수 / 명령줄 인수(--Key=Value) / appsettings.json 순으로 읽습니다.
/// AOT 호환을 위해 리플렉션 바인딩 대신 키를 직접 읽습니다.
/// </summary>
public sealed class GeminiOptions
{
    public required string? ApiKey { get; init; }
    public required string BaseUrl { get; init; }
    public required string TextModel { get; init; }
    public required string ProModel { get; init; }
    public required string ImageModel { get; init; }
    public required string TtsModel { get; init; }
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
            TextModel = Get("GEMINI_MODEL", "Gemini:TextModel") ?? "gemini-flash-latest",
            ProModel = Get("GEMINI_PRO_MODEL", "Gemini:ProModel") ?? "gemini-pro-latest",
            ImageModel = Get("GEMINI_IMAGE_MODEL", "Gemini:ImageModel") ?? "gemini-3.1-flash-image",
            TtsModel = Get("GEMINI_TTS_MODEL", "Gemini:TtsModel") ?? "gemini-2.5-flash-preview-tts",
            OutputDirectory = Get("GEMINI_MCP_OUTPUT_DIR", "Gemini:OutputDirectory") ?? defaultOutput,
            InlineLimitBytes = long.TryParse(Get("GEMINI_INLINE_LIMIT_BYTES"), out var limit) ? limit : 15 * 1024 * 1024,
            RequestTimeout = TimeSpan.FromSeconds(
                int.TryParse(Get("GEMINI_TIMEOUT_SECONDS"), out var seconds) ? seconds : 600),
            Paths = new PathMapper(Get("GEMINI_MCP_PATH_MAPS")),
        };
    }

    /// <summary>"pro" / "flash" 별칭을 실제 모델 이름으로 변환합니다.</summary>
    public string ResolveModel(string? requested) => requested?.Trim().ToLowerInvariant() switch
    {
        null or "" or "default" or "flash" => TextModel,
        "pro" => ProModel,
        _ => requested!.Trim().StartsWith("models/", StringComparison.Ordinal) ? requested.Trim()[7..] : requested.Trim(),
    };
}
