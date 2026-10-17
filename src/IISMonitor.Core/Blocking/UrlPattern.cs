namespace IISMonitor.Core.Blocking;

/// <summary>
/// URL path patterns, separated by ";": case-insensitive, "*" matches any run of characters
/// (including "/"), "?" matches one character, everything else matches itself.
/// "/wp-login.php; /.git/*; *.env".
/// </summary>
public sealed class UrlPattern
{
    private readonly string[] _patterns;

    public UrlPattern(string pattern)
    {
        Text = pattern ?? "";
        _patterns = Split(Text).ToArray();
    }

    public string Text { get; }

    public bool IsMatch(ReadOnlySpan<char> path)
    {
        foreach (var pattern in _patterns)
        {
            if (Match(pattern, path))
                return true;
        }

        return false;
    }

    /// <summary>The patterns, trimmed, each starting with "/" unless it starts with "*".</summary>
    public static IEnumerable<string> Split(string? text) =>
        (text ?? "").Split([';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => p[0] is '/' or '*' ? p : "/" + p);

    /// <summary>Joins patterns the way <see cref="Split"/> reads them.</summary>
    public static string Join(IEnumerable<string> patterns) => string.Join("; ", patterns);

    /// <summary>
    /// Patterns that would catch ordinary visitors: no pattern at all, or one that matches the home
    /// page, or has fewer than three literal characters ("*", "/*", "/a*"). "*.php" is allowed.
    /// </summary>
    public static bool IsTooBroad(string? text)
    {
        var patterns = Split(text).ToList();
        return patterns.Count == 0 || patterns.Any(IsTooBroadOne);
    }

    /// <summary>The patterns of <paramref name="text"/> that are too broad, for the editor to point out.</summary>
    public static IReadOnlyList<string> TooBroad(string? text) => Split(text).Where(IsTooBroadOne).ToList();

    private static bool IsTooBroadOne(string pattern)
    {
        var literal = pattern.Count(c => c is not ('*' or '?' or '/'));
        return literal < 3 || Match(pattern, "/") || Match(pattern, "/index.html") || Match(pattern, "/default.aspx");
    }

    /// <summary>Iterative glob match with single-star backtracking: linear in practice, no regex.</summary>
    private static bool Match(ReadOnlySpan<char> pattern, ReadOnlySpan<char> text)
    {
        int p = 0, t = 0, star = -1, mark = 0;
        while (t < text.Length)
        {
            if (p < pattern.Length && pattern[p] == '*')
            {
                star = p++;
                mark = t;
            }
            else if (p < pattern.Length && (pattern[p] == '?' || char.ToLowerInvariant(pattern[p]) == char.ToLowerInvariant(text[t])))
            {
                p++;
                t++;
            }
            else if (star >= 0)
            {
                p = star + 1;
                t = ++mark;
            }
            else
            {
                return false;
            }
        }

        while (p < pattern.Length && pattern[p] == '*')
            p++;
        return p == pattern.Length;
    }
}
