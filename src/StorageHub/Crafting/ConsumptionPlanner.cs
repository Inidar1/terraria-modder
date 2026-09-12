using System;
using System.Collections.Generic;
using StorageHub.Storage;

namespace StorageHub.Crafting
{
    /// <summary>
    /// Allocates source quantities across all ingredients without mutating storage.
    /// Residual edges allow an earlier group choice to be reassigned when a later
    /// ingredient needs that item. A plan is not a reservation or server approval.
    /// </summary>
    public static class ConsumptionPlanner
    {
        public readonly struct Allocation
        {
            public readonly ItemSnapshot Source;
            public readonly int Count;
            public Allocation(ItemSnapshot source, int count) { Source = source; Count = count; }
        }

        /// <summary>
        /// Bind a recursive step to its chosen real item types, then validate that those
        /// quantities satisfy the original recipe. Fresh locations are selected here;
        /// this neither reserves items nor authorizes a multiplayer transaction.
        /// </summary>
        public static bool TryPlanExact(RecipeInfo recipe, int count, IReadOnlyList<ItemSnapshot> items,
            bool protectHotbar, IReadOnlyDictionary<int, long> selectedMaterials,
            out List<Allocation> plan, out string reason)
        {
            plan = new List<Allocation>();
            reason = "Invalid selected crafting materials";
            if (items == null || selectedMaterials == null) return false;
            var remaining = new Dictionary<int, long>();
            foreach (var choice in selectedMaterials)
            {
                if (choice.Key <= 0 || choice.Value <= 0) return false;
                remaining.Add(choice.Key, choice.Value);
            }
            var originals = new Dictionary<(int, int), ItemSnapshot>();
            var limited = new List<ItemSnapshot>();
            foreach (var item in items)
            {
                if (item.IsEmpty) continue;
                var location = (item.SourceChestIndex, item.SourceSlot);
                if (item.SourceSlot < 0 || originals.ContainsKey(location))
                { reason = "Invalid or duplicate storage location"; return false; }
                originals.Add(location, item);
                if (protectHotbar && item.IsFromInventory && item.SourceSlot < 10) continue;
                if (!remaining.TryGetValue(item.ItemId, out long needed) || needed == 0) continue;
                int take = (int)Math.Min(needed, item.Stack);
                remaining[item.ItemId] = needed - take;
                limited.Add(new ItemSnapshot(item.ItemId, take, item.Prefix, item.Name,
                    item.MaxStack, item.Rarity, item.SourceChestIndex, item.SourceSlot));
            }
            foreach (long amount in remaining.Values)
                if (amount != 0)
                { reason = "Selected crafting materials are no longer available"; return false; }
            if (!TryPlan(recipe, count, limited, protectHotbar, out var allocated, out reason)) return false;
            var consumed = new Dictionary<int, long>();
            foreach (var entry in allocated)
            {
                consumed.TryGetValue(entry.Source.ItemId, out long amount);
                consumed[entry.Source.ItemId] = amount + entry.Count;
            }
            foreach (var choice in selectedMaterials)
                if (!consumed.TryGetValue(choice.Key, out long amount) || amount != choice.Value)
                { reason = "Selected quantities do not match the recipe"; return false; }
            // Restore full source snapshots: mutation validation must compare the actual
            // stack, not the capped capacity used by the allocation solver.
            foreach (var entry in allocated)
                plan.Add(new Allocation(originals[(entry.Source.SourceChestIndex, entry.Source.SourceSlot)], entry.Count));
            return true;
        }

        private sealed class Edge
        {
            public int To, Reverse, Remaining;
            public Edge(int to, int reverse, int remaining)
            { To = to; Reverse = reverse; Remaining = remaining; }
        }

        public static bool TryPlan(RecipeInfo recipe, int count, IReadOnlyList<ItemSnapshot> items,
            bool protectHotbar, out List<Allocation> plan, out string reason)
        {
            return TryPlan(recipe, count, items, protectHotbar, out plan, out reason, out _);
        }

        /// <summary>
        /// Also reports unfilled requirements from the same distinct-material allocation.
        /// A rejected request never returns an executable partial plan. Missing quantities
        /// describe one maximal allocation; null means the request or sources were invalid.
        /// </summary>
        public static bool TryPlan(RecipeInfo recipe, int count, IReadOnlyList<ItemSnapshot> items,
            bool protectHotbar, out List<Allocation> plan, out string reason, out int[] missing)
        {
            plan = new List<Allocation>();
            reason = null;
            missing = null;
            if (recipe == null || items == null || count <= 0)
            { reason = "Invalid crafting request"; return false; }
            var needs = new int[recipe.Ingredients.Count];
            long required = 0;
            for (int i = 0; i < needs.Length; i++)
            {
                var ingredient = recipe.Ingredients[i];
                long amount = ingredient == null ? 0 : (long)ingredient.RequiredStack * count;
                if (amount <= 0 || amount > int.MaxValue ||
                    (ingredient.IsRecipeGroup && (ingredient.ValidItemIds == null || ingredient.ValidItemIds.Count == 0)))
                { reason = "Invalid ingredient quantity or group"; return false; }
                needs[i] = (int)amount;
                required += amount;
            }

            var sources = new List<ItemSnapshot>();
            var locations = new HashSet<(int, int)>();
            foreach (var item in items)
            {
                if (item.IsEmpty) continue;
                if (item.SourceSlot < 0 || !locations.Add((item.SourceChestIndex, item.SourceSlot)))
                { reason = "Invalid or duplicate storage location"; return false; }
                if (protectHotbar && item.IsFromInventory && item.SourceSlot < 10) continue;
                sources.Add(item);
            }

            int sink = 1 + sources.Count + needs.Length;
            var graph = new List<Edge>[sink + 1];
            for (int i = 0; i < graph.Length; i++) graph[i] = new List<Edge>();
            Action<int, int, int> connect = (from, to, capacity) =>
            {
                var forward = new Edge(to, graph[to].Count, capacity);
                var reverse = new Edge(from, graph[from].Count, 0);
                graph[from].Add(forward);
                graph[to].Add(reverse);
            };
            var supply = new Edge[sources.Count];
            for (int i = 0; i < sources.Count; i++)
            {
                connect(0, 1 + i, sources[i].Stack);
                supply[i] = graph[0][graph[0].Count - 1];
                for (int j = 0; j < needs.Length; j++)
                {
                    var ingredient = recipe.Ingredients[j];
                    bool matches = ingredient.IsRecipeGroup
                        ? ingredient.ValidItemIds.Contains(sources[i].ItemId)
                        : ingredient.ItemId == sources[i].ItemId;
                    if (matches) connect(1 + i, 1 + sources.Count + j, sources[i].Stack);
                }
            }
            var demand = new Edge[needs.Length];
            for (int j = 0; j < needs.Length; j++)
            {
                int node = 1 + sources.Count + j;
                connect(node, sink, needs[j]);
                demand[j] = graph[node][graph[node].Count - 1];
            }

            long allocated = 0;
            var previous = new int[graph.Length];
            var via = new int[graph.Length];
            while (allocated < required)
            {
                for (int i = 0; i < previous.Length; i++) previous[i] = -1;
                previous[0] = 0;
                var queue = new Queue<int>();
                queue.Enqueue(0);
                while (queue.Count > 0 && previous[sink] < 0)
                {
                    int node = queue.Dequeue();
                    for (int i = 0; i < graph[node].Count; i++)
                    {
                        var edge = graph[node][i];
                        if (edge.Remaining <= 0 || previous[edge.To] >= 0) continue;
                        previous[edge.To] = node;
                        via[edge.To] = i;
                        queue.Enqueue(edge.To);
                    }
                }
                if (previous[sink] < 0) break;
                int transfer = int.MaxValue;
                for (int node = sink; node != 0; node = previous[node])
                    transfer = Math.Min(transfer, graph[previous[node]][via[node]].Remaining);
                for (int node = sink; node != 0; node = previous[node])
                {
                    var edge = graph[previous[node]][via[node]];
                    edge.Remaining -= transfer;
                    graph[node][edge.Reverse].Remaining += transfer;
                }
                allocated += transfer;
            }
            missing = new int[needs.Length];
            for (int i = 0; i < missing.Length; i++) missing[i] = demand[i].Remaining;
            if (allocated < required)
            { reason = "Not enough distinct materials for all ingredients"; return false; }
            for (int i = 0; i < sources.Count; i++)
            {
                int consumed = sources[i].Stack - supply[i].Remaining;
                if (consumed > 0) plan.Add(new Allocation(sources[i], consumed));
            }
            return true;
        }
    }
}
