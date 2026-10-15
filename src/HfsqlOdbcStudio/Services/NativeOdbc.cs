using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using HfsqlOdbcStudio.Models;

namespace HfsqlOdbcStudio.Services;

/// <summary>
/// Raw ODBC 3.x API (odbc32.dll, Unicode entry points).
/// </summary>
/// <remarks>
/// Some 64-bit ODBC drivers write SQLLEN/SQLULEN values (row counts, length indicators, column sizes) as 32-bit
/// integers into 64-bit buffers. System.Data.Odbc then throws "Arithmetic operation resulted in an overflow"
/// when it converts them with IntPtr.ToInt32(). This layer zero-initialises every such buffer and normalises
/// 32-bit values, so it works with those drivers. SQL text is still sent unchanged with SQLExecDirectW.
/// </remarks>
internal static class OdbcApi
{
    private const string Dll = "odbc32.dll";

    public const short SQL_HANDLE_ENV = 1;
    public const short SQL_HANDLE_DBC = 2;
    public const short SQL_HANDLE_STMT = 3;

    public const short SQL_SUCCESS = 0;
    public const short SQL_SUCCESS_WITH_INFO = 1;
    public const short SQL_NO_DATA = 100;
    public const short SQL_ERROR = -1;
    public const short SQL_INVALID_HANDLE = -2;

    public const short SQL_NTS_SHORT = -3;
    public const int SQL_NTS = -3;
    public const int SQL_ATTR_ODBC_VERSION = 200;
    public const int SQL_OV_ODBC3 = 3;
    public const short SQL_C_WCHAR = -8;
    public const long SQL_NULL_DATA = -1;
    public const long SQL_NO_TOTAL = -4;
    public const ushort SQL_DRIVER_NOPROMPT = 0;
    public const ushort SQL_INDEX_ALL = 1;
    public const ushort SQL_QUICK = 0;

    [DllImport(Dll)]
    public static extern short SQLAllocHandle(short handleType, IntPtr inputHandle, out IntPtr outputHandle);

    [DllImport(Dll)]
    public static extern short SQLFreeHandle(short handleType, IntPtr handle);

    [DllImport(Dll)]
    public static extern short SQLSetEnvAttr(IntPtr env, int attribute, IntPtr value, int stringLength);

    [DllImport(Dll, CharSet = CharSet.Unicode)]
    public static extern short SQLDriverConnectW(IntPtr dbc, IntPtr hwnd, string inConnectionString, short inLength,
        IntPtr outConnectionString, short outBufferLength, out short outLength, ushort driverCompletion);

    [DllImport(Dll)]
    public static extern short SQLDisconnect(IntPtr dbc);

    [DllImport(Dll, CharSet = CharSet.Unicode)]
    public static extern short SQLExecDirectW(IntPtr stmt, string statementText, int textLength);

    [DllImport(Dll)]
    public static extern short SQLNumResultCols(IntPtr stmt, out short columnCount);

    [DllImport(Dll, CharSet = CharSet.Unicode)]
    public static extern short SQLDescribeColW(IntPtr stmt, ushort columnNumber, [Out] char[] columnName, short bufferLength,
        out short nameLength, out short dataType, IntPtr columnSize, out short decimalDigits, out short nullable);

    [DllImport(Dll)]
    public static extern short SQLFetch(IntPtr stmt);

    [DllImport(Dll)]
    public static extern short SQLGetData(IntPtr stmt, ushort columnNumber, short targetType, IntPtr targetValue,
        IntPtr bufferLength, IntPtr strLenOrInd);

    [DllImport(Dll)]
    public static extern short SQLRowCount(IntPtr stmt, IntPtr rowCount);

    [DllImport(Dll)]
    public static extern short SQLCancel(IntPtr stmt);

    [DllImport(Dll, CharSet = CharSet.Unicode)]
    public static extern short SQLGetDiagRecW(short handleType, IntPtr handle, short recNumber, [Out] char[] sqlState,
        out int nativeError, [Out] char[] messageText, short bufferLength, out short textLength);

    [DllImport(Dll, CharSet = CharSet.Unicode)]
    public static extern short SQLTablesW(IntPtr stmt, string? catalog, short catalogLength, string? schema, short schemaLength,
        string? table, short tableLength, string? tableType, short tableTypeLength);

    [DllImport(Dll, CharSet = CharSet.Unicode)]
    public static extern short SQLColumnsW(IntPtr stmt, string? catalog, short catalogLength, string? schema, short schemaLength,
        string? table, short tableLength, string? column, short columnLength);

    [DllImport(Dll, CharSet = CharSet.Unicode)]
    public static extern short SQLStatisticsW(IntPtr stmt, string? catalog, short catalogLength, string? schema, short schemaLength,
        string? table, short tableLength, ushort unique, ushort reserved);

    public static bool Succeeded(short rc) => rc is SQL_SUCCESS or SQL_SUCCESS_WITH_INFO;

    /// <summary>
    /// Repairs an SQLLEN read from a zeroed 64-bit buffer that the driver may have filled with only 32 bits
    /// (e.g. -1 arriving as 0x00000000FFFFFFFF).
    /// </summary>
    public static long NormalizeLength(long value)
    {
        var high = (ulong)value >> 32;
        if (high == 0xFFFFFFFF) return value;                         // genuine negative 64-bit value
        if (high == 0) return value > int.MaxValue ? (int)(uint)value : value; // 32-bit value, sign-extend
        return (int)(uint)((ulong)value & 0xFFFFFFFF);                // garbage in the high half
    }

    public static long ReadLength(IntPtr buffer) => NormalizeLength(Marshal.ReadInt64(buffer));

    public static void Zero(IntPtr buffer, int bytes)
    {
        for (int i = 0; i < bytes; i += 8) Marshal.WriteInt64(buffer, i, 0);
    }
}

/// <summary>An ODBC error raised by the native layer; carries every diagnostic record verbatim.</summary>
public sealed class NativeOdbcException(IReadOnlyList<OdbcErrorInfo> errors, string fallbackMessage)
    : Exception(errors.Count > 0 ? string.Join(Environment.NewLine, errors.Select(e => $"ERROR [{e.SqlState}] {e.Message}")) : fallbackMessage)
{
    public IReadOnlyList<OdbcErrorInfo> Errors { get; } = errors;
}

/// <summary>A minimal ODBC connection that talks to odbc32.dll directly. Not thread-safe; one operation at a time.</summary>
public sealed class NativeOdbcConnection : IDisposable
{
    private const int DataBufferBytes = 16 * 1024;
    private IntPtr _env;
    private IntPtr _dbc;
    private bool _connected;

    private NativeOdbcConnection()
    {
    }

    public static NativeOdbcConnection Open(string connectionString)
    {
        var c = new NativeOdbcConnection();
        try
        {
            c.Check(OdbcApi.SQLAllocHandle(OdbcApi.SQL_HANDLE_ENV, IntPtr.Zero, out c._env), OdbcApi.SQL_HANDLE_ENV, IntPtr.Zero, "SQLAllocHandle(ENV)");
            c.Check(OdbcApi.SQLSetEnvAttr(c._env, OdbcApi.SQL_ATTR_ODBC_VERSION, (IntPtr)OdbcApi.SQL_OV_ODBC3, 0), OdbcApi.SQL_HANDLE_ENV, c._env, "SQLSetEnvAttr");
            c.Check(OdbcApi.SQLAllocHandle(OdbcApi.SQL_HANDLE_DBC, c._env, out c._dbc), OdbcApi.SQL_HANDLE_ENV, c._env, "SQLAllocHandle(DBC)");
            var rc = OdbcApi.SQLDriverConnectW(c._dbc, IntPtr.Zero, connectionString, OdbcApi.SQL_NTS_SHORT, IntPtr.Zero, 0, out _, OdbcApi.SQL_DRIVER_NOPROMPT);
            c.Check(rc, OdbcApi.SQL_HANDLE_DBC, c._dbc, "SQLDriverConnect");
            c._connected = true;
            return c;
        }
        catch
        {
            c.Dispose();
            throw;
        }
    }

    public QueryResult Execute(string sql, int maxRows, CancellationToken ct)
    {
        var result = new QueryResult { Sql = sql };
        var sw = Stopwatch.StartNew();
        var stmt = AllocStatement();
        try
        {
            using (ct.Register(static s => OdbcApi.SQLCancel((IntPtr)s!), stmt))
            {
                ct.ThrowIfCancellationRequested();
                var rc = OdbcApi.SQLExecDirectW(stmt, sql, OdbcApi.SQL_NTS);
                if (rc != OdbcApi.SQL_NO_DATA) Check(rc, OdbcApi.SQL_HANDLE_STMT, stmt, "SQLExecDirect");

                short columns = 0;
                if (rc != OdbcApi.SQL_NO_DATA) Check(OdbcApi.SQLNumResultCols(stmt, out columns), OdbcApi.SQL_HANDLE_STMT, stmt, "SQLNumResultCols");

                if (columns > 0)
                {
                    result.IsResultSet = true;
                    result.Data = ReadResult(stmt, maxRows <= 0 ? int.MaxValue : maxRows, safeColumnNames: true, ct, out var truncated);
                    result.RowCount = result.Data.Rows.Count;
                    result.Truncated = truncated;
                    result.RowsAffected = -1;
                }
                else
                {
                    result.RowsAffected = rc == OdbcApi.SQL_NO_DATA ? 0 : RowCount(stmt);
                }

                result.Success = true;
            }
        }
        catch (Exception ex)
        {
            QueryService.Failed(result, ex, ct, sw.Elapsed);
        }
        finally
        {
            OdbcApi.SQLFreeHandle(OdbcApi.SQL_HANDLE_STMT, stmt);
        }

        result.Elapsed = sw.Elapsed;
        return result;
    }

    public DataTable Tables(string? table) => Catalog(stmt =>
        OdbcApi.SQLTablesW(stmt, null, 0, null, 0, table, table is null ? (short)0 : OdbcApi.SQL_NTS_SHORT, null, 0), "SQLTables");

    public DataTable Columns(string table) => Catalog(stmt =>
        OdbcApi.SQLColumnsW(stmt, null, 0, null, 0, table, OdbcApi.SQL_NTS_SHORT, null, 0), "SQLColumns");

    public DataTable Statistics(string table) => Catalog(stmt =>
        OdbcApi.SQLStatisticsW(stmt, null, 0, null, 0, table, OdbcApi.SQL_NTS_SHORT, OdbcApi.SQL_INDEX_ALL, OdbcApi.SQL_QUICK), "SQLStatistics");

    private DataTable Catalog(Func<IntPtr, short> call, string what)
    {
        var stmt = AllocStatement();
        try
        {
            Check(call(stmt), OdbcApi.SQL_HANDLE_STMT, stmt, what);
            return ReadResult(stmt, int.MaxValue, safeColumnNames: false, CancellationToken.None, out _);
        }
        finally
        {
            OdbcApi.SQLFreeHandle(OdbcApi.SQL_HANDLE_STMT, stmt);
        }
    }

    private IntPtr AllocStatement()
    {
        Check(OdbcApi.SQLAllocHandle(OdbcApi.SQL_HANDLE_STMT, _dbc, out var stmt), OdbcApi.SQL_HANDLE_DBC, _dbc, "SQLAllocHandle(STMT)");
        return stmt;
    }

    private int RowCount(IntPtr stmt)
    {
        var buffer = Marshal.AllocHGlobal(8);
        try
        {
            Marshal.WriteInt64(buffer, 0);
            if (!OdbcApi.Succeeded(OdbcApi.SQLRowCount(stmt, buffer))) return -1;
            var n = OdbcApi.ReadLength(buffer);
            return n is > int.MaxValue or < int.MinValue ? -1 : (int)n;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private DataTable ReadResult(IntPtr stmt, int maxRows, bool safeColumnNames, CancellationToken ct, out bool truncated)
    {
        Check(OdbcApi.SQLNumResultCols(stmt, out var count), OdbcApi.SQL_HANDLE_STMT, stmt, "SQLNumResultCols");
        var names = new string[count];
        var types = new short[count];
        var sizeBuffer = Marshal.AllocHGlobal(8);
        var nameBuffer = new char[512];
        try
        {
            for (int i = 0; i < count; i++)
            {
                Marshal.WriteInt64(sizeBuffer, 0);
                Array.Clear(nameBuffer);
                var rc = OdbcApi.SQLDescribeColW(stmt, (ushort)(i + 1), nameBuffer, (short)nameBuffer.Length, out var nameLength,
                    out types[i], sizeBuffer, out _, out _);
                Check(rc, OdbcApi.SQL_HANDLE_STMT, stmt, "SQLDescribeCol");
                var len = nameLength is >= 0 && nameLength < nameBuffer.Length ? nameLength : Array.IndexOf(nameBuffer, '\0');
                names[i] = new string(nameBuffer, 0, len < 0 ? nameBuffer.Length : len);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(sizeBuffer);
        }

        var rows = new List<string?[]>();
        var data = Marshal.AllocHGlobal(DataBufferBytes);
        var indicator = Marshal.AllocHGlobal(8);
        try
        {
            truncated = false;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var rc = OdbcApi.SQLFetch(stmt);
                if (rc == OdbcApi.SQL_NO_DATA) break;
                Check(rc, OdbcApi.SQL_HANDLE_STMT, stmt, "SQLFetch");
                if (rows.Count >= maxRows)
                {
                    truncated = true;
                    break;
                }

                var values = new string?[count];
                for (int i = 0; i < count; i++) values[i] = GetString(stmt, (ushort)(i + 1), data, indicator);
                rows.Add(values);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(data);
            Marshal.FreeHGlobal(indicator);
        }

        return BuildTable(names, types, rows, safeColumnNames);
    }

    /// <summary>Reads a column as text (SQL_C_WCHAR), in chunks, tolerating 32-bit length indicators.</summary>
    private string? GetString(IntPtr stmt, ushort column, IntPtr data, IntPtr indicator)
    {
        const int maxChars = DataBufferBytes / 2 - 1;
        System.Text.StringBuilder? sb = null;
        while (true)
        {
            OdbcApi.Zero(data, DataBufferBytes);
            Marshal.WriteInt64(indicator, 0);
            var rc = OdbcApi.SQLGetData(stmt, column, OdbcApi.SQL_C_WCHAR, data, (IntPtr)DataBufferBytes, indicator);
            if (rc == OdbcApi.SQL_NO_DATA) break;
            Check(rc, OdbcApi.SQL_HANDLE_STMT, stmt, "SQLGetData");

            var len = OdbcApi.ReadLength(indicator);
            if (len == OdbcApi.SQL_NULL_DATA) return sb?.ToString();

            int chars;
            if (rc == OdbcApi.SQL_SUCCESS_WITH_INFO && (len == OdbcApi.SQL_NO_TOTAL || len / 2 > maxChars))
                chars = maxChars;                                   // truncated: more data follows
            else if (len >= 0 && len / 2 <= maxChars)
                chars = (int)(len / 2);
            else
                chars = TerminatorIndex(data, maxChars);            // indicator unusable: trust the terminator

            sb ??= new System.Text.StringBuilder();
            sb.Append(Marshal.PtrToStringUni(data, chars));
            if (rc == OdbcApi.SQL_SUCCESS) break;
        }

        return sb?.ToString() ?? "";
    }

    private static int TerminatorIndex(IntPtr data, int maxChars)
    {
        for (int i = 0; i < maxChars; i++)
        {
            if (Marshal.ReadInt16(data, i * 2) == 0) return i;
        }

        return maxChars;
    }

    private static DataTable BuildTable(string[] names, short[] sqlTypes, List<string?[]> rows, bool safeColumnNames)
    {
        var table = new DataTable("Result") { Locale = CultureInfo.InvariantCulture };
        for (int i = 0; i < names.Length; i++)
        {
            var type = typeof(string);
            if (IsInteger(sqlTypes[i]) && rows.All(r => r[i] is null || long.TryParse(r[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out _)))
                type = typeof(long);
            else if (IsDecimal(sqlTypes[i]) && rows.All(r => r[i] is null || decimal.TryParse(r[i], NumberStyles.Float, CultureInfo.InvariantCulture, out _)))
                type = typeof(decimal);

            string columnName = safeColumnNames ? "c" + i : UniqueName(table, string.IsNullOrEmpty(names[i]) ? $"Column{i + 1}" : names[i]);
            var col = table.Columns.Add(columnName, type);
            col.Caption = string.IsNullOrEmpty(names[i]) ? $"(column {i + 1})" : names[i];
        }

        table.BeginLoadData();
        var values = new object[names.Length];
        foreach (var row in rows)
        {
            for (int i = 0; i < names.Length; i++)
            {
                var s = row[i];
                var t = table.Columns[i].DataType;
                values[i] = s is null ? DBNull.Value
                    : t == typeof(long) ? long.Parse(s, NumberStyles.Integer, CultureInfo.InvariantCulture)
                    : t == typeof(decimal) ? decimal.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture)
                    : s;
            }

            table.LoadDataRow(values, true);
        }

        table.EndLoadData();
        return table;
    }

    private static string UniqueName(DataTable table, string name)
    {
        var candidate = name;
        for (int n = 2; table.Columns.Contains(candidate); n++) candidate = $"{name}_{n}";
        return candidate;
    }

    private static bool IsInteger(short sqlType) => sqlType is 4 or 5 or -6 or -5;          // INTEGER, SMALLINT, TINYINT, BIGINT

    private static bool IsDecimal(short sqlType) => sqlType is 2 or 3 or 6 or 7 or 8;        // NUMERIC, DECIMAL, FLOAT, REAL, DOUBLE

    private void Check(short rc, short handleType, IntPtr handle, string what)
    {
        if (OdbcApi.Succeeded(rc)) return;
        var errors = handle == IntPtr.Zero ? new List<OdbcErrorInfo>() : ReadDiagnostics(handleType, handle);
        throw new NativeOdbcException(errors, $"{what} failed with SQLRETURN {rc}.");
    }

    private static List<OdbcErrorInfo> ReadDiagnostics(short handleType, IntPtr handle)
    {
        var list = new List<OdbcErrorInfo>();
        var state = new char[6];
        var message = new char[4096];
        for (short rec = 1; rec <= 50; rec++)
        {
            Array.Clear(state);
            Array.Clear(message);
            var rc = OdbcApi.SQLGetDiagRecW(handleType, handle, rec, state, out var native, message, (short)message.Length, out var textLength);
            if (!OdbcApi.Succeeded(rc)) break;
            var len = textLength is >= 0 && textLength < message.Length ? textLength : Math.Max(0, Array.IndexOf(message, '\0'));
            list.Add(new OdbcErrorInfo(new string(state, 0, 5), native, new string(message, 0, len), "odbc32 (native)"));
        }

        return list;
    }

    public void Dispose()
    {
        if (_dbc != IntPtr.Zero)
        {
            if (_connected) OdbcApi.SQLDisconnect(_dbc);
            OdbcApi.SQLFreeHandle(OdbcApi.SQL_HANDLE_DBC, _dbc);
            _dbc = IntPtr.Zero;
        }

        if (_env != IntPtr.Zero)
        {
            OdbcApi.SQLFreeHandle(OdbcApi.SQL_HANDLE_ENV, _env);
            _env = IntPtr.Zero;
        }

        _connected = false;
    }
}
