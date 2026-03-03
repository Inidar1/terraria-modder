using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Terraria;

namespace Randomizer.Modules
{
    /// <summary>
    /// Randomizes enemy spawn types by intercepting NPC.NewNPC for natural spawns.
    /// Excludes town NPCs, bosses, multi-part enemies (worm segments, boss limbs).
    /// </summary>
    public class SpawnModule : ModuleBase
    {
        public override string Id => "spawns";
        public override string Name => "Spawn Shuffle";
        public override string Description => "Enemies spawn as random types";
        public override string Tooltip => "Natural enemy spawns are shuffled. Bosses, town NPCs, worm segments, and multi-part enemies are excluded for safety. Can make areas much harder or easier.";
        public override bool IsDangerous => true;

        internal static SpawnModule Instance;

        // NPCID.Count = 697 in Terraria 1.4.5 (verified from decomp)
        private const int MaxNPCType = 697;

        // NPC types that must NOT be spawned standalone (verified against NPCID.cs)
        // Includes: worm segments, boss parts, boss heads, town NPCs, special entities
        private static readonly HashSet<int> ExcludedNPCs = new HashSet<int>
        {
            // === Bosses (should not be naturally spawned) ===
            4,   // EyeOfCthulhu
            13,  // EaterofWorldsHead
            35,  // SkeletronHead
            50,  // KingSlime
            113, // WallOfFlesh
            114, // WallOfFleshEye
            125, // Retinazer
            126, // Spazmatism
            127, // SkeletronPrime
            134, // TheDestroyer
            222, // QueenBee
            245, // Golem
            262, // Plantera
            266, // BrainofCthulhu
            370, // DukeFishron
            396, // MoonLordHead
            439, // CultistBoss
            636, // EmpressOfLight
            657, // QueenSlimeBoss
            668, // Deerclops

            // === Worm segments (bodies/tails need a head) ===
            7,  8,  9,    // Devourer head/body/tail
            10, 11, 12,   // GiantWorm head/body/tail
            14, 15,       // EaterofWorlds body/tail
            39, 40, 41,   // BoneSerpent head/body/tail
            87, 88, 89, 90, 91, 92, // Wyvern segments
            95, 96, 97,   // Digger head/body/tail
            98, 99, 100,  // Seeker head/body/tail
            117, 118, 119, // Leech head/body/tail
            135, 136,     // TheDestroyer body/tail
            402, 403, 404, // StardustWorm head/body/tail
            412, 413, 414, // SolarCrawltipede head/body/tail
            454, 455, 456, 457, 458, 459, // CultistDragon segments

            // === Boss parts (need parent entity) ===
            36,            // SkeletronHand
            128, 129, 130, 131, // Prime: Cannon/Saw/Vice/Laser
            139,           // Probe (Destroyer)
            246, 247, 248, 249, // Golem: Head/FistLeft/FistRight/HeadFree
            263, 264,      // Plantera: Hook/Tentacle
            267,           // Creeper (Brain of Cthulhu)
            397, 398,      // MoonLord: Hand/Core
            400, 401,      // MoonLord: FreeEye/LeechBlob
            440,           // CultistBossClone
            477, 478, 479, // Mothron/Egg/Spawn
            491, 492,      // PirateShip/Cannon
            558, 559, 560, // DD2 Wyverns

            // === Special entities ===
            68,  // DungeonGuardian
            392, // MartianSaucer
            399, // MartianProbe
            664, // TorchGod

            // === Town NPCs (verified from NPCID portraits) ===
            17,  // Merchant
            18,  // Nurse
            19,  // ArmsDealer
            20,  // Dryad
            22,  // Guide
            37,  // OldMan
            38,  // Demolitionist
            54,  // Clothier
            107, // GoblinTinkerer
            108, // Wizard
            124, // Mechanic
            142, // Santa
            160, // Truffle
            178, // Steampunker
            207, // DyeTrader
            208, // PartyGirl
            209, // Cyborg
            227, // Painter
            228, // WitchDoctor
            229, // Pirate
            353, // Stylist
            368, // TravellingMerchant
            369, // Angler
            441, // TaxCollector
            453, // SkeletonMerchant
            550, // Tavernkeep
            588, // Golfer
            633, // Zoologist
            637, // Cat
            638, // Dog
            656, // Bunny (town)
            663, // Princess
            670, // TownSlimeBlue
            678, // TownSlimeGreen
            679, // TownSlimeOld
            680, // TownSlimePurple
            681, // TownSlimeRainbow
            682, // TownSlimeRed
            683, // TownSlimeYellow
            684, // TownSlimeCopper

            // === Bound town NPCs (should not be randomly spawned) ===
            685, // BoundTownSlimeOld
            686, // BoundTownSlimePurple
            687, // BoundTownSlimeYellow
        };

        public override void BuildShuffleMap()
        {
            Instance = this;

            // Build pool of safe enemy NPC types (exclude bosses, town NPCs, multi-part)
            var pool = new List<int>();
            for (int i = 1; i < MaxNPCType; i++)
            {
                if (!ExcludedNPCs.Contains(i))
                    pool.Add(i);
            }
            ShuffleMap = Seed.BuildShuffleMap(pool, Id);
        }

        public override void ApplyPatches(Harmony harmony)
        {
            Instance = this;

            try
            {
                MethodInfo targetMethod = null;
                foreach (var m in typeof(NPC).GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    if (m.Name != "NewNPC") continue;
                    var parms = m.GetParameters();
                    if (parms.Length >= 4 &&
                        parms[1].ParameterType == typeof(int) && // X
                        parms[2].ParameterType == typeof(int) && // Y
                        parms[3].ParameterType == typeof(int))   // Type
                    {
                        targetMethod = m;
                        break;
                    }
                }

                if (targetMethod != null)
                {
                    var prefix = typeof(SpawnModule).GetMethod(nameof(NewNPC_Prefix),
                        BindingFlags.Public | BindingFlags.Static);
                    harmony.Patch(targetMethod, prefix: new HarmonyMethod(prefix));
                    Log.Info("[Randomizer] Spawns: patched NPC.NewNPC");
                }
                else
                {
                    Log.Warn("[Randomizer] Spawns: could not find NPC.NewNPC");
                }
            }
            catch (Exception ex)
            {
                Log.Error($"[Randomizer] Spawns patch error: {ex.Message}");
            }
        }

        /// <summary>
        /// Prefix on NPC.NewNPC — intercepts the Type parameter.
        /// Only shuffles natural spawns; skips bosses, town NPCs, multi-part, and event spawns.
        /// </summary>
        public static void NewNPC_Prefix(object source, ref int Type)
        {
            if (Instance == null || !Instance.Enabled) return;
            if (Type <= 0 || Type >= MaxNPCType) return;

            // Don't shuffle excluded types (bosses, town NPCs, multi-part entities)
            if (ExcludedNPCs.Contains(Type)) return;

            // Check spawn source to exclude boss/event/town spawns
            try
            {
                if (source != null)
                {
                    var sourceName = source.GetType().Name;
                    if (sourceName.Contains("Boss") ||
                        sourceName.Contains("Summon") ||
                        sourceName.Contains("Statue") ||
                        sourceName.Contains("Catch") ||
                        sourceName.Contains("OldOnesArmy"))
                    {
                        return;
                    }
                }
            }
            catch { }

            if (Instance.ShuffleMap.TryGetValue(Type, out int newType))
            {
                Type = newType;
            }
        }

        public override void RemovePatches(Harmony harmony)
        {
            // Handled by harmony.UnpatchAll
        }
    }
}
