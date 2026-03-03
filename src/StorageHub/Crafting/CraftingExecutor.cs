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

        public CraftingExecutor(ILogger log, IStorageProvider storage)
        {
            _log = log;
            _storage = storage;
        }

        /// <summary>
        /// Execute a recipe craft, consuming materials from ALL storage and creating items.
        ///
        /// IMPORTANT: Uses two-phase commit to prevent item loss:
        /// 1. Build a consumption plan (which slots to take from)
        /// 2. Execute all takes atomically - if ANY fails, abort before modifying
        /// 3. Create output items (directly to inventory if intermediate step, or via QuickSpawnItem)
        /// </summary>
        /// <param name="recipe">Recipe to craft.</param>
        /// <param name="count">Number of times to craft the recipe.</param>
        /// <param name="directToInventory">If true, place output directly in player inventory
        /// instead of using QuickSpawnItem. Used for intermediate recursive craft steps
        /// so the output is immediately available to the next step.</param>
        /// <returns>True if crafting succeeded.</returns>
        public bool ExecuteCraft(RecipeInfo recipe, int count = 1, bool directToInventory = false)
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

                // Get all items from storage
                var allItems = _storage.GetAllItems();

                // PHASE 1: Build consumption plan - identify exactly which slots we'll consume from
                var consumptionPlan = new List<ConsumptionEntry>();
                foreach (var ing in recipe.Ingredients)
                {
                    // Use long to prevent overflow, then validate
                    long neededLong = (long)ing.RequiredStack * count;
                    if (neededLong > int.MaxValue || neededLong < 0)
                    {
                        _log.Error($"Overflow calculating materials for {ing.Name}: {ing.RequiredStack} * {count}");
                        return false;
                    }
                    int needed = (int)neededLong;
                    int remaining = needed;

                    // Find items matching this ingredient (supports recipe groups)
                    foreach (var item in allItems)
                    {
                        if (remaining <= 0) break;
                        if (item.IsEmpty) continue;

                        // Match: exact ID for normal ingredients, any valid ID for recipe groups
                        bool matches = ing.IsRecipeGroup && ing.ValidItemIds != null
                            ? ing.ValidItemIds.Contains(item.ItemId)
                            : item.ItemId == ing.ItemId;
                        if (!matches) continue;

                        int toTake = Math.Min(item.Stack, remaining);
                        consumptionPlan.Add(new ConsumptionEntry
                        {
                            SourceChestIndex = item.SourceChestIndex,
                            SourceSlot = item.SourceSlot,
                            ItemId = item.ItemId,
                            Amount = toTake,
                            ItemName = item.Name
                        });
                        remaining -= toTake;
                    }

                    if (remaining > 0)
                    {
                        _log.Warn($"Not enough {ing.Name}: need {needed}, can only get {needed - remaining}");
                        return false;
                    }
                }

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
                    }
                    return false;
                }
                else if (!allSucceeded)
                {
                    return false;
                }

                // PHASE 3: Create the output item and give to player
                // Use long to prevent overflow, then validate
                long totalOutputLong = (long)recipe.OutputStack * count;
                if (totalOutputLong > int.MaxValue || totalOutputLong < 0)
                {
                    _log.Error($"Overflow calculating output for {recipe.OutputName}: {recipe.OutputStack} * {count}");
                    return false;
                }
                int totalOutput = (int)totalOutputLong;

                // For intermediate recursive craft steps, place directly in inventory
                // so the next step can immediately find the items. QuickSpawnItem drops
                // items on the ground with auto-pickup, which has a timing delay.
                bool created = directToInventory
                    ? PlaceInInventoryDirect(player, recipe.OutputItemId, totalOutput)
                    : GiveItemToPlayer(player, recipe.OutputItemId, totalOutput);
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

        private bool GiveItemToPlayer(Player player, int itemId, int stack)
        {
            try
            {
                // PRIMARY METHOD: Use QuickSpawnItem(IEntitySource, Item) overload
                // The (IEntitySource, int, int) overload calls Prefix(-1) which randomizes prefix.
                // Crafted items should have no prefix (vanilla behavior).
                var source = CreateEntitySource(player);
                if (source != null)
                {
                    var item = new Item();
                    item.SetDefaults(itemId);
                    item.stack = stack;
                    item.prefix = 0;
                    player.QuickSpawnItem(source, item);
                    return true;
                }

                // FALLBACK: Put directly in inventory slots
                int fallbackRemaining = stack;
                for (int i = 0; i < Math.Min(player.inventory.Length, 50) && fallbackRemaining > 0; i++)
                {
                    var slot = player.inventory[i];
                    if (slot == null || slot.type != 0) continue;

                    slot.SetDefaults(itemId);
                    int maxStack = slot.maxStack > 0 ? slot.maxStack : 9999;
                    int toPlace = Math.Min(fallbackRemaining, maxStack);
                    slot.stack = toPlace;
                    fallbackRemaining -= toPlace;
                }

                if (fallbackRemaining <= 0) return true;
                _log.Warn($"Inventory full, {fallbackRemaining}x {itemId} could not be placed");

                // LAST RESORT: Try mouse slot
                if (fallbackRemaining > 0 && Main.mouseItem != null && Main.mouseItem.type == 0)
                {
                    Main.mouseItem.SetDefaults(itemId);
                    int maxStack = Main.mouseItem.maxStack > 0 ? Main.mouseItem.maxStack : 9999;
                    Main.mouseItem.stack = Math.Min(fallbackRemaining, maxStack);
                    return true;
                }

                _log.Error("All item placement methods failed");
                return false;
            }
            catch (Exception ex)
            {
                _log.Error($"GiveItemToPlayer failed: {ex.Message}");
                return false;
            }
        }

        private Terraria.DataStructures.IEntitySource CreateEntitySource(Player player)
        {
            try
            {
                return new Terraria.DataStructures.EntitySource_Parent(player);
            }
            catch { }
            return null;
        }
    }
}
