using System;
using System.Reflection;
using HarmonyLib;
using Terraria;

namespace Randomizer.Modules
{
    /// <summary>
    /// Randomizes fishing catches by intercepting the item-giving method.
    /// Each catch produces a random item from the full item pool.
    /// </summary>
    public class FishingModule : ModuleBase
    {
        public override string Id => "fishing";
        public override string Name => "Fishing Shuffle";
        public override string Description => "Fish up random items instead";
        public override string Tooltip => "Each fishing catch is replaced with a random item. Every reel is a surprise.";
        private const int MaxItemId = 6144; // ItemID.Count=6145, last valid=6144 (verified from decomp)

        internal static FishingModule Instance;

        public override void BuildShuffleMap()
        {
            Instance = this;
            InitPoolRng();
        }

        public override void ApplyPatches(Harmony harmony)
        {
            Instance = this;

            try
            {
                // Patch AI_061_FishingBobber_GiveItemToPlayer(Player, int itemType)
                // This is where the caught item type is passed before creating the item.
                MethodInfo giveItem = null;
                foreach (var m in typeof(Projectile).GetMethods(BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    if (m.Name == "AI_061_FishingBobber_GiveItemToPlayer")
                    {
                        giveItem = m;
                        break;
                    }
                }

                if (giveItem != null)
                {
                    var prefix = typeof(FishingModule).GetMethod(nameof(GiveItem_Prefix),
                        BindingFlags.Public | BindingFlags.Static);
                    harmony.Patch(giveItem, prefix: new HarmonyMethod(prefix));
                    Log.Info("[Randomizer] Fishing: patched AI_061_FishingBobber_GiveItemToPlayer");
                }
                else
                {
                    Log.Warn("[Randomizer] Fishing: could not find GiveItemToPlayer method");
                }
            }
            catch (Exception ex)
            {
                Log.Error($"[Randomizer] Fishing patch error: {ex.Message}");
            }
        }

        /// <summary>
        /// Prefix on AI_061_FishingBobber_GiveItemToPlayer — replaces with random item each catch.
        /// </summary>
        public static void GiveItem_Prefix(ref int itemType)
        {
            if (Instance == null || !Instance.Enabled) return;
            if (itemType <= 0) return;
            itemType = Instance.GetRandomInRange(1, MaxItemId + 1);
        }

        public override void RemovePatches(Harmony harmony)
        {
            // Handled by harmony.UnpatchAll
        }
    }
}
