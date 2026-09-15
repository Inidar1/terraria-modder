using System;
using System.Collections.Generic;
using HarmonyLib;
using Terraria;
using Terraria.ID;

namespace Randomizer.Modules
{
    /// <summary>
    /// Gives the player random starting items when entering a world.
    /// Replaces copper tools with random items from a seed-deterministic pool.
    /// </summary>
    public class StartingItemsModule : ModuleBase, IUpdatable
    {
        public override string Id => "starting_items";
        public override string Name => "Starting Inventory";
        public override string Description => "Start with random items instead of copper tools";
        public override string Tooltip => "Replaces starter copper tools with random weapons, tools, accessories, and potions. Adds up to 3-7 items using empty hotbar slots; existing items are preserved.";
        public override bool IsWorldGen => true;

        internal static StartingItemsModule Instance;

        // Track each character/world pair across re-entry during this session.
        private readonly HashSet<string> _playersGivenItems = new HashSet<string>();
        private bool _pending;

        // All IDs verified against _REFERENCE_SOURCE_READ_ONLY/.../ID/ItemID.cs
        private static readonly int[] StartingPool = {
            // Swords (verified: IronBroadsword=4, IronShortsword=6, WoodenSword=24, LightsBane=46,
            //   Starfury=65, TheBreaker=104, FieryGreatsword=121, Muramasa=155, BladeofGrass=190,
            //   NightsEdge=273, DarkLance=274, Excalibur=368)
            4, 6, 24, 46, 65, 104, 121, 155, 190, 273, 274, 368,
            // Pickaxes (verified: IronPickaxe=1, NightmarePickaxe=103, MoltenPickaxe=122, MeteorHamaxe=204)
            1, 103, 122, 204,
            // Axes & Hammers (verified: IronHammer=7, IronAxe=10)
            7, 10,
            // Ranged (verified: WoodenBow=39, DemonBow=44, IronBow=99, Handgun=164, StarCannon=197, CobaltRepeater=435)
            39, 44, 99, 164, 197, 435,
            // Magic (verified: Vilethorn=64, MagicMissile=113, ShadowOrb=115, WaterBolt=165, Flamelash=218)
            64, 113, 115, 165, 218,
            // Flails, Spears, Boomerangs (verified: BallOHurt=162, BlueMoon=163, Sunfury=220, Spear=280, WoodenBoomerang=284)
            162, 163, 220, 280, 284,
            // Accessories (verified: BandofRegeneration=49, CloudinaBottle=53, HermesBoots=54, MiningHelmet=88,
            //   RocketBoots=128, LuckyHorseshoe=158, ShinyRedBalloon=159, Flipper=187, FeralClaws=211,
            //   AnkletoftheWind=212, DivingHelmet=268, Aglet=285, ObsidianShield=397, CloudinaBalloon=399)
            49, 53, 54, 88, 128, 158, 159, 187, 211, 212, 268, 285, 397, 399,
            // Potions (verified: LesserHealingPotion=28, LesserManaPotion=110, HealingPotion=188, ManaPotion=189,
            //   SwiftnessPotion=290, GillsPotion=291, IronskinPotion=292, ManaRegenerationPotion=293,
            //   MagicPowerPotion=294, FeatherfallPotion=295, SpelunkerPotion=296, InvisibilityPotion=297,
            //   ShinePotion=298, NightOwlPotion=299)
            28, 110, 188, 189, 290, 291, 292, 293, 294, 295, 296, 297, 298, 299,
            // Tools/Utility (verified: Torch=8, Shuriken=42, GrapplingHook=84, Chain=85,
            //   Bomb=166, StickyBomb=235, ThrowingKnife=279, Compass=393, MagicMirror=50)
            8, 42, 50, 84, 85, 166, 235, 279, 393,
        };

        public override void BuildShuffleMap()
        {
            Instance = this;

            // World loading finishes before the first playable game-thread update.
            _pending = true;
        }

        /// <summary>Cancel pending work without forgetting completed character/world pairs.</summary>
        internal void ResetForNewWorld()
        {
            _pending = false;
        }

        public void OnUpdate()
        {
            if (!_pending || Main.gameMenu || Main.netMode != 0) return;
            var player = Main.LocalPlayer;
            if (player == null || !player.active || player.inventory == null) return;
            _pending = false;
            string key = Main.ActiveWorldFileData.UniqueId + ":" + Main.ActivePlayerFileData.Path;
            if (!_playersGivenItems.Add(key)) return;
            ApplyStartingItems(player);
        }

        private void ApplyStartingItems(Player player)
        {
            try
            {
                var inventory = player.inventory;
                var slots = new List<int>();
                for (int i = 0; i < 10; i++)
                {
                    var item = inventory[i];
                    if (item != null && (item.type == ItemID.CopperShortsword ||
                        item.type == ItemID.CopperPickaxe || item.type == ItemID.CopperAxe)) slots.Add(i);
                }
                // An established character has no starter tools to replace.
                if (slots.Count == 0) return;

                // Use seed to pick random starting items
                var rng = new Random(Seed.DeriveSubSeed(Id));
                int numItems = 3 + rng.Next(5); // 3-7 random items
                for (int i = 0; i < 10 && slots.Count < numItems; i++)
                    if (inventory[i] != null && inventory[i].IsAir && !slots.Contains(i)) slots.Add(i);

                for (int i = 0; i < Math.Min(numItems, slots.Count); i++)
                {
                    var item = inventory[slots[i]];
                    if (item == null) continue;

                    int randomItemId = StartingPool[rng.Next(StartingPool.Length)];
                    item.SetDefaults(randomItemId);
                }

                Log.Info($"[Randomizer] Starting Items: gave {Math.Min(numItems, slots.Count)} random items");
            }
            catch (Exception ex)
            {
                Log.Error($"[Randomizer] Starting Items error: {ex.Message}");
            }
        }

        public override void ApplyPatches(Harmony harmony)
        {
            // No runtime patches needed — starting items are applied on world load
        }

        public override void RemovePatches(Harmony harmony)
        {
            _pending = false;
        }
    }
}
