using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Terraria;

namespace Randomizer.Modules
{
    /// <summary>
    /// Randomizes NPC shop inventories by postfixing Chest.SetupShop.
    /// </summary>
    public class ShopModule : ModuleBase
    {
        public override string Id => "shops";
        public override string Name => "Shop Shuffle";
        public override string Description => "NPC shops sell shuffled items";
        public override string Tooltip => "All NPC shop inventories are shuffled. Items keep their original prices. Same seed = same shops every time.";

        internal static ShopModule Instance;

        public override void BuildShuffleMap()
        {
            Instance = this;
            var pool = new List<int>();
            for (int i = 1; i <= 6144; i++) // ItemID.Count=6145, last valid=6144
            {
                pool.Add(i);
            }
            ShuffleMap = Seed.BuildShuffleMap(pool, Id);
        }

        public override void ApplyPatches(Harmony harmony)
        {
            Instance = this;

            try
            {
                var setupShop = typeof(Chest).GetMethod("SetupShop",
                    BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(int) }, null);

                if (setupShop != null)
                {
                    var postfix = typeof(ShopModule).GetMethod(nameof(SetupShop_Postfix),
                        BindingFlags.Public | BindingFlags.Static);
                    harmony.Patch(setupShop, postfix: new HarmonyMethod(postfix));
                    Log.Info("[Randomizer] Shop: patched Chest.SetupShop");
                }
                else
                {
                    Log.Warn("[Randomizer] Shop: could not find Chest.SetupShop");
                }
            }
            catch (Exception ex)
            {
                Log.Error($"[Randomizer] Shop patch error: {ex.Message}");
            }
        }

        /// <summary>
        /// Postfix on Chest.SetupShop — after vanilla populates the shop,
        /// swap item types using the shuffle map.
        /// </summary>
        public static void SetupShop_Postfix(Chest __instance)
        {
            if (Instance == null || !Instance.Enabled) return;

            try
            {
                var items = __instance.item;
                if (items == null) return;

                for (int i = 0; i < items.Length; i++)
                {
                    var item = items[i];
                    if (item == null) continue;

                    int type = item.type;
                    int stack = item.stack;
                    if (type <= 0) continue;

                    if (Instance.ShuffleMap != null &&
                        Instance.ShuffleMap.TryGetValue(type, out int newType) && newType != type)
                    {
                        item.SetDefaults(newType);
                        if (stack > 0) item.stack = stack;
                    }
                }
            }
            catch (Exception ex)
            {
                Instance?.Log?.Error($"[Randomizer] Shop postfix error: {ex.Message}");
            }
        }

        public override void RemovePatches(Harmony harmony)
        {
            // Handled by harmony.UnpatchAll
        }
    }
}
