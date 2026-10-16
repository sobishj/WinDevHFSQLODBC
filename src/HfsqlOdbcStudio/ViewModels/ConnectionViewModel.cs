using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HfsqlOdbcStudio.Helpers;
using HfsqlOdbcStudio.Models;
using HfsqlOdbcStudio.Services;

namespace HfsqlOdbcStudio.ViewModels;

public sealed partial class ConnectionViewModel : ObservableObject
{
    public const string Only32BitWarning =
        "Only a 32-bit HFSQL ODBC driver is installed. This app is 64-bit and needs the 64-bit HFSQL ODBC driver.";

    private readonly IOdbcDriverService _drivers;
    private readonly IProfileService _profiles;
    private readonly IConnectionService _connections;
    private readonly INotificationService _notify;
    private readonly IDialogService _dialogs;
    private ProfileStore _store = new();
    private bool _loadingProfile;

    public ConnectionViewModel(IOdbcDriverService drivers, IProfileService profiles, IConnectionService connections,
        INotificationService notify, IDialogService dialogs)
    {
        _drivers = drivers;
        _profiles = profiles;
        _connections = connections;
        _notify = notify;
        _dialogs = dialogs;
    }

    public event EventHandler<bool>? CloseRequested;

    public ObservableCollection<ConnectionProfile> Profiles { get; } = [];

    public ObservableCollection<string> Drivers { get; } = [];

    public ObservableCollection<DsnInfo> Dsns { get; } = [];

    public ConnectionProfile? ConnectedProfile { get; private set; }

    public ConnectionInfo? ConnectedInfo { get; private set; }

    [ObservableProperty]
    private ConnectionProfile? _selectedProfile;

    [ObservableProperty]
    private string _profileName = "HFSQL server";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EffectiveConnectionString), nameof(IsFieldsMode), nameof(IsDsnMode), nameof(IsRawMode), nameof(CredentialsHint))]
    private ConnectionMode _mode = ConnectionMode.Fields;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EffectiveConnectionString))]
    private string _driver = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EffectiveConnectionString))]
    private string _server = "localhost";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EffectiveConnectionString))]
    private int _port = 4900;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EffectiveConnectionString))]
    private string _database = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EffectiveConnectionString))]
    private string _userName = "admin";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EffectiveConnectionString))]
    private string _password = "";

    [ObservableProperty]
    private bool _isPasswordVisible;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EffectiveConnectionString))]
    private DsnInfo? _selectedDsn;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EffectiveConnectionString))]
    private string _rawConnectionString = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDriverWarning))]
    private string? _driverWarning;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _dataFolder = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EffectiveConnectionString), nameof(IsClassic), nameof(IsClientServer))]
    private HfsqlKind _kind = HfsqlKind.ClientServer;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EffectiveConnectionString))]
    private string _classicDataDirectory = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EffectiveConnectionString))]
    private string _classicAnalysisFile = "";

    public bool IsClientServer
    {
        get => Kind == HfsqlKind.ClientServer;
        set
        {
            if (value) Kind = HfsqlKind.ClientServer;
        }
    }

    public bool IsClassic
    {
        get => Kind == HfsqlKind.Classic;
        set
        {
            if (value) Kind = HfsqlKind.Classic;
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTestResult))]
    private bool? _testSucceeded;

    [ObservableProperty]
    private string _testTitle = "";

    [ObservableProperty]
    private string _testDetail = "";

    public bool HasDriverWarning => !string.IsNullOrEmpty(DriverWarning);

    public bool HasTestResult => TestSucceeded.HasValue;

    public bool HasDsns => Dsns.Count > 0;

    public string EffectiveConnectionString => ConnectionStringFactory.Build(ToProfile(), maskPassword: true);

    public string CredentialsHint => Mode switch
    {
        ConnectionMode.Dsn => "Optional. When filled in, UID/PWD are appended to the DSN connection string.",
        ConnectionMode.ConnectionString => "Substituted for %USER% and %PASSWORD% in the connection string. The password is stored encrypted (DPAPI).",
        _ => "The password is stored encrypted for your Windows account (DPAPI).",
    };

    public bool IsFieldsMode
    {
        get => Mode == ConnectionMode.Fields;
        set
        {
            if (value) Mode = ConnectionMode.Fields;
        }
    }

    public bool IsDsnMode
    {
        get => Mode == ConnectionMode.Dsn;
        set
        {
            if (value) Mode = ConnectionMode.Dsn;
        }
    }

    public bool IsRawMode
    {
        get => Mode == ConnectionMode.ConnectionString;
        set
        {
            if (value) Mode = ConnectionMode.ConnectionString;
        }
    }

    public async Task InitializeAsync()
    {
        LoadDrivers();
        LoadDsns();
        _store = await _profiles.LoadAsync();
        Profiles.Clear();
        foreach (var p in _store.Profiles.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)) Profiles.Add(p);

        var last = Profiles.FirstOrDefault(p => p.Name.Equals(_store.LastProfileName, StringComparison.OrdinalIgnoreCase))
                   ?? Profiles.FirstOrDefault();
        if (last is not null)
        {
            SelectedProfile = last;
        }
        else
        {
            Driver = Drivers.FirstOrDefault(OdbcDriverService.IsHfsql) ?? "";
        }
    }

    public ConnectionProfile ToProfile() => new()
    {
        Name = ProfileName.Trim(),
        Mode = Mode,
        Driver = Driver?.Trim() ?? "",
        Server = Server?.Trim() ?? "",
        Port = Port,
        Database = Database?.Trim() ?? "",
        UserName = UserName ?? "",
        Password = Password ?? "",
        Dsn = SelectedDsn?.Name ?? SelectedProfile?.Dsn ?? "",
        RawConnectionString = RawConnectionString ?? "",
        DataFolder = DataFolder?.Trim() ?? "",
        Kind = Kind,
        ClassicDataDirectory = ClassicDataDirectory?.Trim() ?? "",
        ClassicAnalysisFile = ClassicAnalysisFile?.Trim() ?? "",
        LastUsedUtc = DateTime.UtcNow,
    };

    partial void OnSelectedProfileChanged(ConnectionProfile? value)
    {
        if (value is null || _loadingProfile) return;
        _loadingProfile = true;
        try
        {
            ProfileName = value.Name;
            Mode = value.Mode;
            Driver = value.Driver;
            Server = value.Server;
            Port = value.Port;
            Database = value.Database;
            UserName = value.UserName;
            Password = value.Password;
            SelectedDsn = Dsns.FirstOrDefault(d => d.Name.Equals(value.Dsn, StringComparison.OrdinalIgnoreCase));
            RawConnectionString = value.RawConnectionString;
            DataFolder = value.DataFolder ?? "";
            Kind = value.Kind;
            ClassicDataDirectory = value.ClassicDataDirectory ?? "";
            ClassicAnalysisFile = value.ClassicAnalysisFile ?? "";
            TestSucceeded = null;
        }
        finally
        {
            _loadingProfile = false;
        }
    }

    [RelayCommand]
    private void RefreshDrivers()
    {
        LoadDrivers();
        LoadDsns();
    }

    [RelayCommand]
    private void NewProfile()
    {
        SelectedProfile = null;
        var name = "New connection";
        for (int i = 2; Profiles.Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)); i++) name = $"New connection {i}";
        ProfileName = name;
        Mode = ConnectionMode.Fields;
        Driver = Drivers.FirstOrDefault(OdbcDriverService.IsHfsql) ?? "";
        Server = "localhost";
        Port = 4900;
        Database = "";
        UserName = "admin";
        Password = "";
        SelectedDsn = null;
        RawConnectionString = "";
        DataFolder = "";
        Kind = HfsqlKind.ClientServer;
        ClassicDataDirectory = "";
        ClassicAnalysisFile = "";
        TestSucceeded = null;
    }

    [RelayCommand]
    private async Task SaveProfileAsync()
    {
        if (!ValidateName()) return;
        await SaveCurrentAsync();
        _notify.Success("Profile saved", $"\"{ProfileName.Trim()}\" saved to profiles.json (password encrypted with DPAPI).");
    }

    [RelayCommand]
    private async Task DeleteProfileAsync()
    {
        var target = SelectedProfile;
        if (target is null) return;
        if (!await _dialogs.ConfirmAsync("Delete profile", $"Delete the connection profile \"{target.Name}\"?", "Delete", "Cancel", danger: true)) return;
        Profiles.Remove(target);
        _store.Profiles.Remove(target);
        if (string.Equals(_store.LastProfileName, target.Name, StringComparison.OrdinalIgnoreCase)) _store.LastProfileName = null;
        await _profiles.SaveAsync(_store);
        NewProfile();
    }

    [RelayCommand]
    private void CopyConnectionString()
    {
        try
        {
            Clipboard.SetText(EffectiveConnectionString);
            _notify.Info("Copied", "Connection string copied (password masked).");
        }
        catch (Exception ex)
        {
            _notify.Error("Clipboard", ex.Message);
        }
    }

    [RelayCommand]
    private void EditAsRaw()
    {
        RawConnectionString = ConnectionStringFactory.BuildTemplateForRaw(ToProfile());
        Mode = ConnectionMode.ConnectionString;
    }

    [RelayCommand]
    private async Task TestConnectionAsync(CancellationToken ct)
    {
        if (!ValidateInput()) return;
        await TestCoreAsync(ct);
    }

    [RelayCommand]
    private async Task ConnectAsync(CancellationToken ct)
    {
        if (!ValidateInput() || !ValidateName()) return;
        var (profile, info) = await TestCoreAsync(ct);
        if (profile is null || info is null) return;

        await SaveCurrentAsync();
        ConnectedProfile = profile;
        ConnectedInfo = info;
        _connections.SetActive(profile, info);
        CloseRequested?.Invoke(this, true);
    }

    [RelayCommand]
    private void BrowseDataFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = @"Select the HFSQL database folder (e.g. C:\HVW4HFCS\BDD\IndiaTest or \\server\share\IndiaTest)",
        };
        if (!string.IsNullOrWhiteSpace(DataFolder) && System.IO.Directory.Exists(DataFolder)) dialog.InitialDirectory = DataFolder;
        if (dialog.ShowDialog() == true) DataFolder = dialog.FolderName;
    }

    [RelayCommand]
    private void BrowseClassicDataDirectory()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Select the HFSQL Classic data folder (contains the .fic files)" };
        if (System.IO.Directory.Exists(ClassicDataDirectory)) dialog.InitialDirectory = ClassicDataDirectory;
        if (dialog.ShowDialog() == true) ClassicDataDirectory = dialog.FolderName;
    }

    [RelayCommand]
    private void BrowseClassicAnalysisFile()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select the WinDev analysis file (.wdd)",
            Filter = "WinDev analysis (*.wdd)|*.wdd|All files (*.*)|*.*",
            CheckFileExists = true,
        };
        if (System.IO.File.Exists(ClassicAnalysisFile)) dialog.InitialDirectory = System.IO.Path.GetDirectoryName(ClassicAnalysisFile);
        if (dialog.ShowDialog() == true) ClassicAnalysisFile = dialog.FileName;
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, false);

    private async Task<(ConnectionProfile? Profile, ConnectionInfo? Info)> TestCoreAsync(CancellationToken ct)
    {
        IsBusy = true;
        TestSucceeded = null;
        var profile = ToProfile();
        try
        {
            var info = await _connections.TestAsync(profile, ct);
            TestTitle = "Connection succeeded";
            TestDetail = FormatInfo(info);
            TestSucceeded = true;
            return (profile, info);
        }
        catch (Exception ex)
        {
            TestTitle = "Connection failed";
            TestDetail = OdbcErrorFormatter.Format(ex) + Environment.NewLine + Environment.NewLine +
                         "Connection string (password masked):" + Environment.NewLine + EffectiveConnectionString;
            TestSucceeded = false;
            FileLog.Error("Connection test failed for " + EffectiveConnectionString, ex);
            return (null, null);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public static string FormatInfo(ConnectionInfo info) => string.Join(Environment.NewLine,
    [
        $"Driver:          {info.DriverName}",
        $"Driver file:     {info.DriverFile}",
        $"Driver version:  {info.DriverVersion}",
        $"DBMS:            {info.DbmsName} {info.DbmsVersion}".TrimEnd(),
        $"Server version:  {info.ServerVersion}",
        $"Database:        {info.Database}",
        $"Data source:     {info.DataSource}",
        $"Data folder:     {info.DataFolder}",
        $"ODBC layer:      {info.AccessLayerDisplay}",
        info.AccessLayerNote,
    ]).TrimEnd();

    private async Task SaveCurrentAsync()
    {
        var profile = ToProfile();
        var existing = Profiles.FirstOrDefault(p => p.Name.Equals(profile.Name, StringComparison.OrdinalIgnoreCase));
        _loadingProfile = true;
        try
        {
            if (existing is not null)
            {
                var index = Profiles.IndexOf(existing);
                Profiles[index] = profile;
                _store.Profiles.Remove(existing);
            }
            else
            {
                Profiles.Add(profile);
            }

            _store.Profiles.Add(profile);
            _store.LastProfileName = profile.Name;
            SelectedProfile = profile;
        }
        finally
        {
            _loadingProfile = false;
        }

        await _profiles.SaveAsync(_store);
    }

    private bool ValidateName()
    {
        if (!string.IsNullOrWhiteSpace(ProfileName)) return true;
        _notify.Warning("Profile name required", "Enter a name for this connection profile.");
        return false;
    }

    private bool ValidateInput()
    {
        string? problem = Mode switch
        {
            ConnectionMode.Fields when string.IsNullOrWhiteSpace(Driver) => "Select an ODBC driver.",
            ConnectionMode.Fields when Kind == HfsqlKind.Classic && string.IsNullOrWhiteSpace(ClassicDataDirectory) => "Select the HFSQL Classic data folder (REP).",
            ConnectionMode.Fields when Kind == HfsqlKind.Classic && !System.IO.Directory.Exists(ClassicDataDirectory.Trim()) => $"The data folder does not exist or is not reachable: {ClassicDataDirectory}",
            ConnectionMode.Fields when Kind == HfsqlKind.Classic && !string.IsNullOrWhiteSpace(ClassicAnalysisFile) && !System.IO.File.Exists(ClassicAnalysisFile.Trim()) => $"The analysis file does not exist: {ClassicAnalysisFile}",
            ConnectionMode.Fields when Kind == HfsqlKind.ClientServer && string.IsNullOrWhiteSpace(Server) => "Enter the server name or address.",
            ConnectionMode.Fields when Kind == HfsqlKind.ClientServer && Port is <= 0 or > 65535 => "Enter a valid port (default 4900).",
            ConnectionMode.Dsn when SelectedDsn is null => "Select a data source (DSN).",
            ConnectionMode.ConnectionString when string.IsNullOrWhiteSpace(RawConnectionString) => "Enter a connection string.",
            _ when !string.IsNullOrWhiteSpace(DataFolder) => Helpers.DataFolder.Check(DataFolder.Trim()),
            _ => null,
        };
        if (problem is null) return true;
        _notify.Warning("Missing information", problem);
        return false;
    }

    private void LoadDrivers()
    {
        var current = Driver;
        Drivers.Clear();
        foreach (var d in _drivers.GetDrivers64()) Drivers.Add(d);

        if (Drivers.Any(OdbcDriverService.IsHfsql))
            DriverWarning = null;
        else if (_drivers.IsHfsqlInstalledOnlyAs32Bit())
            DriverWarning = Only32BitWarning;
        else
            DriverWarning = "No HFSQL ODBC driver was found in the 64-bit ODBC driver list. Install the 64-bit HFSQL ODBC driver (PC SOFT).";

        Driver = !string.IsNullOrEmpty(current) ? current : Drivers.FirstOrDefault(OdbcDriverService.IsHfsql) ?? "";
    }

    private void LoadDsns()
    {
        var selected = SelectedDsn?.Name ?? SelectedProfile?.Dsn;
        Dsns.Clear();
        foreach (var d in _drivers.GetDataSources64().OrderByDescending(d => OdbcDriverService.IsHfsql(d.Driver))) Dsns.Add(d);
        SelectedDsn = Dsns.FirstOrDefault(d => d.Name.Equals(selected, StringComparison.OrdinalIgnoreCase));
        OnPropertyChanged(nameof(HasDsns));
    }
}
