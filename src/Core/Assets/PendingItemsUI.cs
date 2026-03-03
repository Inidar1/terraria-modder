using System;
using System.Collections.Generic;
using System.Linq;
using Terraria;
using TerrariaModder.Core.Events;
using TerrariaModder.Core.Input;
using TerrariaModder.Core.Logging;
using TerrariaModder.Core.UI;

namespace TerrariaModder.Core.Assets
{
    /// <summary>
    /// UI panel for managing pending custom items that couldn't be placed on load.
    /// Opens automatically when pending items exist; can be re-opened via debug command.
    /// Similar to ItemSpawner: click to withdraw to cursor, shift-click to inventory.
    /// </summary>
    internal static class PendingItemsUI
    {
        private static ILogger _log;
        private static bool _initialized;
        private static bool _uiOpen;
        private static bool _autoOpened; // Prevents re-auto-opening after manual close

        // Confirmation dialog state
        private static bool _confirmDeleteAll;
        private static int _confirmTimer; // Frames remaining for confirmation

        // UI constants
        private const int PanelWidth = 440;
        private const int HeaderHeight = 35;
        private const int ItemHeight = 40;
        private const int IconSize = 32;
        private const int IconPadding = 4;
        private const int ButtonBarHeight = 45;
        private const int ScrollBarWidth = 8;
        private const int MaxVisibleItems = 8;
        private const string PanelId = "pending-items";

        // Draggable panel state
        private static int _panelX = -1;
        private static int _panelY = -1;
        private static bool _isDragging;
        private static int _dragOffsetX, _dragOffsetY;
        private static int _scrollOffset;

        public static void Initialize(ILogger logger)
        {
            if (_initialized) return;
            _log = logger;
            FrameEvents.OnUIOverlay += OnDraw;
            GameEvents.OnWorldLoad += OnWorldLoad;
            GameEvents.OnWorldUnload += OnWorldUnload;
            _initialized = true;
        }

        public static bool IsOpen => _uiOpen;

        public static void Open()
        {
            if (PendingItemStore.TotalCount == 0) return;
            _uiOpen = true;
            _scrollOffset = 0;
            _confirmDeleteAll = false;

            int x = _panelX >= 0 ? _panelX : (UIRenderer.ScreenWidth - PanelWidth) / 2;
            int y = _panelY >= 0 ? _panelY : (UIRenderer.ScreenHeight - GetPanelHeight()) / 2;
            UIRenderer.RegisterPanelBounds(PanelId, x, y, PanelWidth, GetPanelHeight());
            UIRenderer.OpenInventory();
        }

        public static void Close()
        {
            _uiOpen = false;
            _confirmDeleteAll = false;
            UIRenderer.UnregisterPanelBounds(PanelId);
        }

        public static void Toggle()
        {
            if (_uiOpen) Close();
            else Open();
        }

        private static void OnWorldLoad()
        {
            _autoOpened = false;
        }

        private static void OnWorldUnload()
        {
            _uiOpen = false;
            _confirmDeleteAll = false;
            _autoOpened = false;
        }

        private static int GetPanelHeight()
        {
            int itemCount = PendingItemStore.TotalCount;
            int listItems = Math.Min(itemCount, MaxVisibleItems);
            // Header + info text + item list + button bar
            return HeaderHeight + 30 + (listItems * ItemHeight) + ButtonBarHeight + 15;
        }

        private static void OnDraw()
        {
            // Auto-open on first frame after world load if pending items exist
            // Must check !Main.gameMenu — LoadPlayer fires during character selection (menu),
            // which can populate PendingItemStore before the player enters a world.
            if (!_autoOpened && !_uiOpen && PendingItemStore.TotalCount > 0 && !Main.gameMenu)
            {
                _autoOpened = true;
                Open();
                _log?.Info($"[PendingItemsUI] Auto-opened with {PendingItemStore.TotalCount} pending items");
            }

            if (!_uiOpen) return;

            // Don't draw during menu screens
            if (Main.gameMenu)
            {
                Close();
                return;
            }

            // Close if all items resolved
            if (PendingItemStore.TotalCount == 0)
            {
                Close();
                return;
            }

            // Escape to close
            if (InputState.IsKeyJustPressed(KeyCode.Escape))
            {
                Close();
                UIRenderer.CloseInventory();
                return;
            }

            bool blockInput = UIRenderer.ShouldBlockForHigherPriorityPanel(PanelId);

            try
            {
                if (_panelX < 0) _panelX = (UIRenderer.ScreenWidth - PanelWidth) / 2;
                if (_panelY < 0) _panelY = (UIRenderer.ScreenHeight - GetPanelHeight()) / 2;

                HandleDragging(blockInput);

                // Clamp to screen
                int panelH = GetPanelHeight();
                _panelX = Math.Max(0, Math.Min(_panelX, UIRenderer.ScreenWidth - PanelWidth));
                _panelY = Math.Max(0, Math.Min(_panelY, UIRenderer.ScreenHeight - panelH));

                int x = _panelX;
                int y = _panelY;

                UIRenderer.RegisterPanelBounds(PanelId, x, y, PanelWidth, panelH);

                // --- Panel background ---
                UIRenderer.DrawPanel(x, y, PanelWidth, panelH, 40, 30, 30, 240);

                // --- Header ---
                UIRenderer.DrawRect(x, y, PanelWidth, HeaderHeight, 80, 50, 50, 255);
                UIRenderer.DrawTextShadow("Pending Items", x + 10, y + 8, 255, 200, 100);
                UIRenderer.DrawText($"{PendingItemStore.TotalCount} item{(PendingItemStore.TotalCount != 1 ? "s" : "")}",
                    x + 160, y + 10, 200, 200, 200);

                // Close button
                int closeX = x + PanelWidth - 30;
                bool closeHover = UIRenderer.IsMouseOver(closeX, y + 5, 25, 25) && !blockInput;
                UIRenderer.DrawRect(closeX, y + 5, 25, 25,
                    closeHover ? (byte)120 : (byte)70,
                    closeHover ? (byte)50 : (byte)40,
                    closeHover ? (byte)50 : (byte)40, 255);
                UIRenderer.DrawText("X", closeX + 8, y + 9, 255, 255, 255);
                if (closeHover && UIRenderer.MouseLeftClick)
                {
                    Close();
                    UIRenderer.ConsumeClick();
                    return;
                }

                // --- Info text ---
                int infoY = y + HeaderHeight + 5;
                UIRenderer.DrawTextSmall("L-Click: withdraw to cursor   Shift+L: to inventory   R-Click: delete",
                    x + 10, infoY + 2, 160, 180, 160);

                // --- Item list ---
                int listY = infoY + 25;
                var allItems = GetAllItems();
                int visibleItems = Math.Min(allItems.Count, MaxVisibleItems);
                int maxScroll = Math.Max(0, allItems.Count - MaxVisibleItems);
                _scrollOffset = Math.Min(_scrollOffset, maxScroll);

                // Handle scroll
                int scroll = UIRenderer.ScrollWheel;
                int listHeight = visibleItems * ItemHeight;
                if (scroll != 0 && !blockInput && UIRenderer.IsMouseOver(x, listY, PanelWidth, listHeight))
                {
                    UIRenderer.ConsumeScroll();
                    _scrollOffset -= scroll / 30;
                    _scrollOffset = Math.Max(0, Math.Min(_scrollOffset, maxScroll));
                }

                bool shiftHeld = InputState.IsShiftDown();
                int itemAreaWidth = PanelWidth - 15 - ScrollBarWidth;

                for (int i = 0; i < visibleItems && i + _scrollOffset < allItems.Count; i++)
                {
                    var entry = allItems[i + _scrollOffset];
                    int itemY = listY + i * ItemHeight;
                    bool isHover = UIRenderer.IsMouseOver(x + 5, itemY, itemAreaWidth, ItemHeight - 2) && !blockInput;

                    // Row background
                    byte bgR = isHover ? (byte)60 : (byte)42;
                    byte bgG = isHover ? (byte)50 : (byte)38;
                    byte bgB = isHover ? (byte)50 : (byte)38;
                    UIRenderer.DrawRect(x + 5, itemY, itemAreaWidth, ItemHeight - 2, bgR, bgG, bgB, 220);

                    // Item icon
                    int iconX = x + 5 + IconPadding;
                    int iconY = itemY + (ItemHeight - 2 - IconSize) / 2;
                    UIRenderer.DrawItem(entry.Item.RuntimeType, iconX, iconY, IconSize, IconSize);

                    // Item name
                    int textX = iconX + IconSize + 8;
                    int textY = itemY + 4;
                    string displayName = GetItemDisplayName(entry.Item);
                    if (displayName.Length > 30) displayName = displayName.Substring(0, 27) + "...";
                    UIRenderer.DrawText(displayName, textX, textY,
                        isHover ? (byte)255 : (byte)200,
                        isHover ? (byte)255 : (byte)200,
                        isHover ? (byte)200 : (byte)200);

                    // Source label (player/world)
                    string sourceLabel = entry.IsWorld ? "chest" : "inventory";
                    UIRenderer.DrawTextSmall(sourceLabel, textX, textY + 18, 130, 130, 150);

                    // Stack info on right
                    if (entry.Item.Stack > 1)
                    {
                        UIRenderer.DrawTextSmall($"x{entry.Item.Stack}", x + itemAreaWidth - 40, textY + 8, 180, 180, 180);
                    }

                    // Hover border
                    if (isHover)
                    {
                        UIRenderer.DrawRectOutline(x + 5, itemY, itemAreaWidth, ItemHeight - 2, 120, 100, 80, 180, 1);

                        if (UIRenderer.MouseLeftClick)
                        {
                            if (shiftHeld)
                                WithdrawToInventory(entry);
                            else
                                WithdrawToCursor(entry);
                            UIRenderer.ConsumeClick();
                        }
                        else if (UIRenderer.MouseRightClick)
                        {
                            DeleteItem(entry);
                            UIRenderer.ConsumeRightClick();
                        }
                    }
                }

                // Scroll indicator
                if (allItems.Count > MaxVisibleItems && maxScroll > 0)
                {
                    int scrollBarHeight = Math.Max(20, (int)((float)MaxVisibleItems / allItems.Count * listHeight));
                    int scrollBarY = listY + (int)((float)_scrollOffset / maxScroll * (listHeight - scrollBarHeight));
                    UIRenderer.DrawRect(x + PanelWidth - ScrollBarWidth - 3, scrollBarY, ScrollBarWidth - 2, scrollBarHeight, 100, 80, 80, 200);
                }

                // --- Button bar ---
                int btnY = listY + listHeight + 8;
                DrawButtons(x, btnY, blockInput);

                // --- Confirmation timer ---
                if (_confirmDeleteAll && _confirmTimer > 0)
                    _confirmTimer--;
                if (_confirmTimer <= 0)
                    _confirmDeleteAll = false;
            }
            catch (Exception ex)
            {
                _log?.Error($"[PendingItemsUI] Draw error: {ex.Message}");
            }
        }

        private static void DrawButtons(int x, int btnY, bool blockInput)
        {
            int btnWidth = 130;
            int btnHeight = 30;
            int spacing = 10;

            // Withdraw All button
            int btn1X = x + 10;
            bool btn1Hover = UIRenderer.IsMouseOver(btn1X, btnY, btnWidth, btnHeight) && !blockInput;
            UIRenderer.DrawRect(btn1X, btnY, btnWidth, btnHeight,
                btn1Hover ? (byte)60 : (byte)40,
                btn1Hover ? (byte)90 : (byte)60,
                btn1Hover ? (byte)60 : (byte)40, 255);
            UIRenderer.DrawRectOutline(btn1X, btnY, btnWidth, btnHeight, 80, 120, 80, 200, 1);
            UIRenderer.DrawText("Withdraw All", btn1X + 12, btnY + 7, 180, 255, 180);

            if (btn1Hover && UIRenderer.MouseLeftClick)
            {
                WithdrawAll();
                UIRenderer.ConsumeClick();
            }

            // Delete All button (with confirmation)
            int btn2X = btn1X + btnWidth + spacing;
            if (!_confirmDeleteAll)
            {
                bool btn2Hover = UIRenderer.IsMouseOver(btn2X, btnY, btnWidth, btnHeight) && !blockInput;
                UIRenderer.DrawRect(btn2X, btnY, btnWidth, btnHeight,
                    btn2Hover ? (byte)90 : (byte)60,
                    btn2Hover ? (byte)50 : (byte)35,
                    btn2Hover ? (byte)50 : (byte)35, 255);
                UIRenderer.DrawRectOutline(btn2X, btnY, btnWidth, btnHeight, 120, 60, 60, 200, 1);
                UIRenderer.DrawText("Delete All", btn2X + 20, btnY + 7, 255, 150, 150);

                if (btn2Hover && UIRenderer.MouseLeftClick)
                {
                    _confirmDeleteAll = true;
                    _confirmTimer = 300; // 5 seconds at 60fps
                    UIRenderer.ConsumeClick();
                }
            }
            else
            {
                int confirmWidth = 160;
                bool confirmHover = UIRenderer.IsMouseOver(btn2X, btnY, confirmWidth, btnHeight) && !blockInput;
                UIRenderer.DrawRect(btn2X, btnY, confirmWidth, btnHeight,
                    confirmHover ? (byte)140 : (byte)100,
                    confirmHover ? (byte)40 : (byte)25,
                    confirmHover ? (byte)40 : (byte)25, 255);
                UIRenderer.DrawRectOutline(btn2X, btnY, confirmWidth, btnHeight, 200, 80, 80, 255, 1);
                UIRenderer.DrawText("Confirm Delete?", btn2X + 8, btnY + 7, 255, 255, 100);

                if (confirmHover && UIRenderer.MouseLeftClick)
                {
                    DeleteAll();
                    _confirmDeleteAll = false;
                    UIRenderer.ConsumeClick();
                }

                // Cancel on right click
                if (confirmHover && UIRenderer.MouseRightClick)
                {
                    _confirmDeleteAll = false;
                    UIRenderer.ConsumeRightClick();
                }
            }
        }

        // ── Item operations ──

        private static void WithdrawToCursor(ItemEntry entry)
        {
            try
            {
                var mouseItem = Main.mouseItem;
                int currentType = mouseItem?.type ?? 0;

                if (currentType == 0)
                {
                    // Cursor empty — create item
                    var newItem = CreateItem(entry.Item.RuntimeType, entry.Item.Stack, entry.Item.Prefix, entry.Item.Favorited);
                    if (newItem != null)
                    {
                        Main.mouseItem = newItem;
                        RemoveEntry(entry);
                    }
                }
                else if (currentType == entry.Item.RuntimeType)
                {
                    // Same type — try to stack
                    int currentStack = mouseItem.stack;
                    int maxStack = mouseItem.maxStack;
                    int canAdd = maxStack - currentStack;
                    if (canAdd > 0)
                    {
                        int toAdd = Math.Min(entry.Item.Stack, canAdd);
                        mouseItem.stack = currentStack + toAdd;
                        if (toAdd >= entry.Item.Stack)
                        {
                            RemoveEntry(entry);
                        }
                        else
                        {
                            entry.Item.Stack -= toAdd;
                        }
                    }
                }
                else
                {
                    // Different item on cursor — try inventory instead
                    WithdrawToInventory(entry);
                }
            }
            catch (Exception ex)
            {
                _log?.Error($"[PendingItemsUI] Withdraw to cursor failed: {ex.Message}");
            }
        }

        private static void WithdrawToInventory(ItemEntry entry)
        {
            try
            {
                var player = Main.player[Main.myPlayer];
                var inventory = player.inventory;

                // Find empty slot in main inventory (0-49)
                for (int i = 0; i < 50 && i < inventory.Length; i++)
                {
                    if (inventory[i] == null || inventory[i].IsAir)
                    {
                        var newItem = CreateItem(entry.Item.RuntimeType, entry.Item.Stack, entry.Item.Prefix, entry.Item.Favorited);
                        if (newItem != null)
                        {
                            inventory[i] = newItem;
                            RemoveEntry(entry);
                        }
                        return;
                    }
                }

                _log?.Warn("[PendingItemsUI] No empty inventory slot for withdrawal");
            }
            catch (Exception ex)
            {
                _log?.Error($"[PendingItemsUI] Withdraw to inventory failed: {ex.Message}");
            }
        }

        private static void WithdrawAll()
        {
            // Withdraw as many as possible to inventory
            var allItems = GetAllItems();
            int withdrawn = 0;

            var player = Main.player[Main.myPlayer];
            var inventory = player.inventory;

            foreach (var entry in allItems.ToList())
            {
                try
                {
                    bool placed = false;
                    for (int i = 0; i < 50 && i < inventory.Length; i++)
                    {
                        if (inventory[i] == null || inventory[i].IsAir)
                        {
                            var newItem = CreateItem(entry.Item.RuntimeType, entry.Item.Stack, entry.Item.Prefix, entry.Item.Favorited);
                            if (newItem != null)
                            {
                                inventory[i] = newItem;
                                RemoveEntry(entry);
                                withdrawn++;
                                placed = true;
                            }
                            break;
                        }
                    }

                    if (!placed) break; // No more room
                }
                catch { break; }
            }

            _log?.Info($"[PendingItemsUI] Withdrew {withdrawn} items to inventory");
        }

        private static void DeleteItem(ItemEntry entry)
        {
            RemoveEntry(entry);
            _log?.Info($"[PendingItemsUI] Deleted pending item: {entry.Item.ItemId} x{entry.Item.Stack}");
        }

        private static void DeleteAll()
        {
            int count = PendingItemStore.TotalCount;
            PendingItemStore.ClearAll();
            _log?.Info($"[PendingItemsUI] Deleted all {count} pending items");
        }

        private static void RemoveEntry(ItemEntry entry)
        {
            if (entry.IsWorld)
                PendingItemStore.RemoveWorldItem(entry.Item);
            else
                PendingItemStore.RemovePlayerItem(entry.Item);
        }

        // ── Helpers ──

        private static Item CreateItem(int runtimeType, int stack, int prefix, bool favorited = false)
        {
            var item = new Item();
            item.SetDefaults(runtimeType);
            item.stack = stack;
            if (prefix > 0)
            {
                item.prefix = (byte)prefix;
                item.Prefix(prefix);
            }
            if (favorited)
                item.favorited = true;
            return item;
        }

        private static string GetItemDisplayName(PendingItemStore.PendingItem item)
        {
            // Try to get name from definition
            var def = ItemRegistry.GetDefinition(item.RuntimeType);
            if (def != null && !string.IsNullOrEmpty(def.DisplayName))
                return def.DisplayName;

            // Fallback to item ID
            return item.ItemId;
        }

        private struct ItemEntry
        {
            public PendingItemStore.PendingItem Item;
            public bool IsWorld;
        }

        private static List<ItemEntry> GetAllItems()
        {
            var list = new List<ItemEntry>();
            foreach (var item in PendingItemStore.PlayerItems)
                list.Add(new ItemEntry { Item = item, IsWorld = false });
            foreach (var item in PendingItemStore.WorldItems)
                list.Add(new ItemEntry { Item = item, IsWorld = true });
            return list;
        }

        private static void HandleDragging(bool blockInput)
        {
            bool inHeader = UIRenderer.IsMouseOver(_panelX, _panelY, PanelWidth - 35, HeaderHeight) && !blockInput;

            if (UIRenderer.MouseLeftClick && inHeader && !_isDragging)
            {
                _isDragging = true;
                _dragOffsetX = UIRenderer.MouseX - _panelX;
                _dragOffsetY = UIRenderer.MouseY - _panelY;
            }

            if (_isDragging)
            {
                if (UIRenderer.MouseLeft)
                {
                    _panelX = UIRenderer.MouseX - _dragOffsetX;
                    _panelY = UIRenderer.MouseY - _dragOffsetY;
                }
                else
                {
                    _isDragging = false;
                }
            }
        }

        public static void Cleanup()
        {
            FrameEvents.OnUIOverlay -= OnDraw;
            GameEvents.OnWorldLoad -= OnWorldLoad;
            GameEvents.OnWorldUnload -= OnWorldUnload;
            _uiOpen = false;
            _initialized = false;
        }
    }
}
