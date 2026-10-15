using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HfsqlOdbcStudio.Helpers;

public static class AppPaths
{
    public static string Root { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HfsqlOdbcStudio");

    public static string Logs => Path.Combine(Root, "logs");

    public static string ProfilesFile => Path.Combine(Root, "profiles.json");

    public static string SettingsFile => Path.Combine(Root, "settings.json");

    public static string HistoryFile => Path.Combine(Root, "history.json");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Logs);
    }
}

/// <summary>Append-only diagnostic log in %AppData%\HfsqlOdbcStudio\logs. Never receives passwords.</summary>
public static class FileLog
{
    private static readonly object Gate = new();

    public static string CurrentFile => Path.Combine(AppPaths.Logs, $"app-{DateTime.Now:yyyyMMdd}.log");

    public static void Info(string message) => Write("INFO ", message, null);

    public static void Error(string message, Exception? ex = null) => Write("ERROR", message, ex);

    private static void Write(string level, string message, Exception? ex)
    {
        try
        {
            var sb = new StringBuilder();
            sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")).Append(' ').Append(level).Append(' ').AppendLine(message);
            if (ex is not null) sb.AppendLine(ex.ToString());
            lock (Gate)
            {
                AppPaths.EnsureCreated();
                File.AppendAllText(CurrentFile, sb.ToString());
            }
        }
        catch
        {
            // Logging must never crash the app.
        }
    }
}

/// <summary>Small JSON persistence helper with atomic writes.</summary>
public static class JsonStore
{
    private static readonly SemaphoreSlim WriteLock = new(1, 1);

    public static JsonSerializerOptions Options { get; } = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static T? Load<T>(string path)
        where T : class
    {
        try
        {
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options);
        }
        catch (Exception ex)
        {
            FileLog.Error($"Could not read {path}", ex);
            return null;
        }
    }

    public static async Task<T?> LoadAsync<T>(string path)
        where T : class
    {
        try
        {
            if (!File.Exists(path)) return null;
            await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
            return await JsonSerializer.DeserializeAsync<T>(fs, Options);
        }
        catch (Exception ex)
        {
            FileLog.Error($"Could not read {path}", ex);
            return null;
        }
    }

    public static async Task SaveAsync<T>(string path, T value)
    {
        await WriteLock.WaitAsync();
        try
        {
            AppPaths.EnsureCreated();
            var tmp = path + ".tmp";
            await using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true))
                await JsonSerializer.SerializeAsync(fs, value, Options);
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            WriteLock.Release();
        }
    }

    public static void Save<T>(string path, T value)
    {
        WriteLock.Wait();
        try
        {
            AppPaths.EnsureCreated();
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(value, Options));
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            WriteLock.Release();
        }
    }
}
