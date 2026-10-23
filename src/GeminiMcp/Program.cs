using System.Text.Json;
using System.Text.Json.Serialization;
using GeminiMcp;
using GeminiMcp.Gemini;
using GeminiMcp.Skill;
using GeminiMcp.Tools;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

if (args.Contains("--help") || args.Contains("-h"))
{
    Console.WriteLine("""
        Gemini MCP Server (Streamable HTTP)

        사용법:
          GeminiMcp [--urls http://127.0.0.1:5100] [--install-skill [dir]] [--healthcheck]

        환경 변수:
          GEMINI_API_KEY          Gemini API 키 (필수, GOOGLE_API_KEY 도 인식)
          GEMINI_MODEL            기본 텍스트 모델        (기본: gemini-flash-latest)
          GEMINI_PRO_MODEL        model:"pro" 별칭 모델    (기본: gemini-pro-latest)
          GEMINI_IMAGE_MODEL      이미지 생성 모델         (기본: gemini-3.1-flash-image)
          GEMINI_TTS_MODEL        음성 합성 모델           (기본: gemini-2.5-flash-preview-tts)
          GEMINI_MCP_OUTPUT_DIR   생성 파일 저장 폴더      (기본: ~/gemini-mcp-output)
          MCP_AUTH_TOKEN          설정 시 Authorization: Bearer <token> 요구
          MCP_ALLOWED_ORIGINS     허용할 브라우저 Origin (쉼표 구분, 기본은 localhost 만)
          GEMINI_MCP_PATH_MAPS    호스트=컨테이너 경로 매핑 (';' 구분, Docker 실행 시)
          ASPNETCORE_URLS         수신 주소 (기본: http://127.0.0.1:5100)

        엔드포인트:
          POST /mcp     MCP Streamable HTTP
          GET  /skill   내장 스킬(SKILL.md)
          GET  /health  상태 확인
        """);
    return 0;
}

// 컨테이너 HEALTHCHECK 용 (chiseled 이미지에는 curl 이 없음)
if (args.Contains("--healthcheck"))
{
    var url = Environment.GetEnvironmentVariable("HEALTHCHECK_URL") ?? "http://127.0.0.1:5100/health";
    try
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        using var response = await client.GetAsync(url);
        return response.IsSuccessStatusCode ? 0 : 1;
    }
    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
    {
        return 1;
    }
}

var installIndex = Array.IndexOf(args, "--install-skill");
if (installIndex >= 0)
{
    var target = installIndex + 1 < args.Length && !args[installIndex + 1].StartsWith("--") ? args[installIndex + 1] : null;
    Console.WriteLine($"스킬을 설치했습니다: {SkillContent.InstallForClaudeCode(target)}");
    return 0;
}

var builder = WebApplication.CreateSlimBuilder(args);
if (string.IsNullOrEmpty(builder.Configuration["urls"]) && string.IsNullOrEmpty(builder.Configuration["ASPNETCORE_URLS"]))
    builder.WebHost.UseUrls("http://127.0.0.1:5100");

var options = GeminiOptions.From(builder.Configuration);
builder.Services.AddSingleton(options);
builder.Services.AddSingleton(_ => new HttpClient { Timeout = options.RequestTimeout });
builder.Services.AddSingleton<GeminiClient>();
builder.Services.AddSingleton<MediaResolver>();

// MCP 기본 직렬화 옵션 + 도구 인수 타입(string[] 등)용 소스 생성 컨텍스트 (AOT)
var toolJson = new JsonSerializerOptions(McpJsonUtilities.DefaultOptions);
toolJson.TypeInfoResolverChain.Add(AppJsonContext.Default);

builder.Services
    .AddMcpServer(mcp =>
    {
        mcp.ServerInfo = new Implementation { Name = "gemini-mcp", Title = "Gemini MCP", Version = "1.0.0" };
        mcp.ServerInstructions = SkillContent.ServerInstructions;
    })
    .WithHttpTransport(http => http.Stateless = true)
    .WithTools<GeminiTools>(toolJson)
    .WithPrompts<SkillPrompts>()
    .WithResources<SkillResources>();

var app = builder.Build();

var allowedOrigins = (builder.Configuration["MCP_ALLOWED_ORIGINS"] ?? "")
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
    .ToHashSet(StringComparer.OrdinalIgnoreCase);

// 보안: DNS 리바인딩 방지를 위한 Origin 검사 + 선택적 Bearer 토큰 인증 (MCP 사양 권고사항)
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/mcp"))
    {
        var origin = context.Request.Headers.Origin.ToString();
        if (origin.Length > 0 && !allowedOrigins.Contains(origin)
            && !(Uri.TryCreate(origin, UriKind.Absolute, out var o) && (o.IsLoopback || o.Host == "localhost")))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsync("Origin not allowed");
            return;
        }

        if (options.AuthToken is not null
            && context.Request.Headers.Authorization.ToString() != $"Bearer {options.AuthToken}")
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.Headers.WWWAuthenticate = "Bearer";
            return;
        }
    }
    await next(context);
});

app.MapMcp("/mcp");
app.MapGet("/", () => Results.Text(
    "Gemini MCP server is running.\nMCP endpoint: POST /mcp (Streamable HTTP)\nSkill: GET /skill\n", "text/plain; charset=utf-8"));
app.MapGet("/health", () => Results.Text(options.ApiKey is null ? "ok (GEMINI_API_KEY missing)" : "ok"));
app.MapGet("/skill", () => Results.Text(SkillContent.Markdown, "text/markdown; charset=utf-8"));

if (options.ApiKey is null)
    app.Logger.LogWarning("GEMINI_API_KEY 가 설정되지 않았습니다. 도구 호출 시 오류가 반환됩니다.");
app.Logger.LogInformation("출력 폴더: {Dir}", options.OutputDirectory);
if (options.Paths.IsEnabled)
    app.Logger.LogInformation("경로 매핑: {Maps}", builder.Configuration["GEMINI_MCP_PATH_MAPS"]);

app.Run();
return 0;

[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(double?))]
[JsonSerializable(typeof(int?))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(string))]
internal sealed partial class AppJsonContext : JsonSerializerContext;
