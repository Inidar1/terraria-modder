using System;
using System.Reflection;
using HarmonyLib;
using Terraria;

namespace Randomizer.Modules
{
    /// <summary>
    /// Scrambles item stats (damage, defense, speed) by a seed-deterministic factor.
    /// Same item always gets the same multiplier for a given seed.
    /// </summary>
    public class ItemStatsModule : ModuleBase
    {
        public override string Id => "item_stats";
        public override string Name => "Item Stat Scramble";
        public override string Description => "Weapons and armor get scrambled stats (0.5x-2x)";
        public override string Tooltip => "All item stats (damage, defense, speed, knockback) are multiplied by a random factor between 0.5x and 2x. Same item always gets the same multiplier for a given seed.";
        public override bool IsDangerous => true;

        internal static ItemStatsModule Instance;

        public override void BuildShuffleMap()
        {
            Instance = this;
            // No shuffle map needed — we use deterministic per-item factors
        }

        public override void ApplyPatches(Harmony harmony)
        {
            Instance = this;

            try
            {
                // Find Item.SetDefaults by name+parameter since ItemVariant is an internal type
                MethodInfo setDefaults = null;
                foreach (var m in typeof(Item).GetMethods(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (m.Name == "SetDefaults" && m.GetParameters().Length >= 1 &&
                        m.GetParameters()[0].ParameterType == typeof(int))
                    {
                        setDefaults = m;
                        break;
                    }
                }
                if (setDefaults != null)
                {
                    var postfix = typeof(ItemStatsModule).GetMethod(nameof(SetDefaults_Postfix),
                        BindingFlags.Public | BindingFlags.Static);
                    harmony.Patch(setDefaults, postfix: new HarmonyMethod(postfix));
                    Log.Info("[Randomizer] Item Stats: patched Item.SetDefaults");
                }
                else
                {
                    Log.Warn("[Randomizer] Item Stats: could not find Item.SetDefaults");
                }
            }
            catch (Exception ex)
            {
                Log.Error($"[Randomizer] Item Stats patch error: {ex.Message}");
            }
        }

        /// <summary>
        /// Postfix on Item.SetDefaults — after vanilla sets all stats,
        /// multiply damage/defense/speed by a deterministic factor.
        /// </summary>
        public static void SetDefaults_Postfix(Item __instance)
        {
            if (Instance == null || !Instance.Enabled) return;

            try
            {
                int type = __instance.type;
                if (type <= 0) return;

                int seed = Instance.Seed?.Seed ?? 0;
                if (seed == 0) return;

                // Deterministic factor per stat — no allocation, pure math
                double damageFactor = HashFactor(seed, type, 1);
                double defFactor = HashFactor(seed, type, 2);
                double timeFactor = HashFactor(seed, type, 3);
                double animFactor = HashFactor(seed, type, 4);
                double kbFactor = HashFactor(seed, type, 5);

                // Scramble damage
                if (__instance.damage > 0)
                {
                    __instance.damage = Math.Max(1, (int)(__instance.damage * damageFactor));
                }

                // Scramble defense
                if (__instance.defense > 0)
                {
                    __instance.defense = Math.Max(1, (int)(__instance.defense * defFactor));
                }

                // Scramble use time
                if (__instance.useTime > 0)
                {
                    __instance.useTime = Math.Max(1, (int)(__instance.useTime * timeFactor));
                }

                if (__instance.useAnimation > 0)
                {
                    __instance.useAnimation = Math.Max(1, (int)(__instance.useAnimation * animFactor));
                }

                // Scramble knockback
                if (__instance.knockBack > 0)
                {
                    __instance.knockBack = Math.Max(0.1f, (float)(__instance.knockBack * kbFactor));
                }
            }
            catch (Exception ex)
            {
                Instance?.Log?.Error($"[Randomizer] Item Stats postfix error: {ex.Message}");
            }
        }

        /// <summary>
        /// Deterministic factor (0.5-2.0) from seed+type+salt. No allocation.
        /// Uses integer hash mixing instead of System.Random.
        /// </summary>
        private static double HashFactor(int seed, int type, int salt)
        {
            uint h = (uint)(seed ^ (type * 31) ^ (salt * 7919));
            h = ((h >> 16) ^ h) * 0x45d9f3b;
            h = ((h >> 16) ^ h) * 0x45d9f3b;
            h = (h >> 16) ^ h;
            return 0.5 + (h % 1500) / 1000.0; // 0.5 to 2.0
        }

        public override void RemovePatches(Harmony harmony)
        {
            // Handled by harmony.UnpatchAll
        }
    }
}
