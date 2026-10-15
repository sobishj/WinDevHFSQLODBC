using System.Data;
using HfsqlOdbcStudio.Helpers;
using HfsqlOdbcStudio.Models;

namespace HfsqlOdbcStudio.Services;

/// <summary>Verified = true (confirmed), false (contradicted), null (could not be checked / not applicable).</summary>
public sealed record VerificationResult(bool? Verified, string Message);

public interface ISchemaService
{
    Task<IReadOnlyList<DbTableInfo>> GetTablesAsync(bool includeSystem, CancellationToken ct = default);

    Task<IReadOnlyList<DbColumnInfo>> GetColumnsAsync(string table, CancellationToken ct = default);

    Task<VerificationResult> VerifyAsync(SqlInfo info, CancellationToken ct = default);

    Task<bool> TableExistsAsync(string table, CancellationToken ct = default);

    IReadOnlyList<DbTableInfo> GetTables(IOdbcSession session, bool includeSystem);

    IReadOnlyList<DbColumnInfo> GetColumns(IOdbcSession session, string table);

    bool TableExists(IOdbcSession session, string table);

    /// <summary>
    /// Checks index metadata (SQLStatistics). An index on <paramref name="column"/> under another name also counts,
    /// because HFSQL reports keys under its own names. Returns null when the driver does not expose index metadata.
    /// </summary>
    bool? IndexExists(IOdbcSession session, string table, string index, string? column, out string note);

    VerificationResult Verify(IOdbcSession session, SqlInfo info);
}

public sealed class SchemaService(IConnectionService connections) : ISchemaService
{
    public async Task<IReadOnlyList<DbTableInfo>> GetTablesAsync(bool includeSystem, CancellationToken ct = default)
    {
        using var session = await connections.OpenActiveAsync(ct);
        return await Task.Run(() => GetTables(session, includeSystem), ct);
    }

    public async Task<IReadOnlyList<DbColumnInfo>> GetColumnsAsync(string table, CancellationToken ct = default)
    {
        using var session = await connections.OpenActiveAsync(ct);
        return await Task.Run(() => GetColumns(session, table), ct);
    }

    public async Task<bool> TableExistsAsync(string table, CancellationToken ct = default)
    {
        using var session = await connections.OpenActiveAsync(ct);
        return await Task.Run(() => TableExists(session, table), ct);
    }

    public async Task<VerificationResult> VerifyAsync(SqlInfo info, CancellationToken ct = default)
    {
        try
        {
            using var session = await connections.OpenActiveAsync(ct);
            return await Task.Run(() => Verify(session, info), ct);
        }
        catch (Exception ex)
        {
            return new VerificationResult(null, "Could not verify on server: " + OdbcErrorFormatter.Format(ex));
        }
    }

    public IReadOnlyList<DbTableInfo> GetTables(IOdbcSession session, bool includeSystem)
    {
        using var dt = session.GetCatalog(CatalogKind.Tables, null);
        var list = new List<DbTableInfo>();
        foreach (DataRow row in dt.Rows)
        {
            var name = Str(row, "TABLE_NAME");
            if (string.IsNullOrEmpty(name)) continue;
            var type = Str(row, "TABLE_TYPE") ?? "TABLE";
            var isSystem = type.Contains("SYSTEM", StringComparison.OrdinalIgnoreCase);
            if (isSystem && !includeSystem) continue;
            list.Add(new DbTableInfo(name, type, Str(row, "TABLE_CAT"), Str(row, "TABLE_SCHEM"), isSystem));
        }

        return list.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public IReadOnlyList<DbColumnInfo> GetColumns(IOdbcSession session, string table)
    {
        Exception? schemaError = null;
        try
        {
            using var dt = session.GetCatalog(CatalogKind.Columns, table);
            var list = new List<DbColumnInfo>();
            foreach (DataRow row in dt.Rows)
            {
                // The table argument may be treated as a pattern ("_" wildcard), so compare exact names.
                var tableName = Str(row, "TABLE_NAME");
                if (tableName is not null && !tableName.Equals(table, StringComparison.OrdinalIgnoreCase)) continue;
                var name = Str(row, "COLUMN_NAME");
                if (string.IsNullOrEmpty(name)) continue;

                bool? nullable = null;
                var n = Int(row, "NULLABLE");
                if (n is 0) nullable = false;
                else if (n is 1) nullable = true;
                else if (Str(row, "IS_NULLABLE") is { } isNullable)
                    nullable = isNullable.Equals("YES", StringComparison.OrdinalIgnoreCase) ? true
                        : isNullable.Equals("NO", StringComparison.OrdinalIgnoreCase) ? false : null;

                list.Add(new DbColumnInfo(
                    name,
                    Str(row, "TYPE_NAME") ?? "",
                    Int(row, "COLUMN_SIZE"),
                    Int(row, "DECIMAL_DIGITS"),
                    nullable,
                    Int(row, "ORDINAL_POSITION") ?? list.Count + 1));
            }

            if (list.Count > 0) return list.OrderBy(c => c.Ordinal).ToList();
        }
        catch (Exception ex)
        {
            schemaError = ex;
            FileLog.Error($"Column metadata for {table} failed; describing an empty result set instead", ex);
        }

        // Fallback: describe an empty result set.
        var r = session.Execute($"SELECT * FROM {SqlTemplates.QuoteIdent(table)} WHERE 1=0", returnsRows: true, maxRows: 1, CancellationToken.None);
        if (!r.Success || r.Data is null)
            throw schemaError ?? r.Error ?? new InvalidOperationException(r.ErrorText);
        return r.Data.Columns.Cast<DataColumn>()
            .Select((c, i) => new DbColumnInfo(c.Caption, c.DataType.Name, null, null, null, i + 1))
            .ToList();
    }

    public bool TableExists(IOdbcSession session, string table)
    {
        try
        {
            using var dt = session.GetCatalog(CatalogKind.Tables, table);
            foreach (DataRow row in dt.Rows)
            {
                if (string.Equals(Str(row, "TABLE_NAME"), table, StringComparison.OrdinalIgnoreCase)) return true;
            }
        }
        catch (Exception ex)
        {
            FileLog.Error("Table metadata with a table name failed, falling back to the full list", ex);
        }

        return GetTables(session, includeSystem: true).Any(t => t.Name.Equals(table, StringComparison.OrdinalIgnoreCase));
    }

    public bool? IndexExists(IOdbcSession session, string table, string index, string? column, out string note)
    {
        try
        {
            using var dt = session.GetCatalog(CatalogKind.Indexes, table);
            var reported = new List<(string Index, string Column)>();
            foreach (DataRow row in dt.Rows)
            {
                var name = Str(row, "INDEX_NAME");
                if (string.IsNullOrEmpty(name)) continue; // table statistics row
                reported.Add((name, Str(row, "COLUMN_NAME") ?? ""));
            }

            var listed = reported.Count == 0
                ? "The driver returned no index metadata for this table."
                : "Indexes reported by the driver: " + string.Join(", ", reported.Distinct().Select(r => $"{r.Index} ({r.Column})")) + ".";

            if (reported.Any(r => r.Index.Equals(index, StringComparison.OrdinalIgnoreCase)))
            {
                note = listed;
                return true;
            }

            var onColumn = column is null ? [] : reported.Where(r => r.Column.Equals(column, StringComparison.OrdinalIgnoreCase)).ToList();
            if (onColumn.Count > 0)
            {
                note = $"The driver lists an index on column {column} under the name \"{onColumn[0].Index}\" instead of \"{index}\". {listed}";
                return true;
            }

            note = listed;
            return false;
        }
        catch (Exception ex)
        {
            note = "The driver does not expose index metadata: " + OdbcErrorFormatter.Format(ex);
            return null;
        }
    }

    public VerificationResult Verify(IOdbcSession session, SqlInfo info)
    {
        try
        {
            switch (info.Kind)
            {
                case SqlKind.CreateTable when info.Table is not null:
                    return TableExists(session, info.Table)
                        ? new(true, $"Verified on server: table {info.Table} exists.")
                        : new(false, $"Verification FAILED: table {info.Table} was not found in the schema after CREATE TABLE.");

                case SqlKind.DropTable when info.Table is not null:
                    return !TableExists(session, info.Table)
                        ? new(true, $"Verified on server: table {info.Table} no longer exists.")
                        : new(false, $"Verification FAILED: table {info.Table} still exists in the schema after DROP TABLE.");

                case SqlKind.AlterAddColumn when info.Table is not null && info.Column is not null:
                    return HasColumn(session, info.Table, info.Column)
                        ? new(true, $"Verified on server: table {info.Table} has column {info.Column}.")
                        : new(false, $"Verification FAILED: column {info.Column} was not found in table {info.Table} after ALTER TABLE … ADD.");

                case SqlKind.AlterDropColumn when info.Table is not null && info.Column is not null:
                    return !HasColumn(session, info.Table, info.Column)
                        ? new(true, $"Verified on server: column {info.Column} no longer exists in table {info.Table}.")
                        : new(false, $"Verification FAILED: column {info.Column} still exists in table {info.Table} after ALTER TABLE … DROP.");

                case SqlKind.CreateIndex when info.Table is not null && info.Index is not null:
                {
                    var exists = IndexExists(session, info.Table, info.Index, info.Column, out var note);
                    return exists switch
                    {
                        true => new(true, $"Verified on server: index {info.Index} exists on table {info.Table}. {note}".Trim()),
                        false => new(false, $"Verification FAILED: index {info.Index} is not listed in the index metadata of {info.Table}. {note}".Trim()),
                        _ => new(null, $"Could not verify index {info.Index}. {note}"),
                    };
                }

                case SqlKind.DropIndex when info.Table is not null && info.Index is not null:
                {
                    var exists = IndexExists(session, info.Table, info.Index, null, out var note);
                    return exists switch
                    {
                        false => new(true, $"Verified on server: index {info.Index} no longer exists on table {info.Table}."),
                        true => new(false, $"Verification FAILED: index {info.Index} is still listed on table {info.Table}."),
                        _ => new(null, $"Could not verify index {info.Index}. {note}"),
                    };
                }

                default:
                    return new(null, "");
            }
        }
        catch (Exception ex)
        {
            return new(null, "Could not verify on server: " + OdbcErrorFormatter.Format(ex));
        }
    }

    private bool HasColumn(IOdbcSession session, string table, string column) =>
        GetColumns(session, table).Any(c => c.Name.Equals(column, StringComparison.OrdinalIgnoreCase));

    private static string? Str(DataRow row, string column)
    {
        if (!row.Table.Columns.Contains(column)) return null;
        var v = row[column];
        return v is DBNull or null ? null : Convert.ToString(v)?.Trim();
    }

    private static int? Int(DataRow row, string column)
    {
        if (!row.Table.Columns.Contains(column)) return null;
        var v = row[column];
        if (v is DBNull or null) return null;
        try
        {
            return Convert.ToInt32(v, System.Globalization.CultureInfo.InvariantCulture);
        }
        catch
        {
            return null;
        }
    }
}
