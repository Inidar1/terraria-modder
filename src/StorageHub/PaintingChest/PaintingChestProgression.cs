using System;
using System.Collections.Generic;
using Terraria;
using Terraria.ID;

namespace StorageHub.PaintingChest
{
    /// <summary>
    /// Painting chest capacity progression tiers and chest item ID scanning.
    /// </summary>
    public static class PaintingChestProgression
    {
        public class CapacityTier
        {
            public int TargetLevel { get; set; }
            public int RequiredCount { get; set; }
            public int Capacity { get; set; }
            public int RequiredGameTier { get; set; }
        }

        public static readonly CapacityTier[] Tiers =
        {
            new CapacityTier { TargetLevel = 1, RequiredCount = 1, Capacity = 80, RequiredGameTier = 0 },
            new CapacityTier { TargetLevel = 2, RequiredCount = 5, Capacity = 200, RequiredGameTier = 0 },
            new CapacityTier { TargetLevel = 3, RequiredCount = 20, Capacity = 1000, RequiredGameTier = 2 },
            new CapacityTier { TargetLevel = 4, RequiredCount = 50, Capacity = 5000, RequiredGameTier = 3 },
        };

        public static int GetCapacity(int level)
        {
            switch (level)
            {
                case 0: return 40;
                case 1: return 80;
                case 2: return 200;
                case 3: return 1000;
                case 4: return 5000;
                default: return 40;
            }
        }

        public static CapacityTier GetNextTier(int currentLevel)
        {
            if (currentLevel < 0 || currentLevel >= Tiers.Length) return null;
            return Tiers[currentLevel];
        }

        // Cached chest item IDs (any item whose createTile is in BasicChest)
        private static int[] _chestItemIds;
        private static bool _chestItemIdsComputed;

        /// <summary>
        /// Get all item IDs that represent "chests" (items whose createTile is in TileID.Sets.BasicChest).
        /// Computed once and cached.
        /// </summary>
        public static int[] GetChestItemIds()
        {
            if (_chestItemIdsComputed) return _chestItemIds;
            _chestItemIdsComputed = true;

            try
            {
                var basicChest = TileID.Sets.BasicChest;
                var itemsByType = ContentSamples.ItemsByType;

                var result = new List<int>();
                foreach (var kvp in itemsByType)
                {
                    var item = kvp.Value;
                    if (item == null) continue;

                    int createTile = item.createTile;
                    if (createTile >= 0 && createTile < basicChest.Length && basicChest[createTile])
                    {
                        if (item.type > 0) result.Add(item.type);
                    }
                }

                _chestItemIds = result.ToArray();
            }
            catch
            {
                _chestItemIds = Array.Empty<int>();
            }

            return _chestItemIds;
        }

        /// <summary>
        /// Count total chest items available from an item counts dictionary.
        /// </summary>
        public static int CountChestItems(Dictionary<int, int> itemCounts)
        {
            var chestIds = GetChestItemIds();
            long total = 0;
            foreach (int id in chestIds)
            {
                if (itemCounts.TryGetValue(id, out int count))
                    total += count;
            }
            return total > int.MaxValue ? int.MaxValue : (int)total;
        }
    }
}
