using System.Text.Json.Serialization;

namespace HfsqlOdbcStudio.Models;

/// <summary>How the ODBC connection string is produced.</summary>
public enum ConnectionMode
{
    /// <summary>Built from driver / server / port / database / user / password fields.</summary>
    Fields,

    /// <summary>An existing 64-bit System or User DSN.</summary>
    Dsn,

    /// <summary>A raw, user-edited connection string.</summary>
    ConnectionString,
}

/// <summary>HFSQL database engine used in "server fields" mode.</summary>
public enum HfsqlKind
{
    /// <summary>HFSQL Client/Server: Server Name / Server Port / Database / UID / PWD.</summary>
    ClientServer,

    /// <summary>HFSQL Classic (HyperFile): data files in a folder (REP), optional analysis (ANA).</summary>
    Classic,
}

/// <summary>A named, persisted connection profile. Secrets are stored DPAPI-protected.</summary>
public sealed class ConnectionProfile
{
    public string Name { get; set; } = "";

    public ConnectionMode Mode { get; set; } = ConnectionMode.Fields;

    public string Driver { get; set; } = "";

    public string Server { get; set; } = "localhost";

    public int Port { get; set; } = 4900;

    public string Database { get; set; } = "";

    public string UserName { get; set; } = "admin";

    public string Dsn { get; set; } = "";

    public HfsqlKind Kind { get; set; } = HfsqlKind.ClientServer;

    /// <summary>HFSQL Classic: folder that contains the .fic/.ndx/.mmo data files (REP=).</summary>
    public string ClassicDataDirectory { get; set; } = "";

    /// <summary>HFSQL Classic: optional analysis file (.wdd) describing the data files (ANA=).</summary>
    public string ClassicAnalysisFile { get; set; } = "";

    /// <summary>
    /// Working folder (process current directory) for every operation while connected; the driver reads and writes
    /// table data files relative to it.
    /// Set it to the HFSQL server's database folder, locally or as a UNC path. Empty = the app's startup folder.
    /// </summary>
    public string DataFolder { get; set; } = "";

    /// <summary>DPAPI (CurrentUser) protected password, base64.</summary>
    public string? ProtectedPassword { get; set; }

    /// <summary>DPAPI (CurrentUser) protected raw connection string, base64 (it may embed credentials).</summary>
    public string? ProtectedRawConnectionString { get; set; }

    public DateTime LastUsedUtc { get; set; }

    /// <summary>Clear-text password, only in memory.</summary>
    [JsonIgnore]
    public string Password { get; set; } = "";

    /// <summary>Clear-text raw connection string, only in memory.</summary>
    [JsonIgnore]
    public string RawConnectionString { get; set; } = "";

    /// <summary>Short human-readable target, e.g. "srv01/Sales". Never contains a password.</summary>
    [JsonIgnore]
    public string DisplayTarget => Mode switch
    {
        ConnectionMode.Fields when Kind == HfsqlKind.Classic => $"Classic {ClassicDataDirectory}",
        ConnectionMode.Fields => $"{Server}/{Database}",
        ConnectionMode.Dsn => $"DSN {Dsn}",
        _ => string.IsNullOrWhiteSpace(Name) ? "custom connection string" : Name,
    };

    public ConnectionProfile Clone() => (ConnectionProfile)MemberwiseClone();

    public override string ToString() => Name;
}

/// <summary>Root object of profiles.json.</summary>
public sealed class ProfileStore
{
    public string? LastProfileName { get; set; }

    public List<ConnectionProfile> Profiles { get; set; } = [];
}
