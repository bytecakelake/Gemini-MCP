using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace GeminiMcp.Tests.Infrastructure;

public static class ToolResults
{
    public static string Text(this CallToolResult result) =>
        string.Join("\n", result.Content.OfType<TextContentBlock>().Select(b => b.Text));

    /// <summary>도구를 호출하고 isError 가 아님을 확인한 뒤 텍스트를 반환합니다.</summary>
    public static async Task<CallToolResult> CallOkAsync(this McpClient client, string tool, Dictionary<string, object?> args)
    {
        var result = await client.CallToolAsync(tool, args, cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(result.IsError == true, $"{tool} returned isError: {result.Text()}");
        return result;
    }
}
