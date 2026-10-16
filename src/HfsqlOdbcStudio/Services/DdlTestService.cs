using System.Data;

using System.Diagnostics;
using System.Reflection;
using System.Text;
using HfsqlOdbcStudio.Helpers;
using HfsqlOdbcStudio.Models;

namespace HfsqlOdbcStudio.Services;

public interface IDdlTestService
{
    string TableName { get; }

    IReadOnlyList<DdlTestStep> CreateSteps();

    Task<bool> TestTableExistsAsync(CancellationToken ct);

    /// <summary>Runs every step in order on one connection. A failing step never stops the next ones.</summary>
    Task<DdlTestRun> RunAsync(IReadOnlyList<DdlTestStep> steps, bool dropExistingFirst, CancellationToken ct);

    string BuildReport(DdlTestRun run, bool markdown);
}

public sealed class DdlTestService(IConnectionService connections, ISchemaService schema) : IDdlTestService
{
    private const string Table = "ZZ_ODBC_TEST";
    private const string IndexName = "IDX_ZZ_ODBC_TEST_NAME";
    private const string TestName = "ODBC test";

    public string TableName => Table;

    public IReadOnlyList<DdlTestStep> CreateSteps() =>
    [
        new(1, "CREATE TABLE"),
        new(2, "Verify table exists (schema)"),
        new(3, "INSERT a row, then SELECT it back"),
        new(4, "ALTER TABLE … ADD column + verify"),
        new(5, "ALTER TABLE … DROP column + verify"),
        new(6, "CREATE INDEX"),
        new(7, "DROP TABLE + verify it is gone"),
    ];

    public Task<bool> TestTableExistsAsync(CancellationToken ct) => schema.TableExistsAsync(Table, ct);

    // Not using ConfigureAwait(false): step objects are bound to the UI and must be updated on its thread.
    public async Task<DdlTestRun> RunAsync(IReadOnlyList<DdlTestStep> steps, bool dropExistingFirst, CancellationToken ct)
    {
        var profile = connections.ActiveProfile ?? throw new InvalidOperationException("Not connected.");
        var run = new DdlTestRun
        {
            Started = DateTime.Now,
            Steps = steps,
            Info = connections.ActiveInfo,
            MaskedConnectionString = ConnectionStringFactory.Build(profile, maskPassword: true),
            ServerTarget = profile.DisplayTarget,
        };
        foreach (var s in steps) s.Reset();

        IOdbcSession conn;
        try
        {
            conn = await connections.OpenActiveAsync(ct);
        }
        catch (Exception ex)
        {
            run.AbortReason = "Could not open the connection: " + OdbcErrorFormatter.Format(ex);
            foreach (var s in steps)
            {
                s.Status = DdlStepStatus.Skipped;
                s.Detail = "Not run: the connection could not be opened.";
            }

            run.Finished = DateTime.Now;
            return run;
        }

        using (conn)
        {
            if (dropExistingFirst)
                run.PreCleanup.Add(await NonQueryAsync(conn, $"DROP TABLE {Table}", ct));

            await RunStepAsync(steps[0], ct, async s =>
            {
                var a = await NonQueryAsync(conn, $"CREATE TABLE {Table} (ID INTEGER, Name VARCHAR(50))", ct);
                s.AddAttempt(a);
                return a.Success;
            });

            await RunStepAsync(steps[1], ct, async s =>
            {
                var a = await SchemaTableCheckAsync(conn, expectExists: true, ct);
                s.AddAttempt(a);
                if (!a.Success)
                {
                    var driverPath = connections.ActiveInfo?.DriverPath;
                    var where = await Task.Run(() => LocalFileProbe.Describe(Table, driverPath, TimeSpan.FromMinutes(15)), CancellationToken.None);
                    s.AddAttempt(new DdlAttempt($"[local files] search for {Table}.fic on this PC", false,
                        where + Environment.NewLine + "Process current directory: " + Environment.CurrentDirectory));
                }

                return a.Success;
            });

            await RunStepAsync(steps[2], ct, async s =>
            {
                var insert = await NonQueryAsync(conn, $"INSERT INTO {Table} (ID, Name) VALUES (1, '{TestName}')", ct);
                s.AddAttempt(insert);
                var select = await SelectBackAsync(conn, ct);
                s.AddAttempt(select);
                return insert.Success && select.Success;
            });

            await RunStepAsync(steps[3], ct, async s =>
            {
                var altered = await TryVariantsAsync(s, conn, ct,
                    $"ALTER TABLE {Table} ADD Extra VARCHAR(20)",
                    $"ALTER TABLE {Table} ADD COLUMN Extra VARCHAR(20)");
                if (!altered) return false;
                var check = await SchemaColumnCheckAsync(conn, "Extra", expectExists: true, ct);
                s.AddAttempt(check);
                return check.Success;
            });

            await RunStepAsync(steps[4], ct, async s =>
            {
                var altered = await TryVariantsAsync(s, conn, ct,
                    $"ALTER TABLE {Table} DROP COLUMN Extra",
                    $"ALTER TABLE {Table} DROP Extra");
                if (!altered) return false;
                var check = await SchemaColumnCheckAsync(conn, "Extra", expectExists: false, ct);
                s.AddAttempt(check);
                return check.Success;
            });

            await RunStepAsync(steps[5], ct, async s =>
            {
                var a = await NonQueryAsync(conn, $"CREATE INDEX {IndexName} ON {Table} (Name)", ct);
                s.AddAttempt(a);
                if (!a.Success) return false;
                var (exists, note) = await Task.Run(() =>
                {
                    var e = schema.IndexExists(conn, Table, IndexName, "Name", out var n);
                    return (e, n);
                }, CancellationToken.None);
                var label = $"[schema] GetSchema(\"Indexes\") → {IndexName}";
                s.AddAttempt(exists switch
                {
                    true => new DdlAttempt(label, true, ("Index found in the driver's index metadata. " + note).Trim()),
                    false => new DdlAttempt(label, false, ("Index NOT found in the driver's index metadata. " + note).Trim()),
                    _ => new DdlAttempt(label, true, "Not verifiable (informational only). " + note),
                });
                return exists != false;
            });

            await RunStepAsync(steps[6], ct, async s =>
            {
                var a = await NonQueryAsync(conn, $"DROP TABLE {Table}", ct);
                s.AddAttempt(a);
                if (!a.Success) return false;
                var check = await SchemaTableCheckAsync(conn, expectExists: false, ct);
                s.AddAttempt(check);
                return check.Success;
            });
        }

        run.Finished = DateTime.Now;
        return run;
    }

    private static async Task RunStepAsync(DdlTestStep step, CancellationToken ct, Func<DdlTestStep, Task<bool>> body)
    {
        if (ct.IsCancellationRequested)
        {
            step.Status = DdlStepStatus.Skipped;
            step.Detail = "Skipped: the test was cancelled.";
            return;
        }

        step.Status = DdlStepStatus.Running;
        var sw = Stopwatch.StartNew();
        bool passed;
        try
        {
            passed = await body(step);
        }
        catch (Exception ex)
        {
            step.AddAttempt(new DdlAttempt("(internal)", false, OdbcErrorFormatter.Format(ex)));
            passed = false;
        }

        step.ElapsedMs = sw.Elapsed.TotalMilliseconds;
        step.Status = ct.IsCancellationRequested && !passed ? DdlStepStatus.Skipped : passed ? DdlStepStatus.Passed : DdlStepStatus.Failed;
    }

    private async Task<bool> TryVariantsAsync(DdlTestStep step, IOdbcSession conn, CancellationToken ct, params string[] variants)
    {
        foreach (var sql in variants)
        {
            var a = await NonQueryAsync(conn, sql, ct);
            step.AddAttempt(a);
            if (a.Success) return true;
            if (ct.IsCancellationRequested) return false;
        }

        return false;
    }

    private async Task<DdlAttempt> NonQueryAsync(IOdbcSession conn, string sql, CancellationToken ct)
    {
        var r = await Task.Run(() => conn.Execute(sql, returnsRows: false, maxRows: 0, ct), CancellationToken.None);
        return new DdlAttempt(sql, r.Success, r.Success
            ? $"OK. Rows affected: {r.RowsAffected}. ({r.Elapsed.TotalMilliseconds:0} ms)"
            : r.ErrorText);
    }

    private async Task<DdlAttempt> SelectBackAsync(IOdbcSession conn, CancellationToken ct)
    {
        var sql = $"SELECT ID, Name FROM {Table} WHERE ID = 1";
        var r = await Task.Run(() => conn.Execute(sql, returnsRows: true, maxRows: 10, ct), CancellationToken.None);
        if (!r.Success) return new DdlAttempt(sql, false, r.ErrorText);
        if (r.Data is null || r.Data.Rows.Count == 0)
            return new DdlAttempt(sql, false, "The statement succeeded but returned no rows; the inserted row was not read back.");

        DataRow row = r.Data.Rows[0];
        var name = Convert.ToString(row[1])?.Trim() ?? "";
        var values = $"ID={row[0]}, Name='{name}'";
        return name == TestName
            ? new DdlAttempt(sql, true, $"OK. Read back {r.RowCount} row(s): {values}")
            : new DdlAttempt(sql, false, $"Row read back but the value differs: {values} (expected Name='{TestName}')");
    }

    private async Task<DdlAttempt> SchemaTableCheckAsync(IOdbcSession conn, bool expectExists, CancellationToken ct)
    {
        var label = $"[schema] GetSchema(\"Tables\") → {Table}";
        try
        {
            var exists = await Task.Run(() => schema.TableExists(conn, Table), ct);
            return exists == expectExists
                ? new DdlAttempt(label, true, expectExists ? "Verified: the table exists on the server." : "Verified: the table no longer exists on the server.")
                : new DdlAttempt(label, false, expectExists ? "The table was NOT found in the schema." : "The table is STILL listed in the schema.");
        }
        catch (Exception ex)
        {
            return new DdlAttempt(label, false, OdbcErrorFormatter.Format(ex));
        }
    }

    private async Task<DdlAttempt> SchemaColumnCheckAsync(IOdbcSession conn, string column, bool expectExists, CancellationToken ct)
    {
        var label = $"[schema] GetSchema(\"Columns\") → {Table}.{column}";
        try
        {
            var columns = await Task.Run(() => schema.GetColumns(conn, Table), ct);
            var exists = columns.Any(c => c.Name.Equals(column, StringComparison.OrdinalIgnoreCase));
            var list = string.Join(", ", columns.Select(c => $"{c.Name} {c.TypeDisplay}"));
            return exists == expectExists
                ? new DdlAttempt(label, true, $"Verified. Columns now: {list}")
                : new DdlAttempt(label, false, (expectExists ? "Column NOT found." : "Column is STILL present.") + $" Columns now: {list}");
        }
        catch (Exception ex)
        {
            return new DdlAttempt(label, false, OdbcErrorFormatter.Format(ex));
        }
    }

    public string BuildReport(DdlTestRun run, bool markdown)
    {
        var info = run.Info;
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";
        var passed = run.Steps.Count(s => s.Status == DdlStepStatus.Passed);
        var failed = run.Steps.Count(s => s.Status == DdlStepStatus.Failed);
        var skipped = run.Steps.Count(s => s.Status is DdlStepStatus.Skipped or DdlStepStatus.Pending);

        var header = new List<(string Key, string Value)>
        {
            ("Date/time", $"{run.Started:yyyy-MM-dd HH:mm:ss} (finished {run.Finished:HH:mm:ss})"),
            ("Machine", Environment.MachineName),
            ("Windows user", $@"{Environment.UserDomainName}\{Environment.UserName}"),
            ("Application", $"HFSQL ODBC Studio {version} (64-bit)"),
            ("ODBC driver", info?.DriverName ?? ""),
            ("Driver file", info?.DriverFile ?? ""),
            ("Driver version", info?.DriverVersion ?? ""),
            ("DBMS", $"{info?.DbmsName} {info?.DbmsVersion}".Trim()),
            ("Server version", info?.ServerVersion ?? ""),
            ("ODBC access layer", info is null ? "" : $"{info.AccessLayerDisplay}. {info.AccessLayerNote}".Trim()),
            ("Database", info?.Database is { Length: > 0 } db ? db : run.ServerTarget),
            ("Target", run.ServerTarget),
            ("Connection string", run.MaskedConnectionString),
            ("Data file folder", info?.DataFolder ?? ""),
        };

        var sb = new StringBuilder();
        if (markdown)
        {
            sb.AppendLine("# HFSQL ODBC DDL Capability Report").AppendLine();
            sb.AppendLine("| Item | Value |").AppendLine("|---|---|");
            foreach (var (k, v) in header) sb.AppendLine($"| {k} | {EscapeMd(v)} |");
            sb.AppendLine().AppendLine($"**Result: {passed} passed, {failed} failed, {skipped} not run** (of {run.Steps.Count} steps)").AppendLine();
            if (run.AbortReason is not null) sb.AppendLine("> **Aborted:** " + run.AbortReason.Replace("\n", "\n> ")).AppendLine();
            if (run.PreCleanup.Count > 0)
            {
                sb.AppendLine("## Pre-cleanup").AppendLine();
                foreach (var a in run.PreCleanup) AppendAttemptMd(sb, a);
            }

            sb.AppendLine("## Steps").AppendLine();
            foreach (var s in run.Steps)
            {
                sb.AppendLine($"### {s.Number}. {s.Title} — {s.Status.ToString().ToUpperInvariant()} ({s.ElapsedMs:0} ms)").AppendLine();
                if (s.Attempts.Count == 0 && s.Detail.Length > 0) sb.AppendLine(s.Detail).AppendLine();
                foreach (var a in s.Attempts) AppendAttemptMd(sb, a);
            }
        }
        else
        {
            sb.AppendLine("HFSQL ODBC DDL CAPABILITY REPORT").AppendLine(new string('=', 60));
            foreach (var (k, v) in header) sb.AppendLine($"{k,-18}: {v}");
            sb.AppendLine().AppendLine($"RESULT: {passed} passed, {failed} failed, {skipped} not run (of {run.Steps.Count} steps)").AppendLine();
            if (run.AbortReason is not null) sb.AppendLine("ABORTED: " + run.AbortReason).AppendLine();
            if (run.PreCleanup.Count > 0)
            {
                sb.AppendLine("PRE-CLEANUP").AppendLine(new string('-', 60));
                foreach (var a in run.PreCleanup) AppendAttemptTxt(sb, a);
            }

            foreach (var s in run.Steps)
            {
                sb.AppendLine($"STEP {s.Number}: {s.Title}  [{s.Status.ToString().ToUpperInvariant()}]  ({s.ElapsedMs:0} ms)").AppendLine(new string('-', 60));
                if (s.Attempts.Count == 0 && s.Detail.Length > 0) sb.AppendLine(s.Detail).AppendLine();
                foreach (var a in s.Attempts) AppendAttemptTxt(sb, a);
            }
        }

        return sb.ToString();
    }

    private static void AppendAttemptMd(StringBuilder sb, DdlAttempt a)
    {
        sb.AppendLine("```sql").AppendLine(a.Sql).AppendLine("```");
        sb.AppendLine(a.Success ? "Result: **OK**" : "Result: **FAILED**");
        sb.AppendLine("```text").AppendLine(a.Result).AppendLine("```").AppendLine();
    }

    private static void AppendAttemptTxt(StringBuilder sb, DdlAttempt a)
    {
        sb.AppendLine("  SQL   : " + a.Sql);
        sb.AppendLine("  Result: " + (a.Success ? "OK" : "FAILED"));
        sb.AppendLine("          " + a.Result.Replace("\n", "\n          ")).AppendLine();
    }

    private static string EscapeMd(string v) => v.Replace("|", "\\|").Replace("\r", "").Replace("\n", "<br>");
}
