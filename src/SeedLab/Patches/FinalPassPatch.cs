using System;
using System.Reflection;
using HarmonyLib;
using Terraria;
using TerrariaModder.Core.Logging;
using SeedLab.Gen;

namespace SeedLab.Patches
{
    /// <summary>
    /// Harmony postfix on GenPass.Apply that fires after the "Final Cleanup" pass —
    /// the absolute last worldgen pass — to run custom SeedLab world-gen features.
    ///
    /// This runs after FinalizeSecretSeeds, TownNPCPositionsCleanup, and Liquid.LiquidCheck,
    /// so our tile/spawn/liquid changes are truly the final state of the world.
    /// </summary>
    public static class FinalPassPatch
    {
        private const string FinalCleanupName = "Final Cleanup";

        private static ILogger _log;
        private static WorldGenOverrideManager _manager;
        private static FieldInfo _nameField;

        public static void Apply(Harmony harmony, WorldGenOverrideManager manager, ILogger log)
        {
            _log    = log;
            _manager = manager;

            var genPassType = typeof(Main).Assembly.GetType("Terraria.WorldBuilding.GenPass");
            if (genPassType == null)
            {
                _log.Error("[SeedLab] FinalPassPatch: Could not find GenPass type");
                return;
            }

            _nameField = genPassType.GetField("Name", BindingFlags.Public | BindingFlags.Instance);
            if (_nameField == null)
            {
                _log.Error("[SeedLab] FinalPassPatch: Could not find GenPass.Name field");
                return;
            }

            var applyMethod = genPassType.GetMethod("Apply", BindingFlags.Public | BindingFlags.Instance);
            if (applyMethod == null)
            {
                _log.Error("[SeedLab] FinalPassPatch: Could not find GenPass.Apply method");
                return;
            }

            try
            {
                var postfix = typeof(FinalPassPatch).GetMethod(nameof(Apply_Postfix),
                    BindingFlags.NonPublic | BindingFlags.Static);
                harmony.Patch(applyMethod, postfix: new HarmonyMethod(postfix));
                _log.Info("[SeedLab] FinalPassPatch: Patched GenPass.Apply");
            }
            catch (Exception ex)
            {
                _log.Error($"[SeedLab] FinalPassPatch: Failed to patch: {ex.Message}");
            }
        }

        private static void Apply_Postfix(object __instance)
        {
            try
            {
                string passName = (string)_nameField.GetValue(__instance);
                if (passName != FinalCleanupName) return;
                if (_manager == null) return;

                if (_manager.IsGroupChecked("custom_underwater_surface"))
                {
                    _log.Info("[SeedLab] FinalPassPatch: Running UnderwaterSurfaceGen (after Final Cleanup)");
                    UnderwaterSurfaceGen.DoUnderwaterSurface();
                }

                if (_manager.IsGroupChecked("gog_enable"))
                {
                    _log.Info("[SeedLab] FinalPassPatch: Running GogGen.SpawnAtBorders");
                    GogGen.SpawnAtBorders();
                }

                if (_manager.IsGroupChecked("gog_spawn"))
                {
                    _log.Info("[SeedLab] FinalPassPatch: Running GogGen.SpawnAtSpawn");
                    GogGen.SpawnAtSpawn();
                }
            }
            catch (Exception ex)
            {
                _log.Error($"[SeedLab] FinalPassPatch postfix error: {ex.Message}");
            }
        }
    }
}
