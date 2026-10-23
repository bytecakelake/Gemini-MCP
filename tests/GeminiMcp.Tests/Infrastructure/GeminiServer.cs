using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace GeminiMcp.Tests.Infrastructure;

/// <summary>
/// Native AOT 로 게시된 GeminiMcp.exe 를 빈 포트에서 띄우고, 테스트가 끝나면 종료합니다.
/// 실행 파일 경로는 GEMINI_MCP_EXE 환경 변수로 바꿀 수 있습니다 (기본: publish\win-x64\GeminiMcp.exe).
///
/// 외부 서버 모드: GEMINI_MCP_URL 을 지정하면 프로세스를 띄우지 않고 이미 실행 중인 서버(예: Docker 컨테이너)를 검증합니다.
///   GEMINI_MCP_URL              서버 주소 (예: http://127.0.0.1:5100)
///   GEMINI_MCP_API_KEY          대시보드에서 발급한 API 키 (APISIX 게이트웨이를 거칠 때 필수)
/// </summary>
public abstract class GeminiServer : IAsyncLifetime
{
    private readonly StringBuilder _log = new();
    private readonly string? _externalUrl;
    private Process? _process;

    protected GeminiServer()
    {
        _externalUrl = AllowExternal ? Environment.GetEnvironmentVariable("GEMINI_MCP_URL") : null;
        if (IsExternal)
            GatewayKey = NullIfEmpty(Environment.GetEnvironmentVariable("GEMINI_MCP_API_KEY"));
    }

    public bool IsExternal => _externalUrl is not null;
    /// <summary>API 게이트웨이(APISIX)를 거치는 외부 서버 모드에서 쓰는 API 키. 로컬 프로세스는 인증하지 않습니다.</summary>
    public string? GatewayKey { get; }
    public Uri BaseUri { get; private set; } = null!;
    public Uri McpEndpoint => new(BaseUri, "/mcp");
    /// <summary>로컬 프로세스가 모델 선택을 저장하는 폴더. 서버마다 새로 만들어 개발자의 선택과 섞이지 않게 합니다.</summary>
    public string OutputDirectory { get; } = Path.Combine(Path.GetTempPath(), "gemini-mcp-tests", Guid.NewGuid().ToString("N"));
    public string? ApiKey { get; } = ResolveApiKey();

    /// <summary>
    /// 생성 미디어용 S3. GEMINI_MCP_TEST_S3_ENDPOINT / _ACCESS_KEY / _SECRET_KEY 가 있으면 로컬 서버마다 새 버킷을 씁니다
    /// (예: docker run -p 127.0.0.1:18333:8333 -e AWS_ACCESS_KEY_ID=.. -e AWS_SECRET_ACCESS_KEY=.. chrislusf/seaweedfs:4.48).
    /// 외부 서버 모드에서는 배포의 S3 설정을 그대로 씁니다.
    /// </summary>
    public static readonly string? S3Endpoint = NullIfEmpty(Environment.GetEnvironmentVariable("GEMINI_MCP_TEST_S3_ENDPOINT"));
    public string Bucket { get; } = $"gmcp-test-{Guid.NewGuid():N}"[..30];
    public virtual bool HasMediaStore => IsExternal || S3Endpoint is not null;
    public const string NoMediaStoreReason = "Set GEMINI_MCP_TEST_S3_ENDPOINT (and _ACCESS_KEY/_SECRET_KEY) to test media tools.";
    public HttpClient Http { get; } = new() { Timeout = TimeSpan.FromMinutes(10) };
    public string Log { get { lock (_log) return _log.ToString(); } }

    /// <summary>false 면 GEMINI_MCP_URL 이 있어도 항상 로컬 프로세스를 띄웁니다 (서버 설정을 바꿔야 하는 테스트용).</summary>
    protected virtual bool AllowExternal => true;

    /// <summary>서버에 추가로 전달할 환경 변수.</summary>
    protected virtual IReadOnlyDictionary<string, string> ExtraEnvironment => new Dictionary<string, string>();

    public async ValueTask InitializeAsync()
    {
        if (_externalUrl is not null)
        {
            BaseUri = new Uri(_externalUrl.TrimEnd('/') + "/");
            await WaitHealthyAsync();
            return;
        }

        var exe = ResolveExecutable();
        Directory.CreateDirectory(OutputDirectory);
        BaseUri = new Uri($"http://127.0.0.1:{GetFreePort()}");

        var psi = new ProcessStartInfo(exe)
        {
            ArgumentList = { "--urls", BaseUri.ToString().TrimEnd('/') },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.Environment["GEMINI_MCP_OUTPUT_DIR"] = OutputDirectory;
        // 개발자 셸의 S3 설정이 섞이지 않게 비우고, 테스트 S3 가 있으면 새 버킷으로 지정합니다.
        foreach (var key in psi.Environment.Keys.Where(k => k.StartsWith("GEMINI_MCP_S3_", StringComparison.OrdinalIgnoreCase)).ToList())
            psi.Environment.Remove(key);
        if (HasMediaStore && S3Endpoint is not null)
        {
            psi.Environment["GEMINI_MCP_S3_ENDPOINT"] = S3Endpoint;
            psi.Environment["GEMINI_MCP_S3_BUCKET"] = Bucket;
            psi.Environment["GEMINI_MCP_S3_ACCESS_KEY"] = Environment.GetEnvironmentVariable("GEMINI_MCP_TEST_S3_ACCESS_KEY") ?? "";
            psi.Environment["GEMINI_MCP_S3_SECRET_KEY"] = Environment.GetEnvironmentVariable("GEMINI_MCP_TEST_S3_SECRET_KEY") ?? "";
        }
        if (ApiKey is not null)
            psi.Environment["GEMINI_API_KEY"] = ApiKey;
        foreach (var (key, value) in ExtraEnvironment)
            psi.Environment[key] = value;

        _process = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start {exe}");
        _process.OutputDataReceived += (_, e) => { lock (_log) _log.AppendLine(e.Data); };
        _process.ErrorDataReceived += (_, e) => { lock (_log) _log.AppendLine(e.Data); };
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();

        await WaitHealthyAsync();
    }

    private async Task WaitHealthyAsync()
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (true)
        {
            if (_process is { HasExited: true })
                throw new InvalidOperationException($"Server exited with code {_process.ExitCode}.\n{Log}");
            try
            {
                using var response = await Http.GetAsync(new Uri(BaseUri, "/health"));
                if (response.IsSuccessStatusCode)
                    return;
            }
            catch (HttpRequestException) when (DateTime.UtcNow < deadline)
            {
            }
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException($"Server {BaseUri} did not become healthy.\n{Log}");
            await Task.Delay(100);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_process is { HasExited: false })
        {
            _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync();
        }
        _process?.Dispose();
        Http.Dispose();
        if (!IsExternal)
        {
            try { Directory.Delete(OutputDirectory, recursive: true); } catch (IOException) { }
        }
        GC.SuppressFinalize(this);
    }

    /// <summary>인증 헤더를 포함한 MCP 클라이언트를 만듭니다 (Streamable HTTP). protocolVersion 생략 시 SDK 최신 버전.</summary>
    /// <param name="extraHeaders">모든 요청에 붙일 헤더 (예: 게이트웨이가 넣는 X-Mcp-Consumer 흉내).</param>
    public Task<McpClient> ConnectAsync(CancellationToken ct, string? protocolVersion = null, Dictionary<string, string>? extraHeaders = null)
    {
        var headers = new Dictionary<string, string>(extraHeaders ?? []);
        if (GatewayKey is not null)
            headers["Authorization"] = $"Bearer {GatewayKey}";
        return McpClient.CreateAsync(
            new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = McpEndpoint,
                TransportMode = HttpTransportMode.StreamableHttp,
                AdditionalHeaders = headers.Count == 0 ? null : headers,
            }),
            new McpClientOptions
            {
                ClientInfo = new Implementation { Name = "gemini-mcp-tests", Version = "1.0.0" },
                ProtocolVersion = protocolVersion,
            },
            cancellationToken: ct);
    }

    private static string ResolveExecutable()
    {
        var configured = Environment.GetEnvironmentVariable("GEMINI_MCP_EXE");
        if (!string.IsNullOrEmpty(configured))
            return File.Exists(configured) ? configured : throw new FileNotFoundException($"GEMINI_MCP_EXE not found: {configured}");

        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (!File.Exists(Path.Combine(dir.FullName, "GeminiMcp.slnx")))
                continue;
            var exe = Path.Combine(dir.FullName, "publish", "win-x64", "GeminiMcp.exe");
            return File.Exists(exe)
                ? exe
                : throw new FileNotFoundException($"AOT binary not found: {exe}. Run scripts\\publish-aot.ps1 first.");
        }
        throw new DirectoryNotFoundException("Repository root (GeminiMcp.slnx) not found.");
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;

    private static string? ResolveApiKey()
    {
        var key = Environment.GetEnvironmentVariable("GEMINI_API_KEY");
        if (string.IsNullOrEmpty(key) && OperatingSystem.IsWindows())
            key = Environment.GetEnvironmentVariable("GEMINI_API_KEY", EnvironmentVariableTarget.User);
        return string.IsNullOrEmpty(key) ? null : key;
    }

    internal static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}

/// <summary>기본 설정의 서버. 모든 테스트 클래스가 한 프로세스를 공유합니다.</summary>
public sealed class DefaultServer : GeminiServer;

/// <summary>인라인 한도를 1KB 로 낮춰 Files API 업로드 경로를 강제하는 서버.</summary>
public sealed class UploadServer : GeminiServer
{
    protected override bool AllowExternal => false;

    protected override IReadOnlyDictionary<string, string> ExtraEnvironment =>
        new Dictionary<string, string> { ["GEMINI_INLINE_LIMIT_BYTES"] = "1024" };
}

/// <summary>S3 를 설정하지 않은 서버 (미디어 도구가 생성 전에 오류를 내는지 검증).</summary>
public sealed class NoMediaStoreServer : GeminiServer
{
    protected override bool AllowExternal => false;
    public override bool HasMediaStore => false;
}

[CollectionDefinition(Name)]
public sealed class ServerCollection : ICollectionFixture<DefaultServer>
{
    public const string Name = "GeminiMcp server";
}
