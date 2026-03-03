using System;
using System.Reflection;
using HarmonyLib;
using Terraria.GameContent.ItemDropRules;

namespace Randomizer.Modules
{
    /// <summary>
    /// Randomizes enemy and boss drops by intercepting CommonCode.DropItemFromNPC.
    /// Each kill produces a random item — no two drops are guaranteed the same.
    /// </summary>
    public class EnemyDropsModule : ModuleBase
    {
        public override string Id => "enemy_drops";
        public override string Name => "Enemy Drop Shuffle";
        public override string Description => "Enemies and bosses drop random items";
        public override string Tooltip => "Each enemy drop is randomly replaced with any item in the game. Every kill is a surprise — no fixed mapping.";
        private const int MaxItemId = 6144; // ItemID.Count=6145, last valid=6144 (verified from decomp)

        internal static EnemyDropsModule Instance;

        public override void BuildShuffleMap()
        {
            Instance = this;
            InitPoolRng();
        }

        public override void ApplyPatches(Harmony harmony)
        {
            Instance = this;

            try
            {
                var commonCodeType = typeof(CommonCode);

                // Patch DropItemFromNPC(NPC npc, int itemId, int stack, bool scattered = false)
                MethodInfo dropItemFromNPC = null;
                foreach (var method in commonCodeType.GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    if (method.Name == "DropItemFromNPC")
                    {
                        dropItemFromNPC = method;
                        break;
                    }
                }

                if (dropItemFromNPC != null)
                {
                    var prefix = typeof(EnemyDropsModule).GetMethod(nameof(DropItemFromNPC_Prefix),
                        BindingFlags.Public | BindingFlags.Static);
                    harmony.Patch(dropItemFromNPC, prefix: new HarmonyMethod(prefix));
                    Log.Info("[Randomizer] Enemy Drops: patched CommonCode.DropItemFromNPC");
                }
                else
                {
                    Log.Warn("[Randomizer] Enemy Drops: could not find DropItemFromNPC");
                }

                // Also patch DropItemLocalPerClientAndSetNPCMoneyTo0 for multiplayer-style drops
                MethodInfo dropLocal = null;
                foreach (var method in commonCodeType.GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    if (method.Name == "DropItemLocalPerClientAndSetNPCMoneyTo0")
                    {
                        dropLocal = method;
                        break;
                    }
                }

                if (dropLocal != null)
                {
                    var prefix = typeof(EnemyDropsModule).GetMethod(nameof(DropItemLocal_Prefix),
                        BindingFlags.Public | BindingFlags.Static);
                    harmony.Patch(dropLocal, prefix: new HarmonyMethod(prefix));
                    Log.Info("[Randomizer] Enemy Drops: patched CommonCode.DropItemLocalPerClient");
                }
            }
            catch (Exception ex)
            {
                Log.Error($"[Randomizer] Enemy Drops patch error: {ex.Message}");
            }
        }

        /// <summary>
        /// Prefix on CommonCode.DropItemFromNPC — replaces with random item each drop.
        /// </summary>
        public static void DropItemFromNPC_Prefix(ref int itemId)
        {
            if (Instance == null || !Instance.Enabled) return;
            if (itemId <= 0) return;
            itemId = Instance.GetRandomInRange(1, MaxItemId + 1);
        }

        /// <summary>
        /// Prefix on CommonCode.DropItemLocalPerClientAndSetNPCMoneyTo0 — replaces with random item.
        /// </summary>
        public static void DropItemLocal_Prefix(ref int itemId)
        {
            if (Instance == null || !Instance.Enabled) return;
            if (itemId <= 0) return;
            itemId = Instance.GetRandomInRange(1, MaxItemId + 1);
        }

        public override void RemovePatches(Harmony harmony)
        {
            // Handled by harmony.UnpatchAll in Mod.Unload
        }
    }
}
