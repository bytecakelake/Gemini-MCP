using System.Net;
using GeminiMcp.Tests.Infrastructure;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace GeminiMcp.Tests;

/// <summary>생성 미디어 임시 보관소: resources/read, HTTP 다운로드, 경로 탈출 차단.</summary>
[Collection(ServerCollection.Name)]
public sealed class MediaStoreTests(DefaultServer server)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task MediaResourceTemplate_IsListed()
    {
        await using var client = await server.ConnectAsync(Ct);
        var templates = await client.ListResourceTemplatesAsync(cancellationToken: Ct);
        Assert.Contains(templates, t => t.UriTemplate == "gemini://media/{name}");
    }

    /// <summary>서버는 생성 미디어를 영구 저장하지 않습니다. output_path 인수가 없어야 합니다.</summary>
    [Fact]
    public async Task MediaTools_HaveNoOutputPath()
    {
        await using var client = await server.ConnectAsync(Ct);
        var tools = await client.ListToolsAsync(cancellationToken: Ct);
        foreach (var name in new[] { "gemini_generate_image", "gemini_text_to_speech", "gemini_generate_video" })
        {
            var tool = Assert.Single(tools, t => t.Name == name);
            Assert.False(tool.JsonSchema.GetProperty("properties").TryGetProperty("output_path", out _), $"{name} still has output_path");
            Assert.Contains("48시간", tool.Description);
        }
    }

    [Theory]
    [InlineData("/mcp/media/0123456789abcdef.png")]
    [InlineData("/mcp/media/..%2F..%2Fappsettings.json")]
    [InlineData("/mcp/media/.gemini-mcp-models.json")]
    public async Task UnknownOrUnsafeMedia_Returns404(string path)
    {
        using var response = await server.Http.SendAsync(Get(path), Ct);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task StoredMedia_IsServedOverHttpAndResources()
    {
        Assert.SkipWhen(server.MediaDirectory is null, "Media directory is unknown for an external server.");
        var (name, bytes) = await PutMediaAsync(server.MediaDirectory);

        using var response = await server.Http.SendAsync(Get($"/mcp/media/{name}"), Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(bytes, await response.Content.ReadAsByteArrayAsync(Ct));

        await using var client = await server.ConnectAsync(Ct);
        var read = await client.ReadResourceAsync($"gemini://media/{name}", cancellationToken: Ct);
        var blob = Assert.IsType<BlobResourceContents>(Assert.Single(read.Contents));
        Assert.Equal("image/png", blob.MimeType);
        Assert.Equal(bytes, blob.DecodedData.ToArray());
    }

    internal static async Task<(string Name, byte[] Bytes)> PutMediaAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var name = $"{Guid.NewGuid():N}.png";
        byte[] bytes = [0x89, (byte)'P', (byte)'N', (byte)'G', 1, 2, 3, 4];
        await File.WriteAllBytesAsync(Path.Combine(directory, name), bytes, Ct);
        return (name, bytes);
    }

    private HttpRequestMessage Get(string path)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, new Uri(server.BaseUri, path));
        if (server.GatewayKey is not null)
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {server.GatewayKey}");
        return request;
    }
}

/// <summary>GEMINI_MCP_MEDIA_TTL_HOURS 로 보관 기간을 바꿀 수 있어야 합니다.</summary>
public sealed class MediaTtlSettingTests(ShortTtlServer server) : IClassFixture<ShortTtlServer>
{
    [Fact]
    public async Task ConfiguredTtl_ExpiresMedia()
    {
        var ct = TestContext.Current.CancellationToken;
        var (name, _) = await MediaStoreTests.PutMediaAsync(server.MediaDirectory!);
        var url = new Uri(server.BaseUri, $"/mcp/media/{name}");

        using (var fresh = await server.Http.GetAsync(url, ct))
            Assert.Equal(HttpStatusCode.OK, fresh.StatusCode);
        await Task.Delay(TimeSpan.FromSeconds(4), ct);
        using (var expired = await server.Http.GetAsync(url, ct))
            Assert.Equal(HttpStatusCode.NotFound, expired.StatusCode);
    }
}

/// <summary>S3 호환 저장소: 버킷 자동 생성, 저장/조회, 보관 기간 만료. 생성 단계는 실제 Gemini API 를 씁니다.</summary>
public sealed class S3MediaStoreTests(S3Server server) : IClassFixture<S3Server>
{
    [Fact]
    public async Task Startup_CreatesBucket_AndUnknownMediaIs404()
    {
        Assert.SkipUnless(server.IsConfigured, "Set GEMINI_MCP_TEST_S3_ENDPOINT (and _ACCESS_KEY/_SECRET_KEY) to run.");
        var ct = TestContext.Current.CancellationToken;
        await WaitForStoreAsync(ct);
        Assert.Contains($"bucket={server.Bucket}", server.Log);

        using var response = await server.Http.GetAsync(new Uri(server.BaseUri, "/mcp/media/0123456789abcdef.png"), ct);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    [Trait("Category", "Live")]
    public async Task GeneratedSpeech_IsStoredInS3_AndExpires()
    {
        Assert.SkipUnless(server.IsConfigured, "Set GEMINI_MCP_TEST_S3_ENDPOINT (and _ACCESS_KEY/_SECRET_KEY) to run.");
        Assert.SkipWhen(server.ApiKey is null, "GEMINI_API_KEY is not set.");
        var ct = TestContext.Current.CancellationToken;
        await WaitForStoreAsync(ct);

        await using var client = await server.ConnectAsync(ct);
        var result = await client.CallOkAsync("gemini_text_to_speech", new() { ["text"] = "S3 storage check." });
        var link = Assert.Single(result.Content.OfType<ResourceLinkBlock>());
        // S3 저장소는 로컬 파일 경로를 알려 주지 않습니다.
        Assert.DoesNotContain("임시 파일", result.Text());
        Assert.False(Directory.Exists(server.MediaDirectory) && Directory.EnumerateFiles(server.MediaDirectory).Any());

        var read = await client.ReadResourceAsync(link.Uri, cancellationToken: ct);
        var wav = Assert.IsType<BlobResourceContents>(Assert.Single(read.Contents)).DecodedData.ToArray();
        Assert.Equal(link.Size, wav.Length);

        var name = link.Uri["gemini://media/".Length..];
        using (var http = await server.Http.GetAsync(new Uri(server.BaseUri, $"/mcp/media/{name}"), ct))
        {
            Assert.Equal(HttpStatusCode.OK, http.StatusCode);
            Assert.Equal("audio/wav", http.Content.Headers.ContentType?.MediaType);
            Assert.Equal(wav, await http.Content.ReadAsByteArrayAsync(ct));
        }

        // 생성 미디어 URI 를 다른 도구 입력으로 넘기면 S3 에서 읽어 Gemini 로 보냅니다.
        var transcript = (await client.CallOkAsync("gemini_analyze_media", new()
        {
            ["files"] = new[] { link.Uri },
            ["prompt"] = "Transcribe this audio verbatim.",
        })).Text();
        Assert.Contains("storage", transcript, StringComparison.OrdinalIgnoreCase);

        // 보관 기간(약 7초)이 지나면 더 이상 받을 수 없습니다.
        await Task.Delay(TimeSpan.FromSeconds(8), ct);
        using (var expired = await server.Http.GetAsync(new Uri(server.BaseUri, $"/mcp/media/{name}"), ct))
            Assert.Equal(HttpStatusCode.NotFound, expired.StatusCode);
    }

    private async Task WaitForStoreAsync(CancellationToken ct)
    {
        for (var i = 0; i < 100 && !server.Log.Contains("미디어 저장소: S3"); i++)
        {
            Assert.DoesNotContain("미디어 저장소 준비 실패", server.Log);
            await Task.Delay(100, ct);
        }
        Assert.Contains("미디어 저장소: S3", server.Log);
    }
}

/// <summary>보관 기간(기본 48시간, Gemini Files API 와 같음)이 지난 미디어는 더 이상 내려받을 수 없어야 합니다.</summary>
[Collection(ServerCollection.Name)]
public sealed class MediaExpiryTests(DefaultServer server)
{
    [Fact]
    public async Task ExpiredMedia_IsNoLongerServed()
    {
        Assert.SkipWhen(server.MediaDirectory is null, "Media directory is unknown for an external server.");
        var ct = TestContext.Current.CancellationToken;
        var (fresh, _) = await MediaStoreTests.PutMediaAsync(server.MediaDirectory);
        var (old, _) = await MediaStoreTests.PutMediaAsync(server.MediaDirectory);
        File.SetLastWriteTimeUtc(Path.Combine(server.MediaDirectory, fresh), DateTime.UtcNow.AddHours(-47));
        File.SetLastWriteTimeUtc(Path.Combine(server.MediaDirectory, old), DateTime.UtcNow.AddHours(-49));

        using (var response = await server.Http.GetAsync(new Uri(server.BaseUri, $"/mcp/media/{fresh}"), ct))
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using (var response = await server.Http.GetAsync(new Uri(server.BaseUri, $"/mcp/media/{old}"), ct))
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        await using var client = await server.ConnectAsync(ct);
        await Assert.ThrowsAnyAsync<McpException>(() => client.ReadResourceAsync($"gemini://media/{old}", cancellationToken: ct).AsTask());
    }
}
