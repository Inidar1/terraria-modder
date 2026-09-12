using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Terraria;
using Terraria.GameContent;

namespace TerrariaModder.Core.Assets
{
    // Native load retains these on the Player until Spawn applies them to globals.
    // Detached character-list/save objects must never borrow the active globals.
    internal static class PlayerTemporaryItems
    {
        internal static readonly string[] Locations = { "mouse", "creative", "guide", "reforge" };
        private static readonly FieldInfo Slots = Field("_temporaryItemSlots");
        private static readonly FieldInfo Refunds = Field("_pendingRefunds");
        private static readonly FieldInfo Crafts = typeof(CraftingRequests).GetField("_pendingCrafts", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingFieldException("CraftingRequests._pendingCrafts");
        private static FieldInfo Field(string name) => typeof(Player).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new MissingFieldException(typeof(Player).FullName, name);
        private static bool IsActive(Player player) => ReferenceEquals(player, Main.LocalPlayer);

        internal static Item Get(Player player, int slot)
        {
            if (!IsActive(player)) return ((Item[])Slots.GetValue(player))[slot];
            switch (slot)
            {
                case 0: return Main.mouseItem;
                case 1: return Main.CreativeMenu.GetItemByIndex(0);
                case 2: return Main.guideItem;
                case 3: return Main.reforgeItem;
                default: throw new ArgumentOutOfRangeException(nameof(slot));
            }
        }
        internal static void Set(Player player, int slot, Item item)
        {
            if (!IsActive(player)) { ((Item[])Slots.GetValue(player))[slot] = item; return; }
            switch (slot)
            {
                case 0: Main.mouseItem = item; break;
                case 1: Main.CreativeMenu.SetItembyIndex(item, 0); break;
                case 2: Main.guideItem = item; break;
                case 3: Main.reforgeItem = item; break;
                default: throw new ArgumentOutOfRangeException(nameof(slot));
            }
        }
        internal static IEnumerable<Item> GetRefunds(Player player)
        {
            if (!IsActive(player)) return (Item[])Refunds.GetValue(player);
            return ((IEnumerable<CraftingRequests.RemoteCraftRequest>)Crafts.GetValue(null)).SelectMany(request => request.consumed);
        }
    }
}
