using System.Diagnostics;
using System.IO;
using HfsqlOdbcStudio.Helpers;
using HfsqlOdbcStudio.Models;
using Microsoft.Win32;

namespace HfsqlOdbcStudio.Services;

public interface IOdbcDriverService
{
    /// <summary>Drivers registered under the 64-bit HKLM\SOFTWARE\ODBC\ODBCINST.INI\ODBC Drivers key.</summary>
    IReadOnlyList<string> GetDrivers64();

    /// <summary>System and User DSNs whose driver is a 64-bit driver.</summary>
    IReadOnlyList<DsnInfo> GetDataSources64();

    /// <summary>True when no 64-bit HFSQL driver exists but a 32-bit one is registered under WOW6432Node.</summary>
    bool IsHfsqlInstalledOnlyAs32Bit();

    string GetDriverFile(string driverName);

    string GetDriverVersion(string driverName);

    string? GetDsnDriver(string dsn);
}

public sealed class OdbcDriverService : IOdbcDriverService
{
    private const string OdbcInst = @"SOFTWARE\ODBC\ODBCINST.INI";
    private const string OdbcInst32 = @"SOFTWARE\WOW6432Node\ODBC\ODBCINST.INI";
    private const string DataSources = @"SOFTWARE\ODBC\ODBC.INI\ODBC Data Sources";

    public static bool IsHfsql(string? name) => name?.Contains("HFSQL", StringComparison.OrdinalIgnoreCase) == true;

    public IReadOnlyList<string> GetDrivers64()
    {
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            return ReadDriverList(hklm, OdbcInst + @"\ODBC Drivers");
        }
        catch (Exception ex)
        {
            FileLog.Error("Reading 64-bit ODBC drivers failed", ex);
            return [];
        }
    }

    public IReadOnlyList<DsnInfo> GetDataSources64()
    {
        var drivers = new HashSet<string>(GetDrivers64(), StringComparer.OrdinalIgnoreCase);
        var list = new List<DsnInfo>();
        try
        {
            using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                ReadDsns(hklm, "System", drivers, list);
            using (var hkcu = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64))
                ReadDsns(hkcu, "User", drivers, list);
        }
        catch (Exception ex)
        {
            FileLog.Error("Reading ODBC data sources failed", ex);
        }

        return list.OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public bool IsHfsqlInstalledOnlyAs32Bit()
    {
        if (GetDrivers64().Any(IsHfsql)) return false;
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            if (ReadDriverList(hklm, OdbcInst32 + @"\ODBC Drivers").Any(IsHfsql)) return true;
            using var inst32 = hklm.OpenSubKey(OdbcInst32);
            return inst32?.GetSubKeyNames().Any(IsHfsql) == true;
        }
        catch (Exception ex)
        {
            FileLog.Error("Reading 32-bit ODBC drivers failed", ex);
            return false;
        }
    }

    public string GetDriverFile(string driverName)
    {
        if (string.IsNullOrWhiteSpace(driverName)) return "";
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = hklm.OpenSubKey(OdbcInst + "\\" + driverName);
            return key?.GetValue("Driver") is string path ? Environment.ExpandEnvironmentVariables(path) : "";
        }
        catch (Exception ex)
        {
            FileLog.Error($"Reading driver path for '{driverName}' failed", ex);
            return "";
        }
    }

    public string GetDriverVersion(string driverName)
    {
        var file = GetDriverFile(driverName);
        if (file.Length == 0 || !File.Exists(file)) return "";
        try
        {
            var info = FileVersionInfo.GetVersionInfo(file);
            return info.FileVersion ?? info.ProductVersion ?? "";
        }
        catch (Exception ex)
        {
            FileLog.Error($"Reading version of '{file}' failed", ex);
            return "";
        }
    }

    public string? GetDsnDriver(string dsn) =>
        GetDataSources64().FirstOrDefault(d => d.Name.Equals(dsn, StringComparison.OrdinalIgnoreCase))?.Driver;

    private static List<string> ReadDriverList(RegistryKey root, string path)
    {
        using var key = root.OpenSubKey(path);
        if (key is null) return [];
        return key.GetValueNames()
            .Where(n => !string.IsNullOrEmpty(n) && string.Equals(key.GetValue(n) as string, "Installed", StringComparison.OrdinalIgnoreCase))
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void ReadDsns(RegistryKey root, string scope, HashSet<string> drivers64, List<DsnInfo> list)
    {
        using var key = root.OpenSubKey(DataSources);
        if (key is null) return;
        foreach (var name in key.GetValueNames())
        {
            if (string.IsNullOrEmpty(name)) continue;
            var driver = key.GetValue(name) as string ?? "";
            if (drivers64.Contains(driver)) list.Add(new DsnInfo(name, driver, scope));
        }
    }
}
