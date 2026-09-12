using System;
using System.Reflection;
using HarmonyLib;
using Terraria;
using Terraria.GameContent.Achievements;

namespace StorageHub.Storage
{
    internal static class NativePickupNotifications
    {
        private const string Owner = "storage-hub.native-pickup-notifications";
        private static Harmony _harmony;
        internal static void EnsurePatched()
        {
            if (_harmony != null) return;
            var harmony = new Harmony(Owner);
            try
            {
                var owner = typeof(AchievementsHelper);
                var self = typeof(NativePickupNotifications);
                harmony.Patch(owner.GetMethod("NotifyItemPickup", new[] { typeof(Player), typeof(Item) }),
                    prefix: new HarmonyMethod(self.GetMethod(nameof(BeforePickup), BindingFlags.Static | BindingFlags.NonPublic)));
                harmony.Patch(owner.GetMethod("NotifyItemPickup", new[] { typeof(Player), typeof(Item), typeof(int) }),
                    prefix: new HarmonyMethod(self.GetMethod(nameof(BeforePartialPickup), BindingFlags.Static | BindingFlags.NonPublic)));
                _harmony = harmony;
            }
            catch { harmony.UnpatchAll(Owner); throw; }
        }
        private static bool BeforePickup(Player player, Item item)
            => !NativeItemTransaction.QueuePickup(player, item, item.stack);
        private static bool BeforePartialPickup(Player player, Item item, int customStack)
            => !NativeItemTransaction.QueuePickup(player, item, customStack);
        internal static void Unload()
        {
            _harmony?.UnpatchAll(Owner);
            _harmony = null;
        }
    }
}
