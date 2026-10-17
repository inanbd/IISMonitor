namespace IISMonitor.Core.Blocking;

/// <summary>
/// A URL path pattern: case-insensitive, "*" matches any run of characters (including "/"), "?"
/// matches one character, everything else matches itself. "/wp-login.php", "/.git/*", "*.env".
/// </summary>
public sealed class UrlPattern
{
    private readonly string _pattern;

    public UrlPattern(string pattern)
    {
        _pattern = pattern ?? "";
    }

    public string Text => _pattern;

    public bool IsMatch(ReadOnlySpan<char> path) => Match(_pattern, path);

    /// <summary>
    /// Patterns that would catch ordinary visitors: ones matching the home page, or with fewer than
    /// three literal characters ("*", "/*", "*.js" is allowed but "/*/" is not).
    /// </summary>
    public static bool IsTooBroad(string? pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
            return true;
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
