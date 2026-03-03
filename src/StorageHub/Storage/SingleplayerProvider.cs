using System;
using System.Collections.Generic;
using Terraria;
using StorageHub.Config;
using StorageHub.DedicatedBlocks;
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
        private readonly DriveStorageState _driveStorage;
        private readonly bool _useDriveStorage;
        private const int MaxDriveDiskSlots = 8;
        private const int DriveSourceSlotStride = 10_000;

<<<<<<< HEAD
        // Reflection cache
        private static Type _mainType;
        private static Type _chestType;
        private static Type _itemType;
        private static Type _playerType;

        private static FieldInfo _chestArrayField;
        private static FieldInfo _playerArrayField;
        private static FieldInfo _myPlayerField;

        private static FieldInfo _chestItemField;
        private static FieldInfo _chestXField;
        private static FieldInfo _chestYField;
        private static FieldInfo _chestNameField;

        private static PropertyInfo _itemNameProp;
        private static FieldInfo _itemTypeField;
        private static FieldInfo _itemStackField;
        private static FieldInfo _itemPrefixField;
        private static FieldInfo _itemFavoritedField;
        private static FieldInfo _itemMaxStackField;
        private static FieldInfo _itemRarityField;
        private static FieldInfo _itemDamageField;
        private static FieldInfo _itemPickField;
        private static FieldInfo _itemAxeField;
        private static FieldInfo _itemHammerField;
        private static FieldInfo _itemHeadSlotField;
        private static FieldInfo _itemBodySlotField;
        private static FieldInfo _itemLegSlotField;
        private static FieldInfo _itemAccessoryField;
        private static FieldInfo _itemConsumableField;
        private static FieldInfo _itemCreateTileField;
        private static FieldInfo _itemCreateWallField;
        private static FieldInfo _itemMaterialField;
        private static FieldInfo _itemVanityField;
        private static FieldInfo _itemAmmoField;
        private static FieldInfo _itemNotAmmoField;
        private static FieldInfo _itemMeleeField;
        private static FieldInfo _itemRangedField;
        private static FieldInfo _itemMagicField;
        private static FieldInfo _itemSummonField;
        private static FieldInfo _itemThrownField;
        private static FieldInfo _itemSentryField;
        private static FieldInfo _itemShootField;
        private static FieldInfo _itemHealLifeField;
        private static FieldInfo _itemHealManaField;
        private static FieldInfo _itemPotionField;
        private static FieldInfo _itemDyeField;
        private static FieldInfo _itemHairDyeField;
        private static FieldInfo _itemMountTypeField;
        private static FieldInfo _itemBuffTypeField;
        private static FieldInfo _itemFishingPoleField;
        private static FieldInfo _itemBaitField;

        private static FieldInfo _playerInventoryField;
        private static FieldInfo _playerBankField;
        private static FieldInfo _playerBank2Field;
        private static FieldInfo _playerBank3Field;
        private static FieldInfo _playerBank4Field;
        private static FieldInfo _playerPositionField;

        private static FieldInfo _mouseItemField;
        private static MethodInfo _itemSetDefaultsMethod;
        private static MethodInfo _itemPrefixMethod;

        private static bool _reflectionInitialized = false;

        internal SingleplayerProvider(ILogger log, ChestRegistry registry, StorageHubConfig config, DriveStorageState driveStorage, bool useDriveStorage)
=======
        public SingleplayerProvider(ILogger log, ChestRegistry registry, StorageHubConfig config)
>>>>>>> inidar-main
        {
            _log = log;
            _registry = registry;
            _config = config;
<<<<<<< HEAD
            _driveStorage = driveStorage;
            _useDriveStorage = useDriveStorage;

            if (!_reflectionInitialized)
            {
                InitializeReflection();
            }
        }

        private void InitializeReflection()
        {
            try
            {
                _mainType = Type.GetType("Terraria.Main, Terraria")
                    ?? Assembly.Load("Terraria").GetType("Terraria.Main");
                _chestType = Type.GetType("Terraria.Chest, Terraria")
                    ?? Assembly.Load("Terraria").GetType("Terraria.Chest");
                _itemType = Type.GetType("Terraria.Item, Terraria")
                    ?? Assembly.Load("Terraria").GetType("Terraria.Item");
                _playerType = Type.GetType("Terraria.Player, Terraria")
                    ?? Assembly.Load("Terraria").GetType("Terraria.Player");

                if (_mainType != null)
                {
                    _chestArrayField = _mainType.GetField("chest", BindingFlags.Public | BindingFlags.Static);
                    _playerArrayField = _mainType.GetField("player", BindingFlags.Public | BindingFlags.Static);
                    _myPlayerField = _mainType.GetField("myPlayer", BindingFlags.Public | BindingFlags.Static);
                }

                if (_chestType != null)
                {
                    _chestItemField = _chestType.GetField("item", BindingFlags.Public | BindingFlags.Instance);
                    _chestXField = _chestType.GetField("x", BindingFlags.Public | BindingFlags.Instance);
                    _chestYField = _chestType.GetField("y", BindingFlags.Public | BindingFlags.Instance);
                    _chestNameField = _chestType.GetField("name", BindingFlags.Public | BindingFlags.Instance);
                }

                if (_itemType != null)
                {
                    _itemNameProp = _itemType.GetProperty("Name", BindingFlags.Public | BindingFlags.Instance);
                    _itemTypeField = _itemType.GetField("type", BindingFlags.Public | BindingFlags.Instance);
                    _itemStackField = _itemType.GetField("stack", BindingFlags.Public | BindingFlags.Instance);
                    _itemPrefixField = _itemType.GetField("prefix", BindingFlags.Public | BindingFlags.Instance);
                    _itemFavoritedField = _itemType.GetField("favorited", BindingFlags.Public | BindingFlags.Instance);
                    _itemMaxStackField = _itemType.GetField("maxStack", BindingFlags.Public | BindingFlags.Instance);
                    _itemRarityField = _itemType.GetField("rare", BindingFlags.Public | BindingFlags.Instance);
                    // Category fields
                    _itemDamageField = _itemType.GetField("damage", BindingFlags.Public | BindingFlags.Instance);
                    _itemPickField = _itemType.GetField("pick", BindingFlags.Public | BindingFlags.Instance);
                    _itemAxeField = _itemType.GetField("axe", BindingFlags.Public | BindingFlags.Instance);
                    _itemHammerField = _itemType.GetField("hammer", BindingFlags.Public | BindingFlags.Instance);
                    _itemHeadSlotField = _itemType.GetField("headSlot", BindingFlags.Public | BindingFlags.Instance);
                    _itemBodySlotField = _itemType.GetField("bodySlot", BindingFlags.Public | BindingFlags.Instance);
                    _itemLegSlotField = _itemType.GetField("legSlot", BindingFlags.Public | BindingFlags.Instance);
                    _itemAccessoryField = _itemType.GetField("accessory", BindingFlags.Public | BindingFlags.Instance);
                    _itemConsumableField = _itemType.GetField("consumable", BindingFlags.Public | BindingFlags.Instance);
                    _itemCreateTileField = _itemType.GetField("createTile", BindingFlags.Public | BindingFlags.Instance);
                    _itemCreateWallField = _itemType.GetField("createWall", BindingFlags.Public | BindingFlags.Instance);
                    _itemMaterialField = _itemType.GetField("material", BindingFlags.Public | BindingFlags.Instance);
                    _itemVanityField = _itemType.GetField("vanity", BindingFlags.Public | BindingFlags.Instance);
                    _itemAmmoField = _itemType.GetField("ammo", BindingFlags.Public | BindingFlags.Instance);
                    _itemNotAmmoField = _itemType.GetField("notAmmo", BindingFlags.Public | BindingFlags.Instance);
                    _itemMeleeField = _itemType.GetField("melee", BindingFlags.Public | BindingFlags.Instance);
                    _itemRangedField = _itemType.GetField("ranged", BindingFlags.Public | BindingFlags.Instance);
                    _itemMagicField = _itemType.GetField("magic", BindingFlags.Public | BindingFlags.Instance);
                    _itemSummonField = _itemType.GetField("summon", BindingFlags.Public | BindingFlags.Instance);
                    _itemThrownField = _itemType.GetField("thrown", BindingFlags.Public | BindingFlags.Instance);
                    _itemSentryField = _itemType.GetField("sentry", BindingFlags.Public | BindingFlags.Instance);
                    _itemShootField = _itemType.GetField("shoot", BindingFlags.Public | BindingFlags.Instance);
                    _itemHealLifeField = _itemType.GetField("healLife", BindingFlags.Public | BindingFlags.Instance);
                    _itemHealManaField = _itemType.GetField("healMana", BindingFlags.Public | BindingFlags.Instance);
                    _itemPotionField = _itemType.GetField("potion", BindingFlags.Public | BindingFlags.Instance);
                    _itemDyeField = _itemType.GetField("dye", BindingFlags.Public | BindingFlags.Instance);
                    _itemHairDyeField = _itemType.GetField("hairDye", BindingFlags.Public | BindingFlags.Instance);
                    _itemMountTypeField = _itemType.GetField("mountType", BindingFlags.Public | BindingFlags.Instance);
                    _itemBuffTypeField = _itemType.GetField("buffType", BindingFlags.Public | BindingFlags.Instance);
                    _itemFishingPoleField = _itemType.GetField("fishingPole", BindingFlags.Public | BindingFlags.Instance);
                    _itemBaitField = _itemType.GetField("bait", BindingFlags.Public | BindingFlags.Instance);
                }

                if (_playerType != null)
                {
                    _playerInventoryField = _playerType.GetField("inventory", BindingFlags.Public | BindingFlags.Instance);
                    _playerBankField = _playerType.GetField("bank", BindingFlags.Public | BindingFlags.Instance);
                    _playerBank2Field = _playerType.GetField("bank2", BindingFlags.Public | BindingFlags.Instance);
                    _playerBank3Field = _playerType.GetField("bank3", BindingFlags.Public | BindingFlags.Instance);
                    _playerBank4Field = _playerType.GetField("bank4", BindingFlags.Public | BindingFlags.Instance);
                    _playerPositionField = _playerType.GetField("position", BindingFlags.Public | BindingFlags.Instance);
                }

                if (_mainType != null)
                {
                    _mouseItemField = _mainType.GetField("mouseItem", BindingFlags.Public | BindingFlags.Static);
                }

                if (_itemType != null)
                {
                    // SetDefaults signature is SetDefaults(int Type, ItemVariant variant = null)
                    // GetMethod with just typeof(int) won't match a 2-param method, so search by name
                    foreach (var m in _itemType.GetMethods(BindingFlags.Public | BindingFlags.Instance))
                    {
                        if (m.Name == "SetDefaults")
                        {
                            var p = m.GetParameters();
                            if (p.Length >= 1 && p[0].ParameterType == typeof(int))
                            {
                                _itemSetDefaultsMethod = m;
                                break;
                            }
                        }
                    }

                    // Item.Prefix(int) applies stat modifiers from prefix
                    _itemPrefixMethod = _itemType.GetMethod("Prefix", new[] { typeof(int) });
                }

                _reflectionInitialized = true;
                _log.Debug("SingleplayerProvider reflection initialized");
            }
            catch (Exception ex)
            {
                _log.Error($"Failed to initialize reflection: {ex.Message}");
            }
=======
>>>>>>> inidar-main
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

<<<<<<< HEAD
=======
                // Get items from registered chests
                var chests = Main.chest;
>>>>>>> inidar-main
                int registeredCount = _registry.Count;
                int foundContainers = 0;
                int containerItems = 0;

                if (_useDriveStorage && _driveStorage != null)
                {
                    var chests = _chestArrayField?.GetValue(null) as Array;
                    if (chests == null)
                        return items;

                    foreach (var pos in _registry.GetRegisteredPositions())
                    {
<<<<<<< HEAD
                        foundContainers++;
                        containerItems += AddDriveItems(chests, pos.x, pos.y, BuildDriveSourceIndex(pos.x, pos.y), items);
                    }
                }
                else
                {
                    // Get items from registered chests
                    var chests = _chestArrayField?.GetValue(null) as Array;
                    if (chests != null)
                    {
                        foreach (var pos in _registry.GetRegisteredPositions())
                        {
                            int chestIndex = FindChestAtPosition(chests, pos.x, pos.y);
                            if (chestIndex >= 0)
                            {
                                foundContainers++;
                                int before = items.Count;
                                AddChestItems(chests.GetValue(chestIndex), chestIndex, items);
                                containerItems += items.Count - before;
                            }
=======
                        int chestIndex = FindChestAtPosition(pos.x, pos.y);
                        if (chestIndex >= 0)
                        {
                            foundChests++;
                            int before = items.Count;
                            AddChestItems(chests[chestIndex], chestIndex, items);
                            chestItems += items.Count - before;
>>>>>>> inidar-main
                        }
                    }
                }

                _log.Debug($"[Storage] Registry: {registeredCount} nodes, Found: {foundContainers}, Items: {containerItems}");
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

<<<<<<< HEAD
                // Get items from registered storage nodes within range
                if (_useDriveStorage && _driveStorage != null)
=======
                // Get items from registered chests within range
                var chests = Main.chest;
                if (chests != null)
>>>>>>> inidar-main
                {
                    var chests = _chestArrayField?.GetValue(null) as Array;
                    if (chests == null)
                        return items;

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
<<<<<<< HEAD
                            AddDriveItems(chests, pos.x, pos.y, BuildDriveSourceIndex(pos.x, pos.y), items);
                        }
                    }
                }
                else
                {
                    var chests = _chestArrayField?.GetValue(null) as Array;
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
                                int chestIndex = FindChestAtPosition(chests, pos.x, pos.y);
                                if (chestIndex >= 0)
                                {
                                    AddChestItems(chests.GetValue(chestIndex), chestIndex, items);
                                }
=======
                            int chestIndex = FindChestAtPosition(pos.x, pos.y);
                            if (chestIndex >= 0)
                            {
                                AddChestItems(chests[chestIndex], chestIndex, items);
>>>>>>> inidar-main
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

        public bool TakeItem(int sourceChestIndex, int sourceSlot, int count, out ItemSnapshot taken)
        {
            taken = default;

            try
            {
                Item[] itemArray = null;

                if (sourceChestIndex == SourceIndex.PlayerInventory)
                {
                    var player = GetLocalPlayer();
                    if (player != null) itemArray = player.inventory;
                }
                else if (sourceChestIndex == SourceIndex.PiggyBank)
                {
                    var player = GetLocalPlayer();
                    if (player != null) itemArray = player.bank.item;
                }
                else if (sourceChestIndex == SourceIndex.Safe)
                {
                    var player = GetLocalPlayer();
                    if (player != null) itemArray = player.bank2.item;
                }
                else if (sourceChestIndex == SourceIndex.DefendersForge)
                {
                    var player = GetLocalPlayer();
                    if (player != null) itemArray = player.bank3.item;
                }
                else if (sourceChestIndex == SourceIndex.VoidVault)
                {
                    var player = GetLocalPlayer();
                    if (player != null) itemArray = player.bank4.item;
                }
                else if (_useDriveStorage && IsDriveSourceIndex(sourceChestIndex))
                {
                    if (!TryDecodeDriveSourceIndex(sourceChestIndex, out int driveX, out int driveY))
                        return false;

                    if (!TryDecodeDriveSourceSlot(sourceSlot, out int diskSlot, out int diskItemSlot))
                        return false;

                    var chests = _chestArrayField?.GetValue(null) as Array;
                    if (!TryGetDriveDiskSlots(chests, driveX, driveY, out _, out var diskSlots))
                        return false;

                    int slotLimit = GetDriveDiskSlotLimit(diskSlots);
                    if (diskSlot < 0 || diskSlot >= slotLimit)
                        return false;

                    var diskItem = diskSlots.GetValue(diskSlot);
                    if (!TryGetDiskIdentity(diskItem, assignIfMissing: false, out int diskItemType, out int diskUid))
                        return false;

                    var disk = _driveStorage.EnsureDisk(diskItemType, diskUid);
                    if (disk == null || diskItemSlot < 0 || diskItemSlot >= disk.Items.Count)
                        return false;

                    var existing = disk.Items[diskItemSlot];
                    int driveActualCount = Math.Min(count, existing.Stack);
                    if (driveActualCount <= 0)
                        return false;

                    taken = CreateDriveSnapshot(
                        existing.ItemId,
                        driveActualCount,
                        existing.Prefix,
                        sourceChestIndex,
                        sourceSlot);

                    int driveNewStack = existing.Stack - driveActualCount;
                    if (driveNewStack <= 0)
                    {
                        disk.Items.RemoveAt(diskItemSlot);
                    }
                    else
                    {
                        disk.Items[diskItemSlot] = new DriveItemRecord(existing.ItemId, existing.Prefix, driveNewStack);
                    }

                    _driveStorage.MarkDirty();
                    _log.Debug($"Took {driveActualCount}x {taken.Name} from drive source {sourceChestIndex} slot {sourceSlot}");
                    return true;
                }
                else if (sourceChestIndex >= 0)
                {
                    var chests = Main.chest;
                    if (chests != null && sourceChestIndex < chests.Length)
                    {
                        var chest = chests[sourceChestIndex];
                        if (chest != null)
                        {
                            itemArray = chest.item;
                        }
                    }
                }

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

        public int DepositItem(ItemSnapshot item, out int depositedToChest)
        {
            depositedToChest = -1;

            try
            {
<<<<<<< HEAD
                if (_useDriveStorage && _driveStorage != null)
                    return DepositItemToDrives(item, out depositedToChest);

                var chests = _chestArrayField?.GetValue(null) as Array;
=======
                var chests = Main.chest;
>>>>>>> inidar-main
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

        public bool MoveToInventory(int sourceChestIndex, int sourceSlot, int count)
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
<<<<<<< HEAD
=======
                var chests = Main.chest;
                if (chests == null) return result;

>>>>>>> inidar-main
                var player = GetLocalPlayer();
                var playerPos = GetPlayerPosition(player);

                if (_useDriveStorage && _driveStorage != null)
                {
<<<<<<< HEAD
                    var chests = _chestArrayField?.GetValue(null) as Array;

                    foreach (var pos in _registry.GetRegisteredPositions())
                    {
                        float dx = pos.x * 16 - playerPos.x;
                        float dy = pos.y * 16 - playerPos.y;
                        float distSq = dx * dx + dy * dy;
                        int range = ProgressionTier.GetRange(_config.Tier);
                        long rangeSq = (long)range * range;
                        bool inRange = distSq <= rangeSq;

                        int diskCount = 0;
                        int itemCount = 0;
                        if (TryGetDriveDiskSlots(chests, pos.x, pos.y, out _, out var diskSlots))
                        {
                            int slotLimit = GetDriveDiskSlotLimit(diskSlots);
                            for (int diskSlot = 0; diskSlot < slotLimit; diskSlot++)
                            {
                                var diskItem = diskSlots.GetValue(diskSlot);
                                if (!TryGetDiskIdentity(diskItem, assignIfMissing: true, out int diskItemType, out int diskUid))
                                    continue;

                                var disk = _driveStorage.EnsureDisk(diskItemType, diskUid);
                                if (disk == null)
                                    continue;

                                diskCount++;
                                for (int i = 0; i < disk.Items.Count; i++)
                                {
                                    var entry = disk.Items[i];
                                    if (entry.ItemId > 0 && entry.Stack > 0)
                                        itemCount++;
                                }
                            }
                        }

                        string name = $"Storage Drive ({diskCount}/{MaxDriveDiskSlots} disks)";
                        int sourceIndex = BuildDriveSourceIndex(pos.x, pos.y);
                        result.Add(new ChestInfo(sourceIndex, pos.x, pos.y, name, inRange, itemCount));
                    }
                }
                else
                {
                    var chests = _chestArrayField?.GetValue(null) as Array;
                    if (chests == null) return result;

                    foreach (var pos in _registry.GetRegisteredPositions())
                    {
                        int chestIndex = FindChestAtPosition(chests, pos.x, pos.y);
                        if (chestIndex < 0)
                            continue;

                        var chest = chests.GetValue(chestIndex);
                        string name = _chestNameField?.GetValue(chest) as string ?? "";
=======
                    int chestIndex = FindChestAtPosition(pos.x, pos.y);
                    if (chestIndex >= 0)
                    {
                        var chest = chests[chestIndex];
                        string name = chest?.name ?? "";
>>>>>>> inidar-main

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
<<<<<<< HEAD
                if (_useDriveStorage && IsDriveSourceIndex(chestIndex) &&
                    TryDecodeDriveSourceIndex(chestIndex, out int driveX, out int driveY))
                {
                    float driveDx = driveX * 16 - playerX;
                    float driveDy = driveY * 16 - playerY;
                    float driveDistSq = driveDx * driveDx + driveDy * driveDy;
                    long rangeSqDrive = (long)range * range;
                    return driveDistSq <= rangeSqDrive;
                }

                var chests = _chestArrayField?.GetValue(null) as Array;
=======
                var chests = Main.chest;
>>>>>>> inidar-main
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

        public int DepositFromCursor(bool singleItem)
        {
            try
            {
                if (_mouseItemField == null) return 0;

                var mouseItem = _mouseItemField.GetValue(null);
                if (mouseItem == null) return 0;

                var snapshot = CreateSnapshot(mouseItem, SourceIndex.PlayerInventory, 0);
                if (snapshot.IsEmpty) return 0;

                int requested = singleItem ? 1 : snapshot.Stack;
                if (requested <= 0) return 0;

                var toDeposit = new ItemSnapshot(
                    snapshot.ItemId,
                    requested,
                    snapshot.Prefix,
                    snapshot.Name,
                    snapshot.MaxStack,
                    snapshot.Rarity,
                    snapshot.SourceChestIndex,
                    snapshot.SourceSlot);

                int deposited = DepositItem(toDeposit, out _);
                if (deposited <= 0) return 0;

                int currentStack = GetSafeInt(_itemStackField, mouseItem, 0);
                int newStack = currentStack - deposited;
                if (newStack <= 0)
                {
                    ClearItem(mouseItem);
                }
                else
                {
                    _itemStackField?.SetValue(mouseItem, newStack);
                }

                _log.Debug($"Deposited {deposited}x {snapshot.Name} from cursor");
                return deposited;
            }
            catch (Exception ex)
            {
                _log.Error($"DepositFromCursor failed: {ex.Message}");
                return 0;
            }
        }

        public int DepositFromInventorySlot(int inventorySlot, bool singleItem)
        {
            try
            {
                if (inventorySlot < 0 || inventorySlot >= 50) return 0;

                var player = GetLocalPlayer();
                if (player == null) return 0;

                var inventory = _playerInventoryField?.GetValue(player) as Array;
                if (inventory == null || inventorySlot >= inventory.Length) return 0;

                var slotItem = inventory.GetValue(inventorySlot);
                if (slotItem == null) return 0;

                var snapshot = CreateSnapshot(slotItem, SourceIndex.PlayerInventory, inventorySlot);
                if (snapshot.IsEmpty) return 0;

                int requested = singleItem ? 1 : snapshot.Stack;
                if (requested <= 0) return 0;

                var toDeposit = new ItemSnapshot(
                    snapshot.ItemId,
                    requested,
                    snapshot.Prefix,
                    snapshot.Name,
                    snapshot.MaxStack,
                    snapshot.Rarity,
                    snapshot.SourceChestIndex,
                    snapshot.SourceSlot);

                int deposited = DepositItem(toDeposit, out _);
                if (deposited <= 0) return 0;

                int currentStack = GetSafeInt(_itemStackField, slotItem, 0);
                int newStack = currentStack - deposited;
                if (newStack <= 0)
                {
                    ClearItem(slotItem);
                }
                else
                {
                    _itemStackField?.SetValue(slotItem, newStack);
                }

                _log.Debug($"Deposited {deposited}x {snapshot.Name} from inventory slot {inventorySlot}");
                return deposited;
            }
            catch (Exception ex)
            {
                _log.Error($"DepositFromInventorySlot failed: {ex.Message}");
                return 0;
            }
        }

        public int QuickStackInventory(bool includeHotbar, bool includeFavorited = false, List<QuickStackTransfer> transfers = null)
        {
            try
            {
                var player = GetLocalPlayer();
                if (player == null) return 0;

                var inventory = _playerInventoryField?.GetValue(player) as Array;
                if (inventory == null) return 0;

                // Match vanilla/Magic Storage semantics: quick stack only into item types
                // that already exist in storage.
                HashSet<int> existingTypes;
                if (_useDriveStorage && _driveStorage != null)
                {
                    existingTypes = GetExistingDriveItemTypes(_registry.GetRegisteredPositions());
                }
                else
                {
                    var chests = _chestArrayField?.GetValue(null) as Array;
                    if (chests == null) return 0;
                    existingTypes = GetExistingStorageItemTypes(chests, _registry.GetRegisteredPositions());
                }

                if (existingTypes.Count == 0) return 0;

                int start = includeHotbar ? 0 : 10;
                int end = Math.Min(inventory.Length, 50);
                if (start >= end) return 0;

                int totalDeposited = 0;

                for (int i = start; i < end; i++)
                {
                    var slotItem = inventory.GetValue(i);
                    if (slotItem == null) continue;

                    var snapshot = CreateSnapshot(slotItem, SourceIndex.PlayerInventory, i);
                    if (snapshot.IsEmpty) continue;

                    if (!includeFavorited && GetSafeBool(_itemFavoritedField, slotItem))
                        continue;

                    if (!existingTypes.Contains(snapshot.ItemId))
                        continue;

                    var toDeposit = new ItemSnapshot(
                        snapshot.ItemId,
                        snapshot.Stack,
                        snapshot.Prefix,
                        snapshot.Name,
                        snapshot.MaxStack,
                        snapshot.Rarity,
                        snapshot.SourceChestIndex,
                        snapshot.SourceSlot);

                    int deposited = DepositItem(toDeposit, out _);
                    if (deposited <= 0) continue;

                    int currentStack = GetSafeInt(_itemStackField, slotItem, 0);
                    int newStack = currentStack - deposited;
                    if (newStack <= 0)
                    {
                        ClearItem(slotItem);
                    }
                    else
                    {
                        _itemStackField?.SetValue(slotItem, newStack);
                    }

                    totalDeposited += deposited;
                    transfers?.Add(new QuickStackTransfer(snapshot.ItemId, deposited));
                }

                if (totalDeposited > 0)
                    _log.Debug($"Quick-stacked {totalDeposited} item(s) from inventory");

                return totalDeposited;
            }
            catch (Exception ex)
            {
                _log.Error($"QuickStackInventory failed: {ex.Message}");
                return 0;
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

<<<<<<< HEAD
        private void ClearItem(object item)
        {
            if (item == null) return;

            try
            {
                if (!InvokeSetDefaults(item, 0))
                {
                    _itemTypeField?.SetValue(item, 0);
                    _itemStackField?.SetValue(item, 0);
                }
                else
                {
                    _itemStackField?.SetValue(item, 0);
                }

                if (_itemPrefixField != null)
                {
                    if (_itemPrefixField.FieldType == typeof(byte))
                        _itemPrefixField.SetValue(item, (byte)0);
                    else
                        _itemPrefixField.SetValue(item, 0);
                }
            }
            catch
            {
                // Best effort clear.
            }
        }

        private HashSet<int> GetExistingStorageItemTypes(Array chests, IEnumerable<(int x, int y)> positions)
        {
            var types = new HashSet<int>();
            if (chests == null || positions == null)
                return types;

            foreach (var pos in positions)
            {
                int chestIndex = FindChestAtPosition(chests, pos.x, pos.y);
                if (chestIndex < 0) continue;

                var chest = chests.GetValue(chestIndex);
                if (chest == null) continue;

                var itemArray = _chestItemField?.GetValue(chest) as Array;
                if (itemArray == null) continue;

                for (int i = 0; i < itemArray.Length; i++)
                {
                    var chestItem = itemArray.GetValue(i);
                    if (chestItem == null) continue;

                    int type = GetSafeInt(_itemTypeField, chestItem, 0);
                    int stack = GetSafeInt(_itemStackField, chestItem, 0);
                    if (type > 0 && stack > 0)
                        types.Add(type);
                }
            }

            return types;
        }

        private readonly struct DriveDiskRef
        {
            public readonly int SourceIndex;
            public readonly DiskRecord Disk;

            public DriveDiskRef(int sourceIndex, DiskRecord disk)
            {
                SourceIndex = sourceIndex;
                Disk = disk;
            }
        }

        private HashSet<int> GetExistingDriveItemTypes(IEnumerable<(int x, int y)> positions)
        {
            var types = new HashSet<int>();
            if (_driveStorage == null || positions == null)
                return types;

            var chests = _chestArrayField?.GetValue(null) as Array;
            if (chests == null)
                return types;

            foreach (var pos in positions)
            {
                if (!TryGetDriveDiskSlots(chests, pos.x, pos.y, out _, out var diskSlots))
                    continue;

                int slotLimit = GetDriveDiskSlotLimit(diskSlots);
                for (int diskSlot = 0; diskSlot < slotLimit; diskSlot++)
                {
                    var diskItem = diskSlots.GetValue(diskSlot);
                    if (!TryGetDiskIdentity(diskItem, assignIfMissing: true, out int diskItemType, out int diskUid))
                        continue;

                    var disk = _driveStorage.EnsureDisk(diskItemType, diskUid);
                    if (disk == null)
                        continue;

                    for (int i = 0; i < disk.Items.Count; i++)
                    {
                        var entry = disk.Items[i];
                        if (entry.ItemId > 0 && entry.Stack > 0)
                            types.Add(entry.ItemId);
                    }
                }
            }

            return types;
        }

        private int DepositItemToDrives(ItemSnapshot item, out int depositedToChest)
        {
            depositedToChest = -1;

            if (_driveStorage == null || item.ItemId <= 0 || item.Stack <= 0)
                return 0;

            var chests = _chestArrayField?.GetValue(null) as Array;
            if (chests == null)
                return 0;

            int remaining = item.Stack;
            int maxStack = item.MaxStack > 0 ? item.MaxStack : GetItemMaxStack(item.ItemId);
            if (maxStack <= 0)
                maxStack = 9999;

            var disks = new List<DriveDiskRef>();
            foreach (var pos in _registry.GetRegisteredPositions())
            {
                if (!TryGetDriveDiskSlots(chests, pos.x, pos.y, out _, out var diskSlots))
                    continue;

                int sourceIndex = BuildDriveSourceIndex(pos.x, pos.y);
                int slotLimit = GetDriveDiskSlotLimit(diskSlots);
                for (int diskSlot = 0; diskSlot < slotLimit; diskSlot++)
                {
                    var diskItem = diskSlots.GetValue(diskSlot);
                    if (!TryGetDiskIdentity(diskItem, assignIfMissing: true, out int diskItemType, out int diskUid))
                        continue;

                    var disk = _driveStorage.EnsureDisk(diskItemType, diskUid);
                    if (disk == null || disk.Capacity <= 0)
                        continue;

                    disks.Add(new DriveDiskRef(sourceIndex, disk));
                }
            }

            if (disks.Count == 0)
                return 0;

            // First pass: stack into existing matching entries.
            for (int d = 0; d < disks.Count && remaining > 0; d++)
            {
                var diskRef = disks[d];
                var disk = diskRef.Disk;
                for (int i = 0; i < disk.Items.Count && remaining > 0; i++)
                {
                    var existing = disk.Items[i];
                    if (existing.ItemId != item.ItemId || existing.Prefix != item.Prefix)
                        continue;
                    if (existing.Stack >= maxStack)
                        continue;

                    int canAdd = maxStack - existing.Stack;
                    int toAdd = Math.Min(canAdd, remaining);
                    disk.Items[i] = new DriveItemRecord(existing.ItemId, existing.Prefix, existing.Stack + toAdd);
                    remaining -= toAdd;
                    depositedToChest = diskRef.SourceIndex;
                }
            }

            // Second pass: fill empty stack slots on disks.
            for (int d = 0; d < disks.Count && remaining > 0; d++)
            {
                var diskRef = disks[d];
                var disk = diskRef.Disk;
                while (remaining > 0 && disk.Items.Count < disk.Capacity)
                {
                    int toAdd = Math.Min(maxStack, remaining);
                    disk.Items.Add(new DriveItemRecord(item.ItemId, item.Prefix, toAdd));
                    remaining -= toAdd;
                    depositedToChest = diskRef.SourceIndex;
                }
            }

            int deposited = item.Stack - remaining;
            if (deposited > 0)
                _driveStorage.MarkDirty();

            if (deposited <= 0)
                _log.Warn($"No drive space to deposit {item.Name}");
            else if (remaining > 0)
                _log.Warn($"Partial drive deposit: {deposited}x {item.Name} stored, {remaining}x remaining");

            return deposited;
        }

        private int AddDriveItems(Array chests, int driveX, int driveY, int sourceIndex, List<ItemSnapshot> items)
        {
            if (items == null || _driveStorage == null)
                return 0;

            if (!TryGetDriveDiskSlots(chests, driveX, driveY, out _, out var diskSlots))
                return 0;

            int added = 0;
            int slotLimit = GetDriveDiskSlotLimit(diskSlots);
            for (int diskSlot = 0; diskSlot < slotLimit; diskSlot++)
            {
                var diskItem = diskSlots.GetValue(diskSlot);
                if (!TryGetDiskIdentity(diskItem, assignIfMissing: true, out int diskItemType, out int diskUid))
                    continue;

                var disk = _driveStorage.EnsureDisk(diskItemType, diskUid);
                if (disk == null || disk.Capacity <= 0)
                    continue;

                int entryLimit = disk.Items.Count;
                for (int itemIndex = 0; itemIndex < entryLimit; itemIndex++)
                {
                    var entry = disk.Items[itemIndex];
                    if (entry.ItemId <= 0 || entry.Stack <= 0)
                        continue;

                    int encodedSourceSlot = EncodeDriveSourceSlot(diskSlot, itemIndex);
                    var snapshot = CreateDriveSnapshot(entry.ItemId, entry.Stack, entry.Prefix, sourceIndex, encodedSourceSlot);
                    if (snapshot.IsEmpty)
                        continue;

                    items.Add(snapshot);
                    added++;
                }
            }

            return added;
        }

        private ItemSnapshot CreateDriveSnapshot(int itemId, int stack, int prefix, int sourceChestIndex, int sourceSlot)
        {
            try
            {
                if (_itemType == null || _itemStackField == null)
                {
                    return new ItemSnapshot(itemId, stack, prefix, $"Item {itemId}", 9999, 0, sourceChestIndex, sourceSlot);
                }

                object item = Activator.CreateInstance(_itemType);
                if (item == null || !InvokeSetDefaults(item, itemId))
                    return new ItemSnapshot(itemId, stack, prefix, $"Item {itemId}", 9999, 0, sourceChestIndex, sourceSlot);

                _itemStackField.SetValue(item, stack);
                ApplyPrefix(item, prefix);

                var snapshot = CreateSnapshot(item, sourceChestIndex, sourceSlot);
                if (!snapshot.IsEmpty)
                    return snapshot;
            }
            catch
            {
                // Fallback below.
            }

            return new ItemSnapshot(itemId, stack, prefix, $"Item {itemId}", 9999, 0, sourceChestIndex, sourceSlot);
        }

        private int GetItemMaxStack(int itemId)
        {
            try
            {
                if (itemId <= 0 || _itemType == null || _itemMaxStackField == null)
                    return 9999;

                object item = Activator.CreateInstance(_itemType);
                if (item == null || !InvokeSetDefaults(item, itemId))
                    return 9999;

                return GetSafeInt(_itemMaxStackField, item, 9999);
            }
            catch
            {
                return 9999;
            }
        }

        private bool IsDriveSourceIndex(int sourceIndex)
        {
            return SourceIndex.IsStorageDrive(sourceIndex);
        }

        private int BuildDriveSourceIndex(int x, int y)
        {
            return SourceIndex.BuildStorageDriveIndex(x, y);
        }

        private bool TryDecodeDriveSourceIndex(int sourceIndex, out int x, out int y)
        {
            return SourceIndex.TryDecodeStorageDriveIndex(sourceIndex, out x, out y);
        }

        private static int EncodeDriveSourceSlot(int diskSlot, int diskItemSlot)
        {
            return diskSlot * DriveSourceSlotStride + diskItemSlot;
        }

        private static bool TryDecodeDriveSourceSlot(int sourceSlot, out int diskSlot, out int diskItemSlot)
        {
            diskSlot = 0;
            diskItemSlot = 0;

            if (sourceSlot < 0)
                return false;

            diskSlot = sourceSlot / DriveSourceSlotStride;
            diskItemSlot = sourceSlot % DriveSourceSlotStride;
            return diskSlot >= 0 && diskItemSlot >= 0;
        }

        private bool TryGetDriveDiskSlots(Array chests, int driveX, int driveY, out int chestIndex, out Array diskSlots)
        {
            chestIndex = -1;
            diskSlots = null;

            if (chests == null)
                return false;

            chestIndex = FindChestAtPosition(chests, driveX, driveY);
            if (chestIndex < 0 || chestIndex >= chests.Length)
                return false;

            var chest = chests.GetValue(chestIndex);
            if (chest == null)
                return false;

            diskSlots = _chestItemField?.GetValue(chest) as Array;
            return diskSlots != null;
        }

        private static int GetDriveDiskSlotLimit(Array diskSlots)
        {
            if (diskSlots == null)
                return 0;

            return Math.Min(MaxDriveDiskSlots, diskSlots.Length);
        }

        private bool TryGetDiskIdentity(object diskItem, bool assignIfMissing, out int diskItemType, out int diskUid)
        {
            diskItemType = 0;
            diskUid = 0;

            if (diskItem == null || _itemTypeField == null || _itemStackField == null || _itemPrefixField == null)
                return false;

            diskItemType = GetSafeInt(_itemTypeField, diskItem, 0);
            int stack = GetSafeInt(_itemStackField, diskItem, 0);
            if (diskItemType <= 0 || stack <= 0)
                return false;

            if (!DedicatedBlocksManager.TryGetDiskTierForItemType(diskItemType, out _))
                return false;

            diskUid = GetSafeInt(_itemPrefixField, diskItem, 0);
            if (diskUid > 0)
                return true;

            if (!assignIfMissing || _driveStorage == null)
                return false;

            int allocatedUid = _driveStorage.AllocateDiskUid(diskItemType);
            if (allocatedUid <= 0)
            {
                _log.Warn($"No free disk UIDs available for disk item type {diskItemType}");
                return false;
            }

            SetItemPrefix(diskItem, allocatedUid);
            _driveStorage.EnsureDisk(diskItemType, allocatedUid);
            _driveStorage.MarkDirty();
            diskUid = allocatedUid;
            return true;
        }

        private void SetItemPrefix(object item, int prefix)
        {
            if (item == null || _itemPrefixField == null)
                return;

            int clamped = Math.Max(0, Math.Min(byte.MaxValue, prefix));
            try
            {
                if (_itemPrefixField.FieldType == typeof(byte))
                    _itemPrefixField.SetValue(item, (byte)clamped);
                else
                    _itemPrefixField.SetValue(item, clamped);
            }
            catch
            {
                // Best effort only.
            }
        }

        private object GetLocalPlayer()
=======
        private Player GetLocalPlayer()
>>>>>>> inidar-main
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

<<<<<<< HEAD
                string name = _itemNameProp?.GetValue(item)?.ToString() ?? "";

                var maxStackVal = _itemMaxStackField.GetValue(item);
                int maxStack = maxStackVal != null ? (int)maxStackVal : 999;

                var rarityVal = _itemRarityField.GetValue(item);
                int rarity = rarityVal != null ? (int)rarityVal : 0;

                // Get category info with safe casts
                int damage = GetSafeInt(_itemDamageField, item, 0);
                int pick = GetSafeInt(_itemPickField, item, 0);
                int axe = GetSafeInt(_itemAxeField, item, 0);
                int hammer = GetSafeInt(_itemHammerField, item, 0);
                int headSlot = GetSafeInt(_itemHeadSlotField, item, -1);
                int bodySlot = GetSafeInt(_itemBodySlotField, item, -1);
                int legSlot = GetSafeInt(_itemLegSlotField, item, -1);
                bool accessory = GetSafeBool(_itemAccessoryField, item);
                bool consumable = GetSafeBool(_itemConsumableField, item);
                int createTile = GetSafeInt(_itemCreateTileField, item, -1);
                int createWall = GetSafeInt(_itemCreateWallField, item, -1);
                bool material = GetSafeBool(_itemMaterialField, item);
                bool vanity = GetSafeBool(_itemVanityField, item);
                int ammo = GetSafeInt(_itemAmmoField, item, 0);
                bool notAmmo = GetSafeBool(_itemNotAmmoField, item);
                bool melee = GetSafeBool(_itemMeleeField, item);
                bool ranged = GetSafeBool(_itemRangedField, item);
                bool magic = GetSafeBool(_itemMagicField, item);
                bool summon = GetSafeBool(_itemSummonField, item);
                bool thrown = GetSafeBool(_itemThrownField, item);
                bool sentry = GetSafeBool(_itemSentryField, item);
                int shoot = GetSafeInt(_itemShootField, item, 0);
                int healLife = GetSafeInt(_itemHealLifeField, item, 0);
                int healMana = GetSafeInt(_itemHealManaField, item, 0);
                bool potion = GetSafeBool(_itemPotionField, item);
                int dye = GetSafeInt(_itemDyeField, item, 0);
                int hairDye = GetSafeInt(_itemHairDyeField, item, -1);
                int mountType = GetSafeInt(_itemMountTypeField, item, -1);
                int buffType = GetSafeInt(_itemBuffTypeField, item, 0);
                int fishingPole = GetSafeInt(_itemFishingPoleField, item, 0);
                int bait = GetSafeInt(_itemBaitField, item, 0);

                bool isTool = pick > 0 || axe > 0 || hammer > 0;
                bool isWeapon = damage > 0 && !isTool && ammo == 0;
                bool isMelee = melee && isWeapon;
                bool isRanged = ranged && isWeapon;
                bool isMagic = magic && isWeapon;
                bool isSummon = (summon || sentry) && isWeapon;
                bool isThrown = thrown && damage > 0 && (ammo == 0 || notAmmo) && shoot > 0;
                if (!isMelee && !isRanged && !isMagic && !isSummon && !isThrown && isWeapon)
                    isMelee = true; // Fallback for versions where class flags are unavailable

                bool isAmmo = ammo > 0 && damage > 0;
                bool isPlaceable = createTile >= 0 || createWall >= 0;
                bool isVanity = vanity || dye > 0 || hairDye >= 0;
                bool isPotion = consumable && (healLife > 0 || healMana > 0 || buffType > 0 || potion);
                bool isFishing = fishingPole > 0 || bait > 0;
                bool isEquipment = accessory || mountType >= 0 || buffType > 0 || isFishing;
                bool isMaterial = material && !isPlaceable && !isWeapon && !isTool && !accessory && !isVanity;
=======
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
>>>>>>> inidar-main

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
                    pickPower: pick,
                    isAxe: axe > 0,
                    axePower: axe,
                    isHammer: hammer > 0,
                    hammerPower: hammer,
                    isArmor: !vanity && (headSlot >= 0 || bodySlot >= 0 || legSlot >= 0),
                    isAccessory: accessory,
                    isConsumable: consumable,
                    isPlaceable: isPlaceable,
                    isMaterial: isMaterial,
                    isVanity: isVanity,
                    isAmmo: isAmmo,
                    ammoType: ammo,
                    isPotion: isPotion,
                    healLife: healLife,
                    healMana: healMana,
                    isFishing: isFishing,
                    fishingPolePower: fishingPole,
                    fishingBaitPower: bait,
                    isEquipment: isEquipment,
                    isMelee: isMelee,
                    isRanged: isRanged,
                    isMagic: isMagic,
                    isSummon: isSummon,
                    isThrown: isThrown
                );
            }
            catch (Exception ex)
            {
                _log.Error($"Error creating snapshot: {ex.Message}");
                return default;
            }
        }
<<<<<<< HEAD

        /// <summary>
        /// Safely get an int value from a field, returning default if null or failed.
        /// </summary>
        private int GetSafeInt(FieldInfo field, object obj, int defaultValue)
        {
            if (field == null) return defaultValue;
            try
            {
                var val = field.GetValue(obj);
                if (val == null) return defaultValue;
                if (val is int i) return i;
                return Convert.ToInt32(val);
            }
            catch
            {
                return defaultValue;
            }
        }

        /// <summary>
        /// Safely get a bool value from a field, returning false if null or failed.
        /// </summary>
        private bool GetSafeBool(FieldInfo field, object obj)
        {
            if (field == null) return false;
            try
            {
                var val = field.GetValue(obj);
                if (val == null) return false;
                if (val is bool b) return b;
                return Convert.ToBoolean(val);
            }
            catch
            {
                return false;
            }
        }
=======
>>>>>>> inidar-main
    }
}
