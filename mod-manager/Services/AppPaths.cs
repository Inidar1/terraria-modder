using System.Runtime.InteropServices;

namespace TerrariaModManager.Services;

/// <summary>
/// Owns every writable application path and process-scoped identity. Production uses
/// the normal user locations; tests and isolated sessions inject alternate roots.
/// </summary>
public sealed class AppPaths
{
    public AppPaths(
        string? appDataDirectory = null,
        string? tempDirectory = null,
        string? sessionId = null,
        bool isIsolated = false)
    {
        AppDataDirectory = Path.GetFullPath(appDataDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TerrariaModManager"));
        TempDirectory = Path.GetFullPath(tempDirectory ?? Path.Combine(
            Path.GetTempPath(), "TerrariaModderVault"));
        SessionId = string.IsNullOrWhiteSpace(sessionId) ? "production" : sessionId;
        IsIsolated = isIsolated;
    }

    public string AppDataDirectory { get; }
    public string TempDirectory { get; }
    public string SessionId { get; }
    public bool IsIsolated { get; }

    public string SettingsFile => Path.Combine(AppDataDirectory, "settings.json");
    public string DownloadsDirectory => Path.Combine(AppDataDirectory, "downloads");
    public string CacheDirectory => Path.Combine(AppDataDirectory, "cache");
    public string LogFile => Path.Combine(AppDataDirectory, "app.log");
    public string WebViewDirectory => Path.Combine(AppDataDirectory, "WebView2");
    public string NexusMapFile => Path.Combine(AppDataDirectory, "nexus-mod-map.json");
    public string InstalledVersionsFile => Path.Combine(AppDataDirectory, "installed-versions.json");
    public string BrowserStagingDirectory => Path.Combine(TempDirectory, "browser-staging");
    public string UpdateDirectory => Path.Combine(TempDirectory, "updates");
    public string UpdateLogFile => Path.Combine(TempDirectory, "updater.log");

    public string SingleInstanceName(string baseName)
    {
        if (!IsIsolated)
            return baseName;

        var safe = new string(SessionId.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
        return $"{baseName}_{safe}";
    }

    public void EnsureApplicationDirectories()
    {
        Directory.CreateDirectory(AppDataDirectory);
        Directory.CreateDirectory(DownloadsDirectory);
        Directory.CreateDirectory(CacheDirectory);
        Directory.CreateDirectory(TempDirectory);
    }

    public static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
}
