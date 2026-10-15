using System.Security.Cryptography;
using System.Text;
using HfsqlOdbcStudio.Helpers;
using HfsqlOdbcStudio.Models;

namespace HfsqlOdbcStudio.Services;

public interface IProfileService
{
    Task<ProfileStore> LoadAsync();

    Task SaveAsync(ProfileStore store);
}

/// <summary>Stores profiles in %AppData%\HfsqlOdbcStudio\profiles.json with DPAPI (CurrentUser) protected secrets.</summary>
public sealed class ProfileService : IProfileService
{
    private static readonly byte[] Entropy = "HfsqlOdbcStudio/profiles/v1"u8.ToArray();

    public async Task<ProfileStore> LoadAsync()
    {
        var store = await JsonStore.LoadAsync<ProfileStore>(AppPaths.ProfilesFile) ?? new ProfileStore();
        store.Profiles ??= [];
        foreach (var p in store.Profiles)
        {
            p.Password = Unprotect(p.ProtectedPassword);
            p.RawConnectionString = Unprotect(p.ProtectedRawConnectionString);
        }

        return store;
    }

    public Task SaveAsync(ProfileStore store)
    {
        var copy = new ProfileStore
        {
            LastProfileName = store.LastProfileName,
            Profiles = store.Profiles.Select(p =>
            {
                var c = p.Clone();
                c.ProtectedPassword = Protect(p.Password);
                c.ProtectedRawConnectionString = Protect(p.RawConnectionString);
                return c;
            }).ToList(),
        };
        return JsonStore.SaveAsync(AppPaths.ProfilesFile, copy);
    }

    private static string? Protect(string? clear)
    {
        if (string.IsNullOrEmpty(clear)) return null;
        var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(clear), Entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(bytes);
    }

    private static string Unprotect(string? protectedBase64)
    {
        if (string.IsNullOrEmpty(protectedBase64)) return "";
        try
        {
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(protectedBase64), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            FileLog.Error("A stored secret could not be decrypted (profile created by another Windows user?)", ex);
            return "";
        }
    }
}

public interface ISettingsService
{
    AppSettings Current { get; }

    void Load();

    void Save();
}

public sealed class SettingsService : ISettingsService
{
    public AppSettings Current { get; private set; } = new();

    public void Load() => Current = JsonStore.Load<AppSettings>(AppPaths.SettingsFile) ?? new AppSettings();

    public void Save()
    {
        try
        {
            JsonStore.Save(AppPaths.SettingsFile, Current);
        }
        catch (Exception ex)
        {
            FileLog.Error("Saving settings failed", ex);
        }
    }
}

public interface IHistoryService
{
    Task<List<HistoryEntry>> LoadAsync();

    Task SaveAsync(IEnumerable<HistoryEntry> entries);
}

public sealed class HistoryService : IHistoryService
{
    public const int MaxEntries = 100;

    public async Task<List<HistoryEntry>> LoadAsync() =>
        (await JsonStore.LoadAsync<List<HistoryEntry>>(AppPaths.HistoryFile) ?? []).Take(MaxEntries).ToList();

    public async Task SaveAsync(IEnumerable<HistoryEntry> entries)
    {
        try
        {
            await JsonStore.SaveAsync(AppPaths.HistoryFile, entries.Take(MaxEntries).ToList());
        }
        catch (Exception ex)
        {
            FileLog.Error("Saving history failed", ex);
        }
    }
}
