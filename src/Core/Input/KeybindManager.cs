using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using TerrariaModder.Core.Config;
using TerrariaModder.Core.Logging;
using TerrariaModder.Core.Reflection;

namespace TerrariaModder.Core.Input
{
    /// <summary>
    /// Conflict between two keybinds.
    /// </summary>
    public class KeybindConflict
    {
        public Keybind Keybind1 { get; set; }
        public Keybind Keybind2 { get; set; }
        public KeyCombo ConflictingKey { get; set; }
    }

    /// <summary>
    /// Central keybind registry and manager.
    /// </summary>
    public static class KeybindManager
    {
        private static readonly List<Keybind> _keybinds = new List<Keybind>();
        private static readonly Dictionary<string, Keybind> _keybindsById = new Dictionary<string, Keybind>();
        private static readonly Dictionary<string, string> _savedBindings = new Dictionary<string, string>();
        private static readonly Dictionary<string, string> _baselineBindings = new Dictionary<string, string>(); // Snapshot at registration for restart-required tracking
        private static ILogger _log;
        private static bool _enabled = true;
        private static string _keybindsFilePath;
        private static bool _loaded;

        /// <summary>All registered keybinds.</summary>
        public static IReadOnlyList<Keybind> Keybinds => _keybinds.AsReadOnly();

        /// <summary>Enable/disable all keybind processing.</summary>
        public static bool Enabled
        {
            get => _enabled;
            set => _enabled = value;
        }

        internal static void Initialize(ILogger logger)
        {
            _log = logger;

            // Set up keybinds file path in core folder
            _keybindsFilePath = Path.Combine(CoreConfig.Instance.CorePath, "keybinds.json");

            // Load saved bindings
            LoadBindings();
        }

        /// <summary>
        /// Register an internal (core framework) keybind.
        /// </summary>
        internal static Keybind RegisterInternal(string keybindId, string label, string description, string defaultKey, Action callback)
        {
            return Register("core", keybindId, label, description, defaultKey, callback);
        }

        /// <summary>
        /// Get all registered keybinds.
        /// </summary>
        public static IReadOnlyList<Keybind> GetAllKeybinds()
        {
            return _keybinds.AsReadOnly();
        }

        /// <summary>
        /// Register a keybind.
        /// </summary>
        public static Keybind Register(string modId, string keybindId, string label, string description, string defaultKey, Action callback)
        {
            var fullId = $"{modId}.{keybindId}";

            if (_keybindsById.ContainsKey(fullId))
            {
                _log?.Warn($"Keybind already registered: {fullId}");
                return _keybindsById[fullId];
            }

            var keyCombo = KeyCombo.Parse(defaultKey);
            if (keyCombo.Key == KeyCode.None && !string.IsNullOrWhiteSpace(defaultKey))
            {
                _log?.Warn($"Keybind {fullId}: invalid default key '{defaultKey}' - keybind will be unbound");
            }

            var keybind = new Keybind(fullId, modId, label, description, keyCombo, callback, _log);

            // Apply saved binding if one exists
            ApplySavedBinding(keybind);

            // Snapshot baseline for restart-required tracking
            _baselineBindings[fullId] = keybind.CurrentKey.ToString();

            _keybinds.Add(keybind);
            _keybindsById[fullId] = keybind;

            _log?.Info($"Registered keybind: {fullId} = {keybind.CurrentKey}");

            return keybind;
        }

        /// <summary>
        /// Register a keybind with KeyCombo.
        /// </summary>
        public static Keybind Register(string modId, string keybindId, string label, string description, KeyCombo defaultKey, Action callback)
        {
            var fullId = $"{modId}.{keybindId}";

            if (_keybindsById.ContainsKey(fullId))
            {
                _log?.Warn($"Keybind already registered: {fullId}");
                return _keybindsById[fullId];
            }

            var keybind = new Keybind(fullId, modId, label, description, defaultKey, callback, _log);

            // Apply saved binding if one exists
            ApplySavedBinding(keybind);

            // Snapshot baseline for restart-required tracking
            _baselineBindings[fullId] = keybind.CurrentKey.ToString();

            _keybinds.Add(keybind);
            _keybindsById[fullId] = keybind;

            _log?.Debug($"Registered keybind: {fullId} = {keybind.CurrentKey}");

            return keybind;
        }

        /// <summary>
        /// Get a keybind by ID.
        /// </summary>
        public static Keybind GetKeybind(string fullId)
        {
            _keybindsById.TryGetValue(fullId, out var keybind);
            return keybind;
        }

        /// <summary>
        /// Get all keybinds for a mod.
        /// </summary>
        public static IEnumerable<Keybind> GetKeybindsForMod(string modId)
        {
            return _keybinds.Where(k => k.ModId == modId);
        }

        /// <summary>
        /// Check if any keybinds for a mod have changed from baseline (startup values).
        /// Used to determine if restart is required for mods without hot reload.
        /// </summary>
        public static bool HasKeybindChangesFromBaseline(string modId)
        {
            foreach (var keybind in _keybinds.Where(k => k.ModId == modId))
            {
                if (_baselineBindings.TryGetValue(keybind.Id, out var baseline))
                {
                    if (keybind.CurrentKey.ToString() != baseline)
                        return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Set a new binding for a keybind.
        /// </summary>
        public static void SetBinding(string fullId, KeyCombo newKey)
        {
            if (_keybindsById.TryGetValue(fullId, out var keybind))
            {
                keybind.CurrentKey = newKey?.Clone() ?? new KeyCombo();
                _log?.Info($"Keybind {fullId} rebound to {keybind.CurrentKey}");

                // Save to persistent storage
                _savedBindings[fullId] = keybind.CurrentKey.ToString();
                SaveBindings();
            }
        }

        /// <summary>
        /// Reset a keybind to its default.
        /// </summary>
        public static void ResetToDefault(string fullId)
        {
            if (_keybindsById.TryGetValue(fullId, out var keybind))
            {
                keybind.ResetToDefault();
                _log?.Info($"Keybind {fullId} reset to default: {keybind.CurrentKey}");

                // Remove from saved bindings (use default)
                _savedBindings.Remove(fullId);
                SaveBindings();
            }
        }

        /// <summary>
        /// Load saved keybind overrides from disk.
        /// </summary>
        private static void LoadBindings()
        {
            if (_loaded) return;
            _loaded = true;

            try
            {
                if (File.Exists(_keybindsFilePath))
                {
                    string json = File.ReadAllText(_keybindsFilePath);
                    ParseKeybindsJson(json);
                    _log?.Info($"Loaded {_savedBindings.Count} saved keybinds");
                }
            }
            catch (Exception ex)
            {
                _log?.Warn($"Failed to load keybinds: {ex.Message}");
            }
        }

        /// <summary>
        /// Save keybind overrides to disk.
        /// </summary>
        private static void SaveBindings()
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("{");

                int count = 0;
                foreach (var kvp in _savedBindings)
                {
                    count++;
                    string comma = count < _savedBindings.Count ? "," : "";
                    sb.AppendLine($"  \"{kvp.Key}\": \"{kvp.Value}\"{comma}");
                }

                sb.AppendLine("}");

                File.WriteAllText(_keybindsFilePath, sb.ToString());
                _log?.Debug("Keybinds saved");
            }
            catch (Exception ex)
            {
                _log?.Error($"Failed to save keybinds: {ex.Message}");
            }
        }

        /// <summary>
        /// Parse keybinds JSON file.
        /// </summary>
        private static void ParseKeybindsJson(string json)
        {
            _savedBindings.Clear();

            // Simple JSON parser for flat key-value pairs
            var pattern = @"""([^""]+)""\s*:\s*""([^""]*)""";
            var matches = Regex.Matches(json, pattern);

            foreach (Match match in matches)
            {
                string key = match.Groups[1].Value;
                string value = match.Groups[2].Value;
                _savedBindings[key] = value;
            }
        }

        /// <summary>
        /// Apply saved binding to a keybind if one exists.
        /// </summary>
        private static void ApplySavedBinding(Keybind keybind)
        {
            if (_savedBindings.TryGetValue(keybind.Id, out string savedKey))
            {
                try
                {
                    var keyCombo = KeyCombo.Parse(savedKey);
                    if (keyCombo.Key == KeyCode.None && !string.IsNullOrWhiteSpace(savedKey))
                    {
                        _log?.Warn($"Saved keybind {keybind.Id} has unrecognized key '{savedKey}' in keybinds.json - using default instead");
                        return;
                    }
                    keybind.CurrentKey = keyCombo;
                    _log?.Debug($"Restored keybind {keybind.Id} = {savedKey}");
                }
                catch (Exception ex)
                {
                    _log?.Warn($"Failed to parse saved keybind {keybind.Id}: {ex.Message} - using default instead");
                }
            }
        }

        /// <summary>
        /// Detect conflicts between keybinds.
        /// </summary>
        public static List<KeybindConflict> GetConflicts()
        {
            var conflicts = new List<KeybindConflict>();

            for (int i = 0; i < _keybinds.Count; i++)
            {
                for (int j = i + 1; j < _keybinds.Count; j++)
                {
                    var k1 = _keybinds[i];
                    var k2 = _keybinds[j];

                    if (k1.CurrentKey != null && k2.CurrentKey != null &&
                        k1.CurrentKey.Key != KeyCode.None && k2.CurrentKey.Key != KeyCode.None &&
                        k1.CurrentKey.Equals(k2.CurrentKey))
                    {
                        conflicts.Add(new KeybindConflict
                        {
                            Keybind1 = k1,
                            Keybind2 = k2,
                            ConflictingKey = k1.CurrentKey
                        });
                    }
                }
            }

            return conflicts;
        }

        /// <summary>
        /// Update keybinds - called each frame.
        /// </summary>
        internal static void Update()
        {
            // Skip on dedicated server - no input to process
            if (Game.IsServer) return;

            if (!_enabled) return;

            // Update input state first
            InputState.Update();

            bool blocked = InputState.ShouldBlockInput();
            bool menuOnly = blocked && Game.InMenu;

            // Check all keybinds (snapshot to avoid InvalidOperationException if a callback modifies the list)
            var snapshot = _keybinds.ToArray();
            foreach (var keybind in snapshot)
            {
                // Skip blocked keybinds, but allow AllowInMenu keybinds on the menu screen
                if (blocked && !(menuOnly && keybind.AllowInMenu))
                    continue;
                keybind.CheckAndFire();
            }
        }

        /// <summary>
        /// Unregister all keybinds for a mod.
        /// </summary>
        internal static void UnregisterMod(string modId)
        {
            var toRemove = _keybinds.Where(k => k.ModId == modId).ToList();
            foreach (var keybind in toRemove)
            {
                _keybinds.Remove(keybind);
                _keybindsById.Remove(keybind.Id);
            }
        }

        /// <summary>
        /// Clear all keybinds.
        /// </summary>
        internal static void Clear()
        {
            _keybinds.Clear();
            _keybindsById.Clear();
        }
    }
}
