using System.Diagnostics;
using HfsqlOdbcStudio.Helpers;
using HfsqlOdbcStudio.Models;

namespace HfsqlOdbcStudio.Services;

public interface IQueryService
{
    /// <summary>Opens a session for the active profile and executes one statement on a worker thread.</summary>
    Task<QueryResult> ExecuteAsync(string sql, bool returnsRows, int maxRows, CancellationToken ct);
}

/// <summary>
/// Sends SQL text to the driver unchanged, through the session's ODBC layer.
/// Errors are captured with every ODBC diagnostic record, never reworded.
/// </summary>
public sealed class QueryService(IConnectionService connections) : IQueryService
{
    public async Task<QueryResult> ExecuteAsync(string sql, bool returnsRows, int maxRows, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        IOdbcSession session;
        try
        {
            session = await connections.OpenActiveAsync(ct);
        }
        catch (Exception ex)
        {
            return Failed(new QueryResult { Sql = sql }, ex, ct, sw.Elapsed);
        }

        try
        {
            return await Task.Run(() => session.Execute(sql, returnsRows, maxRows, ct), CancellationToken.None);
        }
        finally
        {
            session.Dispose();
        }
    }

    internal static QueryResult Failed(QueryResult result, Exception ex, CancellationToken ct, TimeSpan elapsed)
    {
        result.Success = false;
        result.Error = ex;
        result.Elapsed = elapsed;
        result.Errors = OdbcErrorFormatter.GetErrors(ex);
        if (ct.IsCancellationRequested)
        {
            result.Cancelled = true;
            result.ErrorText = ex is OperationCanceledException
                ? "Cancelled by user."
                : "Cancelled by user." + Environment.NewLine + OdbcErrorFormatter.Format(ex);
        }
        else
        {
            result.ErrorText = OdbcErrorFormatter.Format(ex);
        }

        return result;
    }
}
