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
public sealed class GeminiTools(GeminiClient gemini, MediaResolver media, ModelCatalog catalog, GeminiOptions options)
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
        [Description("모델 이름 또는 용도 별칭 'flash'/'pro'. 생략 시 gemini_select_model 로 선택된 text 모델. 예: gemini-3.1-pro-preview")] string? model = null,
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

        var resolved = catalog.Resolve(model, ModelCatalog.Text);
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
        [Description("모델 이름 또는 용도 별칭 'flash'/'pro'. 생략 시 gemini_select_model 로 선택된 text 모델")] string? model = null,
        CancellationToken cancellationToken = default)
    {
        var request = NewRequest(new JsonArray(TextPart(query)), instructions);
        request["tools"] = new JsonArray(new JsonObject { ["google_search"] = new JsonObject() });

        var resolved = catalog.Resolve(model, ModelCatalog.Text);
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
        [Description("모델 이름 또는 용도 별칭 'flash'/'pro'. 생략 시 gemini_select_model 로 선택된 text 모델")] string? model = null,
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

        var resolved = catalog.Resolve(model, ModelCatalog.Text);
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
        [Description("모델 이름 또는 용도 별칭 'flash'/'pro'. 생략 시 gemini_select_model 로 선택된 text 모델")] string? model = null,
        CancellationToken cancellationToken = default)
    {
        if (files is not { Length: > 0 })
            throw new McpException("files 에 최소 1개의 파일/URL 이 필요합니다.");

        // Gemini 는 미디어를 텍스트 앞에 둘 때 결과가 더 좋습니다.
        var parts = await BuildPartsAsync(prompt, files, cancellationToken);
        var resolved = catalog.Resolve(model, ModelCatalog.Text);
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
        [Description("모델 이름 또는 용도 별칭 'flash'/'pro'. 생략 시 gemini_select_model 로 선택된 text 모델")] string? model = null,
        CancellationToken cancellationToken = default)
    {
        var parts = await BuildPartsAsync(task, files, cancellationToken);
        var request = NewRequest(parts, null);
        request["tools"] = new JsonArray(new JsonObject { ["code_execution"] = new JsonObject() });

        var resolved = catalog.Resolve(model, ModelCatalog.Text);
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
        [Description("이미지 모델 이름. 생략 시 선택된 image 모델")] string? model = null,
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

        var resolved = catalog.Resolve(model, ModelCatalog.Image);
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
        [Description("TTS 모델 이름. 생략 시 선택된 tts 모델")] string? model = null,
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

        var resolved = catalog.Resolve(model, ModelCatalog.Tts);
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

    [McpServerTool(Name = "gemini_generate_video", Title = "Generate / edit video", Destructive = false, OpenWorld = true)]
    [Description("""
        Gemini 영상 모델(기본 gemini-omni-1.1-flash)로 텍스트·이미지에서 영상(MP4, 오디오 포함)을 생성하거나,
        input_video 또는 previous_interaction_id 로 기존 영상을 편집/연장합니다. 생성에는 수 분이 걸릴 수 있습니다.
        결과는 파일로 저장되고 경로와 interaction_id 를 반환합니다. 이어서 수정하려면 그 id 를 previous_interaction_id 로 넘기세요.
        """)]
    public async Task<string> GenerateVideoAsync(
        [Description("영상 지시문: 피사체, 동작, 카메라 움직임, 스타일, 대사/소리. 넣지 말아야 할 것도 문장으로 적습니다")] string prompt,
        [Description("시작 프레임/참조용 이미지 경로 또는 URL")] string[]? input_images = null,
        [Description("편집/연장할 영상 경로 또는 URL (업로드 영상은 10초 이하)")] string? input_video = null,
        [Description("이전 gemini_generate_video 결과의 interaction_id. 그 영상을 이어서 편집/연장합니다")] string? previous_interaction_id = null,
        [Description("작업 유형 힌트: text_to_video, image_to_video, reference_to_video, edit, extend (보통 생략)")] string? task = null,
        [Description("가로세로 비율: 16:9(기본) 또는 9:16")] string? aspect_ratio = null,
        [Description("해상도: 360p, 720p(기본), 1080p, 4k")] string? resolution = null,
        [Description("저장 경로(파일 또는 폴더). 생략 시 기본 출력 폴더")] string? output_path = null,
        [Description("영상 모델 이름. 생략 시 선택된 video 모델")] string? model = null,
        CancellationToken cancellationToken = default)
    {
        var content = new JsonArray();
        foreach (var image in input_images ?? [])
        {
            if (!string.IsNullOrWhiteSpace(image))
                content.Add((JsonNode)await media.ToInteractionContentAsync(image, cancellationToken));
        }
        if (!string.IsNullOrWhiteSpace(input_video))
            content.Add((JsonNode)await media.ToInteractionContentAsync(input_video, cancellationToken));
        content.Add((JsonNode)new JsonObject { ["type"] = "text", ["text"] = prompt });

        var resolved = catalog.Resolve(model, ModelCatalog.Video);
        var responseFormat = new JsonObject { ["type"] = "video", ["delivery"] = "uri" };
        if (!string.IsNullOrWhiteSpace(aspect_ratio))
            responseFormat["aspect_ratio"] = aspect_ratio.Trim();
        if (!string.IsNullOrWhiteSpace(resolution))
            responseFormat["resolution"] = resolution.Trim().ToLowerInvariant();

        var request = new JsonObject
        {
            ["model"] = resolved,
            ["input"] = content.Count == 1 ? JsonValue.Create(prompt) : content,
            ["response_format"] = responseFormat,
        };
        if (!string.IsNullOrWhiteSpace(previous_interaction_id))
            request["previous_interaction_id"] = previous_interaction_id.Trim();
        if (!string.IsNullOrWhiteSpace(task))
            request["generation_config"] = new JsonObject { ["video_config"] = new JsonObject { ["task"] = task.Trim().ToLowerInvariant() } };

        var interaction = await gemini.CreateInteractionAsync(request, cancellationToken);
        var id = interaction["id"]?.GetValue<string>();

        // 보통 생성 응답에 결과가 바로 들어오지만, 진행 중 상태로 오면 완료될 때까지 기다립니다.
        var deadline = DateTime.UtcNow.Add(options.RequestTimeout);
        while (interaction["status"]?.GetValue<string>() is "in_progress" or "queued" or "pending" && id is not null)
        {
            if (DateTime.UtcNow > deadline)
                throw new McpException($"영상 생성 대기 시간 초과 (interaction_id={id}).");
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            interaction = await gemini.GetInteractionAsync(id, cancellationToken);
        }

        if (interaction["status"]?.GetValue<string>() is "failed" or "cancelled" or "incomplete")
            throw new McpException($"영상 생성 실패 ({interaction["status"]}): {interaction["error"]?["message"]?.GetValue<string>() ?? interaction["error"]?.ToJsonString() ?? "원인 미상"}");

        var outputs = (interaction["steps"] as JsonArray ?? new JsonArray())
            .OfType<JsonObject>()
            .Where(s => s["type"]?.GetValue<string>() == "model_output")
            .SelectMany(s => (s["content"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
            .ToList();
        var video = outputs.FirstOrDefault(c => c["type"]?.GetValue<string>() == "video");
        var comment = string.Join("\n", outputs.Where(c => c["type"]?.GetValue<string>() == "text").Select(c => c["text"]?.GetValue<string>()));
        if (video is null)
            throw new McpException($"영상이 생성되지 않았습니다. 모델 응답: {(comment.Length > 0 ? comment : "(없음)")}");

        var mime = video["mime_type"]?.GetValue<string>() ?? "video/mp4";
        var bytes = video["data"]?.GetValue<string>() is { } data
            ? Convert.FromBase64String(data)
            : await gemini.DownloadGeneratedFileAsync(
                video["uri"]?.GetValue<string>() ?? throw new McpException("영상 응답에 data 와 uri 가 모두 없습니다."), cancellationToken);

        var path = OutputFiles.Resolve(options, output_path, "gemini-video", MimeTypes.ToExtension(mime));
        await File.WriteAllBytesAsync(path, bytes, cancellationToken);

        var sb = new StringBuilder($"영상을 저장했습니다: {options.Paths.ToHost(path)} ({mime}, {bytes.Length:N0} bytes)\n");
        if (id is not null)
            sb.Append($"interaction_id: {id} (previous_interaction_id 로 넘기면 이 영상을 이어서 편집/연장합니다)\n");
        if (comment.Length > 0)
            sb.Append($"\n모델 코멘트: {comment}\n");
        sb.Append($"\n[gemini model={interaction["model"]?.GetValue<string>() ?? resolved}]");
        return sb.ToString();
    }

    [McpServerTool(Name = "gemini_list_models", Title = "List Gemini models", ReadOnly = true, Idempotent = true, OpenWorld = true)]
    [Description("""
        현재 API 키로 사용 가능한 Gemini 모델을 용도(category: text, pro, image, tts, video)별로 최신순으로 보여주고,
        각 용도에 지금 선택된 모델(★)과, 선택/기본 모델이 목록에 없을 때의 경고(⚠)와 추천 후보를 표시합니다.
        모델을 바꾸려면 이 목록에서 고른 이름으로 gemini_select_model 을 호출하세요.
        목록은 1시간 캐시되며 refresh=true 로 즉시 새로 받아올 수 있습니다.
        """)]
    public async Task<string> ListModelsAsync(
        [Description("이 용도의 모델만 표시: text, pro, image, tts, video")] string? category = null,
        [Description("모델 이름에 포함될 문자열 (예: '3.8', 'preview')")] string? filter = null,
        [Description("true 면 용도에 해당하지 않는 모델(음악, 에이전트, 임베딩, Live 전용 등)도 표시")] bool include_other = false,
        [Description("true 면 캐시(1시간)를 무시하고 모델 목록을 새로 받아옴")] bool refresh = false,
        CancellationToken cancellationToken = default)
    {
        var only = string.IsNullOrWhiteSpace(category) ? null : ModelCatalog.NormalizeCategory(category);
        var list = await catalog.GetModelsAsync(refresh, cancellationToken);
        var sb = new StringBuilder(SelectionTable(list));

        var groups = new Dictionary<string, List<string>>();
        foreach (var m in ModelInfo.Newest(list.Models))
        {
            if (!string.IsNullOrEmpty(filter) && !m.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                continue;
            var group = m.Category ?? "other";
            // pro 모델은 text 용도로도 쓸 수 있으므로 두 그룹 모두에 보입니다.
            var targets = group == ModelCatalog.Pro ? new[] { ModelCatalog.Pro, ModelCatalog.Text } : [group];
            foreach (var target in targets)
            {
                if ((only is not null && target != only) || (target == "other" && !include_other))
                    continue;
                if (!groups.TryGetValue(target, out var lines))
                    groups[target] = lines = [];
                lines.Add(FormatModel(m, target));
            }
        }

        foreach (var group in ModelCatalog.Categories.Append("other"))
        {
            if (!groups.TryGetValue(group, out var lines))
                continue;
            sb.Append($"\n## {group}\n");
            foreach (var line in lines)
                sb.Append(line).Append('\n');
        }

        if (groups.Count == 0)
            sb.Append("\n(조건에 맞는 모델이 없습니다)\n");
        var age = DateTimeOffset.UtcNow - list.FetchedAt;
        sb.Append($"\n★ = 현재 선택, [latest] = 항상 최신 버전을 가리키는 별칭, [preview] = 미리보기(예고 없이 종료될 수 있음).\n");
        sb.Append($"목록 기준: {list.FetchedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss} ({(int)age.TotalMinutes}분 전 조회, 캐시 {(int)ModelCatalog.ModelListTtl.TotalMinutes}분). 새로 받으려면 refresh=true.\n");
        return sb.ToString();
    }

    [McpServerTool(Name = "gemini_select_model", Title = "Select Gemini model", Destructive = false, Idempotent = true, OpenWorld = true)]
    [Description("""
        용도(category)별 기본 모델을 선택합니다. 이후 해당 도구에서 model 인수를 생략하면 이 모델을 씁니다.
        category: text(gemini_ask/web_search/read_urls/analyze_media/run_code 기본), pro(model:'pro' 별칭), image, tts, video.
        model 은 gemini_list_models 에 나온 이름이며, 'default' 를 주면 내장 기본값으로, 'newest' 를 주면 그 용도의 최신 모델로 정합니다.
        선택은 서버 전역(모든 클라이언트 공통)이고 서버 재시작 후에도 유지됩니다.
        """)]
    public async Task<string> SelectModelAsync(
        [Description("용도: text, pro, image, tts, video")] string category,
        [Description("모델 이름 (예: gemini-3.1-pro-preview). 'default' = 기본값으로 초기화, 'newest' = 이 용도의 최신 모델")] string model,
        [Description("true 면 모델 목록에 없는 이름이어도 확인 없이 선택")] bool skip_validation = false,
        CancellationToken cancellationToken = default)
    {
        var key = ModelCatalog.NormalizeCategory(category);
        if (string.IsNullOrWhiteSpace(model))
            throw new McpException("model 이 비어 있습니다. gemini_list_models 로 이름을 확인하세요.");

        if (model.Trim().Equals("default", StringComparison.OrdinalIgnoreCase))
        {
            catalog.Set(key, null);
            return $"{key} 모델을 기본값({ModelCatalog.Defaults[key]})으로 되돌렸습니다.\n\n{SelectionTable(await TryGetModelsAsync(cancellationToken))}";
        }

        if (model.Trim().Equals("newest", StringComparison.OrdinalIgnoreCase))
        {
            var newest = ModelCatalog.Newest(await catalog.GetModelsAsync(refresh: true, cancellationToken), key)
                ?? throw new McpException($"{key} 용도로 분류된 모델이 목록에 없습니다.");
            catalog.Set(key, newest.Name);
            return $"{key} 모델을 최신 모델 {newest.Name} 으로 선택했습니다.\n\n{SelectionTable(await TryGetModelsAsync(cancellationToken))}";
        }

        var name = ModelCatalog.StripPrefix(model);
        var note = "";
        ModelList? list = null;
        if (!skip_validation)
        {
            list = await catalog.GetModelsAsync(refresh: false, cancellationToken);
            var match = list.Find(name);
            if (match is null)
            {
                // 캐시 이후 새로 나온 모델일 수 있으므로 한 번 새로 받아 다시 찾습니다.
                list = await catalog.GetModelsAsync(refresh: true, cancellationToken);
                match = list.Find(name);
            }
            if (match is null)
            {
                var candidates = ModelInfo.Newest(list.Models.Where(m => FitsCategory(m, key))).Select(m => m.Name).Take(15);
                throw new McpException(
                    $"'{name}' 모델을 현재 API 키의 모델 목록에서 찾을 수 없습니다. {key} 용도 후보(최신순): {string.Join(", ", candidates)}\n" +
                    "그래도 쓰려면 skip_validation=true 로 다시 호출하세요.");
            }

            if (!FitsCategory(match, key))
                note = $"\n주의: 이 모델은 '{match.Category ?? match.OtherKind}' 용도로 보입니다. {key} 도구에서 동작하지 않을 수 있습니다.\n";
            else if (match.IsPreview)
                note = "\n참고: 미리보기 모델은 예고 없이 종료될 수 있습니다.\n";
        }

        catalog.Set(key, name);
        return $"{key} 모델을 {name} 으로 선택했습니다.{note}\n\n{SelectionTable(list ?? await TryGetModelsAsync(cancellationToken))}";
    }

    /// <summary>text 용도에는 pro 모델도, pro 용도에는 text 모델도 쓸 수 있습니다.</summary>
    private static bool FitsCategory(ModelInfo model, string category) =>
        model.Category == category
        || (category == ModelCatalog.Text && model.Category == ModelCatalog.Pro)
        || (category == ModelCatalog.Pro && model.Category == ModelCatalog.Text);

    private async Task<ModelList?> TryGetModelsAsync(CancellationToken ct)
    {
        try
        {
            return await catalog.GetModelsAsync(refresh: false, ct);
        }
        catch (McpException)
        {
            // 목록을 못 받아도 선택 자체는 끝났으므로 경고 없이 표만 보여줍니다.
            return null;
        }
    }

    private string FormatModel(ModelInfo m, string group)
    {
        var sb = new StringBuilder($"- {m.Name}");
        if (group != "other" && catalog.Get(group) == m.Name)
            sb.Append(" ★");
        if (m.IsLatestAlias)
            sb.Append(" [latest]");
        else if (m.IsPreview)
            sb.Append(" [preview]");
        if (m.DisplayName is not null)
            sb.Append($" — {m.DisplayName}");
        if (m.OtherKind is not null)
            sb.Append($" ({m.OtherKind})");
        if (m.InputTokenLimit is not null)
            sb.Append($"  in={m.InputTokenLimit} out={m.OutputTokenLimit}");
        return sb.ToString();
    }

    /// <summary>용도별 현재 모델. 목록이 있으면 목록에 없는(종료되었거나 잘못된) 모델을 ⚠ 로 표시하고 최신 후보를 제안합니다.</summary>
    private string SelectionTable(ModelList? list)
    {
        var sb = new StringBuilder("현재 선택된 모델:\n");
        foreach (var category in ModelCatalog.Categories)
        {
            var current = catalog.Get(category);
            sb.Append($"- {category}: {current}{(catalog.IsCustom(category) ? "" : " (기본값)")}");
            if (list is not null && list.Find(current) is null)
            {
                var newest = ModelCatalog.Newest(list, category);
                sb.Append(" ⚠ 모델 목록에 없음(종료되었거나 이름 오류)");
                if (newest is not null)
                    sb.Append($" → 후보: {newest.Name} (gemini_select_model {{ category: \"{category}\", model: \"{newest.Name}\" }})");
            }
            sb.Append('\n');
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
