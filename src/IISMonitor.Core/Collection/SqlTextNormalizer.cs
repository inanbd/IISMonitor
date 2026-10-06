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

            // [bracketed identifier] and "quoted identifier" are names, kept as they are.
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

                Emit(sql[start..i]);
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

    private static char Peek(string s, int index) => index >= 0 && index < s.Length ? s[index] : '\0';

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';
}
