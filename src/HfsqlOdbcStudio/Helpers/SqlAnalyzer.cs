using System.Text.RegularExpressions;

namespace HfsqlOdbcStudio.Helpers;

public enum SqlKind
{
    Other,
    Select,
    Insert,
    Update,
    Delete,
    CreateTable,
    AlterAddColumn,
    AlterDropColumn,
    AlterOther,
    DropTable,
    CreateIndex,
    DropIndex,
    Truncate,
    OtherDdl,
}

/// <summary>What the app understood about a statement. Used for routing, confirmation and verification only.</summary>
public sealed record SqlInfo(
    SqlKind Kind,
    string FirstKeyword,
    string? Table,
    string? Column,
    string? Index,
    bool ReturnsRows,
    bool IsDdl,
    string? DangerReason)
{
    public bool IsDangerous => DangerReason is not null;
}

public static partial class SqlAnalyzer
{
    private const string Ident = @"(?:""[^""]+""|\[[^\]]+\]|`[^`]+`|[\p{L}\p{N}_$#@]+)";
    private const string QIdent = Ident + @"(?:\s*\.\s*" + Ident + ")*";
    private const RegexOptions Opts = RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant;

    private static readonly HashSet<string> RowKeywords = new(StringComparer.OrdinalIgnoreCase)
        { "SELECT", "WITH", "SHOW", "DESCRIBE", "DESC", "EXPLAIN", "VALUES", "PRAGMA" };

    private static readonly HashSet<string> DdlKeywords = new(StringComparer.OrdinalIgnoreCase)
        { "CREATE", "ALTER", "DROP", "TRUNCATE", "RENAME" };

    private static readonly HashSet<string> NonColumnWords = new(StringComparer.OrdinalIgnoreCase)
        { "CONSTRAINT", "PRIMARY", "FOREIGN", "UNIQUE", "INDEX", "KEY", "CHECK" };

    [GeneratedRegex(@"^\s*\(*\s*([A-Za-z]+)", Opts)]
    private static partial Regex FirstWordRegex();

    [GeneratedRegex(@"^\s*CREATE\s+(?:(?:GLOBAL|LOCAL)\s+)?(?:(?:TEMPORARY|TEMP)\s+)?TABLE\s+(?:IF\s+NOT\s+EXISTS\s+)?(?<t>" + QIdent + ")", Opts)]
    private static partial Regex CreateTableRegex();

    [GeneratedRegex(@"^\s*DROP\s+TABLE\s+(?:IF\s+EXISTS\s+)?(?<t>" + QIdent + ")", Opts)]
    private static partial Regex DropTableRegex();

    [GeneratedRegex(@"^\s*ALTER\s+TABLE\s+(?<t>" + QIdent + @")\s+(?<op>ADD|DROP)\s+(?:(?<kw>COLUMN)\s+)?(?:IF\s+(?:NOT\s+)?EXISTS\s+)?(?<c>" + Ident + ")", Opts)]
    private static partial Regex AlterColumnRegex();

    [GeneratedRegex(@"^\s*ALTER\s+TABLE\s+(?<t>" + QIdent + ")", Opts)]
    private static partial Regex AlterTableRegex();

    [GeneratedRegex(@"^\s*CREATE\s+(?:UNIQUE\s+)?(?:(?:CLUSTERED|NONCLUSTERED)\s+)?INDEX\s+(?<i>" + QIdent + @")\s+ON\s+(?<t>" + QIdent + @")(?:\s*\(\s*(?<c>" + Ident + "))?", Opts)]
    private static partial Regex CreateIndexRegex();

    [GeneratedRegex(@"^\s*DROP\s+INDEX\s+(?<i>" + QIdent + @")(?:\s+ON\s+(?<t>" + QIdent + "))?", Opts)]
    private static partial Regex DropIndexRegex();

    [GeneratedRegex(@"^\s*DELETE\s+(?:FROM\s+)?(?<t>" + QIdent + ")", Opts)]
    private static partial Regex DeleteRegex();

    [GeneratedRegex(@"^\s*TRUNCATE\s+(?:TABLE\s+)?(?<t>" + QIdent + ")", Opts)]
    private static partial Regex TruncateRegex();

    [GeneratedRegex(@"\bWHERE\b", Opts)]
    private static partial Regex WhereRegex();

    [GeneratedRegex(@"\bDROP\b", Opts)]
    private static partial Regex DropWordRegex();

    [GeneratedRegex(Ident, Opts)]
    private static partial Regex IdentRegex();

    public static SqlInfo Analyze(string sql)
    {
        var text = SqlLexer.StripComments(sql).Trim();
        var code = SqlLexer.CodeOnly(sql).Trim();
        var first = FirstWordRegex().Match(code) is { Success: true } fm ? fm.Groups[1].Value.ToUpperInvariant() : "";
        bool returnsRows = RowKeywords.Contains(first);
        bool isDdl = DdlKeywords.Contains(first);

        Match m;
        switch (first)
        {
            case "SELECT" or "WITH":
                return new SqlInfo(SqlKind.Select, first, null, null, null, true, false, null);
            case "INSERT":
                return new SqlInfo(SqlKind.Insert, first, null, null, null, false, false, null);
            case "UPDATE":
                return new SqlInfo(SqlKind.Update, first, null, null, null, false, false, null);
            case "DELETE":
                m = DeleteRegex().Match(text);
                var delTable = m.Success ? Unquote(m.Groups["t"].Value) : null;
                var noWhere = !WhereRegex().IsMatch(code);
                return new SqlInfo(SqlKind.Delete, first, delTable, null, null, false, false,
                    noWhere ? "DELETE without a WHERE clause deletes every row of the table." : null);
            case "TRUNCATE":
                m = TruncateRegex().Match(text);
                return new SqlInfo(SqlKind.Truncate, first, m.Success ? Unquote(m.Groups["t"].Value) : null, null, null, false, true,
                    "TRUNCATE deletes every row of the table.");
            case "CREATE":
                m = CreateTableRegex().Match(text);
                if (m.Success)
                    return new SqlInfo(SqlKind.CreateTable, first, Unquote(m.Groups["t"].Value), null, null, false, true, null);
                m = CreateIndexRegex().Match(text);
                if (m.Success)
                    return new SqlInfo(SqlKind.CreateIndex, first, Unquote(m.Groups["t"].Value),
                        m.Groups["c"].Success ? Unquote(m.Groups["c"].Value) : null, Unquote(m.Groups["i"].Value), false, true, null);
                return new SqlInfo(SqlKind.OtherDdl, first, null, null, null, false, true, null);
            case "DROP":
                m = DropTableRegex().Match(text);
                if (m.Success)
                    return new SqlInfo(SqlKind.DropTable, first, Unquote(m.Groups["t"].Value), null, null, false, true,
                        "DROP TABLE permanently deletes the table and all of its data.");
                m = DropIndexRegex().Match(text);
                if (m.Success)
                    return new SqlInfo(SqlKind.DropIndex, first, m.Groups["t"].Success ? Unquote(m.Groups["t"].Value) : null, null,
                        Unquote(m.Groups["i"].Value), false, true, "DROP INDEX removes an index.");
                return new SqlInfo(SqlKind.OtherDdl, first, null, null, null, false, true, "DROP permanently removes a database object.");
            case "ALTER":
                m = AlterColumnRegex().Match(text);
                if (m.Success)
                {
                    var table = Unquote(m.Groups["t"].Value);
                    var column = Unquote(m.Groups["c"].Value);
                    bool isDrop = m.Groups["op"].Value.Equals("DROP", StringComparison.OrdinalIgnoreCase);
                    bool isColumn = m.Groups["kw"].Success || !NonColumnWords.Contains(column);
                    if (isColumn)
                    {
                        return isDrop
                            ? new SqlInfo(SqlKind.AlterDropColumn, first, table, column, null, false, true,
                                "ALTER TABLE … DROP removes the column and all of its data.")
                            : new SqlInfo(SqlKind.AlterAddColumn, first, table, column, null, false, true, null);
                    }
                }

                m = AlterTableRegex().Match(text);
                return new SqlInfo(SqlKind.AlterOther, first, m.Success ? Unquote(m.Groups["t"].Value) : null, null, null, false, true,
                    DropWordRegex().IsMatch(code) ? "ALTER TABLE … DROP removes part of the table definition." : null);
            default:
                return new SqlInfo(isDdl ? SqlKind.OtherDdl : SqlKind.Other, first, null, null, null, returnsRows, isDdl, null);
        }
    }

    /// <summary>Takes the last part of a qualified identifier and removes quoting.</summary>
    public static string Unquote(string qualified)
    {
        var parts = IdentRegex().Matches(qualified);
        var last = parts.Count > 0 ? parts[^1].Value : qualified.Trim();
        if (last.Length >= 2 && ((last[0] == '"' && last[^1] == '"') || (last[0] == '[' && last[^1] == ']') || (last[0] == '`' && last[^1] == '`')))
            return last[1..^1];
        return last;
    }
}
