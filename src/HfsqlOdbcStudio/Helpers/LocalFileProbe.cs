using System.IO;

namespace HfsqlOdbcStudio.Helpers;

/// <summary>
/// Looks for HFSQL data files (&lt;table&gt;.fic) written recently on this PC. Used when the driver reports
/// CREATE TABLE as successful but the server's catalog does not contain the table: the HFSQL SQL engine inside
/// the driver may have created a local HFSQL Classic file instead.
/// </summary>
public static class LocalFileProbe
{
    public static IReadOnlyList<string> CandidateDirectories(string? driverFile)
    {
        var dirs = new List<string?>
        {
            Environment.CurrentDirectory,
            AppContext.BaseDirectory,
            string.IsNullOrEmpty(driverFile) ? null : Path.GetDirectoryName(driverFile),
            Path.GetTempPath(),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VirtualStore"),
            Environment.SystemDirectory,
        };

        return dirs.Where(d => !string.IsNullOrEmpty(d) && Directory.Exists(d))
            .Select(d => Path.GetFullPath(d!).TrimEnd('\\'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Finds &lt;table&gt;.fic files modified within <paramref name="window"/> (shallow search of each candidate directory).</summary>
    public static IReadOnlyList<FileInfo> FindRecent(string table, string? driverFile, TimeSpan window)
    {
        var since = DateTime.Now - window;
        var options = new EnumerationOptions
        {
            MatchCasing = MatchCasing.CaseInsensitive,
            RecurseSubdirectories = true,
            MaxRecursionDepth = 3,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };

        var found = new List<FileInfo>();
        foreach (var dir in CandidateDirectories(driverFile))
        {
            try
            {
                foreach (var path in Directory.EnumerateFiles(dir, table + ".fic", options))
                {
                    var fi = new FileInfo(path);
                    if (fi.LastWriteTime >= since) found.Add(fi);
                }
            }
            catch (Exception ex)
            {
                FileLog.Error($"Searching {dir} for {table}.fic failed", ex);
            }
        }

        return found.DistinctBy(f => f.FullName, StringComparer.OrdinalIgnoreCase).OrderByDescending(f => f.LastWriteTime).ToList();
    }

    /// <summary>Human-readable summary for the Messages tab and the DDL report.</summary>
    public static string Describe(string table, string? driverFile, TimeSpan window)
    {
        var files = FindRecent(table, driverFile, window);
        if (files.Count == 0)
        {
            return $"No local {table}.fic written in the last {window.TotalMinutes:0} minutes was found in: " +
                   string.Join("; ", CandidateDirectories(driverFile)) + ".";
        }

        return $"The driver appears to have created the table as a LOCAL HFSQL file on this PC instead of on the server: " +
               string.Join("; ", files.Select(f => $"{f.FullName} ({f.LastWriteTime:HH:mm:ss}, {f.Length:N0} bytes)"));
    }
}
