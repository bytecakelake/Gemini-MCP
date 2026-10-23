using System.ComponentModel;
using System.Security.Cryptography;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace GeminiMcp.Gemini;

/// <summary>
/// 생성된 미디어를 저장소(로컬 디스크 또는 S3)에 보관 기간(TTL) 동안 두고 ResourceLink 로 내보냅니다. TTL 이 지나면 자동으로 지웁니다.
/// 클라이언트는 MCP resources/read(gemini://media/{name}) 또는 HTTP GET /mcp/media/{name} 으로 필요할 때만 받아 갑니다.
/// </summary>
public sealed class MediaStore(IMediaBackend backend, GeminiOptions options, ILogger<MediaStore> logger)
{
    public const string UriPrefix = "gemini://media/";
    public const string HttpPrefix = "/mcp/media/";

    /// <summary>기본 보관 기간. Gemini Files API 의 파일 보관 기간(48시간)과 같습니다.</summary>
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromHours(48);

    public TimeSpan Ttl => options.MediaTtl;
    public string Description => backend.Description;

    public Task InitializeAsync(CancellationToken ct) => backend.InitializeAsync(ct);

    public async Task<MediaItem> SaveAsync(byte[] data, string mimeType, CancellationToken ct)
    {
        // 추측할 수 없는 이름 (128bit 난수) + 실제 형식의 확장자
        var name = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)) + MimeTypes.ToExtension(mimeType);
        await backend.PutAsync(name, data, mimeType, ct);
        return new MediaItem(name, backend.LocalPath(name), mimeType, data.LongLength, DateTimeOffset.UtcNow.Add(Ttl));
    }

    /// <summary>이름(또는 gemini://media/ URI, /mcp/media/ URL)으로 아직 만료되지 않은 미디어를 엽니다. 없으면 null.</summary>
    public async Task<MediaObject?> OpenAsync(string nameOrUri, CancellationToken ct)
    {
        if (ToName(nameOrUri) is not { } name)
            return null;

        var media = await backend.OpenAsync(name, ct);
        if (media is null || !IsExpired(media.LastModifiedUtc))
            return media;
        await media.DisposeAsync();
        return null;
    }

    public async Task<byte[]?> ReadAllBytesAsync(string nameOrUri, CancellationToken ct)
    {
        await using var media = await OpenAsync(nameOrUri, ct);
        if (media is null)
            return null;
        using var buffer = new MemoryStream(media.Length is { } length ? (int)Math.Min(length, int.MaxValue) : 0);
        await media.Content.CopyToAsync(buffer, ct);
        return buffer.ToArray();
    }

    public static bool IsMediaReference(string source) =>
        source.StartsWith(UriPrefix, StringComparison.OrdinalIgnoreCase)
        || (Uri.TryCreate(source, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            && uri.AbsolutePath.StartsWith(HttpPrefix, StringComparison.OrdinalIgnoreCase));

    /// <summary>만료된 미디어를 지웁니다. 서버를 다시 시작해도 저장 시각 기준으로 정리됩니다.</summary>
    public async Task<int> CleanupAsync(CancellationToken ct)
    {
        var deleted = 0;
        await foreach (var (name, lastModifiedUtc) in backend.ListAsync(ct))
        {
            if (!IsExpired(lastModifiedUtc))
                continue;
            try
            {
                await backend.DeleteAsync(name, ct);
                deleted++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or HttpRequestException)
            {
                // 다운로드 중인 파일 등은 다음 주기에 다시 시도합니다.
                logger.LogDebug(ex, "만료된 미디어 삭제 실패: {Name}", name);
            }
        }
        if (deleted > 0)
            logger.LogInformation("만료된 미디어 {Count}개를 삭제했습니다.", deleted);
        return deleted;
    }

    /// <summary>gemini://media/ URI, /mcp/media/ URL 또는 이름 → 안전한 이름. 경로 탈출이 될 수 있는 값은 null.</summary>
    private static string? ToName(string nameOrUri)
    {
        var name = nameOrUri.Trim();
        if (name.StartsWith(UriPrefix, StringComparison.OrdinalIgnoreCase))
            name = name[UriPrefix.Length..];
        else if (Uri.TryCreate(name, UriKind.Absolute, out var uri) && uri.AbsolutePath.StartsWith(HttpPrefix, StringComparison.OrdinalIgnoreCase))
            name = uri.AbsolutePath[HttpPrefix.Length..];

        // 생성 시 만든 형식(영숫자 + 확장자)만 허용합니다.
        return name.Length is > 0 and <= 64 && !name.StartsWith('.') && name.All(c => char.IsAsciiLetterOrDigit(c) || c == '.')
            ? name
            : null;
    }

    private bool IsExpired(DateTime lastModifiedUtc) => DateTime.UtcNow - lastModifiedUtc >= Ttl;
}

/// <param name="LocalPath">로컬 디스크 저장소일 때의 파일 경로 (S3 이면 null).</param>
public sealed record MediaItem(string Name, string? LocalPath, string MimeType, long Size, DateTimeOffset ExpiresAt)
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

/// <summary>저장소에서 연 미디어. 다 읽은 뒤 Dispose 해야 합니다 (S3 는 HTTP 응답을 닫습니다).</summary>
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

/// <summary>미디어 저장소 구현 (로컬 디스크 / S3 호환 객체 저장소).</summary>
public interface IMediaBackend
{
    /// <summary>로그에 표시할 저장소 설명.</summary>
    string Description { get; }
    /// <summary>시작 시 한 번 호출됩니다 (폴더/버킷 준비).</summary>
    Task InitializeAsync(CancellationToken ct);
    Task PutAsync(string name, byte[] data, string mimeType, CancellationToken ct);
    /// <summary>없으면 null.</summary>
    Task<MediaObject?> OpenAsync(string name, CancellationToken ct);
    IAsyncEnumerable<(string Name, DateTime LastModifiedUtc)> ListAsync(CancellationToken ct);
    Task DeleteAsync(string name, CancellationToken ct);
    /// <summary>클라이언트가 직접 열 수 있는 로컬 파일 경로 (로컬 디스크 저장소만).</summary>
    string? LocalPath(string name);
}

/// <summary>로컬 디스크 저장소. 만료 판단은 파일 수정 시각 기준입니다.</summary>
public sealed class LocalMediaBackend(GeminiOptions options) : IMediaBackend
{
    private string Directory => options.MediaDirectory;

    public string Description => $"로컬 디스크 {Directory}";

    public Task InitializeAsync(CancellationToken ct)
    {
        System.IO.Directory.CreateDirectory(Directory);
        return Task.CompletedTask;
    }

    public async Task PutAsync(string name, byte[] data, string mimeType, CancellationToken ct)
    {
        System.IO.Directory.CreateDirectory(Directory);
        await File.WriteAllBytesAsync(Path.Combine(Directory, name), data, ct);
    }

    public Task<MediaObject?> OpenAsync(string name, CancellationToken ct)
    {
        var info = new FileInfo(Path.Combine(Directory, name));
        if (!info.Exists)
            return Task.FromResult<MediaObject?>(null);
        var stream = new FileStream(info.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, useAsync: true);
        return Task.FromResult<MediaObject?>(new MediaObject(stream, info.Length, MimeTypes.FromPath(info.Name), info.LastWriteTimeUtc));
    }

    public async IAsyncEnumerable<(string Name, DateTime LastModifiedUtc)> ListAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        if (!System.IO.Directory.Exists(Directory))
            yield break;
        foreach (var file in new DirectoryInfo(Directory).EnumerateFiles())
        {
            ct.ThrowIfCancellationRequested();
            yield return (file.Name, file.LastWriteTimeUtc);
        }
        await Task.CompletedTask;
    }

    public Task DeleteAsync(string name, CancellationToken ct)
    {
        File.Delete(Path.Combine(Directory, name));
        return Task.CompletedTask;
    }

    public string? LocalPath(string name) => Path.GetFullPath(Path.Combine(Directory, name));
}

[McpServerResourceType]
public sealed class MediaResources
{
    [McpServerResource(UriTemplate = MediaStore.UriPrefix + "{name}", Name = "gemini-media", Title = "Gemini 생성 미디어")]
    [Description("gemini_generate_image / gemini_text_to_speech / gemini_generate_video 가 만든 미디어. 보관 기간이 지나면 삭제됩니다.")]
    public static async Task<ResourceContents> Read(MediaStore store, string name, CancellationToken cancellationToken)
    {
        await using var media = await store.OpenAsync(name, cancellationToken)
            ?? throw new McpException($"생성 미디어를 찾을 수 없습니다(보관 기간이 지나 삭제되었을 수 있음): {name}");
        using var buffer = new MemoryStream();
        await media.Content.CopyToAsync(buffer, cancellationToken);
        return BlobResourceContents.FromBytes(buffer.ToArray(), MediaStore.UriPrefix + name, media.MimeType);
    }
}

/// <summary>시작 시 저장소를 준비하고, 주기적으로 만료된 미디어를 정리합니다.</summary>
public sealed class MediaCleanupService(MediaStore store, ILogger<MediaCleanupService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 보관 기간의 1/10 마다 (1분~30분)
        var interval = TimeSpan.FromTicks(Math.Clamp(store.Ttl.Ticks / 10, TimeSpan.FromMinutes(1).Ticks, TimeSpan.FromMinutes(30).Ticks));
        // 저장소가 아직 준비되지 않았으면(예: S3 컨테이너 시작 전) 서버는 계속 동작하고 15초마다 다시 시도합니다.
        while (true)
        {
            try
            {
                await store.InitializeAsync(stoppingToken);
                logger.LogInformation("미디어 저장소: {Store} (보관 {Hours}시간)", store.Description, store.Ttl.TotalHours);
                break;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "미디어 저장소 준비 실패, 15초 후 다시 시도합니다: {Store}", store.Description);
                await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
            }
        }

        using var timer = new PeriodicTimer(interval);
        do
        {
            try
            {
                await store.CleanupAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "만료된 미디어 정리 실패: {Store}", store.Description);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
