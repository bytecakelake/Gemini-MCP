using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace GeminiMcp.Gemini;

/// <summary>models.list 의 한 항목. 용도 분류와 최신순 정렬 기준을 함께 계산합니다.</summary>
public sealed partial record ModelInfo(
    string Name,
    string? DisplayName,
    string? Description,
    IReadOnlyList<string> Methods,
    int? InputTokenLimit,
    int? OutputTokenLimit)
{
    /// <summary>gemini_select_model 로 고를 수 있는 용도(text/pro/image/tts/video). 해당 없음이면 null.</summary>
    public string? Category { get; } = Classify(Name, DisplayName, Description, Methods);

    /// <summary>용도가 없는 모델의 종류 (예: 음악, 에이전트, 임베딩). 목록의 'other' 그룹에 표시합니다.</summary>
    public string? OtherKind => Category is null ? DescribeOther(Name, Description, Methods) : null;

    /// <summary>'-latest' 별칭: Google 이 가리키는 모델을 바꾸므로 항상 최신 버전을 따라갑니다.</summary>
    public bool IsLatestAlias => Name.EndsWith("-latest", StringComparison.Ordinal);

    public bool IsPreview => Name.Contains("preview", StringComparison.Ordinal) || Name.Contains("-exp", StringComparison.Ordinal);

    /// <summary>이름의 첫 버전 숫자 (gemini-3.8-flash → 3.8). 없으면 null.</summary>
    public decimal? Version { get; } = ParseVersion(Name);

    public static ModelInfo Parse(JsonObject model) => new(
        ModelCatalog.StripPrefix(model["name"]?.GetValue<string>() ?? ""),
        model["displayName"]?.GetValue<string>(),
        model["description"]?.GetValue<string>(),
        (model["supportedGenerationMethods"] as JsonArray ?? new JsonArray()).Select(x => x?.GetValue<string>()).OfType<string>().ToList(),
        model["inputTokenLimit"]?.GetValue<int>(),
        model["outputTokenLimit"]?.GetValue<int>());

    /// <summary>
    /// 최신 모델이 위로 오도록 정렬합니다: '-latest' 별칭 → gemini 계열 → 버전 높은 순 → 정식(GA) → lite 가 아닌 모델 → 이름.
    /// </summary>
    public static IOrderedEnumerable<ModelInfo> Newest(IEnumerable<ModelInfo> models) => models
        .OrderByDescending(m => m.IsLatestAlias)
        .ThenByDescending(m => m.Name.StartsWith("gemini-", StringComparison.Ordinal))
        .ThenByDescending(m => m.Version ?? -1)
        .ThenBy(m => m.IsPreview)
        .ThenBy(m => m.Name.Contains("-lite", StringComparison.Ordinal))
        .ThenBy(m => m.Name, StringComparer.Ordinal);

    // 이름에 용도가 드러나지 않거나, generateContent 를 지원해도 일반 텍스트 도구로 쓰면 안 되는 계열.
    private static readonly (string Pattern, string Kind)[] SpecialFamilies =
    [
        ("lyria", "음악 생성"),
        ("deep-research", "Deep Research 에이전트"),
        ("antigravity", "Antigravity 에이전트"),
        ("computer-use", "컴퓨터 사용 에이전트"),
        ("robotics", "로보틱스"),
        ("transcribe", "음성 전사 전용"),
        ("embedding", "임베딩"),
        ("aqa", "질의응답(AQA)"),
    ];

    private static string? Classify(string name, string? displayName, string? description, IReadOnlyList<string> methods)
    {
        var lower = name.ToLowerInvariant();
        var text = $"{displayName} {description}";

        if (lower.Contains("omni") || lower.Contains("veo"))
            return ModelCatalog.Video;
        if (!methods.Contains("generateContent"))
            return null;
        if (SpecialFamilies.Any(f => lower.Contains(f.Pattern)) || MusicWord().IsMatch(text))
            return null;
        if (TtsWord().IsMatch(lower))
            return ModelCatalog.Tts;
        // nano-banana-pro-preview 처럼 이름에 image 가 없어도 설명("Gemini 3 Pro Image Preview")으로 판별합니다.
        if (lower.Contains("image") || lower.Contains("nano-banana") || ImageWord().IsMatch(text))
            return ModelCatalog.Image;
        if (lower.Contains("native-audio") || lower.Contains("live"))
            return null;
        return ProSegment().IsMatch(lower) ? ModelCatalog.Pro : ModelCatalog.Text;
    }

    private static string DescribeOther(string name, string? description, IReadOnlyList<string> methods)
    {
        var lower = name.ToLowerInvariant();
        foreach (var (pattern, kind) in SpecialFamilies)
        {
            if (lower.Contains(pattern))
                return kind;
        }
        if (MusicWord().IsMatch(description ?? ""))
            return "음악 생성";
        if (methods.Any(m => m.StartsWith("bidi", StringComparison.Ordinal)))
            return "Live API(실시간 스트리밍) 전용";
        return "generateContent 미지원";
    }

    private static decimal? ParseVersion(string name)
    {
        var match = VersionNumber().Match(name);
        return match.Success && decimal.TryParse(match.Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var version)
            ? version
            : null;
    }

    [GeneratedRegex(@"(^|-)pro(-|$)")]
    private static partial Regex ProSegment();

    [GeneratedRegex(@"(^|-)tts(-|$)")]
    private static partial Regex TtsWord();

    [GeneratedRegex(@"\bimage\b", RegexOptions.IgnoreCase)]
    private static partial Regex ImageWord();

    [GeneratedRegex(@"\bmusic\b", RegexOptions.IgnoreCase)]
    private static partial Regex MusicWord();

    [GeneratedRegex(@"(?<=-)\d+(\.\d+)?(?=-|$)")]
    private static partial Regex VersionNumber();
}
