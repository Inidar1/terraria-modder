using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Terraria;
using TerrariaModder.Core.Logging;

namespace SeedLab.Patches
{
    /// <summary>
    /// Harmony postfix on WorldGen.Reset() to override seed flags during world generation.
    ///
    /// Two-state logic (v2):
    /// - If ANY group for a seed is checked → enable the seed flag globally
    /// - If NO groups are checked → don't touch the flag
    ///
    /// The WorldGenPassPatch handles per-pass flag suppression for unchecked groups.
    /// </summary>
    public static class WorldGenResetPatch
    {
        private static ILogger _log;
        private static WorldGenOverrideManager _manager;

        // Reflection cache for Main.* fields
        private static Type _mainType;
        private static readonly Dictionary<string, FieldInfo> _mainFields = new Dictionary<string, FieldInfo>();

        // Reflection cache for WorldGen.* alias fields
        private static Type _worldGenType;
        private static readonly Dictionary<string, FieldInfo> _worldGenFields = new Dictionary<string, FieldInfo>();

        // Reflection cache for GenVars derived fields
        private static Type _genVarsType;
        private static FieldInfo _notTheBeesAndFtwField;
        private static FieldInfo _noTrapsAndFtwField;
        private static FieldInfo _flipInfectionsField;

        // Reflection cache for SecretSeed internals
        private static Type _secretSeedType;
        private static FieldInfo _enabledField;
        private static FieldInfo _activeCountField;
        private static readonly Dictionary<string, FieldInfo> _secretSeedInstanceFields = new Dictionary<string, FieldInfo>();

        public static void Apply(Harmony harmony, WorldGenOverrideManager manager, ILogger log)
        {
            _log = log;
            _manager = manager;

            // Cache Main type and seed flag fields
            _mainType = typeof(Main);

            // Collect all Main.* fields needed from the catalog
            var mainFlagNames = new HashSet<string>();
            foreach (var seed in WorldGenFeatureCatalog.Seeds)
            {
                if (seed.Kind == SeedKind.SpecialSeed && seed.FlagField != null)
                    mainFlagNames.Add(seed.FlagField);
                if (seed.MainFlagField != null)
                    mainFlagNames.Add(seed.MainFlagField);
            }

            foreach (var name in mainFlagNames)
            {
                var field = _mainType.GetField(name, BindingFlags.Public | BindingFlags.Static);
                if (field != null)
                    _mainFields[name] = field;
                else
                    _log.Warn($"[SeedLab] WorldGenResetPatch: Missing Main.{name}");
            }

            // Cache WorldGen type and alias fields
            _worldGenType = typeof(WorldGen);
            {
                foreach (var kvp in WorldGenFeatureCatalog.MainToWorldGenAlias)
                {
                    var field = _worldGenType.GetField(kvp.Value, BindingFlags.Public | BindingFlags.Static);
                    if (field != null)
                        _worldGenFields[kvp.Value] = field;
                    else
                        _log.Warn($"[SeedLab] WorldGenResetPatch: Missing WorldGen.{kvp.Value}");
                }
            }

            // Cache GenVars derived fields
            var terrariaAsm = typeof(Main).Assembly;
            _genVarsType = terrariaAsm.GetType("Terraria.WorldBuilding.GenVars")
                ?? terrariaAsm.GetType("Terraria.GameContent.Generation.GenVars");
            if (_genVarsType != null)
            {
                _notTheBeesAndFtwField = _genVarsType.GetField("notTheBeesAndForTheWorthyNoCelebration", BindingFlags.Public | BindingFlags.Static);
                _noTrapsAndFtwField = _genVarsType.GetField("noTrapsAndForTheWorthyNoCelebration", BindingFlags.Public | BindingFlags.Static);
                _flipInfectionsField = _genVarsType.GetField("flipInfections", BindingFlags.Public | BindingFlags.Static);
            }

            // Cache SecretSeed internals
            _secretSeedType = typeof(WorldGen).GetNestedType("SecretSeed", BindingFlags.Public | BindingFlags.NonPublic);
            if (_secretSeedType != null)
            {
                _enabledField = _secretSeedType.GetField("_enabled", BindingFlags.NonPublic | BindingFlags.Instance);
                _activeCountField = _secretSeedType.GetField("activeSecretSeedCount", BindingFlags.NonPublic | BindingFlags.Static);

                foreach (var seed in WorldGenFeatureCatalog.Seeds)
                {
                    if (seed.Kind != SeedKind.SecretSeed || seed.SecretSeedField == null) continue;
                    var ssField = _secretSeedType.GetField(seed.SecretSeedField, BindingFlags.Public | BindingFlags.Static);
                    if (ssField != null)
                        _secretSeedInstanceFields[seed.SecretSeedField] = ssField;
                    else
                        _log.Warn($"[SeedLab] WorldGenResetPatch: Missing SecretSeed.{seed.SecretSeedField}");
                }

                _log.Info($"[SeedLab] WorldGenResetPatch: Cached {_secretSeedInstanceFields.Count} SecretSeed fields");
            }

            // Patch WorldGen.Reset()
            var resetMethod = typeof(WorldGen).GetMethod("Reset", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
            if (resetMethod == null)
            {
                _log.Error("[SeedLab] WorldGenResetPatch: Could not find WorldGen.Reset()");
                return;
            }

            try
            {
                var postfix = typeof(WorldGenResetPatch).GetMethod(nameof(Reset_Postfix), BindingFlags.NonPublic | BindingFlags.Static);
                harmony.Patch(resetMethod, postfix: new HarmonyMethod(postfix));
                _log.Info("[SeedLab] WorldGenResetPatch: Patched WorldGen.Reset()");
            }
            catch (Exception ex)
            {
                _log.Error($"[SeedLab] WorldGenResetPatch: Failed to patch: {ex.Message}");
            }
        }

        private static void Reset_Postfix()
        {
            if (_manager == null || !_manager.HasOverrides) return;

            try
            {
                int applied = 0;

                foreach (var seed in WorldGenFeatureCatalog.Seeds)
                {
                    if (seed.Kind == SeedKind.SpecialSeed)
                    {
                        var flagOverride = _manager.ShouldEnableSeedFlag(seed.Id);
                        if (!flagOverride.HasValue) continue; // All default — don't touch

                        bool value = flagOverride.Value;

                        // Set Main.* flag
                        if (seed.FlagField != null && _mainFields.TryGetValue(seed.FlagField, out var mainField))
                        {
                            mainField.SetValue(null, value);
                            _log.Debug($"[SeedLab]   Main.{seed.FlagField} = {value}");
                        }

                        // Set WorldGen.* alias field
                        if (seed.WorldGenAlias != null && _worldGenFields.TryGetValue(seed.WorldGenAlias, out var wgField))
                        {
                            wgField.SetValue(null, value);
                            _log.Debug($"[SeedLab]   WorldGen.{seed.WorldGenAlias} = {value}");
                        }

                        applied++;
                    }
                    else if (seed.Kind == SeedKind.SecretSeed)
                    {
                        // For secret seeds: check the single group's override
                        if (seed.Groups.Length == 0) continue;
                        var groupOverride = _manager.GetGroupOverride(seed.Groups[0].Id);
                        if (!groupOverride.HasValue) continue; // Default — don't touch

                        bool value = groupOverride.Value;

                        // Set SecretSeed._enabled
                        if (seed.SecretSeedField != null && _enabledField != null && _activeCountField != null &&
                            _secretSeedInstanceFields.TryGetValue(seed.SecretSeedField, out var ssField))
                        {
                            object instance = ssField.GetValue(null);
                            if (instance != null)
                            {
                                bool currentEnabled = (bool)_enabledField.GetValue(instance);
                                if (currentEnabled != value)
                                {
                                    _enabledField.SetValue(instance, value);
                                    int count = (int)_activeCountField.GetValue(null);
                                    _activeCountField.SetValue(null, value ? count + 1 : count - 1);
                                    _log.Debug($"[SeedLab]   SecretSeed.{seed.SecretSeedField}._enabled = {value} (count: {(value ? count + 1 : count - 1)})");
                                }
                            }
                        }

                        // Set associated Main.* flag if this secret seed has one
                        if (seed.MainFlagField != null && _mainFields.TryGetValue(seed.MainFlagField, out var mainFlag))
                        {
                            mainFlag.SetValue(null, value);
                            _log.Debug($"[SeedLab]   Main.{seed.MainFlagField} = {value}");
                        }

                        applied++;
                    }
                }

                if (applied > 0)
                {
                    RecalculateDerivedGenVars();
                    _log.Info($"[SeedLab] WorldGenResetPatch: Applied {applied} override(s)");
                }
            }
            catch (Exception ex)
            {
                _log.Error($"[SeedLab] WorldGenResetPatch: Error applying overrides: {ex.Message}");
            }
        }

        private static void RecalculateDerivedGenVars()
        {
            bool getGood = GetMainFlag("getGoodWorld");
            bool notTheBees = GetMainFlag("notTheBeesWorld");
            bool noTraps = GetMainFlag("noTrapsWorld");
            bool tenth = GetMainFlag("tenthAnniversaryWorld");
            bool drunk = GetMainFlag("drunkWorld");
            bool remix = GetMainFlag("remixWorld");

            if (_notTheBeesAndFtwField != null)
                _notTheBeesAndFtwField.SetValue(null, notTheBees && getGood && !tenth);
            if (_noTrapsAndFtwField != null)
                _noTrapsAndFtwField.SetValue(null, noTraps && getGood && !tenth);
            if (_flipInfectionsField != null)
                _flipInfectionsField.SetValue(null, drunk && getGood && !remix);
        }

        private static bool GetMainFlag(string name)
        {
            if (_mainFields.TryGetValue(name, out var field))
            {
                try { return (bool)field.GetValue(null); }
                catch { return false; }
            }
            return false;
        }
    }
}
