using System.Text;

namespace HfsqlOdbcStudio.Helpers;

internal enum SqlSegmentKind
{
    Code,
    Literal,
    Comment,
}

internal readonly record struct SqlSegment(SqlSegmentKind Kind, int Start, int Length);

/// <summary>
/// Minimal SQL lexer that separates code, quoted literals/identifiers and comments.
/// It is only used for splitting statements and for detecting statement intent; SQL is never rewritten.
/// </summary>
internal static class SqlLexer
{
    public static List<SqlSegment> Lex(string sql)
    {
        var segments = new List<SqlSegment>();
        int n = sql.Length, i = 0, codeStart = -1;

        void FlushCode(int end)
        {
            if (codeStart >= 0 && end > codeStart)
                segments.Add(new SqlSegment(SqlSegmentKind.Code, codeStart, end - codeStart));
            codeStart = -1;
        }

        while (i < n)
        {
            char c = sql[i];
            char next = i + 1 < n ? sql[i + 1] : '\0';

            if (c == '-' && next == '-')
            {
                FlushCode(i);
                int j = sql.IndexOf('\n', i);
                j = j < 0 ? n : j;
                segments.Add(new SqlSegment(SqlSegmentKind.Comment, i, j - i));
                i = j;
                continue;
            }

            if (c == '/' && next == '*')
            {
                FlushCode(i);
                int j = sql.IndexOf("*/", i + 2, StringComparison.Ordinal);
                j = j < 0 ? n : j + 2;
                segments.Add(new SqlSegment(SqlSegmentKind.Comment, i, j - i));
                i = j;
                continue;
            }

            if (c is '\'' or '"' or '`')
            {
                FlushCode(i);
                int j = i + 1;
                while (j < n)
                {
                    if (sql[j] == c)
                    {
                        if (j + 1 < n && sql[j + 1] == c)
                        {
                            j += 2;
                            continue;
                        }

                        j++;
                        break;
                    }

                    j++;
                }

                segments.Add(new SqlSegment(SqlSegmentKind.Literal, i, j - i));
                i = j;
                continue;
            }

            if (c == '[')
            {
                FlushCode(i);
                int j = sql.IndexOf(']', i + 1);
                j = j < 0 ? n : j + 1;
                segments.Add(new SqlSegment(SqlSegmentKind.Literal, i, j - i));
                i = j;
                continue;
            }

            if (codeStart < 0) codeStart = i;
            i++;
        }

        FlushCode(n);
        return segments;
    }

    /// <summary>Removes comments (replaced by a space); literals are kept.</summary>
    public static string StripComments(string sql)
    {
        var sb = new StringBuilder(sql.Length);
        foreach (var s in Lex(sql))
            sb.Append(s.Kind == SqlSegmentKind.Comment ? " " : sql.AsSpan(s.Start, s.Length));
        return sb.ToString();
    }

    /// <summary>Removes comments and empties string literals, leaving only code (for keyword detection).</summary>
    public static string CodeOnly(string sql)
    {
        var sb = new StringBuilder(sql.Length);
        foreach (var s in Lex(sql))
        {
            switch (s.Kind)
            {
                case SqlSegmentKind.Comment:
                    sb.Append(' ');
                    break;
                case SqlSegmentKind.Literal when sql[s.Start] == '\'':
                    sb.Append("''");
                    break;
                default:
                    sb.Append(sql.AsSpan(s.Start, s.Length));
                    break;
            }
        }

        return sb.ToString();
    }

    public static bool HasCode(string sql) =>
        Lex(sql).Any(s => s.Kind != SqlSegmentKind.Comment && !string.IsNullOrWhiteSpace(sql.Substring(s.Start, s.Length)));

    /// <summary>
    /// Splits a script into statements on top-level semicolons. Each statement is returned exactly as typed,
    /// except that surrounding whitespace and comments before/after the statement are trimmed.
    /// </summary>
    public static List<string> SplitStatements(string sql)
    {
        var result = new List<string>();
        int start = 0;
        foreach (var seg in Lex(sql))
        {
            if (seg.Kind != SqlSegmentKind.Code) continue;
            for (int k = seg.Start; k < seg.Start + seg.Length; k++)
            {
                if (sql[k] != ';') continue;
                Add(sql[start..k]);
                start = k + 1;
            }
        }

        Add(sql[start..]);
        return result;

        void Add(string piece)
        {
            var trimmed = TrimOuterComments(piece);
            if (trimmed.Length > 0 && HasCode(trimmed)) result.Add(trimmed);
        }
    }

    /// <summary>Trims whitespace and comments that precede the first, or follow the last, code token.</summary>
    public static string TrimOuterComments(string sql)
    {
        var segs = Lex(sql);
        int first = -1, last = -1;
        for (int i = 0; i < segs.Count; i++)
        {
            var s = segs[i];
            if (s.Kind == SqlSegmentKind.Comment) continue;
            if (string.IsNullOrWhiteSpace(sql.Substring(s.Start, s.Length))) continue;
            if (first < 0) first = i;
            last = i;
        }

        if (first < 0) return "";
        int begin = segs[first].Start;
        int end = segs[last].Start + segs[last].Length;
        return sql[begin..end].Trim();
    }
}
