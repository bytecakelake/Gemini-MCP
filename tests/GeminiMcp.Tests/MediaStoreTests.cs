using System.Net;
using System.Text.Json;
using GeminiMcp.Tests.Infrastructure;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace GeminiMcp.Tests;

/// <summary>
/// 생성 미디어 S3 저장소: 리소스·다운로드, 대시보드용 내부 API(/internal/media 목록·통계·삭제), 메타데이터, 경로 탈출 차단.
/// 저장/조회 검증은 실제 Gemini 음성 합성으로 객체를 만들어 합니다.
/// </summary>
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

    /// <summary>미디어는 S3 에만 보관합니다. 보관 기간·로컬 저장 경로 인수가 없어야 합니다.</summary>
    [Fact]
    public async Task MediaTools_HaveNoOutputPathOrTtl()
    {
        await using var client = await server.ConnectAsync(Ct);
        var tools = await client.ListToolsAsync(cancellationToken: Ct);
        foreach (var name in new[] { "gemini_generate_image", "gemini_text_to_speech", "gemini_generate_video" })
        {
            var tool = Assert.Single(tools, t => t.Name == name);
            Assert.False(tool.JsonSchema.GetProperty("properties").TryGetProperty("output_path", out _), $"{name} still has output_path");
            Assert.Contains("S3", tool.Description);
            Assert.DoesNotContain("자동 삭제", tool.Description);
        }
    }

    [Theory]
    [InlineData("/mcp/media/9999999999999-0123456789abcdef.png")]
    [InlineData("/mcp/media/..%2F..%2Fappsettings.json")]
    [InlineData("/mcp/media/.gemini-mcp-models.json")]
    public async Task UnknownOrUnsafeMedia_Returns404(string path)
    {
        Assert.SkipUnless(server.HasMediaStore, GeminiServer.NoMediaStoreReason);
        using var response = await server.Http.SendAsync(Get(path), Ct);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task InternalApi_RejectsBrowserOrigin()
    {
        Assert.SkipWhen(server.IsExternal, "The internal API is not exposed through the gateway.");
        using var request = new HttpRequestMessage(HttpMethod.Delete, new Uri(server.BaseUri, "/internal/media/9999999999999-0123456789abcdef.png"));
        request.Headers.TryAddWithoutValidation("Origin", "http://localhost:3000");
        using var response = await server.Http.SendAsync(request, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task InternalApi_DeleteUnknown_Returns404()
    {
        Assert.SkipWhen(server.IsExternal, "The internal API is not exposed through the gateway.");
        Assert.SkipUnless(server.HasMediaStore, GeminiServer.NoMediaStoreReason);
        using var response = await server.Http.DeleteAsync(new Uri(server.BaseUri, "/internal/media/9999999999999-0123456789abcdef.png"), Ct);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// 음성 두 개를 만들어 목록이 최신순인지, 메타데이터(도구·모델·한글 프롬프트·API 키 컨슈머)가 남는지,
    /// 관리자 삭제 뒤에는 리소스·다운로드·다른 도구 입력이 모두 실패하는지 확인합니다.
    /// </summary>
    [Fact]
    [Trait("Category", "Live")]
    public async Task GeneratedMedia_IsListedWithMetadata_AndCanBeDeleted()
    {
        Assert.SkipWhen(server.IsExternal, "The internal API is not exposed through the gateway.");
        Assert.SkipUnless(server.HasMediaStore, GeminiServer.NoMediaStoreReason);
        Assert.SkipWhen(server.ApiKey is null, "GEMINI_API_KEY is not set.");

        // 게이트웨이(APISIX)가 넣는 헤더를 흉내 냅니다.
        await using var client = await server.ConnectAsync(Ct, extraHeaders: new() { ["X-Mcp-Consumer"] = "k_testconsumer" });
        var first = await SpeakAsync(client, "첫 번째 저장소 확인입니다.");
        var second = await SpeakAsync(client, "두 번째 저장소 확인입니다.");

        var page = await ListAsync();
        var names = page.Items.Select(i => i.Name).ToList();
        Assert.True(names.IndexOf(second) < names.IndexOf(first), "newest media must come first");

        var item = Assert.Single(page.Items, i => i.Name == second);
        Assert.Equal("gemini_text_to_speech", item.Tool);
        Assert.Equal("audio/wav", item.MimeType);
        Assert.False(string.IsNullOrEmpty(item.Model));
        Assert.Contains("두 번째 저장소 확인입니다.", item.Prompt);
        Assert.Equal("k_testconsumer", item.Consumer);
        Assert.True(item.Size > 1000);

        using (var stats = await server.Http.GetAsync(new Uri(server.BaseUri, "/internal/media/stats"), Ct))
        {
            var json = JsonDocument.Parse(await stats.Content.ReadAsStringAsync(Ct)).RootElement;
            Assert.True(json.GetProperty("count").GetInt64() >= 2);
            Assert.True(json.GetProperty("bytes").GetInt64() >= item.Size);
            Assert.Contains($"bucket={server.Bucket}", json.GetProperty("store").GetString());
        }

        // 관리자 삭제 (대시보드 → 내부 API)
        using (var deleted = await server.Http.DeleteAsync(new Uri(server.BaseUri, $"/internal/media/{second}"), Ct))
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        using (var again = await server.Http.DeleteAsync(new Uri(server.BaseUri, $"/internal/media/{second}"), Ct))
            Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);

        Assert.DoesNotContain((await ListAsync()).Items, i => i.Name == second);
        using (var download = await server.Http.GetAsync(new Uri(server.BaseUri, $"/mcp/media/{second}"), Ct))
            Assert.Equal(HttpStatusCode.NotFound, download.StatusCode);
        await Assert.ThrowsAnyAsync<McpException>(() => client.ReadResourceAsync($"gemini://media/{second}", cancellationToken: Ct).AsTask());
        var reuse = await client.CallToolAsync("gemini_analyze_media",
            new Dictionary<string, object?> { ["files"] = new[] { $"gemini://media/{second}" }, ["prompt"] = "Transcribe." }, cancellationToken: Ct);
        Assert.True(reuse.IsError);
        Assert.Contains("찾을 수 없습니다", reuse.Text());

        // 지우지 않은 객체는 그대로 남아 있습니다 (자동 삭제 없음).
        var read = await client.ReadResourceAsync($"gemini://media/{first}", cancellationToken: Ct);
        Assert.IsType<BlobResourceContents>(Assert.Single(read.Contents));
    }

    private static async Task<string> SpeakAsync(McpClient client, string text)
    {
        var result = await client.CallOkAsync("gemini_text_to_speech", new() { ["text"] = text });
        Assert.Contains("관리자가 대시보드에서 삭제할 수 있습니다", result.Text());
        var link = Assert.Single(result.Content.OfType<ResourceLinkBlock>());
        return link.Uri["gemini://media/".Length..];
    }

    private sealed record Item(string Name, long Size, string MimeType, string? Tool, string? Model, string? Prompt, string? Consumer);
    private sealed record Page(List<Item> Items, string? NextCursor);

    private async Task<Page> ListAsync()
    {
        using var response = await server.Http.GetAsync(new Uri(server.BaseUri, "/internal/media?limit=200"), Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonSerializer.Deserialize<Page>(await response.Content.ReadAsStringAsync(Ct), JsonSerializerOptions.Web)!;
    }

    private HttpRequestMessage Get(string path)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, new Uri(server.BaseUri, path));
        if (server.GatewayKey is not null)
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {server.GatewayKey}");
        return request;
    }
}

/// <summary>S3 가 없으면 미디어 도구는 Gemini 를 부르기 전에(비용 없이) 오류를 내고, 다운로드·내부 API 는 503 입니다.</summary>
public sealed class NoMediaStoreTests(NoMediaStoreServer server) : IClassFixture<NoMediaStoreServer>
{
    [Fact]
    public async Task MediaTools_FailFast_WithoutS3()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var client = await server.ConnectAsync(ct);
        foreach (var (tool, args) in new (string, Dictionary<string, object?>)[]
        {
            ("gemini_text_to_speech", new() { ["text"] = "hello" }),
            ("gemini_generate_image", new() { ["prompt"] = "a red apple" }),
            ("gemini_generate_video", new() { ["prompt"] = "a red ball" }),
        })
        {
            var result = await client.CallToolAsync(tool, args, cancellationToken: ct);
            Assert.True(result.IsError, tool);
            Assert.Contains("GEMINI_MCP_S3_BUCKET", result.Text());
        }
        // 생성 요청이 Gemini 로 나가지 않았어야 합니다.
        Assert.DoesNotContain("generateContent model=", server.Log);
        Assert.DoesNotContain("interactions.create", server.Log);

        using var download = await server.Http.GetAsync(new Uri(server.BaseUri, "/mcp/media/9999999999999-0123456789abcdef.png"), ct);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, download.StatusCode);
        using var list = await server.Http.GetAsync(new Uri(server.BaseUri, "/internal/media"), ct);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, list.StatusCode);
    }
}
