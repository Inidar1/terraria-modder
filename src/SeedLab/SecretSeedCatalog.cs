namespace SeedLab
{
    public enum SeedKind
    {
        SpecialSeed,
        SecretSeed,
        Custom          // SeedLab-original features, not tied to any vanilla seed
    }

    public class WorldGenSeedEntry
    {
        public string Id;
        public string DisplayName;
        public SeedKind Kind;
        public string Category;
        public string FieldName;       // Main.* field (special) or SecretSeed field name (secret)
        public string MainFlagField;   // For secret seeds that also set Main.* flags, else null

        public WorldGenSeedEntry(string id, string displayName, SeedKind kind, string category, string fieldName, string mainFlagField = null)
        {
            Id = id;
            DisplayName = displayName;
            Kind = kind;
            Category = category;
            FieldName = fieldName;
            MainFlagField = mainFlagField;
        }
    }

    /// <summary>
    /// Flat catalog of all 44 Terraria 1.4.5 seeds for the world-gen override panel.
    /// 9 special seeds (Main.* boolean flags) + 35 secret seeds (WorldGen.SecretSeed.* instances).
    /// </summary>
    public static class SecretSeedCatalog
    {
        public const string CategorySpecial = "Special Seeds";
        public const string CategorySecret = "Secret Seeds";

        public static readonly WorldGenSeedEntry[] AllSeeds = new[]
        {
            // --- 9 Special Seeds (Main.* flags + WorldGen.* aliases) ---
            new WorldGenSeedEntry("ftw",         "For the Worthy",  SeedKind.SpecialSeed, CategorySpecial, "getGoodWorld"),
            new WorldGenSeedEntry("drunk",       "Drunk World",     SeedKind.SpecialSeed, CategorySpecial, "drunkWorld"),
            new WorldGenSeedEntry("ds",          "Don't Starve",    SeedKind.SpecialSeed, CategorySpecial, "dontStarveWorld"),
            new WorldGenSeedEntry("ntb",         "Not the Bees",    SeedKind.SpecialSeed, CategorySpecial, "notTheBeesWorld"),
            new WorldGenSeedEntry("remix",       "Don't Dig Up",    SeedKind.SpecialSeed, CategorySpecial, "remixWorld"),
            new WorldGenSeedEntry("zenith",      "Zenith",          SeedKind.SpecialSeed, CategorySpecial, "zenithWorld"),
            new WorldGenSeedEntry("celebration", "Celebration",     SeedKind.SpecialSeed, CategorySpecial, "tenthAnniversaryWorld"),
            new WorldGenSeedEntry("notraps",     "No Traps",        SeedKind.SpecialSeed, CategorySpecial, "noTrapsWorld"),
            new WorldGenSeedEntry("skyblock",    "Skyblock",        SeedKind.SpecialSeed, CategorySpecial, "skyblockWorld"),

            // --- 35 Secret Seeds (WorldGen.SecretSeed.* instances) ---
            new WorldGenSeedEntry("ss_paint_gray",       "Paint Everything Gray",     SeedKind.SecretSeed, CategorySecret, "paintEverythingGray"),
            new WorldGenSeedEntry("ss_paint_negative",   "Paint Everything Negative", SeedKind.SecretSeed, CategorySecret, "paintEverythingNegative"),
            new WorldGenSeedEntry("ss_coat_echo",        "Coat Everything Echo",      SeedKind.SecretSeed, CategorySecret, "coatEverythingEcho"),
            new WorldGenSeedEntry("ss_coat_illuminant",  "Coat Everything Illuminant",SeedKind.SecretSeed, CategorySecret, "coatEverythingIlluminant"),
            new WorldGenSeedEntry("ss_no_surface",       "No Surface",               SeedKind.SecretSeed, CategorySecret, "noSurface"),
            new WorldGenSeedEntry("ss_extra_trees",      "Extra Living Trees",        SeedKind.SecretSeed, CategorySecret, "extraLivingTrees"),
            new WorldGenSeedEntry("ss_extra_islands",    "Extra Floating Islands",    SeedKind.SecretSeed, CategorySecret, "extraFloatingIslands"),
            new WorldGenSeedEntry("ss_error_world",      "Error World",              SeedKind.SecretSeed, CategorySecret, "errorWorld"),
            new WorldGenSeedEntry("ss_graveyard_blood",  "Graveyard Bloodmoon",      SeedKind.SecretSeed, CategorySecret, "graveyardBloodmoonStart"),
            new WorldGenSeedEntry("ss_surface_space",    "Surface in Space",         SeedKind.SecretSeed, CategorySecret, "surfaceIsInSpace"),
            new WorldGenSeedEntry("ss_rains_year",       "Rains for a Year",         SeedKind.SecretSeed, CategorySecret, "rainsForAYear"),
            new WorldGenSeedEntry("ss_bigger_houses",    "Bigger Abandoned Houses",   SeedKind.SecretSeed, CategorySecret, "biggerAbandonedHouses"),
            new WorldGenSeedEntry("ss_random_spawn",     "Random Spawn",             SeedKind.SecretSeed, CategorySecret, "randomSpawn"),
            new WorldGenSeedEntry("ss_teleporters",      "Add Teleporters",          SeedKind.SecretSeed, CategorySecret, "addTeleporters"),
            new WorldGenSeedEntry("ss_start_hardmode",   "Start in Hardmode",        SeedKind.SecretSeed, CategorySecret, "startInHardmode"),
            new WorldGenSeedEntry("ss_no_infection",     "No Infection",             SeedKind.SecretSeed, CategorySecret, "noInfection"),
            new WorldGenSeedEntry("ss_hallow_surface",   "Hallow on Surface",        SeedKind.SecretSeed, CategorySecret, "hallowOnTheSurface"),
            new WorldGenSeedEntry("ss_world_infected",   "World Is Infected",        SeedKind.SecretSeed, CategorySecret, "worldIsInfected",       "infectedSeed"),
            new WorldGenSeedEntry("ss_surface_mushrooms","Surface Is Mushrooms",     SeedKind.SecretSeed, CategorySecret, "surfaceIsMushrooms"),
            new WorldGenSeedEntry("ss_surface_desert",   "Surface Is Desert",        SeedKind.SecretSeed, CategorySecret, "surfaceIsDesert"),
            new WorldGenSeedEntry("ss_poo_everywhere",   "Poo Everywhere",           SeedKind.SecretSeed, CategorySecret, "pooEverywhere"),
            new WorldGenSeedEntry("ss_no_spider_caves",  "No Spider Caves",          SeedKind.SecretSeed, CategorySecret, "noSpiderCaves"),
            new WorldGenSeedEntry("ss_no_traps",         "Actually No Traps",        SeedKind.SecretSeed, CategorySecret, "actuallyNoTraps"),
            new WorldGenSeedEntry("ss_rainbow",          "Rainbow Stuff",            SeedKind.SecretSeed, CategorySecret, "rainbowStuff"),
            new WorldGenSeedEntry("ss_dig_holes",        "Dig Extra Holes",          SeedKind.SecretSeed, CategorySecret, "digExtraHoles"),
            new WorldGenSeedEntry("ss_round_land",       "Round Landmasses",         SeedKind.SecretSeed, CategorySecret, "roundLandmasses"),
            new WorldGenSeedEntry("ss_extra_liquid",     "Extra Liquid",             SeedKind.SecretSeed, CategorySecret, "extraLiquid"),
            new WorldGenSeedEntry("ss_portal_gun",       "Portal Gun in Chests",     SeedKind.SecretSeed, CategorySecret, "portalGunInChests"),
            new WorldGenSeedEntry("ss_world_frozen",     "World Is Frozen",          SeedKind.SecretSeed, CategorySecret, "worldIsFrozen"),
            new WorldGenSeedEntry("ss_halloween_gen",    "Halloween Gen",            SeedKind.SecretSeed, CategorySecret, "halloweenGen"),
            new WorldGenSeedEntry("ss_endless_halloween","Endless Halloween",        SeedKind.SecretSeed, CategorySecret, "endlessHalloween",      "forceHalloweenForever"),
            new WorldGenSeedEntry("ss_endless_christmas","Endless Christmas",        SeedKind.SecretSeed, CategorySecret, "endlessChristmas",      "forceXMasForever"),
            new WorldGenSeedEntry("ss_vampirism",        "Vampirism",               SeedKind.SecretSeed, CategorySecret, "vampirism",             "vampireSeed"),
            new WorldGenSeedEntry("ss_team_spawns",      "Team Based Spawns",        SeedKind.SecretSeed, CategorySecret, "teamBasedSpawns",       "teamBasedSpawnsSeed"),
            new WorldGenSeedEntry("ss_dual_dungeons",    "Dual Dungeons",            SeedKind.SecretSeed, CategorySecret, "dualDungeons",          "dualDungeonsSeed"),
        };
    }
}
