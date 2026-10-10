using System.Text;

namespace IISMonitor.Core.Collection;

/// <summary>
/// Replaces literal values in T-SQL with "?" so stored statements don't keep customer data
/// (names, e-mail addresses, IDs) and so the same query shape groups together. Comments are
/// dropped and whitespace collapsed. Identifiers, parameters (@p0) and keywords are kept.
/// </summary>
public static class SqlTextNormalizer
{
    public const int MaxLength = 4000;

    public static string Normalize(string? sql)
    {
        if (string.IsNullOrEmpty(sql))
            return "";

        var output = new StringBuilder(Math.Min(sql.Length, MaxLength + 16));
        var i = 0;
        var pendingSpace = false;

        void Emit(string text)
        {
            if (pendingSpace && output.Length > 0)
                output.Append(' ');
            pendingSpace = false;
            output.Append(text);
        }

        while (i < sql.Length && output.Length < MaxLength)
        {
            var c = sql[i];

            if (char.IsWhiteSpace(c))
            {
                pendingSpace = true;
                i++;
                continue;
            }

            // -- line comment
            if (c == '-' && Peek(sql, i + 1) == '-')
            {
                while (i < sql.Length && sql[i] != '\n')
                    i++;
                pendingSpace = true;
                continue;
            }

            // /* block comment */ (T-SQL allows nesting)
            if (c == '/' && Peek(sql, i + 1) == '*')
            {
                var depth = 0;
                while (i < sql.Length)
                {
                    if (sql[i] == '/' && Peek(sql, i + 1) == '*')
                    {
                        depth++;
                        i += 2;
                    }
                    else if (sql[i] == '*' && Peek(sql, i + 1) == '/')
                    {
                        depth--;
                        i += 2;
                        if (depth == 0)
                            break;
                    }
                    else
                    {
                        i++;
                    }
                }

                pendingSpace = true;
                continue;
            }

            // 'string' or N'string', with '' as an escaped quote.
            if (c == '\'' || ((c is 'N' or 'n') && Peek(sql, i + 1) == '\'' && !IsWordChar(Peek(sql, i - 1))))
            {
                i += c == '\'' ? 1 : 2;
                while (i < sql.Length)
                {
                    if (sql[i] == '\'' && Peek(sql, i + 1) == '\'')
                    {
                        i += 2;
                        continue;
                    }

                    if (sql[i] == '\'')
                    {
                        i++;
                        break;
                    }

                    i++;
                }

                Emit("?");
                continue;
            }

            // [bracketed identifier] is a name, kept as it is. "Double quoted" is a name too, unless
            // QUOTED_IDENTIFIER is OFF, when it is a string; keep it only when it looks like a name in
            // a name's place, so a value never survives.
            if (c is '[' or '"')
            {
                var close = c == '[' ? ']' : '"';
                var start = i++;
                while (i < sql.Length)
                {
                    if (sql[i] == close && Peek(sql, i + 1) == close)
                    {
                        i += 2;
                        continue;
                    }

                    if (sql[i++] == close)
                        break;
                }

                var token = sql[start..i];
                Emit(c == '"' && !LooksLikeQuotedName(token, output, Peek(sql, i)) ? "?" : token);
                continue;
            }

            // Numbers (incl. 0x binary, decimals, exponents), unless part of an identifier.
            if ((char.IsDigit(c) || (c == '.' && char.IsDigit(Peek(sql, i + 1)))) && !IsWordChar(Peek(sql, i - 1)))
            {
                if (c == '0' && Peek(sql, i + 1) is 'x' or 'X')
                {
                    i += 2;
                    while (i < sql.Length && Uri.IsHexDigit(sql[i]))
                        i++;
                }
                else
                {
                    while (i < sql.Length && (char.IsDigit(sql[i]) || sql[i] == '.'))
                        i++;
                    if (i < sql.Length && sql[i] is 'e' or 'E')
                    {
                        i++;
                        if (i < sql.Length && sql[i] is '+' or '-')
                            i++;
                        while (i < sql.Length && char.IsDigit(sql[i]))
                            i++;
                    }
                }

                Emit("?");
                continue;
            }

            // Words: identifiers, keywords, @parameters, #temp tables.
            if (IsWordChar(c) || c is '@' or '#')
            {
                var start = i;
                i++;
                while (i < sql.Length && (IsWordChar(sql[i]) || sql[i] is '@' or '#' or '$'))
                    i++;
                Emit(sql[start..i]);
                continue;
            }

            Emit(c.ToString());
            i++;
        }

        if (output.Length > MaxLength)
            output.Length = MaxLength;
        return output.ToString();
    }

    private static readonly HashSet<string> NameKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "FROM", "JOIN", "INTO", "UPDATE", "TABLE", "AS", "EXEC", "EXECUTE", "PROCEDURE", "PROC", "FUNCTION", "VIEW", "ON", "APPLY",
    };

    /// <summary>
    /// Whether a "double quoted" token is a name: part of a dotted name, right after a keyword that
    /// takes a name, or a plain identifier that doesn't follow an operator, comma or bracket.
    /// </summary>
    private static bool LooksLikeQuotedName(string token, StringBuilder output, char next)
    {
        if (token.Length < 3 || token[^1] != '"')
            return false;

        var end = output.Length;
        while (end > 0 && char.IsWhiteSpace(output[end - 1]))
            end--;
        var previous = end > 0 ? output[end - 1] : '\0';
        if (previous == '.' || next == '.')
            return true;

        var wordStart = end;
        while (wordStart > 0 && IsWordChar(output[wordStart - 1]))
            wordStart--;
        if (wordStart < end && NameKeywords.Contains(output.ToString(wordStart, end - wordStart)))
            return true;

        var name = token[1..^1];
        return !"=<>!+-*/%(,".Contains(previous)
               && (char.IsLetter(name[0]) || name[0] is '_' or '#' or '@')
               && name.All(ch => IsWordChar(ch) || ch is '#' or '@' or '$');
    }

    private static char Peek(string s, int index) => index >= 0 && index < s.Length ? s[index] : '\0';

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';
}
