using System;
using System.Collections.Generic;
using HarmonyLib;
using Terraria;

namespace Randomizer.Modules
{
    /// <summary>
    /// Randomizes chest loot by swapping item types in all world chests on world load.
    /// </summary>
    public class ChestLootModule : ModuleBase
    {
        public override string Id => "chest_loot";
        public override string Name => "Chest Loot Shuffle";
        public override string Description => "Swap items in all world chests";
        public override string Tooltip => "Swaps item types in all world chests. Same seed always produces the same swaps. Requires world reload to re-randomize.";
        public override bool IsWorldGen => true;

        private static ChestLootModule _instance;

        public override void BuildShuffleMap()
        {
            _instance = this;

            // Build pool of valid item IDs for chest loot (ItemID.Count=6145, last valid=6144)
            var pool = new List<int>();
            for (int i = 1; i <= 6144; i++)
            {
                pool.Add(i);
            }
            ShuffleMap = Seed.BuildShuffleMap(pool, Id);

            // Apply the shuffle to existing chests in the world
            ApplyToWorldChests();
        }

        private void ApplyToWorldChests()
        {
            try
            {
                var chests = Main.chest;
                if (chests == null) return;

                int chestCount = 0;
                int itemCount = 0;

                for (int c = 0; c < chests.Length; c++)
                {
                    var chest = chests[c];
                    if (chest == null) continue;

                    var items = chest.item;
                    if (items == null) continue;

                    bool modified = false;
                    for (int i = 0; i < items.Length; i++)
                    {
                        var item = items[i];
                        if (item == null) continue;

                        int type = item.type;
                        int stack = item.stack;
                        if (type <= 0 || stack <= 0) continue;

                        if (ShuffleMap.TryGetValue(type, out int newType) && newType != type)
                        {
                            item.SetDefaults(newType);
                            item.stack = stack; // Preserve stack
                            modified = true;
                            itemCount++;
                        }
                    }
                    if (modified) chestCount++;
                }

                Log.Info($"[Randomizer] Chest Loot: shuffled {itemCount} items in {chestCount} chests");
            }
            catch (Exception ex)
            {
                Log.Error($"[Randomizer] Chest Loot error: {ex.Message}");
            }
        }

        public override void ApplyPatches(Harmony harmony)
        {
            // Chest loot doesn't need runtime patches — it modifies chests on world load
        }

        public override void RemovePatches(Harmony harmony)
        {
            // No patches to remove
        }
    }
}
