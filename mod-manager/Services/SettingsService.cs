using System.IO;
using System.Text.Json;
using TerrariaModManager.Models;

namespace TerrariaModManager.Services;

public class SettingsService
{
    private readonly string _appDataDir;
    private readonly string _settingsPath;
    private readonly JsonSerializerOptions _jsonOpts = new()
    {
        WriteIndented = true
    };

    public SettingsService()
    {
        _appDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TerrariaModManager");
        _settingsPath = Path.Combine(_appDataDir, "settings.json");
    }

    public string DownloadsDir => Path.Combine(_appDataDir, "downloads");
    public string CacheDir => Path.Combine(_appDataDir, "cache");

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
        }
        catch { }
    }

    public void EnsureDirectories()
    {
        Directory.CreateDirectory(_appDataDir);
        Directory.CreateDirectory(DownloadsDir);
        Directory.CreateDirectory(CacheDir);
    }
}
