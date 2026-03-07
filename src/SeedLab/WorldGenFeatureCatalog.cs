using System.Collections.Generic;

namespace SeedLab
{
    public class WGSeedDef
    {
        public string Id;
        public string DisplayName;
        public SeedKind Kind;

        // Special seeds: Main.* flag + WorldGen.* alias
        public string FlagField;       // e.g. "getGoodWorld"
        public string WorldGenAlias;   // e.g. "getGoodWorldGen"

        // Secret seeds: SecretSeed instance field + optional Main.* runtime flag
        public string SecretSeedField; // e.g. "paintEverythingGray"
        public string MainFlagField;   // e.g. "vampireSeed" (null if none)

        public WGFeatureGroupDef[] Groups;

        /// <summary>Special seed constructor.</summary>
        public WGSeedDef(string id, string displayName, string flagField, string worldGenAlias, WGFeatureGroupDef[] groups)
        {
            Id = id; DisplayName = displayName; Kind = SeedKind.SpecialSeed;
            FlagField = flagField; WorldGenAlias = worldGenAlias; Groups = groups;
        }

        /// <summary>Secret seed constructor (single group).</summary>
        public static WGSeedDef Secret(string id, string displayName, string field, string desc,
            string category = "Misc", string mainFlagField = null,
            string[] passNames = null, string[] finalizeMethods = null)
        {
            return new WGSeedDef
            {
                Id = id, DisplayName = displayName, Kind = SeedKind.SecretSeed,
                SecretSeedField = field, MainFlagField = mainFlagField,
                Groups = new[] { new WGFeatureGroupDef(id, displayName, desc,
                    category: category, passNames: passNames, finalizeMethodNames: finalizeMethods) }
            };
        }

        /// <summary>Custom SeedLab feature (not tied to any vanilla seed).</summary>
        public static WGSeedDef Custom(string id, string displayName, string desc, string category = "Misc")
        {
            return new WGSeedDef
            {
                Id = id, DisplayName = displayName, Kind = SeedKind.Custom,
                Groups = new[] { new WGFeatureGroupDef(id, displayName, desc, category: category) }
            };
        }

        private WGSeedDef() { }
    }

    public class WGFeatureGroupDef
    {
        public string Id;
        public string DisplayName;
        public string Description;
        public string Category;              // "Terrain", "Structures", "Ores & Resources", etc.
        public string EasyGroup;             // Groups with same EasyGroup under same seed collapse in Easy mode
        public string[] PassNames;           // Gen passes where this group's flag override applies
        public string[] FinalizeMethodNames; // Do*() methods in FinalizeSecretSeeds
        public string[] Conflicts;           // Other group IDs that conflict with this

        public WGFeatureGroupDef(string id, string displayName, string description,
            string category = "Misc", string easyGroup = null,
            string[] passNames = null, string[] finalizeMethodNames = null, string[] conflicts = null)
        {
            Id = id; DisplayName = displayName; Description = description;
            Category = category ?? "Misc";
            EasyGroup = easyGroup ?? id; // default: each group is its own easy group
            PassNames = passNames ?? new string[0];
            FinalizeMethodNames = finalizeMethodNames ?? new string[0];
            Conflicts = conflicts ?? new string[0];
        }
    }

    /// <summary>
    /// Catalog of world-generation features organized by seed with per-group granularity.
    /// Special seeds are decomposed into feature groups (e.g., FTW Ores, FTW Traps).
    /// Secret seeds are one group each. Each group maps to gen passes or finalize methods.
    ///
    /// Pass names are GenPassNameID string values from the Terraria decomp
    /// (e.g., "Shinies" for OresAndShinies, "Corruption" for CorruptionAndCrimson).
    /// </summary>
    public static class WorldGenFeatureCatalog
    {
        public static readonly Dictionary<string, string> MainToWorldGenAlias = new Dictionary<string, string>
        {
            { "getGoodWorld", "getGoodWorldGen" },
            { "drunkWorld", "drunkWorldGen" },
            { "tenthAnniversaryWorld", "tenthAnniversaryWorldGen" },
            { "notTheBeesWorld", "notTheBees" },
            { "remixWorld", "remixWorldGen" },
            { "noTrapsWorld", "noTrapsWorldGen" },
            { "zenithWorld", "everythingWorldGen" },
            { "dontStarveWorld", "dontStarveWorldGen" },
            { "skyblockWorld", "skyblockWorldGen" },
        };

        public static readonly WGSeedDef[] Seeds = new[]
        {
            // =================================================================
            // SPECIAL SEEDS (9) — decomposed into feature groups per gen pass
            // =================================================================

            // --- FOR THE WORTHY (55+ flag checks, 18 passes) ---
            new WGSeedDef("ftw", "For the Worthy", "getGoodWorld", "getGoodWorldGen", new[]
            {
                new WGFeatureGroupDef("ftw_ores", "Ores & Resources",
                    "Replaces certain ores with upgraded versions and changes ore generation",
                    category: "Ores & Resources",
                    passNames: new[] { "Shinies", "Surface Ore and Stone" }),
                new WGFeatureGroupDef("ftw_traps", "Traps & Hazards",
                    "More traps, boulders, and explosive hazards throughout the world",
                    category: "Traps & Hazards",
                    passNames: new[] { "Traps", "Micro Biomes", "Shell Piles", "Remove Broken Traps" },
                    conflicts: new[] { "notraps_worldgen" }),
                new WGFeatureGroupDef("ftw_structures", "Structures",
                    "Modified dungeon, floating islands, underworld, and temple generation",
                    category: "Structures",
                    passNames: new[] { "Dungeon", "Floating Islands", "Floating Island Houses", "Underworld", "Jungle Temple", "Temple", "Hives" },
                    conflicts: new[] { "remix_structures", "drunk_structures" }),
                new WGFeatureGroupDef("ftw_loot", "Loot & Chests",
                    "Changed chest contents and loot tables",
                    category: "Loot & Chests", easyGroup: "ftw_loot",
                    passNames: new[] { "Buried Chests", "Surface Chests", "Jungle Chests Placement", "Water Chests", "Pots", "Hellforge" }),
                new WGFeatureGroupDef("ftw_life", "Life Crystals",
                    "Reduces the number of life crystals generated",
                    category: "Loot & Chests", easyGroup: "ftw_loot",
                    passNames: new[] { "Life Crystals" }),
                new WGFeatureGroupDef("ftw_terrain", "Terrain",
                    "Cave sizes, surface features, biome shapes, and spawn point changes",
                    category: "Terrain",
                    passNames: new[] { "Terrain", "Tunnels", "Corruption", "Beaches", "Living Trees", "Smooth World", "Full Desert", "Generate Ice Biome", "Spawn Point" },
                    conflicts: new[] { "remix_terrain", "drunk_terrain" }),
                new WGFeatureGroupDef("ftw_enemies", "Enemies & Spawns",
                    "Spider density, mountain cave spawning, and creature placement",
                    category: "Enemies & Spawns",
                    passNames: new[] { "Mountain Caves", "Spider Caves", "Spreading Grass" }),
                new WGFeatureGroupDef("ftw_hardening", "Tile Hardening",
                    "Block replacements and hardened tile variants across the world",
                    category: "Terrain", easyGroup: "ftw_terrain",
                    passNames: new[] { "Tile Cleanup", "Quick Cleanup", "Clean Up Dirt" }),
            }),

            // --- DRUNK WORLD (116 flag checks, 15+ passes) ---
            new WGSeedDef("drunk", "Drunk World", "drunkWorld", "drunkWorldGen", new[]
            {
                new WGFeatureGroupDef("drunk_evil", "Dual Evil Biomes",
                    "Generates both Corruption and Crimson in the same world",
                    category: "Biomes",
                    passNames: new[] { "Corruption" }),
                new WGFeatureGroupDef("drunk_ores", "Alternating Ore Tiers",
                    "Copper/Iron/Silver/Gold randomly swap between normal and decorative ore types",
                    category: "Ores & Resources",
                    passNames: new[] { "Shinies" }),
                new WGFeatureGroupDef("drunk_structures", "Structure Changes",
                    "Dungeon side flip, guaranteed pyramids, ocean caves, and beehive depth changes",
                    category: "Structures",
                    passNames: new[] { "Dungeon", "Floating Islands", "Floating Island Houses", "Underworld", "Pyramids", "Shimmer", "Create Ocean Caves", "Hives", "Dunes" },
                    conflicts: new[] { "ftw_structures", "remix_structures" }),
                new WGFeatureGroupDef("drunk_terrain", "Terrain Changes",
                    "Modified cave systems, surface terrain, and biome placement",
                    category: "Terrain",
                    passNames: new[] { "Terrain", "Tunnels", "Beaches", "Full Desert", "Generate Ice Biome", "Jungle", "Mud Caves To Grass", "Lakes" },
                    conflicts: new[] { "ftw_terrain", "remix_terrain" }),
                new WGFeatureGroupDef("drunk_trees", "Tree Density",
                    "Increased living tree density and satellite tree spread",
                    category: "Trees & Plants",
                    passNames: new[] { "Living Trees" }),
                new WGFeatureGroupDef("drunk_npcs", "Starter NPCs",
                    "Painter and Tavernkeep spawn as starter NPCs instead of default choices",
                    category: "Enemies & Spawns",
                    passNames: new[] { "Guide" }),
                new WGFeatureGroupDef("drunk_traps", "Trap Pranks",
                    "Adds extra progress bar pranks and trap placement in drunk-exclusive areas",
                    category: "Traps & Hazards",
                    passNames: new[] { "Traps" }),
            }),

            // --- DON'T DIG UP / REMIX (211 flag checks, 51 passes) ---
            new WGSeedDef("remix", "Don't Dig Up", "remixWorld", "remixWorldGen", new[]
            {
                new WGFeatureGroupDef("remix_terrain", "Terrain Flip",
                    "Cave expansion, tunnel restructuring, and underworld modifications — the core remix mechanic",
                    category: "Terrain", easyGroup: "remix_world_flip",
                    passNames: new[] { "Terrain", "Tunnels", "Small Holes", "Dirt Layer Caves", "Rock Layer Caves", "Surface Caves", "Underworld", "Mount Caves" },
                    conflicts: new[] { "ftw_terrain", "drunk_terrain" }),
                new WGFeatureGroupDef("remix_biomes", "Biome Rearrangement",
                    "Evil biome spreading doubles, center placement allowed, ice/desert/mushroom biome depth changes",
                    category: "Biomes", easyGroup: "remix_world_flip",
                    passNames: new[] { "Corruption", "Generate Ice Biome", "Full Desert", "Mushroom Patches", "Grass", "Jungle", "Marble", "Granite" }),
                new WGFeatureGroupDef("remix_structures", "Structure Relocation",
                    "Floating islands, dungeon, temple, shimmer, and ocean caves placed at flipped depths",
                    category: "Structures",
                    passNames: new[] { "Floating Islands", "Floating Island Houses", "Dungeon", "Create Ocean Caves", "Shimmer", "Statues", "Cave Walls", "Pyramids" },
                    conflicts: new[] { "ftw_structures", "drunk_structures" }),
                new WGFeatureGroupDef("remix_ores", "Ore Redistribution",
                    "Ore veins distributed across full world height instead of fixed layer depths",
                    category: "Ores & Resources",
                    passNames: new[] { "Shinies", "Surface Ore and Stone" }),
                new WGFeatureGroupDef("remix_loot", "Treasure Relocation",
                    "Life crystals, buried/surface/underwater chests placed at restructured depths",
                    category: "Loot & Chests",
                    passNames: new[] { "Life Crystals", "Buried Chests", "Surface Chests", "Water Chests", "Pots", "Hellforge", "Jungle Chests Placement" }),
                new WGFeatureGroupDef("remix_liquids", "Liquid Restructuring",
                    "Lakes, lava, and cave walls use restructured height tiers for liquid placement",
                    category: "Liquids", easyGroup: "remix_world_flip",
                    passNames: new[] { "Settle Liquids", "Lakes", "Ice", "Wall Variety", "Waterfalls" }),
                new WGFeatureGroupDef("remix_vegetation", "Vegetation Changes",
                    "Living trees, moss caves, and biome vegetation at new depths",
                    category: "Trees & Plants",
                    passNames: new[] { "Living Trees", "Moss", "Beaches", "Smooth World", "Mud Caves To Grass", "Spider Caves" }),
                new WGFeatureGroupDef("remix_traps", "Trap Modifications",
                    "Trap placement adapted for remix world structure, enables traps on NotTheBees worlds",
                    category: "Traps & Hazards",
                    passNames: new[] { "Traps", "Remove Broken Traps" }),
                new WGFeatureGroupDef("remix_spawn", "Spawn & Cleanup",
                    "Spawn point logic, grass spreading, and tile cleanup adapted for flipped world",
                    category: "Misc", easyGroup: "remix_world_flip",
                    passNames: new[] { "Spawn Point", "Spreading Grass", "Tile Cleanup", "Quick Cleanup" }),
            }),

            // --- DON'T STARVE (43 flag checks, 14 passes) ---
            new WGSeedDef("ds", "Don't Starve", "dontStarveWorld", "dontStarveWorldGen", new[]
            {
                new WGFeatureGroupDef("ds_terrain", "Surface & Caves",
                    "Modified surface terrain, cave systems, and wavy cave generation",
                    category: "Terrain", easyGroup: "ds_world",
                    passNames: new[] { "Terrain", "Tunnels", "Wavy Caves", "Corruption" }),
                new WGFeatureGroupDef("ds_structures", "Structure Placement",
                    "Dungeon positioning, floating islands, ocean caves, and living tree changes",
                    category: "Structures", easyGroup: "ds_world",
                    passNames: new[] { "Dungeon", "Floating Islands", "Create Ocean Caves", "Living Trees", "Shimmer" }),
                new WGFeatureGroupDef("ds_spiders", "Spider Caverns",
                    "Increased spider cavern frequency and modified placement",
                    category: "Enemies & Spawns",
                    passNames: new[] { "Mountain Caves", "Spider Caves" }),
                new WGFeatureGroupDef("ds_cleanup", "World Cleanup",
                    "Graveyard generation, tile/wall cleanup, and starter NPC changes",
                    category: "Misc", easyGroup: "ds_world",
                    passNames: new[] { "Pots", "Clean Up Dirt", "Guide" }),
            }),

            // --- NOT THE BEES (106 flag checks, 16 passes) ---
            new WGSeedDef("ntb", "Not the Bees", "notTheBeesWorld", "notTheBees", new[]
            {
                new WGFeatureGroupDef("ntb_terrain", "Hive Conversion",
                    "Converts most terrain to hive blocks, modifies cave systems for bee world",
                    category: "Terrain", easyGroup: "ntb_beefication",
                    passNames: new[] { "Terrain", "Tunnels", "Corruption", "Generate Ice Biome", "Full Desert", "Jungle", "Smooth World" }),
                new WGFeatureGroupDef("ntb_liquids", "Honey Mechanics",
                    "Fills caves with honey, modifies liquid settling behavior",
                    category: "Liquids", easyGroup: "ntb_beefication",
                    passNames: new[] { "Lakes", "Settle Liquids", "Settle Liquids Again" }),
                new WGFeatureGroupDef("ntb_structures", "Structure Changes",
                    "Modified dungeon, floating islands, hive generation, and living trees",
                    category: "Structures",
                    passNames: new[] { "Dungeon", "Floating Islands", "Hives", "Living Trees" }),
                new WGFeatureGroupDef("ntb_loot", "Treasure & Traps",
                    "Modified chest placement and trap restrictions for bee-themed world",
                    category: "Loot & Chests",
                    passNames: new[] { "Buried Chests", "Surface Chests", "Traps" }),
            }),

            // --- CELEBRATION / 10TH ANNIVERSARY (91 flag checks, 20+ passes) ---
            new WGSeedDef("celebration", "Celebration", "tenthAnniversaryWorld", "tenthAnniversaryWorldGen", new[]
            {
                new WGFeatureGroupDef("celebration_terrain", "Center-Focused Terrain",
                    "Tunnels and features placed primarily in center 20-80% of world",
                    category: "Terrain", easyGroup: "celebration_world",
                    passNames: new[] { "Terrain", "Tunnels", "Small Holes", "Corruption", "Lakes" }),
                new WGFeatureGroupDef("celebration_structures", "Anniversary Structures",
                    "Golden boulders, enchanted sword shrines doubled, shimmer pools, expanded gem caves",
                    category: "Structures", easyGroup: "celebration_world",
                    passNames: new[] { "Floating Islands", "Floating Island Houses", "Micro Biomes", "Shimmer", "Gem Caves", "Create Ocean Caves", "Dungeon" }),
                new WGFeatureGroupDef("celebration_loot", "Abundant Resources",
                    "20% more life crystals, modified underwater chest loot, 1.5x gem caves",
                    category: "Loot & Chests",
                    passNames: new[] { "Life Crystals", "Water Chests", "Buried Chests", "Surface Chests", "Moss" }),
                new WGFeatureGroupDef("celebration_npcs", "NPC & Visual",
                    "Modified starter NPCs, tree planting, statues, graveyards, and slime column variants",
                    category: "Enemies & Spawns",
                    passNames: new[] { "Guide", "Planting Trees", "Statues", "Pots", "Final Cleanup" }),
                new WGFeatureGroupDef("celebration_traps", "Trap Balance",
                    "Adjusted trap density when combined with No Traps seed (5x vs 100x normal)",
                    category: "Traps & Hazards",
                    passNames: new[] { "Traps", "Remove Broken Traps" }),
            }),

            // --- NO TRAPS (36 flag checks) ---
            new WGSeedDef("notraps", "No Traps", "noTrapsWorld", "noTrapsWorldGen", new[]
            {
                new WGFeatureGroupDef("notraps_worldgen", "No Traps",
                    "Removes all traps from world generation and adds TNT barrels and other hazards",
                    category: "Traps & Hazards",
                    passNames: new[] { "Traps", "Remove Broken Traps", "Micro Biomes", "Piles" },
                    conflicts: new[] { "ftw_traps" }),
            }),

            // --- ZENITH (combines all seeds) ---
            new WGSeedDef("zenith", "Zenith", "zenithWorld", "everythingWorldGen", new[]
            {
                new WGFeatureGroupDef("zenith_worldgen", "Zenith",
                    "Combines all seed effects with extreme world generation",
                    category: "Misc",
                    passNames: new[] { "Shinies", "Corruption" }),
            }),

            // --- SKYBLOCK (70 flag checks, 20+ passes) ---
            new WGSeedDef("skyblock", "Skyblock", "skyblockWorld", "skyblockWorldGen", new[]
            {
                new WGFeatureGroupDef("skyblock_terrain", "Island Terrain",
                    "Generates world as floating islands with no underground — the core skyblock mechanic",
                    category: "Terrain", easyGroup: "skyblock_world",
                    passNames: new[] { "Terrain", "Skyblock" }),
                new WGFeatureGroupDef("skyblock_resources", "Limited Resources",
                    "Reduced ore availability, limited gem caves, restricted moss and vegetation",
                    category: "Ores & Resources", easyGroup: "skyblock_world",
                    passNames: new[] { "Shinies", "Gem Caves", "Gems", "Moss" }),
                new WGFeatureGroupDef("skyblock_restrictions", "Feature Restrictions",
                    "Disables evil biomes, lakes, traps, living trees, spider caves for island-only world",
                    category: "Misc", easyGroup: "skyblock_world",
                    passNames: new[] { "Corruption", "Lakes", "Traps", "Living Trees", "Spider Caves", "Shimmer", "Settle Liquids" }),
            }),

            // =================================================================
            // SECRET SEEDS (35) — one feature group each
            // Pass names are GenPassNameID string values from the decomp.
            // =================================================================

            // --- Visual seeds ---
            WGSeedDef.Secret("ss_paint_gray", "Paint Everything Gray", "paintEverythingGray",
                "Paints all tiles gray after world generation",
                category: "Visual",
                finalizeMethods: new[] { "DoPaintEverythingGray" }),
            WGSeedDef.Secret("ss_paint_negative", "Paint Everything Negative", "paintEverythingNegative",
                "Paints all tiles with negative paint after world generation",
                category: "Visual",
                finalizeMethods: new[] { "DoPaintEverythingNegative" }),
            WGSeedDef.Secret("ss_coat_echo", "Coat Everything Echo", "coatEverythingEcho",
                "Applies echo coating to all tiles after world generation",
                category: "Visual",
                finalizeMethods: new[] { "DoCoatEverythingEcho" }),
            WGSeedDef.Secret("ss_coat_illuminant", "Coat Everything Illuminant", "coatEverythingIlluminant",
                "Applies illuminant coating to all tiles after world generation",
                category: "Visual",
                finalizeMethods: new[] { "DoCoatEverythingIlluminant" }),
            WGSeedDef.Secret("ss_rainbow", "Rainbow Stuff", "rainbowStuff",
                "Adds rainbow-themed decorations throughout the world",
                category: "Visual",
                finalizeMethods: new[] { "DoRainbowStuff" }),

            // --- Terrain seeds ---
            WGSeedDef.Secret("ss_no_surface", "No Surface", "noSurface",
                "Removes the surface layer of the world",
                category: "Terrain",
                finalizeMethods: new[] { "DoNoSurface" }),
            WGSeedDef.Secret("ss_surface_space", "Surface in Space", "surfaceIsInSpace",
                "Raises the surface level to space height",
                category: "Terrain",
                passNames: new[] { "Terrain" },
                finalizeMethods: new[] { "DoSurfaceIsInSpace" }),
            WGSeedDef.Secret("ss_surface_desert", "Surface Is Desert", "surfaceIsDesert",
                "Converts the surface to desert biome",
                category: "Terrain",
                passNames: new[] { "Terrain", "Full Desert" },
                finalizeMethods: new[] { "DoSurfaceIsDesertFinish" }),
            WGSeedDef.Secret("ss_surface_mushrooms", "Surface Is Mushrooms", "surfaceIsMushrooms",
                "Converts the surface to glowing mushroom biome",
                category: "Terrain",
                finalizeMethods: new[] { "DoSurfaceIsMushrooms" }),
            WGSeedDef.Secret("ss_round_land", "Round Landmasses", "roundLandmasses",
                "Makes terrain features more rounded",
                category: "Terrain",
                passNames: new[] { "Terrain" }),
            WGSeedDef.Secret("ss_dig_holes", "Dig Extra Holes", "digExtraHoles",
                "Adds extra cave tunnels throughout the world",
                category: "Terrain",
                passNames: new[] { "Tunnels" }),

            // --- Biome seeds ---
            WGSeedDef.Secret("ss_world_infected", "World Is Infected", "worldIsInfected",
                "Covers the world in corruption/crimson",
                category: "Biomes",
                mainFlagField: "infectedSeed",
                finalizeMethods: new[] { "DoWorldIsInfected" }),
            WGSeedDef.Secret("ss_world_frozen", "World Is Frozen", "worldIsFrozen",
                "Converts the world to ice/snow biome",
                category: "Biomes",
                finalizeMethods: new[] { "DoWorldIsFrozen", "DoWorldIsFrozenFinish" }),
            WGSeedDef.Secret("ss_no_infection", "No Infection", "noInfection",
                "Removes all corruption/crimson from the generated world",
                category: "Biomes",
                finalizeMethods: new[] { "DoNoInfection" }),
            WGSeedDef.Secret("ss_hallow_surface", "Hallow on Surface", "hallowOnTheSurface",
                "Spreads Hallow across the world surface after generation",
                category: "Biomes",
                finalizeMethods: new[] { "DoHallowOnSurface" }),

            // --- Structure seeds ---
            WGSeedDef.Secret("ss_extra_trees", "Extra Living Trees", "extraLivingTrees",
                "Increases the number of living trees generated",
                category: "Trees & Plants",
                passNames: new[] { "Living Trees" }),
            WGSeedDef.Secret("ss_extra_islands", "Extra Floating Islands", "extraFloatingIslands",
                "Increases the number of floating islands generated",
                category: "Structures",
                passNames: new[] { "Floating Islands", "Floating Island Houses" }),
            WGSeedDef.Secret("ss_bigger_houses", "Bigger Abandoned Houses", "biggerAbandonedHouses",
                "Increases the size of underground houses",
                category: "Structures",
                passNames: new[] { "Buried Chests" }),
            WGSeedDef.Secret("ss_dual_dungeons", "Dual Dungeons", "dualDungeons",
                "Generates two dungeons (one on each side)",
                category: "Structures",
                mainFlagField: "dualDungeonsSeed",
                passNames: new[] { "Dungeon", "Dual Dungeons Dither Snake" }),
            WGSeedDef.Secret("ss_no_spider_caves", "No Spider Caves", "noSpiderCaves",
                "Removes spider cave biomes from generation",
                category: "Structures",
                passNames: new[] { "Spider Caves" }),
            WGSeedDef.Secret("ss_teleporters", "Add Teleporters", "addTeleporters",
                "Places teleporters in the world during generation",
                category: "Structures",
                passNames: new[] { "Final Cleanup" }),

            // --- Traps & Hazards seeds ---
            WGSeedDef.Secret("ss_no_traps", "Actually No Traps", "actuallyNoTraps",
                "Removes ALL traps (more thorough than No Traps seed)",
                category: "Traps & Hazards",
                finalizeMethods: new[] { "DoActuallyNoTraps" }),

            // --- Loot & Chests seeds ---
            WGSeedDef.Secret("ss_portal_gun", "Portal Gun in Chests", "portalGunInChests",
                "Places Portal Guns in dungeon chests",
                category: "Loot & Chests",
                finalizeMethods: new[] { "DoPortalGunInChests" }),

            // --- Liquids seeds ---
            WGSeedDef.Secret("ss_extra_liquid", "Extra Liquid", "extraLiquid",
                "Adds extra liquid pools throughout the world",
                category: "Liquids",
                passNames: new[] { "Settle Liquids" },
                finalizeMethods: new[] { "DoExtraLiquidFinish" }),

            // --- Events seeds ---
            WGSeedDef.Secret("ss_start_hardmode", "Start in Hardmode", "startInHardmode",
                "Triggers hardmode immediately after world generation",
                category: "Events",
                finalizeMethods: new[] { "DoStartInHardmode" }),
            WGSeedDef.Secret("ss_halloween_gen", "Halloween Gen", "halloweenGen",
                "Generates Halloween-themed world features",
                category: "Events",
                passNames: new[] { "Pots", "Planting Trees", "Weeds" }),
            WGSeedDef.Secret("ss_graveyard_blood", "Graveyard Bloodmoon", "graveyardBloodmoonStart",
                "Starts the world with a graveyard bloodmoon event",
                category: "Events"),
            WGSeedDef.Secret("ss_rains_year", "Rains for a Year", "rainsForAYear",
                "Makes it rain continuously for a full in-game year",
                category: "Events"),
            WGSeedDef.Secret("ss_endless_halloween", "Endless Halloween", "endlessHalloween",
                "Forces permanent Halloween event",
                category: "Events",
                mainFlagField: "forceHalloweenForever"),
            WGSeedDef.Secret("ss_endless_christmas", "Endless Christmas", "endlessChristmas",
                "Forces permanent Christmas event",
                category: "Events",
                mainFlagField: "forceXMasForever"),

            // --- Misc seeds ---
            WGSeedDef.Secret("ss_error_world", "Error World", "errorWorld",
                "Applies glitch/error visual effects after generation",
                category: "Visual",
                finalizeMethods: new[] { "DoErrorWorldFinish" }),
            WGSeedDef.Secret("ss_random_spawn", "Random Spawn", "randomSpawn",
                "Randomizes the player spawn point location",
                category: "Misc",
                finalizeMethods: new[] { "DoRandomSpawn" }),
            WGSeedDef.Secret("ss_poo_everywhere", "Poo Everywhere", "pooEverywhere",
                "Replaces various blocks with... poo",
                category: "Misc",
                passNames: new[] { "Tile Cleanup" }),
            WGSeedDef.Secret("ss_vampirism", "Vampirism", "vampirism",
                "Enables vampire-themed gameplay effects",
                category: "Misc",
                mainFlagField: "vampireSeed"),
            WGSeedDef.Secret("ss_team_spawns", "Team Based Spawns", "teamBasedSpawns",
                "Generates team-based spawn points",
                category: "Misc",
                mainFlagField: "teamBasedSpawnsSeed"),

            // ================================================================
            // CUSTOM SEEDLAB FEATURES — handled by FinalPassPatch after worldgen
            // ================================================================
            WGSeedDef.Custom("custom_underwater_surface", "Underwater Surface",
                "Floods the surface with ocean water and adds sand islands with palm trees. " +
                "A stone seal prevents water from draining underground.",
                category: "Terrain"),

            WGSeedDef.Custom("gog_enable", "Enable Gog",
                "The Gog may spawn where a Surface Mushroom Biome borders a Forest. " +
                "Gog tiles are painted deep blue and slowly spread. " +
                "Requires Mythril-tier pickaxe to mine.",
                category: "Biome"),

            WGSeedDef.Custom("gog_spawn", "Gog Spawn",
                "Forces the starting area to be infected by the Gog regardless of biome layout.",
                category: "Biome"),
        };

        /// <summary>
        /// Build lookup dictionaries for efficient access.
        /// </summary>
        public static void BuildLookups(
            out Dictionary<string, WGSeedDef> seedsById,
            out Dictionary<string, WGFeatureGroupDef> groupsById,
            out Dictionary<string, WGSeedDef> groupToSeed,
            out Dictionary<string, List<GroupSeedPair>> passToGroups)
        {
            seedsById = new Dictionary<string, WGSeedDef>();
            groupsById = new Dictionary<string, WGFeatureGroupDef>();
            groupToSeed = new Dictionary<string, WGSeedDef>();
            passToGroups = new Dictionary<string, List<GroupSeedPair>>();

            foreach (var seed in Seeds)
            {
                seedsById[seed.Id] = seed;
                foreach (var group in seed.Groups)
                {
                    groupsById[group.Id] = group;
                    groupToSeed[group.Id] = seed;

                    foreach (var passName in group.PassNames)
                    {
                        if (!passToGroups.TryGetValue(passName, out var list))
                        {
                            list = new List<GroupSeedPair>();
                            passToGroups[passName] = list;
                        }
                        list.Add(new GroupSeedPair { Group = group, Seed = seed });
                    }
                }
            }
        }
    }

    public struct GroupSeedPair
    {
        public WGFeatureGroupDef Group;
        public WGSeedDef Seed;
    }
}
