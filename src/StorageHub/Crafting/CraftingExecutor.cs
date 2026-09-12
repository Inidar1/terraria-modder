using System;
using System.Collections.Generic;
using Terraria;
using TerrariaModder.Core.Logging;
using StorageHub.Storage;

namespace StorageHub.Crafting
{
    /// <summary>
    /// Executes crafting operations by consuming materials and creating items.
    ///
    /// Design principles:
    /// - Consumes materials from ALL registered storage via IStorageProvider
    /// - Consumes materials first, then creates items
    /// - Returns created item to player's inventory or mouse cursor
    /// </summary>
    public class CraftingExecutor
    {
        private readonly ILogger _log;
        private readonly IStorageProvider _storage;
        private readonly CraftingMaterialSource _materials;
        private NativeItemTransaction _transaction;
        private bool IsSingleplayer => Main.netMode == 0 && _storage.GetType() == typeof(SingleplayerProvider);

        /// <summary>
        /// When true, items in hotbar slots 0-9 of the player inventory are never
        /// consumed as crafting materials.
        /// </summary>
        public bool ProtectHotbar { get; set; }

        public CraftingExecutor(ILogger log, IStorageProvider storage)
            : this(log, storage, new CraftingMaterialSource(storage)) { }

        public CraftingExecutor(ILogger log, IStorageProvider storage, CraftingMaterialSource materials)
        {
            _log = log;
            _storage = storage;
            _materials = materials ?? throw new ArgumentNullException(nameof(materials));
        }

        /// <summary>
        /// Execute a recipe craft, consuming materials from ALL storage and creating items.
        ///
        /// Plans distinct source quantities before consumption, then creates output.
        /// Execution and recovery are still synchronous; this is not an atomic
        /// transaction or proof of multiplayer authority.
        /// </summary>
        /// <param name="recipe">Recipe to craft.</param>
        /// <param name="count">Number of times to craft the recipe.</param>
        /// <param name="directToInventory">If true, place output directly in player inventory
        /// instead of using QuickSpawnItem. Used for intermediate recursive craft steps
        /// so the output is immediately available to the next step.</param>
        /// <returns>True if crafting succeeded.</returns>
        public bool ExecuteCraft(RecipeInfo recipe, int count = 1, bool directToInventory = false)
            => ExecuteTransaction(() => ExecuteCraftCore(recipe, count, directToInventory, null));

        /// <summary>Execute only the validated material choices made by a recursive plan.</summary>
        public bool ExecuteCraft(RecipeInfo recipe, int count, IReadOnlyDictionary<int, long> selectedMaterials,
            bool directToInventory = false)
            => selectedMaterials != null && ExecuteTransaction(() => ExecuteCraftCore(recipe, count, directToInventory, selectedMaterials));

        internal bool ExecuteTransaction(Func<bool> action)
        {
            if (!IsSingleplayer || _transaction != null) return action();
            try
            {
                using (var transaction = new NativeItemTransaction(Main.LocalPlayer, _log))
                {
                    _transaction = transaction;
                    try
                    {
                        bool success = action();
                        if (success)
                        {
                            // Notification callbacks may start a fresh craft after commit.
                            _transaction = null;
                            transaction.Commit();
                        }
                        return success;
                    }
                    finally { _transaction = null; }
                }
            }
            catch (Exception ex)
            {
                _log.Error($"Native crafting transaction failed: {ex}");
                return false;
            }
        }

        private bool ExecuteCraftCore(RecipeInfo recipe, int count, bool directToInventory,
            IReadOnlyDictionary<int, long> selectedMaterials)
        {
            if (recipe == null || count <= 0) return false;

            try
            {
                var player = Main.player[Main.myPlayer];
                if (player == null)
                {
                    _log.Error("Cannot craft: player not found");
                    return false;
                }

                // Validate output before taking any ingredients.
                long totalOutputLong = (long)recipe.OutputStack * count;
                if (recipe.OutputItemId <= 0 || totalOutputLong <= 0 || totalOutputLong > int.MaxValue)
                {
                    _log.Warn($"Invalid output quantity for {recipe.OutputName}");
                    return false;
                }
                int totalOutput = (int)totalOutputLong;
                var outputTemplate = new Item();
                outputTemplate.SetDefaults(recipe.OutputItemId);
                if (outputTemplate.type != recipe.OutputItemId || outputTemplate.maxStack <= 0)
                {
                    _log.Warn("Crafting output item could not be initialized");
                    return false;
                }

                var sources = _materials.Read();
                List<ConsumptionPlanner.Allocation> allocation;
                string reason;
                bool planned = selectedMaterials == null
                    ? ConsumptionPlanner.TryPlan(recipe, count, sources, ProtectHotbar, out allocation, out reason)
                    : ConsumptionPlanner.TryPlanExact(recipe, count, sources, ProtectHotbar, selectedMaterials, out allocation, out reason);
                if (!planned)
                {
                    _log.Warn(reason);
                    return false;
                }
                // Intermediate SP crafts must not consume materials or leave partial output
                // when the inventory cannot hold the complete result.
                if (IsSingleplayer)
                {
                    var output = outputTemplate;
                    var batch = new ItemMutationBatch();
                    foreach (var entry in allocation)
                        _transaction.Capture(SingleplayerProvider.ResolveItems(player, entry.Source.SourceChestIndex));
                    foreach (var entry in allocation)
                        if (!batch.Consume(SingleplayerProvider.ResolveItems(player, entry.Source.SourceChestIndex),
                            entry.Source.SourceSlot, entry.Source, entry.Count))
                        {
                            _log.Warn("Crafting materials changed before commit");
                            return false;
                        }
                    // Intermediate output must remain available to the next craft without
                    // making pre-existing protected hotbar items eligible as ingredients.
                    int firstOutputSlot = ProtectHotbar || _materials.ProtectHotbar ? 10 : 0;
                    if (directToInventory && !batch.Place(player.inventory, 50, output, totalOutput, firstOutputSlot))
                    {
                        _log.Warn("Inventory cannot hold the complete crafting output");
                        return false;
                    }
                    if (!batch.Commit())
                    {
                        _log.Warn("Crafting storage changed before commit");
                        return false;
                    }
                    if (!directToInventory && !GiveItemToPlayer(player, outputTemplate, totalOutput)) return false;
                    _log.Info($"Crafted {totalOutput}x {recipe.OutputName}");
                    return true;
                }

                var consumptionPlan = new List<ConsumptionEntry>();
                foreach (var entry in allocation)
                    consumptionPlan.Add(new ConsumptionEntry
                    {
                        SourceChestIndex = entry.Source.SourceChestIndex,
                        SourceSlot = entry.Source.SourceSlot,
                        ItemId = entry.Source.ItemId,
                        Amount = entry.Count,
                        ItemName = entry.Source.Name
                    });

                // PHASE 2: Execute all consumptions - all or nothing
                var consumed = new List<ConsumptionEntry>();
                bool allSucceeded = true;

                foreach (var entry in consumptionPlan)
                {
                    if (_storage.TakeItem(entry.SourceChestIndex, entry.SourceSlot, entry.Amount, out var taken))
                    {
                        var consumedEntry = entry;
                        consumedEntry.ActualTaken = taken.Stack;
                        consumed.Add(consumedEntry);
                    }
                    else
                    {
                        _log.Error($"Failed to take {entry.Amount}x {entry.ItemName} from {SourceIndex.GetSourceName(entry.SourceChestIndex)} slot {entry.SourceSlot}");
                        allSucceeded = false;
                        break;
                    }
                }

                // If any consumption failed, attempt to restore consumed items
                if (!allSucceeded && consumed.Count > 0)
                {
                    _log.Error($"Partial consumption failure - attempting to restore {consumed.Count} consumed items...");
                    int restored = 0;
                    int partialRestores = 0;
                    foreach (var entry in consumed)
                    {
                        var recovery = new ItemSnapshot(
                            entry.ItemId, entry.ActualTaken, 0,
                            entry.ItemName, 999, 0,
                            SourceIndex.PlayerInventory, 0);
                        int deposited = _storage.DepositItem(recovery, out _);
                        if (deposited >= entry.ActualTaken)
                            restored++;
                        else if (deposited > 0)
                            partialRestores++;
                    }
                    if (restored == consumed.Count)
                    {
                        _log.Info("All consumed items restored successfully");
                    }
                    else
                    {
                        _log.Error($"CRITICAL: Only fully restored {restored}/{consumed.Count} items ({partialRestores} partial) - some may be lost!");
                        try { Terraria.Main.NewText("[StorageHub] Crafting failed! Some materials may be lost. Check logs.", 255, 80, 80); } catch { }
                    }
                    return false;
                }
                else if (!allSucceeded)
                {
                    return false;
                }

                // PHASE 3: Create the validated output quantity.
                // Intermediate steps require inventory output. Ordinary output uses
                // native pickup priorities and drops only inventory/void-vault overflow.
                bool created = directToInventory
                    ? PlaceInInventoryDirect(player, recipe.OutputItemId, totalOutput)
                    : GiveItemToPlayer(player, outputTemplate, totalOutput);
                if (!created)
                {
                    // CRITICAL: Output failed - restore ALL consumed items
                    _log.Error($"Failed to give crafted item {recipe.OutputName} - restoring consumed materials...");
                    int restored = 0;
                    int partialRestores2 = 0;
                    foreach (var entry in consumed)
                    {
                        var recovery = new ItemSnapshot(
                            entry.ItemId, entry.ActualTaken, 0,
                            entry.ItemName, 999, 0,
                            SourceIndex.PlayerInventory, 0);
                        int deposited = _storage.DepositItem(recovery, out _);
                        if (deposited >= entry.ActualTaken)
                            restored++;
                        else if (deposited > 0)
                            partialRestores2++;
                    }
                    if (restored == consumed.Count)
                    {
                        _log.Info("All materials restored - craft aborted safely");
                    }
                    else
                    {
                        _log.Error($"CRITICAL: Only fully restored {restored}/{consumed.Count} material entries ({partialRestores2} partial) - some may be lost!");
                        try { Terraria.Main.NewText("[StorageHub] Crafting failed! Some materials may be lost. Check logs.", 255, 80, 80); } catch { }
                    }
                    return false;
                }

                _log.Info($"Crafted {totalOutput}x {recipe.OutputName}");
                return true;
            }
            catch (Exception ex)
            {
                _log.Error($"ExecuteCraft failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Entry in the consumption plan for two-phase commit.
        /// </summary>
        private struct ConsumptionEntry
        {
            public int SourceChestIndex;
            public int SourceSlot;
            public int ItemId;
            public int Amount;
            public int ActualTaken;
            public string ItemName;
        }
        /// <summary>
        /// Place items directly into player inventory slots (no QuickSpawnItem, no world drop).
        /// Items are immediately available for subsequent crafting steps.
        /// </summary>
        private bool PlaceInInventoryDirect(Player player, int itemId, int stack)
        {
            try
            {
                int remaining = stack;

                // First pass: stack with existing items of same type
                for (int i = 0; i < Math.Min(player.inventory.Length, 50) && remaining > 0; i++)
                {
                    var slot = player.inventory[i];
                    if (slot == null || slot.type != itemId) continue;

                    int maxStack = slot.maxStack > 0 ? slot.maxStack : 9999;
                    if (slot.stack < maxStack)
                    {
                        int toAdd = Math.Min(remaining, maxStack - slot.stack);
                        slot.stack += toAdd;
                        remaining -= toAdd;
                    }
                }

                // Second pass: use empty slots
                for (int i = 0; i < Math.Min(player.inventory.Length, 50) && remaining > 0; i++)
                {
                    var slot = player.inventory[i];
                    if (slot == null || slot.type != 0) continue;

                    slot.SetDefaults(itemId);
                    int maxStack = slot.maxStack > 0 ? slot.maxStack : 9999;
                    int toPlace = Math.Min(remaining, maxStack);
                    slot.stack = toPlace;
                    remaining -= toPlace;
                }

                if (remaining > 0)
                {
                    _log.Warn($"PlaceInInventoryDirect: inventory full, {remaining}x {itemId} could not be placed");
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                _log.Error($"PlaceInInventoryDirect failed: {ex.Message}");
                return false;
            }
        }

        private bool GiveItemToPlayer(Player player, Item template, int stack)
        {
            try
            {
                var source = new Terraria.DataStructures.EntitySource_Parent(player);
                int remaining = stack;
                while (remaining > 0)
                {
                    // GetItem's empty-slot path takes the supplied Item as-is, so
                    // bulk output must be divided before invoking native pickup.
                    int amount = Math.Min(remaining, template.maxStack);
                    var item = template.Clone();
                    item.stack = amount;
                    if (IsSingleplayer)
                    {
                        // Same pickup settings/priorities as QuickSpawnItem, but retain
                        // the overflow result and require a playable native spawn slot.
                        item.newAndShiny = true;
                        var overflow = player.GetItem(item, GetItemSettings.PickupItemFromWorld);
                        if (!overflow.IsAir)
                        {
                            int slot = Item.NewItem(player.GetItemSource_InventoryOverflow(), player.Center,
                                overflow.type, overflow.stack, overflow.prefix, NewItemOwnership.ReserveForLocalPlayer);
                            if (slot < 0 || slot >= Main.maxItems || !Main.item[slot].active)
                            { _log.Warn("Native crafting output has no playable world slot"); return false; }
                        }
                    }
                    else player.QuickSpawnItem(source, item);
                    remaining -= amount;
                }
                return true;
            }
            catch (Exception ex)
            {
                _log.Error($"GiveItemToPlayer failed: {ex.Message}");
                return false;
            }
        }
    }
}
