using System;
using StorageHub.Config;
using StorageHub.Relay;
using Terraria;

namespace StorageHub.Storage
{
    /// <summary>
    /// Computes a cheap fingerprint of the live storage state used by Storage Hub.
    /// This detects vanilla and external-mod mutations without constructing item snapshots
    /// or reevaluating the recipe catalog on a timer.
    /// </summary>
    internal sealed class StorageStateProbe
    {
        private const ulong OffsetBasis = 14695981039346656037UL;
        private const ulong Prime = 1099511628211UL;

        private readonly ChestRegistry _registry;
        private readonly RangeCalculator _range;
        private readonly StorageHubConfig _config;
        private readonly StorageHubModConfig _modConfig;

        public StorageStateProbe(ChestRegistry registry, RangeCalculator range,
            StorageHubConfig config, StorageHubModConfig modConfig)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _range = range ?? throw new ArgumentNullException(nameof(range));
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _modConfig = modConfig ?? throw new ArgumentNullException(nameof(modConfig));
        }

        public ulong Capture()
        {
            ulong hash = OffsetBasis;
            Add(ref hash, _config.Tier);
            Add(ref hash, _modConfig.BlockHotbarFromCrafting ? 1 : 0);

            var player = GetLocalPlayer();
            if (player == null)
            {
                Add(ref hash, -1);
            }
            else
            {
                AddItems(ref hash, player.inventory, 50);
                AddItems(ref hash, player.bank?.item);
                AddItems(ref hash, player.bank2?.item);
                AddItems(ref hash, player.bank3?.item);
                AddItems(ref hash, player.bank4?.item);
            }

            Add(ref hash, _registry.Count);
            var chests = Main.chest;
            foreach (var position in _registry.GetRegisteredPositions())
            {
                Add(ref hash, position.x);
                Add(ref hash, position.y);
                Add(ref hash, _range.IsInRange(position.x, position.y) ? 1 : 0);

                int chestIndex = FindChestAtPosition(chests, position.x, position.y);
                Add(ref hash, chestIndex);
                if (chestIndex < 0)
                    continue;

                var chest = chests[chestIndex];
                AddString(ref hash, chest?.name);
                AddItems(ref hash, chest?.item);
            }

            return hash;
        }

        private static Player GetLocalPlayer()
        {
            try
            {
                if (Main.player == null || Main.myPlayer < 0 || Main.myPlayer >= Main.player.Length)
                    return null;
                return Main.player[Main.myPlayer];
            }
            catch
            {
                return null;
            }
        }

        private static int FindChestAtPosition(Chest[] chests, int x, int y)
        {
            if (chests == null)
                return -1;

            for (int i = 0; i < chests.Length; i++)
            {
                var chest = chests[i];
                if (chest != null && chest.x == x && chest.y == y)
                    return i;
            }

            return -1;
        }

        private static void AddItems(ref ulong hash, Item[] items, int maximumCount = int.MaxValue)
        {
            if (items == null)
            {
                Add(ref hash, -1);
                return;
            }

            int count = Math.Min(items.Length, maximumCount);
            Add(ref hash, count);
            for (int i = 0; i < count; i++)
            {
                var item = items[i];
                Add(ref hash, i);
                Add(ref hash, item?.type ?? 0);
                Add(ref hash, item?.stack ?? 0);
                Add(ref hash, item?.prefix ?? 0);
            }
        }

        private static void AddString(ref ulong hash, string value)
        {
            if (value == null)
            {
                Add(ref hash, -1);
                return;
            }

            Add(ref hash, value.Length);
            for (int i = 0; i < value.Length; i++)
                Add(ref hash, value[i]);
        }

        private static void Add(ref ulong hash, int value)
        {
            unchecked
            {
                hash ^= (uint)value;
                hash *= Prime;
                hash ^= (uint)(value >> 16);
                hash *= Prime;
            }
        }
    }
}
