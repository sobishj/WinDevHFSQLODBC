using System.Data.Odbc;
using System.Text;
using HfsqlOdbcStudio.Models;

namespace HfsqlOdbcStudio.Helpers;

/// <summary>
/// Formats ODBC errors exactly as returned by the driver: every OdbcError in the Errors collection
/// with its SQLSTATE, native error code, source and verbatim message. Messages are never reworded.
/// </summary>
public static class OdbcErrorFormatter
{
    public static OdbcException? FindOdbcException(Exception? ex)
    {
        while (ex is not null)
        {
            if (ex is OdbcException odbc) return odbc;
            if (ex is AggregateException agg && agg.InnerExceptions.Count > 0)
            {
                ex = agg.InnerExceptions[0];
                continue;
            }

            ex = ex.InnerException;
        }

        return null;
    }

    public static IReadOnlyList<OdbcErrorInfo> GetErrors(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is Services.NativeOdbcException native) return native.Errors;
        }

        var odbc = FindOdbcException(ex);
        if (odbc is null) return [];
        var list = new List<OdbcErrorInfo>(odbc.Errors.Count);
        foreach (OdbcError e in odbc.Errors)
            list.Add(new OdbcErrorInfo(e.SQLState ?? "", e.NativeError, e.Message ?? "", e.Source ?? ""));
        return list;
    }

    public static string Format(Exception ex)
    {
        var errors = GetErrors(ex);
        var sb = new StringBuilder();
        if (errors.Count > 0)
        {
            for (int i = 0; i < errors.Count; i++)
            {
                var e = errors[i];
                if (i > 0) sb.AppendLine();
                sb.Append("Error ").Append(i + 1).Append(" of ").Append(errors.Count)
                  .Append(": SQLSTATE=").Append(e.SqlState)
                  .Append("  NativeError=").Append(e.NativeError);
                if (!string.IsNullOrEmpty(e.Source)) sb.Append("  Source=").Append(e.Source);
                sb.AppendLine();
                sb.Append(e.Message);
            }

            return sb.ToString();
        }

        sb.Append(ex.GetType().FullName).Append(": ").Append(ex.Message);
        for (var inner = ex.InnerException; inner is not null; inner = inner.InnerException)
            sb.AppendLine().Append("  ---> ").Append(inner.GetType().FullName).Append(": ").Append(inner.Message);

        if (ex is OverflowException && ex.StackTrace?.Contains("System.Data.Odbc", StringComparison.Ordinal) == true)
        {
            sb.AppendLine().AppendLine()
              .Append("This is not a server error: System.Data.Odbc could not read a 64-bit length or row-count value returned by the ODBC driver. ")
              .Append("If this was not a SELECT, the statement may already have been executed on the server. ")
              .Append("Set \"ODBC access layer\" to Native (or Auto) in the connection profile.");
        }

        return sb.ToString();
    }

    /// <summary>One-line summary for toasts. The full text is always logged in the Messages tab.</summary>
    public static string Short(Exception ex)
    {
        var errors = GetErrors(ex);
        var text = errors.Count > 0 ? $"[{errors[0].SqlState}] {errors[0].Message}" : ex.Message;
        var line = text.Split('\n')[0].Trim();
        return line.Length > 220 ? line[..220] + "…" : line;
    }
}
