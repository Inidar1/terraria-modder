using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using TerrariaModder.Core.Config;
using TerrariaModder.Core.Manifest;

namespace TerrariaModder.Core.Nexus
{
    internal sealed class NativeModStateService
    {
        private static readonly string[] ConfigExtensions = { ".json", ".cfg", ".ini", ".xml", ".config", ".txt", ".toml", ".yaml", ".yml" };
        private static readonly string[] AlwaysDeleteFiles = { "manifest.json" };

        private readonly NexusUpdateTracker _updateTracker;

        public NativeModStateService(NexusUpdateTracker updateTracker)
        {
            _updateTracker = updateTracker;
        }

        public List<InstalledModRecord> ScanInstalledMods()
        {
            var mods = new List<InstalledModRecord>();
            string modsDir = CoreConfig.Instance.ModsPath;
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
                        HasConfigFiles = HasModConfigFiles(dir)
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

            if (deleteSettings)
            {
                Directory.Delete(modDir, true);
                return;
            }

            DeleteNonConfigFiles(modDir);
            if (!Directory.EnumerateFileSystemEntries(modDir).Any())
                Directory.Delete(modDir);
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

        private void DeleteNonConfigFiles(string dir)
        {
            foreach (var file in Directory.GetFiles(dir))
            {
                string fileName = Path.GetFileName(file);
                if (AlwaysDeleteFiles.Contains(fileName, StringComparer.OrdinalIgnoreCase)
                    || fileName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                    || fileName.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(file);
                    continue;
                }

                string ext = Path.GetExtension(file).ToLowerInvariant();
                if (!ConfigExtensions.Contains(ext))
                    File.Delete(file);
            }

            foreach (var subDir in Directory.GetDirectories(dir))
            {
                if (string.Equals(Path.GetFileName(subDir), "config", StringComparison.OrdinalIgnoreCase))
                    continue;

                Directory.Delete(subDir, true);
            }
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
