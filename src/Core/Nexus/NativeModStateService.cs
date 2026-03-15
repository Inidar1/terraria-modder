using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using TerrariaModder.Core.Config;
using TerrariaModder.Core.Logging;
using TerrariaModder.Core.Manifest;

namespace TerrariaModder.Core.Nexus
{
    internal sealed class NativeModStateService
    {
        private static readonly string[] ConfigExtensions = { ".json", ".cfg", ".ini", ".xml", ".config", ".txt", ".toml", ".yaml", ".yml" };
        private static readonly string[] AlwaysDeleteFiles = { "manifest.json" };

        private readonly NexusUpdateTracker _updateTracker;
        private readonly ILogger _log;

        public NativeModStateService(NexusUpdateTracker updateTracker, ILogger log = null)
        {
            _updateTracker = updateTracker;
            _log = log;
        }

        public List<InstalledModRecord> ScanInstalledMods()
        {
            var mods = new List<InstalledModRecord>();
            string modsDir = CoreConfig.Instance.ModsPath;
            var pendingDeletes = LoadPendingDeletes(_log);
            if (!Directory.Exists(modsDir))
                return mods;

            foreach (var dir in Directory.GetDirectories(modsDir))
            {
                string folderName = Path.GetFileName(dir);
                if (folderName == "Libs" || folderName == "logs")
                    continue;

                bool enabled = !folderName.StartsWith(".", StringComparison.Ordinal);
                string manifestPath = Path.Combine(dir, "manifest.json");
                if (!File.Exists(manifestPath))
                    continue;

                try
                {
                    ModManifest manifest = ManifestParser.Parse(manifestPath);
                    if (manifest == null || !manifest.IsValid)
                        continue;

                    mods.Add(new InstalledModRecord
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
                        HasConfigFiles = HasModConfigFiles(dir),
                        IsPendingDelete = IsPendingDelete(manifest.Id, dir, pendingDeletes),
                        PendingDeleteIncludesSettings = IncludesPendingSettingsDelete(manifest.Id, dir, pendingDeletes)
                    });
                }
                catch
                {
                }
            }

            var coreInfo = GetCoreInfo();
            if (coreInfo.IsInstalled)
            {
                string version = _updateTracker.GetTrackedVersion("core") ?? coreInfo.CoreVersion ?? "unknown";
                mods.Insert(0, new InstalledModRecord
                {
                    Id = "core",
                    Name = "TerrariaModder Core",
                    Version = version,
                    Author = "SixteenthBit",
                    Description = "Core framework - required for all mods to work",
                    FolderPath = CoreConfig.Instance.CorePath,
                    IsEnabled = true,
                    IsCore = true
                });
            }

            return mods.OrderBy(m => m.IsCore ? 0 : 1).ThenBy(m => m.Name).ToList();
        }

        public CoreInstallInfo GetCoreInfo()
        {
            string coreDll = Path.Combine(CoreConfig.Instance.CorePath, "TerrariaModder.Core.dll");
            string injector = Path.Combine(CoreConfig.Instance.GameFolder, "TerrariaInjector.exe");
            string modsDir = CoreConfig.Instance.ModsPath;

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
                    var fvi = FileVersionInfo.GetVersionInfo(coreDll);
                    info.CoreVersion = fvi.ProductVersion ?? fvi.FileVersion ?? "unknown";
                }
                catch
                {
                    info.CoreVersion = "unknown";
                }
            }

            return info;
        }

        public void EnableMod(string modId)
        {
            string disabled = Path.Combine(CoreConfig.Instance.ModsPath, "." + modId);
            string enabled = Path.Combine(CoreConfig.Instance.ModsPath, modId);
            if (Directory.Exists(disabled) && !Directory.Exists(enabled))
                Directory.Move(disabled, enabled);
        }

        public void DisableMod(string modId)
        {
            string enabled = Path.Combine(CoreConfig.Instance.ModsPath, modId);
            string disabled = Path.Combine(CoreConfig.Instance.ModsPath, "." + modId);
            if (Directory.Exists(enabled) && !Directory.Exists(disabled))
                Directory.Move(enabled, disabled);
        }

        public void UninstallMod(string modId, bool deleteSettings)
        {
            string modDir;
            if (string.Equals(modId, "core", StringComparison.OrdinalIgnoreCase))
            {
                modDir = CoreConfig.Instance.CorePath;
            }
            else
            {
                string enabled = Path.Combine(CoreConfig.Instance.ModsPath, modId);
                string disabled = Path.Combine(CoreConfig.Instance.ModsPath, "." + modId);
                modDir = Directory.Exists(enabled) ? enabled : Directory.Exists(disabled) ? disabled : null;
            }

            if (modDir == null || !Directory.Exists(modDir))
                return;

            QueuePendingDelete(modId, modDir, deleteSettings);
            LaunchDeferredDeleteWorker(modId, modDir, deleteSettings);
            _log?.Info("[Nexus] Queued mod uninstall for " + modId + " after process exit.");
        }

        public bool IsPendingDelete(string modId)
        {
            return LoadPendingDeletes(_log).Any(entry => string.Equals(entry.ModId, modId, StringComparison.OrdinalIgnoreCase));
        }

        public bool CancelPendingDelete(string modId)
        {
            string queuePath = GetPendingDeletesPath();
            var entries = LoadPendingDeletes(_log);
            int removed = entries.RemoveAll(entry => string.Equals(entry.ModId, modId, StringComparison.OrdinalIgnoreCase));
            if (removed > 0)
                SavePendingDeletes(queuePath, entries, _log);
            return removed > 0;
        }

        public bool HasModConfigFiles(string modDir)
        {
            if (string.IsNullOrEmpty(modDir) || !Directory.Exists(modDir))
                return false;

            foreach (var file in Directory.GetFiles(modDir))
            {
                string ext = Path.GetExtension(file).ToLowerInvariant();
                if (ConfigExtensions.Contains(ext) && !string.Equals(Path.GetFileName(file), "manifest.json", StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            string configDir = Path.Combine(modDir, "config");
            return Directory.Exists(configDir) && Directory.EnumerateFiles(configDir, "*", SearchOption.AllDirectories).Any();
        }

        private void QueuePendingDelete(string modId, string modDir, bool deleteSettings)
        {
            string queuePath = GetPendingDeletesPath();
            var entries = LoadPendingDeletes(_log);
            PendingDeleteEntry existing = entries.FirstOrDefault(entry =>
                string.Equals(entry.ModId, modId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(entry.DirectoryPath, modDir, StringComparison.OrdinalIgnoreCase));

            if (existing != null)
            {
                existing.DeleteSettings |= deleteSettings;
                existing.DirectoryPath = modDir;
            }
            else
            {
                entries.Add(new PendingDeleteEntry
                {
                    ModId = modId,
                    DirectoryPath = modDir,
                    DeleteSettings = deleteSettings
                });
            }

            SavePendingDeletes(queuePath, entries, _log);
        }

        private void LaunchDeferredDeleteWorker(string modId, string modDir, bool deleteSettings)
        {
            if (string.IsNullOrWhiteSpace(modDir) || !Directory.Exists(modDir))
                return;

            try
            {
                string escapedDir = EscapePowerShellLiteral(modDir);
                string escapedQueue = EscapePowerShellLiteral(GetPendingDeletesPath());
                string escapedModId = EscapePowerShellLiteral(modId ?? string.Empty);
                int currentPid = Process.GetCurrentProcess().Id;
                string deleteFlag = deleteSettings ? "$true" : "$false";

                string script = @"
$pidToWait = " + currentPid + @"
$targetDir = '" + escapedDir + @"'
$queuePath = '" + escapedQueue + @"'
$modId = '" + escapedModId + @"'
$deleteSettings = " + deleteFlag + @"
$configExts = @('.json','.cfg','.ini','.xml','.config','.txt','.toml','.yaml','.yml')
while (Get-Process -Id $pidToWait -ErrorAction SilentlyContinue) { Start-Sleep -Milliseconds 500 }
if (Test-Path -LiteralPath $targetDir) {
  if ($deleteSettings) {
    Remove-Item -LiteralPath $targetDir -Recurse -Force -ErrorAction SilentlyContinue
  } else {
    Get-ChildItem -LiteralPath $targetDir -Force -File -ErrorAction SilentlyContinue | ForEach-Object {
      $name = $_.Name
      $ext = [System.IO.Path]::GetExtension($name).ToLowerInvariant()
      if ($name -ieq 'manifest.json' -or $name.EndsWith('.dll',[System.StringComparison]::OrdinalIgnoreCase) -or $name.EndsWith('.pdb',[System.StringComparison]::OrdinalIgnoreCase)) {
        Remove-Item -LiteralPath $_.FullName -Force -ErrorAction SilentlyContinue
      } elseif ($configExts -notcontains $ext) {
        Remove-Item -LiteralPath $_.FullName -Force -ErrorAction SilentlyContinue
      }
    }
    Get-ChildItem -LiteralPath $targetDir -Force -Directory -ErrorAction SilentlyContinue | Where-Object { $_.Name -ine 'config' } | ForEach-Object {
      Remove-Item -LiteralPath $_.FullName -Recurse -Force -ErrorAction SilentlyContinue
    }
    if (-not (Get-ChildItem -LiteralPath $targetDir -Force -ErrorAction SilentlyContinue | Select-Object -First 1)) {
      Remove-Item -LiteralPath $targetDir -Force -ErrorAction SilentlyContinue
    }
  }
}
if (Test-Path -LiteralPath $queuePath) {
  try {
    $json = Get-Content -LiteralPath $queuePath -Raw | ConvertFrom-Json
    if ($json -isnot [System.Array]) { $json = @($json) }
    $remaining = @($json | Where-Object { $_.ModId -ne $modId })
    if ($remaining.Count -gt 0) {
      $remaining | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $queuePath
    } else {
      Remove-Item -LiteralPath $queuePath -Force -ErrorAction SilentlyContinue
    }
  } catch { }
}
";
                string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
                var startInfo = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = "-NoProfile -WindowStyle Hidden -EncodedCommand " + encoded,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                Process.Start(startInfo);
            }
            catch (Exception ex)
            {
                _log?.Warn("[Nexus] Failed to start deferred delete worker for " + modId + ": " + ex.Message);
            }
        }

        private static string EscapePowerShellLiteral(string value)
        {
            return (value ?? string.Empty).Replace("'", "''");
        }

        private static List<PendingDeleteEntry> LoadPendingDeletes(ILogger log)
        {
            string queuePath = GetPendingDeletesPath();
            try
            {
                if (!File.Exists(queuePath))
                    return new List<PendingDeleteEntry>();

                string json = File.ReadAllText(queuePath);
                return JsonSerializer.Deserialize<List<PendingDeleteEntry>>(json) ?? new List<PendingDeleteEntry>();
            }
            catch (Exception ex)
            {
                log?.Warn("[Nexus] Failed to load pending delete queue: " + ex.Message);
                return new List<PendingDeleteEntry>();
            }
        }

        private static void SavePendingDeletes(string queuePath, List<PendingDeleteEntry> entries, ILogger log)
        {
            try
            {
                if (entries == null || entries.Count == 0)
                {
                    if (File.Exists(queuePath))
                        File.Delete(queuePath);
                    return;
                }

                string json = JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(queuePath, json);
            }
            catch (Exception ex)
            {
                log?.Warn("[Nexus] Failed to save pending delete queue: " + ex.Message);
            }
        }

        private static string GetPendingDeletesPath()
        {
            return Path.Combine(CoreConfig.Instance.CorePath, "pending-mod-deletes.json");
        }

        private static bool IsPendingDelete(string modId, string dir, List<PendingDeleteEntry> pendingDeletes)
        {
            return pendingDeletes.Any(entry =>
                string.Equals(entry.ModId, modId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(entry.DirectoryPath, dir, StringComparison.OrdinalIgnoreCase));
        }

        private static bool IncludesPendingSettingsDelete(string modId, string dir, List<PendingDeleteEntry> pendingDeletes)
        {
            PendingDeleteEntry entry = pendingDeletes.FirstOrDefault(item =>
                string.Equals(item.ModId, modId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(item.DirectoryPath, dir, StringComparison.OrdinalIgnoreCase));
            return entry != null && entry.DeleteSettings;
        }

        private sealed class PendingDeleteEntry
        {
            public string ModId { get; set; }
            public string DirectoryPath { get; set; }
            public bool DeleteSettings { get; set; }
        }
    }

    internal sealed class CoreInstallInfo
    {
        public bool IsInstalled { get; set; }
        public bool InjectorPresent { get; set; }
        public bool ModsFolderExists { get; set; }
        public string CoreVersion { get; set; }
    }
}
