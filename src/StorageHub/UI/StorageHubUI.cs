using System;
using System.Collections.Generic;
using TerrariaModder.Core.UI;
using TerrariaModder.Core.Logging;
using TerrariaModder.Core.Input;
using StorageHub.Storage;
using StorageHub.Config;
using StorageHub.Crafting;
using StorageHub.UI.Components;
using StorageHub.UI.Tabs;
using StorageHub.Relay;
using TerrariaModder.Core.Config;
using TerrariaModder.Core.UI.Widgets;
using TextUtil = TerrariaModder.Core.UI.Widgets.TextUtil;
using ItemTooltip = TerrariaModder.Core.UI.Widgets.ItemTooltip;

namespace StorageHub.UI
{
    /// <summary>
    /// Main UI container for Storage Hub.
    /// Manages the 6 tabs and coordinates between components.
    ///
    /// Design principles:
    /// - All item access through IStorageProvider (snapshot-based, safe)
    /// - Tabs are separate views with focused purposes
    /// - Lazy refresh (only when needed, not every frame)
    /// </summary>
    public class StorageHubUI
    {
        private readonly ILogger _log;
        private readonly IStorageProvider _storage;
        private readonly StorageHubConfig _config;
        private readonly RecipeIndex _recipeIndex;
        private readonly CraftabilityChecker _craftChecker;
        private readonly RecursiveCrafter _crafter;
        private readonly RangeCalculator _rangeCalc;
        private readonly ChestPinger _chestPinger;
        private readonly IModConfig _modConfig;

        // Tab indices
        private const int TabItems = 0;
        private const int TabCraft = 1;
        private const int TabRecipes = 2;
        private const int TabShimmer = 3;
        private const int TabInfo = 4;
        private const int TabNetwork = 5;
        private static readonly string[] TabNames = { "Items", "Crafting", "Recipes", "Shimmer", "Unlocks", "Network" };

        // UI State
        private bool _isOpen = false;
        private int _activeTab = TabItems;

        // UI Components
        private readonly TextInput _searchInput = new TextInput("Search / #tag...", 200);
        private readonly ScrollView _scrollView = new ScrollView();
        private readonly ItemSlotGrid _itemGrid = new ItemSlotGrid();

        // Tab components
        private CraftTab _craftTab;
        private RecipesTab _recipesTab;
        private ShimmerTab _shimmerTab;
        private InfoTab _infoTab;
        private NetworkTab _networkTab;

        // Panel dimensions - larger for better visibility
        private const int PanelWidth = 800;
        private const int PanelHeight = 600;
        private const int HeaderHeight = 35;
        private const int TabBarHeight = 30;
        private const int SearchBarHeight = 30;
        private const int ContentPadding = 10;

        // Cached data
        private List<ItemSnapshot> _allItems = new List<ItemSnapshot>();
        private List<ItemSnapshot> _filteredItems = new List<ItemSnapshot>();
        private bool _needsRefresh = true;
        private int _hoveredItemIndex = -1;

        // Periodic refresh to catch external inventory/chest changes
        private int _framesSinceRefresh = 0;
        private const int RefreshIntervalFrames = 30; // ~0.5 sec at 60fps

        // Draggable panel state
        private int _panelX = -1;  // -1 means center on screen
        private int _panelY = -1;
        private bool _isDragging = false;
        private int _dragOffsetX, _dragOffsetY;

        // Ping mode state
        private bool _pingMode = false;

        public bool IsOpen => _isOpen;

        public StorageHubUI(ILogger log, IStorageProvider storage, StorageHubConfig config,
            RecipeIndex recipeIndex, CraftabilityChecker craftChecker, RecursiveCrafter crafter,
            RangeCalculator rangeCalc, IModConfig modConfig)
        {
            _log = log;
            _storage = storage;
            _config = config;
            _recipeIndex = recipeIndex;
            _craftChecker = craftChecker;
            _crafter = crafter;
            _rangeCalc = rangeCalc;
            _modConfig = modConfig;

            // Initialize tab components
            _craftTab = new CraftTab(_log, _recipeIndex, _craftChecker, _crafter, _config, _storage, _modConfig);
            _recipesTab = new RecipesTab(_log, _recipeIndex, _craftChecker, _config);
            _shimmerTab = new ShimmerTab(_log, _storage, _config);
            _infoTab = new InfoTab(_log, _storage, _config, _rangeCalc);
            _chestPinger = new ChestPinger(_log);
            _networkTab = new NetworkTab(_log, _config, _craftChecker, _storage,
                ChestRegistry.Instance, _chestPinger, _rangeCalc);

            // TextInput handles keyboard blocking automatically via KeyBlockId
            _searchInput.KeyBlockId = "storage-hub-search";

            // Set up callbacks
            _recipesTab.OnJumpToCraft = (itemId) =>
            {
                _activeTab = TabCraft;
                _craftTab.NavigateToItem(itemId);
            };

            // Wire up storage modification callbacks so all tabs refresh when storage changes
            _craftTab.OnStorageModified = () => MarkDirty();
            _shimmerTab.OnStorageModified = () => MarkDirty();
            _infoTab.OnStorageModified = () => MarkDirty();
            _networkTab.OnStorageModified = () => MarkDirty();
        }

        /// <summary>
        /// Toggle the UI open/closed.
        /// </summary>
        public void Toggle()
        {
            _isOpen = !_isOpen;

            if (_isOpen)
            {
                _needsRefresh = true;
                _framesSinceRefresh = 0;  // Fresh start on open
                MarkDirty();  // Propagate to all tabs immediately (stations, materials, etc.)
                _searchInput.Clear();
                _scrollView.ResetScroll();

                // Register panel bounds - this automatically enables mouse blocking
                int x = _panelX >= 0 ? _panelX : (UIRenderer.ScreenWidth - PanelWidth) / 2;
                int y = _panelY >= 0 ? _panelY : (UIRenderer.ScreenHeight - PanelHeight) / 2;
                UIRenderer.RegisterPanelBounds("storage-hub", x, y, PanelWidth, PanelHeight);

                // Open inventory when storage hub opens
                UIRenderer.OpenInventory();
            }
            else
            {
                _searchInput.Unfocus();
                // Unregister panel bounds - this automatically disables mouse blocking if no other panels
                UIRenderer.UnregisterPanelBounds("storage-hub");
            }
        }

        /// <summary>
        /// Open storage UI on the Items tab.
        /// If already open, switches tab and refreshes.
        /// </summary>
        public void OpenItems()
        {
            OpenAtTab(TabItems);
        }

        /// <summary>
        /// Open storage UI on the Crafting tab.
        /// If already open, switches tab and refreshes.
        /// </summary>
        public void OpenCrafting()
        {
            OpenAtTab(TabCraft);
        }

        private void OpenAtTab(int tab)
        {
            _activeTab = Math.Max(0, Math.Min(TabNames.Length - 1, tab));

            if (!_isOpen)
            {
                Toggle();
                return;
            }

            _needsRefresh = true;
            _framesSinceRefresh = 0;
            MarkDirty();
        }

        /// <summary>
        /// Close the UI via Escape key. Also closes inventory.
        /// </summary>
        public void CloseWithEscape()
        {
            if (_isOpen)
            {
                _isOpen = false;
                _searchInput.Unfocus();
                UIRenderer.UnregisterPanelBounds("storage-hub");
                UIRenderer.CloseInventory();
            }
        }

        /// <summary>
        /// Close the UI.
        /// </summary>
        public void Close()
        {
            if (_isOpen)
            {
                _isOpen = false;
                _searchInput.Unfocus();
                UIRenderer.UnregisterPanelBounds("storage-hub");
            }
        }

        /// <summary>
        /// Called during Update phase for input handling.
        /// </summary>
        public void Update()
        {
            // Always update chest pinger (even when UI closed, for ongoing visual effect)
            _chestPinger.Update();

            if (!_isOpen) return;

            // Periodic refresh to catch external inventory/chest changes
            _framesSinceRefresh++;
            if (_framesSinceRefresh >= RefreshIntervalFrames)
            {
                MarkDirty();  // Propagates to all tabs
                _framesSinceRefresh = 0;
            }

            // Update player position for range calculations
            _rangeCalc.UpdatePlayerPosition();

            // Handle search input during Update
            _searchInput.Update();

            // Handle tab-specific updates
            if (_activeTab == TabCraft)
                _craftTab.Update();
            else if (_activeTab == TabRecipes)
                _recipesTab.Update();
            else if (_activeTab == TabShimmer)
                _shimmerTab.Update();
        }

        /// <summary>
        /// Draw the UI.
        /// </summary>
        public void Draw()
        {
            if (!_isOpen) return;

            // Handle Escape to close storage hub (unless search is focused)
            if (!_searchInput.IsFocused)
            {
                if (InputState.IsKeyJustPressed(KeyCode.Escape))
                {
                    CloseWithEscape();
                    return;
                }
            }

            // Check if a higher-priority panel (e.g., ModMenu) should handle input instead
            bool blockInput = WidgetInput.ShouldBlockForHigherPriorityPanel("storage-hub");

            // Set the global block flag so ALL sub-components (TabBar, ItemSlotGrid, SearchBar, etc.)
            // automatically skip input when a higher-z panel overlaps us.
            WidgetInput.BlockInput = blockInput;

            try
            {
                // Refresh data if needed
                if (_needsRefresh)
                {
                    RefreshData();
                    _needsRefresh = false;
                }

                // Initialize panel position to center if not set
                if (_panelX < 0) _panelX = (UIRenderer.ScreenWidth - PanelWidth) / 2;
                if (_panelY < 0) _panelY = (UIRenderer.ScreenHeight - PanelHeight) / 2;

                // Handle dragging
                HandleDragging();

                // Clamp to screen bounds
                _panelX = Math.Max(0, Math.Min(_panelX, UIRenderer.ScreenWidth - PanelWidth));
                _panelY = Math.Max(0, Math.Min(_panelY, UIRenderer.ScreenHeight - PanelHeight));

                int x = _panelX;
                int y = _panelY;

                // Register panel bounds for click-through prevention (persists until UI closes)
                UIRenderer.RegisterPanelBounds("storage-hub", x, y, PanelWidth, PanelHeight);

                // Draw main panel
                DrawMainPanel(x, y);

                // Draw tooltip for hovered item
                if (_hoveredItemIndex >= 0 && _hoveredItemIndex < _filteredItems.Count)
                {
                    var hovered = _filteredItems[_hoveredItemIndex];
                    string source = hovered.SourceChestIndex >= 0
                        ? $"In: Chest #{hovered.SourceChestIndex}"
                        : SourceIndex.GetSourceName(hovered.SourceChestIndex);
                    ItemTooltip.Set(hovered.ItemId, hovered.Prefix, hovered.Stack, source);
                    ItemTooltip.DrawDeferred();
                }

                // Consume ALL clicks over our panel area to prevent click-through to inventory
                // This catches any clicks that weren't handled by specific UI elements
                // Only consume if we're not blocked by a higher-priority panel
                if (WidgetInput.IsMouseOver(x, y, PanelWidth, PanelHeight) && !blockInput)
                {
                    if (WidgetInput.MouseLeftClick)
                        WidgetInput.ConsumeClick();
                    if (WidgetInput.MouseRightClick)
                        WidgetInput.ConsumeRightClick();
                    if (WidgetInput.MouseMiddleClick)
                        WidgetInput.ConsumeMiddleClick();
                    if (WidgetInput.ScrollWheel != 0)
                        WidgetInput.ConsumeScroll();
                }
            }
            catch (Exception ex)
            {
                _log.Error($"StorageHubUI Draw error: {ex.Message}");
            }
            finally
            {
                // Always clear the block flag so other code isn't affected
                WidgetInput.BlockInput = false;
            }
        }

        private void HandleDragging()
        {
            // Check if a higher-priority panel should block dragging
            bool blockInput = WidgetInput.ShouldBlockForHigherPriorityPanel("storage-hub");

            // Check if clicking on header area (drag handle) - exclude close button area (30px + padding)
            bool inHeader = WidgetInput.IsMouseOver(_panelX, _panelY, PanelWidth - 40, HeaderHeight) && !blockInput;

            if (WidgetInput.MouseLeftClick && inHeader && !_isDragging)
            {
                // Start dragging
                _isDragging = true;
                _dragOffsetX = WidgetInput.MouseX - _panelX;
                _dragOffsetY = WidgetInput.MouseY - _panelY;
            }

            if (_isDragging)
            {
                if (WidgetInput.MouseLeft)
                {
                    // Update position while dragging
                    _panelX = WidgetInput.MouseX - _dragOffsetX;
                    _panelY = WidgetInput.MouseY - _dragOffsetY;
                }
                else
                {
                    // Stop dragging when mouse released
                    _isDragging = false;
                }
            }
        }

        private void DrawMainPanel(int x, int y)
        {
            // Background
            UIRenderer.DrawPanel(x, y, PanelWidth, PanelHeight, UIColors.PanelBg);

            // Header
            DrawHeader(x, y);

            // Tab bar
            int tabY = y + HeaderHeight;
            var newTab = TabBar.Draw(x, tabY, PanelWidth, TabNames, _activeTab);
            if (newTab != _activeTab)
            {
                _activeTab = newTab;
                _needsRefresh = true;
                // Don't reset scroll - preserve scroll position per tab
                // Each tab maintains its own scroll state internally
            }

            // Content area
            int contentY = tabY + TabBarHeight + 5;
            int contentHeight = PanelHeight - HeaderHeight - TabBarHeight - ContentPadding - 5;

            // Draw active tab content
            int cx = x + ContentPadding;
            int cw = PanelWidth - ContentPadding * 2;
            switch (_activeTab)
            {
                case TabItems:
                    DrawItemsTab(cx, contentY, cw, contentHeight);
                    break;
                case TabCraft:
                    _craftTab.Draw(cx, contentY, cw, contentHeight);
                    break;
                case TabRecipes:
                    _recipesTab.Draw(cx, contentY, cw, contentHeight);
                    break;
                case TabShimmer:
                    _shimmerTab.Draw(cx, contentY, cw, contentHeight);
                    break;
                case TabInfo:
                    _infoTab.Draw(cx, contentY, cw, contentHeight);
                    break;
                case TabNetwork:
                    _networkTab.Draw(cx, contentY, cw, contentHeight);
                    break;
            }
        }

        private void DrawHeader(int x, int y)
        {
            UIRenderer.DrawRect(x, y, PanelWidth, HeaderHeight, UIColors.HeaderBg);

            // Draw mod icon
            TerrariaModder.Core.PluginLoader.LoadModIcons();
            var icon = TerrariaModder.Core.PluginLoader.GetMod("storage-hub")?.IconTexture ?? TerrariaModder.Core.PluginLoader.DefaultIcon;
            int titleX = x + 10;
            if (icon != null)
            {
                UIRenderer.DrawTexture(icon, x + 8, y + 6, 22, 22);
                titleX = x + 34;
            }
            UIRenderer.DrawTextShadow("Storage Hub", titleX, y + 8, UIColors.TextTitle);

            // Header stats — chained with measured widths
            int statX = titleX + TextUtil.MeasureWidth("Storage Hub") + 15;

            // Item count (chest storage only, exclude inventory/bank)
            int storageCount = 0;
            foreach (var item in _allItems)
            {
                if (item.SourceChestIndex >= 0 && !item.IsEmpty) storageCount++;
            }
            string itemsText = $"{storageCount} items";
            UIRenderer.DrawText(itemsText, statX, y + 10, UIColors.TextHint);
            statX += TextUtil.MeasureWidth(itemsText) + 20;

            // Craftable count
            int craftable = _craftTab.CraftableCount;
            if (craftable > 0)
            {
                string craftText = $"{craftable} craftable";
                UIRenderer.DrawText(craftText, statX, y + 10, UIColors.Success);
                statX += TextUtil.MeasureWidth(craftText) + 20;
            }

            // Tier display
            string tierText = $"Tier {_config.Tier}";
            UIRenderer.DrawText(tierText, statX, y + 10, UIColors.AccentText);

            // Check if a higher-priority panel should block input
            bool blockInput = WidgetInput.ShouldBlockForHigherPriorityPanel("storage-hub");

            // Close button (30x30 for easier clicking)
            int closeX = x + PanelWidth - 35;
            bool closeHover = WidgetInput.IsMouseOver(closeX, y + 3, 30, 30) && !blockInput;
            UIRenderer.DrawRect(closeX, y + 3, 30, 30, closeHover ? UIColors.CloseBtnHover : UIColors.CloseBtn);
            UIRenderer.DrawText("X", closeX + 11, y + 10, UIColors.Text);

            if (closeHover && WidgetInput.MouseLeftClick)
            {
                Toggle();
                WidgetInput.ConsumeClick();
            }
        }

        // Items tab sort direction
        private bool _itemsSortAscending = true;

        // Deferred tooltip for items tab
        private string _itemsTooltipText;
        private int _itemsTooltipX, _itemsTooltipY;

        private void DrawItemsTab(int x, int y, int width, int height)
        {
            const int RowHeight = 28;
            const int RowGap = 2;
            _itemsTooltipText = null;

            // Row 1: [Search bar] [Ping Mode]
            int pingBtnWidth = 80;
            int searchWidth = width - pingBtnWidth - 5;

            _searchInput.Draw(x, y, searchWidth, 28);
            if (_searchInput.HasChanged)
            {
                FilterItems();
                _scrollView.ResetScroll();
            }

            // Ping mode button (right of search)
            int pingBtnX = x + searchWidth + 5;
            bool pingHover = WidgetInput.IsMouseOver(pingBtnX, y, pingBtnWidth, 25);
            Color4 pingBg = _pingMode
                ? (pingHover ? UIColors.Accent : UIColors.Accent.WithAlpha(180))
                : (pingHover ? UIColors.ButtonHover : UIColors.Button);
            UIRenderer.DrawRect(pingBtnX, y, pingBtnWidth, 25, pingBg);
            UIRenderer.DrawText("Ping", pingBtnX + 22, y + 5, _pingMode ? UIColors.Text : UIColors.TextDim);

            if (pingHover && WidgetInput.MouseLeftClick)
            {
                _pingMode = !_pingMode;
                WidgetInput.ConsumeClick();
            }

            // Row 2: Filter buttons
            int row2Y = y + RowHeight + RowGap;
            DrawItemsFilterRow(x, row2Y, width);

            // Row 3: Sort buttons
            int row3Y = row2Y + RowHeight + RowGap;
            DrawItemsSortRow(x, row3Y, width);

            // Item grid (below 3 rows)
            int gridY = row3Y + RowHeight + 4;
            int gridHeight = height - (gridY - y) - 25; // Leave room for help text

            // Calculate scroll dimensions
            int columns = _itemGrid.CalculateColumns(width);
            int totalHeight = _itemGrid.CalculateTotalHeight(_filteredItems.Count, columns);
            _scrollView.Begin(x, gridY, width, gridHeight, totalHeight);

            // Draw items - pass ping mode to change click behavior
            _hoveredItemIndex = _itemGrid.Draw(
                _filteredItems,
                x, gridY, width, gridHeight,
                _scrollView.ScrollOffset,
                OnItemClick,
                _config.FavoriteItems,
                OnToggleFavorite,
                _pingMode
            );

            _scrollView.End();

            HandleCursorDepositInput(x, gridY, width, gridHeight, pingMode: _pingMode);

            // Help text at bottom - changes based on ping mode
            if (_pingMode)
            {
                UIRenderer.DrawText("PING MODE: Click item to locate storage drive", x, y + height - 18, UIColors.Accent);
            }
            else
            {
                UIRenderer.DrawText("L=Take  R=+1  Shift=Inv  Carry+L/R=Deposit  Mid=Fav", x, y + height - 18, UIColors.TextHint);
            }

            // Deferred tooltip (drawn last, on top)
            if (_itemsTooltipText != null)
            {
                int pad = 8;
                int tw = TextUtil.MeasureWidth(_itemsTooltipText) + pad * 2;
                int th = 24;
                int tx = _itemsTooltipX;
                int ty = _itemsTooltipY;
                if (tx + tw > UIRenderer.ScreenWidth - 4) tx = UIRenderer.ScreenWidth - tw - 4;
                if (tx < 4) tx = 4;
                UIRenderer.DrawRect(tx, ty, tw, th, UIColors.TooltipBg.WithAlpha(245));
                UIRenderer.DrawRectOutline(tx, ty, tw, th, UIColors.Divider, 1);
                UIRenderer.DrawText(_itemsTooltipText, tx + pad, ty + 4, UIColors.TextDim);
            }
        }

        private void DrawItemsFilterRow(int x, int y, int width)
        {
            int btnHeight = 25;
            int labelWidth = 50;

            UIRenderer.DrawText("Filter:", x, y + 5, UIColors.TextDim);
            int xPos = x + labelWidth;

            DrawItemsFilterButton(xPos, y, 36, btnHeight, "All", CategoryFilter.All, UIColors.TextDim, "All Categories"); xPos += 40;
            DrawItemsFilterButton(xPos, y, 50, btnHeight, "Wpns", CategoryFilter.Weapons, UIColors.Error, "Weapons"); xPos += 54;
            DrawItemsFilterButton(xPos, y, 50, btnHeight, "Tools", CategoryFilter.Tools, UIColors.Info, "Tools"); xPos += 54;
            DrawItemsFilterButton(xPos, y, 50, btnHeight, "Armor", CategoryFilter.Armor, UIColors.Accent, "Armor"); xPos += 54;
            DrawItemsFilterButton(xPos, y, 46, btnHeight, "Accs", CategoryFilter.Accessories, UIColors.AccentText, "Accessories"); xPos += 50;
            DrawItemsFilterButton(xPos, y, 46, btnHeight, "Cons", CategoryFilter.Consumables, UIColors.Success, "Consumables"); xPos += 50;
            DrawItemsFilterButton(xPos, y, 50, btnHeight, "Place", CategoryFilter.Placeable, UIColors.Warning, "Placeable"); xPos += 54;
            DrawItemsFilterButton(xPos, y, 50, btnHeight, "Mats", CategoryFilter.Materials, UIColors.TextDim, "Materials"); xPos += 54;
            DrawItemsFilterButton(xPos, y, 50, btnHeight, "Misc", CategoryFilter.Misc, UIColors.TextHint, "Miscellaneous");
        }

        private void DrawItemsFilterButton(int x, int y, int btnWidth, int btnHeight, string text,
            CategoryFilter filter, Color4 indicatorColor, string tooltip)
        {
            bool isActive = _config.ItemCategoryFilter == filter;
            bool isHovered = WidgetInput.IsMouseOver(x, y, btnWidth, btnHeight);

            Color4 bgColor = isActive ? UIColors.Button : (isHovered ? UIColors.ButtonHover : UIColors.InputBg);
            UIRenderer.DrawRect(x, y, btnWidth, btnHeight, bgColor);

            if (isActive)
                UIRenderer.DrawRect(x, y + btnHeight - 2, btnWidth, 2, UIColors.Accent);

            UIRenderer.DrawText(text, x + 5, y + 6, UIColors.TextDim);

            if (isHovered)
            {
                _itemsTooltipText = tooltip;
                _itemsTooltipX = x;
                _itemsTooltipY = y + btnHeight + 5;

                if (WidgetInput.MouseLeftClick)
                {
                    _config.ItemCategoryFilter = filter;
                    FilterItems();
                    _scrollView.ResetScroll();
                    WidgetInput.ConsumeClick();
                }
            }
        }

        private void DrawItemsSortRow(int x, int y, int width)
        {
            int btnHeight = 25;
            int labelWidth = 50;

            UIRenderer.DrawText("Sort:", x, y + 5, UIColors.TextDim);
            int xPos = x + labelWidth;

            DrawItemsSortButton(xPos, y, 80, btnHeight, "Name", SortMode.Name); xPos += 84;
            DrawItemsSortButton(xPos, y, 80, btnHeight, "Stack", SortMode.Stack); xPos += 84;
            DrawItemsSortButton(xPos, y, 80, btnHeight, "Rarity", SortMode.Rarity); xPos += 84;
            DrawItemsSortButton(xPos, y, 80, btnHeight, "Type", SortMode.Type); xPos += 84;
            DrawItemsSortButton(xPos, y, 80, btnHeight, "Recent", SortMode.Recent); xPos += 84;
            DrawItemsSortButton(xPos, y, 80, btnHeight, "Magic", SortMode.MagicDefault);
        }

        private void DrawItemsSortButton(int x, int y, int btnWidth, int btnHeight, string text, SortMode mode)
        {
            bool isActive = _config.ItemSortMode == mode;
            bool isHovered = WidgetInput.IsMouseOver(x, y, btnWidth, btnHeight);

            Color4 bgColor = isActive ? UIColors.Button : (isHovered ? UIColors.ButtonHover : UIColors.InputBg);
            UIRenderer.DrawRect(x, y, btnWidth, btnHeight, bgColor);

            // Active indicator bar at bottom
            if (isActive)
                UIRenderer.DrawRect(x, y + btnHeight - 2, btnWidth, 2, UIColors.Accent);

            UIRenderer.DrawText(text, x + 5, y + 6, UIColors.TextDim);

            // Direction arrow if active
            if (isActive)
            {
                string arrow = _itemsSortAscending ? "\u25B2" : "\u25BC";
                UIRenderer.DrawText(arrow, x + btnWidth - 19, y + 6, UIColors.Accent);
            }

            if (isHovered && WidgetInput.MouseLeftClick)
            {
                if (_config.ItemSortMode == mode)
                    _itemsSortAscending = !_itemsSortAscending;
                else
                {
                    _config.ItemSortMode = mode;
                    _itemsSortAscending = true;
                }
                FilterItems();
                WidgetInput.ConsumeClick();
            }
        }

        private void RefreshData()
        {
            _allItems = _storage.GetAllItems();
            FilterItems();
        }

        private void FilterItems()
        {
            var query = MagicSearchQuery.Parse(_searchInput.Text);
            _filteredItems = new List<ItemSnapshot>();

            foreach (var item in _allItems)
            {
                // Exclude inventory items and bank items - Items tab shows only chest storage
                if (item.SourceChestIndex < 0)
                    continue;

                var traits = ItemSearchTraitsBuilder.FromSnapshot(item);
                bool isFavorite = _config.FavoriteItems.Contains(item.ItemId);

                // Apply text/tag search filter
                if (!query.Matches(item.Name, tag => MagicSearchQuery.MatchesTag(traits, tag, isFavorite)))
                    continue;

                // Apply category filter
                if (_config.ItemCategoryFilter != CategoryFilter.All && traits.PrimaryCategory != _config.ItemCategoryFilter)
                    continue;

                _filteredItems.Add(item);
            }

            // Sort with favorites always first
            int dir = _itemsSortAscending ? 1 : -1;
            _filteredItems.Sort((a, b) =>
            {
                bool aFav = _config.FavoriteItems.Contains(a.ItemId);
                bool bFav = _config.FavoriteItems.Contains(b.ItemId);
                if (aFav != bFav) return bFav.CompareTo(aFav);

                // Apply selected sort mode with direction
                return _config.ItemSortMode switch
                {
                    SortMode.Name => dir * string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase),
                    SortMode.Stack => dir * a.Stack.CompareTo(b.Stack),
                    SortMode.Rarity => dir * a.Rarity.CompareTo(b.Rarity),
                    SortMode.Type => dir * a.ItemId.CompareTo(b.ItemId),
                    SortMode.Recent => 0,
                    SortMode.MagicDefault => dir * MagicSearchQuery.CompareMagicDefault(a, b),
                    _ => dir * string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase)
                };
            });
        }

        private void OnItemClick(ItemSnapshot item, int index, bool isRightClick, bool isShiftHeld, bool isPingMode)
        {
            if (item.IsEmpty) return;

            // Ping mode: left-click pings the chest instead of taking item
            if (isPingMode && !isRightClick)
            {
                if (item.SourceChestIndex >= 0)
                {
                    _chestPinger.PingChest(item.SourceChestIndex);
                }
                return;
            }

            if (!_storage.IsCursorEmpty())
            {
                int deposited = _storage.DepositFromCursor(singleItem: isRightClick);
                if (deposited > 0)
                    MarkDirty();
                return;
            }

            if (isShiftHeld)
            {
                // Shift-click: Move to inventory
                if (_storage.MoveToInventory(item.SourceChestIndex, item.SourceSlot, item.Stack))
                    MarkDirty();
            }
            else if (isRightClick)
            {
                // Right-click: Take 1 and place on cursor
                if (_storage.TakeItemToCursor(item.SourceChestIndex, item.SourceSlot, 1))
                    MarkDirty();
            }
            else
            {
                // Left-click: Take stack and place on cursor
                if (_storage.TakeItemToCursor(item.SourceChestIndex, item.SourceSlot, item.Stack))
                    MarkDirty();
            }
        }

        private void HandleCursorDepositInput(int gridX, int gridY, int gridWidth, int gridHeight, bool pingMode)
        {
            if (pingMode) return;
            if (_storage.IsCursorEmpty()) return;
            if (!WidgetInput.IsMouseOver(gridX, gridY, gridWidth, gridHeight)) return;

            if (WidgetInput.MouseLeftClick)
            {
                int deposited = _storage.DepositFromCursor(singleItem: false);
                if (deposited > 0)
                {
                    MarkDirty();
                }
                WidgetInput.ConsumeClick();
            }
            else if (WidgetInput.MouseRightClick)
            {
                int deposited = _storage.DepositFromCursor(singleItem: true);
                if (deposited > 0)
                {
                    MarkDirty();
                }
                WidgetInput.ConsumeRightClick();
            }
        }

        private void OnToggleFavorite(ItemSnapshot item, int index)
        {
            if (item.IsEmpty) return;

            if (_config.FavoriteItems.Contains(item.ItemId))
                _config.FavoriteItems.Remove(item.ItemId);
            else
                _config.FavoriteItems.Add(item.ItemId);

            // Re-sort to reflect favorite changes
            FilterItems();
        }

        /// <summary>
        /// Mark data as needing refresh.
        /// Call when storage contents may have changed.
        /// Propagates to all child tabs so they refresh on next draw.
        /// </summary>
        public void MarkDirty()
        {
            _needsRefresh = true;
            // Propagate to all tabs that have their own refresh logic
            _craftTab.MarkDirty();
            _recipesTab.MarkDirty();
            _shimmerTab.MarkDirty();
            _infoTab.MarkDirty();
            _networkTab.MarkDirty();
        }
    }
}
