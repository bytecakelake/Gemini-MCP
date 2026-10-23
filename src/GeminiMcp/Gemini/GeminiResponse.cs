using System.Text;
using System.Text.Json.Nodes;
using ModelContextProtocol;

namespace GeminiMcp.Gemini;

/// <summary>generateContent 응답에서 텍스트·코드 실행 결과·출처·바이너리 데이터를 추출합니다.</summary>
public sealed class GeminiResponse
{
    public required string Model { get; init; }
    public required string Text { get; init; }
    public required IReadOnlyList<(string MimeType, byte[] Data)> Blobs { get; init; }
    public required IReadOnlyList<(string Title, string Uri)> Sources { get; init; }
    public required IReadOnlyList<string> SearchQueries { get; init; }
    public required IReadOnlyList<string> UrlStatuses { get; init; }
    public string? FinishReason { get; init; }
    public int? PromptTokens { get; init; }
    public int? OutputTokens { get; init; }

    public static GeminiResponse Parse(string model, JsonObject response)
    {
        var blockReason = response["promptFeedback"]?["blockReason"]?.GetValue<string>();
        if (blockReason is not null)
            throw new McpException($"Gemini 가 요청을 차단했습니다 (blockReason: {blockReason}).");

        var candidate = (response["candidates"] as JsonArray)?.FirstOrDefault() as JsonObject;
        if (candidate is null)
            throw new McpException("Gemini 응답에 candidates 가 없습니다.");

        var text = new StringBuilder();
        var blobs = new List<(string, byte[])>();
        foreach (var node in candidate["content"]?["parts"] as JsonArray ?? new JsonArray())
        {
            if (node is not JsonObject part || part["thought"]?.GetValue<bool>() == true)
                continue;

            if (part["text"]?.GetValue<string>() is { } t)
                text.Append(t);
            else if (part["executableCode"] is JsonObject code)
                text.Append($"\n```{code["language"]?.GetValue<string>()?.ToLowerInvariant() ?? ""}\n{code["code"]?.GetValue<string>()}\n```\n");
            else if (part["codeExecutionResult"] is JsonObject result)
                text.Append($"\n실행 결과 ({result["outcome"]?.GetValue<string>()}):\n```\n{result["output"]?.GetValue<string>()}\n```\n");
            else if (part["inlineData"] is JsonObject inline && inline["data"]?.GetValue<string>() is { } data)
                blobs.Add((inline["mimeType"]?.GetValue<string>() ?? "application/octet-stream", Convert.FromBase64String(data)));
        }

        var sources = new List<(string, string)>();
        var grounding = candidate["groundingMetadata"] as JsonObject;
        foreach (var chunk in grounding?["groundingChunks"] as JsonArray ?? new JsonArray())
        {
            if (chunk?["web"] is JsonObject web && web["uri"]?.GetValue<string>() is { } uri)
                sources.Add((web["title"]?.GetValue<string>() ?? uri, uri));
        }

        var queries = (grounding?["webSearchQueries"] as JsonArray ?? new JsonArray())
            .Select(q => q?.GetValue<string>()).OfType<string>().ToList();

        var urlStatuses = new List<string>();
        foreach (var meta in candidate["urlContextMetadata"]?["urlMetadata"] as JsonArray ?? new JsonArray())
        {
            if (meta is JsonObject m)
                urlStatuses.Add($"{m["retrievedUrl"]?.GetValue<string>()} → {m["urlRetrievalStatus"]?.GetValue<string>()}");
        }

        var usage = response["usageMetadata"] as JsonObject;
        return new GeminiResponse
        {
            Model = response["modelVersion"]?.GetValue<string>() ?? model,
            Text = text.ToString().Trim(),
            Blobs = blobs,
            Sources = sources,
            SearchQueries = queries,
            UrlStatuses = urlStatuses,
            FinishReason = candidate["finishReason"]?.GetValue<string>(),
            PromptTokens = usage?["promptTokenCount"]?.GetValue<int>(),
            OutputTokens = usage?["candidatesTokenCount"]?.GetValue<int>(),
        };
    }

    /// <summary>Claude 에게 돌려줄 최종 텍스트 (본문 + 출처 + 메타 정보).</summary>
    public string ToToolText(bool includeSources = true)
    {
        var sb = new StringBuilder(Text.Length > 0 ? Text : "(Gemini 가 텍스트를 반환하지 않았습니다)");

        if (includeSources && Sources.Count > 0)
        {
            sb.Append("\n\n## Sources\n");
            for (var i = 0; i < Sources.Count; i++)
                sb.Append($"[{i + 1}] {Sources[i].Title} — {Sources[i].Uri}\n");
        }

        if (SearchQueries.Count > 0)
            sb.Append($"\nSearch queries: {string.Join(" | ", SearchQueries)}\n");

        if (UrlStatuses.Count > 0)
            sb.Append($"\nURL retrieval: {string.Join("; ", UrlStatuses)}\n");

        sb.Append($"\n---\n[gemini model={Model}");
        if (FinishReason is not null and not "STOP")
            sb.Append($" finish={FinishReason}");
        if (PromptTokens is not null || OutputTokens is not null)
            sb.Append($" tokens in={PromptTokens} out={OutputTokens}");
        sb.Append(']');
        return sb.ToString();
    }
}
