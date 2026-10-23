using System.ComponentModel;
using ModelContextProtocol.Server;

namespace GeminiMcp.Skill;

/// <summary>어셈블리에 내장된 SKILL.md (사용 설명서).</summary>
public static class SkillContent
{
    public const string ResourceUri = "gemini://skill/SKILL.md";

    public static string Markdown { get; } = Load();

    /// <summary>MCP initialize 응답의 instructions 로 전달되는 요약.</summary>
    public const string ServerInstructions = """
        Gemini MCP: Claude 가 할 수 없거나 약한 작업을 Google Gemini 에 위임합니다.
        - 최신 정보: gemini_web_search / 웹 페이지 읽기: gemini_read_urls
        - 영상·오디오·YouTube 분석/전사: gemini_analyze_media
        - 이미지 생성·편집: gemini_generate_image / 영상 생성·편집: gemini_generate_video / 음성 합성: gemini_text_to_speech
        - Python 샌드박스 실행: gemini_run_code / 세컨드 오피니언·대용량 분석: gemini_ask
        - 모델 확인·변경: gemini_list_models 로 용도별 목록을 보고 gemini_select_model 로 선택 (환경 변수 아님)
        생성 미디어는 서버 S3 저장소에 보관되고 gemini://media/... ResourceLink 로 반환됩니다(관리자가 삭제할 수 있음).
        사용자에게 전달하거나 오래 쓸 결과물은 내려받아 클라이언트 쪽에 저장하세요.
        파일 경로는 서버 PC 기준 절대 경로. 자세한 사용법은 gemini_usage_guide 도구,
        'gemini_skill' 프롬프트 또는 gemini://skill/SKILL.md 리소스를 참고하세요.
        """;

    private static string Load()
    {
        using var stream = typeof(SkillContent).Assembly.GetManifestResourceStream("GeminiMcp.SKILL.md")
            ?? throw new InvalidOperationException("Embedded SKILL.md not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>~/.claude/skills/gemini-mcp/SKILL.md 에 설치합니다 (Claude Code 스킬).</summary>
    public static string InstallForClaudeCode(string? targetDirectory = null)
    {
        var dir = targetDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "skills", "gemini-mcp");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "SKILL.md");
        File.WriteAllText(path, Markdown);
        return path;
    }
}

[McpServerResourceType]
public sealed class SkillResources
{
    [McpServerResource(UriTemplate = SkillContent.ResourceUri, Name = "gemini-mcp-skill", Title = "Gemini MCP Skill", MimeType = "text/markdown")]
    [Description("Gemini MCP 서버 사용 설명서(스킬). 어떤 상황에 어떤 gemini_* 도구를 쓰는지 설명합니다.")]
    public static string Skill() => SkillContent.Markdown;
}

[McpServerPromptType]
public sealed class SkillPrompts
{
    [McpServerPrompt(Name = "gemini_skill", Title = "Gemini MCP 사용법")]
    [Description("Gemini MCP 스킬(사용 설명서)을 대화에 불러옵니다. task 를 주면 그 작업에 맞는 도구 선택까지 안내합니다.")]
    public static string Skill([Description("하려는 작업 (선택)")] string? task = null) =>
        string.IsNullOrWhiteSpace(task)
            ? SkillContent.Markdown
            : $"{SkillContent.Markdown}\n\n---\n위 스킬을 따라 다음 작업에 가장 알맞은 gemini_* 도구를 골라 실행하세요:\n{task}";
}
