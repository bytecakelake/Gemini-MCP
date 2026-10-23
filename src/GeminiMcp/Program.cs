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
          GEMINI_MCP_OUTPUT_DIR   모델 선택 저장 폴더   (기본: ~/gemini-mcp-output)
          GEMINI_MCP_S3_BUCKET    생성 미디어를 보관할 S3 호환 저장소 (이미지·음성·영상 도구에 필수)
                                  GEMINI_MCP_S3_ENDPOINT, _REGION, _ACCESS_KEY, _SECRET_KEY, _PATH_STYLE
          GEMINI_MCP_PUBLIC_URL   미디어 다운로드 URL 기준 주소 (기본: 요청 Host)
          MCP_ALLOWED_ORIGINS     허용할 브라우저 Origin (쉼표 구분, 기본은 localhost 만)
          ASPNETCORE_URLS         수신 주소 (기본: http://127.0.0.1:5100)

        모델은 환경 변수가 아니라 MCP 도구로 고릅니다: gemini_list_models 로 목록을 보고 gemini_select_model 로 선택
        (선택은 출력 폴더의 .gemini-mcp-models.json 에 저장).

        인증은 하지 않습니다. 루프백/내부 네트워크에만 노출하고, 외부 접속은 API 게이트웨이(Apache APISIX, API 키 인증)를 거치게 하세요.

        엔드포인트:
          POST /mcp     MCP Streamable HTTP
          GET  /mcp/media/{name}  생성 미디어 다운로드
          GET|DELETE /internal/media  미디어 목록·통계·삭제 (대시보드 전용, 게이트웨이로 노출하지 않음)
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
builder.Services.AddSingleton<ModelCatalog>();
builder.Services.AddSingleton<MediaStore>();
builder.Services.AddHostedService<MediaStoreInitializer>();
builder.Services.AddHttpContextAccessor();

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
    .WithResources<SkillResources>()
    .WithResources<MediaResources>();

var app = builder.Build();

var allowedOrigins = (builder.Configuration["MCP_ALLOWED_ORIGINS"] ?? "")
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
    .ToHashSet(StringComparer.OrdinalIgnoreCase);

// 보안: DNS 리바인딩 방지를 위한 Origin 검사 (MCP 사양 권고사항). API 키 인증은 앞단 API 게이트웨이(Apache APISIX)가 담당합니다.
app.Use(async (context, next) =>
{
    // 내부 관리 API 는 브라우저에서 부를 일이 없으므로 Origin 이 있으면 모두 거부합니다.
    if (context.Request.Path.StartsWithSegments("/internal") && context.Request.Headers.Origin.Count > 0)
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        await context.Response.WriteAsync("Origin not allowed");
        return;
    }
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
    }
    await next(context);
});

app.MapMcp("/mcp");
// 생성 미디어 다운로드. /mcp/* 아래라서 게이트웨이(APISIX)에서 MCP 와 같은 API 키로 인증됩니다.
app.MapGet(MediaStore.HttpPrefix + "{name}", async (string name, MediaStore store, HttpContext context, CancellationToken ct) =>
{
    if (!store.IsEnabled)
        return MediaStoreDisabled();
    var media = await store.OpenAsync(name, ct);
    if (media is null)
        return Results.Text("Media not found", statusCode: StatusCodes.Status404NotFound);
    context.Response.RegisterForDisposeAsync(media);
    return Results.Stream(media.Content, media.MimeType, lastModified: media.LastModifiedUtc, enableRangeProcessing: media.Content.CanSeek);
});

// 미디어 관리용 내부 API (대시보드 → MCP 서버, backend 네트워크). APISIX 는 /mcp, /skill, /health 만 이 서버로 보내므로
// 외부에서는 닿지 않습니다. MCP 엔드포인트와 마찬가지로 인증하지 않으니 이 서버를 루프백/내부 네트워크에만 노출하세요.
var internalMedia = app.MapGroup("/internal/media");
internalMedia.MapGet("", async (MediaStore store, string? cursor, int? limit, CancellationToken ct) =>
    store.IsEnabled
        ? Results.Json(await store.ListAsync(cursor, limit ?? 50, ct), MediaJsonContext.Default.MediaPage)
        : MediaStoreDisabled());
internalMedia.MapGet("/stats", async (MediaStore store, CancellationToken ct) =>
    store.IsEnabled
        ? Results.Json(await store.StatsAsync(ct), MediaJsonContext.Default.MediaStats)
        : MediaStoreDisabled());
internalMedia.MapDelete("/{name}", async (string name, MediaStore store, ILogger<MediaStore> logger, CancellationToken ct) =>
{
    if (!store.IsEnabled)
        return MediaStoreDisabled();
    if (!await store.DeleteAsync(name, ct))
        return Results.Text("Media not found", statusCode: StatusCodes.Status404NotFound);
    logger.LogInformation("Media deleted: {Name}", name);
    return Results.NoContent();
});
app.MapGet("/", () => Results.Text(
    "Gemini MCP server is running.\nMCP endpoint: POST /mcp (Streamable HTTP)\nSkill: GET /skill\n", "text/plain; charset=utf-8"));
app.MapGet("/health", () => Results.Text(options.ApiKey is null ? "ok (GEMINI_API_KEY missing)" : "ok"));
app.MapGet("/skill", () => Results.Text(SkillContent.Markdown, "text/markdown; charset=utf-8"));

if (options.ApiKey is null)
    app.Logger.LogWarning("GEMINI_API_KEY 가 설정되지 않았습니다. 도구 호출 시 오류가 반환됩니다.");
app.Logger.LogInformation("모델 선택 저장 폴더: {Dir}", options.OutputDirectory);

app.Run();
return 0;

static IResult MediaStoreDisabled() =>
    Results.Text("Media store (S3) is not configured", statusCode: StatusCodes.Status503ServiceUnavailable);

[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(double?))]
[JsonSerializable(typeof(int?))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(string))]
internal sealed partial class AppJsonContext : JsonSerializerContext;
