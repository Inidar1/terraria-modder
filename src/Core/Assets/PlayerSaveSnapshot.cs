using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.IO;
using Terraria;
using Terraria.IO;
using Terraria.GameContent;

namespace TerrariaModder.Core.Assets
{
    internal sealed class PlayerSaveSnapshot
    {
        private static readonly MethodInfo Copy = typeof(object).GetMethod("MemberwiseClone", BindingFlags.NonPublic | BindingFlags.Instance);
        internal readonly int CapturedThreadId = System.Threading.Thread.CurrentThread.ManagedThreadId;
        internal byte[] SerializedPlayer;
        internal PlayerFileData StagingFile;
        internal readonly List<TerrariaModder.Core.IO.PlayerSaveContributors.CapturedSidecar> Sidecars;
        internal readonly PlayerFileData OriginalFile, File;
        internal readonly PlayerSaveSnapshot Previous;
        internal readonly List<ModdataFile.ItemEntry> Items = new List<ModdataFile.ItemEntry>();
        internal readonly List<ModdataFile.ItemEntry> Preserved;
        internal readonly Item[] TemporarySlots, Refunds;
        private static T Clone<T>(T value) where T : class => value == null ? null : (T)Copy.Invoke(value, null);

        internal PlayerSaveSnapshot(PlayerFileData original, List<ModdataFile.ItemEntry> preserved, PlayerSaveSnapshot previous)
        {
            OriginalFile = original; Previous = previous;
            File = Clone(original);
            File.Metadata = Clone(original.Metadata);
            var player = Clone(original.Player);
            File.Player = player;
            player.inventory = Capture(player.inventory, "inventory");
            player.armor = Capture(player.armor, "armor"); player.dye = Capture(player.dye, "dye");
            player.miscEquips = Capture(player.miscEquips, "misc_equips"); player.miscDyes = Capture(player.miscDyes, "misc_dyes");
            CaptureBank(player, nameof(Player.bank));
            CaptureBank(player, nameof(Player.bank2));
            CaptureBank(player, nameof(Player.bank3));
            CaptureBank(player, nameof(Player.bank4));
            player.trashItem = CaptureItem(player.trashItem, "trash", 0);
            if (player.Loadouts != null)
            {
                player.Loadouts = player.Loadouts.Select(Clone).ToArray();
                for (int i = 0; i < player.Loadouts.Length; i++)
                {
                    var loadout = player.Loadouts[i]; if (loadout == null) continue;
                    loadout.Armor = Capture(loadout.Armor, $"loadout_{i}_armor");
                    loadout.Dye = Capture(loadout.Dye, $"loadout_{i}_dye");
                    loadout.Hide = (bool[])loadout.Hide.Clone();
                }
            }
            TemporarySlots = PlayerTemporaryItems.Locations.Select((location, slot) =>
                CaptureItem(PlayerTemporaryItems.Get(original.Player, slot), location, 0)).ToArray();
            Refunds = PlayerTemporaryItems.GetRefunds(original.Player)
                .Select((item, index) => CaptureItem(item, "pending", index + 1)).ToArray();
            Items.AddRange(PendingItemStore.GetPlayerModdataEntries(original.Player).Select(Clone));
            Preserved = (preserved ?? new List<ModdataFile.ItemEntry>()).Select(Clone).ToList();
            Sidecars = TerrariaModder.Core.IO.PlayerSaveContributors.Capture(original);
        }
        internal PlayerFileData CreateStagingFile(string path)
        {
            var file = Clone(File);
            typeof(FileData).GetField("_path", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(file, path);
            typeof(FileData).GetField("_isCloudSave", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(file, false);
            return StagingFile = file;
        }

        private void CaptureBank(Player detachedPlayer, string location)
        {
            // Native bank references are readonly. Set only the detached clone's
            // instance field; changing the shared Chest would mutate the live player.
            var field = typeof(Player).GetField(location, BindingFlags.Public | BindingFlags.Instance)
                ?? throw new MissingFieldException(typeof(Player).FullName, location);
            var bank = (Chest)field.GetValue(detachedPlayer);
            var result = Clone(bank);
            if (result != null) result.item = Capture(bank.item, location);
            field.SetValue(detachedPlayer, result);
        }
        private Item[] Capture(Item[] source, string location) => source?.Select((item, slot) => CaptureItem(item, location, slot)).ToArray();
        private Item CaptureItem(Item item, string location, int slot)
        {
            if (item == null) return new Item();
            if (item.IsAir || item.type < ItemRegistry.VanillaItemCount) return item.Clone();
            string id = ItemRegistry.GetFullId(item.type);
            if (id == null && ItemRegistry.IsKnownUnknown(item.type, out string known)) id = known;
            if (id == null) throw new IOException($"Cannot safely save unregistered custom item type {item.type} at {location}:{slot}");
            Items.Add(new ModdataFile.ItemEntry { Location = location, Slot = slot, ItemId = id,
                Stack = item.stack, Prefix = item.prefix, Favorited = item.favorited });
            return new Item();
        }
    }
}
