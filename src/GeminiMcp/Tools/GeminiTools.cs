using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GeminiMcp.Gemini;
using GeminiMcp.Skill;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace GeminiMcp.Tools;

/// <summary>
/// Claude 가 직접 하기 어려운 작업(구글 검색 그라운딩, 영상/오디오 이해, 이미지 생성, 음성 합성,
/// 대용량 컨텍스트 분석, 다른 모델의 의견)을 Gemini 에 위임하는 MCP 도구 모음.
/// </summary>
[McpServerToolType]
public sealed class GeminiTools(GeminiClient gemini, MediaResolver media, GeminiOptions options)
{
    [McpServerTool(Name = "gemini_ask", Title = "Ask Gemini", ReadOnly = true, OpenWorld = true)]
    [Description("""
        Gemini 에게 프롬프트를 보내고 답변을 받습니다. 세컨드 오피니언, 교차 검증, 1M 토큰급 대용량 문서/코드 분석,
        JSON 구조화 출력에 사용합니다. files 로 로컬 파일/URL(이미지, PDF, 영상, 오디오, 텍스트)을 함께 첨부할 수 있습니다.
        """)]
    public async Task<string> AskAsync(
        [Description("Gemini 에게 보낼 질문/지시문")] string prompt,
        [Description("첨부할 파일들: 서버 PC 의 절대 경로, http(s) URL, YouTube URL")] string[]? files = null,
        [Description("시스템 지시문(역할, 말투, 규칙)")] string? system_instruction = null,
        [Description("모델 이름 또는 별칭 'flash'(기본)/'pro'. 예: gemini-3.1-pro-preview")] string? model = null,
        [Description("0.0~2.0 샘플링 온도")] double? temperature = null,
        [Description("최대 출력 토큰 수")] int? max_output_tokens = null,
        [Description("JSON Schema 문자열. 지정하면 이 스키마를 따르는 JSON 으로만 응답합니다")] string? json_schema = null,
        CancellationToken cancellationToken = default)
    {
        var parts = await BuildPartsAsync(prompt, files, cancellationToken);
        var request = NewRequest(parts, system_instruction);
        var config = GenerationConfig(request, temperature, max_output_tokens);
        if (!string.IsNullOrWhiteSpace(json_schema))
        {
            config["responseMimeType"] = "application/json";
            config["responseJsonSchema"] = ParseJson(json_schema, nameof(json_schema));
        }

        var resolved = options.ResolveModel(model);
        var response = await gemini.GenerateContentAsync(resolved, request, cancellationToken);
        return GeminiResponse.Parse(resolved, response).ToToolText();
    }

    [McpServerTool(Name = "gemini_web_search", Title = "Google Search via Gemini", ReadOnly = true, OpenWorld = true)]
    [Description("""
        Google 검색 그라운딩으로 최신 정보를 조사하고 출처 URL 목록과 함께 요약 답변을 받습니다.
        최신 뉴스, 가격, 릴리스 노트, 라이브러리 최신 버전 확인 등 학습 데이터 이후의 정보가 필요할 때 사용합니다.
        """)]
    public async Task<string> WebSearchAsync(
        [Description("검색/조사할 질문")] string query,
        [Description("답변 형식이나 관점에 대한 추가 지시")] string? instructions = null,
        [Description("모델 이름 또는 별칭 'flash'(기본)/'pro'")] string? model = null,
        CancellationToken cancellationToken = default)
    {
        var request = NewRequest(new JsonArray(TextPart(query)), instructions);
        request["tools"] = new JsonArray(new JsonObject { ["google_search"] = new JsonObject() });

        var resolved = options.ResolveModel(model);
        var response = await gemini.GenerateContentAsync(resolved, request, cancellationToken);
        return GeminiResponse.Parse(resolved, response).ToToolText();
    }

    [McpServerTool(Name = "gemini_read_urls", Title = "Read URLs via Gemini", ReadOnly = true, OpenWorld = true)]
    [Description("""
        Gemini 의 URL Context 도구로 웹 페이지(최대 20개)를 직접 읽고 질문에 답합니다.
        긴 문서 요약, 여러 페이지 비교, 페이지에서 특정 정보 추출에 사용합니다. 필요하면 Google 검색도 함께 켭니다.
        """)]
    public async Task<string> ReadUrlsAsync(
        [Description("읽을 URL 목록 (최대 20개)")] string[] urls,
        [Description("URL 내용에 대해 수행할 작업/질문")] string prompt,
        [Description("true 면 Google 검색 그라운딩도 함께 사용")] bool also_search = false,
        [Description("모델 이름 또는 별칭 'flash'(기본)/'pro'")] string? model = null,
        CancellationToken cancellationToken = default)
    {
        if (urls is not { Length: > 0 })
            throw new McpException("urls 에 최소 1개의 URL 이 필요합니다.");
        if (urls.Length > 20)
            throw new McpException("URL 은 최대 20개까지 지원합니다.");

        var text = new StringBuilder(prompt).Append("\n\nURLs:\n");
        foreach (var url in urls)
            text.Append("- ").Append(url).Append('\n');

        var request = NewRequest(new JsonArray(TextPart(text.ToString())), null);
        var tools = new JsonArray(new JsonObject { ["url_context"] = new JsonObject() });
        if (also_search)
            tools.Add((JsonNode)new JsonObject { ["google_search"] = new JsonObject() });
        request["tools"] = tools;

        var resolved = options.ResolveModel(model);
        var response = await gemini.GenerateContentAsync(resolved, request, cancellationToken);
        return GeminiResponse.Parse(resolved, response).ToToolText();
    }

    [McpServerTool(Name = "gemini_analyze_media", Title = "Analyze video/audio/image/PDF", ReadOnly = true, OpenWorld = true)]
    [Description("""
        Claude 가 직접 처리할 수 없는 영상·오디오(그리고 이미지, 대용량 PDF)를 Gemini 로 분석합니다.
        영상 요약, 타임스탬프별 장면 설명, 음성 받아쓰기(전사), 화자 구분, 번역, 회의록 작성 등에 사용합니다.
        로컬 파일(큰 파일은 자동으로 Files API 업로드), http(s) URL, YouTube URL 을 지원합니다.
        """)]
    public async Task<string> AnalyzeMediaAsync(
        [Description("분석할 미디어: 서버 PC 의 절대 경로, http(s) URL, YouTube URL")] string[] files,
        [Description("수행할 작업. 예: '타임스탬프와 함께 요약', '화자 구분하여 전사'")] string prompt,
        [Description("모델 이름 또는 별칭 'flash'(기본)/'pro'")] string? model = null,
        CancellationToken cancellationToken = default)
    {
        if (files is not { Length: > 0 })
            throw new McpException("files 에 최소 1개의 파일/URL 이 필요합니다.");

        // Gemini 는 미디어를 텍스트 앞에 둘 때 결과가 더 좋습니다.
        var parts = await BuildPartsAsync(prompt, files, cancellationToken);
        var resolved = options.ResolveModel(model);
        var response = await gemini.GenerateContentAsync(resolved, NewRequest(parts, null), cancellationToken);
        return GeminiResponse.Parse(resolved, response).ToToolText();
    }

    [McpServerTool(Name = "gemini_run_code", Title = "Run Python in Gemini sandbox", ReadOnly = true, OpenWorld = true)]
    [Description("""
        Gemini 의 코드 실행(Python 샌드박스)으로 계산·데이터 분석·시뮬레이션을 수행하고, 실행한 코드와 출력을 돌려받습니다.
        로컬에서 코드를 실행할 수 없는 환경이거나 결과를 독립적으로 재검증하고 싶을 때 사용합니다. files 로 CSV 등을 첨부할 수 있습니다.
        """)]
    public async Task<string> RunCodeAsync(
        [Description("코드를 작성·실행해 해결할 작업")] string task,
        [Description("함께 제공할 데이터 파일 (경로/URL)")] string[]? files = null,
        [Description("모델 이름 또는 별칭 'flash'(기본)/'pro'")] string? model = null,
        CancellationToken cancellationToken = default)
    {
        var parts = await BuildPartsAsync(task, files, cancellationToken);
        var request = NewRequest(parts, null);
        request["tools"] = new JsonArray(new JsonObject { ["code_execution"] = new JsonObject() });

        var resolved = options.ResolveModel(model);
        var response = await gemini.GenerateContentAsync(resolved, request, cancellationToken);
        return GeminiResponse.Parse(resolved, response).ToToolText();
    }

    [McpServerTool(Name = "gemini_generate_image", Title = "Generate / edit image", Destructive = false, OpenWorld = true)]
    [Description("""
        Gemini 이미지 모델(Nano Banana)로 이미지를 생성하거나, input_images 를 주면 기존 이미지를 편집/합성합니다.
        결과는 PNG 등으로 저장되고 파일 경로를 반환합니다. return_image=true 면 이미지 자체도 응답에 포함합니다.
        """)]
    public async Task<CallToolResult> GenerateImageAsync(
        [Description("생성/편집 지시문 (구체적일수록 좋음: 피사체, 스타일, 구도, 조명, 텍스트)")] string prompt,
        [Description("편집/참조용 입력 이미지 경로 또는 URL")] string[]? input_images = null,
        [Description("가로세로 비율: 1:1, 16:9, 9:16, 4:3, 3:4, 3:2, 2:3, 21:9 등")] string? aspect_ratio = null,
        [Description("해상도: 1K, 2K, 4K (지원 모델에서만)")] string? image_size = null,
        [Description("저장 경로(파일 또는 폴더). 생략 시 기본 출력 폴더")] string? output_path = null,
        [Description("true 면 생성된 이미지를 응답에 포함 (토큰 사용 증가)")] bool return_image = false,
        [Description("이미지 모델 이름. 생략 시 서버 기본값")] string? model = null,
        CancellationToken cancellationToken = default)
    {
        var parts = await BuildPartsAsync(prompt, input_images, cancellationToken);
        var request = NewRequest(parts, null);
        var config = GenerationConfig(request, null, null);
        config["responseModalities"] = new JsonArray("TEXT", "IMAGE");
        if (!string.IsNullOrWhiteSpace(aspect_ratio) || !string.IsNullOrWhiteSpace(image_size))
        {
            var imageConfig = new JsonObject();
            if (!string.IsNullOrWhiteSpace(aspect_ratio))
                imageConfig["aspectRatio"] = aspect_ratio.Trim();
            if (!string.IsNullOrWhiteSpace(image_size))
                imageConfig["imageSize"] = image_size.Trim().ToUpperInvariant();
            config["imageConfig"] = imageConfig;
        }

        var resolved = string.IsNullOrWhiteSpace(model) ? options.ImageModel : options.ResolveModel(model);
        var response = GeminiResponse.Parse(resolved, await gemini.GenerateContentAsync(resolved, request, cancellationToken));

        var images = response.Blobs.Where(b => b.MimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)).ToList();
        if (images.Count == 0)
            throw new McpException($"이미지가 생성되지 않았습니다. 모델 응답: {response.ToToolText(includeSources: false)}");

        var result = new CallToolResult { Content = [] };
        var summary = new StringBuilder($"{images.Count}개의 이미지를 저장했습니다:\n");
        for (var i = 0; i < images.Count; i++)
        {
            var (mime, data) = images[i];
            var path = OutputFiles.Resolve(options, output_path, "gemini-image", MimeTypes.ToExtension(mime), i);
            await File.WriteAllBytesAsync(path, data, cancellationToken);
            summary.Append($"- {options.Paths.ToHost(path)} ({mime}, {data.Length:N0} bytes)\n");
            if (return_image)
                result.Content.Add(ImageContentBlock.FromBytes(data, mime));
        }

        if (response.Text.Length > 0)
            summary.Append($"\n모델 코멘트: {response.Text}\n");
        summary.Append($"\n[gemini model={response.Model}]");
        result.Content.Insert(0, new TextContentBlock { Text = summary.ToString() });
        return result;
    }

    [McpServerTool(Name = "gemini_text_to_speech", Title = "Text to speech", Destructive = false, OpenWorld = true)]
    [Description("""
        Gemini TTS 로 텍스트를 자연스러운 음성(WAV, 24kHz mono)으로 합성해 파일로 저장합니다. 한국어 등 다국어를 지원합니다.
        style 로 말투/감정(예: '밝고 경쾌하게', '차분한 뉴스 앵커처럼')을 지시할 수 있습니다.
        """)]
    public async Task<string> TextToSpeechAsync(
        [Description("읽을 텍스트")] string text,
        [Description("음성 이름: Kore, Puck, Charon, Fenrir, Aoede, Leda, Orus, Zephyr 등 (기본 Kore)")] string? voice = null,
        [Description("말투/감정/속도 지시")] string? style = null,
        [Description("저장 경로(파일 또는 폴더). 생략 시 기본 출력 폴더")] string? output_path = null,
        [Description("TTS 모델 이름. 생략 시 서버 기본값")] string? model = null,
        CancellationToken cancellationToken = default)
    {
        var spoken = string.IsNullOrWhiteSpace(style) ? text : $"{style.Trim()}:\n{text}";
        var request = NewRequest(new JsonArray(TextPart(spoken)), null);
        var config = GenerationConfig(request, null, null);
        config["responseModalities"] = new JsonArray("AUDIO");
        config["speechConfig"] = new JsonObject
        {
            ["voiceConfig"] = new JsonObject
            {
                ["prebuiltVoiceConfig"] = new JsonObject { ["voiceName"] = string.IsNullOrWhiteSpace(voice) ? "Kore" : voice.Trim() },
            },
        };

        var resolved = string.IsNullOrWhiteSpace(model) ? options.TtsModel : options.ResolveModel(model);
        var response = GeminiResponse.Parse(resolved, await gemini.GenerateContentAsync(resolved, request, cancellationToken));
        var audio = response.Blobs.FirstOrDefault(b => b.MimeType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase));
        if (audio.Data is null)
            throw new McpException("오디오가 생성되지 않았습니다.");

        var isPcm = audio.MimeType.Contains("L16", StringComparison.OrdinalIgnoreCase) || audio.MimeType.Contains("pcm", StringComparison.OrdinalIgnoreCase);
        var bytes = isPcm ? OutputFiles.PcmToWav(audio.Data, audio.MimeType) : audio.Data;
        var path = OutputFiles.Resolve(options, output_path, "gemini-speech", isPcm ? ".wav" : MimeTypes.ToExtension(audio.MimeType));
        await File.WriteAllBytesAsync(path, bytes, cancellationToken);

        var seconds = isPcm ? audio.Data.Length / 2.0 / 24000 : (double?)null;
        return $"음성을 저장했습니다: {options.Paths.ToHost(path)} ({bytes.Length:N0} bytes{(seconds is null ? "" : $", 약 {seconds:F1}초")})\n[gemini model={response.Model}]";
    }

    [McpServerTool(Name = "gemini_list_models", Title = "List Gemini models", ReadOnly = true, Idempotent = true, OpenWorld = true)]
    [Description("현재 API 키로 사용 가능한 Gemini 모델 목록과 서버의 기본 모델 설정을 보여줍니다. filter 로 이름을 걸러낼 수 있습니다.")]
    public async Task<string> ListModelsAsync(
        [Description("모델 이름에 포함될 문자열 (예: 'pro', 'image', 'tts')")] string? filter = null,
        CancellationToken cancellationToken = default)
    {
        var list = await gemini.ListModelsAsync(cancellationToken);
        var sb = new StringBuilder();
        sb.Append($"기본값 — text(flash): {options.TextModel}, pro: {options.ProModel}, image: {options.ImageModel}, tts: {options.TtsModel}\n\n");

        foreach (var node in list["models"] as JsonArray ?? new JsonArray())
        {
            if (node is not JsonObject m)
                continue;
            var name = m["name"]?.GetValue<string>()?.Replace("models/", "") ?? "";
            if (!string.IsNullOrEmpty(filter) && !name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                continue;
            var methods = string.Join(",", (m["supportedGenerationMethods"] as JsonArray ?? new JsonArray()).Select(x => x?.GetValue<string>()));
            sb.Append($"- {name}  in={m["inputTokenLimit"]} out={m["outputTokenLimit"]}  [{methods}]\n");
        }

        return sb.ToString();
    }

    [McpServerTool(Name = "gemini_usage_guide", Title = "Gemini MCP usage guide (skill)", ReadOnly = true, Idempotent = true)]
    [Description("이 서버의 내장 스킬(사용 설명서)을 반환합니다. 어떤 상황에 어떤 Gemini 도구를 써야 하는지 모를 때 먼저 호출하세요.")]
    public static string UsageGuide() => SkillContent.Markdown;

    private async Task<JsonArray> BuildPartsAsync(string prompt, string[]? files, CancellationToken ct)
    {
        var parts = new JsonArray();
        foreach (var file in files ?? [])
        {
            if (!string.IsNullOrWhiteSpace(file))
                parts.Add((JsonNode)await media.ToPartAsync(file, ct));
        }
        parts.Add((JsonNode)TextPart(prompt));
        return parts;
    }

    private static JsonObject NewRequest(JsonArray parts, string? systemInstruction)
    {
        var request = new JsonObject
        {
            ["contents"] = new JsonArray(new JsonObject { ["role"] = "user", ["parts"] = parts }),
        };
        if (!string.IsNullOrWhiteSpace(systemInstruction))
            request["systemInstruction"] = new JsonObject { ["parts"] = new JsonArray(TextPart(systemInstruction)) };
        return request;
    }

    private static JsonObject GenerationConfig(JsonObject request, double? temperature, int? maxOutputTokens)
    {
        var config = new JsonObject();
        if (temperature is not null)
            config["temperature"] = temperature.Value;
        if (maxOutputTokens is not null)
            config["maxOutputTokens"] = maxOutputTokens.Value;
        request["generationConfig"] = config;
        return config;
    }

    private static JsonObject TextPart(string text) => new() { ["text"] = text };

    private static JsonNode ParseJson(string json, string parameter)
    {
        try
        {
            return JsonNode.Parse(json) ?? throw new McpException($"{parameter} 가 비어 있습니다.");
        }
        catch (JsonException ex)
        {
            throw new McpException($"{parameter} 가 올바른 JSON 이 아닙니다: {ex.Message}");
        }
    }
}
