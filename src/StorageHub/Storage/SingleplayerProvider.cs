using System;
using System.Collections.Generic;
using Terraria;
using StorageHub.Config;
using TerrariaModder.Core.Logging;

namespace StorageHub.Storage
{
    /// <summary>
    /// Singleplayer implementation of IStorageProvider.
    /// Uses direct typed access to Main.chest[], player inventory, and banks.
    ///
    /// DESIGN RATIONALE - Why this is the singleplayer implementation:
    ///
    /// Singleplayer can use simple direct access because:
    /// - State is local (no sync issues)
    /// - No network latency
    /// - "Add before remove" pattern handles crash safety
    ///
    /// Multiplayer will need MultiplayerProvider that:
    /// - Sends packets instead of direct access
    /// - Waits for server confirmation
    /// - Handles network errors gracefully
    ///
    /// The UI doesn't know the difference - it just calls IStorageProvider methods.
    /// </summary>
    public class SingleplayerProvider : IStorageProvider
    {
        private readonly ILogger _log;
        private readonly ChestRegistry _registry;
        private readonly StorageHubConfig _config;

        public SingleplayerProvider(ILogger log, ChestRegistry registry, StorageHubConfig config)
        {
            _log = log;
            _registry = registry;
            _config = config;
        }

        public List<ItemSnapshot> GetAllItems()
        {
            var items = new List<ItemSnapshot>();

            try
            {
                // Get player inventory
                var player = GetLocalPlayer();
                if (player != null)
                {
                    int beforeInv = items.Count;
                    AddInventoryItems(player, items);
                    int afterInv = items.Count;
                    AddBankItems(player, items);
                    int afterBank = items.Count;
                    _log.Debug($"[Storage] Inventory: {afterInv - beforeInv} items, Banks: {afterBank - afterInv} items");
                }

                // Get items from registered chests
                var chests = Main.chest;
                int registeredCount = _registry.Count;
                int foundChests = 0;
                int chestItems = 0;

                if (chests != null)
                {
                    foreach (var pos in _registry.GetRegisteredPositions())
                    {
                        int chestIndex = FindChestAtPosition(pos.x, pos.y);
                        if (chestIndex >= 0)
                        {
                            foundChests++;
                            int before = items.Count;
                            AddChestItems(chests[chestIndex], chestIndex, items);
                            chestItems += items.Count - before;
                        }
                    }
                }

                _log.Debug($"[Storage] Registry: {registeredCount} chests, Found: {foundChests}, ChestItems: {chestItems}");
            }
            catch (Exception ex)
            {
                _log.Error($"Error getting all items: {ex.Message}");
            }

            return items;
        }

        public List<ItemSnapshot> GetItemsInRange(float playerX, float playerY, int range)
        {
            var items = new List<ItemSnapshot>();

            try
            {
                // Player inventory is always "in range"
                var player = GetLocalPlayer();
                if (player != null)
                {
                    AddInventoryItems(player, items);

                    // Portable banks are always accessible
                    AddBankItems(player, items);
                }

                // Get items from registered chests within range
                var chests = Main.chest;
                if (chests != null)
                {
                    // Handle max range (int.MaxValue) - everything is in range
                    bool maxRange = (range == int.MaxValue);

                    foreach (var pos in _registry.GetRegisteredPositions())
                    {
                        bool inRange = maxRange;

                        if (!maxRange)
                        {
                            // Convert tile position to world position (1 tile = 16 pixels)
                            float chestWorldX = pos.x * 16;
                            float chestWorldY = pos.y * 16;

                            float dx = chestWorldX - playerX;
                            float dy = chestWorldY - playerY;
                            float distSq = dx * dx + dy * dy;

                            // Use long to prevent overflow when range is large
                            long rangeSq = (long)range * range;
                            inRange = distSq <= rangeSq;
                        }

                        if (inRange)
                        {
                            int chestIndex = FindChestAtPosition(pos.x, pos.y);
                            if (chestIndex >= 0)
                            {
                                AddChestItems(chests[chestIndex], chestIndex, items);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _log.Error($"Error getting items in range: {ex.Message}");
            }

            return items;
        }

        public virtual bool TakeItem(int sourceChestIndex, int sourceSlot, int count, out ItemSnapshot taken)
        {
            taken = default;
            if (count <= 0) return false;

            try
            {
                var itemArray = ResolveItems(GetLocalPlayer(), sourceChestIndex);

                if (itemArray == null) return false;

                // Bounds check for sourceSlot
                if (sourceSlot < 0 || sourceSlot >= itemArray.Length) return false;

                var item = itemArray[sourceSlot];
                if (item == null) return false;

                int itemType = item.type;
                int itemStack = item.stack;

                if (itemType <= 0 || itemStack <= 0) return false;

                int actualCount = Math.Min(count, itemStack);

                // Create snapshot before modifying
                taken = CreateSnapshot(item, sourceChestIndex, sourceSlot);

                // Modify the actual item
                int newStack = itemStack - actualCount;
                if (newStack <= 0)
                {
                    // Clear the item
                    item.type = 0;
                    item.stack = 0;
                }
                else
                {
                    item.stack = newStack;
                }

                // Update snapshot with actual taken count, preserving all category flags
                taken = new ItemSnapshot(
                    taken.ItemId,
                    actualCount,
                    taken.Prefix,
                    taken.Name,
                    taken.MaxStack,
                    taken.Rarity,
                    taken.SourceChestIndex,
                    taken.SourceSlot,
                    taken.Damage,
                    taken.IsPickaxe,
                    taken.IsAxe,
                    taken.IsHammer,
                    taken.IsArmor,
                    taken.IsAccessory,
                    taken.IsConsumable,
                    taken.IsPlaceable,
                    taken.IsMaterial
                );

                _log.Debug($"Took {actualCount}x {taken.Name} from {SourceIndex.GetSourceName(sourceChestIndex)} slot {sourceSlot}");
                return true;
            }
            catch (Exception ex)
            {
                _log.Error($"Error taking item: {ex.Message}");
                return false;
            }
        }

        public virtual int DepositItem(ItemSnapshot item, out int depositedToChest)
        {
            depositedToChest = -1;

            try
            {
                var chests = Main.chest;
                if (chests == null) return 0;

                // Track how much we still need to deposit (item is readonly struct)
                int remaining = item.Stack;

                // First pass: Try to stack with existing items of same type
                foreach (var pos in _registry.GetRegisteredPositions())
                {
                    if (remaining <= 0) break;

                    int chestIndex = FindChestAtPosition(pos.x, pos.y);
                    if (chestIndex < 0) continue;

                    var chest = chests[chestIndex];
                    if (chest == null) continue;

                    var chestItems = chest.item;
                    if (chestItems == null) continue;

                    for (int i = 0; i < chestItems.Length; i++)
                    {
                        if (remaining <= 0) break;

                        var chestItem = chestItems[i];
                        if (chestItem == null) continue;

                        int type = chestItem.type;
                        int stack = chestItem.stack;
                        int prefix = chestItem.prefix;
                        int maxStack = chestItem.maxStack;

                        // Found matching item that can stack
                        if (type == item.ItemId && prefix == item.Prefix && stack < maxStack)
                        {
                            int canAdd = maxStack - stack;
                            int toAdd = Math.Min(canAdd, remaining);

                            chestItem.stack = stack + toAdd;
                            depositedToChest = chestIndex;
                            OnChestModified(chestIndex);
                            remaining -= toAdd;
                            _log.Debug($"Stacked {toAdd}x {item.Name} into chest {chestIndex} slot {i}, {remaining} remaining");

                            if (remaining <= 0)
                            {
                                return item.Stack;
                            }
                            // Partially deposited - continue looking for more space
                        }
                    }
                }

                // Second pass: Find empty slots for remaining items
                foreach (var pos in _registry.GetRegisteredPositions())
                {
                    if (remaining <= 0) break;

                    int chestIndex = FindChestAtPosition(pos.x, pos.y);
                    if (chestIndex < 0) continue;

                    var chest = chests[chestIndex];
                    if (chest == null) continue;

                    var chestItems = chest.item;
                    if (chestItems == null) continue;

                    for (int i = 0; i < chestItems.Length; i++)
                    {
                        if (remaining <= 0) break;

                        var chestItem = chestItems[i];
                        if (chestItem == null) continue;

                        int type = chestItem.type;
                        if (type == 0)
                        {
                            // Empty slot - use SetDefaults to properly initialize all item fields
                            chestItem.SetDefaults(item.ItemId);

                            // Get maxStack for this item type after SetDefaults
                            int maxStack = chestItem.maxStack;
                            if (maxStack <= 0) maxStack = 9999;

                            int toDeposit = Math.Min(remaining, maxStack);
                            chestItem.stack = toDeposit;
                            ApplyPrefix(chestItem, item.Prefix);

                            depositedToChest = chestIndex;
                            OnChestModified(chestIndex);
                            remaining -= toDeposit;
                            _log.Debug($"Deposited {toDeposit}x {item.Name} into chest {chestIndex} slot {i}, {remaining} remaining");

                            if (remaining <= 0)
                            {
                                return item.Stack;
                            }
                        }
                    }
                }

                int deposited = item.Stack - remaining;
                if (deposited > 0)
                {
                    // Partial success - some items were deposited but not all
                    _log.Warn($"Partial deposit: {deposited}x {item.Name} deposited, {remaining}x could not fit");
                    return deposited;
                }

                _log.Warn($"No space to deposit {item.Name}");
                return 0;
            }
            catch (Exception ex)
            {
                _log.Error($"DepositItem failed: {ex.Message}");
                return 0;
            }
        }

        public virtual bool MoveToInventory(int sourceChestIndex, int sourceSlot, int count)
        {
            try
            {
                // Validate we can access inventory BEFORE taking items
                var player = GetLocalPlayer();
                if (player == null)
                {
                    _log.Error("Cannot move to inventory: player not found");
                    return false;
                }

                var inventory = player.inventory;
                if (inventory == null)
                {
                    _log.Error("Cannot move to inventory: inventory not found");
                    return false;
                }

                // Now take the item (validated inventory access first)
                if (!TakeItem(sourceChestIndex, sourceSlot, count, out var taken))
                {
                    return false;
                }

                int remaining = taken.Stack;

                // First pass: Try to stack with existing items (cap at 50 = main inventory, skip coin/ammo slots)
                int mainSlots = Math.Min(inventory.Length, 50);
                for (int i = 0; i < mainSlots && remaining > 0; i++)
                {
                    var slot = inventory[i];
                    if (slot == null) continue;

                    int type = slot.type;
                    int stack = slot.stack;
                    int prefix = slot.prefix;
                    int maxStack = slot.maxStack;

                    if (type == taken.ItemId && prefix == taken.Prefix && stack < maxStack)
                    {
                        int canAdd = maxStack - stack;
                        int toAdd = Math.Min(canAdd, remaining);
                        slot.stack = stack + toAdd;
                        remaining -= toAdd;
                    }
                }

                // Second pass: Find empty slot (cap at 50 = main inventory, skip coin/ammo slots)
                for (int i = 0; i < mainSlots && remaining > 0; i++)
                {
                    var slot = inventory[i];
                    if (slot == null) continue;

                    int type = slot.type;
                    if (type == 0)
                    {
                        // Empty slot - use SetDefaults to properly initialize all item fields
                        slot.SetDefaults(taken.ItemId);
                        // Cap at maxStack to prevent invalid stack sizes
                        int maxStack = slot.maxStack;
                        if (maxStack <= 0) maxStack = 9999;
                        int toPlace = Math.Min(remaining, maxStack);
                        slot.stack = toPlace;
                        ApplyPrefix(slot, taken.Prefix);
                        remaining -= toPlace;
                    }
                }

                if (remaining > 0)
                {
                    // CRITICAL: Return overflow items to storage - do not lose them!
                    _log.Warn($"Could not fit {remaining}x {taken.Name} in inventory, returning to storage...");

                    var overflow = new ItemSnapshot(
                        taken.ItemId, remaining, taken.Prefix,
                        taken.Name, taken.MaxStack, taken.Rarity,
                        SourceIndex.PlayerInventory, 0);

                    int depositedCount = DepositItem(overflow, out int returnedChest);
                    if (depositedCount >= remaining)
                    {
                        _log.Info($"Returned {remaining}x {taken.Name} to chest {returnedChest}");
                        // Partial success - some items moved to inventory
                        return true;
                    }
                    else if (depositedCount > 0)
                    {
                        _log.Error($"CRITICAL: Only returned {depositedCount}/{remaining}x {taken.Name} to storage - {remaining - depositedCount} items lost!");
                        return false;
                    }
                    else
                    {
                        _log.Error($"CRITICAL: Could not return {remaining}x {taken.Name} to storage - items may be lost!");
                        return false;
                    }
                }

                _log.Debug($"Moved {taken.Stack}x {taken.Name} to inventory");
                return true;
            }
            catch (Exception ex)
            {
                _log.Error($"MoveToInventory failed: {ex.Message}");
                return false;
            }
        }

        public IReadOnlyList<ChestInfo> GetRegisteredChests()
        {
            var result = new List<ChestInfo>();

            try
            {
                var chests = Main.chest;
                if (chests == null) return result;

                var player = GetLocalPlayer();
                var playerPos = GetPlayerPosition(player);

                foreach (var pos in _registry.GetRegisteredPositions())
                {
                    int chestIndex = FindChestAtPosition(pos.x, pos.y);
                    if (chestIndex >= 0)
                    {
                        var chest = chests[chestIndex];
                        string name = chest?.name ?? "";

                        // Calculate if in range
                        float dx = pos.x * 16 - playerPos.x;
                        float dy = pos.y * 16 - playerPos.y;
                        float distSq = dx * dx + dy * dy;
                        int range = ProgressionTier.GetRange(_config.Tier);
                        // Use long to prevent overflow with large ranges (Tier 4 = int.MaxValue)
                        long rangeSq = (long)range * range;
                        bool inRange = distSq <= rangeSq;

                        // Count non-empty items
                        int itemCount = 0;
                        if (chest != null && chest.item != null)
                        {
                            for (int i = 0; i < chest.item.Length; i++)
                            {
                                var item = chest.item[i];
                                if (item != null && item.type > 0) itemCount++;
                            }
                        }

                        result.Add(new ChestInfo(chestIndex, pos.x, pos.y, name, inRange, itemCount));
                    }
                }
            }
            catch (Exception ex)
            {
                _log.Error($"Error getting registered chests: {ex.Message}");
            }

            return result;
        }

        public bool IsChestInRange(int chestIndex, float playerX, float playerY, int range)
        {
            try
            {
                var chests = Main.chest;
                if (chests == null || chestIndex < 0 || chestIndex >= chests.Length)
                    return false;

                var chest = chests[chestIndex];
                if (chest == null) return false;

                int x = chest.x;
                int y = chest.y;

                float dx = x * 16 - playerX;
                float dy = y * 16 - playerY;
                float distSq = dx * dx + dy * dy;

                // Use long to prevent overflow with large ranges (Tier 4 = int.MaxValue)
                long rangeSq = (long)range * range;
                return distSq <= rangeSq;
            }
            catch (Exception ex)
            {
                _log.Error($"Error checking chest range: {ex.Message}");
                return false;
            }
        }

        public void Refresh()
        {
            // For singleplayer, we always read fresh data
            // This method exists for multiplayer where we might need to request updates
        }

        public bool PlaceOnCursor(ItemSnapshot item)
        {
            try
            {
                if (item.IsEmpty) return false;

                var mouseItem = Main.mouseItem;
                if (mouseItem == null)
                {
                    _log.Error("Could not get Main.mouseItem");
                    return false;
                }

                // Check if cursor is empty
                int currentType = mouseItem.type;
                if (currentType != 0)
                {
                    _log.Warn("Cursor not empty, cannot place item");
                    return false;
                }

                // Set defaults for the item type (initializes all fields including fishingPole, damage, etc.)
                mouseItem.SetDefaults(item.ItemId);
                mouseItem.stack = item.Stack;

                // Apply prefix via Prefix(int) method to get stat modifiers (damage, speed, etc.)
                // This must happen AFTER SetDefaults which sets base stats
                ApplyPrefix(mouseItem, item.Prefix);

                _log.Debug($"Placed {item.Stack}x {item.Name} on cursor (prefix={item.Prefix})");
                return true;
            }
            catch (Exception ex)
            {
                _log.Error($"PlaceOnCursor failed: {ex.Message}");
                return false;
            }
        }

        public bool TakeItemToCursor(int sourceChestIndex, int sourceSlot, int count)
        {
            try
            {
                // Check if cursor is empty first
                if (!IsCursorEmpty())
                {
                    _log.Warn("Cursor not empty, cannot take item");
                    return false;
                }

                // Take the item from storage
                if (!TakeItem(sourceChestIndex, sourceSlot, count, out var taken))
                {
                    return false;
                }

                // Place on cursor
                if (!PlaceOnCursor(taken))
                {
                    // CRITICAL: Failed to place on cursor - MUST return items to storage
                    _log.Error("Failed to place on cursor after taking - attempting recovery...");

                    // Create a snapshot for deposit (use -1 for source since we're depositing fresh)
                    var recovery = new ItemSnapshot(
                        taken.ItemId, taken.Stack, taken.Prefix,
                        taken.Name, taken.MaxStack, taken.Rarity,
                        SourceIndex.PlayerInventory, 0);

                    int recoveredCount = DepositItem(recovery, out int depositedChest);
                    if (recoveredCount >= taken.Stack)
                    {
                        _log.Info($"Recovery successful - {taken.Stack}x {taken.Name} returned to chest {depositedChest}");
                    }
                    else if (recoveredCount > 0)
                    {
                        _log.Error($"CRITICAL: Partial recovery - {recoveredCount}/{taken.Stack}x {taken.Name} returned, {taken.Stack - recoveredCount} may be lost!");
                    }
                    else
                    {
                        _log.Error($"CRITICAL: Recovery failed - {taken.Stack}x {taken.Name} may be lost!");
                    }
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                _log.Error($"TakeItemToCursor failed: {ex.Message}");
                return false;
            }
        }

        public bool IsCursorEmpty()
        {
            try
            {
                var mouseItem = Main.mouseItem;
                if (mouseItem == null) return true;
                return mouseItem.type == 0;
            }
            catch
            {
                return true;
            }
        }

        internal static Item[] ResolveItems(Player player, int source)
        {
            switch (source)
            {
                case SourceIndex.PlayerInventory: return player?.inventory;
                case SourceIndex.PiggyBank: return player?.bank?.item;
                case SourceIndex.Safe: return player?.bank2?.item;
                case SourceIndex.DefendersForge: return player?.bank3?.item;
                case SourceIndex.VoidVault: return player?.bank4?.item;
                default: return source >= 0 && Main.chest != null && source < Main.chest.Length ? Main.chest[source]?.item : null;
            }
        }

        // Helper methods

        /// <summary>
        /// Apply prefix stat modifiers via Item.Prefix(int) method.
        /// Must be called AFTER SetDefaults since it multiplies base stats.
        /// </summary>
        private static void ApplyPrefix(Item item, int prefix)
        {
            if (prefix <= 0) return;
            item.Prefix(prefix);
        }

        private Player GetLocalPlayer()
        {
            try
            {
                int myPlayer = Main.myPlayer;
                var players = Main.player;
                if (players == null) return null;

                if (myPlayer < 0 || myPlayer >= players.Length)
                    return null;

                return players[myPlayer];
            }
            catch
            {
                return null;
            }
        }

        private (float x, float y) GetPlayerPosition(Player player)
        {
            try
            {
                if (player == null) return (0, 0);
                return (player.position.X, player.position.Y);
            }
            catch { }
            return (0, 0);
        }

        private int FindChestAtPosition(int x, int y)
        {
            var chests = Main.chest;
            if (chests == null) return -1;

            for (int i = 0; i < chests.Length; i++)
            {
                var chest = chests[i];
                if (chest == null) continue;

                if (chest.x == x && chest.y == y)
                    return i;
            }
            return -1;
        }

        private void AddInventoryItems(Player player, List<ItemSnapshot> items)
        {
            try
            {
                var inventory = player.inventory;
                if (inventory == null) return;

                for (int i = 0; i < Math.Min(inventory.Length, 50); i++)
                {
                    var item = inventory[i];
                    var snapshot = CreateSnapshot(item, SourceIndex.PlayerInventory, i);
                    if (!snapshot.IsEmpty)
                    {
                        items.Add(snapshot);
                    }
                }
            }
            catch (Exception ex)
            {
                _log.Error($"Error adding inventory items: {ex.Message}");
            }
        }

        private void AddBankItems(Player player, List<ItemSnapshot> items)
        {
            try
            {
                // Piggy bank
                if (player.bank != null)
                {
                    var bankItems = player.bank.item;
                    if (bankItems != null)
                    {
                        for (int i = 0; i < bankItems.Length; i++)
                        {
                            var snapshot = CreateSnapshot(bankItems[i], SourceIndex.PiggyBank, i);
                            if (!snapshot.IsEmpty) items.Add(snapshot);
                        }
                    }
                }

                // Safe
                if (player.bank2 != null)
                {
                    var bank2Items = player.bank2.item;
                    if (bank2Items != null)
                    {
                        for (int i = 0; i < bank2Items.Length; i++)
                        {
                            var snapshot = CreateSnapshot(bank2Items[i], SourceIndex.Safe, i);
                            if (!snapshot.IsEmpty) items.Add(snapshot);
                        }
                    }
                }

                // Defender's Forge
                if (player.bank3 != null)
                {
                    var bank3Items = player.bank3.item;
                    if (bank3Items != null)
                    {
                        for (int i = 0; i < bank3Items.Length; i++)
                        {
                            var snapshot = CreateSnapshot(bank3Items[i], SourceIndex.DefendersForge, i);
                            if (!snapshot.IsEmpty) items.Add(snapshot);
                        }
                    }
                }

                // Void Vault
                if (player.bank4 != null)
                {
                    var bank4Items = player.bank4.item;
                    if (bank4Items != null)
                    {
                        for (int i = 0; i < bank4Items.Length; i++)
                        {
                            var snapshot = CreateSnapshot(bank4Items[i], SourceIndex.VoidVault, i);
                            if (!snapshot.IsEmpty) items.Add(snapshot);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _log.Error($"Error adding bank items: {ex.Message}");
            }
        }

        /// <summary>
        /// Called whenever a chest slot is modified by DepositItem.
        /// Override in MultiplayerProvider to track which chests need packet 32 broadcasts.
        /// </summary>
        protected virtual void OnChestModified(int chestIndex) { }

        private void AddChestItems(Chest chest, int chestIndex, List<ItemSnapshot> items)
        {
            try
            {
                if (chest == null) return;
                var itemArray = chest.item;
                if (itemArray == null) return;

                for (int i = 0; i < itemArray.Length; i++)
                {
                    var snapshot = CreateSnapshot(itemArray[i], chestIndex, i);
                    if (!snapshot.IsEmpty)
                    {
                        items.Add(snapshot);
                    }
                }
            }
            catch (Exception ex)
            {
                _log.Error($"Error adding chest items: {ex.Message}");
            }
        }

        private ItemSnapshot CreateSnapshot(Item item, int sourceChestIndex, int sourceSlot)
        {
            try
            {
                if (item == null) return default;

                int itemType = item.type;
                if (itemType <= 0) return default;

                int stack = item.stack;
                if (stack <= 0) return default;

                int prefix = item.prefix;
                string name = item.Name ?? "";
                int maxStack = item.maxStack;
                int rarity = item.rare;

                // Get category info
                int damage = item.damage;
                int pick = item.pick;
                int axe = item.axe;
                int hammer = item.hammer;
                int headSlot = item.headSlot;
                int bodySlot = item.bodySlot;
                int legSlot = item.legSlot;
                bool accessory = item.accessory;
                bool consumable = item.consumable;
                int createTile = item.createTile;
                int createWall = item.createWall;
                bool material = item.material;

                return new ItemSnapshot(
                    itemType,
                    stack,
                    prefix,
                    name,
                    maxStack,
                    rarity,
                    sourceChestIndex,
                    sourceSlot,
                    damage: damage > 0 ? damage : (int?)null,
                    isPickaxe: pick > 0,
                    isAxe: axe > 0,
                    isHammer: hammer > 0,
                    isArmor: headSlot >= 0 || bodySlot >= 0 || legSlot >= 0,
                    isAccessory: accessory,
                    isConsumable: consumable,
                    isPlaceable: createTile >= 0 || createWall >= 0,
                    isMaterial: material
                );
            }
            catch (Exception ex)
            {
                _log.Error($"Error creating snapshot: {ex.Message}");
                return default;
            }
        }
    }
}
