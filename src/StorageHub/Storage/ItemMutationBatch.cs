using System;
using System.Collections.Generic;
using Terraria;

namespace StorageHub.Storage
{
    /// <summary>
    /// Stages item-array edits without touching original items. Commit is synchronous
    /// on the authoritative game thread; this is not a network reservation or lock.
    /// A failed operation invalidates the batch. It has no effects until Commit.
    /// </summary>
    internal sealed class ItemMutationBatch
    {
        private sealed class Slot
        {
            internal Item[] Items;
            internal int Index, Type, Stack, Prefix;
            internal Item Original, Next;
        }
        private readonly Dictionary<(Item[], int), Slot> _slots = new Dictionary<(Item[], int), Slot>();
        private bool _finished;

        private Slot Read(Item[] items, int index)
        {
            if (_finished || items == null || index < 0 || index >= items.Length) return null;
            var key = (items, index);
            if (_slots.TryGetValue(key, out var existing)) return existing;
            var original = items[index];
            var slot = new Slot { Items = items, Index = index, Original = original, Next = original,
                Type = original?.type ?? 0, Stack = original?.stack ?? 0, Prefix = original?.prefix ?? 0 };
            _slots.Add(key, slot);
            return slot;
        }

        private bool Abort() { _finished = true; return false; }

        internal bool Consume(Item[] items, int index, ItemSnapshot expected, int count)
        {
            var slot = Read(items, index);
            var item = slot?.Next;
            if (count <= 0 || item == null || item.type != expected.ItemId || item.prefix != expected.Prefix ||
                item.stack != expected.Stack || item.stack < count) return Abort();
            slot.Next = item.stack == count ? new Item() : item.Clone();
            if (item.stack > count) slot.Next.stack -= count;
            return true;
        }

        internal bool Place(Item[] items, int slotLimit, Item output, int count, int firstSlot = 0)
        {
            if (_finished || items == null || output == null || output.type <= 0 || output.maxStack <= 0 || count <= 0) return Abort();
            int remaining = count;
            int limit = Math.Min(items.Length, slotLimit);
            if (firstSlot < 0 || firstSlot >= limit) return Abort();
            for (int pass = 0; pass < 2 && remaining > 0; pass++)
            for (int i = firstSlot; i < limit && remaining > 0; i++)
            {
                var slot = Read(items, i);
                var item = slot.Next;
                bool empty = item == null || item.type <= 0 || item.stack <= 0;
                if (pass == 0 && !empty && Item.CanStack(item, output))
                {
                    int capacity = Math.Max(0, item.maxStack - item.stack);
                    int amount = Math.Min(remaining, capacity);
                    if (amount == 0) continue;
                    slot.Next = item.Clone();
                    slot.Next.stack += amount;
                    remaining -= amount;
                }
                else if (pass == 1 && empty)
                {
                    int amount = Math.Min(remaining, output.maxStack);
                    slot.Next = output.Clone();
                    slot.Next.stack = amount;
                    remaining -= amount;
                }
            }
            return remaining == 0 || Abort();
        }

        internal bool Commit()
        {
            if (_finished) return false;
            _finished = true;
            foreach (var slot in _slots.Values)
            {
                var current = slot.Items[slot.Index];
                if (!ReferenceEquals(current, slot.Original) || (current?.type ?? 0) != slot.Type ||
                    (current?.stack ?? 0) != slot.Stack || (current?.prefix ?? 0) != slot.Prefix) return false;
            }
            foreach (var slot in _slots.Values)
                if (!ReferenceEquals(slot.Original, slot.Next)) slot.Items[slot.Index] = slot.Next;
            return true;
        }
    }
}
