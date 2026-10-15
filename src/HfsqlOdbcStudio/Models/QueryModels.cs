using System.Data;

namespace HfsqlOdbcStudio.Models;

/// <summary>One error record from an OdbcException.Errors collection, kept verbatim.</summary>
public sealed record OdbcErrorInfo(string SqlState, int NativeError, string Message, string Source);

/// <summary>Result of executing one SQL statement.</summary>
public sealed class QueryResult
{
    public required string Sql { get; init; }

    public bool Success { get; set; }

    public bool Cancelled { get; set; }

    public bool IsResultSet { get; set; }

    public DataTable? Data { get; set; }

    public int RowsAffected { get; set; } = -1;

    public int RowCount { get; set; }

    public bool Truncated { get; set; }

    public TimeSpan Elapsed { get; set; }

    public Exception? Error { get; set; }

    /// <summary>Full error text: every OdbcError with SQLSTATE, native code and message, unmodified.</summary>
    public string ErrorText { get; set; } = "";

    public IReadOnlyList<OdbcErrorInfo> Errors { get; set; } = [];
}

public enum LogLevel
{
    Info,
    Success,
    Warning,
    Error,
}

/// <summary>A line in the Messages tab.</summary>
public sealed record LogEntry(DateTime Timestamp, LogLevel Level, string Title, string? Sql, string? Detail)
{
    public string TimeText => Timestamp.ToString("HH:mm:ss.fff");

    public bool HasSql => !string.IsNullOrWhiteSpace(Sql);

    public bool HasDetail => !string.IsNullOrWhiteSpace(Detail);

    public override string ToString()
    {
        var s = $"[{Timestamp:yyyy-MM-dd HH:mm:ss.fff}] {Level.ToString().ToUpperInvariant()}: {Title}";
        if (HasSql) s += Environment.NewLine + "  SQL: " + Sql;
        if (HasDetail) s += Environment.NewLine + "  " + Detail!.Replace("\n", "\n  ");
        return s;
    }
}

/// <summary>A statement in the History tab.</summary>
public sealed class HistoryEntry
{
    public DateTime Timestamp { get; set; }

    public string Sql { get; set; } = "";

    public bool Success { get; set; }

    public double ElapsedMs { get; set; }

    public string Summary { get; set; } = "";

    public string OneLine
    {
        get
        {
            var flat = string.Join(' ', Sql.Split(['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()));
            return flat.Length > 200 ? flat[..200] + "…" : flat;
        }
    }

    public string TimeText => Timestamp.ToString("yyyy-MM-dd HH:mm:ss");
}

public enum StatementType
{
    Select,
    Insert,
    Update,
    Delete,
    CreateTable,
    AlterAddColumn,
    AlterDropColumn,
    DropTable,
    CreateIndex,
    Custom,
}

public sealed record StatementTypeOption(StatementType Type, string Display)
{
    public static IReadOnlyList<StatementTypeOption> All { get; } =
    [
        new(StatementType.Select, "SELECT"),
        new(StatementType.Insert, "INSERT"),
        new(StatementType.Update, "UPDATE"),
        new(StatementType.Delete, "DELETE"),
        new(StatementType.CreateTable, "CREATE TABLE"),
        new(StatementType.AlterAddColumn, "ALTER TABLE (add column)"),
        new(StatementType.AlterDropColumn, "ALTER TABLE (drop column)"),
        new(StatementType.DropTable, "DROP TABLE"),
        new(StatementType.CreateIndex, "CREATE INDEX"),
        new(StatementType.Custom, "Custom"),
    ];

    public override string ToString() => Display;
}
