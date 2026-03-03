using System;
using System.Collections.Generic;
using Terraria;
using Terraria.ID;
using StorageHub.Config;
using StorageHub.Storage;
using TerrariaModder.Core.Logging;

namespace StorageHub.Shared
{
    /// <summary>
    /// Single source of truth for item classification into categories.
    /// Replaces 3 separate implementations (StorageHubUI, CraftTab, RecipesTab).
    ///
    /// Priority order (first match wins):
    /// 1. Tools (pick/axe/hammer > 0)
    /// 2. Weapons (damage > 0)
    /// 3. Armor (headSlot/bodySlot/legSlot > -1)
    /// 4. Accessories
    /// 5. Placeable (createTile >= 0 || createWall >= 0)
    /// 6. Consumables
    /// 7. Materials
    /// 8. Misc (fallback)
    /// </summary>
    public static class ItemClassifier
    {
        // Cached classification by item type ID (built from ContentSamples)
        private static Dictionary<int, CategoryFilter> _cache;
        private static bool _cacheBuilt;

        /// <summary>
        /// Get the classification cache. Built lazily from ContentSamples on first call.
        /// Returns empty dictionary if ContentSamples isn't available.
        /// </summary>
        public static Dictionary<int, CategoryFilter> GetClassificationCache(ILogger log)
        {
            if (!_cacheBuilt)
            {
                _cache = BuildCache(log);
                _cacheBuilt = true;
            }
            return _cache;
        }

        /// <summary>
        /// Classify an item by its type ID using the ContentSamples cache.
        /// Falls back to Misc if the item isn't in the cache.
        /// </summary>
        public static CategoryFilter Classify(int itemId, ILogger log)
        {
            var cache = GetClassificationCache(log);
            return cache.TryGetValue(itemId, out var cat) ? cat : CategoryFilter.Misc;
        }

        /// <summary>
        /// Classify an ItemSnapshot using its boolean flags.
        /// Same priority order as the ContentSamples classifier.
        /// </summary>
        public static CategoryFilter Classify(ItemSnapshot item)
        {
            if (item.IsPickaxe || item.IsAxe || item.IsHammer)
                return CategoryFilter.Tools;
            if (item.Damage > 0)
                return CategoryFilter.Weapons;
            if (item.IsArmor)
                return CategoryFilter.Armor;
            if (item.IsAccessory)
                return CategoryFilter.Accessories;
            if (item.IsPlaceable)
                return CategoryFilter.Placeable;
            if (item.IsConsumable)
                return CategoryFilter.Consumables;
            if (item.IsMaterial)
                return CategoryFilter.Materials;
            return CategoryFilter.Misc;
        }

        /// <summary>
        /// Reset the cache (call on world unload to free memory).
        /// </summary>
        public static void Reset()
        {
            _cache = null;
            _cacheBuilt = false;
        }

        private static Dictionary<int, CategoryFilter> BuildCache(ILogger log)
        {
            var result = new Dictionary<int, CategoryFilter>();
            try
            {
                var dict = ContentSamples.ItemsByType;
                if (dict == null) return result;

                foreach (var entry in dict)
                {
                    int id = entry.Key;
                    Item item = entry.Value;

                    int damage = item.damage;
                    int pick = item.pick;
                    int axe = item.axe;
                    int hammer = item.hammer;
                    int headSlot = item.headSlot;
                    int bodySlot = item.bodySlot;
                    int legSlot = item.legSlot;
                    bool accessory = item.accessory;
                    bool consumable = item.consumable;
                    int createTile = item.createTile;
                    int createWall = item.createWall;
                    bool material = item.material;

                    CategoryFilter cat;
                    if (pick > 0 || axe > 0 || hammer > 0)
                        cat = CategoryFilter.Tools;
                    else if (damage > 0)
                        cat = CategoryFilter.Weapons;
                    else if (headSlot > -1 || bodySlot > -1 || legSlot > -1)
                        cat = CategoryFilter.Armor;
                    else if (accessory)
                        cat = CategoryFilter.Accessories;
                    else if (createTile >= 0 || createWall >= 0)
                        cat = CategoryFilter.Placeable;
                    else if (consumable)
                        cat = CategoryFilter.Consumables;
                    else if (material)
                        cat = CategoryFilter.Materials;
                    else
                        cat = CategoryFilter.Misc;

                    result[id] = cat;
                }

                log.Debug($"ItemClassifier: Classified {result.Count} items");
            }
            catch (Exception ex)
            {
                log.Error($"ItemClassifier: Failed to build cache: {ex.Message}");
            }
            return result;
        }
    }
}
