using System.Text;
using System.Text.Json;
using GeminiMcp.Tests.Infrastructure;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace GeminiMcp.Tests;

/// <summary>
/// 실제 Gemini API 를 호출하는 도구 테스트. GEMINI_API_KEY 가 없으면 건너뜁니다.
/// 필터: dotnet test --filter-not-trait "Category=Live" 로 제외할 수 있습니다.
/// </summary>
[Collection(ServerCollection.Name)]
[Trait("Category", "Live")]
public sealed class GeminiToolTests(DefaultServer server) : IAsyncLifetime
{
    private McpClient _client = null!;

    public async ValueTask InitializeAsync()
    {
        Assert.SkipWhen(server.ApiKey is null, "GEMINI_API_KEY is not set.");
        _client = await server.ConnectAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_client is not null)
            await _client.DisposeAsync();
    }

    [Fact]
    public async Task ListModels_IncludesConfiguredDefaults()
    {
        var text = (await _client.CallOkAsync("gemini_list_models", new() { ["filter"] = "flash" })).Text();
        Assert.Contains("기본값", text);
        Assert.Contains("flash", text);
    }

    [Fact]
    public async Task Ask_AnswersArithmetic()
    {
        var text = (await _client.CallOkAsync("gemini_ask", new() { ["prompt"] = "What is 17 * 23? Answer with the number only." })).Text();
        Assert.Contains("391", text);
        Assert.Contains("[gemini model=", text);
    }

    [Fact]
    public async Task Ask_WithJsonSchema_ReturnsMatchingJson()
    {
        var text = (await _client.CallOkAsync("gemini_ask", new()
        {
            ["prompt"] = "Capital of France?",
            ["json_schema"] = """{"type":"object","properties":{"capital":{"type":"string"}},"required":["capital"]}""",
        })).Text();

        using var json = JsonDocument.Parse(text.Split("\n---")[0]);
        Assert.Contains("Paris", json.RootElement.GetProperty("capital").GetString());
    }

    [Fact]
    public async Task WebSearch_ReturnsGroundedAnswerWithSources()
    {
        var text = (await _client.CallOkAsync("gemini_web_search", new() { ["query"] = "What is the latest stable .NET release and its release date?" })).Text();
        Assert.Contains("## Sources", text);
        Assert.Contains("http", text);
    }

    [Fact]
    public async Task ReadUrls_SummarizesPage()
    {
        var text = (await _client.CallOkAsync("gemini_read_urls", new()
        {
            ["urls"] = new[] { "https://modelcontextprotocol.io/specification/2025-06-18/basic/transports" },
            ["prompt"] = "In one sentence, what is the Streamable HTTP transport?",
        })).Text();
        Assert.Contains("HTTP", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunCode_ExecutesPython()
    {
        var text = (await _client.CallOkAsync("gemini_run_code", new() { ["task"] = "Use Python to compute the sum of the first 100 prime numbers and print it." })).Text();
        Assert.Contains("24133", text);
    }

    [Fact]
    public async Task GenerateImage_SavesFileReturnsImage_AndCanBeAnalyzed()
    {
        var name = $"apple-{Guid.NewGuid():N}";
        var result = await _client.CallOkAsync("gemini_generate_image", new()
        {
            ["prompt"] = "A simple flat icon of a red apple on a white background",
            ["aspect_ratio"] = "1:1",
            ["output_path"] = $"{name}.png",
            ["return_image"] = true,
        });

        var saved = Assert.Single(Directory.GetFiles(server.OutputDirectory, $"{name}.*"));
        try
        {
            var image = Assert.Single(result.Content.OfType<ImageContentBlock>());
            // 저장된 확장자는 실제 이미지 형식과 일치해야 합니다 (요청은 .png 여도 JPEG 이면 .jpg).
            Assert.Equal(ExtensionFor(image.MimeType), Path.GetExtension(saved));
            Assert.True(new FileInfo(saved).Length > 1000);
            // 응답의 경로는 클라이언트(호스트) 기준이어야 합니다 (Docker 경로 매핑 검증).
            Assert.Contains(saved, result.Text(), StringComparison.OrdinalIgnoreCase);

            var text = (await _client.CallOkAsync("gemini_analyze_media", new()
            {
                ["files"] = new[] { saved },
                ["prompt"] = "What fruit is shown? One word.",
            })).Text();
            Assert.Contains("apple", text, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(saved);
        }
    }

    [Fact]
    public async Task TextToSpeech_WritesWav_AndCanBeTranscribed()
    {
        var name = $"speech-{Guid.NewGuid():N}.wav";
        var text = (await _client.CallOkAsync("gemini_text_to_speech", new()
        {
            ["text"] = "Hello from the Gemini MCP server. Verification complete.",
            ["voice"] = "Kore",
            ["output_path"] = name,
        })).Text();

        var wav = Path.Combine(server.OutputDirectory, name);
        try
        {
            Assert.Contains(wav, text, StringComparison.OrdinalIgnoreCase);
            var header = Encoding.ASCII.GetString(File.ReadAllBytes(wav), 0, 12);
            Assert.StartsWith("RIFF", header);
            Assert.EndsWith("WAVE", header);

            var transcript = (await _client.CallOkAsync("gemini_analyze_media", new()
            {
                ["files"] = new[] { wav },
                ["prompt"] = "Transcribe this audio verbatim.",
            })).Text();
            Assert.Contains("verification", transcript, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(wav);
        }
    }

    [Fact]
    public async Task AnalyzeMedia_DescribesYouTubeVideo()
    {
        var text = (await _client.CallOkAsync("gemini_analyze_media", new()
        {
            ["files"] = new[] { "https://www.youtube.com/watch?v=jNQXAC9IVRw" },
            ["prompt"] = "Describe this video in one sentence.",
        })).Text();
        Assert.Contains("elephant", text, StringComparison.OrdinalIgnoreCase);
    }

    private static string ExtensionFor(string mimeType) => mimeType switch
    {
        "image/png" => ".png",
        "image/jpeg" => ".jpg",
        "image/webp" => ".webp",
        _ => throw new InvalidOperationException($"Unexpected image type {mimeType}"),
    };
}

/// <summary>인라인 한도를 넘는 파일이 Gemini Files API 업로드 경로로 처리되는지 검증합니다.</summary>
[Trait("Category", "Live")]
public sealed class FilesApiUploadTests(UploadServer server) : IClassFixture<UploadServer>
{
    [Fact]
    public async Task LargeFile_IsUploadedViaFilesApi()
    {
        Assert.SkipWhen(server.ApiKey is null, "GEMINI_API_KEY is not set.");
        var ct = TestContext.Current.CancellationToken;

        var file = Path.Combine(server.OutputDirectory, "notes.txt");
        var filler = string.Concat(Enumerable.Repeat("This line is filler text for the upload test.\n", 100));
        await File.WriteAllTextAsync(file, filler + "The secret code word is PELICAN-42.\n", ct);
        Assert.True(new FileInfo(file).Length > 1024);

        await using var client = await server.ConnectAsync(ct);
        var text = (await client.CallOkAsync("gemini_analyze_media", new()
        {
            ["files"] = new[] { file },
            ["prompt"] = "What is the secret code word in this file? Answer with the code word only.",
        })).Text();

        Assert.Contains("PELICAN-42", text);
        Assert.Contains("Files API upload notes.txt", server.Log);
    }
}
