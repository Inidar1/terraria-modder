using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using TerrariaModder.Core.Config;
using TerrariaModder.Core.Conflicts;
using TerrariaModder.Core.Input;
using TerrariaModder.Core.Logging;
using TerrariaModder.Core.Manifest;

namespace TerrariaModder.Core.UI
{
    internal sealed class NativeModsService
    {
        private readonly ILogger _log;
        private ConflictReport _conflictReport;

        public NativeModsService(ILogger log)
        {
            _log = log;
            ConflictScanner.Initialize(log);
        }

        public IReadOnlyList<ModInfo> GetMods() => PluginLoader.Mods;

        public IEnumerable<Keybind> GetKeybinds(string modId)
            => KeybindManager.GetKeybindsForMod(modId).OrderBy(k => k.Label);

        public List<LogEntry> GetRecentLogs(int count = 50) => LogManager.GetRecentLogs(count);

        public ConflictReport GetConflictReport(bool forceRefresh = false)
        {
            if (_conflictReport == null || forceRefresh)
                _conflictReport = ConflictScanner.Scan();
            return _conflictReport;
        }

        public List<string> GetEditableLoadOrder()
        {
            var saved = DependencyResolver.LoadUserOrder(CoreConfig.Instance.CorePath);
            var current = PluginLoader.Mods.Select(m => m.Manifest.Id).ToList();
            if (saved == null || saved.Count == 0)
                return current;

            var merged = new List<string>();
            foreach (var modId in saved)
            {
                if (current.Contains(modId) && !merged.Contains(modId))
                    merged.Add(modId);
            }

            foreach (var modId in current)
            {
                if (!merged.Contains(modId))
                    merged.Add(modId);
            }

            return merged;
        }

        public bool MoveLoadOrderEntry(List<string> order, int fromIndex, int toIndex, out string error)
        {
            error = null;

            if (fromIndex < 0 || fromIndex >= order.Count || toIndex < 0 || toIndex >= order.Count)
            {
                error = "Invalid load order index.";
                return false;
            }

            if (fromIndex == toIndex)
                return true;

            string movingModId = order[fromIndex];
            var movingMod = PluginLoader.Mods.FirstOrDefault(m => m.Manifest.Id == movingModId);
            if (movingMod == null)
            {
                error = "Mod not found.";
                return false;
            }

            if (toIndex < fromIndex)
            {
                foreach (var dependency in movingMod.Manifest.Dependencies)
                {
                    int dependencyIndex = order.IndexOf(dependency);
                    if (dependencyIndex >= 0 && dependencyIndex >= toIndex)
                    {
                        error = $"{movingMod.Manifest.Name} cannot be moved before dependency {dependency}.";
                        return false;
                    }
                }
            }
            else
            {
                for (int i = fromIndex + 1; i <= toIndex; i++)
                {
                    string otherModId = order[i];
                    var otherMod = PluginLoader.Mods.FirstOrDefault(m => m.Manifest.Id == otherModId);
                    if (otherMod != null && otherMod.Manifest.Dependencies.Contains(movingModId))
                    {
                        error = $"{movingMod.Manifest.Name} cannot be moved after dependent {otherMod.Manifest.Name}.";
                        return false;
                    }
                }
            }

            string item = order[fromIndex];
            order.RemoveAt(fromIndex);
            order.Insert(toIndex, item);
            return true;
        }

        public void SaveLoadOrder(List<string> order)
        {
            DependencyResolver.SaveLoadOrder(CoreConfig.Instance.CorePath, order);
            _log?.Info($"[NativeMods] Saved load order: {string.Join(", ", order)}");
            _conflictReport = null;
        }

        public bool ModSupportsHotReload(ModInfo mod)
        {
            if (mod?.Instance == null) return false;

            try
            {
                return mod.Instance.GetType().GetMethod("OnConfigChanged",
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly) != null;
            }
            catch
            {
                return false;
            }
        }

        public bool ModNeedsRestart(ModInfo mod)
        {
            if (mod == null) return false;

            bool configChanged = (mod.Context?.Config as ModConfig)?.HasChangesFromBaseline() ?? false;
            bool keybindChanged = KeybindManager.HasKeybindChangesFromBaseline(mod.Manifest.Id);
            return !ModSupportsHotReload(mod) && (configChanged || keybindChanged);
        }

        public void ResetConfigToDefaults(ModInfo mod)
        {
            if (mod?.Context?.Config == null) return;
            mod.Context.Config.ResetToDefaults();
            AutoSaveConfig(mod.Manifest.Id);
        }

        public void SetConfigValue(ModInfo mod, ConfigField field, object value)
        {
            if (mod?.Context?.Config == null || field == null) return;
            mod.Context.Config.Set(field.Key, value);
            AutoSaveConfig(mod.Manifest.Id);
        }

        public void AutoSaveConfig(string modId)
        {
            try
            {
                var mod = PluginLoader.Mods.FirstOrDefault(m => m.Manifest.Id == modId);
                if (mod?.Context?.Config == null) return;

                mod.Context.Config.Save();
                if (ModSupportsHotReload(mod))
                    CallOnConfigChanged(mod);
            }
            catch (Exception ex)
            {
                _log?.Error($"[NativeMods] AutoSaveConfig failed for {modId}: {ex.Message}");
            }
        }

        public void SetKeybind(Keybind keybind, KeyCombo combo)
        {
            if (keybind != null)
                KeybindManager.SetBinding(keybind.Id, combo);
        }

        public void ResetKeybind(Keybind keybind)
        {
            if (keybind != null)
                KeybindManager.ResetToDefault(keybind.Id);
        }

        private void CallOnConfigChanged(ModInfo mod)
        {
            try
            {
                var method = mod.Instance.GetType().GetMethod("OnConfigChanged",
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                method?.Invoke(mod.Instance, null);
            }
            catch (Exception ex)
            {
                _log?.Error($"[NativeMods] OnConfigChanged failed for {mod.Manifest.Id}: {ex.Message}");
            }
        }
    }
}
