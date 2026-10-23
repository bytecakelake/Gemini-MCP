using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using ModelContextProtocol;

namespace GeminiMcp.Gemini;

/// <summary>
/// Gemini REST API(v1beta) 클라이언트. JSON 은 AOT 안전한 JsonNode 로만 다룹니다.
/// </summary>
public sealed class GeminiClient(HttpClient http, GeminiOptions options, ILogger<GeminiClient> logger)
{
    private string ApiRoot => $"{options.BaseUrl}/v1beta";

    public async Task<JsonObject> GenerateContentAsync(string model, JsonObject request, CancellationToken ct)
    {
        logger.LogInformation("generateContent model={Model}", model);
        using var message = CreateRequest(HttpMethod.Post, $"{ApiRoot}/models/{Uri.EscapeDataString(model)}:generateContent");
        message.Content = new StringContent(request.ToJsonString(), Encoding.UTF8, "application/json");
        return await SendAsync(message, ct);
    }

    public async Task<JsonObject> ListModelsAsync(CancellationToken ct)
    {
        var models = new JsonArray();
        string? pageToken = null;
        do
        {
            var url = $"{ApiRoot}/models?pageSize=1000" + (pageToken is null ? "" : $"&pageToken={Uri.EscapeDataString(pageToken)}");
            using var message = CreateRequest(HttpMethod.Get, url);
            var page = await SendAsync(message, ct);
            if (page["models"] is JsonArray items)
                foreach (var item in items)
                    models.Add(item?.DeepClone());
            pageToken = page["nextPageToken"]?.GetValue<string>();
        } while (!string.IsNullOrEmpty(pageToken));

        return new JsonObject { ["models"] = models };
    }

    /// <summary>
    /// Files API 로 업로드(resumable)하고 파일이 ACTIVE 가 될 때까지 기다립니다. 대용량 영상/오디오/PDF 용.
    /// </summary>
    public async Task<JsonObject> UploadFileAsync(Stream content, long length, string mimeType, string displayName, CancellationToken ct)
    {
        logger.LogInformation("Files API upload {Name} ({Bytes} bytes, {Mime})", displayName, length, mimeType);

        using var start = CreateRequest(HttpMethod.Post, $"{options.BaseUrl}/upload/v1beta/files");
        start.Headers.Add("X-Goog-Upload-Protocol", "resumable");
        start.Headers.Add("X-Goog-Upload-Command", "start");
        start.Headers.Add("X-Goog-Upload-Header-Content-Length", length.ToString());
        start.Headers.Add("X-Goog-Upload-Header-Content-Type", mimeType);
        var meta = new JsonObject { ["file"] = new JsonObject { ["display_name"] = displayName } };
        start.Content = new StringContent(meta.ToJsonString(), Encoding.UTF8, "application/json");

        using var startResponse = await http.SendAsync(start, ct);
        await EnsureSuccessAsync(startResponse, ct);
        var uploadUrl = startResponse.Headers.TryGetValues("X-Goog-Upload-URL", out var values)
            ? values.FirstOrDefault()
            : null;
        if (string.IsNullOrEmpty(uploadUrl))
            throw new McpException("Gemini Files API 가 업로드 URL 을 반환하지 않았습니다.");

        using var upload = new HttpRequestMessage(HttpMethod.Post, uploadUrl);
        upload.Headers.Add("X-Goog-Upload-Offset", "0");
        upload.Headers.Add("X-Goog-Upload-Command", "upload, finalize");
        upload.Content = new StreamContent(content);
        upload.Content.Headers.ContentLength = length;
        upload.Content.Headers.ContentType = new MediaTypeHeaderValue(mimeType);

        var uploaded = await SendAsync(upload, ct);
        var file = uploaded["file"] as JsonObject
            ?? throw new McpException("Gemini Files API 응답에 file 이 없습니다.");

        // 영상 등은 서버측 처리(PROCESSING) 후 ACTIVE 가 되어야 사용할 수 있습니다.
        var name = file["name"]?.GetValue<string>();
        var deadline = DateTime.UtcNow.AddMinutes(10);
        while (file["state"]?.GetValue<string>() == "PROCESSING")
        {
            if (DateTime.UtcNow > deadline)
                throw new McpException($"파일 처리 대기 시간 초과: {name}");
            await Task.Delay(TimeSpan.FromSeconds(3), ct);
            using var poll = CreateRequest(HttpMethod.Get, $"{ApiRoot}/{name}");
            file = await SendAsync(poll, ct);
        }

        if (file["state"]?.GetValue<string>() == "FAILED")
            throw new McpException($"Gemini 가 파일 처리에 실패했습니다: {name}");

        return file;
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string url)
    {
        if (string.IsNullOrEmpty(options.ApiKey))
            throw new McpException("GEMINI_API_KEY 가 설정되지 않았습니다. 서버 실행 전 환경 변수 GEMINI_API_KEY 를 지정하세요.");

        var message = new HttpRequestMessage(method, url);
        message.Headers.Add("x-goog-api-key", options.ApiKey);
        return message;
    }

    private async Task<JsonObject> SendAsync(HttpRequestMessage message, CancellationToken ct)
    {
        using var response = await http.SendAsync(message, ct);
        await EnsureSuccessAsync(response, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        return JsonNode.Parse(body) as JsonObject ?? new JsonObject();
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
            return;

        var body = await response.Content.ReadAsStringAsync(ct);
        string detail = body;
        try
        {
            if (JsonNode.Parse(body)?["error"] is JsonObject error)
                detail = $"{error["status"]?.GetValue<string>()}: {error["message"]?.GetValue<string>()}";
        }
        catch (System.Text.Json.JsonException)
        {
        }

        if (detail.Length > 2000)
            detail = detail[..2000] + "…";
        throw new McpException($"Gemini API 오류 ({(int)response.StatusCode} {response.StatusCode}) {detail}");
    }
}
