using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using Terraria;
using TerrariaModder.Core.Logging;

namespace SeedLab
{
    /// <summary>
    /// Manages the enabled/disabled state of seed features and provides flag override values
    /// for Harmony patch prefixes.
    ///
    /// Uses a hybrid global/per-method approach:
    /// - When ALL features for a seed are enabled: sets Main.* flag globally (covers every check site)
    /// - When ALL features for a seed are disabled: clears Main.* flag globally
    /// - When MIXED: sets Main.* to world original, per-method prefixes handle individual overrides
    /// </summary>
    public class FeatureManager
    {
        private readonly ILogger _log;
        private readonly string _configPath;

        // Feature state: featureId → enabled
        private readonly Dictionary<string, bool> _featureStates = new Dictionary<string, bool>();

        // Lookup tables built from catalog
        private readonly Dictionary<string, SeedDefinition> _seedsById;
        private readonly Dictionary<string, FeatureGroupDefinition> _groupsById;
        private readonly Dictionary<string, FeatureDefinition> _featuresById;
        private readonly Dictionary<string, List<FeatureDefinition>> _featuresByTarget;

        // Reflection cache for Main.* seed flags
        private static Type _mainType;
        private static readonly Dictionary<string, FieldInfo> _flagFields = new Dictionary<string, FieldInfo>();

        // World original flag values (captured before any overrides)
        private readonly Dictionary<string, bool> _worldOriginalFlags = new Dictionary<string, bool>();

        // Flags currently in "mixed" mode requiring per-method prefix overrides.
        // Flags NOT in this set are globally managed (all-on or all-off) and prefixes skip them.
        private readonly HashSet<string> _mixedFlags = new HashSet<string>();

        public bool AdvancedMode { get; set; }

        /// <summary>
        /// Whether the feature manager has been initialized with world state.
        /// </summary>
        public bool Initialized { get; private set; }

        public FeatureManager(ILogger log, string configPath)
        {
            _log = log;
            _configPath = configPath;

            SeedFeatures.BuildLookups(out _seedsById, out _groupsById, out _featuresById, out _featuresByTarget);
            InitReflection();

            // All features start disabled until world loads
            foreach (var kvp in _featuresById)
                _featureStates[kvp.Key] = false;
        }

        private void InitReflection()
        {
            _mainType = typeof(Main);

            string[] flagNames = {
                SeedFeatures.GetGoodWorld,
                SeedFeatures.DrunkWorld,
                SeedFeatures.DontStarveWorld,
                SeedFeatures.NotTheBeesWorld,
                SeedFeatures.RemixWorld,
                SeedFeatures.ZenithWorld,
                SeedFeatures.TenthAnniversaryWorld,
                SeedFeatures.NoTrapsWorld,
                SeedFeatures.SkyblockWorld,
                SeedFeatures.VampireSeed,
                SeedFeatures.InfectedSeed,
                SeedFeatures.TeamBasedSpawnsSeed,
                SeedFeatures.DualDungeonsSeed,
                SeedFeatures.ForceHalloweenForever,
                SeedFeatures.ForceXMasForever
            };

            foreach (var name in flagNames)
            {
                var field = _mainType.GetField(name, BindingFlags.Public | BindingFlags.Static);
                if (field != null)
                    _flagFields[name] = field;
                else
                    _log.Warn($"[SeedLab] Could not find Main.{name}");
            }
        }

        #region State Queries

        /// <summary>
        /// Check if a specific feature is enabled.
        /// </summary>
        public bool IsFeatureEnabled(string featureId)
        {
            return _featureStates.TryGetValue(featureId, out bool enabled) && enabled;
        }

        /// <summary>
        /// Check if a group is enabled (all features in group are enabled).
        /// </summary>
        public bool IsGroupEnabled(string groupId)
        {
            if (!_groupsById.TryGetValue(groupId, out var group)) return false;
            foreach (var feature in group.Features)
            {
                if (!IsFeatureEnabled(feature.Id)) return false;
            }
            return true;
        }

        /// <summary>
        /// Check if a group is partially enabled (some but not all features).
        /// </summary>
        public bool IsGroupPartial(string groupId)
        {
            if (!_groupsById.TryGetValue(groupId, out var group)) return false;
            bool anyOn = false, anyOff = false;
            foreach (var feature in group.Features)
            {
                if (IsFeatureEnabled(feature.Id)) anyOn = true;
                else anyOff = true;
            }
            return anyOn && anyOff;
        }

        /// <summary>
        /// Check if any feature in a seed is enabled.
        /// </summary>
        public bool IsSeedAnyEnabled(string seedId)
        {
            if (!_seedsById.TryGetValue(seedId, out var seed)) return false;
            foreach (var group in seed.Groups)
            {
                foreach (var feature in group.Features)
                {
                    if (IsFeatureEnabled(feature.Id)) return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Check if all features in a seed are enabled.
        /// </summary>
        public bool IsSeedAllEnabled(string seedId)
        {
            if (!_seedsById.TryGetValue(seedId, out var seed)) return false;
            foreach (var group in seed.Groups)
            {
                foreach (var feature in group.Features)
                {
                    if (!IsFeatureEnabled(feature.Id)) return false;
                }
            }
            return true;
        }

        #endregion

        #region State Mutations

        /// <summary>
        /// Toggle a specific feature.
        /// </summary>
        public void ToggleFeature(string featureId)
        {
            if (_featureStates.ContainsKey(featureId))
            {
                _featureStates[featureId] = !_featureStates[featureId];
                _log.Info($"[SeedLab] Feature '{featureId}' = {_featureStates[featureId]}");
                RecalculateGlobalFlags();
            }
        }

        /// <summary>
        /// Set a specific feature state.
        /// </summary>
        public void SetFeature(string featureId, bool enabled)
        {
            _featureStates[featureId] = enabled;
            RecalculateGlobalFlags();
        }

        /// <summary>
        /// Toggle all features in a group.
        /// </summary>
        public void ToggleGroup(string groupId)
        {
            if (!_groupsById.TryGetValue(groupId, out var group)) return;
            bool newState = !IsGroupEnabled(groupId);
            foreach (var feature in group.Features)
            {
                _featureStates[feature.Id] = newState;
            }
            _log.Info($"[SeedLab] Group '{groupId}' = {newState}");
            RecalculateGlobalFlags();
        }

        /// <summary>
        /// Set all features in a group.
        /// </summary>
        public void SetGroup(string groupId, bool enabled)
        {
            if (!_groupsById.TryGetValue(groupId, out var group)) return;
            foreach (var feature in group.Features)
            {
                _featureStates[feature.Id] = enabled;
            }
            RecalculateGlobalFlags();
        }

        /// <summary>
        /// Toggle all features for an entire seed.
        /// </summary>
        public void ToggleSeed(string seedId)
        {
            if (!_seedsById.TryGetValue(seedId, out var seed)) return;
            bool newState = !IsSeedAnyEnabled(seedId);
            foreach (var group in seed.Groups)
            {
                foreach (var feature in group.Features)
                {
                    _featureStates[feature.Id] = newState;
                }
            }
            _log.Info($"[SeedLab] Seed '{seedId}' = {newState}");
            RecalculateGlobalFlags();
        }

        /// <summary>
        /// Disable all features.
        /// </summary>
        public void DisableAll()
        {
            foreach (var key in new List<string>(_featureStates.Keys))
                _featureStates[key] = false;
            RecalculateGlobalFlags();
        }

        #endregion

        #region Global Flag Management

        /// <summary>
        /// Recalculate which flags are globally managed vs needing per-method overrides.
        /// Called after any feature state change.
        ///
        /// For each seed:
        /// - ALL features ON  → Main.flag = true globally, prefixes skip this flag
        /// - ALL features OFF → Main.flag = false globally, prefixes skip this flag
        /// - MIXED            → Main.flag = world original, prefixes override per-method
        /// </summary>
        public void RecalculateGlobalFlags()
        {
            if (!Initialized) return;

            _mixedFlags.Clear();

            foreach (var seed in SeedFeatures.Seeds)
            {
                bool allOn = true;
                bool allOff = true;

                foreach (var group in seed.Groups)
                {
                    foreach (var feature in group.Features)
                    {
                        if (IsFeatureEnabled(feature.Id))
                            allOff = false;
                        else
                            allOn = false;
                    }
                }

                if (allOn)
                {
                    // Full seed enabled — set global flag, covers ALL check sites in the game
                    SetFlag(seed.FlagField, true);
                }
                else if (allOff)
                {
                    // Full seed disabled — clear global flag
                    SetFlag(seed.FlagField, false);
                }
                else
                {
                    // Mixed — restore world original, per-method prefixes handle the rest
                    bool worldOriginal = _worldOriginalFlags.TryGetValue(seed.FlagField, out var orig) && orig;
                    SetFlag(seed.FlagField, worldOriginal);
                    _mixedFlags.Add(seed.FlagField);
                }
            }
        }

        /// <summary>
        /// Returns true if this flag is in mixed mode and needs per-method prefix overrides.
        /// When false, the flag is globally managed and prefixes should skip it.
        /// </summary>
        public bool NeedsMixedOverride(string seedFlag)
        {
            return _mixedFlags.Contains(seedFlag);
        }

        /// <summary>
        /// Get the world's original value for a seed flag (before any SeedLab overrides).
        /// </summary>
        public bool GetWorldOriginalFlag(string seedFlag)
        {
            return _worldOriginalFlags.TryGetValue(seedFlag, out var val) && val;
        }

        #endregion

        #region Flag Override Logic (for per-method prefixes)

        /// <summary>
        /// Get what a seed flag should be set to for a specific patch target method.
        /// Only called by prefixes when a flag is in mixed mode.
        ///
        /// If any feature targets this (method, flag) combo:
        ///   - Returns true if ANY such feature is enabled
        ///   - Returns false if ALL such features are disabled
        /// If NO features target this (method, flag) combo:
        ///   - Returns the ORIGINAL Main.* flag value (passthrough for unpatched effects)
        /// </summary>
        public bool GetFlagForTarget(string patchTarget, string seedFlag)
        {
            if (!_featuresByTarget.TryGetValue(patchTarget, out var features))
                return GetFlag(seedFlag); // No features target this method at all

            bool anyTargetsThisFlag = false;
            foreach (var feature in features)
            {
                if (feature.SeedFlag == seedFlag)
                {
                    anyTargetsThisFlag = true;
                    if (IsFeatureEnabled(feature.Id))
                        return true;
                }
            }

            // No features control this specific flag for this method — pass through original
            if (!anyTargetsThisFlag)
                return GetFlag(seedFlag);

            return false; // Features exist for this flag but none are enabled
        }

        public static bool GetFlag(string flagName)
        {
            if (_flagFields.TryGetValue(flagName, out var field))
            {
                try { return (bool)field.GetValue(null); }
                catch { return false; }
            }
            return false;
        }

        public static void SetFlag(string flagName, bool value)
        {
            if (_flagFields.TryGetValue(flagName, out var field))
            {
                try { field.SetValue(null, value); }
                catch { }
            }
        }

        #endregion

        #region World Load / Unload

        /// <summary>
        /// Initialize feature states from the world's actual seed flags.
        /// Captures world originals, sets initial state, loads saved overrides, then applies global flags.
        /// </summary>
        public void InitFromWorldFlags()
        {
            // 1. Capture world's original flag values (before any SeedLab modifications)
            _worldOriginalFlags.Clear();
            foreach (var seed in SeedFeatures.Seeds)
            {
                _worldOriginalFlags[seed.FlagField] = GetFlag(seed.FlagField);
            }

            // 2. Set initial feature states from world flags
            foreach (var seed in SeedFeatures.Seeds)
            {
                bool worldHasSeed = _worldOriginalFlags[seed.FlagField];
                foreach (var group in seed.Groups)
                {
                    foreach (var feature in group.Features)
                    {
                        _featureStates[feature.Id] = worldHasSeed;
                    }
                }
            }
            Initialized = true;
            _log.Info("[SeedLab] Features initialized from world flags");

            // 3. Load saved overrides (may change feature states)
            LoadState();

            // 4. Apply global flags based on final feature states
            RecalculateGlobalFlags();

            // Log the mode for each seed
            foreach (var seed in SeedFeatures.Seeds)
            {
                if (_mixedFlags.Contains(seed.FlagField))
                    _log.Debug($"[SeedLab]   {seed.DisplayName}: mixed mode (per-method overrides)");
                else if (IsSeedAllEnabled(seed.Id))
                    _log.Debug($"[SeedLab]   {seed.DisplayName}: global ON");
                else
                    _log.Debug($"[SeedLab]   {seed.DisplayName}: global OFF");
            }
        }

        /// <summary>
        /// Reset all features on world unload. Restores world original flags.
        /// </summary>
        public void Reset()
        {
            // Restore world original flags before clearing state
            foreach (var kvp in _worldOriginalFlags)
                SetFlag(kvp.Key, kvp.Value);
            _worldOriginalFlags.Clear();
            _mixedFlags.Clear();

            foreach (var key in new List<string>(_featureStates.Keys))
                _featureStates[key] = false;
            Initialized = false;
        }

        #endregion

        #region Config Save/Load

        public void SaveState()
        {
            try
            {
                string dir = Path.GetDirectoryName(_configPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                var lines = new List<string>();
                lines.Add("{");
                lines.Add($"  \"advancedMode\": {(AdvancedMode ? "true" : "false")},");
                lines.Add("  \"features\": {");

                var featureLines = new List<string>();
                foreach (var kvp in _featureStates)
                {
                    featureLines.Add($"    \"{kvp.Key}\": {(kvp.Value ? "true" : "false")}");
                }
                lines.Add(string.Join(",\n", featureLines));
                lines.Add("  }");
                lines.Add("}");

                File.WriteAllText(_configPath, string.Join("\n", lines));
            }
            catch (Exception ex)
            {
                _log.Error($"[SeedLab] Failed to save state: {ex.Message}");
            }
        }

        private void LoadState()
        {
            if (!File.Exists(_configPath)) return;

            try
            {
                string json = File.ReadAllText(_configPath);

                // Parse advancedMode
                var advMatch = Regex.Match(json, @"""advancedMode""\s*:\s*(true|false)", RegexOptions.IgnoreCase);
                if (advMatch.Success)
                    AdvancedMode = advMatch.Groups[1].Value.ToLower() == "true";

                // Parse individual feature states
                var featureMatches = Regex.Matches(json, @"""(\w+)""\s*:\s*(true|false)");
                foreach (Match m in featureMatches)
                {
                    string key = m.Groups[1].Value;
                    bool val = m.Groups[2].Value.ToLower() == "true";
                    if (key != "advancedMode" && _featureStates.ContainsKey(key))
                    {
                        _featureStates[key] = val;
                    }
                }

                _log.Info("[SeedLab] Loaded saved feature states");
            }
            catch (Exception ex)
            {
                _log.Error($"[SeedLab] Failed to load state: {ex.Message}");
            }
        }

        #endregion

        #region Accessors for UI / Tests

        public Dictionary<string, SeedDefinition> SeedsById => _seedsById;
        public Dictionary<string, FeatureGroupDefinition> GroupsById => _groupsById;
        public Dictionary<string, FeatureDefinition> FeaturesById => _featuresById;

        /// <summary>
        /// Get a snapshot of all feature states (for presets).
        /// </summary>
        public Dictionary<string, bool> GetAllStates()
        {
            return new Dictionary<string, bool>(_featureStates);
        }

        /// <summary>
        /// Apply a set of feature states (from a preset).
        /// </summary>
        public void ApplyStates(Dictionary<string, bool> states)
        {
            foreach (var kvp in states)
            {
                if (_featureStates.ContainsKey(kvp.Key))
                    _featureStates[kvp.Key] = kvp.Value;
            }
            RecalculateGlobalFlags();
        }

        #endregion
    }
}
