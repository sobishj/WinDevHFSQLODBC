using System.Text.RegularExpressions;
using HfsqlOdbcStudio.Models;

namespace HfsqlOdbcStudio.Helpers;

/// <summary>Sample statements for the statement-type selector and the explorer context menu.</summary>
public static partial class SqlTemplates
{
    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex SimpleIdentRegex();

    [GeneratedRegex(@"[^A-Za-z0-9_]")]
    private static partial Regex NonWordRegex();

    public static string QuoteIdent(string name) =>
        SimpleIdentRegex().IsMatch(name) ? name : "\"" + name.Replace("\"", "\"\"") + "\"";

    /// <summary>Builds a template. Returns null for <see cref="StatementType.Custom"/>.</summary>
    public static string? Build(StatementType type, string? table, IReadOnlyList<DbColumnInfo>? columns)
    {
        var cols = columns is { Count: > 0 } && table is not null ? columns : null;
        var t = table is null ? "table1" : QuoteIdent(table);
        var c1 = cols is null ? null : cols[0];
        var c2 = cols is null ? null : cols.Count > 1 ? cols[1] : cols[0];

        switch (type)
        {
            case StatementType.Select:
                return cols is null
                    ? $"SELECT col1, col2 FROM {t}"
                    : $"SELECT {JoinList(cols.Select(c => QuoteIdent(c.Name)).ToList(), 7)} FROM {t}";

            case StatementType.Insert:
                if (cols is null) return $"INSERT INTO {t} (col1, col2) VALUES ('value1', 'value2')";
                return $"INSERT INTO {t} ({JoinList(cols.Select(c => QuoteIdent(c.Name)).ToList(), 13)})" + Environment.NewLine +
                       $"VALUES ({JoinList(cols.Select(SampleValue).ToList(), 8)})";

            case StatementType.Update:
                return cols is null
                    ? $"UPDATE {t} SET col1 = 'value' WHERE col2 = 'value'"
                    : $"UPDATE {t} SET {QuoteIdent(c2!.Name)} = {SampleValue(c2)} WHERE {QuoteIdent(c1!.Name)} = {SampleValue(c1)}";

            case StatementType.Delete:
                return cols is null
                    ? $"DELETE FROM {t} WHERE col1 = 'value'"
                    : $"DELETE FROM {t} WHERE {QuoteIdent(c1!.Name)} = {SampleValue(c1)}";

            case StatementType.CreateTable:
                if (cols is null)
                    return table is null
                        ? "CREATE TABLE NewTable (ID INTEGER PRIMARY KEY, Name VARCHAR(50), CreatedOn DATE)"
                        : $"CREATE TABLE {t} (ID INTEGER PRIMARY KEY, Name VARCHAR(50), CreatedOn DATE)";
                var defs = cols.Select(c => "    " + QuoteIdent(c.Name) + " " + c.TypeDisplay + (c.IsNullable == false ? " NOT NULL" : ""));
                return $"CREATE TABLE {t} (" + Environment.NewLine + string.Join("," + Environment.NewLine, defs) + Environment.NewLine + ")";

            case StatementType.AlterAddColumn:
                return $"ALTER TABLE {t} ADD NewColumn VARCHAR(50)";

            case StatementType.AlterDropColumn:
                return $"ALTER TABLE {t} DROP COLUMN {(cols is null ? "col1" : QuoteIdent(cols[^1].Name))}";

            case StatementType.DropTable:
                return $"DROP TABLE {t}";

            case StatementType.CreateIndex:
                var idxCol = cols is null ? "col1" : c2!.Name;
                var idxName = $"IDX_{Sanitize(table ?? "table1")}_{Sanitize(idxCol)}";
                return $"CREATE INDEX {idxName} ON {t} ({(cols is null ? "col1" : QuoteIdent(idxCol))})";

            default:
                return null;
        }
    }

    public static string SelectTop(string table, int rows = 100) => $"SELECT TOP {rows} * FROM {QuoteIdent(table)}";

    private static string SampleValue(DbColumnInfo c)
    {
        if (c.IsNumeric) return "0";
        var u = c.TypeName.ToUpperInvariant();
        if (u.Contains("DATETIME") || u.Contains("TIMESTAMP")) return $"'{DateTime.Today:yyyy-MM-dd} 00:00:00'";
        if (u.Contains("DATE")) return $"'{DateTime.Today:yyyy-MM-dd}'";
        if (u.Contains("TIME")) return "'00:00:00'";
        if (u.Contains("BIT") || u.Contains("BOOL")) return "0";
        return "'value'";
    }

    private static string JoinList(IReadOnlyList<string> items, int indent) =>
        items.Count > 6
            ? string.Join("," + Environment.NewLine + new string(' ', indent), items)
            : string.Join(", ", items);

    private static string Sanitize(string s) => NonWordRegex().Replace(s, "_");
}
