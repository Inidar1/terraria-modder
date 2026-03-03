using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using TerrariaModder.Core.Logging;

namespace Randomizer
{
    /// <summary>
    /// Manages per-world randomizer state for world-gen modules.
    /// Stores "armed" settings (for next world creation) and per-world locked settings.
    /// </summary>
    public class WorldGenState
    {
        private readonly ILogger _log;
        private readonly string _modFolder;
        private readonly string _armedPath;
        private readonly string _worldsFolder;

        // Armed state: what to apply to the next world
        private int _armedSeed;
        private readonly Dictionary<string, bool> _armedModules = new Dictionary<string, bool>();

        // Current world state
        private string _currentWorldName;
        private int _worldSeed;
        private readonly HashSet<string> _lockedModules = new HashSet<string>();

        public bool HasArmedSettings => _armedSeed != 0 || _armedModules.Count > 0;
        public int ArmedSeed => _armedSeed;
        public bool HasLockedModules => _lockedModules.Count > 0;
        public int WorldSeed => _worldSeed;

        public WorldGenState(ILogger log, string modFolder)
        {
            _log = log;
            _modFolder = modFolder;
            _armedPath = Path.Combine(modFolder, "state-worldgen.json");
            _worldsFolder = Path.Combine(modFolder, "worlds");
            LoadArmed();
        }

        /// <summary>Whether a specific module is armed for next world creation.</summary>
        public bool IsArmed(string moduleId)
        {
            return _armedModules.TryGetValue(moduleId, out bool v) && v;
        }

        /// <summary>Set armed state for a module.</summary>
        public void SetArmed(string moduleId, bool armed)
        {
            _armedModules[moduleId] = armed;
            SaveArmed();
        }

        /// <summary>Set the armed seed.</summary>
        public void SetArmedSeed(int seed)
        {
            _armedSeed = seed;
            SaveArmed();
        }

        /// <summary>Whether a module is locked for the current world.</summary>
        public bool IsLocked(string moduleId)
        {
            return _lockedModules.Contains(moduleId);
        }

        /// <summary>
        /// Called on world load. Checks for per-world state or applies armed settings.
        /// Returns the seed to use for world-gen modules (0 if none).
        /// </summary>
        public int OnWorldLoad(string worldName)
        {
            _currentWorldName = SanitizeFileName(worldName);
            _lockedModules.Clear();
            _worldSeed = 0;

            string worldFile = GetWorldFilePath(_currentWorldName);

            if (File.Exists(worldFile))
            {
                // Returning to a world that was already tagged
                LoadWorldState(worldFile);
                _log.Info($"[Randomizer] Loaded world-gen state for '{worldName}': seed={_worldSeed}, modules={string.Join(",", _lockedModules)}");
                return _worldSeed;
            }

            // Check if armed settings should apply
            if (HasArmedSettings)
            {
                _worldSeed = _armedSeed;
                foreach (var kvp in _armedModules)
                {
                    if (kvp.Value) _lockedModules.Add(kvp.Key);
                }

                // Save to per-world file
                if (_lockedModules.Count > 0)
                {
                    SaveWorldState(worldFile);
                    _log.Info($"[Randomizer] Applied world-gen settings to '{worldName}': seed={_worldSeed}, modules={string.Join(",", _lockedModules)}");
                }

                return _worldSeed;
            }

            return 0;
        }

        /// <summary>Called on world unload.</summary>
        public void OnWorldUnload()
        {
            _currentWorldName = null;
            _lockedModules.Clear();
            _worldSeed = 0;
        }

        private string GetWorldFilePath(string sanitizedName)
        {
            return Path.Combine(_worldsFolder, sanitizedName + ".json");
        }

        private static string SanitizeFileName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "unknown";
            foreach (char c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return name;
        }

        #region Armed State Persistence

        private void SaveArmed()
        {
            try
            {
                var entries = new List<string>();
                if (_armedSeed != 0)
                    entries.Add($"  \"seed\": {_armedSeed}");
                foreach (var kvp in _armedModules)
                {
                    if (kvp.Value)
                        entries.Add($"  \"{kvp.Key}\": true");
                }

                string json = "{\n" + string.Join(",\n", entries) + "\n}";
                File.WriteAllText(_armedPath, json);
            }
            catch (Exception ex)
            {
                _log.Error($"[Randomizer] Failed to save armed state: {ex.Message}");
            }
        }

        private void LoadArmed()
        {
            if (!File.Exists(_armedPath)) return;
            try
            {
                string json = File.ReadAllText(_armedPath);
                ParseJson(json, out _armedSeed, _armedModules);
                _log.Info("[Randomizer] Loaded armed world-gen state");
            }
            catch (Exception ex)
            {
                _log.Error($"[Randomizer] Failed to load armed state: {ex.Message}");
            }
        }

        #endregion

        #region Per-World State Persistence

        private void SaveWorldState(string path)
        {
            try
            {
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                var entries = new List<string>();
                if (_worldSeed != 0)
                    entries.Add($"  \"seed\": {_worldSeed}");
                foreach (string moduleId in _lockedModules)
                    entries.Add($"  \"{moduleId}\": true");

                string json = "{\n" + string.Join(",\n", entries) + "\n}";
                File.WriteAllText(path, json);
            }
            catch (Exception ex)
            {
                _log.Error($"[Randomizer] Failed to save world state: {ex.Message}");
            }
        }

        private void LoadWorldState(string path)
        {
            try
            {
                string json = File.ReadAllText(path);
                var modules = new Dictionary<string, bool>();
                ParseJson(json, out _worldSeed, modules);
                foreach (var kvp in modules)
                {
                    if (kvp.Value) _lockedModules.Add(kvp.Key);
                }
            }
            catch (Exception ex)
            {
                _log.Error($"[Randomizer] Failed to load world state: {ex.Message}");
            }
        }

        #endregion

        /// <summary>Regex-based JSON parser (no dependency on JSON library).</summary>
        private static void ParseJson(string json, out int seed, Dictionary<string, bool> modules)
        {
            seed = 0;
            var matches = Regex.Matches(json, @"""(\w+)""\s*:\s*(\w+)");
            foreach (Match m in matches)
            {
                string key = m.Groups[1].Value;
                string val = m.Groups[2].Value.ToLower();

                if (key == "seed")
                {
                    int.TryParse(val, out seed);
                }
                else if (val == "true")
                {
                    modules[key] = true;
                }
            }
        }
    }
}
