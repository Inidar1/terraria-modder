using System;
using System.Collections.Generic;
using System.Reflection;
using TerrariaModder.Core.UI;
using TerrariaModder.Core.Logging;
using StorageHub.Crafting;
using StorageHub.Config;
using TerrariaModder.Core.UI.Widgets;

namespace StorageHub.UI.Tabs
{
    /// <summary>
    /// Recipes tab - browse all game recipes.
    ///
    /// Features:
    /// - Shows all items in the game
    /// - Color coded by craftability (green=craftable, yellow=partial, gray=none)
    /// - Click item to see recipes that create it and recipes that use it
    /// - Double-click craftable item to jump to Craft tab
    /// - Visual icon-based layout for recipe details
    /// </summary>
    public class RecipesTab
    {
        private readonly ILogger _log;
        private readonly RecipeIndex _recipeIndex;
        private readonly CraftabilityChecker _checker;
        private readonly StorageHubConfig _config;

        // UI components
        private readonly TextInput _searchBar = new TextInput("Search / #tag...", 200);
        private readonly ScrollView _itemScroll = new ScrollView();
        private readonly ScrollView _createdByScroll = new ScrollView();
        private readonly ScrollView _usedInScroll = new ScrollView();

        // Cached data
        private List<ItemEntry> _allItems = new List<ItemEntry>();
        private List<ItemEntry> _filteredItems = new List<ItemEntry>();
        private bool _needsRefresh = true;

        // Category sort cache from ContentSamples.ItemCreativeSortingId
        private Dictionary<int, int> _itemSortOrder = null;

        // Selection
        private int _selectedItemId = -1;
        private List<RecipeInfo> _recipesCreating = new List<RecipeInfo>();
        private List<RecipeInfo> _recipesUsing = new List<RecipeInfo>();
        private List<CraftabilityResult> _cachedCraftResults = new List<CraftabilityResult>();
        private bool _craftResultsDirty = true;

        // Filter and Sort
        private CategoryFilter _categoryFilter = CategoryFilter.All;
        private RecipeFilter _currentFilter = RecipeFilter.All;
        private RecipeSortMode _currentSort = RecipeSortMode.Craft;
        private bool _sortAscending = true;

        // Item trait cache (loaded once from ContentSamples)
        private Dictionary<int, ItemSearchTraits> _itemTraits;

        // Double-click tracking
        private int _lastClickedItemId = -1;
        private DateTime _lastClickTime = DateTime.MinValue;
        private const int DoubleClickMs = 300;

        // Layout constants
        private const int SlotSize = 44;
        private const int SmallIconSize = 28;      // Was 24 - larger for better visibility
        private const int RecipeRowHeight = 36;    // Was 30 - more breathing room
        private const int UsedInIconSize = 32;
        private const int IconSpacing = 4;         // Spacing between ingredient icons
        private const int HeaderHeight = 118;      // Four rows: search + category + craft filter + sort (28px each + 2px gaps)

        // Deferred tooltip state - tooltips are drawn LAST to render on top
        private string _pendingButtonTooltip = null;
        private int _pendingTooltipX, _pendingTooltipY;

        // Jump to craft callback
        public Action<int> OnJumpToCraft { get; set; }

        public RecipesTab(ILogger log, RecipeIndex recipeIndex, CraftabilityChecker checker, StorageHubConfig config)
        {
            _log = log;
            _recipeIndex = recipeIndex;
            _checker = checker;
            _config = config;
        }

        /// <summary>
        /// Mark data as needing refresh.
        /// </summary>
        public void MarkDirty()
        {
            _needsRefresh = true;
            _craftResultsDirty = true;
        }

        /// <summary>
        /// Handle input during Update phase.
        /// </summary>
        public void Update()
        {
            _searchBar.Update();
        }

        /// <summary>
        /// Draw the recipes tab.
        /// </summary>
        public void Draw(int x, int y, int width, int height)
        {
            // Reset deferred tooltip state at start of each frame
            _pendingButtonTooltip = null;
            ItemTooltip.Clear();

            // Refresh if needed
            if (_needsRefresh)
            {
                RefreshItems();
                _needsRefresh = false;
            }

            // Layout: left panel is 65% for item grid, right 35% for details
            int itemGridWidth = (width - 10) * 65 / 100;
            int detailsWidth = width - itemGridWidth - 10;

            // === Four-row header layout ===
            // Row 1: Search bar
            // Row 2: Category filter
            // Row 3: Craft filter
            // Row 4: Sort buttons
            int rowHeight = 28;
            int row1Y = y;
            int row2Y = y + rowHeight + 2;
            int row3Y = row2Y + rowHeight + 2;
            int row4Y = row3Y + rowHeight + 2;

            // Row 1: Search bar spans full item grid width
            _searchBar.Draw(x, row1Y, itemGridWidth, 28);
            if (_searchBar.HasChanged)
            {
                FilterItems();
                _itemScroll.ResetScroll();
            }

            // Row 2: Category filter
            DrawCategoryFilterRow(x, row2Y);

            // Row 3: Craft filter
            DrawFilterButtons(x, row3Y);

            // Row 4: Sort buttons
            DrawSortButtons(x, row4Y);

            // Item grid (left side - 55%, below header)
            int gridY = y + HeaderHeight + 5;
            int gridHeight = height - HeaderHeight - 5;
            DrawItemGrid(x, gridY, itemGridWidth, gridHeight);

            // Recipe details (right side - 45%)
            int detailsX = x + itemGridWidth + 10;
            DrawRecipeDetails(detailsX, y, detailsWidth, height);

            // LAST: Draw any pending tooltip on top of everything
            DrawPendingTooltip();
        }

        /// <summary>
        /// Draw the pending tooltip (if any) on top of all other elements.
        /// </summary>
        private void DrawPendingTooltip()
        {
            // Full vanilla-style item tooltip (grid items, ingredients, used-in items)
            ItemTooltip.DrawDeferred();

            // Simple text tooltip for filter/sort buttons
            if (_pendingButtonTooltip != null)
            {
                DrawButtonTooltipImpl(_pendingTooltipX, _pendingTooltipY, _pendingButtonTooltip);
            }
        }

        private void DrawItemGrid(int x, int y, int width, int height)
        {
            int columns = width / SlotSize;
            int totalHeight = ((_filteredItems.Count + columns - 1) / columns) * SlotSize;

            _itemScroll.Begin(x, y, width, height, totalHeight);

            // Draw visible items - only items that fit fully within the view
            // This approach avoids relying on GPU scissor clipping
            int visibleRows = height / SlotSize;
            int startRow = _itemScroll.ScrollOffset / SlotSize;
            int startIndex = startRow * columns;

            for (int i = 0; i < visibleRows * columns && startIndex + i < _filteredItems.Count; i++)
            {
                int itemIdx = startIndex + i;
                if (itemIdx < 0 || itemIdx >= _filteredItems.Count) continue;

                int row = i / columns;
                int col = i % columns;

                int slotX = x + col * SlotSize;
                int slotY = y + row * SlotSize;

                var item = _filteredItems[itemIdx];
                bool isSelected = item.ItemId == _selectedItemId;
                bool isInScrollBounds = WidgetInput.IsMouseOver(x, y, width, height);
                bool isHovered = isInScrollBounds && WidgetInput.IsMouseOver(slotX, slotY, SlotSize - 2, SlotSize - 2);

                DrawItemSlot(slotX, slotY, item, isSelected, isHovered);

                // Handle click / double-click
                if (isHovered && WidgetInput.MouseLeftClick)
                {
                    // Check for double-click
                    if (_lastClickedItemId == item.ItemId &&
                        (DateTime.Now - _lastClickTime).TotalMilliseconds < DoubleClickMs)
                    {
                        // Double-click - jump to craft tab if craftable
                        if (item.CraftStatus == ItemCraftStatus.Craftable)
                        {
                            OnJumpToCraft?.Invoke(item.ItemId);
                        }
                        _lastClickedItemId = -1;
                    }
                    else
                    {
                        // Single click - select item
                        SelectItem(item.ItemId);
                        _lastClickedItemId = item.ItemId;
                        _lastClickTime = DateTime.Now;
                    }
                    WidgetInput.ConsumeClick();
                }
            }

            _itemScroll.End();

            // Empty state
            if (_filteredItems.Count == 0)
            {
                UIRenderer.DrawText("No items found", x + 10, y + 20, UIColors.TextHint);
            }
        }

        private void DrawItemSlot(int x, int y, ItemEntry item, bool isSelected, bool isHovered)
        {
            // Background color based on craftability
            Color4 bgColor = isSelected ? UIColors.ItemActiveBg : (isHovered ? UIColors.ItemHoverBg : UIColors.ItemBg);
            UIRenderer.DrawRect(x, y, SlotSize - 2, SlotSize - 2, bgColor.WithAlpha(230));

            // Craftability indicator (left border)
            Color4 indColor;
            switch (item.CraftStatus)
            {
                case ItemCraftStatus.Craftable: indColor = UIColors.Success; break;
                case ItemCraftStatus.Partial: indColor = UIColors.Warning; break;
                case ItemCraftStatus.HasRecipe: indColor = UIColors.Info; break;
                default: indColor = UIColors.TextHint; break;
            }
            UIRenderer.DrawRect(x, y, 3, SlotSize - 2, indColor);

            // Draw item icon (centered, no name text)
            if (item.ItemId > 0)
            {
                UIRenderer.DrawItem(item.ItemId, x + 5, y + 5, SlotSize - 12, SlotSize - 12);
            }

            // Full item tooltip on hover
            if (isHovered)
            {
                string statusExtra;
                switch (item.CraftStatus)
                {
                    case ItemCraftStatus.Craftable: statusExtra = "Craftable"; break;
                    case ItemCraftStatus.Partial: statusExtra = "Missing some materials"; break;
                    case ItemCraftStatus.HasRecipe: statusExtra = "No materials"; break;
                    default: statusExtra = "Cannot craft"; break;
                }
                ItemTooltip.Set(item.ItemId, 0, 1, statusExtra);
                // Clear other tooltip types
                _pendingButtonTooltip = null;
            }
        }

        private void DrawRecipeDetails(int x, int y, int width, int height)
        {
            // Background
            UIRenderer.DrawRect(x, y, width, height, UIColors.PanelBg);

            if (_selectedItemId < 0)
            {
                // Empty state - show helpful message with icon placeholder
                int centerY = y + height / 2 - 30;
                UIRenderer.DrawRect(x + width / 2 - 20, centerY, 40, 40, UIColors.ItemBg);
                UIRenderer.DrawText("?", x + width / 2 - 5, centerY + 12, UIColors.Info);
                UIRenderer.DrawText("Select an item", x + width / 2 - 45, centerY + 50, UIColors.TextHint);
                return;
            }

            var item = _filteredItems.Find(i => i.ItemId == _selectedItemId);
            // ItemEntry is a struct, so Find() returns default (ItemId=0) when not found
            if (item.ItemId <= 0)
            {
                UIRenderer.DrawText("Item not found", x + 10, y + 20, UIColors.Error);
                return;
            }

            int padding = 8;
            int lineY = y + padding;
            int contentWidth = width - padding * 2;

            // === HEADER: Large icon + item name ===
            int headerIconSize = 40;
            UIRenderer.DrawRect(x + padding, lineY, headerIconSize + 4, headerIconSize + 4, UIColors.SectionBg);
            UIRenderer.DrawItem(item.ItemId, x + padding + 2, lineY + 2, headerIconSize, headerIconSize);

            // Tooltip on header icon hover
            if (WidgetInput.IsMouseOver(x + padding, lineY, headerIconSize + 4, headerIconSize + 4))
                ItemTooltip.Set(item.ItemId);

            // Item name next to icon
            int nameX = x + padding + headerIconSize + 10;
            int nameMaxWidth = contentWidth - headerIconSize - 15;
            string displayName = TextUtil.Truncate(item.Name, nameMaxWidth);
            UIRenderer.DrawText(displayName, nameX, lineY + 5, UIColors.AccentText);

            // Craftability status indicator below name
            string statusText;
            Color4 statusColor;
            switch (item.CraftStatus)
            {
                case ItemCraftStatus.Craftable:
                    statusText = "Craftable";
                    statusColor = UIColors.Success;
                    break;
                case ItemCraftStatus.Partial:
                    statusText = "Missing items";
                    statusColor = UIColors.Warning;
                    break;
                case ItemCraftStatus.HasRecipe:
                    statusText = "No materials";
                    statusColor = UIColors.Info;
                    break;
                default:
                    statusText = "Cannot craft";
                    statusColor = UIColors.TextHint;
                    break;
            }
            UIRenderer.DrawText(statusText, nameX, lineY + 22, statusColor);

            lineY += headerIconSize + 12;

            // Calculate space for the two sections
            int buttonHeight = 35;
            int sectionGap = 8;
            int availableHeight = height - lineY + y - buttonHeight - padding;

            // Split available space: 55% for "Created by", 45% for "Used in"
            int createdByHeight = (availableHeight - sectionGap) * 55 / 100;
            int usedInHeight = availableHeight - createdByHeight - sectionGap;

            // === SECTION: Created by (recipes that make this item) ===
            DrawCreatedBySection(x + padding, lineY, contentWidth, createdByHeight);
            lineY += createdByHeight + sectionGap;

            // === SECTION: Used in (recipes that use this item) ===
            DrawUsedInSection(x + padding, lineY, contentWidth, usedInHeight);
            lineY += usedInHeight;

            // === BUTTON: Go to Craft Tab (if craftable) ===
            if (_cachedCraftResults.Count > 0)
            {
                var firstResult = _cachedCraftResults[0];
                if (firstResult.Status == CraftStatus.Craftable)
                {
                    int btnY = y + height - buttonHeight - padding + 5;
                    int btnWidth = width - padding * 2;
                    bool btnHover = WidgetInput.IsMouseOver(x + padding, btnY, btnWidth, 28);

                    UIRenderer.DrawRect(x + padding, btnY, btnWidth, 28,
                        btnHover ? UIColors.Success : UIColors.Success.WithAlpha(160));

                    // Centered button text
                    int craftBtnTextW = TextUtil.MeasureWidth("Go to Craft Tab");
                    int btnTextX = x + padding + (btnWidth - craftBtnTextW) / 2;
                    UIRenderer.DrawText("Go to Craft Tab", btnTextX, btnY + 7, UIColors.Text);

                    if (btnHover && WidgetInput.MouseLeftClick)
                    {
                        OnJumpToCraft?.Invoke(_selectedItemId);
                        WidgetInput.ConsumeClick();
                    }
                }
            }
        }

        /// <summary>
        /// Draw "Created by" section showing recipes that create the selected item.
        /// Each recipe is shown as a row of ingredient icons.
        /// </summary>
        private void DrawCreatedBySection(int x, int y, int width, int height)
        {
            // Refresh cached craft results if dirty
            if (_craftResultsDirty && _recipesCreating.Count > 0)
            {
                _cachedCraftResults.Clear();
                foreach (var recipe in _recipesCreating)
                {
                    _cachedCraftResults.Add(_checker.CanCraft(recipe));
                }
                _craftResultsDirty = false;
            }

            // Section header
            string headerText = $"Created by ({_recipesCreating.Count})";
            UIRenderer.DrawText(headerText, x, y, UIColors.TextDim);
            int contentY = y + 18;
            int contentHeight = height - 18;

            if (_recipesCreating.Count == 0)
            {
                // Empty state with icon
                UIRenderer.DrawRect(x + 4, contentY + 4, 20, 20, UIColors.ItemBg);
                UIRenderer.DrawText("-", x + 11, contentY + 6, UIColors.Info);
                UIRenderer.DrawText("No recipes", x + 30, contentY + 6, UIColors.TextHint);
                return;
            }

            // Calculate scrolling
            int totalHeight = _recipesCreating.Count * RecipeRowHeight;
            _createdByScroll.Begin(x, contentY, width, contentHeight, totalHeight);

            int visibleCount = contentHeight / RecipeRowHeight;
            int startIndex = _createdByScroll.ScrollOffset / RecipeRowHeight;

            for (int i = 0; i < visibleCount + 1 && startIndex + i < _recipesCreating.Count; i++)
            {
                int recipeIdx = startIndex + i;
                int rowY = contentY + i * RecipeRowHeight;

                // Check if row is fully visible
                if (rowY + RecipeRowHeight > contentY + contentHeight)
                    break;

                var recipe = _recipesCreating[recipeIdx];
                var result = recipeIdx < _cachedCraftResults.Count ? _cachedCraftResults[recipeIdx] : _checker.CanCraft(recipe);

                DrawRecipeIngredientRow(x, rowY, width - 10, recipe, result);
            }

            _createdByScroll.End();
        }

        /// <summary>
        /// Draw a single recipe as a row of ingredient icons with craftability indicator.
        /// </summary>
        private void DrawRecipeIngredientRow(int x, int y, int width, RecipeInfo recipe, CraftabilityResult result)
        {
            // Row background
            bool isHovered = WidgetInput.IsMouseOver(x, y, width, RecipeRowHeight - 2);
            UIRenderer.DrawRect(x, y, width, RecipeRowHeight - 2, UIColors.SectionBg.WithAlpha(isHovered ? (byte)180 : (byte)140));

            // Craftability indicator bar on left
            Color4 indColor;
            switch (result.Status)
            {
                case CraftStatus.Craftable:
                    indColor = UIColors.Success;
                    break;
                case CraftStatus.MissingMaterials:
                    indColor = UIColors.Warning;
                    break;
                default:
                    indColor = UIColors.Error;
                    break;
            }
            UIRenderer.DrawRect(x, y, 3, RecipeRowHeight - 2, indColor);

            // Draw ingredient icons in a row
            int iconX = x + 6;
            int iconY = y + (RecipeRowHeight - SmallIconSize) / 2;
            int maxIcons = (width - 40) / (SmallIconSize + IconSpacing); // Leave room for status

            // Track hovered ingredient for tooltip
            int hoveredIngredientIndex = -1;

            for (int i = 0; i < recipe.Ingredients.Count && i < maxIcons; i++)
            {
                var ing = recipe.Ingredients[i];

                // For recipe groups, resolve to first valid item ID for the icon
                int iconItemId = ing.ItemId;
                if (ing.IsRecipeGroup && ing.ValidItemIds != null)
                {
                    foreach (int validId in ing.ValidItemIds)
                    {
                        iconItemId = validId;
                        break;
                    }
                }

                // Check if this specific icon is hovered
                bool iconHovered = isHovered && WidgetInput.IsMouseOver(iconX, iconY, SmallIconSize, SmallIconSize);
                if (iconHovered)
                {
                    hoveredIngredientIndex = i;
                }

                // Icon background - highlight if hovered
                UIRenderer.DrawRect(iconX, iconY, SmallIconSize, SmallIconSize,
                    iconHovered ? UIColors.InputFocusBg.WithAlpha(200) : UIColors.InputBg.WithAlpha(200));

                // Item icon
                UIRenderer.DrawItem(iconItemId, iconX, iconY, SmallIconSize, SmallIconSize);

                // Stack count (small, bottom-right corner)
                if (ing.RequiredStack > 1)
                {
                    string countText = ing.RequiredStack > 99 ? "99+" : ing.RequiredStack.ToString();
                    int countTextW = TextUtil.MeasureWidth(countText);
                    int countX = iconX + SmallIconSize - countTextW - 1;
                    int countY = iconY + SmallIconSize - 10;
                    UIRenderer.DrawRect(countX - 1, countY - 1, countTextW + 2, 10, UIColors.TooltipBg.WithAlpha(220));
                    UIRenderer.DrawTextSmall(countText, countX, countY, UIColors.Text);
                }

                // Handle click on ingredient icon - navigate to that item
                if (iconHovered && WidgetInput.MouseLeftClick)
                {
                    NavigateToItem(ing.ItemId);
                    WidgetInput.ConsumeClick();
                }

                iconX += SmallIconSize + IconSpacing;
            }

            // Show "..." if more ingredients
            if (recipe.Ingredients.Count > maxIcons)
            {
                UIRenderer.DrawText("..", iconX, iconY + 6, UIColors.TextDim);
            }

            // Full item tooltip for hovered ingredient
            if (hoveredIngredientIndex >= 0)
            {
                var hoveredIng = recipe.Ingredients[hoveredIngredientIndex];
                int have = _checker.GetMaterialCount(hoveredIng.ItemId);
                string haveNeed = $"Have: {have}  Need: {hoveredIng.RequiredStack}";
                // For recipe groups, resolve to a real item ID for tooltip stats
                int tooltipItemId = hoveredIng.ItemId;
                if (hoveredIng.IsRecipeGroup && hoveredIng.ValidItemIds != null)
                {
                    foreach (int validId in hoveredIng.ValidItemIds)
                    {
                        tooltipItemId = validId;
                        break;
                    }
                }
                if (hoveredIng.IsRecipeGroup)
                    ItemTooltip.SetWithName(tooltipItemId, hoveredIng.Name, haveNeed);
                else
                    ItemTooltip.Set(hoveredIng.ItemId, 0, hoveredIng.RequiredStack, haveNeed);
                // Clear other tooltip types
                _pendingButtonTooltip = null;
            }
        }

        /// <summary>
        /// Draw button tooltip (for filter/sort buttons).
        /// Called from DrawPendingTooltip at end of frame.
        /// </summary>
        private void DrawButtonTooltipImpl(int x, int y, string text)
        {
            int padding = 8;
            int tooltipWidth = TextUtil.MeasureWidth(text) + padding * 2;
            int tooltipHeight = 24;

            // Clamp to screen on all edges
            if (x + tooltipWidth > WidgetInput.ScreenWidth - 4)
                x = WidgetInput.ScreenWidth - tooltipWidth - 4;
            if (y + tooltipHeight > WidgetInput.ScreenHeight - 4)
                y = WidgetInput.ScreenHeight - tooltipHeight - 4;
            if (x < 4) x = 4;
            if (y < 4) y = 4;

            UIRenderer.DrawRect(x, y, tooltipWidth, tooltipHeight, UIColors.TooltipBg.WithAlpha(245));
            UIRenderer.DrawRectOutline(x, y, tooltipWidth, tooltipHeight, UIColors.Divider, 1);
            UIRenderer.DrawText(text, x + padding, y + 4, UIColors.TextDim);
        }

        /// <summary>
        /// Draw "Used in" section showing items that can be crafted using the selected item.
        /// Displayed as a grid of output item icons.
        /// </summary>
        private void DrawUsedInSection(int x, int y, int width, int height)
        {
            // Section header
            string headerText = $"Used in ({_recipesUsing.Count})";
            UIRenderer.DrawText(headerText, x, y, UIColors.TextDim);
            int contentY = y + 18;
            int contentHeight = height - 18;

            if (_recipesUsing.Count == 0)
            {
                // Empty state with icon
                UIRenderer.DrawRect(x + 4, contentY + 4, 20, 20, UIColors.ItemBg);
                UIRenderer.DrawText("-", x + 11, contentY + 6, UIColors.Info);
                UIRenderer.DrawText("Not used", x + 30, contentY + 6, UIColors.TextHint);
                return;
            }

            // Grid layout for output items
            int iconSize = UsedInIconSize;
            int iconSpacing = 3;
            int columns = Math.Max(1, width / (iconSize + iconSpacing));
            int rows = (_recipesUsing.Count + columns - 1) / columns;
            int totalHeight = rows * (iconSize + iconSpacing);

            _usedInScroll.Begin(x, contentY, width, contentHeight, totalHeight);

            int visibleRows = (contentHeight / (iconSize + iconSpacing)) + 1;
            int startRow = _usedInScroll.ScrollOffset / (iconSize + iconSpacing);
            int startIndex = startRow * columns;

            for (int i = 0; i < visibleRows * columns && startIndex + i < _recipesUsing.Count; i++)
            {
                int idx = startIndex + i;
                int row = i / columns;
                int col = i % columns;

                int iconX = x + col * (iconSize + iconSpacing);
                int iconY = contentY + row * (iconSize + iconSpacing);

                // Check if icon is fully visible
                if (iconY + iconSize > contentY + contentHeight)
                    break;

                var recipe = _recipesUsing[idx];
                bool isHovered = WidgetInput.IsMouseOver(x, contentY, width, contentHeight) &&
                                 WidgetInput.IsMouseOver(iconX, iconY, iconSize, iconSize);

                // Icon background
                UIRenderer.DrawRect(iconX, iconY, iconSize, iconSize, UIColors.SectionBg.WithAlpha(isHovered ? (byte)220 : (byte)180));

                // Item icon
                UIRenderer.DrawItem(recipe.OutputItemId, iconX + 2, iconY + 2, iconSize - 4, iconSize - 4);

                // Full item tooltip on hover
                if (isHovered)
                {
                    ItemTooltip.Set(recipe.OutputItemId);
                    // Clear other tooltip types
                    _pendingButtonTooltip = null;

                    // Click to select this item
                    if (WidgetInput.MouseLeftClick)
                    {
                        SelectItem(recipe.OutputItemId);
                        _createdByScroll.ResetScroll();
                        _usedInScroll.ResetScroll();
                        WidgetInput.ConsumeClick();
                    }
                }
            }

            _usedInScroll.End();
        }


        private void SelectItem(int itemId)
        {
            _selectedItemId = itemId;
            _craftResultsDirty = true;
            _cachedCraftResults.Clear();

            // Get recipes that create this item
            _recipesCreating.Clear();
            var creatingIndices = _recipeIndex.GetRecipesByOutput(itemId);
            foreach (int idx in creatingIndices)
            {
                var recipe = _recipeIndex.GetRecipe(idx);
                if (recipe != null) _recipesCreating.Add(recipe);
            }

            // Get recipes that use this item
            _recipesUsing.Clear();
            var usingIndices = _recipeIndex.GetRecipesUsingIngredient(itemId);
            foreach (int idx in usingIndices)
            {
                var recipe = _recipeIndex.GetRecipe(idx);
                if (recipe != null) _recipesUsing.Add(recipe);
            }

            // Reset scroll positions for the new item
            _createdByScroll.ResetScroll();
            _usedInScroll.ResetScroll();

            _log.Debug($"Selected item {itemId}: {_recipesCreating.Count} creating, {_recipesUsing.Count} using");
        }

        /// <summary>
        /// Navigate to an item by ID - finds and selects it, changing filter if needed.
        /// </summary>
        private void NavigateToItem(int itemId)
        {
            // Check if item exists in current filtered list
            int index = -1;
            for (int i = 0; i < _filteredItems.Count; i++)
            {
                if (_filteredItems[i].ItemId == itemId)
                {
                    index = i;
                    break;
                }
            }

            // If not found in current filter, try "All" filters
            if (index < 0 && (_currentFilter != RecipeFilter.All || _categoryFilter != CategoryFilter.All))
            {
                _currentFilter = RecipeFilter.All;
                _categoryFilter = CategoryFilter.All;
                FilterItems();

                // Search again
                for (int i = 0; i < _filteredItems.Count; i++)
                {
                    if (_filteredItems[i].ItemId == itemId)
                    {
                        index = i;
                        break;
                    }
                }
            }

            if (index >= 0)
            {
                // Select the item
                SelectItem(itemId);

                // Scroll to show the item in the grid
                // Estimate columns based on typical grid width (~55% of window)
                int estimatedColumns = Math.Max(1, 400 / SlotSize);
                int row = index / estimatedColumns;
                _itemScroll.ScrollToItem(row, SlotSize);

                _log.Debug($"Navigated to item {itemId} at index {index}");
            }
            else
            {
                _log.Debug($"Could not find item {itemId} to navigate to");
            }
        }

        private void RefreshItems()
        {
            _allItems.Clear();
            _checker.RefreshMaterials();

            // Load creative sort order from ContentSamples (once)
            if (_itemSortOrder == null)
            {
                _itemSortOrder = LoadCreativeSortOrder();
            }

            // Load item traits from ContentSamples (once)
            if (_itemTraits == null)
            {
                _itemTraits = ItemSearchTraitsBuilder.GetAllFromContentSamples(_log);
            }

            // Get all unique output items from recipes
            var itemIds = new HashSet<int>();
            foreach (var recipe in _recipeIndex.GetAllRecipes())
            {
                itemIds.Add(recipe.OutputItemId);

                // Also include ingredients
                foreach (var ing in recipe.Ingredients)
                {
                    itemIds.Add(ing.ItemId);
                }
            }

            // Build item entries
            int nextFallback = 100000; // For items not in creative sort
            foreach (int itemId in itemIds)
            {
                if (itemId <= 0) continue;

                var entry = new ItemEntry { ItemId = itemId };

                // Try to get name from first recipe
                var recipes = _recipeIndex.GetRecipesByOutput(itemId);
                if (recipes.Count > 0)
                {
                    var recipe = _recipeIndex.GetRecipe(recipes[0]);
                    entry.Name = recipe?.OutputName ?? $"Item #{itemId}";
                }
                else
                {
                    // Get from a recipe using it
                    var usedIn = _recipeIndex.GetRecipesUsingIngredient(itemId);
                    if (usedIn.Count > 0)
                    {
                        var recipe = _recipeIndex.GetRecipe(usedIn[0]);
                        var ing = recipe?.Ingredients.Find(i => i.ItemId == itemId);
                        entry.Name = ing?.Name ?? $"Item #{itemId}";
                    }
                    else
                    {
                        entry.Name = $"Item #{itemId}";
                    }
                }

                // Creative sort order (groups items by category: weapons, armor, tools, etc.)
                if (_itemSortOrder != null && _itemSortOrder.TryGetValue(itemId, out int order))
                    entry.SortOrder = order;
                else
                    entry.SortOrder = nextFallback++;

                // Category/traits from ContentSamples classification
                entry.Traits = (_itemTraits != null && _itemTraits.TryGetValue(itemId, out var traits))
                    ? traits : ItemSearchTraits.Default;
                entry.Category = entry.Traits.PrimaryCategory;

                // Determine craftability
                entry.CraftStatus = ItemCraftStatus.NoCraft;

                if (recipes.Count > 0)
                {
                    entry.CraftStatus = ItemCraftStatus.HasRecipe;

                    // Check if any recipe is craftable
                    foreach (int recipeIdx in recipes)
                    {
                        var result = _checker.CanCraft(recipeIdx);
                        if (result.Status == CraftStatus.Craftable)
                        {
                            entry.CraftStatus = ItemCraftStatus.Craftable;
                            break;
                        }
                        else if (result.Status == CraftStatus.MissingMaterials &&
                                 result.MissingMaterials.Count < result.Recipe.Ingredients.Count)
                        {
                            if (entry.CraftStatus != ItemCraftStatus.Craftable)
                            {
                                entry.CraftStatus = ItemCraftStatus.Partial;
                            }
                        }
                    }
                }

                _allItems.Add(entry);
            }

            // Sort: craftable first, then by name
            _allItems.Sort((a, b) =>
            {
                if (a.CraftStatus != b.CraftStatus)
                {
                    return a.CraftStatus.CompareTo(b.CraftStatus);
                }
                return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            });

            FilterItems();
            _log.Debug($"Refreshed recipes tab: {_allItems.Count} items");
        }

        private void FilterItems()
        {
            var query = MagicSearchQuery.Parse(_searchBar.Text);
            _filteredItems = new List<ItemEntry>();

            foreach (var item in _allItems)
            {
                // Never show uncraftable items (no recipe exists)
                if (item.CraftStatus == ItemCraftStatus.NoCraft)
                    continue;

                // Apply text/tag search
                if (!query.Matches(item.Name, tag => MagicSearchQuery.MatchesTag(item.Traits, tag, false)))
                    continue;

                // Apply category filter
                if (_categoryFilter != CategoryFilter.All && item.Category != _categoryFilter)
                    continue;

                // Apply craftability filter
                switch (_currentFilter)
                {
                    case RecipeFilter.Craftable:
                        if (item.CraftStatus != ItemCraftStatus.Craftable)
                            continue;
                        break;
                    case RecipeFilter.Partial:
                        if (item.CraftStatus != ItemCraftStatus.Partial)
                            continue;
                        break;
                    case RecipeFilter.HasRecipe:
                        if (item.CraftStatus != ItemCraftStatus.HasRecipe)
                            continue;
                        break;
                    // RecipeFilter.All shows everything (except NoCraft, filtered above)
                }

                _filteredItems.Add(item);
            }

            // Apply sorting based on current sort mode
            int dir = _sortAscending ? 1 : -1;
            switch (_currentSort)
            {
                case RecipeSortMode.Name:
                    _filteredItems.Sort((a, b) => dir * string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
                    break;
                case RecipeSortMode.Craft:
                    _filteredItems.Sort((a, b) =>
                    {
                        if (a.CraftStatus != b.CraftStatus)
                            return dir * a.CraftStatus.CompareTo(b.CraftStatus);
                        return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
                    });
                    break;
                case RecipeSortMode.Type:
                    _filteredItems.Sort((a, b) =>
                    {
                        int cmp = a.SortOrder.CompareTo(b.SortOrder);
                        if (cmp != 0) return dir * cmp;
                        return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
                    });
                    break;
            }

            // Invalidate selection if selected item is no longer in filtered list
            if (_selectedItemId >= 0)
            {
                bool found = false;
                foreach (var item in _filteredItems)
                {
                    if (item.ItemId == _selectedItemId)
                    {
                        found = true;
                        break;
                    }
                }
                if (!found)
                {
                    _selectedItemId = -1;
                    _recipesCreating.Clear();
                    _recipesUsing.Clear();
                }
            }
        }

        private void DrawCategoryFilterRow(int x, int y)
        {
            int btnHeight = 25;
            int labelWidth = 40;

            UIRenderer.DrawText("Cat:", x, y + 5, UIColors.TextDim);
            int xPos = x + labelWidth;

            DrawCategoryFilterButton(xPos, y, 36, btnHeight, "All", CategoryFilter.All, UIColors.TextDim, "All Categories"); xPos += 40;
            DrawCategoryFilterButton(xPos, y, 50, btnHeight, "Wpns", CategoryFilter.Weapons, UIColors.Error, "Weapons"); xPos += 54;
            DrawCategoryFilterButton(xPos, y, 50, btnHeight, "Tools", CategoryFilter.Tools, UIColors.Info, "Tools"); xPos += 54;
            DrawCategoryFilterButton(xPos, y, 50, btnHeight, "Armor", CategoryFilter.Armor, UIColors.Accent, "Armor"); xPos += 54;
            DrawCategoryFilterButton(xPos, y, 46, btnHeight, "Accs", CategoryFilter.Accessories, UIColors.AccentText, "Accessories"); xPos += 50;
            DrawCategoryFilterButton(xPos, y, 46, btnHeight, "Cons", CategoryFilter.Consumables, UIColors.Success, "Consumables"); xPos += 50;
            DrawCategoryFilterButton(xPos, y, 50, btnHeight, "Place", CategoryFilter.Placeable, UIColors.Warning, "Placeable"); xPos += 54;
            DrawCategoryFilterButton(xPos, y, 50, btnHeight, "Mats", CategoryFilter.Materials, UIColors.TextDim, "Materials"); xPos += 54;
            DrawCategoryFilterButton(xPos, y, 50, btnHeight, "Misc", CategoryFilter.Misc, UIColors.TextHint, "Miscellaneous");
        }

        private void DrawCategoryFilterButton(int x, int y, int btnWidth, int btnHeight, string text,
            CategoryFilter filter, Color4 indicatorColor, string tooltip)
        {
            bool isActive = _categoryFilter == filter;
            bool isHovered = WidgetInput.IsMouseOver(x, y, btnWidth, btnHeight);

            Color4 bgColor = isActive ? UIColors.Button : (isHovered ? UIColors.ButtonHover : UIColors.InputBg);
            UIRenderer.DrawRect(x, y, btnWidth, btnHeight, bgColor);

            if (isActive)
                UIRenderer.DrawRect(x, y + btnHeight - 2, btnWidth, 2, UIColors.Accent);

            UIRenderer.DrawText(text, x + 5, y + 6, UIColors.TextDim);

            if (isHovered)
            {
                _pendingButtonTooltip = tooltip;
                _pendingTooltipX = x;
                _pendingTooltipY = y + btnHeight + 5;
                // (ItemTooltip cleared at frame start)

                if (WidgetInput.MouseLeftClick)
                {
                    _categoryFilter = filter;
                    FilterItems();
                    _itemScroll.ResetScroll();
                    WidgetInput.ConsumeClick();
                }
            }
        }

        private void DrawFilterButtons(int x, int y)
        {
            int btnHeight = 25;
            int spacing = 4;
            int labelWidth = 50;

            UIRenderer.DrawText("Craft:", x, y + 5, UIColors.TextDim);
            int xPos = x + labelWidth;

            DrawFilterButton(xPos, y, 50, btnHeight, "All", RecipeFilter.All, UIColors.TextDim,
                "Show all items");
            xPos += 50 + spacing;

            DrawFilterButton(xPos, y, 65, btnHeight, "Craft", RecipeFilter.Craftable, UIColors.Success,
                "Items you can craft now");
            xPos += 65 + spacing;

            DrawFilterButton(xPos, y, 65, btnHeight, "Some", RecipeFilter.Partial, UIColors.Warning,
                "Have some materials");
            xPos += 65 + spacing;

            DrawFilterButton(xPos, y, 75, btnHeight, "Recipe", RecipeFilter.HasRecipe, UIColors.Info,
                "Has recipe but no materials");
        }

        private void DrawFilterButton(int x, int y, int width, int height, string text, RecipeFilter filter,
            Color4 indicatorColor, string tooltip)
        {
            bool isActive = _currentFilter == filter;
            bool isHovered = WidgetInput.IsMouseOver(x, y, width, height);

            Color4 bgColor = isActive ? UIColors.Button : (isHovered ? UIColors.ButtonHover : UIColors.InputBg);
            UIRenderer.DrawRect(x, y, width, height, bgColor);

            // Color indicator at top
            if (filter != RecipeFilter.All)
            {
                UIRenderer.DrawRect(x, y, width, 3, indicatorColor);
            }

            // Active indicator
            if (isActive)
            {
                UIRenderer.DrawRect(x, y + height - 2, width, 2, UIColors.Accent);
            }

            UIRenderer.DrawText(text, x + 5, y + 6, UIColors.TextDim);

            if (isHovered)
            {
                // Defer tooltip to end of frame
                _pendingButtonTooltip = tooltip;
                _pendingTooltipX = x;
                _pendingTooltipY = y + height + 5;
                // Clear other tooltip types
                // (ItemTooltip cleared at frame start)

                if (WidgetInput.MouseLeftClick)
                {
                    _currentFilter = filter;
                    FilterItems();
                    _itemScroll.ResetScroll();
                    WidgetInput.ConsumeClick();
                }
            }
        }

        private void DrawSortButtons(int x, int y)
        {
            int btnWidth = 80;
            int btnHeight = 25;
            int spacing = 4;
            int labelWidth = 50;

            // "Sort:" label
            UIRenderer.DrawText("Sort:", x, y + 5, UIColors.TextDim);
            int xPos = x + labelWidth;

            // Name button
            DrawSortButton(xPos, y, btnWidth, btnHeight, "Name", RecipeSortMode.Name,
                "Sort alphabetically by name");
            xPos += btnWidth + spacing;

            // Craft button
            DrawSortButton(xPos, y, btnWidth, btnHeight, "Craft", RecipeSortMode.Craft,
                "Sort by craftability");
            xPos += btnWidth + spacing;

            // Type button
            DrawSortButton(xPos, y, btnWidth, btnHeight, "Type", RecipeSortMode.Type,
                "Sort by item category");
        }

        private void DrawSortButton(int x, int y, int width, int height, string text, RecipeSortMode mode, string tooltip)
        {
            bool isActive = _currentSort == mode;
            bool isHovered = WidgetInput.IsMouseOver(x, y, width, height);

            Color4 bgColor = isActive ? UIColors.Button : (isHovered ? UIColors.ButtonHover : UIColors.InputBg);
            UIRenderer.DrawRect(x, y, width, height, bgColor);

            // Active indicator bar
            if (isActive)
            {
                UIRenderer.DrawRect(x, y + height - 2, width, 2, UIColors.Accent);
            }

            UIRenderer.DrawText(text, x + 5, y + 6, UIColors.TextDim);

            // Draw big unicode arrow on the right side if active
            if (isActive)
            {
                string arrow = _sortAscending ? "\u25B2" : "\u25BC";
                UIRenderer.DrawText(arrow, x + width - 19, y + 6, UIColors.Accent);
            }

            if (isHovered)
            {
                // Defer tooltip to end of frame
                _pendingButtonTooltip = tooltip;
                _pendingTooltipX = x;
                _pendingTooltipY = y + height + 5;
                // Clear other tooltip types
                // (ItemTooltip cleared at frame start)

                if (WidgetInput.MouseLeftClick)
                {
                    if (_currentSort == mode)
                        _sortAscending = !_sortAscending; // Toggle direction
                    else
                    {
                        _currentSort = mode;
                        _sortAscending = true;
                    }
                    FilterItems();
                    WidgetInput.ConsumeClick();
                }
            }
        }

        private struct ItemEntry
        {
            public int ItemId;
            public string Name;
            public ItemCraftStatus CraftStatus;
            public int SortOrder; // From ContentSamples creative sorting
            public CategoryFilter Category;
            public ItemSearchTraits Traits;
        }

        private enum ItemCraftStatus
        {
            Craftable = 0,   // Can craft now
            Partial = 1,     // Has some materials
            HasRecipe = 2,   // Has recipe but no materials
            NoCraft = 3      // Cannot be crafted
        }

        private enum RecipeFilter
        {
            All,        // Show all items (except uncraftable)
            Craftable,  // Only craftable items
            Partial,    // Only partially craftable items
            HasRecipe   // Items with recipes but no materials
        }

        private enum RecipeSortMode
        {
            Craft,      // Group by craftability (default)
            Name,       // Sort alphabetically by name
            Type        // Sort by item category (Terraria's creative sort order)
        }

        /// <summary>
        /// Load item sort order from ContentSamples.ItemCreativeSortingId via reflection.
        /// Returns a flat dictionary mapping itemId -> sequential sort index.
        /// Items in the same category (weapons, armor, etc.) get adjacent indices.
        /// </summary>
        private Dictionary<int, int> LoadCreativeSortOrder()
        {
            try
            {
                var contentSamplesType = Type.GetType("Terraria.ID.ContentSamples, Terraria")
                    ?? Assembly.Load("Terraria").GetType("Terraria.ID.ContentSamples");
                if (contentSamplesType == null)
                {
                    _log.Error("Could not find ContentSamples type");
                    return null;
                }

                var sortField = contentSamplesType.GetField("ItemCreativeSortingId",
                    BindingFlags.Public | BindingFlags.Static);
                if (sortField == null)
                {
                    _log.Error("Could not find ItemCreativeSortingId field");
                    return null;
                }

                var dict = sortField.GetValue(null);
                if (dict == null)
                {
                    _log.Error("ItemCreativeSortingId is null");
                    return null;
                }

                // It's Dictionary<int, CreativeHelper.ItemGroupAndOrderInGroup>
                // ItemGroupAndOrderInGroup has: int ItemType, ItemGroup Group, int OrderInGroup
                // We need to read Group (enum, underlying int) for category sorting
                var result = new Dictionary<int, int>();

                // Use IDictionary interface to iterate
                var enumerator = dict.GetType().GetMethod("GetEnumerator").Invoke(dict, null);
                var enumeratorType = enumerator.GetType();
                var moveNext = enumeratorType.GetMethod("MoveNext");
                var currentProp = enumeratorType.GetProperty("Current");

                // Get field accessors for the struct
                FieldInfo groupField = null;
                FieldInfo orderField = null;
                FieldInfo itemTypeField = null;

                // Build a list of (itemType, group, orderInGroup) then sort to get sequential indices
                var entries = new List<(int itemType, int group, int order)>();

                while ((bool)moveNext.Invoke(enumerator, null))
                {
                    var kvp = currentProp.GetValue(enumerator);
                    var kvpType = kvp.GetType();

                    int key = (int)kvpType.GetProperty("Key").GetValue(kvp);
                    var value = kvpType.GetProperty("Value").GetValue(kvp);

                    if (groupField == null)
                    {
                        var valueType = value.GetType();
                        groupField = valueType.GetField("Group");
                        orderField = valueType.GetField("OrderInGroup");
                        itemTypeField = valueType.GetField("ItemType");
                    }

                    int group = Convert.ToInt32(groupField.GetValue(value));
                    int order = (int)orderField.GetValue(value);
                    entries.Add((key, group, order));
                }

                // Sort by group then by order within group
                entries.Sort((a, b) =>
                {
                    int cmp = a.group.CompareTo(b.group);
                    if (cmp != 0) return cmp;
                    return a.order.CompareTo(b.order);
                });

                // Assign sequential indices
                for (int i = 0; i < entries.Count; i++)
                {
                    result[entries[i].itemType] = i;
                }

                _log.Debug($"Loaded creative sort order for {result.Count} items");
                return result;
            }
            catch (Exception ex)
            {
                _log.Error($"Failed to load creative sort order: {ex.Message}");
                return null;
            }
        }

    }
}
