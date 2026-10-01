using System.IO;
using System.Text.Json;
using TerrariaModManager.Models;

namespace TerrariaModManager.Services;

public class ModStateService
{
    private readonly UpdateTracker _updateTracker;
    private readonly Logger _logger;
    private readonly List<string> _lastScanWarnings = new();

    public IReadOnlyList<string> LastScanWarnings => _lastScanWarnings;

    public ModStateService(UpdateTracker updateTracker, Logger logger)
    {
        _updateTracker = updateTracker;
        _logger = logger;
    }

    public List<InstalledMod> ScanInstalledMods(string terrariaPath)
    {
        var mods = new List<InstalledMod>();
        var modsDir = Path.Combine(terrariaPath, "TerrariaModder", "mods");
        _lastScanWarnings.Clear();

        var centralConfigsDir = Path.Combine(terrariaPath, "TerrariaModder", "core", "configs");

        foreach (var dir in Directory.Exists(modsDir) ? Directory.GetDirectories(modsDir) : [])
        {
            var folderName = Path.GetFileName(dir);
            if (folderName == "Libs" || folderName == "logs") continue;

            bool enabled = !folderName.StartsWith(".");
            string modId = enabled ? folderName : folderName[1..];

            var manifestPath = Path.Combine(dir, "manifest.json");
            if (!File.Exists(manifestPath))
            {
                WarnScan($"Installed folder '{folderName}' has no manifest.json and is not listed.");
                continue;
            }

            try
            {
                var json = File.ReadAllText(manifestPath);
                var manifest = JsonSerializer.Deserialize<ModManifest>(json);
                if (manifest == null || string.IsNullOrWhiteSpace(manifest.Id))
                {
                    WarnScan($"Installed folder '{folderName}' has an empty or ID-less manifest and is not listed.");
                    continue;
                }

                if (!string.Equals(manifest.Id, modId, StringComparison.OrdinalIgnoreCase))
                    WarnScan($"Installed folder '{folderName}' contains mod '{manifest.Id}'; operations will resolve it by manifest ID.");

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
                    HasConfigFiles = HasModConfigFiles(dir, centralConfigsDir, manifest.Id)
                });
            }
            catch (Exception ex)
            {
                WarnScan($"Installed folder '{folderName}' has an unreadable manifest and is not listed: {ex.Message}");
            }
        }

        foreach (var duplicate in mods.GroupBy(mod => mod.Id, StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1))
            WarnScan($"Multiple installed folders claim mod ID '{duplicate.Key}'; resolve the duplicate before changing it.");

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

        return mods.OrderBy(m => m.IsCore ? 0 : 1)
            .ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(m => m.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private void WarnScan(string message)
    {
        _lastScanWarnings.Add(message);
        _logger.Warn($"Installed scan: {message}");
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
        var disabled = ResolveModDirectory(modId, modsDir, enabled: false)
            ?? throw new InvalidOperationException($"Disabled mod '{modId}' is not installed.");
        var folderName = Path.GetFileName(disabled).TrimStart('.');
        var enabled = Path.Combine(modsDir, folderName);
        if (Directory.Exists(enabled))
            throw new InvalidOperationException($"Cannot enable '{modId}' because both enabled and disabled folders exist.");
        Directory.Move(disabled, enabled);
    }

    public void DisableMod(string modId, string terrariaPath)
    {
        var modsDir = Path.Combine(terrariaPath, "TerrariaModder", "mods");
        var enabled = ResolveModDirectory(modId, modsDir, enabled: true)
            ?? throw new InvalidOperationException($"Enabled mod '{modId}' is not installed.");
        var disabled = Path.Combine(modsDir, "." + Path.GetFileName(enabled));
        if (Directory.Exists(disabled))
            throw new InvalidOperationException($"Cannot disable '{modId}' because both enabled and disabled folders exist.");
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
            modDir = ResolveModDirectory(modId, modsDir, enabled: true)
                ?? ResolveModDirectory(modId, modsDir, enabled: false);
        }

        if (modDir == null) return;

        if (deleteSettings)
        {
            // Full delete — everything goes
            Directory.Delete(modDir, true);

            // Also clean up centralized config files (scoped format: .client.json / .server.json)
            var configsDir = Path.Combine(terrariaPath, "TerrariaModder", "core", "configs");
            foreach (var suffix in new[] { ".client.json", ".server.json" })
            {
                var path = Path.Combine(configsDir, modId + suffix);
                if (File.Exists(path))
                    try { File.Delete(path); } catch { }
            }
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

    private static string? ResolveModDirectory(string modId, string modsDir, bool enabled)
    {
        if (!Directory.Exists(modsDir)) return null;
        var exact = Path.Combine(modsDir, enabled ? modId : "." + modId);
        if (Directory.Exists(exact)) return exact;

        var matches = new List<string>();
        foreach (var directory in Directory.GetDirectories(modsDir))
        {
            var name = Path.GetFileName(directory);
            if ((!name.StartsWith('.')) != enabled) continue;
            var manifestPath = Path.Combine(directory, "manifest.json");
            if (!File.Exists(manifestPath)) continue;
            try
            {
                var manifest = JsonSerializer.Deserialize<ModManifest>(File.ReadAllText(manifestPath));
                if (string.Equals(manifest?.Id, modId, StringComparison.OrdinalIgnoreCase))
                    matches.Add(directory);
            }
            catch { }
        }

        return matches.Count switch
        {
            0 => null,
            1 => matches[0],
            _ => throw new InvalidOperationException($"Multiple {(enabled ? "enabled" : "disabled")} folders claim mod ID '{modId}'.")
        };
    }

    private static bool HasModConfigFiles(string modDir, string configsDir, string modId)
    {
        // Check centralized scoped config files (current format: .client.json / .server.json)
        foreach (var suffix in new[] { ".client.json", ".server.json" })
        {
            if (File.Exists(Path.Combine(configsDir, modId + suffix)))
                return true;
        }

        // Check mod folder for any user data files (configs, save data, presets, any format)
        foreach (var file in Directory.GetFiles(modDir))
        {
            var name = Path.GetFileName(file);
            var ext = Path.GetExtension(file).ToLowerInvariant();
            // Skip build artifacts — everything else is user data
            if (name.Equals("manifest.json", StringComparison.OrdinalIgnoreCase)) continue;
            if (ext is ".dll" or ".pdb") continue;
            return true;
        }

        // Check ALL subdirectories for any files
        return Directory.GetDirectories(modDir).Any(subDir =>
            Directory.EnumerateFiles(subDir, "*", SearchOption.AllDirectories).Any());
    }

    private static readonly string[] AlwaysDeleteFiles = { "manifest.json" };

    private void DeleteNonConfigFiles(string dir)
    {
        // Only delete build artifacts (manifest, DLLs, PDBs).
        // Everything else is user data (configs, save data, presets, custom files of any format).
        foreach (var file in Directory.GetFiles(dir))
        {
            var fileName = Path.GetFileName(file);
            var ext = Path.GetExtension(file).ToLowerInvariant();

            if (AlwaysDeleteFiles.Contains(fileName, StringComparer.OrdinalIgnoreCase)
                || ext is ".dll" or ".pdb")
            {
                File.Delete(file);
            }
        }

        // Keep all subdirectories — they contain user data (worlds/, characters/, etc.)
        // Only delete empty subdirectories left behind after artifact removal
        foreach (var subDir in Directory.GetDirectories(dir))
        {
            bool hasAnyFiles = Directory.EnumerateFiles(subDir, "*", SearchOption.AllDirectories).Any();
            if (!hasAnyFiles)
                Directory.Delete(subDir, true);
        }
    }
}
