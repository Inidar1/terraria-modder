using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using TerrariaModManager.Models;

namespace TerrariaModManager.Services;

public class SettingsService
{
    private readonly AppPaths _paths;
    private readonly string _appDataDir;
    private readonly string _settingsPath;
    private readonly JsonSerializerOptions _jsonOpts = new()
    {
        WriteIndented = true
    };

    public SettingsService(AppPaths paths)
    {
        _paths = paths;
        _appDataDir = paths.AppDataDirectory;
        _settingsPath = paths.SettingsFile;
    }

    public string DownloadsDir => _paths.DownloadsDirectory;
    public string CacheDir => _paths.CacheDirectory;

    public AppSettings Load()
    {
        try
        {
            if (File.Exists(_settingsPath))
            {
                var json = File.ReadAllText(_settingsPath);
                return JsonSerializer.Deserialize<AppSettings>(json, _jsonOpts) ?? new AppSettings();
            }
        }
        catch { }
        return new AppSettings();
    }

    public void Save(AppSettings settings)
    {
        try
        {
            EnsureDirectories();
            var json = JsonSerializer.Serialize(settings, _jsonOpts);
            File.WriteAllText(_settingsPath, json);
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                File.SetUnixFileMode(_settingsPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch { }
    }

    public void EnsureDirectories()
    {
        _paths.EnsureApplicationDirectories();
    }

    /// <summary>Get the decrypted API key, handling legacy migration.</summary>
    public string? GetApiKey(AppSettings settings)
    {
        // Migrate legacy plaintext key to encrypted
        if (!string.IsNullOrEmpty(settings.NexusApiKeyLegacy))
        {
            var key = settings.NexusApiKeyLegacy;
            settings.NexusApiKeyLegacy = null;
            settings.NexusApiKeyEncrypted = EncryptString(key);
            Save(settings);
            return key;
        }

        if (string.IsNullOrEmpty(settings.NexusApiKeyEncrypted))
            return null;

        return DecryptString(settings.NexusApiKeyEncrypted);
    }

    /// <summary>Set the API key (encrypts before storing).</summary>
    public void SetApiKey(AppSettings settings, string? apiKey)
    {
        settings.NexusApiKeyLegacy = null;
        settings.NexusApiKeyEncrypted = string.IsNullOrEmpty(apiKey) ? null : EncryptString(apiKey);
    }

    private static string EncryptString(string plaintext)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var bytes = Encoding.UTF8.GetBytes(plaintext);
            var encrypted = System.Security.Cryptography.ProtectedData.Protect(
                bytes, null, System.Security.Cryptography.DataProtectionScope.CurrentUser);
            return "dpapi:" + Convert.ToBase64String(encrypted);
        }
        // Non-Windows keeps the value in a user-read/write-only settings file. The prefix is
        // an encoding/version marker, not a claim of cryptographic protection.
        return "b64:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(plaintext));
    }

    private static string? DecryptString(string encrypted)
    {
        try
        {
            if (encrypted.StartsWith("dpapi:") && RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                var data = Convert.FromBase64String(encrypted[6..]);
                var decrypted = System.Security.Cryptography.ProtectedData.Unprotect(
                    data, null, System.Security.Cryptography.DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(decrypted);
            }
            if (encrypted.StartsWith("b64:"))
            {
                return Encoding.UTF8.GetString(Convert.FromBase64String(encrypted[4..]));
            }
            // Unrecognized format — treat as plaintext for backward compat
            return encrypted;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"DecryptString failed: {ex.Message}");
            return null;
        }
    }
}
