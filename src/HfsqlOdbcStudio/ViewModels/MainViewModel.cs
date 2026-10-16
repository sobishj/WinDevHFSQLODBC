using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HfsqlOdbcStudio.Helpers;
using HfsqlOdbcStudio.Models;
using HfsqlOdbcStudio.Services;
using HfsqlOdbcStudio.Views;
using Wpf.Ui.Controls;

namespace HfsqlOdbcStudio.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private readonly IConnectionService _connections;
    private readonly IProfileService _profiles;
    private readonly IDialogService _dialogs;
    private readonly INotificationService _notify;
    private readonly Func<ConnectionViewModel> _connectionVmFactory;
    private ConnectionProfile? _lastProfile;

    public MainViewModel(IConnectionService connections, IProfileService profiles, IDialogService dialogs, INotificationService notify,
        ExplorerViewModel explorer, QueryViewModel query,
        Func<ConnectionViewModel> connectionVmFactory)
    {
        _connections = connections;
        _profiles = profiles;
        _dialogs = dialogs;
        _notify = notify;
        _connectionVmFactory = connectionVmFactory;
        Explorer = explorer;
        Query = query;

        _connections.StateChanged += (_, _) => OnConnectionStateChanged();
    }

    public ExplorerViewModel Explorer { get; }

    public QueryViewModel Query { get; }

    [ObservableProperty]
    private bool _isConnected;

    [ObservableProperty]
    private string _connectionText = "Disconnected";

    [ObservableProperty]
    private string _connectionDetail = "Not connected";

    [ObservableProperty]
    private string? _connectionToolTip;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasQuickConnect))]
    private string? _quickConnectName;

    [ObservableProperty]
    private bool _isConnecting;

    public bool HasQuickConnect => !IsConnected && !string.IsNullOrEmpty(QuickConnectName);

    public async Task InitializeAsync()
    {
        await Query.InitializeAsync();
        var store = await _profiles.LoadAsync();
        _lastProfile = store.Profiles.FirstOrDefault(p => p.Name.Equals(store.LastProfileName, StringComparison.OrdinalIgnoreCase));
        QuickConnectName = _lastProfile?.Name;
        Query.Log(LogLevel.Info, _lastProfile is null
            ? "Welcome. Click Connect… to configure an HFSQL ODBC connection."
            : $"Last used profile: \"{_lastProfile.Name}\" ({_lastProfile.DisplayTarget}). Click Connect to open it.", null, null);
    }

    [RelayCommand]
    private async Task OpenConnectionAsync()
    {
        var vm = _connectionVmFactory();
        await vm.InitializeAsync();
        var window = new ConnectionWindow(vm);
        _dialogs.ShowModal(window);
        // On success the dialog already called IConnectionService.SetActive.
        var store = await _profiles.LoadAsync();
        _lastProfile = store.Profiles.FirstOrDefault(p => p.Name.Equals(store.LastProfileName, StringComparison.OrdinalIgnoreCase));
        QuickConnectName = _lastProfile?.Name;
    }

    [RelayCommand]
    private async Task QuickConnectAsync()
    {
        if (_lastProfile is null)
        {
            await OpenConnectionAsync();
            return;
        }

        IsConnecting = true;
        try
        {
            var info = await _connections.TestAsync(_lastProfile);
            _connections.SetActive(_lastProfile, info);
        }
        catch (Exception ex)
        {
            Query.Log(LogLevel.Error, $"Connection to \"{_lastProfile.Name}\" failed", null,
                OdbcErrorFormatter.Format(ex) + Environment.NewLine + "Connection string (password masked): " +
                ConnectionStringFactory.Build(_lastProfile, maskPassword: true));
            Query.SelectedTabIndex = QueryViewModel.MessagesTab;
            _notify.Error("Connection failed", OdbcErrorFormatter.Short(ex));
        }
        finally
        {
            IsConnecting = false;
        }
    }

    [RelayCommand]
    private void Disconnect()
    {
        _connections.Disconnect();
        _notify.Info("Disconnected", "The connection was closed.");
    }

    private void OnConnectionStateChanged()
    {
        IsConnected = _connections.IsConnected;
        OnPropertyChanged(nameof(HasQuickConnect));
        var profile = _connections.ActiveProfile;
        var info = _connections.ActiveInfo;

        if (profile is null || info is null)
        {
            ConnectionText = "Disconnected";
            ConnectionDetail = "Not connected";
            ConnectionToolTip = null;
            Explorer.Clear();
            Query.Log(LogLevel.Info, "Disconnected.", null, null);
            return;
        }

        var server = profile.Mode == ConnectionMode.Fields ? profile.Server : info.DataSource is { Length: > 0 } ds ? ds : profile.DisplayTarget;
        var database = info.Database is { Length: > 0 } db ? db : profile.Database;
        ConnectionText = profile.Mode == ConnectionMode.Fields && profile.Kind == HfsqlKind.Classic
            ? $"Connected to HFSQL Classic: {profile.ClassicDataDirectory}"
            : $"Connected to {server}/{database}";
        ConnectionDetail = $"{info.DriverName} {info.DriverVersion} · {info.DbmsName} {info.ServerVersion}".Replace("  ", " ").Trim();
        ConnectionToolTip = ConnectionViewModel.FormatInfo(info);
        Query.Log(LogLevel.Success, $"Connected to {server}/{database}", null, ConnectionViewModel.FormatInfo(info));
        _notify.Success("Connected", $"{server}/{database} · {info.DbmsName} {info.ServerVersion}".Trim());
        _ = Explorer.RefreshAsync(null);
    }
}
