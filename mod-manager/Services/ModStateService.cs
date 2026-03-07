using System.IO;
using System.Text.Json;
using TerrariaModManager.Models;

namespace TerrariaModManager.Services;

public class ModStateService
{
    private readonly UpdateTracker _updateTracker;
    private readonly Logger _logger;

    public ModStateService(UpdateTracker updateTracker, Logger logger)
    {
        _updateTracker = updateTracker;
        _logger = logger;
    }

    public List<InstalledMod> ScanInstalledMods(string terrariaPath)
    {
        var mods = new List<InstalledMod>();
        var modsDir = Path.Combine(terrariaPath, "TerrariaModder", "mods");
        if (!Directory.Exists(modsDir)) return mods;

        foreach (var dir in Directory.GetDirectories(modsDir))
        {
            var folderName = Path.GetFileName(dir);
            if (folderName == "Libs" || folderName == "logs") continue;

            bool enabled = !folderName.StartsWith(".");
            string modId = enabled ? folderName : folderName[1..];

            var manifestPath = Path.Combine(dir, "manifest.json");
            if (!File.Exists(manifestPath)) continue;

            try
            {
                var json = File.ReadAllText(manifestPath);
                var manifest = JsonSerializer.Deserialize<ModManifest>(json);
                if (manifest == null) continue;

                mods.Add(new InstalledMod
                {
                    Id = manifest.Id,
                    Name = manifest.Name,
                    Version = manifest.Version,
                    Author = manifest.Author,
                    Description = manifest.Description,
                    EntryDll = manifest.EntryDll,
                    FolderPath = dir,
                    IsEnabled = enabled,
                    Manifest = manifest,
                    HasConfigFiles = HasModConfigFiles(dir)
                });
            }
            catch { }
        }

        // Add Core as a special entry at the top
        var coreInfo = GetCoreInfo(terrariaPath);
        if (coreInfo.IsInstalled)
        {
            // Prefer tracked Nexus version (stamped after download) over DLL version
            // to avoid format mismatches (e.g. DLL "1.2.0.0" vs Nexus "1.2.0")
            var coreVersion = _updateTracker.GetTrackedVersion("core")
                              ?? coreInfo.CoreVersion ?? "unknown";

            mods.Insert(0, new InstalledMod
            {
                Id = "core",
                Name = "TerrariaModder Core",
                Version = coreVersion,
                Author = "SixteenthBit",
                Description = "Core framework — required for all mods to work",
                FolderPath = Path.Combine(terrariaPath, "TerrariaModder", "core"),
                IsEnabled = true,
                IsCore = true
            });
        }

        return mods.OrderBy(m => m.IsCore ? 0 : 1).ThenBy(m => m.Name).ToList();
    }

    public CoreInstallInfo GetCoreInfo(string terrariaPath)
    {
        var coreDll = Path.Combine(terrariaPath, "TerrariaModder", "core", "TerrariaModder.Core.dll");
        var injector = Path.Combine(terrariaPath, "TerrariaInjector.exe");
        var modsDir = Path.Combine(terrariaPath, "TerrariaModder", "mods");

        var info = new CoreInstallInfo
        {
            InjectorPresent = File.Exists(injector),
            ModsFolderExists = Directory.Exists(modsDir)
        };

        if (File.Exists(coreDll))
        {
            info.IsInstalled = true;
            try
            {
                var fvi = System.Diagnostics.FileVersionInfo.GetVersionInfo(coreDll);
                var dllVersion = fvi.ProductVersion ?? fvi.FileVersion ?? "unknown";
                // Strip semver build metadata ("+<hash>" suffix) — Nexus appends commit info
                var plusIdx = dllVersion.IndexOf('+');
                if (plusIdx >= 0) dllVersion = dllVersion[..plusIdx];
                // Prefer the Nexus-stamped version (e.g. "0.1.0-hotfix") over the raw DLL version
                info.CoreVersion = _updateTracker.GetTrackedVersion("core") ?? dllVersion;
            }
            catch
            {
                info.CoreVersion = _updateTracker.GetTrackedVersion("core") ?? "unknown";
            }
        }

        return info;
    }

    public void EnableMod(string modId, string terrariaPath)
    {
        var modsDir = Path.Combine(terrariaPath, "TerrariaModder", "mods");
        var disabled = Path.Combine(modsDir, "." + modId);
        var enabled = Path.Combine(modsDir, modId);

        if (Directory.Exists(disabled) && !Directory.Exists(enabled))
            Directory.Move(disabled, enabled);
    }

    public void DisableMod(string modId, string terrariaPath)
    {
        var modsDir = Path.Combine(terrariaPath, "TerrariaModder", "mods");
        var enabled = Path.Combine(modsDir, modId);
        var disabled = Path.Combine(modsDir, "." + modId);

        if (Directory.Exists(enabled) && !Directory.Exists(disabled))
            Directory.Move(enabled, disabled);
    }

    private static readonly string[] ConfigExtensions =
        { ".json", ".cfg", ".ini", ".xml", ".config", ".txt", ".toml", ".yaml", ".yml" };

    public void UninstallMod(string modId, string terrariaPath, bool deleteSettings = true)
    {
        string? modDir;

        if (modId == "core")
        {
            // Core lives at TerrariaModder/core/, not in the mods/ folder
            var coreDir = Path.Combine(terrariaPath, "TerrariaModder", "core");
            modDir = Directory.Exists(coreDir) ? coreDir : null;
        }
        else
        {
            var modsDir = Path.Combine(terrariaPath, "TerrariaModder", "mods");

            // Try both enabled and disabled paths
            var enabled = Path.Combine(modsDir, modId);
            var disabled = Path.Combine(modsDir, "." + modId);

            modDir = Directory.Exists(enabled) ? enabled : Directory.Exists(disabled) ? disabled : null;
        }

        if (modDir == null) return;

        if (deleteSettings)
        {
            // Full delete — everything goes
            Directory.Delete(modDir, true);
        }
        else
        {
            // Keep config files, delete everything else
            DeleteNonConfigFiles(modDir);

            // If the folder is now empty (no config files existed), delete it
            if (!Directory.EnumerateFileSystemEntries(modDir).Any())
                Directory.Delete(modDir);
        }
    }

    private static bool HasModConfigFiles(string modDir)
    {
        foreach (var file in Directory.GetFiles(modDir))
        {
            var ext = Path.GetExtension(file).ToLowerInvariant();
            if (ConfigExtensions.Contains(ext)
                && !Path.GetFileName(file).Equals("manifest.json", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        var configDir = Path.Combine(modDir, "config");
        return Directory.Exists(configDir) && Directory.EnumerateFiles(configDir, "*", SearchOption.AllDirectories).Any();
    }

    private static readonly string[] AlwaysDeleteFiles = { "manifest.json" };

    private void DeleteNonConfigFiles(string dir)
    {
        foreach (var file in Directory.GetFiles(dir))
        {
            var fileName = Path.GetFileName(file);

            // Always delete manifest and DLLs — these aren't user settings
            if (AlwaysDeleteFiles.Contains(fileName, StringComparer.OrdinalIgnoreCase)
                || fileName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                || fileName.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(file);
                continue;
            }

            var ext = Path.GetExtension(file).ToLowerInvariant();
            if (!ConfigExtensions.Contains(ext))
                File.Delete(file);
        }

        foreach (var subDir in Directory.GetDirectories(dir))
        {
            var dirName = Path.GetFileName(subDir);
            // Keep config directories too (some mods store configs in subdirs)
            if (dirName.Equals("config", StringComparison.OrdinalIgnoreCase))
                continue;

            Directory.Delete(subDir, true);
        }
    }
}
