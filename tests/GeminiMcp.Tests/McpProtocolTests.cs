using GeminiMcp.Tests.Infrastructure;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace GeminiMcp.Tests;

/// <summary>공식 MCP 클라이언트로 initialize / tools / prompts / resources 를 검증합니다 (Gemini API 호출 없음).</summary>
[Collection(ServerCollection.Name)]
public sealed class McpProtocolTests(DefaultServer server) : IAsyncLifetime
{
    private const string SkillUri = "gemini://skill/SKILL.md";

    private static readonly string[] ExpectedTools =
    [
        "gemini_ask", "gemini_web_search", "gemini_read_urls", "gemini_analyze_media", "gemini_run_code",
        "gemini_generate_image", "gemini_generate_video", "gemini_text_to_speech", "gemini_list_models",
        "gemini_select_model", "gemini_usage_guide",
    ];

    private McpClient _client = null!;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => _client = await server.ConnectAsync(Ct);

    public ValueTask DisposeAsync() => _client.DisposeAsync();

    [Fact]
    public void Initialize_ReportsServerInfoInstructionsAndCapabilities()
    {
        Assert.Equal("gemini-mcp", _client.ServerInfo?.Name);
        Assert.Equal("1.0.0", _client.ServerInfo?.Version);
        Assert.Contains("gemini_usage_guide", _client.ServerInstructions ?? "");
        Assert.NotNull(_client.ServerCapabilities?.Tools);
        Assert.NotNull(_client.ServerCapabilities?.Prompts);
        Assert.NotNull(_client.ServerCapabilities?.Resources);
    }

    /// <summary>구버전 프로토콜을 쓰는 클라이언트도 연결·도구 호출이 되어야 합니다. (ping 은 2026-07-28 에서 제거됨)</summary>
    [Theory]
    [InlineData("2025-03-26")]
    [InlineData("2025-06-18")]
    [InlineData("2025-11-25")]
    public async Task OlderProtocolVersions_NegotiateAndWork(string version)
    {
        await using var client = await server.ConnectAsync(Ct, version);

        Assert.Equal(version, client.NegotiatedProtocolVersion);
        await client.PingAsync(cancellationToken: Ct);
        var result = await client.CallOkAsync("gemini_usage_guide", []);
        Assert.Contains("name: gemini-mcp", result.Text());
    }

    [Fact]
    public async Task ListTools_ReturnsAllGeminiTools()
    {
        var tools = await _client.ListToolsAsync(cancellationToken: Ct);
        Assert.Equal(ExpectedTools.Order(), tools.Select(t => t.Name).Order());
    }

    [Fact]
    public async Task ToolSchemas_DescribeArgumentsCorrectly()
    {
        var tools = (await _client.ListToolsAsync(cancellationToken: Ct)).ToDictionary(t => t.Name);

        var media = tools["gemini_analyze_media"].JsonSchema;
        Assert.Equal("array", media.GetProperty("properties").GetProperty("files").GetProperty("type").GetString());
        var required = media.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.Equal(["files", "prompt"], required.Order());

        Assert.True(tools["gemini_web_search"].ProtocolTool.Annotations?.ReadOnlyHint);
        Assert.False(tools["gemini_generate_image"].ProtocolTool.Annotations?.DestructiveHint);

        var video = tools["gemini_generate_video"].JsonSchema;
        Assert.Equal(["prompt"], video.GetProperty("required").EnumerateArray().Select(e => e.GetString()));
        // 선택 인수(string[]?)는 type 이 ["array", "null"] 로 표현됩니다.
        Assert.Contains("array", video.GetProperty("properties").GetProperty("input_images").GetProperty("type").EnumerateArray().Select(e => e.GetString()));

        var select = tools["gemini_select_model"].JsonSchema;
        Assert.Equal(["category", "model"], select.GetProperty("required").EnumerateArray().Select(e => e.GetString()).Order());
    }

    [Fact]
    public async Task SelectModel_SetsAndResetsCategory()
    {
        try
        {
            var set = await _client.CallOkAsync("gemini_select_model", new()
            {
                ["category"] = "tts",
                ["model"] = "models/test-tts-model",
                ["skip_validation"] = true,
            });
            Assert.Contains("- tts: test-tts-model\n", set.Text());
        }
        finally
        {
            var reset = await _client.CallOkAsync("gemini_select_model", new() { ["category"] = "tts", ["model"] = "default" });
            Assert.Contains("- tts: gemini-2.5-flash-preview-tts (기본값)", reset.Text());
        }
    }

    [Fact]
    public async Task SelectModel_UnknownCategory_ReturnsToolError()
    {
        var result = await _client.CallToolAsync("gemini_select_model", new Dictionary<string, object?>
        {
            ["category"] = "music",
            ["model"] = "x",
            ["skip_validation"] = true,
        }, cancellationToken: Ct);

        Assert.True(result.IsError);
        Assert.Contains("알 수 없는 category", result.Text());
    }

    [Fact]
    public async Task SkillPrompt_IsListedAndIncludesTask()
    {
        var prompts = await _client.ListPromptsAsync(cancellationToken: Ct);
        Assert.Contains(prompts, p => p.Name == "gemini_skill");

        var prompt = await _client.GetPromptAsync("gemini_skill", new Dictionary<string, object?> { ["task"] = "summarize a video" }, cancellationToken: Ct);
        var text = Assert.IsType<TextContentBlock>(Assert.Single(prompt.Messages).Content).Text;
        Assert.Contains("name: gemini-mcp", text);
        Assert.Contains("summarize a video", text);
    }

    [Fact]
    public async Task SkillResource_IsListedAndReadable()
    {
        var resources = await _client.ListResourcesAsync(cancellationToken: Ct);
        Assert.Contains(resources, r => r.Uri == SkillUri);

        var read = await _client.ReadResourceAsync(SkillUri, cancellationToken: Ct);
        var content = Assert.IsType<TextResourceContents>(Assert.Single(read.Contents));
        Assert.Equal("text/markdown", content.MimeType);
        Assert.Contains("gemini_web_search", content.Text);
    }

    [Fact]
    public async Task UsageGuideTool_ReturnsSameSkillAsHttpEndpoint()
    {
        var result = await _client.CallOkAsync("gemini_usage_guide", []);
        var http = await server.Http.GetStringAsync(new Uri(server.BaseUri, "/skill"), Ct);
        Assert.Equal(http, result.Text());
    }

    [Fact]
    public async Task MissingLocalFile_ReturnsToolError()
    {
        var result = await _client.CallToolAsync("gemini_analyze_media", new Dictionary<string, object?>
        {
            ["files"] = new[] { @"C:\no\such\file.mp4" },
            ["prompt"] = "x",
        }, cancellationToken: Ct);

        Assert.True(result.IsError);
        Assert.Contains("파일을 찾을 수 없습니다", result.Text());
    }

    [Fact]
    public async Task ReadUrls_WithoutUrls_ReturnsToolError()
    {
        var result = await _client.CallToolAsync("gemini_read_urls", new Dictionary<string, object?>
        {
            ["urls"] = Array.Empty<string>(),
            ["prompt"] = "x",
        }, cancellationToken: Ct);

        Assert.True(result.IsError);
    }
}
