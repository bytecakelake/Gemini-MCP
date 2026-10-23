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
    public async Task ListModels_GroupsByCategoryAndShowsSelection()
    {
        var text = (await _client.CallOkAsync("gemini_list_models", new() { ["filter"] = "flash" })).Text();
        Assert.Contains("현재 선택된 모델", text);
        Assert.Contains("- video: ", text);
        Assert.Contains("## text", text);
        Assert.Contains("flash", text);

        var images = (await _client.CallOkAsync("gemini_list_models", new() { ["category"] = "image" })).Text();
        Assert.Contains("## image", images);
        Assert.DoesNotContain("## text", images);
    }

    /// <summary>이름에 용도가 드러나지 않는 최신 모델도 설명·계열로 올바르게 분류되는지 검증합니다.</summary>
    [Fact]
    public async Task ListModels_ClassifiesSpecialFamilies()
    {
        var text = (await _client.CallOkAsync("gemini_list_models", new() { ["include_other"] = true })).Text();
        var sections = ParseSections(text);

        Assert.Contains(sections["text"], l => l.Contains("gemini-flash-latest") && l.Contains("[latest]"));
        Assert.All(sections["text"].Concat(sections["pro"]), l =>
            Assert.DoesNotMatch(@"^- (lyria|deep-research|antigravity|nano-banana|gemini-nano-banana)", l));
        Assert.DoesNotContain(sections["pro"], l => l.Contains("tts"));
        if (text.Contains("nano-banana"))
            Assert.Contains(sections["image"], l => l.Contains("nano-banana"));
        if (text.Contains("lyria"))
            Assert.Contains(sections["other"], l => l.StartsWith("- lyria") && l.Contains("음악 생성"));
        Assert.Contains(sections["video"], l => l.Contains("gemini-omni"));
    }

    [Fact]
    public async Task ListModels_SortsNewestFirst()
    {
        var text = (await _client.CallOkAsync("gemini_list_models", new() { ["category"] = "text" })).Text();
        var names = ParseSections(text)["text"].Select(l => l.Split(' ')[1]).ToList();

        // '-latest' 별칭이 맨 위, 그다음 gemini 버전 번호가 내림차순.
        Assert.EndsWith("-latest", names[0]);
        var versions = names.Where(n => !n.EndsWith("-latest") && n.StartsWith("gemini-"))
            .Select(n => System.Text.RegularExpressions.Regex.Match(n, @"(?<=-)\d+(\.\d+)?(?=-|$)"))
            .Where(m => m.Success)
            .Select(m => decimal.Parse(m.Value, System.Globalization.CultureInfo.InvariantCulture))
            .ToList();
        Assert.True(versions.Count > 1);
        Assert.Equal(versions.OrderByDescending(v => v), versions);
    }

    [Fact]
    public async Task ListModels_IsCachedUntilRefresh()
    {
        Assert.SkipWhen(server.IsExternal, "Needs the server log of a local process.");
        static int Fetches(string log) => log.Split("models.list 를 새로 받았습니다").Length - 1;

        await _client.CallOkAsync("gemini_list_models", new() { ["refresh"] = true });
        var afterRefresh = Fetches(server.Log);
        await _client.CallOkAsync("gemini_list_models", new() { ["category"] = "tts" });
        var cachedText = (await _client.CallOkAsync("gemini_list_models", new())).Text();
        Assert.Equal(afterRefresh, Fetches(server.Log));
        Assert.Contains("캐시 60분", cachedText);

        await _client.CallOkAsync("gemini_list_models", new() { ["refresh"] = true });
        Assert.Equal(afterRefresh + 1, Fetches(server.Log));
    }

    private static Dictionary<string, List<string>> ParseSections(string text)
    {
        var sections = new[] { "text", "pro", "image", "tts", "video", "other" }.ToDictionary(s => s, _ => new List<string>());
        List<string>? current = null;
        foreach (var line in text.Split('\n'))
        {
            if (line.StartsWith("## "))
                current = sections[line[3..].Trim()];
            else if (line.StartsWith("- ") && current is not null)
                current.Add(line);
            else if (line.Length == 0)
                current = null;
        }
        return sections;
    }

    [Fact]
    public async Task SelectModel_RejectsUnknownModel()
    {
        var result = await _client.CallToolAsync("gemini_select_model", new Dictionary<string, object?>
        {
            ["category"] = "text",
            ["model"] = "gemini-does-not-exist-123",
        }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.IsError);
        Assert.Contains("찾을 수 없습니다", result.Text());
    }

    /// <summary>영상 생성은 느리고 비용이 커서 GEMINI_MCP_TEST_VIDEO=1 일 때만 실행합니다.</summary>
    [Fact]
    public async Task GenerateVideo_ReturnsMp4Link()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("GEMINI_MCP_TEST_VIDEO") == "1", "Set GEMINI_MCP_TEST_VIDEO=1 to run.");
        var result = await _client.CallOkAsync("gemini_generate_video", new()
        {
            ["prompt"] = "A red ball slowly rolling across a wooden table, static camera",
            ["resolution"] = "360p",
        });

        Assert.Contains("interaction_id", result.Text());
        var link = Assert.Single(result.Content.OfType<ResourceLinkBlock>());
        var read = await _client.ReadResourceAsync(link.Uri, cancellationToken: TestContext.Current.CancellationToken);
        var bytes = Assert.IsType<BlobResourceContents>(Assert.Single(read.Contents)).DecodedData.ToArray();
        Assert.Equal("ftyp", Encoding.ASCII.GetString(bytes, 4, 4));
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
    public async Task GenerateImage_ReturnsLinkAndImage_AndCanBeAnalyzed()
    {
        var result = await _client.CallOkAsync("gemini_generate_image", new()
        {
            ["prompt"] = "A simple flat icon of a red apple on a white background",
            ["aspect_ratio"] = "1:1",
            ["return_image"] = true,
        });

        var image = Assert.Single(result.Content.OfType<ImageContentBlock>());
        // 임시 보관본은 ResourceLink 로 오고, resources/read 로 응답의 이미지와 같은 바이트를 받을 수 있어야 합니다.
        var link = Assert.Single(result.Content.OfType<ResourceLinkBlock>());
        Assert.StartsWith("gemini://media/", link.Uri);
        // 이름의 확장자는 실제 이미지 형식과 일치해야 합니다.
        Assert.EndsWith(ExtensionFor(image.MimeType), link.Uri);
        Assert.Equal(image.MimeType, link.MimeType);
        var read = await _client.ReadResourceAsync(link.Uri, cancellationToken: TestContext.Current.CancellationToken);
        var blob = Assert.IsType<BlobResourceContents>(Assert.Single(read.Contents));
        Assert.Equal(image.DecodedData.ToArray(), blob.DecodedData.ToArray());
        Assert.True(blob.DecodedData.Length > 1000);

        // 생성 미디어 URI 를 다른 도구의 입력으로 바로 넘길 수 있어야 합니다.
        var text = (await _client.CallOkAsync("gemini_analyze_media", new()
        {
            ["files"] = new[] { link.Uri },
            ["prompt"] = "What fruit is shown? One word.",
        })).Text();
        Assert.Contains("apple", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TextToSpeech_ReturnsWavLink_AndCanBeTranscribed()
    {
        var before = Directory.GetFiles(server.OutputDirectory).Length;
        var result = await _client.CallOkAsync("gemini_text_to_speech", new()
        {
            ["text"] = "Hello from the Gemini MCP server. Verification complete.",
            ["voice"] = "Kore",
        });

        // 출력 폴더에 영구 저장하지 않고 ResourceLink 만 돌려줍니다.
        Assert.Equal(before, Directory.GetFiles(server.OutputDirectory).Length);
        var link = Assert.Single(result.Content.OfType<ResourceLinkBlock>());
        Assert.Equal("audio/wav", link.MimeType);
        Assert.Contains(link.Uri, result.Text());
        // 에이전트가 곧 삭제된다는 사실을 알 수 있어야 합니다.
        Assert.Contains("자동 삭제", result.Text());
        Assert.Contains("자동 삭제", link.Description);

        var read = await _client.ReadResourceAsync(link.Uri, cancellationToken: TestContext.Current.CancellationToken);
        var wav = Assert.IsType<BlobResourceContents>(Assert.Single(read.Contents)).DecodedData.ToArray();
        Assert.Equal(link.Size, wav.Length);
        var header = Encoding.ASCII.GetString(wav, 0, 12);
        Assert.StartsWith("RIFF", header);
        Assert.EndsWith("WAVE", header);

        var transcript = (await _client.CallOkAsync("gemini_analyze_media", new()
        {
            ["files"] = new[] { link.Uri },
            ["prompt"] = "Transcribe this audio verbatim.",
        })).Text();
        Assert.Contains("verification", transcript, StringComparison.OrdinalIgnoreCase);
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
