using System.Collections.Generic;

namespace StorageHub.Shared
{
    /// <summary>
    /// Single source of truth for crafting station tile IDs.
    /// All tile IDs verified against TileID.cs and Recipe.cs decomp for 1.4.5.
    ///
    /// Used by: StationDetector, TileNames, NetworkTab.
    /// DO NOT duplicate these lists elsewhere.
    /// </summary>
    public static class CraftingStations
    {
        /// <summary>
        /// Station info: tile ID, display name, and whether it's an equivalence-only tile.
        /// Equivalence-only tiles (like Alchemy Table) resolve to another tile via TileCountsAs
        /// and are not direct Recipe.requiredTile values.
        /// </summary>
        public struct StationInfo
        {
            public int TileId;
            public string Name;
            public bool IsEquivalenceOnly;

            public StationInfo(int tileId, string name, bool isEquivalenceOnly = false)
            {
                TileId = tileId;
                Name = name;
                IsEquivalenceOnly = isEquivalenceOnly;
            }
        }

        /// <summary>
        /// All crafting station tiles — the master list.
        /// </summary>
        public static readonly StationInfo[] AllStations =
        {
            // Tiles that appear directly in Recipe.requiredTile assignments
            new StationInfo(13,  "Bottle"),
            new StationInfo(16,  "Iron/Lead Anvil"),
            new StationInfo(17,  "Furnace"),
            new StationInfo(18,  "Work Bench"),
            new StationInfo(26,  "Demon/Crimson Altar"),
            new StationInfo(77,  "Hellforge"),
            new StationInfo(86,  "Loom"),
            new StationInfo(94,  "Keg"),
            new StationInfo(96,  "Cooking Pot"),
            new StationInfo(101, "Bookcase"),
            new StationInfo(106, "Sawmill"),
            new StationInfo(114, "Tinkerer's Workshop"),
            new StationInfo(125, "Crystal Ball"),
            new StationInfo(133, "Adamantite/Titanium Forge"),
            new StationInfo(134, "Mythril/Orichalcum Anvil"),
            new StationInfo(215, "Campfire"),
            new StationInfo(217, "Blend-O-Matic"),
            new StationInfo(218, "Meat Grinder"),
            new StationInfo(220, "Solidifier"),
            new StationInfo(228, "Dye Vat"),
            new StationInfo(243, "Imbuing Station"),
            new StationInfo(247, "Autohammer"),
            new StationInfo(283, "Heavy Work Bench"),
            new StationInfo(300, "Bone Welder"),
            new StationInfo(301, "Flesh Cloning Vat"),
            new StationInfo(302, "Glass Kiln"),
            new StationInfo(303, "Lihzahrd Furnace"),
            new StationInfo(304, "Living Loom"),
            new StationInfo(305, "Sky Mill"),
            new StationInfo(306, "Ice Machine"),
            new StationInfo(307, "Steampunk Boiler"),
            new StationInfo(308, "Honey Dispenser"),
            new StationInfo(412, "Ancient Manipulator"),
            new StationInfo(622, "Tea Kettle"),
            // Physical tiles that resolve to recipe tiles via TileCountsAs
            new StationInfo(355, "Alchemy Table", isEquivalenceOnly: true),   // → Bottles (13)
            new StationInfo(699, "Potion Station", isEquivalenceOnly: true),  // → Bottles (13)
        };

        /// <summary>
        /// HashSet of all crafting station tile IDs (including equivalence tiles).
        /// Used by StationDetector for detection.
        /// </summary>
        public static readonly HashSet<int> AllTileIds;

        /// <summary>
        /// Array of tile IDs to display in UI (excludes equivalence-only tiles).
        /// Used by NetworkTab for display.
        /// </summary>
        public static readonly int[] DisplayTileIds;

        /// <summary>
        /// Dictionary of tile ID → display name.
        /// Used by TileNames for lookups.
        /// </summary>
        public static readonly Dictionary<int, string> Names;

        static CraftingStations()
        {
            AllTileIds = new HashSet<int>();
            Names = new Dictionary<int, string>();
            var displayList = new List<int>();

            foreach (var station in AllStations)
            {
                AllTileIds.Add(station.TileId);
                Names[station.TileId] = station.Name;
                if (!station.IsEquivalenceOnly)
                    displayList.Add(station.TileId);
            }

            DisplayTileIds = displayList.ToArray();
        }

        /// <summary>
        /// Get the display name for a tile ID. Returns "Station #ID" if not found.
        /// </summary>
        public static string GetName(int tileId)
        {
            return Names.TryGetValue(tileId, out string name) ? name : $"Station #{tileId}";
        }

        /// <summary>
        /// Get the display name with tile ID for debugging.
        /// </summary>
        public static string GetNameWithId(int tileId)
        {
            return Names.TryGetValue(tileId, out string name)
                ? $"{name} (#{tileId})"
                : $"Unknown #{tileId}";
        }
    }
}
