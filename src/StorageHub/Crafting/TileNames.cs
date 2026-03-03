using System.Collections.Generic;

namespace StorageHub.Crafting
{
    /// <summary>
    /// Lookup for Terraria tile IDs to human-readable station names.
    /// Verified against TileID.cs decomp for 1.4.5.
    /// </summary>
    public static class TileNames
    {
        private static readonly Dictionary<int, string> Names = new Dictionary<int, string>
        {
            // All crafting station tiles (matches CraftingStations.AllTileIds)
            { 13, "Bottle" },
            { 16, "Iron/Lead Anvil" },
            { 17, "Furnace" },
            { 18, "Work Bench" },
            { 26, "Demon/Crimson Altar" },
            { 77, "Hellforge" },
            { 86, "Loom" },
            { 94, "Keg" },
            { 96, "Cooking Pot" },
            { 101, "Bookcase" },
            { 106, "Sawmill" },
            { 114, "Tinkerer's Workshop" },
            { 125, "Crystal Ball" },
            { 133, "Adamantite/Titanium Forge" },
            { 134, "Mythril/Orichalcum Anvil" },
            { 215, "Campfire" },
            { 217, "Blend-O-Matic" },
            { 218, "Meat Grinder" },
            { 220, "Solidifier" },
            { 228, "Dye Vat" },
            { 243, "Imbuing Station" },
            { 247, "Autohammer" },
            { 283, "Heavy Work Bench" },
            { 300, "Bone Welder" },
            { 301, "Flesh Cloning Vat" },
            { 302, "Glass Kiln" },
            { 303, "Lihzahrd Furnace" },
            { 304, "Living Loom" },
            { 305, "Sky Mill" },
            { 306, "Ice Machine" },
            { 307, "Steampunk Boiler" },
            { 308, "Honey Dispenser" },
            { 355, "Alchemy Table" },
            { 412, "Ancient Manipulator" },
            { 622, "Tea Kettle" },
            { 699, "Potion Station" },
        };

        /// <summary>
        /// Get the display name for a tile ID.
        /// Returns the tile ID in parentheses if not found.
        /// </summary>
        public static string GetName(int tileId)
        {
            if (Names.TryGetValue(tileId, out string name))
            {
                return name;
            }
            return $"Station #{tileId}";
        }

        /// <summary>
        /// Get the display name with tile ID for debugging.
        /// </summary>
        public static string GetNameWithId(int tileId)
        {
            if (Names.TryGetValue(tileId, out string name))
            {
                return $"{name} (#{tileId})";
            }
            return $"Unknown #{tileId}";
        }
    }
}
