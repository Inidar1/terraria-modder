using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Terraria;

namespace TerrariaModder.Core.Assets
{
    // Loaded character objects retain their own sidecar-only data even while the
    // native character list loads other files. Lifetime follows the Player.
    internal sealed class PlayerItemSaveState
    {
        private static readonly ConditionalWeakTable<Player, PlayerItemSaveState> States = new ConditionalWeakTable<Player, PlayerItemSaveState>();
        internal List<ModdataFile.ItemEntry> Preserved = new List<ModdataFile.ItemEntry>();
        internal readonly List<PendingItemStore.PendingItem> Pending = new List<PendingItemStore.PendingItem>();
        internal static PlayerItemSaveState For(Player player)
        {
            if (player == null) throw new ArgumentNullException(nameof(player));
            return States.GetValue(player, _ => new PlayerItemSaveState());
        }
    }
}
