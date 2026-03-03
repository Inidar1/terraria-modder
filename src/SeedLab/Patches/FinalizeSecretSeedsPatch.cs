using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Terraria;
using TerrariaModder.Core.Logging;

namespace SeedLab.Patches
{
    /// <summary>
    /// Harmony prefix on WorldGen.SecretSeed.FinalizeSecretSeeds() for per-secret-seed control.
    ///
    /// Returns false to skip the original method and re-implements it with per-feature checks.
    /// Each Do*() method is only called if the corresponding feature group is enabled.
    ///
    /// Two-state logic (v2):
    ///   Unchecked (null) → use original .Enabled state
    ///   Checked (true) → always call the method
    /// </summary>
    public static class FinalizeSecretSeedsPatch
    {
        private static ILogger _log;
        private static WorldGenOverrideManager _manager;

        // Reflection cache
        private static Type _secretSeedType;
        private static FieldInfo _ssEnabledField;          // SecretSeed._enabled (instance)
        private static readonly Dictionary<string, FieldInfo> _ssInstanceFields = new Dictionary<string, FieldInfo>();
        private static readonly Dictionary<string, MethodInfo> _doMethods = new Dictionary<string, MethodInfo>();

        // For teamBasedSpawns special case
        private static Type _extraSpawnPointManagerType;
        private static MethodInfo _generateExtraSpawnsMethod;
        private static Type _extraSpawnSettingsType;

        // For noSpiderCavesActuallyNoSpiderCaves variant
        private static Type _variationsType;
        private static PropertyInfo _noSpiderCavesVariantProp;
        private static Type _npcType;
        private static FieldInfo _savedStylistField;

        // For ExtraSpawnSettings fields
        private static Type _mainType;

        // Ordered list matching FinalizeSecretSeeds execution order
        private static readonly FinalizeEntry[] FinalizeOrder = new[]
        {
            new FinalizeEntry("ss_surface_desert",      "surfaceIsDesert",     new[] { "DoSurfaceIsDesertFinish" }),
            new FinalizeEntry("ss_extra_liquid",         "extraLiquid",         new[] { "DoExtraLiquidFinish" }),
            new FinalizeEntry("ss_surface_space",        "surfaceIsInSpace",    new[] { "DoSurfaceIsInSpace" }),
            new FinalizeEntry("ss_no_traps",             "actuallyNoTraps",     new[] { "DoActuallyNoTraps" }),
            new FinalizeEntry("ss_surface_mushrooms",    "surfaceIsMushrooms",  null, isMushrooms: true),
            new FinalizeEntry("ss_world_frozen",         "worldIsFrozen",       new[] { "DoWorldIsFrozen" }),
            new FinalizeEntry("ss_no_infection",         "noInfection",         new[] { "DoNoInfection" }),
            new FinalizeEntry("ss_hallow_surface",       "hallowOnTheSurface",  new[] { "DoHallowOnSurface" }),
            new FinalizeEntry("ss_world_infected",       "worldIsInfected",     new[] { "DoWorldIsInfected" }),
            new FinalizeEntry("ss_start_hardmode",       "startInHardmode",     new[] { "DoStartInHardmode" }),
            new FinalizeEntry("ss_no_surface",           "noSurface",           new[] { "DoNoSurface" }),
            new FinalizeEntry("ss_coat_echo",            "coatEverythingEcho",  new[] { "DoCoatEverythingEcho" }),
            new FinalizeEntry("ss_coat_illuminant",      "coatEverythingIlluminant", new[] { "DoCoatEverythingIlluminant" }),
            new FinalizeEntry("ss_paint_gray",           "paintEverythingGray", new[] { "DoPaintEverythingGray" }),
            new FinalizeEntry("ss_paint_negative",       "paintEverythingNegative", new[] { "DoPaintEverythingNegative" }),
            new FinalizeEntry("ss_random_spawn",         "randomSpawn",         new[] { "DoRandomSpawn" }),
            new FinalizeEntry("ss_rainbow",              "rainbowStuff",        new[] { "DoRainbowStuff" }),
            new FinalizeEntry("ss_portal_gun",           "portalGunInChests",   new[] { "DoPortalGunInChests" }),
            new FinalizeEntry("ss_world_frozen",         "worldIsFrozen",       new[] { "DoWorldIsFrozenFinish" }, isSecondPass: true),
            new FinalizeEntry("ss_error_world",          "errorWorld",          new[] { "DoErrorWorldFinish" }),
            new FinalizeEntry("ss_team_spawns",          "teamBasedSpawns",     null, isTeamSpawns: true),
        };

        public static void Apply(Harmony harmony, WorldGenOverrideManager manager, ILogger log)
        {
            _log = log;
            _manager = manager;

            var worldGenType = typeof(WorldGen);
            _mainType = typeof(Main);
            _npcType = typeof(NPC);

            // Find SecretSeed type
            _secretSeedType = typeof(WorldGen).GetNestedType("SecretSeed", BindingFlags.Public | BindingFlags.NonPublic);
            if (_secretSeedType == null)
            {
                _log.Error("[SeedLab] FinalizeSecretSeedsPatch: Could not find WorldGen.SecretSeed");
                return;
            }

            _ssEnabledField = _secretSeedType.GetField("_enabled", BindingFlags.NonPublic | BindingFlags.Instance);

            // Cache SecretSeed instance fields
            foreach (var seed in WorldGenFeatureCatalog.Seeds)
            {
                if (seed.Kind != SeedKind.SecretSeed || seed.SecretSeedField == null) continue;
                var field = _secretSeedType.GetField(seed.SecretSeedField, BindingFlags.Public | BindingFlags.Static);
                if (field != null)
                    _ssInstanceFields[seed.SecretSeedField] = field;
            }

            // Cache Do*() methods (static methods on SecretSeed)
            foreach (var entry in FinalizeOrder)
            {
                if (entry.MethodNames == null) continue;
                foreach (var methodName in entry.MethodNames)
                {
                    if (_doMethods.ContainsKey(methodName)) continue;
                    var method = _secretSeedType.GetMethod(methodName,
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                    if (method != null)
                        _doMethods[methodName] = method;
                    else
                        _log.Warn($"[SeedLab] FinalizeSecretSeedsPatch: Missing method SecretSeed.{methodName}");
                }
            }

            // Cache Variations type for noSpiderCaves check
            _variationsType = _secretSeedType.GetNestedType("Variations", BindingFlags.Public | BindingFlags.NonPublic)
                ?? typeof(WorldGen).GetNestedType("Variations", BindingFlags.Public | BindingFlags.NonPublic);
            if (_variationsType != null)
                _noSpiderCavesVariantProp = _variationsType.GetProperty("noSpiderCavesActuallyNoSpiderCaves", BindingFlags.Public | BindingFlags.Static);
            if (_npcType != null)
                _savedStylistField = _npcType.GetField("savedStylist", BindingFlags.Public | BindingFlags.Static);

            // Cache ExtraSpawnPointManager for teamBasedSpawns
            _extraSpawnPointManagerType = typeof(Main).Assembly.GetType("Terraria.GameContent.ExtraSpawnPointManager");
            if (_extraSpawnPointManagerType != null)
                _generateExtraSpawnsMethod = _extraSpawnPointManagerType.GetMethod("GenerateExtraSpawns",
                    BindingFlags.Public | BindingFlags.Static);
            _extraSpawnSettingsType = typeof(Main).Assembly.GetType("Terraria.GameContent.ExtraSpawnSettings");

            // Find FinalizeSecretSeeds method
            var finalizeMethod = _secretSeedType.GetMethod("FinalizeSecretSeeds",
                BindingFlags.Public | BindingFlags.Static);
            if (finalizeMethod == null)
            {
                _log.Error("[SeedLab] FinalizeSecretSeedsPatch: Could not find FinalizeSecretSeeds method");
                return;
            }

            try
            {
                var prefix = typeof(FinalizeSecretSeedsPatch).GetMethod(nameof(FinalizeSecretSeeds_Prefix),
                    BindingFlags.NonPublic | BindingFlags.Static);
                harmony.Patch(finalizeMethod, prefix: new HarmonyMethod(prefix));
                _log.Info($"[SeedLab] FinalizeSecretSeedsPatch: Patched FinalizeSecretSeeds ({_doMethods.Count} Do*() methods cached)");
            }
            catch (Exception ex)
            {
                _log.Error($"[SeedLab] FinalizeSecretSeedsPatch: Failed to patch: {ex.Message}");
            }
        }

        /// <summary>
        /// Prefix that replaces FinalizeSecretSeeds entirely.
        /// Returns false to skip original.
        /// </summary>
        private static bool FinalizeSecretSeeds_Prefix()
        {
            if (_manager == null || !_manager.HasOverrides)
                return true; // No overrides — run original

            _log.Debug("[SeedLab] FinalizeSecretSeedsPatch: Running custom FinalizeSecretSeeds");

            // Once we start executing Do*() methods, we must NOT fall back to vanilla
            // (that would re-run already-executed methods). Log errors per-entry instead.
            foreach (var entry in FinalizeOrder)
            {
                try
                {
                    bool shouldRun = ShouldRunFinalize(entry.GroupId, entry.SecretSeedField);
                    if (!shouldRun) continue;

                    if (entry.IsMushrooms)
                    {
                        RunMushroomsFinalize(entry);
                    }
                    else if (entry.IsTeamSpawns)
                    {
                        RunTeamSpawnsFinalize(entry);
                    }
                    else if (entry.MethodNames != null)
                    {
                        foreach (var methodName in entry.MethodNames)
                        {
                            if (_doMethods.TryGetValue(methodName, out var method))
                            {
                                _log.Debug($"[SeedLab]   Running {methodName}");
                                method.Invoke(null, null);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _log.Error($"[SeedLab] FinalizeSecretSeedsPatch: Error running {entry.GroupId}: {ex}");
                }
            }

            // noSpiderCavesActuallyNoSpiderCaves variant check
            try { RunNoSpiderCavesVariant(); }
            catch (Exception ex) { _log.Error($"[SeedLab] FinalizeSecretSeedsPatch: Error in noSpiderCaves variant: {ex}"); }

            return false; // Always skip original — we've already executed entries
        }

        /// <summary>
        /// Determine if a finalize entry should run based on override state.
        /// </summary>
        private static bool ShouldRunFinalize(string groupId, string secretSeedField)
        {
            var overrideVal = _manager.GetSecretSeedFinalizeOverride(groupId);
            if (overrideVal.HasValue)
                return overrideVal.Value;

            // Default: use original .Enabled state
            return GetSecretSeedEnabled(secretSeedField);
        }

        private static bool GetSecretSeedEnabled(string fieldName)
        {
            if (_ssEnabledField == null) return false;
            if (!_ssInstanceFields.TryGetValue(fieldName, out var ssField)) return false;
            object instance = ssField.GetValue(null);
            if (instance == null) return false;
            return (bool)_ssEnabledField.GetValue(instance);
        }

        /// <summary>
        /// Replicates the mushrooms special case:
        /// if (surfaceIsMushrooms.Enabled) {
        ///     if (!noSurface.Enabled) DoSurfaceIsMushrooms();
        ///     DoSurfaceIsMushrooms();
        /// }
        /// </summary>
        private static void RunMushroomsFinalize(FinalizeEntry entry)
        {
            if (!_doMethods.TryGetValue("DoSurfaceIsMushrooms", out var method)) return;

            bool noSurfaceEnabled = GetSecretSeedEnabled("noSurface");
            var noSurfaceOverride = _manager.GetSecretSeedFinalizeOverride("ss_no_surface");
            if (noSurfaceOverride.HasValue) noSurfaceEnabled = noSurfaceOverride.Value;

            if (!noSurfaceEnabled)
            {
                _log.Debug("[SeedLab]   Running DoSurfaceIsMushrooms (pre-noSurface)");
                method.Invoke(null, null);
            }
            _log.Debug("[SeedLab]   Running DoSurfaceIsMushrooms");
            method.Invoke(null, null);
        }

        /// <summary>
        /// Replicates the teamBasedSpawns special case with ExtraSpawnPointManager.
        /// </summary>
        private static void RunTeamSpawnsFinalize(FinalizeEntry entry)
        {
            if (_generateExtraSpawnsMethod == null || _extraSpawnSettingsType == null) return;

            try
            {
                // Create ExtraSpawnSettings struct
                var settings = Activator.CreateInstance(_extraSpawnSettingsType);

                // Set fields via reflection
                SetField(_extraSpawnSettingsType, settings, "spawnType", GetEnumValue("Terraria.GameContent.Generation.ExtraSpawnType", "TeamBased"));
                SetField(_extraSpawnSettingsType, settings, "surface",
                    !GetStaticBoolField("Terraria.WorldBuilding.GenVars", "worldSpawnHasBeenRandomized") &&
                    GetStaticBoolField("Terraria.Main", "isThereAWorldSurface"));
                SetField(_extraSpawnSettingsType, settings, "remix", GetStaticBoolField("Terraria.Main", "remixWorld"));
                SetField(_extraSpawnSettingsType, settings, "roundLandmass", GetSecretSeedEnabled("roundLandmasses"));
                SetField(_extraSpawnSettingsType, settings, "skyblock", GetStaticBoolField("Terraria.Main", "skyblockWorld"));
                SetField(_extraSpawnSettingsType, settings, "extraLiquid", GetSecretSeedEnabled("extraLiquid"));

                // Set ExtraSpawnPointManager.settings
                var settingsField = _extraSpawnPointManagerType.GetField("settings",
                    BindingFlags.Public | BindingFlags.Static);
                if (settingsField != null)
                    settingsField.SetValue(null, settings);

                _log.Debug("[SeedLab]   Running ExtraSpawnPointManager.GenerateExtraSpawns");
                _generateExtraSpawnsMethod.Invoke(null, null);
            }
            catch (Exception ex)
            {
                _log.Error($"[SeedLab] FinalizeSecretSeedsPatch: Error in teamBasedSpawns: {ex.Message}");
            }
        }

        /// <summary>
        /// Replicates: if (Variations.noSpiderCavesActuallyNoSpiderCaves) NPC.savedStylist = true;
        /// </summary>
        private static void RunNoSpiderCavesVariant()
        {
            if (_noSpiderCavesVariantProp == null || _savedStylistField == null) return;
            try
            {
                bool variant = (bool)_noSpiderCavesVariantProp.GetValue(null, null);
                if (variant)
                    _savedStylistField.SetValue(null, true);
            }
            catch { }
        }

        private static bool GetStaticBoolField(string typeName, string fieldName)
        {
            try
            {
                var type = typeof(Main).Assembly.GetType(typeName);
                var field = type?.GetField(fieldName, BindingFlags.Public | BindingFlags.Static);
                if (field != null) return (bool)field.GetValue(null);
                // Fallback: try property (e.g. Main.isThereAWorldSurface is a getter-only property)
                var prop = type?.GetProperty(fieldName, BindingFlags.Public | BindingFlags.Static);
                return prop != null && (bool)prop.GetValue(null);
            }
            catch { return false; }
        }

        private static object GetEnumValue(string typeName, string valueName)
        {
            try
            {
                var type = typeof(Main).Assembly.GetType(typeName);
                return type != null ? Enum.Parse(type, valueName) : 0;
            }
            catch { return 0; }
        }

        private static void SetField(Type type, object instance, string fieldName, object value)
        {
            var field = type.GetField(fieldName, BindingFlags.Public | BindingFlags.Instance);
            if (field != null)
                field.SetValue(instance, value);
        }

        private class FinalizeEntry
        {
            public string GroupId;
            public string SecretSeedField;
            public string[] MethodNames;
            public bool IsMushrooms;
            public bool IsTeamSpawns;
            public bool IsSecondPass;

            public FinalizeEntry(string groupId, string secretSeedField, string[] methodNames,
                bool isMushrooms = false, bool isTeamSpawns = false, bool isSecondPass = false)
            {
                GroupId = groupId;
                SecretSeedField = secretSeedField;
                MethodNames = methodNames;
                IsMushrooms = isMushrooms;
                IsTeamSpawns = isTeamSpawns;
                IsSecondPass = isSecondPass;
            }
        }
    }
}
