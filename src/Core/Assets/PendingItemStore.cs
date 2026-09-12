using System;
using System.Collections.Generic;
using System.Linq;
using Terraria;

namespace TerrariaModder.Core.Assets
{
    /// <summary>
    /// Holds custom items that couldn't be placed on load (all storage full).
    /// Items persist here until the player withdraws or deletes them via the UI.
    /// On save, pending items are written back to moddata so they survive save/quit.
    /// </summary>
    public static class PendingItemStore
    {
        public class PendingItem
        {
            public string ItemId { get; set; }     // "modid:itemname"
            public int RuntimeType { get; set; }
            public int Stack { get; set; } = 1;
            public int Prefix { get; set; }
            public bool Favorited { get; set; }
        }

        private static List<PendingItem> CurrentPlayerItems => PlayerItemSaveState.For(Main.LocalPlayer).Pending;
        private static readonly List<PendingItem> _worldItems = new List<PendingItem>();

        /// <summary>Current pending player items (inventory overflow).</summary>
        public static IReadOnlyList<PendingItem> PlayerItems => CurrentPlayerItems;

        /// <summary>Current pending world items (chest overflow).</summary>
        public static IReadOnlyList<PendingItem> WorldItems => _worldItems;

        /// <summary>Total pending items across player + world.</summary>
        public static int TotalCount => CurrentPlayerItems.Count + _worldItems.Count;

        public static void AddPlayerItem(PendingItem item)
        {
            AddPlayerItem(Main.LocalPlayer, item);
        }

        public static void AddWorldItem(PendingItem item)
        {
            if (item != null) _worldItems.Add(item);
        }

        public static void RemovePlayerItem(PendingItem item)
        {
            CurrentPlayerItems.Remove(item);
        }

        public static void RemoveWorldItem(PendingItem item)
        {
            _worldItems.Remove(item);
        }

        public static void ClearPlayer() => ClearPlayer(Main.LocalPlayer);
        public static void ClearWorld() => _worldItems.Clear();
        public static void ClearAll()
        {
            ClearPlayer();
            _worldItems.Clear();
        }

        /// <summary>
        /// Convert pending player items to moddata entries for persistence.
        /// Uses location "pending" so they're recognized on next load.
        /// </summary>
        public static IReadOnlyList<PendingItem> GetPlayerItems(Player player) => PlayerItemSaveState.For(player).Pending;
        public static void AddPlayerItem(Player player, PendingItem item)
        {
            if (item != null) PlayerItemSaveState.For(player).Pending.Add(item);
        }
        /// <summary>Validate and append a complete recovery batch on the game thread.</summary>
        public static void AddPlayerItems(Player player, IEnumerable<PendingItem> items)
        {
            var batch = items.ToArray();
            if (batch.Any(item => item == null || string.IsNullOrEmpty(item.ItemId) || item.RuntimeType <= 0 || item.Stack <= 0))
                throw new ArgumentException("Recovery batch contains an invalid item", nameof(items));
            // Array-backed AddRange reserves capacity before publishing any entries.
            PlayerItemSaveState.For(player).Pending.AddRange(batch);
        }
        public static void ClearPlayer(Player player) => PlayerItemSaveState.For(player).Pending.Clear();
        public static List<ModdataFile.ItemEntry> GetPlayerModdataEntries() => GetPlayerModdataEntries(Main.LocalPlayer);
        public static List<ModdataFile.ItemEntry> GetPlayerModdataEntries(Player player)
        {
            var items = PlayerItemSaveState.For(player).Pending;
            var entries = new List<ModdataFile.ItemEntry>();
            for (int i = 0; i < items.Count; i++)
            {
                var p = items[i];
                entries.Add(new ModdataFile.ItemEntry
                {
                    Location = "pending",
                    Slot = i,
                    ItemId = p.ItemId,
                    Stack = p.Stack,
                    Prefix = p.Prefix,
                    Favorited = p.Favorited
                });
            }
            return entries;
        }

        /// <summary>
        /// Convert pending world items to moddata entries for persistence.
        /// Uses location "pending_world" so they're recognized on next load.
        /// </summary>
        public static List<ModdataFile.ItemEntry> GetWorldModdataEntries()
        {
            var entries = new List<ModdataFile.ItemEntry>();
            for (int i = 0; i < _worldItems.Count; i++)
            {
                var w = _worldItems[i];
                entries.Add(new ModdataFile.ItemEntry
                {
                    Location = "pending_world",
                    Slot = i,
                    ItemId = w.ItemId,
                    Stack = w.Stack,
                    Prefix = w.Prefix,
                    Favorited = w.Favorited
                });
            }
            return entries;
        }
    }
}
