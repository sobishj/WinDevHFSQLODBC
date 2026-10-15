using System.Data;
using System.Data.Odbc;
using System.Diagnostics;
using HfsqlOdbcStudio.Models;

namespace HfsqlOdbcStudio.Services;

public enum CatalogKind
{
    Tables,
    Columns,
    Indexes,
}

/// <summary>An open database session on one ODBC connection. Blocking; use from a worker thread.</summary>
public interface IOdbcSession : IDisposable
{
    string LayerName { get; }

    /// <summary>
    /// Returns an ODBC catalog result set (SQLTables / SQLColumns / SQLStatistics) with the standard ODBC
    /// column names (TABLE_NAME, COLUMN_NAME, TYPE_NAME, …).
    /// </summary>
    DataTable GetCatalog(CatalogKind kind, string? table);

    /// <summary>Sends one statement unchanged and returns its result or the verbatim driver errors.</summary>
    QueryResult Execute(string sql, bool returnsRows, int maxRows, CancellationToken ct);
}

/// <summary>System.Data.Odbc implementation: ExecuteReader for row-returning statements, ExecuteNonQuery otherwise.</summary>
public sealed class ManagedOdbcSession(OdbcConnection connection) : IOdbcSession
{
    public string LayerName => "System.Data.Odbc";

    public DataTable GetCatalog(CatalogKind kind, string? table) => kind switch
    {
        CatalogKind.Tables => table is null ? connection.GetSchema("Tables") : connection.GetSchema("Tables", [null, null, table]),
        CatalogKind.Columns => connection.GetSchema("Columns", [null, null, table, null]),
        _ => connection.GetSchema("Indexes", [null, null, table, null]),
    };

    public QueryResult Execute(string sql, bool returnsRows, int maxRows, CancellationToken ct)
    {
        var result = new QueryResult { Sql = sql };
        var sw = Stopwatch.StartNew();
        try
        {
            ct.ThrowIfCancellationRequested();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            cmd.CommandTimeout = 0;
            using var registration = ct.Register(static state =>
            {
                try
                {
                    ((OdbcCommand)state!).Cancel();
                }
                catch
                {
                    // The driver may not support SQLCancel; the statement then finishes normally.
                }
            }, cmd);

            if (returnsRows)
            {
                using var reader = cmd.ExecuteReader();
                result.IsResultSet = reader.FieldCount > 0;
                if (result.IsResultSet)
                {
                    result.Data = Load(reader, maxRows <= 0 ? int.MaxValue : maxRows, ct, out var truncated);
                    result.RowCount = result.Data.Rows.Count;
                    result.Truncated = truncated;
                    if (truncated)
                    {
                        try
                        {
                            cmd.Cancel();
                        }
                        catch
                        {
                            // Not all drivers support cancel; closing the reader is enough.
                        }
                    }
                }

                result.RowsAffected = reader.RecordsAffected;
            }
            else
            {
                result.RowsAffected = cmd.ExecuteNonQuery();
            }

            result.Success = true;
        }
        catch (Exception ex)
        {
            QueryService.Failed(result, ex, ct, sw.Elapsed);
        }

        result.Elapsed = sw.Elapsed;
        return result;
    }

    /// <summary>
    /// Loads up to <paramref name="maxRows"/> rows. Columns are renamed c0..cN (the real name is the caption)
    /// so that any column name, even with dots or brackets, binds safely in the DataGrid.
    /// </summary>
    private static DataTable Load(OdbcDataReader reader, int maxRows, CancellationToken ct, out bool truncated)
    {
        var table = new DataTable("Result") { Locale = System.Globalization.CultureInfo.InvariantCulture };
        int n = reader.FieldCount;
        var binary = new bool[n];
        for (int i = 0; i < n; i++)
        {
            var name = reader.GetName(i);
            if (string.IsNullOrEmpty(name)) name = $"(column {i + 1})";
            Type type;
            try
            {
                type = reader.GetFieldType(i) ?? typeof(object);
            }
            catch
            {
                type = typeof(object);
            }

            if (type == typeof(byte[]))
            {
                binary[i] = true;
                type = typeof(string);
            }

            var col = table.Columns.Add("c" + i, type);
            col.Caption = name;
        }

        var values = new object[n];
        int count = 0;
        table.BeginLoadData();
        while (count < maxRows && reader.Read())
        {
            ct.ThrowIfCancellationRequested();
            for (int i = 0; i < n; i++)
                values[i] = ReadValue(reader, i, binary[i], table.Columns[i].DataType);
            table.LoadDataRow(values, true);
            count++;
        }

        truncated = count >= maxRows && reader.Read();
        table.EndLoadData();
        return table;
    }

    private static object ReadValue(OdbcDataReader reader, int i, bool binary, Type columnType)
    {
        try
        {
            if (reader.IsDBNull(i)) return DBNull.Value;
            var v = reader.GetValue(i);
            if (binary && v is byte[] bytes)
            {
                var hex = Convert.ToHexString(bytes.Length > 64 ? bytes[..64] : bytes);
                return $"0x{hex}{(bytes.Length > 64 ? "…" : "")} ({bytes.Length} bytes)";
            }

            return v;
        }
        catch
        {
            if (columnType != typeof(string) && columnType != typeof(object)) return DBNull.Value;
            try
            {
                return reader.GetString(i);
            }
            catch
            {
                return DBNull.Value;
            }
        }
    }

    public void Dispose() => connection.Dispose();
}

/// <summary>Direct odbc32.dll implementation (see <see cref="OdbcApi"/>).</summary>
public sealed class NativeOdbcSession(NativeOdbcConnection connection) : IOdbcSession
{
    public string LayerName => "Native ODBC (odbc32.dll)";

    public DataTable GetCatalog(CatalogKind kind, string? table) => kind switch
    {
        CatalogKind.Tables => connection.Tables(table),
        CatalogKind.Columns => connection.Columns(table ?? throw new ArgumentNullException(nameof(table))),
        _ => connection.Statistics(table ?? throw new ArgumentNullException(nameof(table))),
    };

    public QueryResult Execute(string sql, bool returnsRows, int maxRows, CancellationToken ct) =>
        connection.Execute(sql, maxRows, ct);

    public void Dispose() => connection.Dispose();
}
