using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HfsqlOdbcStudio.Helpers;
using HfsqlOdbcStudio.Models;
using HfsqlOdbcStudio.Services;

namespace HfsqlOdbcStudio.ViewModels;

public sealed partial class DdlTestViewModel : ObservableObject
{
    private const string DefaultSummary =
        "Runs CREATE TABLE, INSERT/SELECT, ALTER TABLE ADD/DROP, CREATE INDEX and DROP TABLE against a scratch table " +
        "and reports exactly what the HFSQL ODBC driver passes through to the server.";

    private readonly IDdlTestService _service;
    private readonly IConnectionService _connections;
    private readonly IDialogService _dialogs;
    private readonly INotificationService _notify;
    private readonly ExplorerViewModel _explorer;

    public DdlTestViewModel(IDdlTestService service, IConnectionService connections, IDialogService dialogs,
        INotificationService notify, ExplorerViewModel explorer)
    {
        _service = service;
        _connections = connections;
        _dialogs = dialogs;
        _notify = notify;
        _explorer = explorer;
        foreach (var s in service.CreateSteps()) Steps.Add(s);
    }

    public event EventHandler? Started;

    public ObservableCollection<DdlTestStep> Steps { get; } = [];

    public string TableName => _service.TableName;

    [ObservableProperty]
    private string _summary = DefaultSummary;

    [ObservableProperty]
    private bool? _overallPassed;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExportReportCommand), nameof(CopyReportCommand))]
    private DdlTestRun? _lastRun;

    private bool CanExport => LastRun is not null;

    [RelayCommand(IncludeCancelCommand = true)]
    private async Task RunAsync(CancellationToken ct)
    {
        if (!_connections.IsConnected)
        {
            _notify.Warning("Not connected", "Connect to a database before running the DDL capability test.");
            return;
        }

        Started?.Invoke(this, EventArgs.Empty);
        foreach (var s in Steps) s.Reset();
        OverallPassed = null;
        Summary = $"Checking whether {TableName} already exists…";

        bool dropFirst = false;
        try
        {
            if (await _service.TestTableExistsAsync(ct))
            {
                dropFirst = await _dialogs.ConfirmAsync(
                    "Test table already exists",
                    $"The table {TableName} already exists on the server (probably left over from an earlier run).{Environment.NewLine}{Environment.NewLine}" +
                    $"Drop it first and continue with the test?",
                    "Drop and continue",
                    "Cancel test",
                    danger: true);
                if (!dropFirst)
                {
                    Summary = $"Test cancelled: {TableName} already exists and was not dropped.";
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            Summary = "Test cancelled.";
            return;
        }
        catch (Exception ex)
        {
            // The existence check itself is informational; the test continues and reports real failures.
            FileLog.Error("DDL test pre-check failed", ex);
        }

        Summary = "Running…";
        var run = await _service.RunAsync(Steps, dropFirst, ct);
        LastRun = run;

        var passed = Steps.Count(s => s.Status == DdlStepStatus.Passed);
        var failed = Steps.Count(s => s.Status == DdlStepStatus.Failed);
        var notRun = Steps.Count - passed - failed;
        OverallPassed = failed == 0 && notRun == 0;
        Summary = run.AbortReason ?? $"{passed} of {Steps.Count} steps passed" +
            (failed > 0 ? $", {failed} failed" : "") + (notRun > 0 ? $", {notRun} not run" : "") +
            $" · {(run.Finished - run.Started).TotalSeconds:0.0} s · {run.Finished:HH:mm:ss}";

        if (OverallPassed == true) _notify.Success("DDL test passed", Summary);
        else _notify.Warning("DDL test finished with failures", Summary);

        await _explorer.RefreshAsync(null);
    }

    [RelayCommand(CanExecute = nameof(CanExport))]
    private async Task ExportReportAsync()
    {
        if (LastRun is null) return;
        var path = _dialogs.SaveFile(
            "Export DDL test report",
            "Markdown (*.md)|*.md|Text file (*.txt)|*.txt",
            $"HFSQL-DDL-Test-{LastRun.Started:yyyyMMdd-HHmmss}.md");
        if (path is null) return;

        try
        {
            var markdown = !Path.GetExtension(path).Equals(".txt", StringComparison.OrdinalIgnoreCase);
            await File.WriteAllTextAsync(path, _service.BuildReport(LastRun, markdown));
            _notify.Success("Report exported", path);
        }
        catch (Exception ex)
        {
            FileLog.Error("Exporting report failed", ex);
            _notify.Error("Export failed", ex.Message);
        }
    }

    [RelayCommand(CanExecute = nameof(CanExport))]
    private void CopyReport()
    {
        if (LastRun is null) return;
        try
        {
            Clipboard.SetText(_service.BuildReport(LastRun, markdown: true));
            _notify.Info("Copied", "Report copied as Markdown.");
        }
        catch (Exception ex)
        {
            _notify.Error("Clipboard", ex.Message);
        }
    }
}
