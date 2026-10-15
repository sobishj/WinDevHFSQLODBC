using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HfsqlOdbcStudio.Helpers;
using HfsqlOdbcStudio.Models;
using HfsqlOdbcStudio.Services;

namespace HfsqlOdbcStudio.ViewModels;

public sealed record SqlRequest(string Sql, bool Execute);

public sealed record MessageNode(string Text);

public sealed partial class ExplorerViewModel : ObservableObject
{
    private readonly ISchemaService _schema;
    private readonly IConnectionService _connections;
    private readonly INotificationService _notify;
    private readonly ISettingsService _settings;
    private CancellationTokenSource? _refreshCts;
    private string? _loadError;

    public ExplorerViewModel(ISchemaService schema, IConnectionService connections, INotificationService notify, ISettingsService settings)
    {
        _schema = schema;
        _connections = connections;
        _notify = notify;
        _settings = settings;
        _showSystemTables = settings.Current.ShowSystemTables;
        TablesView = CollectionViewSource.GetDefaultView(Tables);
        TablesView.Filter = o => o is TableNodeViewModel t &&
                                 (string.IsNullOrWhiteSpace(SearchText) || t.Name.Contains(SearchText.Trim(), StringComparison.OrdinalIgnoreCase));
        UpdateEmptyState();
    }

    public event EventHandler<SqlRequest>? SqlRequested;

    public event EventHandler<TableNodeViewModel>? HighlightRequested;

    public ObservableCollection<TableNodeViewModel> Tables { get; } = [];

    public ICollectionView TablesView { get; }

    [ObservableProperty]
    private string _searchText = "";

    [ObservableProperty]
    private bool _showSystemTables;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasEmptyMessage))]
    private string? _emptyMessage;

    [ObservableProperty]
    private string _summary = "";

    [ObservableProperty]
    private TableNodeViewModel? _selectedTable;

    public bool HasEmptyMessage => !string.IsNullOrEmpty(EmptyMessage);

    partial void OnSearchTextChanged(string value)
    {
        TablesView.Refresh();
        UpdateEmptyState();
    }

    partial void OnShowSystemTablesChanged(bool value)
    {
        _settings.Current.ShowSystemTables = value;
        _settings.Save();
        _ = RefreshAsync();
    }

    public void OnSelectedNodeChanged(object? node) =>
        SelectedTable = node switch
        {
            TableNodeViewModel t => t,
            ColumnNodeViewModel c => c.Parent,
            _ => null,
        };

    [RelayCommand]
    private Task RefreshAsync() => RefreshAsync(null);

    [RelayCommand]
    private void ClearSearch() => SearchText = "";

    public async Task RefreshAsync(string? highlight)
    {
        _refreshCts?.Cancel();
        if (!_connections.IsConnected)
        {
            Clear();
            return;
        }

        var cts = new CancellationTokenSource();
        _refreshCts = cts;
        IsLoading = true;
        _loadError = null;
        UpdateEmptyState();
        try
        {
            var selectedName = SelectedTable?.Name;
            var expanded = Tables.Where(t => t.IsExpanded).Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var tables = await _schema.GetTablesAsync(ShowSystemTables, cts.Token);
            if (cts.IsCancellationRequested) return;

            Tables.Clear();
            foreach (var t in tables) Tables.Add(new TableNodeViewModel(this, t));
            foreach (var node in Tables.Where(n => expanded.Contains(n.Name))) node.IsExpanded = true;

            var target = highlight ?? selectedName;
            var match = target is null ? null : Tables.FirstOrDefault(n => n.Name.Equals(target, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                if (highlight is not null)
                {
                    if (!string.IsNullOrEmpty(SearchText) && !match.Name.Contains(SearchText, StringComparison.OrdinalIgnoreCase)) SearchText = "";
                    match.IsHighlighted = true;
                }

                match.IsSelected = true;
                SelectedTable = match;
                if (highlight is not null) HighlightRequested?.Invoke(this, match);
            }

            var userTables = Tables.Count(t => !t.IsSystem);
            Summary = $"{userTables} table{(userTables == 1 ? "" : "s")}" + (ShowSystemTables ? $" · {Tables.Count - userTables} system" : "");
        }
        catch (OperationCanceledException)
        {
            // A newer refresh replaced this one.
        }
        catch (Exception ex)
        {
            _loadError = "Could not load tables:" + Environment.NewLine + OdbcErrorFormatter.Format(ex);
            Summary = "";
            _notify.Error("Explorer", OdbcErrorFormatter.Short(ex));
            FileLog.Error("GetSchema(Tables) failed", ex);
        }
        finally
        {
            if (ReferenceEquals(_refreshCts, cts)) IsLoading = false;
            UpdateEmptyState();
        }
    }

    public void Clear()
    {
        Tables.Clear();
        SelectedTable = null;
        Summary = "";
        _loadError = null;
        IsLoading = false;
        UpdateEmptyState();
    }

    internal Task<IReadOnlyList<DbColumnInfo>> LoadColumnsAsync(string table) => _schema.GetColumnsAsync(table);

    internal void RequestSql(string sql, bool execute) => SqlRequested?.Invoke(this, new SqlRequest(sql, execute));

    internal async Task GenerateTemplateAsync(TableNodeViewModel node, StatementType type)
    {
        var columns = await node.EnsureColumnsAsync();
        var sql = SqlTemplates.Build(type, node.Name, columns);
        if (sql is not null) RequestSql(sql, execute: false);
    }

    internal void CopyName(TableNodeViewModel node)
    {
        try
        {
            Clipboard.SetText(node.Name);
            _notify.Info("Copied", node.Name);
        }
        catch (Exception ex)
        {
            _notify.Error("Clipboard", ex.Message);
        }
    }

    private void UpdateEmptyState()
    {
        if (!_connections.IsConnected) EmptyMessage = "Connect to a database to see tables";
        else if (IsLoading) EmptyMessage = null;
        else if (_loadError is not null) EmptyMessage = _loadError;
        else if (Tables.Count == 0) EmptyMessage = "No tables found in this database";
        else if (!TablesView.Cast<object>().Any()) EmptyMessage = $"No tables match \"{SearchText.Trim()}\"";
        else EmptyMessage = null;
    }
}

public sealed partial class TableNodeViewModel : ObservableObject
{
    private static readonly MessageNode LoadingNode = new("Loading columns…");
    private readonly ExplorerViewModel _owner;
    private Task<IReadOnlyList<DbColumnInfo>>? _loadTask;

    public TableNodeViewModel(ExplorerViewModel owner, DbTableInfo info)
    {
        _owner = owner;
        Info = info;
        Children.Add(LoadingNode);
    }

    public DbTableInfo Info { get; }

    public string Name => Info.Name;

    public bool IsSystem => Info.IsSystem;

    public bool IsView => Info.Type.Contains("VIEW", StringComparison.OrdinalIgnoreCase);

    public string TypeBadge => IsSystem ? "system" : IsView ? "view" : "";

    public bool HasTypeBadge => TypeBadge.Length > 0;

    public ObservableCollection<object> Children { get; } = [];

    [ObservableProperty]
    private bool _isExpanded;

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private bool _isHighlighted;

    partial void OnIsExpandedChanged(bool value)
    {
        if (value) _ = EnsureColumnsAsync();
    }

    public Task<IReadOnlyList<DbColumnInfo>> EnsureColumnsAsync() => _loadTask ??= LoadAsync();

    private async Task<IReadOnlyList<DbColumnInfo>> LoadAsync()
    {
        try
        {
            var columns = await _owner.LoadColumnsAsync(Name);
            Children.Clear();
            foreach (var c in columns) Children.Add(new ColumnNodeViewModel(this, c));
            if (columns.Count == 0) Children.Add(new MessageNode("No columns reported"));
            return columns;
        }
        catch (Exception ex)
        {
            Children.Clear();
            Children.Add(new MessageNode("Error: " + OdbcErrorFormatter.Short(ex)));
            FileLog.Error($"GetSchema(Columns) failed for {Name}", ex);
            _loadTask = null;
            return [];
        }
    }

    [RelayCommand]
    private void SelectTop100() => _owner.RequestSql(SqlTemplates.SelectTop(Name), execute: true);

    [RelayCommand]
    private Task GenerateCreateAsync() => _owner.GenerateTemplateAsync(this, StatementType.CreateTable);

    [RelayCommand]
    private Task GenerateAlterAsync() => _owner.GenerateTemplateAsync(this, StatementType.AlterAddColumn);

    [RelayCommand]
    private Task GenerateDropAsync() => _owner.GenerateTemplateAsync(this, StatementType.DropTable);

    [RelayCommand]
    private void CopyName() => _owner.CopyName(this);
}

public sealed class ColumnNodeViewModel(TableNodeViewModel parent, DbColumnInfo info)
{
    public TableNodeViewModel Parent { get; } = parent;

    public DbColumnInfo Info { get; } = info;

    public string Name => Info.Name;

    public string TypeDisplay => Info.TypeDisplay;

    public string NullableDisplay => Info.NullableDisplay;

    public string ToolTip => $"{Info.Name}  {Info.TypeDisplay}  {Info.NullableDisplay}".Trim();
}
