using TerrariaModder.Core.Config;

namespace Randomizer
{
    public class RandomizerConfig : ModConfig
    {
        public override int Version => 1;

        [Client, Label("Enabled"), Description("Enable the Randomizer mod.")]
        public bool Enabled { get; set; } = true;

        [Client, Label("Seed"), Description("Randomization seed (0 = random each time).")]
        public int Seed { get; set; } = 0;

        // Module toggles
        [Client, Label("Chest Loot Shuffle"), Description("Shuffle chest contents when entering an unconfigured world. Existing world settings stay locked.")]
        public bool ModuleChestLoot { get; set; } = false;

        [Client, Label("Enemy Drop Shuffle"), Description("Shuffle items dropped by enemies.")]
        public bool ModuleEnemyDrops { get; set; } = false;

        [Client, Label("Recipe Shuffle"), Description("Shuffle crafting recipe outputs (ingredients unchanged).")]
        public bool ModuleRecipes { get; set; } = false;

        [Client, Label("Shop Shuffle"), Description("Shuffle NPC shop inventories.")]
        public bool ModuleShops { get; set; } = false;

        [Client, Label("Fishing Shuffle"), Description("Shuffle fishing catches.")]
        public bool ModuleFishing { get; set; } = false;

        [Client, Label("Tile Drop Shuffle"), Description("Shuffle items dropped by mined tiles.")]
        public bool ModuleTileDrops { get; set; } = false;

        [Client, Label("Spawn Shuffle"), Description("Shuffle enemy spawn types.")]
        public bool ModuleSpawns { get; set; } = false;

        [Client, Label("Item Stat Scramble"), Description("Scramble item stats.")]
        public bool ModuleItemStats { get; set; } = false;

        [Client, Label("Starting Inventory"), Description("Replace starter copper tools in an unconfigured world. Existing items are preserved; existing world settings stay locked.")]
        public bool ModuleStartingItems { get; set; } = false;

        [Client, Label("Gravity Chaos"), Description("Randomly change gravity strength (0.25x to 2.5x).")]
        public bool ModuleGravity { get; set; } = false;

        [Client, Label("Weather Chaos"), Description("Randomly change weather conditions.")]
        public bool ModuleWeather { get; set; } = false;
    }
}
