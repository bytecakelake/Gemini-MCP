using System.ComponentModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace GeminiMcp.Gemini;

/// <summary>
/// 생성된 미디어를 S3 호환 저장소에 메타데이터(도구, 모델, 프롬프트, API 키)와 함께 보관하고 ResourceLink 로 내보냅니다.
/// 자동 삭제는 하지 않습니다. 관리자가 대시보드(내부 API /internal/media)에서 보고 지웁니다.
/// 클라이언트는 MCP resources/read(gemini://media/{name}) 또는 HTTP GET /mcp/media/{name} 으로 필요할 때만 받아 갑니다.
/// </summary>
public sealed class MediaStore(GeminiOptions options, HttpClient http)
{
    public const string UriPrefix = "gemini://media/";
    public const string HttpPrefix = "/mcp/media/";

    private readonly S3MediaBackend? _s3 = options.S3 is { } s3 ? new S3MediaBackend(s3, http) : null;

    public bool IsEnabled => _s3 is not null;
    public string Description => _s3?.Description ?? "설정되지 않음 (GEMINI_MCP_S3_BUCKET)";

    private S3MediaBackend S3 => _s3 ?? throw new McpException(
        "미디어 저장소(S3)가 설정되지 않아 생성 결과를 보관할 수 없습니다. 서버에 GEMINI_MCP_S3_BUCKET 등 S3 설정이 필요합니다.");

    public Task InitializeAsync(CancellationToken ct) => _s3?.InitializeAsync(ct) ?? Task.CompletedTask;

    public async Task<MediaItem> SaveAsync(byte[] data, string mimeType, MediaMetadata metadata, CancellationToken ct)
    {
        var name = NewName(mimeType);
        await S3.PutAsync(name, data, mimeType, metadata, ct);
        return new MediaItem(name, mimeType, data.LongLength);
    }

    /// <summary>이름(또는 gemini://media/ URI, /mcp/media/ URL)으로 미디어를 엽니다. 없으면 null.</summary>
    public async Task<MediaObject?> OpenAsync(string nameOrUri, CancellationToken ct) =>
        ToName(nameOrUri) is { } name ? await S3.OpenAsync(name, ct) : null;

    /// <summary>최신순(이름이 역순 시각으로 시작) 한 페이지. 각 객체의 메타데이터는 HEAD 로 읽습니다.</summary>
    public Task<MediaPage> ListAsync(string? cursor, int limit, CancellationToken ct) =>
        S3.ListAsync(cursor, Math.Clamp(limit, 1, 200), ct);

    public Task<MediaStats> StatsAsync(CancellationToken ct) => S3.StatsAsync(ct);

    /// <returns>지웠으면 true, 없으면 false.</returns>
    public async Task<bool> DeleteAsync(string nameOrUri, CancellationToken ct) =>
        ToName(nameOrUri) is { } name && await S3.DeleteAsync(name, ct);

    public static bool IsMediaReference(string source) =>
        source.StartsWith(UriPrefix, StringComparison.OrdinalIgnoreCase)
        || (Uri.TryCreate(source, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            && uri.AbsolutePath.StartsWith(HttpPrefix, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// "{9999999999999 - 생성 시각(ms)}-{64bit 난수}{확장자}". S3 목록이 키 오름차순이라 최신 객체가 먼저 나옵니다.
    /// 난수 부분 때문에 이름을 추측할 수 없습니다.
    /// </summary>
    private static string NewName(string mimeType)
    {
        var inverted = 9_999_999_999_999L - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return $"{inverted.ToString("D13", CultureInfo.InvariantCulture)}-{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8))}{MimeTypes.ToExtension(mimeType)}";
    }

    /// <summary>gemini://media/ URI, /mcp/media/ URL 또는 이름 → 안전한 이름. 경로 탈출이 될 수 있는 값은 null.</summary>
    public static string? ToName(string nameOrUri)
    {
        var name = nameOrUri.Trim();
        if (name.StartsWith(UriPrefix, StringComparison.OrdinalIgnoreCase))
            name = name[UriPrefix.Length..];
        else if (Uri.TryCreate(name, UriKind.Absolute, out var uri) && uri.AbsolutePath.StartsWith(HttpPrefix, StringComparison.OrdinalIgnoreCase))
            name = uri.AbsolutePath[HttpPrefix.Length..];

        // 생성 시 만든 형식(영숫자, '-', 확장자)만 허용합니다.
        return name.Length is > 0 and <= 64 && char.IsAsciiLetterOrDigit(name[0])
            && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-')
            ? name
            : null;
    }
}

/// <summary>객체와 함께 저장하는 메타데이터 (S3 x-amz-meta-*).</summary>
/// <param name="Consumer">요청한 API 키의 APISIX 컨슈머 이름 (게이트웨이를 거치지 않으면 null).</param>
public sealed record MediaMetadata(string Tool, string? Model, string? Prompt, string? Consumer, string? InteractionId = null);

public sealed record MediaItem(string Name, string MimeType, long Size)
{
    public string Uri => MediaStore.UriPrefix + Name;

    public ResourceLinkBlock ToResourceLink(string? description = null) => new()
    {
        Uri = Uri,
        Name = Name,
        MimeType = MimeType,
        Size = Size,
        Description = description,
    };
}

/// <summary>대시보드에 보여 줄 객체 정보.</summary>
public sealed record MediaInfo(
    string Name, long Size, string MimeType, DateTimeOffset LastModified,
    string? Tool, string? Model, string? Prompt, string? Consumer, string? InteractionId);

public sealed record MediaPage(IReadOnlyList<MediaInfo> Items, string? NextCursor);

public sealed record MediaStats(long Count, long Bytes, string Store);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(MediaPage))]
[JsonSerializable(typeof(MediaStats))]
internal sealed partial class MediaJsonContext : JsonSerializerContext;

/// <summary>저장소에서 연 미디어. 다 읽은 뒤 Dispose 해야 합니다 (HTTP 응답을 닫습니다).</summary>
public sealed class MediaObject(Stream content, long? length, string mimeType, DateTime lastModifiedUtc, IDisposable? owner = null)
    : IAsyncDisposable
{
    public Stream Content { get; } = content;
    public long? Length { get; } = length;
    public string MimeType { get; } = mimeType;
    public DateTime LastModifiedUtc { get; } = lastModifiedUtc;

    public async ValueTask DisposeAsync()
    {
        await Content.DisposeAsync();
        owner?.Dispose();
    }
}

[McpServerResourceType]
public sealed class MediaResources
{
    [McpServerResource(UriTemplate = MediaStore.UriPrefix + "{name}", Name = "gemini-media", Title = "Gemini 생성 미디어")]
    [Description("gemini_generate_image / gemini_text_to_speech / gemini_generate_video 가 만든 미디어. 관리자가 삭제하면 더 이상 읽을 수 없습니다.")]
    public static async Task<ResourceContents> Read(MediaStore store, string name, CancellationToken cancellationToken)
    {
        await using var media = await store.OpenAsync(name, cancellationToken)
            ?? throw new McpException($"생성 미디어를 찾을 수 없습니다(관리자가 삭제했을 수 있음): {name}");
        using var buffer = new MemoryStream();
        await media.Content.CopyToAsync(buffer, cancellationToken);
        return BlobResourceContents.FromBytes(buffer.ToArray(), MediaStore.UriPrefix + name, media.MimeType);
    }
}

/// <summary>시작 시 S3 버킷을 준비합니다. 저장소가 아직 안 뜬 경우(예: S3 컨테이너 시작 전) 15초마다 다시 시도합니다.</summary>
public sealed class MediaStoreInitializer(MediaStore store, ILogger<MediaStoreInitializer> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!store.IsEnabled)
        {
            logger.LogWarning("미디어 저장소(S3)가 설정되지 않았습니다. 이미지·음성·영상 생성 도구는 오류를 반환합니다.");
            return;
        }
        while (true)
        {
            try
            {
                await store.InitializeAsync(stoppingToken);
                logger.LogInformation("Media store ready: {Store}", store.Description);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Media store not ready, retrying in 15s: {Store}", store.Description);
                await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
            }
        }
    }
}
