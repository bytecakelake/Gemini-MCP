using System.Net;
using System.Text;
using GeminiMcp.Tests.Infrastructure;

namespace GeminiMcp.Tests;

/// <summary>MCP 외 HTTP 엔드포인트와 보안 검사(Origin, APISIX API 키) 검증.</summary>
[Collection(ServerCollection.Name)]
public sealed class HttpEndpointTests(DefaultServer server)
{
    private const string Ping = """{"jsonrpc":"2.0","id":1,"method":"ping"}""";

    [Fact]
    public async Task Health_ReturnsOk()
    {
        var body = await server.Http.GetStringAsync(new Uri(server.BaseUri, "/health"), TestContext.Current.CancellationToken);
        Assert.StartsWith("ok", body);
    }

    [Fact]
    public async Task Skill_ReturnsEmbeddedSkillMarkdown()
    {
        using var response = await server.Http.GetAsync(new Uri(server.BaseUri, "/skill"), TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/markdown", response.Content.Headers.ContentType?.MediaType);
        Assert.StartsWith("---", body);
        Assert.Contains("name: gemini-mcp", body);
    }

    [Fact]
    public async Task Mcp_ViaGateway_WithoutKey_Returns401()
    {
        Assert.SkipUnless(server.GatewayKey is not null, "Not running against the gateway (GEMINI_MCP_API_KEY unset).");
        using var response = await PostMcpAsync(authorization: null, origin: null);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Mcp_ViaGateway_WithUnknownKey_Returns401()
    {
        Assert.SkipUnless(server.GatewayKey is not null, "Not running against the gateway (GEMINI_MCP_API_KEY unset).");
        using var response = await PostMcpAsync(authorization: "Bearer gmcp_unknown", origin: null);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Mcp_FromForeignOrigin_Returns403()
    {
        using var response = await PostMcpAsync(Bearer(server.GatewayKey), origin: "https://evil.example");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Mcp_FromLocalhostOrigin_IsAllowed()
    {
        using var response = await PostMcpAsync(Bearer(server.GatewayKey), origin: "http://localhost:3000");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static string? Bearer(string? token) => token is null ? null : $"Bearer {token}";

    private Task<HttpResponseMessage> PostMcpAsync(string? authorization, string? origin)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, server.McpEndpoint)
        {
            Content = new StringContent(Ping, Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");
        if (authorization is not null)
            request.Headers.TryAddWithoutValidation("Authorization", authorization);
        if (origin is not null)
            request.Headers.TryAddWithoutValidation("Origin", origin);
        return server.Http.SendAsync(request, TestContext.Current.CancellationToken);
    }
}
