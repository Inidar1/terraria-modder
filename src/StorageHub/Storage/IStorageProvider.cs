using System.Collections.Generic;

namespace StorageHub.Storage
{
    public readonly struct QuickStackTransfer
    {
        public int ItemId { get; }
        public int Stack { get; }

        public QuickStackTransfer(int itemId, int stack)
        {
            ItemId = itemId;
            Stack = stack;
        }
    }

    /// <summary>
    /// Interface for storage access operations.
    ///
    /// CRITICAL DESIGN DECISION - Why this abstraction exists:
    ///
    /// Multiplayer requires fundamentally different item access patterns than singleplayer:
    /// - Singleplayer: Direct array access (Main.chest[i].item[j])
    /// - Multiplayer: Request → Server validates → Server executes → Client confirms
    ///
    /// If we built singleplayer with direct array access everywhere, adding multiplayer means:
    /// 1. Finding every place that touches items
    /// 2. Replacing with async request/response
    /// 3. High chance of missing something → desync bugs
    ///
    /// The Solution: Build the interface from day one. UI calls IStorageProvider.TakeItem()
    /// and it works the same whether it's SingleplayerProvider (direct access) or
    /// MultiplayerProvider (packet-based). The UI never knows the difference.
    ///
    /// We test singleplayer first because:
    /// 1. Faster iteration (no network complexity)
    /// 2. Easier debugging (state is local)
    /// 3. Core features get validated before adding sync complexity
    /// 4. If singleplayer doesn't work, multiplayer won't either
    /// </summary>
    public interface IStorageProvider
    {
        /// <summary>
        /// Scan all accessible storage and return snapshots.
        /// This is a READ-ONLY operation that returns copies, never references.
        /// </summary>
        /// <returns>List of item snapshots from all accessible storage.</returns>
        List<ItemSnapshot> GetAllItems();

        /// <summary>
        /// Get items that are within the specified range of the player.
        /// </summary>
        /// <param name="playerX">Player X position in world coordinates (pixels).</param>
        /// <param name="playerY">Player Y position in world coordinates (pixels).</param>
        /// <param name="range">Maximum range in pixels.</param>
        /// <returns>List of item snapshots from storage within range.</returns>
        List<ItemSnapshot> GetItemsInRange(float playerX, float playerY, int range);

        /// <summary>
        /// Take items from a specific location.
        /// This is a WRITE operation - use with care.
        ///
        /// Singleplayer: "Add before remove" pattern - item exists in both places briefly
        /// Multiplayer: Request → Server validates → Server executes → Confirms
        /// </summary>
        /// <param name="sourceChestIndex">Source chest index (or special value for inventory/banks).</param>
        /// <param name="sourceSlot">Slot within the container.</param>
        /// <param name="count">Number of items to take.</param>
        /// <param name="taken">The items that were actually taken (may be less than requested).</param>
        /// <returns>True if any items were taken.</returns>
        bool TakeItem(int sourceChestIndex, int sourceSlot, int count, out ItemSnapshot taken);

        /// <summary>
        /// Deposit items into storage.
        /// Finds an appropriate chest based on existing contents (like vanilla quick-stack).
        /// </summary>
        /// <param name="item">The item to deposit (from player inventory/cursor).</param>
        /// <param name="depositedToChest">The chest index where items were deposited (-1 if failed).</param>
        /// <returns>Number of items actually deposited (0 = nothing deposited, may be less than item.Stack for partial deposit).</returns>
        int DepositItem(ItemSnapshot item, out int depositedToChest);

        /// <summary>
        /// Move items directly to player inventory.
        /// Used by shift-click operation.
        /// </summary>
        /// <param name="sourceChestIndex">Source chest index.</param>
        /// <param name="sourceSlot">Slot within the container.</param>
        /// <param name="count">Number of items to move.</param>
        /// <returns>True if items were moved to inventory.</returns>
        bool MoveToInventory(int sourceChestIndex, int sourceSlot, int count);

        /// <summary>
        /// Get information about registered chests.
        /// </summary>
        IReadOnlyList<ChestInfo> GetRegisteredChests();

        /// <summary>
        /// Check if a chest is within range of the player.
        /// </summary>
        bool IsChestInRange(int chestIndex, float playerX, float playerY, int range);

        /// <summary>
        /// Refresh the storage view.
        /// Called when chest contents may have changed.
        /// </summary>
        void Refresh();

        /// <summary>
        /// Place an item on the mouse cursor (Main.mouseItem).
        /// Used when taking items from storage to pick them up.
        /// </summary>
        /// <param name="item">The item snapshot to place on cursor.</param>
        /// <returns>True if successfully placed on cursor.</returns>
        bool PlaceOnCursor(ItemSnapshot item);

        /// <summary>
        /// Take item from storage and place directly on cursor.
        /// Combines TakeItem + PlaceOnCursor for atomic operation.
        /// </summary>
        bool TakeItemToCursor(int sourceChestIndex, int sourceSlot, int count);

        /// <summary>
        /// Deposit the item currently on the mouse cursor into storage.
        /// </summary>
        /// <param name="singleItem">If true, only one item is deposited.</param>
        /// <returns>Number of items actually deposited.</returns>
        int DepositFromCursor(bool singleItem);

        /// <summary>
        /// Deposit an item from the player inventory into storage.
        /// </summary>
        /// <param name="inventorySlot">Main inventory slot index (0-49).</param>
        /// <param name="singleItem">If true, only one item is deposited.</param>
        /// <returns>Number of items actually deposited.</returns>
        int DepositFromInventorySlot(int inventorySlot, bool singleItem);

        /// <summary>
        /// Quick stack inventory items into storage.
        /// </summary>
        /// <param name="includeHotbar">If false, slots 0-9 are skipped.</param>
        /// <param name="includeFavorited">If false, favorited inventory items are skipped.</param>
        /// <param name="transfers">Optional transfer details for feedback effects.</param>
        /// <returns>Total number of items deposited into existing item types in storage.</returns>
        int QuickStackInventory(bool includeHotbar, bool includeFavorited = false, List<QuickStackTransfer> transfers = null);

        /// <summary>
        /// Check if cursor is empty.
        /// </summary>
        bool IsCursorEmpty();
    }

    /// <summary>
    /// Information about a registered chest.
    /// </summary>
    public class ChestInfo
    {
        public int ChestIndex { get; set; }
        public int X { get; set; }
        public int Y { get; set; }
        public string Name { get; set; }
        public bool IsInRange { get; set; }
        public int ItemCount { get; set; }

        public ChestInfo(int chestIndex, int x, int y, string name, bool isInRange, int itemCount)
        {
            ChestIndex = chestIndex;
            X = x;
            Y = y;
            Name = name;
            IsInRange = isInRange;
            ItemCount = itemCount;
        }
    }

}
