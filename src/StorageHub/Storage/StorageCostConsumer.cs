using System;
using System.Collections.Generic;
using System.Linq;
using Terraria;
using TerrariaModder.Core.Logging;

namespace StorageHub.Storage
{
    /// <summary>Consumes a progression cost from the same accessible sources shown by its UI.</summary>
    internal sealed class StorageCostConsumer
    {
        private readonly ILogger _log;
        private readonly IStorageProvider _storage;
        private readonly Func<List<ItemSnapshot>> _readAccessible;

        internal StorageCostConsumer(ILogger log, IStorageProvider storage, Func<List<ItemSnapshot>> readAccessible)
        { _log = log; _storage = storage; _readAccessible = readAccessible; }

        public bool ConsumeItems(int[] acceptedItemIds, int requiredCount)
        {
            if (requiredCount <= 0 || acceptedItemIds == null || acceptedItemIds.Length == 0 || _storage == null)
                return false;
            try
            {
                var accepted = new HashSet<int>(acceptedItemIds);
                // Storage first, preserving inventory where possible. Personal banks are
                // included because they are included in the displayed available count.
                var sources = _readAccessible().Where(item => !item.IsEmpty && accepted.Contains(item.ItemId))
                    .OrderBy(item => item.IsFromInventory ? 1 : 0).ToList();
                var locations = new HashSet<(int, int)>();
                long available = 0;
                foreach (var item in sources)
                {
                    if (item.SourceSlot < 0 || !locations.Add((item.SourceChestIndex, item.SourceSlot)))
                    { _log.Warn("Invalid or duplicate upgrade source"); return false; }
                    available += item.Stack;
                }
                if (available < requiredCount)
                { _log.Warn($"Not enough accessible items: have {available}, need {requiredCount}"); return false; }

                int remaining = requiredCount;
                if (Main.netMode == 0 && _storage.GetType() == typeof(SingleplayerProvider))
                {
                    var batch = new ItemMutationBatch();
                    foreach (var item in sources)
                    {
                        if (remaining == 0) break;
                        int count = Math.Min(item.Stack, remaining);
                        if (!batch.Consume(SingleplayerProvider.ResolveItems(Main.LocalPlayer, item.SourceChestIndex),
                            item.SourceSlot, item, count))
                        { _log.Warn("Upgrade source changed before commit"); return false; }
                        remaining -= count;
                    }
                    return remaining == 0 && batch.Commit();
                }

                // Existing provider transport remains synchronous. Its replacement with
                // correlated server-approved transactions is required for MP acceptance.
                foreach (var item in sources)
                {
                    if (remaining == 0) break;
                    int count = Math.Min(item.Stack, remaining);
                    if (_storage.TakeItem(item.SourceChestIndex, item.SourceSlot, count, out var taken))
                        remaining -= taken.Stack;
                    else
                        _log.Warn($"Failed to consume upgrade source {item.SourceChestIndex}:{item.SourceSlot}");
                }
                return remaining == 0;
            }
            catch (Exception ex)
            { _log.Error($"ConsumeItems failed: {ex.Message}"); return false; }
        }
    }
}
