using System.Collections.ObjectModel;
using System.Data;
using System.Diagnostics;
using System.Text;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HfsqlOdbcStudio.Helpers;
using HfsqlOdbcStudio.Models;
using HfsqlOdbcStudio.Services;
using StatementType = HfsqlOdbcStudio.Models.StatementType;

namespace HfsqlOdbcStudio.ViewModels;

/// <summary>Abstraction over the AvalonEdit control so the view model stays UI-agnostic.</summary>
public interface ISqlEditor
{
    string Text { get; set; }

    string SelectedText { get; }

    void Focus();
}

public sealed partial class QueryViewModel : ObservableObject
{
    public const int ResultsTab = 0;
    public const int MessagesTab = 1;
    public const int HistoryTab = 2;
    private const int MaxMessages = 2000;

    private readonly IQueryService _queries;
    private readonly ISchemaService _schema;
    private readonly IConnectionService _connections;
    private readonly ExplorerViewModel _explorer;
    private readonly IDialogService _dialogs;
    private readonly INotificationService _notify;
    private readonly IHistoryService _historyService;
    private readonly ISettingsService _settings;
    private string? _lastInsertedTemplate;

    public QueryViewModel(IQueryService queries, ISchemaService schema, IConnectionService connections, ExplorerViewModel explorer,
        IDialogService dialogs, INotificationService notify, IHistoryService historyService, ISettingsService settings)
    {
        _queries = queries;
        _schema = schema;
        _connections = connections;
        _explorer = explorer;
        _dialogs = dialogs;
        _notify = notify;
        _historyService = historyService;
        _settings = settings;
        _maxRows = settings.Current.MaxRows;
        _explorer.SqlRequested += async (_, request) => await HandleSqlRequestAsync(request);
    }

    public ISqlEditor? Editor { get; set; }

    public IReadOnlyList<StatementTypeOption> StatementTypes => StatementTypeOption.All;

    public ObservableCollection<LogEntry> Messages { get; } = [];

    public ObservableCollection<HistoryEntry> History { get; } = [];

    [ObservableProperty]
    private StatementTypeOption? _selectedStatementType;

    [ObservableProperty]
    private int _maxRows;

    [ObservableProperty]
    private DataView? _results;

    [ObservableProperty]
    private bool _hasResults;

    [ObservableProperty]
    private string _resultSummary = "";

    [ObservableProperty]
    private string _executingText = "";

    [ObservableProperty]
    private int _selectedTabIndex;

    [ObservableProperty]
    private string _lastExecutionText = "—";

    [ObservableProperty]
    private string _rowCountText = "—";

    [ObservableProperty]
    private HistoryEntry? _selectedHistory;

    public async Task InitializeAsync()
    {
        foreach (var h in await _historyService.LoadAsync()) History.Add(h);
    }

    partial void OnMaxRowsChanged(int value)
    {
        if (value < 0)
        {
            MaxRows = 0;
            return;
        }

        _settings.Current.MaxRows = value;
        _settings.Save();
    }

    partial void OnSelectedStatementTypeChanged(StatementTypeOption? value)
    {
        if (value is not null) _ = InsertTemplateAsync();
    }

    [RelayCommand]
    private async Task InsertTemplateAsync()
    {
        var option = SelectedStatementType;
        if (option is null || option.Type == StatementType.Custom) return;

        var table = _explorer.SelectedTable;
        IReadOnlyList<DbColumnInfo>? columns = null;
        if (table is not null) columns = await table.EnsureColumnsAsync();
        var sql = SqlTemplates.Build(option.Type, table?.Name, columns);
        if (sql is not null) await SetEditorTextAsync(sql, askIfDirty: false);
    }

    /// <summary>Replaces the editor content, asking first if it holds text the user typed.</summary>
    public async Task<bool> SetEditorTextAsync(string sql, bool askIfDirty)
    {
        if (Editor is null) return false;
        var current = Editor.Text;
        if (askIfDirty && !string.IsNullOrWhiteSpace(current) && current != _lastInsertedTemplate && current != sql)
        {
            var replace = await _dialogs.ConfirmAsync(
                "Replace editor content?",
                "The editor already contains SQL. Replace it with the new statement?",
                "Replace",
                "Keep my SQL");
            if (!replace) return false;
        }

        Editor.Text = sql;
        _lastInsertedTemplate = sql;
        Editor.Focus();
        return true;
    }

    private async Task HandleSqlRequestAsync(SqlRequest request)
    {
        try
        {
            if (!await SetEditorTextAsync(request.Sql, askIfDirty: true)) return;
            if (request.Execute && ExecuteCommand.CanExecute(null)) await ExecuteCommand.ExecuteAsync(null);
        }
        catch (Exception ex)
        {
            FileLog.Error("Handling explorer request failed", ex);
            _notify.Error("Error", ex.Message);
        }
    }

    [RelayCommand(IncludeCancelCommand = true)]
    private async Task ExecuteAsync(CancellationToken ct)
    {
        if (!_connections.IsConnected)
        {
            _notify.Warning("Not connected", "Open a connection first (Connect… in the top bar).");
            return;
        }

        if (Editor is null) return;
        var text = Editor.SelectedText;
        if (string.IsNullOrWhiteSpace(text)) text = Editor.Text;

        var statements = SqlLexer.SplitStatements(text);
        if (statements.Count == 0)
        {
            _notify.Info("Nothing to execute", "Type a SQL statement in the editor first.");
            return;
        }

        var analyzed = statements.Select(s => (Sql: s, Info: SqlAnalyzer.Analyze(s))).ToList();
        var dangerous = analyzed.Where(a => a.Info.IsDangerous).ToList();
        if (dangerous.Count > 0)
        {
            var details = string.Join(Environment.NewLine + Environment.NewLine,
                dangerous.Select(d => Truncate(d.Sql, 400) + Environment.NewLine + "→ " + d.Info.DangerReason));
            var confirmed = await _dialogs.ConfirmAsync(
                "Confirm destructive statement",
                "You are about to execute:" + Environment.NewLine + Environment.NewLine + details + Environment.NewLine + Environment.NewLine +
                "This cannot be undone. Continue?",
                "Execute",
                "Cancel",
                danger: true);
            if (!confirmed)
            {
                Log(LogLevel.Info, "Execution cancelled (confirmation declined).", dangerous[0].Sql, null);
                return;
            }
        }

        var total = Stopwatch.StartNew();
        int succeeded = 0;
        bool showedResults = false;
        try
        {
            for (int i = 0; i < analyzed.Count; i++)
            {
                var (sql, info) = analyzed[i];
                ExecutingText = analyzed.Count > 1 ? $"Executing statement {i + 1} of {analyzed.Count}…" : "Executing…";

                var r = await _queries.ExecuteAsync(sql, info.ReturnsRows, MaxRows, ct);
                AddHistory(sql, r);
                var ms = $"{r.Elapsed.TotalMilliseconds:N0} ms";

                if (r.Cancelled)
                {
                    Log(LogLevel.Warning, $"Cancelled after {ms}", sql, r.ErrorText);
                    _notify.Warning("Cancelled", "The statement was cancelled.");
                    SelectedTabIndex = MessagesTab;
                    break;
                }

                if (!r.Success)
                {
                    Log(LogLevel.Error, $"Error after {ms}", sql, r.ErrorText);
                    _notify.Error("Statement failed", r.Error is null ? r.ErrorText : OdbcErrorFormatter.Short(r.Error));
                    SelectedTabIndex = MessagesTab;
                    break;
                }

                succeeded++;
                if (r.IsResultSet && r.Data is not null)
                {
                    Results = r.Data.DefaultView;
                    HasResults = true;
                    var limited = r.Truncated ? $" (limited to the first {MaxRows:N0} rows — see Max rows)" : "";
                    ResultSummary = $"{r.RowCount:N0} row{(r.RowCount == 1 ? "" : "s")}{limited} · {r.Data.Columns.Count} column{(r.Data.Columns.Count == 1 ? "" : "s")} · {ms}";
                    RowCountText = $"{r.RowCount:N0}{(r.Truncated ? "+" : "")} rows";
                    Log(LogLevel.Success, $"{r.RowCount:N0} row(s) returned{limited} · {ms}", sql, null);
                    showedResults = true;
                }
                else
                {
                    var affected = r.RowsAffected >= 0 ? $"{r.RowsAffected:N0} row(s) affected" : "Statement executed (the driver reported no row count)";
                    RowCountText = r.RowsAffected >= 0 ? $"{r.RowsAffected:N0} affected" : "—";
                    Log(LogLevel.Success, $"{affected} · {ms}", sql, null);
                }

                if (info.IsDdl) await AfterDdlAsync(info);
            }
        }
        finally
        {
            ExecutingText = "";
            LastExecutionText = $"{total.Elapsed.TotalMilliseconds:N0} ms";
        }

        if (succeeded == analyzed.Count)
        {
            if (showedResults) SelectedTabIndex = ResultsTab;
            else SelectedTabIndex = MessagesTab;
            if (!showedResults || analyzed.Count > 1)
                _notify.Success("Executed", analyzed.Count == 1 ? $"Statement executed in {LastExecutionText}." : $"{analyzed.Count} statements executed in {LastExecutionText}.");
        }
    }

    private async Task AfterDdlAsync(SqlInfo info)
    {
        var verification = await _schema.VerifyAsync(info);
        string? highlight = info.Kind is SqlKind.CreateTable or SqlKind.AlterAddColumn or SqlKind.AlterDropColumn or SqlKind.CreateIndex
            ? info.Table
            : null;
        await _explorer.RefreshAsync(highlight);

        switch (verification.Verified)
        {
            case true:
                Log(LogLevel.Success, verification.Message, null, null);
                _notify.Success("Verified on server", verification.Message);
                break;
            case false:
                Log(LogLevel.Warning, verification.Message, null, null);
                _notify.Warning("Verification failed", verification.Message);
                if (info.Kind == SqlKind.CreateTable && info.Table is { } created)
                {
                    var driverPath = _connections.ActiveInfo?.DriverPath;
                    var where = await Task.Run(() => LocalFileProbe.Describe(created, driverPath, TimeSpan.FromMinutes(15)));
                    Log(LogLevel.Warning, $"Looked for a local {created}.fic created by the driver", null,
                        where + Environment.NewLine + "Process current directory: " + Environment.CurrentDirectory);
                }

                break;
            default:
                if (!string.IsNullOrWhiteSpace(verification.Message)) Log(LogLevel.Info, verification.Message, null, null);
                break;
        }
    }

    public void Log(LogLevel level, string title, string? sql, string? detail)
    {
        Messages.Insert(0, new LogEntry(DateTime.Now, level, title, sql, detail));
        while (Messages.Count > MaxMessages) Messages.RemoveAt(Messages.Count - 1);
    }

    private void AddHistory(string sql, QueryResult r)
    {
        var entry = new HistoryEntry
        {
            Timestamp = DateTime.Now,
            Sql = sql,
            Success = r.Success,
            ElapsedMs = r.Elapsed.TotalMilliseconds,
            Summary = r.Cancelled ? "Cancelled"
                : !r.Success ? "Error: " + (r.Errors.Count > 0 ? r.Errors[0].SqlState : "failed")
                : r.IsResultSet ? $"{r.RowCount:N0} row(s)"
                : r.RowsAffected >= 0 ? $"{r.RowsAffected:N0} affected" : "OK",
        };
        History.Insert(0, entry);
        while (History.Count > HistoryService.MaxEntries) History.RemoveAt(History.Count - 1);
        _ = _historyService.SaveAsync(History.ToList());
    }

    [RelayCommand]
    private async Task LoadHistoryAsync(HistoryEntry? entry)
    {
        entry ??= SelectedHistory;
        if (entry is null) return;
        await SetEditorTextAsync(entry.Sql, askIfDirty: true);
    }

    [RelayCommand]
    private async Task ClearHistoryAsync()
    {
        if (History.Count == 0) return;
        if (!await _dialogs.ConfirmAsync("Clear history", "Remove all statements from the history?", "Clear", "Cancel")) return;
        History.Clear();
        await _historyService.SaveAsync([]);
    }

    [RelayCommand]
    private void ClearMessages() => Messages.Clear();

    [RelayCommand]
    private void CopyMessages()
    {
        var text = string.Join(Environment.NewLine + Environment.NewLine, Messages.Reverse().Select(m => m.ToString()));
        CopyToClipboard(text, $"{Messages.Count} message(s) copied.");
    }

    [RelayCommand]
    private void CopyResults()
    {
        if (Results?.Table is not { } table) return;
        var sb = new StringBuilder();
        sb.AppendLine(string.Join('\t', table.Columns.Cast<DataColumn>().Select(c => c.Caption)));
        foreach (DataRowView row in Results)
            sb.AppendLine(string.Join('\t', row.Row.ItemArray.Select(v => v is DBNull ? "" : Convert.ToString(v)?.Replace('\t', ' ').Replace("\r", " ").Replace("\n", " "))));
        CopyToClipboard(sb.ToString(), $"{Results.Count:N0} row(s) copied as tab-separated text.");
    }

    private void CopyToClipboard(string text, string message)
    {
        try
        {
            Clipboard.SetText(text);
            _notify.Info("Copied", message);
        }
        catch (Exception ex)
        {
            _notify.Error("Clipboard", ex.Message);
        }
    }

    public void ClearResults()
    {
        Results = null;
        HasResults = false;
        ResultSummary = "";
        RowCountText = "—";
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
