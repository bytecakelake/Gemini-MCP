namespace GeminiMcp;

/// <summary>
/// 컨테이너에서 실행할 때 Claude 가 넘기는 호스트 경로와 컨테이너 경로를 상호 변환합니다.
/// GEMINI_MCP_PATH_MAPS="C:\Users\me=/share;/home/me/data=/data" 형식 (항목은 ';', 호스트=컨테이너).
/// </summary>
public sealed class PathMapper
{
    private readonly List<(string Host, string Local, bool WindowsHost)> _maps = [];

    public PathMapper(string? spec)
    {
        foreach (var entry in (spec ?? "").Split([';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = entry.IndexOf('=');
            if (separator <= 0 || separator == entry.Length - 1)
                throw new FormatException($"GEMINI_MCP_PATH_MAPS 항목 형식 오류: '{entry}' (호스트경로=컨테이너경로)");

            var host = entry[..separator].Trim();
            var local = entry[(separator + 1)..].Trim();
            _maps.Add((Normalize(host), Normalize(local), IsWindowsPath(host)));
        }
    }

    public bool IsEnabled => _maps.Count > 0;

    /// <summary>호스트 경로 → 서버(컨테이너) 경로. 매핑이 없으면 그대로 반환합니다.</summary>
    public string ToLocal(string path)
    {
        var normalized = Normalize(path);
        foreach (var (host, local, windows) in _maps)
        {
            if (TryStrip(normalized, host, windows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal, out var rest))
                return local + rest;
        }
        return path;
    }

    /// <summary>서버(컨테이너) 경로 → 호스트 경로. Claude 에게 돌려줄 저장 경로에 사용합니다.</summary>
    public string ToHost(string path)
    {
        var normalized = Normalize(path);
        foreach (var (host, local, windows) in _maps)
        {
            if (TryStrip(normalized, local, StringComparison.Ordinal, out var rest))
                return windows ? (host + rest).Replace('/', '\\') : host + rest;
        }
        return path;
    }

    private static bool TryStrip(string path, string prefix, StringComparison comparison, out string rest)
    {
        rest = "";
        if (!path.StartsWith(prefix, comparison))
            return false;
        if (path.Length == prefix.Length)
            return true;
        if (path[prefix.Length] != '/' && !prefix.EndsWith('/'))
            return false;
        rest = path[prefix.Length..];
        return true;
    }

    private static string Normalize(string path)
    {
        var p = path.Trim().Trim('"').Replace('\\', '/');
        return p.Length > 1 ? p.TrimEnd('/') : p;
    }

    private static bool IsWindowsPath(string path) =>
        path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':' || path.StartsWith(@"\\", StringComparison.Ordinal);
}
