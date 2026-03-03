using System;
using System.Reflection;
using HarmonyLib;
using Terraria;

namespace Randomizer.Modules
{
    /// <summary>
    /// Randomizes what items drop when tiles are broken (mining, chopping, etc.).
    /// Each tile break produces a random item from a curated pool of tile-droppable items.
    /// Patches Item.NewItem and filters by EntitySource_TileBreak.
    /// </summary>
    public class TileDropsModule : ModuleBase
    {
        public override string Id => "tile_drops";
        public override string Name => "Tile Drop Shuffle";
        public override string Description => "Mining and chopping drops random items";
        public override string Tooltip => "Breaking tiles (mining, chopping trees, etc.) drops a random item from the tile-drop pool each time. Every swing is a surprise.";

        internal static TileDropsModule Instance;

        // Curated pool of items that actually drop from tiles when mined.
        // All IDs verified from _REFERENCE_SOURCE_READ_ONLY/.../ID/ItemID.cs
        // and traced from WorldGen.KillTile_GetItemDrops in the decomp.
        private static readonly int[] TileDropPool = {
            // Terrain blocks (verified)
            2,    // DirtBlock
            3,    // StoneBlock
            61,   // EbonstoneBlock
            133,  // ClayBlock
            169,  // SandBlock
            170,  // Glass
            172,  // AshBlock
            173,  // Obsidian
            174,  // Hellstone
            176,  // MudBlock
            370,  // EbonsandBlock
            408,  // PearlsandBlock
            409,  // PearlstoneBlock
            424,  // SiltBlock
            502,  // CrystalShard
            586,  // CandyCaneBlock
            593,  // SnowBlock
            664,  // IceBlock
            762,  // SlimeBlock
            763,  // FleshBlock
            836,  // CrimstoneBlock
            1101, // LihzahrdBrick
            1103, // SlushBlock
            1124, // Hive
            3081, // Marble
            3086, // Granite
            3271, // Sandstone
            3272, // HardenedSand
            4564, // BambooBlock
            5349, // ShimmerBlock (verified from decomp — NOT 4988 which is QueenSlimeCrystal)
            5398, // ShimmerBrick (verified from decomp — NOT 5400 which is DirtiestBlock)

            // Ores (verified from ItemID.cs)
            11,   // IronOre
            12,   // CopperOre
            13,   // GoldOre
            14,   // SilverOre
            56,   // DemoniteOre
            116,  // Meteorite
            364,  // CobaltOre
            365,  // MythrilOre
            366,  // AdamantiteOre
            699,  // TinOre
            700,  // LeadOre
            701,  // TungstenOre
            702,  // PlatinumOre
            880,  // CrimtaneOre
            947,  // ChlorophyteOre
            1104, // PalladiumOre
            1105, // OrichalcumOre
            1106, // TitaniumOre
            3460, // LunarOre

            // Gems (verified from ItemID.cs)
            177,  // Sapphire
            178,  // Ruby
            179,  // Emerald
            180,  // Topaz
            181,  // Amethyst
            182,  // Diamond
            999,  // Amber

            // Wood (verified from ItemID.cs)
            9,    // Wood
            619,  // Ebonwood
            620,  // RichMahogany
            621,  // Pearlwood
            911,  // Shadewood
            1729, // SpookyWood
            2260, // DynastyWood
            2503, // BorealWood
            2504, // PalmWood
            5215, // AshWood

            // Bars (verified from ItemID.cs — from bar tile case 239)
            19,   // GoldBar
            20,   // CopperBar
            21,   // SilverBar
            22,   // IronBar
            57,   // DemoniteBar
            117,  // MeteoriteBar
            175,  // HellstoneBar
            381,  // CobaltBar
            382,  // MythrilBar
            391,  // AdamantiteBar
            703,  // TinBar
            704,  // LeadBar
            705,  // TungstenBar
            706,  // PlatinumBar
            1006, // ChlorophyteBar
            1184, // PalladiumBar
            1191, // OrichalcumBar
            1198, // TitaniumBar
            1225, // HallowedBar
            1257, // CrimtaneBar (verified from decomp)
            1552, // ShroomiteBar (verified from decomp)
            3261, // SpectreBar (verified from decomp)
            3467, // LunarBar (verified from decomp — was missing)

            // Torches (from KillTile_GetItemDrops case 4)
            8,    // Torch
            427, 428, 429, 430, 431, 432, 433, // Colored torches
            523,  // CursedTorch
            974,  // IceTorch
            1245, // Style 10 torch
            1333, // IchorTorch
            2274, // UltrabrightTorch
            3004, // BoneTorch
            3045, // RainbowTorch
            3114, // Style 15 torch
            4383, // DesertTorch
            4384, // CoralTorch
            4385, 4386, 4387, 4388, // Biome torches
            5293, // Style 22 torch
            5353, // ShimmerTorch

            // Bricks (verified from ItemID.cs)
            129,  // GrayBrick
            131,  // RedBrick
            134,  // BlueBrick
            137,  // GreenBrick
            139,  // PinkBrick
            141,  // GoldBrick
            143,  // SilverBrick
            145,  // CopperBrick

            // Natural drops (verified from ItemID.cs)
            5,    // Mushroom
            27,   // Acorn
            60,   // VileMushroom
            62,   // GrassSeeds
            150,  // Cobweb
            183,  // GlowingMushroom
            194,  // MushroomGrassSeeds
            195,  // JungleGrassSeeds
            208,  // JungleRose
            223,  // NaturesGift
            275,  // Coral
            276,  // Cactus
            331,  // JungleSpores
            1725, // Pumpkin

            // Herbs & seeds (from KillTile_GetItemDrops cases 83/84)
            307, 308, 309, 310, 311, 312, // Herb seeds
            313, 314, 315, 316, 317, 318, // Herbs
            2357, // ShiverthornSeeds (verified from decomp — FireblossomSeeds is 312, already in range above)
            2358, // Shiverthorn

            // Gemspark blocks (verified from ItemID.cs)
            1970, 1971, 1972, 1973, 1974, 1975, 1976,

            // Misc tile drops (from decomp trace)
            85,   // Chain
            149,  // Book
            154,  // Bone
            165,  // WaterBolt
            538,  // Switch
            580,  // Explosives
            751,  // BoneBlock
            765,  // LivingFireBlock
            965,  // Rope
            2340, // MinecartTrack
            2996, // VineRope
            3077, // SilkRope
            3078, // WebRope

            // Gem tree seeds (verified from ItemID.cs)
            4851, 4852, 4853, 4854, 4855, 4856, 4857,
        };

        public override void BuildShuffleMap()
        {
            Instance = this;
            RandomPool = TileDropPool;
            InitPoolRng();
        }

        public override void ApplyPatches(Harmony harmony)
        {
            Instance = this;

            try
            {
                // Find Item.NewItem overload: (IEntitySource source, int X, int Y, int Width, int Height, int Type, ...)
                MethodInfo targetMethod = null;
                foreach (var m in typeof(Item).GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    if (m.Name != "NewItem") continue;
                    var parms = m.GetParameters();
                    if (parms.Length >= 7 &&
                        parms[1].ParameterType == typeof(int) &&
                        parms[2].ParameterType == typeof(int) &&
                        parms[5].ParameterType == typeof(int))
                    {
                        targetMethod = m;
                        break;
                    }
                }

                if (targetMethod != null)
                {
                    var prefix = typeof(TileDropsModule).GetMethod(nameof(NewItem_Prefix),
                        BindingFlags.Public | BindingFlags.Static);
                    harmony.Patch(targetMethod, prefix: new HarmonyMethod(prefix));
                    Log.Info("[Randomizer] Tile Drops: patched Item.NewItem");
                }
                else
                {
                    Log.Warn("[Randomizer] Tile Drops: could not find Item.NewItem");
                }
            }
            catch (Exception ex)
            {
                Log.Error($"[Randomizer] Tile Drops patch error: {ex.Message}");
            }
        }

        /// <summary>
        /// Prefix on Item.NewItem — picks a random item from the tile-drop pool
        /// when source is tile-related. Each drop is independently random.
        /// </summary>
        public static void NewItem_Prefix(object source, ref int Type)
        {
            if (Instance == null || !Instance.Enabled) return;
            if (Type <= 0) return;

            // Only shuffle for tile-break and tree-shake drops, not interactions/entities
            try
            {
                if (source == null) return;
                var sourceName = source.GetType().Name;
                if (sourceName != "EntitySource_TileBreak" &&
                    sourceName != "EntitySource_ShakeTree")
                    return;
            }
            catch { return; }

            int randomItem = Instance.GetRandomFromPool();
            if (randomItem > 0) Type = randomItem;
        }

        public override void RemovePatches(Harmony harmony)
        {
            // Handled by harmony.UnpatchAll
        }
    }
}
