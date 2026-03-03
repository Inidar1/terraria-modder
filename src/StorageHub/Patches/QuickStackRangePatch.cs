using System;
using System.Reflection;
using HarmonyLib;
using StorageHub.Config;
using TerrariaModder.Core.Logging;

namespace StorageHub.Patches
{
    /// <summary>
    /// Harmony prefix on NearbyChests.GetChestsInRangeOf to extend quick-stack range
    /// to match StorageHub's current tier range.
    ///
    /// Vanilla uses a fixed 600px (37.5 tile) range. This patch replaces it with the
    /// tier-based range from ProgressionTier when StorageHub is active.
    ///
    /// This covers all callers: vanilla quick-stack button, QuickKeys keybind, etc.
    /// </summary>
    internal static class QuickStackRangePatch
    {
        private static Harmony _harmony;
        private static ILogger _log;

        /// <summary>
        /// Set by Mod.cs on world load. When null, patch is inactive (vanilla range used).
        /// </summary>
        public static StorageHubConfig Config { get; set; }

        public static void Apply(ILogger logger)
        {
            _log = logger;

            try
            {
                var nearbyChestsType = typeof(Terraria.GameContent.NearbyChests);
                if (nearbyChestsType == null)
                {
                    _log.Warn("QuickStackRangePatch: NearbyChests type not found, skipping");
                    return;
                }

                // Find GetChestsInRangeOf(Vector2, float)
                var method = nearbyChestsType.GetMethod("GetChestsInRangeOf",
                    BindingFlags.Public | BindingFlags.Static);
                if (method == null)
                {
                    _log.Warn("QuickStackRangePatch: GetChestsInRangeOf method not found, skipping");
                    return;
                }

                _harmony = new Harmony("com.terrariamodder.storagehub.quickstackrange");
                _harmony.Patch(method, prefix: new HarmonyMethod(
                    typeof(QuickStackRangePatch).GetMethod(nameof(Prefix),
                        BindingFlags.NonPublic | BindingFlags.Static)));

                _log.Info("QuickStackRangePatch applied — quick-stack range will match tier");
            }
            catch (Exception ex)
            {
                _log.Error($"QuickStackRangePatch failed to apply: {ex.Message}");
            }
        }

        public static void Remove()
        {
            try
            {
                _harmony?.UnpatchAll("com.terrariamodder.storagehub.quickstackrange");
            }
            catch { }

            Config = null;
            _harmony = null;
        }

        /// <summary>
        /// Prefix: replace default range (0 → 600 in vanilla) with tier range.
        /// Only modifies range when it's the default value (0 or 600).
        /// Explicit non-default ranges from other callers are left untouched.
        /// </summary>
        private static void Prefix(ref float range)
        {
            if (Config == null) return;

            // Only override the default range (callers pass 0 which vanilla converts to 600)
            if (range > 0f) return;

            int tierRange = ProgressionTier.GetRange(Config.Tier);
            // Clamp to float.MaxValue for Tier 4 (int.MaxValue would overflow as float)
            range = tierRange == int.MaxValue ? float.MaxValue : (float)tierRange;
        }
    }
}
