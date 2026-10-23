using System.Text.Json.Nodes;
using ModelContextProtocol;

namespace GeminiMcp.Gemini;

/// <summary>
/// 용도(category)별로 사용할 모델을 관리합니다. 환경 변수가 아니라 에이전트가 gemini_list_models 로 목록을 보고
/// gemini_select_model 로 고릅니다. 선택은 서버 전역이며 출력 폴더의 .gemini-mcp-models.json 에 저장되어 재시작 후에도 유지됩니다.
/// </summary>
public sealed class ModelCatalog
{
    public const string Text = "text";
    public const string Pro = "pro";
    public const string Image = "image";
    public const string Tts = "tts";
    public const string Video = "video";

    /// <summary>선택하지 않았을 때 쓰는 내장 기본값.</summary>
    public static readonly IReadOnlyDictionary<string, string> Defaults = new Dictionary<string, string>
    {
        [Text] = "gemini-flash-latest",
        [Pro] = "gemini-pro-latest",
        [Image] = "gemini-3.1-flash-image",
        [Tts] = "gemini-3.8-flash-tts",
        [Video] = "gemini-omni-1.1-flash",
    };

    public static IEnumerable<string> Categories => Defaults.Keys;

    public static readonly TimeSpan ModelListTtl = TimeSpan.FromSeconds(3600);

    private readonly Dictionary<string, string> _selected = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _lock = new();
    private readonly SemaphoreSlim _fetchLock = new(1, 1);
    private readonly string _statePath;
    private readonly GeminiClient _gemini;
    private readonly ILogger<ModelCatalog> _logger;
    private volatile ModelList? _models;

    public ModelCatalog(GeminiClient gemini, GeminiOptions options, ILogger<ModelCatalog> logger)
    {
        _gemini = gemini;
        _logger = logger;
        _statePath = Path.Combine(options.OutputDirectory, ".gemini-mcp-models.json");
        Load();
    }

    /// <summary>해당 용도에 현재 선택된 모델 (선택이 없으면 기본값).</summary>
    public string Get(string category)
    {
        category = NormalizeCategory(category);
        lock (_lock)
            return _selected.TryGetValue(category, out var model) ? model : Defaults[category];
    }

    public bool IsCustom(string category)
    {
        lock (_lock)
            return _selected.ContainsKey(NormalizeCategory(category));
    }

    /// <summary>model 이 null 이면 기본값으로 되돌립니다.</summary>
    public void Set(string category, string? model)
    {
        category = NormalizeCategory(category);
        lock (_lock)
        {
            if (model is null)
                _selected.Remove(category);
            else
                _selected[category] = StripPrefix(model);
            Save();
        }
    }

    /// <summary>
    /// 도구 호출의 model 인수를 실제 모델 이름으로 바꿉니다. 비어 있으면 category 의 선택 모델,
    /// 'flash'/'pro' 등 용도 이름은 그 용도의 선택 모델입니다.
    /// </summary>
    public string Resolve(string? requested, string category)
    {
        var value = requested?.Trim();
        if (string.IsNullOrEmpty(value) || value.Equals("default", StringComparison.OrdinalIgnoreCase))
            return Get(category);
        if (value.Equals("flash", StringComparison.OrdinalIgnoreCase))
            return Get(Text);
        if (Defaults.ContainsKey(value.ToLowerInvariant()))
            return Get(value);
        return StripPrefix(value);
    }

    /// <summary>
    /// models.list 결과. 모든 호출이 API 를 다시 부르지 않도록 <see cref="ModelListTtl"/> 동안 캐시합니다.
    /// refresh=true 면 캐시를 무시하고 새로 받아옵니다.
    /// </summary>
    public async Task<ModelList> GetModelsAsync(bool refresh, CancellationToken ct)
    {
        var cached = _models;
        if (!refresh && cached is not null && DateTimeOffset.UtcNow - cached.FetchedAt < ModelListTtl)
            return cached;

        await _fetchLock.WaitAsync(ct);
        try
        {
            // 기다리는 동안 다른 요청이 이미 새로 받아왔으면 그 결과를 씁니다.
            if (_models is { } fresh && fresh != cached && DateTimeOffset.UtcNow - fresh.FetchedAt < ModelListTtl)
                return fresh;

            var raw = await _gemini.ListModelsAsync(ct);
            var models = (raw["models"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().Select(ModelInfo.Parse).ToList();
            _models = new ModelList(models, DateTimeOffset.UtcNow);
            _logger.LogInformation("models.list 를 새로 받았습니다 ({Count}개)", models.Count);
            return _models;
        }
        finally
        {
            _fetchLock.Release();
        }
    }

    /// <summary>해당 용도에서 가장 최신인 모델 ('-latest' 별칭 우선, 정식 버전 우선).</summary>
    public static ModelInfo? Newest(ModelList list, string category) =>
        ModelInfo.Newest(list.Models.Where(m => m.Category == category)).FirstOrDefault();

    public static string NormalizeCategory(string category)
    {
        var value = category.Trim().ToLowerInvariant();
        if (value == "flash")
            return Text;
        return Defaults.ContainsKey(value)
            ? value
            : throw new McpException($"알 수 없는 category 입니다: '{category}'. 사용 가능: {string.Join(", ", Categories)}");
    }

    public static string StripPrefix(string model)
    {
        model = model.Trim();
        return model.StartsWith("models/", StringComparison.Ordinal) ? model[7..] : model;
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_statePath) || JsonNode.Parse(File.ReadAllText(_statePath)) is not JsonObject state)
                return;
            foreach (var (category, value) in state)
            {
                if (Defaults.ContainsKey(category) && value?.GetValue<string>() is { Length: > 0 } model)
                    _selected[category] = model;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "모델 선택 파일을 읽지 못했습니다: {Path}", _statePath);
        }
    }

    private void Save()
    {
        try
        {
            var state = new JsonObject();
            foreach (var (category, model) in _selected)
                state[category] = model;
            Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
            File.WriteAllText(_statePath, state.ToJsonString());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 저장에 실패해도 메모리 상의 선택은 유지됩니다 (재시작 시 초기화).
            _logger.LogWarning(ex, "모델 선택을 저장하지 못했습니다: {Path}", _statePath);
        }
    }
}

/// <summary>캐시된 models.list 결과와 받아온 시각.</summary>
public sealed record ModelList(IReadOnlyList<ModelInfo> Models, DateTimeOffset FetchedAt)
{
    public ModelInfo? Find(string name) => Models.FirstOrDefault(m => m.Name == ModelCatalog.StripPrefix(name));
}
