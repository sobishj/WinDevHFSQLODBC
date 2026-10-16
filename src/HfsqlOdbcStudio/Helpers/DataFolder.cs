using System.IO;

namespace HfsqlOdbcStudio.Helpers;

/// <summary>
/// The HFSQL ODBC driver creates the data files (.fic/.ndx/.mmo) of tables made with CREATE TABLE in the
/// process's current directory. Pointing the current directory at the database folder of the HFSQL server
/// (local path or UNC share) makes those tables appear in the server database.
/// </summary>
public static class DataFolder
{
    private static readonly string StartupDirectory = Environment.CurrentDirectory;

    /// <summary>Makes <paramref name="folder"/> the process current directory (empty = the startup directory).</summary>
    public static void Apply(string? folder)
    {
        var target = string.IsNullOrWhiteSpace(folder) ? StartupDirectory : folder.Trim();
        if (!Directory.Exists(target))
            throw new DirectoryNotFoundException($"Data file folder not found or not reachable: {target}");
        if (!string.Equals(Environment.CurrentDirectory.TrimEnd('\\'), target.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            Environment.CurrentDirectory = target;
    }

    /// <summary>Returns null when the folder exists and is writable, otherwise the problem.</summary>
    public static string? Check(string folder)
    {
        try
        {
            if (!Directory.Exists(folder)) return $"The folder does not exist or is not reachable: {folder}";
            var probe = Path.Combine(folder, $".hfodbcstudio-write-test-{Guid.NewGuid():N}.tmp");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return null;
        }
        catch (Exception ex)
        {
            return $"The folder is not writable: {folder}{Environment.NewLine}{ex.Message}";
        }
    }
}
