using System.Data;
using System.Data.Common;
using System.Data.Odbc;
using HfsqlOdbcStudio.Helpers;
using HfsqlOdbcStudio.Models;

namespace HfsqlOdbcStudio.Services;

public interface IConnectionService
{
    ConnectionProfile? ActiveProfile { get; }

    ConnectionInfo? ActiveInfo { get; }

    bool IsConnected { get; }

    event EventHandler? StateChanged;

    /// <summary>Opens a new session for the active profile, using the layer chosen at connect time. Caller disposes it.</summary>
    Task<IOdbcSession> OpenActiveAsync(CancellationToken ct = default);

    /// <summary>Opens a connection, reads driver/server information and decides which ODBC layer to use.</summary>
    Task<ConnectionInfo> TestAsync(ConnectionProfile profile, CancellationToken ct = default);

    void SetActive(ConnectionProfile profile, ConnectionInfo info);

    void Disconnect();
}

/// <summary>
/// Manages the active profile. Every operation opens its own short-lived session, so a long query never
/// blocks schema refreshes and cancellation is isolated.
/// </summary>
public sealed class ConnectionService(IOdbcDriverService drivers) : IConnectionService
{
    public ConnectionProfile? ActiveProfile { get; private set; }

    public ConnectionInfo? ActiveInfo { get; private set; }

    public bool IsConnected => ActiveProfile is not null;

    public event EventHandler? StateChanged;

    public Task<IOdbcSession> OpenActiveAsync(CancellationToken ct = default)
    {
        var profile = ActiveProfile ?? throw new InvalidOperationException("Not connected. Open a connection first.");
        var native = ActiveInfo?.UseNativeLayer == true;
        var cs = ConnectionStringFactory.Build(profile, maskPassword: false);
        return Task.Run<IOdbcSession>(() =>
        {
            ct.ThrowIfCancellationRequested();
            DataFolder.Apply(profile.DataFolder);
            return native ? new NativeOdbcSession(NativeOdbcConnection.Open(cs)) : new ManagedOdbcSession(OpenManaged(cs));
        }, ct);
    }

    public async Task<ConnectionInfo> TestAsync(ConnectionProfile profile, CancellationToken ct = default)
    {
        var cs = ConnectionStringFactory.Build(profile, maskPassword: false);
        return await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            DataFolder.Apply(profile.DataFolder);
            ConnectionInfo info;
            OdbcConnection opened;
            try
            {
                opened = OpenManaged(cs);
            }
            catch (Exception managedError)
            {
                // System.Data.Odbc sets connection attributes some drivers reject, and may report no diagnostics.
                // The native layer only calls SQLDriverConnect and returns the driver's own messages.
                FileLog.Error("System.Data.Odbc could not open the connection; trying the native ODBC layer", managedError);
                return OpenNativeOnly(cs, profile, managedError);
            }

            using (var conn = opened)
            {
                info = ReadInfo(conn, profile);

                // A read-only catalog call shows whether System.Data.Odbc can cope with this driver.
                try
                {
                    using var probe = conn.GetSchema("Tables");
                    return info with { AccessLayerNote = "System.Data.Odbc works with this driver." };
                }
                catch (OverflowException ex)
                {
                    FileLog.Error("System.Data.Odbc overflow while reading driver metadata; switching to the native ODBC layer", ex);
                }
            }

            // Make sure the native layer works before choosing it.
            using (var native = NativeOdbcConnection.Open(cs))
            {
                using var tables = native.Tables(null);
            }

            return info with
            {
                UseNativeLayer = true,
                AccessLayerNote = "The driver returns 32-bit length/row-count values that System.Data.Odbc cannot read " +
                                  "(\"Arithmetic operation resulted in an overflow\"), so the native ODBC layer is used.",
            };
        }, ct);
    }

    private ConnectionInfo OpenNativeOnly(string cs, ConnectionProfile profile, Exception managedError)
    {
        try
        {
            using var native = NativeOdbcConnection.Open(cs);
        }
        catch (NativeOdbcException nativeError) when (nativeError.Errors.Count > 0)
        {
            throw;
        }
        catch (Exception nativeError)
        {
            FileLog.Error("The native ODBC layer could not open the connection either", nativeError);
            throw managedError;
        }

        var driverName = ResolveDriverName(profile);
        var version = drivers.GetDriverVersion(driverName);
        return new ConnectionInfo(
            DriverName: driverName,
            DriverFile: System.IO.Path.GetFileName(drivers.GetDriverFile(driverName)),
            DriverVersion: version.Length > 0 ? version : "unknown",
            DbmsName: "",
            DbmsVersion: "",
            ServerVersion: "",
            Database: "",
            DataSource: "",
            UseNativeLayer: true,
            AccessLayerNote: "System.Data.Odbc could not open this connection (" + OdbcErrorFormatter.Short(managedError) +
                              "), so the native ODBC layer is used.",
            DriverPath: drivers.GetDriverFile(driverName),
            DataFolder: Environment.CurrentDirectory);
    }

    public void SetActive(ConnectionProfile profile, ConnectionInfo info)
    {
        ActiveProfile = profile.Clone();
        ActiveInfo = info;
        FileLog.Info($"Connected ({info.AccessLayerDisplay}): {ConnectionStringFactory.Build(profile, maskPassword: true)}");
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Disconnect()
    {
        ActiveProfile = null;
        ActiveInfo = null;
        try
        {
            DataFolder.Apply(null);
            OdbcConnection.ReleaseObjectPool();
        }
        catch (Exception ex)
        {
            FileLog.Error("ReleaseObjectPool failed", ex);
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private static OdbcConnection OpenManaged(string cs)
    {
        var conn = new OdbcConnection(cs);
        try
        {
            conn.Open();
            return conn;
        }
        catch
        {
            conn.Dispose();
            throw;
        }
    }

    private ConnectionInfo ReadInfo(OdbcConnection conn, ConnectionProfile profile)
    {
        string dbmsName = "", dbmsVersion = "";
        try
        {
            using DataTable info = conn.GetSchema(DbMetaDataCollectionNames.DataSourceInformation);
            if (info.Rows.Count > 0)
            {
                var row = info.Rows[0];
                dbmsName = Convert.ToString(row[DbMetaDataColumnNames.DataSourceProductName]) ?? "";
                dbmsVersion = Convert.ToString(row[DbMetaDataColumnNames.DataSourceProductVersion]) ?? "";
            }
        }
        catch (Exception ex)
        {
            FileLog.Error("Reading DataSourceInformation failed", ex);
        }

        var driverName = ResolveDriverName(profile);
        var driverVersion = drivers.GetDriverVersion(driverName);

        return new ConnectionInfo(
            DriverName: driverName,
            DriverFile: Safe(() => conn.Driver),
            DriverVersion: driverVersion.Length > 0 ? driverVersion : "unknown",
            DbmsName: dbmsName,
            DbmsVersion: dbmsVersion,
            ServerVersion: Safe(() => conn.ServerVersion),
            Database: Safe(() => conn.Database),
            DataSource: Safe(() => conn.DataSource),
            DriverPath: drivers.GetDriverFile(driverName),
            DataFolder: Environment.CurrentDirectory);
    }

    private string ResolveDriverName(ConnectionProfile profile) => profile.Mode switch
    {
        ConnectionMode.Fields => profile.Driver,
        ConnectionMode.Dsn => drivers.GetDsnDriver(profile.Dsn) ?? "",
        _ => ResolveRawDriver(profile.RawConnectionString),
    };

    private string ResolveRawDriver(string raw)
    {
        var driver = ConnectionStringFactory.ExtractDriver(raw);
        if (!string.IsNullOrEmpty(driver)) return driver;
        var dsn = ConnectionStringFactory.ExtractDsn(raw);
        return dsn is null ? "" : drivers.GetDsnDriver(dsn) ?? "";
    }

    private static string Safe(Func<string?> get)
    {
        try
        {
            return get() ?? "";
        }
        catch
        {
            return "";
        }
    }
}
