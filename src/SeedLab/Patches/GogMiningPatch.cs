using System;
using System.Reflection;
using HarmonyLib;
using Terraria;
using TerrariaModder.Core.Logging;
using SeedLab.Gen;

namespace SeedLab.Patches
{
    /// <summary>
    /// Blocks mining of Gog-painted tiles unless the player has at least Mythril-tier
    /// pickaxe power (150). Implemented as a prefix on Player.PickTile that returns
    /// false (skip original) when the requirement is not met — the swing animation
    /// still plays, but no crack/progress appears, matching vanilla unmineables.
    /// </summary>
    public static class GogMiningPatch
    {
        private static ILogger _log;

        public static void Apply(Harmony harmony, ILogger log)
        {
            _log = log;
            try
            {
                var pickTile = typeof(Player).GetMethod("PickTile",
                    BindingFlags.Public | BindingFlags.Instance);
                if (pickTile == null)
                {
                    _log.Error("[SeedLab] GogMiningPatch: Could not find Player.PickTile");
                    return;
                }

                var prefix = typeof(GogMiningPatch).GetMethod(nameof(PickTile_Prefix),
                    BindingFlags.NonPublic | BindingFlags.Static);
                harmony.Patch(pickTile, prefix: new HarmonyMethod(prefix));
                _log.Info("[SeedLab] GogMiningPatch: Patched Player.PickTile");
            }
            catch (Exception ex)
            {
                _log.Error($"[SeedLab] GogMiningPatch: Failed to patch: {ex.Message}");
            }
        }

        private static bool PickTile_Prefix(int x, int y, int pickPower)
        {
            var t = Main.tile[x, y];
            if (t != null && t.active() && t.color() == GogGen.GogPaint
                && pickPower < GogGen.MythrilPickPower)
                return false; // block mining — swing plays but tile doesn't crack
            return true;
        }
    }
}
