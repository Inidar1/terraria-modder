using System.Collections.Generic;

namespace SeedLab
{
    /// <summary>
    /// Defines the seed flags, feature groups, and individual features available for toggling.
    /// </summary>
    public static class SeedFeatures
    {
        // Seed flag field names on Main (9 special seeds)
        public const string GetGoodWorld = "getGoodWorld";
        public const string DrunkWorld = "drunkWorld";
        public const string DontStarveWorld = "dontStarveWorld";
        public const string NotTheBeesWorld = "notTheBeesWorld";
        public const string RemixWorld = "remixWorld";
        public const string ZenithWorld = "zenithWorld";
        public const string TenthAnniversaryWorld = "tenthAnniversaryWorld";
        public const string NoTrapsWorld = "noTrapsWorld";
        public const string SkyblockWorld = "skyblockWorld";

        // Seed flag field names on Main (6 secret seeds with runtime effects)
        public const string VampireSeed = "vampireSeed";
        public const string InfectedSeed = "infectedSeed";
        public const string TeamBasedSpawnsSeed = "teamBasedSpawnsSeed";
        public const string DualDungeonsSeed = "dualDungeonsSeed";
        public const string ForceHalloweenForever = "forceHalloweenForever";
        public const string ForceXMasForever = "forceXMasForever";

        // Patch target identifiers (method we patch)
        public const string Target_NPC_SetDefaults = "NPC.SetDefaults";
        public const string Target_NPC_AI = "NPC.AI";
        public const string Target_NPC_ScaleStatsTweaks = "NPC.ScaleStats_ByDifficulty_Tweaks";
        public const string Target_Spawner_GetSpawnRate = "Spawner.GetSpawnRate";
        public const string Target_Spawner_SpawnNPC = "Spawner.SpawnNPC";
        public const string Target_Player_UpdateBuffs = "Player.UpdateBuffs";
        public const string Target_Player_PickTile = "Player.PickTile";
        public const string Target_WorldGen_ShakeTree = "WorldGen.ShakeTree";
        public const string Target_Projectile_SetDefaults = "Projectile.SetDefaults";
        public const string Target_Projectile_AI = "Projectile.AI";
        public const string Target_Projectile_FishingCheck = "Projectile.FishingCheck";
        public const string Target_Global = "Global"; // No per-method patch; global toggle only

        /// <summary>
        /// All seed definitions, ordered for display.
        /// </summary>
        public static readonly SeedDefinition[] Seeds = new[]
        {
            new SeedDefinition("ftw", "For the Worthy", GetGoodWorld, new[]
            {
                new FeatureGroupDefinition("ftw_enemy_scaling", "Enemy Scaling", "Enemies get increased HP, damage, defense, and size", new[]
                {
                    new FeatureDefinition("ftw_enemy_stats", "Enemy Stat Scaling", "NPC.getGoodAdjustments: size, HP, defense, damage boosts per NPC type", Target_NPC_SetDefaults, GetGoodWorld, true),
                    new FeatureDefinition("ftw_boss_stats", "Boss Stat Tweaks", "ScaleStats_ByDifficulty_Tweaks: boss life/damage multipliers", Target_NPC_ScaleStatsTweaks, GetGoodWorld, true),
                }),
                new FeatureGroupDefinition("ftw_boss_ai", "Boss AI", "Bosses have modified attack patterns and increased difficulty", new[]
                {
                    new FeatureDefinition("ftw_boss_behavior", "Boss AI Changes", "EoC reflects projectiles, Skeletron spins faster, WoF speed increase, etc.", Target_NPC_AI, GetGoodWorld, true),
                }),
                new FeatureGroupDefinition("ftw_spawning", "Spawn Rates", "Enemies spawn faster and in greater numbers", new[]
                {
                    new FeatureDefinition("ftw_spawn_rate", "Spawn Rate x0.8, Max x1.2", "GetSpawnRate: spawnRate * 0.8, maxSpawns * 1.2", Target_Spawner_GetSpawnRate, GetGoodWorld, true),
                    new FeatureDefinition("ftw_spawn_npc", "Spawn NPC Changes", "Spider spawn logic and other NPC spawn tweaks", Target_Spawner_SpawnNPC, GetGoodWorld, true),
                }),
                new FeatureGroupDefinition("ftw_tree_bombs", "Tree Bombs", "Trees drop bomb projectiles when shaken", new[]
                {
                    new FeatureDefinition("ftw_tree_bomb_drop", "Tree Bomb Drops", "WorldGen.ShakeTree: 1/17 chance to drop bomb (type 28) when shaking trees", Target_WorldGen_ShakeTree, GetGoodWorld, true),
                }),
                new FeatureGroupDefinition("ftw_difficulty", "Difficulty Scaling", "Main.Difficulty adds +1 to world difficulty (Classic->Expert, Expert->Master, Master->Legendary)", new[]
                {
                    new FeatureDefinition("ftw_difficulty_plus1", "Difficulty +1", "All stat scaling game-wide shifts up one tier via Main.Difficulty getter", Target_Global, GetGoodWorld, true),
                }),
                new FeatureGroupDefinition("ftw_mining", "Mining Changes", "Double pickaxe damage against all tiles", new[]
                {
                    new FeatureDefinition("ftw_pickaxe_damage", "Double Pickaxe Damage", "Player.PickTile_DetermineDamage: damage *= 2 against all tiles", Target_Player_PickTile, GetGoodWorld, true),
                }),
                new FeatureGroupDefinition("ftw_projectiles", "Projectile Changes", "Star projectiles become hostile, boss star reflection, grenade bounce changes", new[]
                {
                    new FeatureDefinition("ftw_projectile_defaults", "Hostile Stars & Grenades", "Projectile.SetDefaults: falling stars become hostile+friendly, grenade properties changed", Target_Projectile_SetDefaults, GetGoodWorld, true),
                    new FeatureDefinition("ftw_projectile_ai", "Boss Star Reflection & Bounce", "Projectile.AI: ReflectStarShotsInForTheWorthy bosses reflect stars, grenade bounce behavior", Target_Projectile_AI, GetGoodWorld, true),
                }),
                new FeatureGroupDefinition("ftw_cross_seed", "Cross-Seed Effects", "Special behaviors requiring FTW + other seeds active simultaneously", new[]
                {
                    new FeatureDefinition("ftw_bosses_keep_spawning", "Bosses Keep Spawning", "SpecialSeedFeatures.BossesKeepSpawning: requires Don't Starve ON + Celebration OFF", Target_Global, GetGoodWorld, true),
                    new FeatureDefinition("ftw_mechdusa", "Mechdusa", "SpecialSeedFeatures.Mechdusa: Mechdusa boss variant, requires Remix ON", Target_Global, GetGoodWorld, true),
                }),
            }),

            new SeedDefinition("ds", "Don't Starve", DontStarveWorld, new[]
            {
                new FeatureGroupDefinition("ds_hunger", "Hunger System", "Hunger debuff applied when no fed-state buff is active", new[]
                {
                    new FeatureDefinition("ds_starving", "Hunger Debuff", "UpdateBuffs: applies hunger debuff when no food buff active", Target_Player_UpdateBuffs, DontStarveWorld, true),
                }),
                new FeatureGroupDefinition("ds_spawning", "Spawn Depth", "Expanded NPC spawn depth range", new[]
                {
                    new FeatureDefinition("ds_spawn_depth", "Expanded Spawn Depth", "Spawner.SpawnNPC: allows NPC spawns deeper in cavern layer", Target_Spawner_SpawnNPC, DontStarveWorld, true),
                }),
                new FeatureGroupDefinition("ds_darkness", "Darkness Damage", "250 damage per hit after 120 frames in complete darkness (core DS mechanic)", new[]
                {
                    new FeatureDefinition("ds_darkness_damage", "Darkness Damage", "DontStarveDarknessDamageDealer.Update: 250 damage per hit in prolonged darkness", Target_Global, DontStarveWorld, true),
                }),
                new FeatureGroupDefinition("ds_night_lighting", "Night Lighting", "Pitch black nights except during full moon; blood moon adds some brightness", new[]
                {
                    new FeatureDefinition("ds_night_darkness", "Dark Nights", "DontStarveSeed: ModifyNightColor + ModifyMinimumLightColorAtNight + FixBiomeDarkness", Target_Global, DontStarveWorld, true),
                }),
                new FeatureGroupDefinition("ds_death_sounds", "Death Sounds", "Don't Starve Together style death sounds", new[]
                {
                    new FeatureDefinition("ds_death_sound", "DST Death Sounds", "Player.PlayDeathSound: plays DST male/female hurt sounds on death", Target_Global, DontStarveWorld, true),
                }),
            }),

            new SeedDefinition("ntb", "Not the Bees", NotTheBeesWorld, new[]
            {
                new FeatureGroupDefinition("ntb_ai", "NPC Behavior", "Modified NPC AI with bee-related changes", new[]
                {
                    new FeatureDefinition("ntb_npc_ai", "NPC AI Changes", "NPC.AI: spider cocoon/egg-sac spawning, slime behavior in bee-themed worlds", Target_NPC_AI, NotTheBeesWorld, true),
                }),
            }),

            new SeedDefinition("drunk", "Drunk World", DrunkWorld, new[]
            {
                new FeatureGroupDefinition("drunk_spawning", "Spawn Changes", "Modified spawn rates near certain structures", new[]
                {
                    new FeatureDefinition("drunk_spawn_rate", "Dungeon Brick Spawn Rates", "Standing on brick wall type 86: 0.3x rate, 1.8x max", Target_Spawner_GetSpawnRate, DrunkWorld, true),
                    new FeatureDefinition("drunk_spawn_npc", "Dungeon Spawn Logic", "Modified dungeon NPC spawn selection", Target_Spawner_SpawnNPC, DrunkWorld, true),
                }),
            }),

            new SeedDefinition("remix", "Don't Dig Up", RemixWorld, new[]
            {
                new FeatureGroupDefinition("remix_spawning", "Flipped Spawning", "NPC spawn zones are depth-inverted", new[]
                {
                    new FeatureDefinition("remix_spawn_rate", "Flipped Spawn Rates", "Spawn rate adjustments for flipped depth zones", Target_Spawner_GetSpawnRate, RemixWorld, true),
                    new FeatureDefinition("remix_spawn_npc", "Flipped NPC Selection", "Which NPCs spawn at which depths is inverted", Target_Spawner_SpawnNPC, RemixWorld, true),
                }),
                new FeatureGroupDefinition("remix_fishing", "Fishing Changes", "Surface corruption/crimson fishing disabled, remix ocean fishing at depth", new[]
                {
                    new FeatureDefinition("remix_fishing_loot", "Remix Fishing Loot", "Projectile.FishingCheck: disables corruption/crimson at surface, adds deep ocean remix fish", Target_Projectile_FishingCheck, RemixWorld, true),
                }),
            }),

            new SeedDefinition("zenith", "Zenith", ZenithWorld, new[]
            {
                new FeatureGroupDefinition("zenith_scaling", "Star Difficulty", "Comprehensive NPC stat scaling for maximum challenge", new[]
                {
                    new FeatureDefinition("zenith_npc_scaling", "Zenith NPC Scaling", "getZenithSeedAdjustmentsBeforeEverything: reduces life to 0.8x for specific NPCs (slimes 125-131, Duke Fishron 370)", Target_NPC_SetDefaults, ZenithWorld, true),
                }),
            }),

            new SeedDefinition("celebration", "Celebration", TenthAnniversaryWorld, new[]
            {
                new FeatureGroupDefinition("celebration_npcs", "NPC Stats", "Modified NPC stat adjustments for celebration", new[]
                {
                    new FeatureDefinition("celebration_npc_adjust", "Celebration NPC Adjustments", "getTenthAnniversaryAdjustments: NPC stat tweaks (only when FTW is off)", Target_NPC_SetDefaults, TenthAnniversaryWorld, true),
                }),
                new FeatureGroupDefinition("celebration_spawning", "Spawn Changes", "Fairy spawning, jungle mimics, and special creature frequency", new[]
                {
                    new FeatureDefinition("celebration_spawn_npc", "Celebration Spawn Effects", "Fairy spawning, jungle mimics, modified creature frequency", Target_Spawner_SpawnNPC, TenthAnniversaryWorld, true),
                }),
                new FeatureGroupDefinition("celebration_tree_bombs", "Anniversary Bombs", "Trees drop Anniversary Bomb (type 75) instead of normal bomb, doubled chance", new[]
                {
                    new FeatureDefinition("celebration_tree_bomb_type", "Anniversary Tree Bombs", "WorldGen.ShakeTree: bomb type changed to 75, chance doubled (1/34 instead of 1/17)", Target_WorldGen_ShakeTree, TenthAnniversaryWorld, true),
                }),
                new FeatureGroupDefinition("celebration_dev_armor", "Dev Armor Rate", "Dev armor drop rate doubled (1/8 instead of 1/16)", new[]
                {
                    new FeatureDefinition("celebration_dev_rate", "Dev Armor 1/8", "Player.TryGettingDevArmor: drop chance 1/8 instead of 1/16", Target_Global, TenthAnniversaryWorld, true),
                }),
            }),

            new SeedDefinition("notraps", "No Traps", NoTrapsWorld, new[]
            {
                new FeatureGroupDefinition("notraps_npc", "NPC Projectiles", "Increased trap projectile spawn frequency from slimes and other NPCs", new[]
                {
                    new FeatureDefinition("notraps_npc_ai", "NPC Projectile Frequency", "NPC.AI: slime/trap projectile spawn chance drastically increased", Target_NPC_AI, NoTrapsWorld, true),
                }),
            }),

            new SeedDefinition("skyblock", "Skyblock", SkyblockWorld, new[]
            {
                new FeatureGroupDefinition("skyblock_mode", "Skyblock Mode", "World generated as floating islands — worldgen only, no runtime effects", new[]
                {
                    new FeatureDefinition("skyblock_flag", "Skyblock Flag", "Main.skyblockWorld flag (no runtime gameplay effects, worldgen only)", Target_Global, SkyblockWorld, false),
                }),
            }),

            // --- Secret Seeds (1.4.5) ---
            // These use WorldGen.SecretSeed.* system but persist as Main.* boolean flags.
            // Global toggle flips the flag at runtime; per-method patching not yet implemented.

            new SeedDefinition("vampire", "Vampire", VampireSeed, new[]
            {
                new FeatureGroupDefinition("vampire_effects", "Vampire Effects", "Vampire music, buff duration protection, NPC spawning, background style", new[]
                {
                    new FeatureDefinition("vampire_runtime", "Vampire Runtime Effects", "Music override, buff 23/24/32 protection, Princess NPC spawn, bg style 8", Target_Global, VampireSeed, true),
                }),
            }),

            new SeedDefinition("infected", "Infected", InfectedSeed, new[]
            {
                new FeatureGroupDefinition("infected_effects", "Infection Effects", "Flask speed boost, larger projectile hitboxes, mining bypass, spawn changes", new[]
                {
                    new FeatureDefinition("infected_runtime", "Infection Runtime Effects", "Flask projectile speed x2.2, hitbox x1.66, ore mining bypass, corruption spawn disable", Target_Global, InfectedSeed, true),
                }),
            }),

            new SeedDefinition("team_spawns", "Team Spawns", TeamBasedSpawnsSeed, new[]
            {
                new FeatureGroupDefinition("team_spawn_effects", "Team Spawn Effects", "Team-based spawn points and reduced dungeon safe zones", new[]
                {
                    new FeatureDefinition("team_spawn_runtime", "Team Spawn Runtime Effects", "Routes player to team spawn point, reduces NPC safe zone in dungeons", Target_Global, TeamBasedSpawnsSeed, true),
                }),
            }),

            new SeedDefinition("dual_dungeons", "Dual Dungeons", DualDungeonsSeed, new[]
            {
                new FeatureGroupDefinition("dual_dungeon_effects", "Dual Dungeon Effects", "NPC context and dungeon spawn rule changes", new[]
                {
                    new FeatureDefinition("dual_dungeon_runtime", "Dual Dungeon Runtime Effects", "Dual dungeon NPC spawn rules, brick type changes, spawn exclusion zones", Target_Global, DualDungeonsSeed, true),
                }),
            }),

            new SeedDefinition("endless_halloween", "Endless Halloween", ForceHalloweenForever, new[]
            {
                new FeatureGroupDefinition("endless_halloween_effects", "Endless Halloween", "Forces permanent Halloween event regardless of real-world date", new[]
                {
                    new FeatureDefinition("endless_halloween_runtime", "Endless Halloween Flag", "Main.forceHalloweenForever — forces Halloween event permanently", Target_Global, ForceHalloweenForever, true),
                }),
            }),

            new SeedDefinition("endless_christmas", "Endless Christmas", ForceXMasForever, new[]
            {
                new FeatureGroupDefinition("endless_christmas_effects", "Endless Christmas", "Forces permanent Christmas event regardless of real-world date", new[]
                {
                    new FeatureDefinition("endless_christmas_runtime", "Endless Christmas Flag", "Main.forceXMasForever — forces Christmas event permanently", Target_Global, ForceXMasForever, true),
                }),
            }),
        };

        /// <summary>
        /// Build lookup dictionaries from the static catalog.
        /// </summary>
        public static void BuildLookups(
            out Dictionary<string, SeedDefinition> seedsById,
            out Dictionary<string, FeatureGroupDefinition> groupsById,
            out Dictionary<string, FeatureDefinition> featuresById,
            out Dictionary<string, List<FeatureDefinition>> featuresByTarget)
        {
            seedsById = new Dictionary<string, SeedDefinition>();
            groupsById = new Dictionary<string, FeatureGroupDefinition>();
            featuresById = new Dictionary<string, FeatureDefinition>();
            featuresByTarget = new Dictionary<string, List<FeatureDefinition>>();

            foreach (var seed in Seeds)
            {
                seedsById[seed.Id] = seed;
                foreach (var group in seed.Groups)
                {
                    groupsById[group.Id] = group;
                    foreach (var feature in group.Features)
                    {
                        featuresById[feature.Id] = feature;

                        if (!featuresByTarget.TryGetValue(feature.PatchTarget, out var list))
                        {
                            list = new List<FeatureDefinition>();
                            featuresByTarget[feature.PatchTarget] = list;
                        }
                        list.Add(feature);
                    }
                }
            }
        }
    }

    public class SeedDefinition
    {
        public string Id;
        public string DisplayName;
        public string FlagField;       // Main.* field name
        public FeatureGroupDefinition[] Groups;

        public SeedDefinition(string id, string displayName, string flagField, FeatureGroupDefinition[] groups)
        {
            Id = id;
            DisplayName = displayName;
            FlagField = flagField;
            Groups = groups;
        }
    }

    public class FeatureGroupDefinition
    {
        public string Id;
        public string DisplayName;
        public string Description;
        public FeatureDefinition[] Features;

        public FeatureGroupDefinition(string id, string displayName, string description, FeatureDefinition[] features)
        {
            Id = id;
            DisplayName = displayName;
            Description = description;
            Features = features;
        }
    }

    public class FeatureDefinition
    {
        public string Id;
        public string DisplayName;
        public string Description;
        public string PatchTarget;      // Which method this is controlled by
        public string SeedFlag;         // Which Main.* field this overrides
        public bool RuntimeToggleable;  // Can be toggled while in-game

        public FeatureDefinition(string id, string displayName, string description, string patchTarget, string seedFlag, bool runtimeToggleable)
        {
            Id = id;
            DisplayName = displayName;
            Description = description;
            PatchTarget = patchTarget;
            SeedFlag = seedFlag;
            RuntimeToggleable = runtimeToggleable;
        }
    }
}
