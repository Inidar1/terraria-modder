using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Terraria;
using TerrariaModder.Core.Logging;

namespace SeedLab.Patches
{
    /// <summary>
    /// Harmony prefix+postfix on GenPass.Apply for per-pass seed flag toggling.
    ///
    /// Before each generation pass executes, checks which feature groups target this pass
    /// and overrides seed flags accordingly. After the pass, restores original values.
    ///
    /// FLAG LEAK FIX (v2): For seeds with any checked group, ONLY passes mapped to a
    /// checked group get the flag set true. All other passes get the flag suppressed (false).
    /// This prevents e.g. enabling "Dual Evil" from also triggering drunk dungeon changes.
    ///
    /// For special seeds: toggles Main.* and WorldGen.* flags.
    /// For secret seeds: toggles SecretSeed._enabled via reflection.
    /// </summary>
    public static class WorldGenPassPatch
    {
        private static ILogger _log;
        private static WorldGenOverrideManager _manager;

        // Reflection cache
        private static FieldInfo _nameField;                           // GenPass.Name (public field)
        private static readonly Dictionary<string, FieldInfo> _mainFields = new Dictionary<string, FieldInfo>();
        private static readonly Dictionary<string, FieldInfo> _worldGenFields = new Dictionary<string, FieldInfo>();

        // Secret seed reflection cache
        private static Type _secretSeedType;
        private static FieldInfo _ssEnabledField;                      // SecretSeed._enabled (instance)
        private static readonly Dictionary<string, FieldInfo> _ssInstanceFields = new Dictionary<string, FieldInfo>();

        // Per-pass saved state (world gen is single-threaded, so static is safe)
        private static readonly Dictionary<string, bool> _savedMainFlags = new Dictionary<string, bool>();
        private static readonly Dictionary<string, bool> _savedWorldGenFlags = new Dictionary<string, bool>();
        private static readonly Dictionary<string, bool> _savedSecretSeedFlags = new Dictionary<string, bool>();
        private static bool _hasOverrides;

        public static void Apply(Harmony harmony, WorldGenOverrideManager manager, ILogger log)
        {
            _log = log;
            _manager = manager;

            // Find GenPass.Apply
            var genPassType = typeof(Main).Assembly.GetType("Terraria.WorldBuilding.GenPass");
            if (genPassType == null)
            {
                _log.Error("[SeedLab] WorldGenPassPatch: Could not find Terraria.WorldBuilding.GenPass");
                return;
            }

            _nameField = genPassType.GetField("Name", BindingFlags.Public | BindingFlags.Instance);
            if (_nameField == null)
            {
                _log.Error("[SeedLab] WorldGenPassPatch: Could not find GenPass.Name field");
                return;
            }

            var applyMethod = genPassType.GetMethod("Apply", BindingFlags.Public | BindingFlags.Instance);
            if (applyMethod == null)
            {
                _log.Error("[SeedLab] WorldGenPassPatch: Could not find GenPass.Apply method");
                return;
            }

            // Cache Main.* fields for all special seeds
            var mainType = typeof(Main);
            foreach (var seed in WorldGenFeatureCatalog.Seeds)
            {
                if (seed.Kind != SeedKind.SpecialSeed) continue;
                CacheMainField(mainType, seed.FlagField);
            }

            // Cache WorldGen.* alias fields
            var worldGenType = typeof(WorldGen);
            {
                foreach (var kvp in WorldGenFeatureCatalog.MainToWorldGenAlias)
                {
                    var field = worldGenType.GetField(kvp.Value, BindingFlags.Public | BindingFlags.Static);
                    if (field != null)
                        _worldGenFields[kvp.Value] = field;
                }
            }

            // Cache SecretSeed fields for secret seeds
            _secretSeedType = typeof(WorldGen).GetNestedType("SecretSeed", BindingFlags.Public | BindingFlags.NonPublic);
            if (_secretSeedType != null)
            {
                _ssEnabledField = _secretSeedType.GetField("_enabled", BindingFlags.NonPublic | BindingFlags.Instance);

                foreach (var seed in WorldGenFeatureCatalog.Seeds)
                {
                    if (seed.Kind != SeedKind.SecretSeed || seed.SecretSeedField == null) continue;
                    var field = _secretSeedType.GetField(seed.SecretSeedField, BindingFlags.Public | BindingFlags.Static);
                    if (field != null)
                        _ssInstanceFields[seed.SecretSeedField] = field;
                }
            }

            // Patch GenPass.Apply
            try
            {
                var prefix = typeof(WorldGenPassPatch).GetMethod(nameof(Apply_Prefix), BindingFlags.NonPublic | BindingFlags.Static);
                var postfix = typeof(WorldGenPassPatch).GetMethod(nameof(Apply_Postfix), BindingFlags.NonPublic | BindingFlags.Static);

                harmony.Patch(applyMethod,
                    prefix: new HarmonyMethod(prefix),
                    postfix: new HarmonyMethod(postfix));

                _log.Info($"[SeedLab] WorldGenPassPatch: Patched GenPass.Apply (cached {_mainFields.Count} Main fields, {_ssInstanceFields.Count} SecretSeed fields)");
            }
            catch (Exception ex)
            {
                _log.Error($"[SeedLab] WorldGenPassPatch: Failed to patch: {ex.Message}");
            }
        }

        private static void CacheMainField(Type mainType, string name)
        {
            if (mainType == null || _mainFields.ContainsKey(name)) return;
            var field = mainType.GetField(name, BindingFlags.Public | BindingFlags.Static);
            if (field != null)
                _mainFields[name] = field;
        }

        /// <summary>
        /// Prefix: save current flags and override based on feature group config.
        ///
        /// Flag leak fix: For each seed with any checked groups, we use GetFlagOverrideForPass
        /// which returns true only for passes mapped to checked groups, and false for all other
        /// passes (suppressing the flag to prevent leak).
        /// </summary>
        private static void Apply_Prefix(object __instance)
        {
            _hasOverrides = false;
            if (_manager == null || !_manager.HasOverrides) return;

            try
            {
                string passName = (string)_nameField.GetValue(__instance);
                if (string.IsNullOrEmpty(passName)) return;

                _savedMainFlags.Clear();
                _savedWorldGenFlags.Clear();
                _savedSecretSeedFlags.Clear();

                // For each seed, check the per-pass flag override
                foreach (var seed in WorldGenFeatureCatalog.Seeds)
                {
                    if (seed.Kind == SeedKind.SpecialSeed)
                    {
                        // GetFlagOverrideForPass handles the leak fix:
                        // - null = no checked groups for this seed, don't interfere
                        // - true = a checked group maps to this pass
                        // - false = seed has checked groups but none map here (suppress)
                        var passOverride = _manager.GetFlagOverrideForPass(passName, seed.Id);
                        if (!passOverride.HasValue) continue;

                        // Save and override Main.* flag
                        if (seed.FlagField != null && _mainFields.TryGetValue(seed.FlagField, out var mainField))
                        {
                            if (!_savedMainFlags.ContainsKey(seed.FlagField))
                                _savedMainFlags[seed.FlagField] = (bool)mainField.GetValue(null);
                            mainField.SetValue(null, passOverride.Value);
                            _hasOverrides = true;
                        }

                        // Save and override WorldGen.* alias
                        if (seed.WorldGenAlias != null && _worldGenFields.TryGetValue(seed.WorldGenAlias, out var wgField))
                        {
                            if (!_savedWorldGenFlags.ContainsKey(seed.WorldGenAlias))
                                _savedWorldGenFlags[seed.WorldGenAlias] = (bool)wgField.GetValue(null);
                            wgField.SetValue(null, passOverride.Value);
                        }
                    }
                    else if (seed.Kind == SeedKind.SecretSeed)
                    {
                        // For secret seeds, check if their single group maps to this pass
                        if (seed.Groups.Length == 0) continue;
                        var group = seed.Groups[0];

                        // Only override if this pass is in the group's pass list
                        bool passMatches = false;
                        foreach (var pn in group.PassNames)
                        {
                            if (pn == passName) { passMatches = true; break; }
                        }
                        if (!passMatches) continue;

                        var overrideVal = _manager.GetGroupOverride(group.Id);
                        if (overrideVal != true) continue;

                        // Override SecretSeed._enabled
                        if (seed.SecretSeedField != null && _ssEnabledField != null &&
                            _ssInstanceFields.TryGetValue(seed.SecretSeedField, out var ssField))
                        {
                            object instance = ssField.GetValue(null);
                            if (instance != null)
                            {
                                if (!_savedSecretSeedFlags.ContainsKey(seed.SecretSeedField))
                                    _savedSecretSeedFlags[seed.SecretSeedField] = (bool)_ssEnabledField.GetValue(instance);
                                _ssEnabledField.SetValue(instance, true);
                                _hasOverrides = true;
                            }
                        }
                    }
                }

                if (_hasOverrides)
                    _log.Debug($"[SeedLab] WorldGenPassPatch: Pass '{passName}' — overriding {_savedMainFlags.Count + _savedSecretSeedFlags.Count} flag(s)");
            }
            catch (Exception ex)
            {
                _log.Error($"[SeedLab] WorldGenPassPatch prefix error: {ex.Message}");
                // Don't clear _hasOverrides — let postfix restore any already-saved flags
            }
        }

        /// <summary>
        /// Postfix: restore saved flag values.
        /// </summary>
        private static void Apply_Postfix(object __instance)
        {
            if (!_hasOverrides) return;
            _hasOverrides = false;

            try
            {
                // Restore Main.* flags
                foreach (var kvp in _savedMainFlags)
                {
                    if (_mainFields.TryGetValue(kvp.Key, out var field))
                        field.SetValue(null, kvp.Value);
                }

                // Restore WorldGen.* flags
                foreach (var kvp in _savedWorldGenFlags)
                {
                    if (_worldGenFields.TryGetValue(kvp.Key, out var field))
                        field.SetValue(null, kvp.Value);
                }

                // Restore SecretSeed._enabled flags
                foreach (var kvp in _savedSecretSeedFlags)
                {
                    if (_ssInstanceFields.TryGetValue(kvp.Key, out var ssField) && _ssEnabledField != null)
                    {
                        object instance = ssField.GetValue(null);
                        if (instance != null)
                            _ssEnabledField.SetValue(instance, kvp.Value);
                    }
                }

                _savedMainFlags.Clear();
                _savedWorldGenFlags.Clear();
                _savedSecretSeedFlags.Clear();
            }
            catch (Exception ex)
            {
                _log.Error($"[SeedLab] WorldGenPassPatch postfix error: {ex.Message}");
            }
        }
    }
}
