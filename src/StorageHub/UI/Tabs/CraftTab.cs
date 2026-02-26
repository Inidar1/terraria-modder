using System;
using System.Collections.Generic;
using TerrariaModder.Core.UI;
using TerrariaModder.Core.Logging;
using StorageHub.Crafting;
using StorageHub.Config;
using StorageHub.Storage;
using TerrariaModder.Core.Config;
using TerrariaModder.Core.UI.Widgets;

namespace StorageHub.UI.Tabs
{
    /// <summary>
    /// Craft tab - shows craftable recipes in a grid view with bottom control panel.
    ///
    /// Features:
    /// - Grid of item icons (no names, hover for tooltip)
    /// - Bottom panel with recipe details and craft controls
    /// - Quantity selector (+1, +10, Max)
    /// - Shows missing materials for partial recipes
    /// - Recursive crafting support
    /// </summary>
    public class CraftTab
    {
        private readonly ILogger _log;
        private readonly RecipeIndex _recipeIndex;
        private readonly CraftabilityChecker _checker;
        private readonly RecursiveCrafter _crafter;
        private readonly StorageHubConfig _config;
        private readonly IModConfig _modConfig;
        private CraftingExecutor _executor;

        // UI components
        private readonly TextInput _searchBar = new TextInput("Search / #tag...", 200);
        private readonly ScrollView _scrollPanel = new ScrollView();

        // Cached data
        private List<CraftabilityResult> _craftableRecipes = new List<CraftabilityResult>();
        private List<CraftabilityResult> _filteredRecipes = new List<CraftabilityResult>();
        private bool _needsRefresh = true;
        private bool _showPartial = false;

        // Selection — tracked by stable recipe identity, not list position
        private int _selectedRecipeIndex = -1;
        private int _selectedOriginalIndex = -1; // Terraria recipe index (stable across refreshes)
        private int _craftAmount = 1;

        // Filter and sort state
        private CategoryFilter _categoryFilter = CategoryFilter.All;
        private CraftSortMode _craftSort = CraftSortMode.Name;
        private bool _craftSortAscending = true;

        // Item trait cache (loaded once from ContentSamples)
        private Dictionary<int, ItemSearchTraits> _itemTraits;

        // Deferred tooltip
        private string _craftTooltipText;
        private int _craftTooltipX, _craftTooltipY;

        // Toast notification
        private string _toastMessage = "";
        private int _toastTimer = 0;
        private bool _toastIsError = false;
        private const int ToastDuration = 120; // 2 seconds at 60fps

        // Recursive crafting plan cache (computed per selection)
        private int _recursiveMaxCraftable;
        private int _recursivePlanRecipeIdx = -1;
        private bool _recursivePlanComputed;

        // Layout — grid view with bottom panel
        private const int GridSlotSize = 44;
        private const int BottomPanelHeight = 120;
        private int _gridColumns = 1;

        /// <summary>
        /// Number of fully craftable recipes (all materials available).
        /// </summary>
        public int CraftableCount { get; private set; }

        /// <summary>
        /// Callback when storage is modified (crafting consumed/created items).
        /// Parent UI should refresh storage data when this is called.
        /// </summary>
        public Action OnStorageModified { get; set; }

        public CraftTab(ILogger log, RecipeIndex recipeIndex, CraftabilityChecker checker, RecursiveCrafter crafter, StorageHubConfig config, IStorageProvider storage, IModConfig modConfig)
        {
            _log = log;
            _recipeIndex = recipeIndex;
            _checker = checker;
            _crafter = crafter;
            _config = config;
            _modConfig = modConfig;
            _executor = new CraftingExecutor(log, storage);
        }

        /// <summary>
        /// Mark data as needing refresh.
        /// </summary>
        public void MarkDirty()
        {
            _needsRefresh = true;
            _checker.MarkDirty();
            _recursivePlanComputed = false;
        }

        /// <summary>
        /// Lazily compute a recursive crafting plan for the selected recipe.
        /// Only runs when the recipe is MissingMaterials and recursive crafting is enabled.
        /// </summary>
        private void EnsureRecursivePlan(CraftabilityResult result)
        {
            int recipeIdx = result.Recipe.OriginalIndex;
            if (_recursivePlanComputed && _recursivePlanRecipeIdx == recipeIdx)
                return;

            _recursivePlanRecipeIdx = recipeIdx;
            _recursivePlanComputed = true;
            _recursiveMaxCraftable = 0;

            int depth = _modConfig.Get<int>("recursiveCraftingDepth", 0);
            var plan = _crafter.CalculatePlan(recipeIdx, 1, depth);
            if (plan == null)
            {
                _log.Debug($"[CraftTab] Recursive plan null for {result.Recipe.OutputName} (OrigIdx={recipeIdx})");
                return;
            }
            if (!plan.CanCraft)
            {
                _log.Debug($"[CraftTab] Recursive plan failed for {result.Recipe.OutputName}: {plan.ErrorMessage}");
                return;
            }

            // Estimate max from raw material ratios (approximate; exact check on craft click)
            int max = int.MaxValue;
            foreach (var kvp in plan.RawMaterialsNeeded)
            {
                if (kvp.Value <= 0) continue;
                int have = _checker.GetMaterialCount(kvp.Key);
                int ratio = have / kvp.Value;
                _log.Debug($"[CraftTab]   raw mat {kvp.Key}: need={kvp.Value}, have={have}, ratio={ratio}");
                max = Math.Min(max, ratio);
            }

            // If no raw materials needed (all intermediates available), plan is directly craftable
            // with at least 1 output — use the plan's step counts to estimate
            if (max == int.MaxValue)
                max = plan.RawMaterialsNeeded.Count == 0 ? 1 : 0;

            _recursiveMaxCraftable = max;
            _log.Debug($"[CraftTab] Recursive plan OK for {result.Recipe.OutputName}: max={_recursiveMaxCraftable}, steps={plan.Steps.Count}, rawMats={plan.RawMaterialsNeeded.Count}");
        }

        /// <summary>
        /// Get the effective max craftable count, accounting for recursive crafting.
        /// </summary>
        private int GetEffectiveMaxCraftable()
        {
            if (_selectedRecipeIndex < 0 || _selectedRecipeIndex >= _filteredRecipes.Count)
                return 0;

            var result = _filteredRecipes[_selectedRecipeIndex];
            if (result.MaxCraftable > 0)
                return result.MaxCraftable;

            return _recursiveMaxCraftable;
        }

        /// <summary>
        /// Navigate to a specific item's recipe, adjusting filters as needed.
        /// </summary>
        /// <param name="itemId">The item ID to find and select</param>
        /// <returns>True if item was found and selected</returns>
        public bool NavigateToItem(int itemId)
        {
            // Force refresh to get latest state
            RefreshRecipes();
            _needsRefresh = false;

            // First try with current filter
            int index = FindRecipeByOutputId(itemId);
            if (index >= 0)
            {
                SelectRecipeAt(index);
                _craftAmount = 1;
                ScrollToSelectedRecipe();
                return true;
            }

            // If not found with current filter, enable "All" mode and try again
            if (!_showPartial)
            {
                _showPartial = true;
                RefreshRecipes();
                _needsRefresh = false;

                index = FindRecipeByOutputId(itemId);
                if (index >= 0)
                {
                    SelectRecipeAt(index);
                    _craftAmount = 1;
                    ScrollToSelectedRecipe();
                    return true;
                }
            }

            // Clear search filter too if needed
            if (!string.IsNullOrEmpty(_searchBar.Text))
            {
                _searchBar.Clear();
                // Consume HasChanged so Draw() doesn't re-filter and reset scroll on next frame
                var _ = _searchBar.HasChanged;
                FilterRecipes();

                index = FindRecipeByOutputId(itemId);
                if (index >= 0)
                {
                    SelectRecipeAt(index);
                    _craftAmount = 1;
                    ScrollToSelectedRecipe();
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Set selection by list index, tracking stable recipe identity.
        /// </summary>
        private void SelectRecipeAt(int listIndex)
        {
            _selectedRecipeIndex = listIndex;
            _selectedOriginalIndex = (listIndex >= 0 && listIndex < _filteredRecipes.Count)
                ? _filteredRecipes[listIndex].Recipe.OriginalIndex
                : -1;
            _recursivePlanComputed = false;
        }

        /// <summary>
        /// Re-find the previously selected recipe after the list was rebuilt.
        /// Falls back to -1 if the recipe is no longer in the filtered list.
        /// </summary>
        private void RestoreSelection()
        {
            if (_selectedOriginalIndex < 0)
            {
                _selectedRecipeIndex = -1;
                return;
            }

            for (int i = 0; i < _filteredRecipes.Count; i++)
            {
                if (_filteredRecipes[i].Recipe.OriginalIndex == _selectedOriginalIndex)
                {
                    _selectedRecipeIndex = i;
                    return;
                }
            }

            // Recipe no longer in list
            _selectedRecipeIndex = -1;
            _selectedOriginalIndex = -1;
        }

        private int FindRecipeByOutputId(int itemId)
        {
            for (int i = 0; i < _filteredRecipes.Count; i++)
            {
                if (_filteredRecipes[i].Recipe.OutputItemId == itemId)
                    return i;
            }
            return -1;
        }

        private void ScrollToSelectedRecipe()
        {
            if (_selectedRecipeIndex < 0) return;
            // Grid layout: scroll to the row containing the selected item
            int row = _gridColumns > 0 ? _selectedRecipeIndex / _gridColumns : 0;
            _scrollPanel.ScrollToItem(row, GridSlotSize);
        }

        /// <summary>
        /// Handle input during Update phase.
        /// </summary>
        public void Update()
        {
            _searchBar.Update();
        }

        /// <summary>
        /// Draw the craft tab — grid of item icons with bottom control panel.
        /// </summary>
        public void Draw(int x, int y, int width, int height)
        {
            const int RowHeight = 28;
            const int RowGap = 2;

            // Reset deferred tooltips
            _craftTooltipText = null;
            ItemTooltip.Clear();

            // Refresh if needed
            if (_needsRefresh)
            {
                RefreshRecipes();
                _needsRefresh = false;
            }

            // Row 1: Search bar (full width)
            _searchBar.Draw(x, y, width, 28);
            if (_searchBar.HasChanged)
            {
                FilterRecipes();
                _scrollPanel.ResetScroll();
            }

            // Row 2: Category filter
            int row2Y = y + RowHeight + RowGap;
            DrawCategoryFilterRow(x, row2Y, width);

            // Row 3: Craft filter (Craftable / All)
            int row3Y = row2Y + RowHeight + RowGap;
            DrawCraftFilterRow(x, row3Y, width);

            // Row 4: Sort buttons
            int row4Y = row3Y + RowHeight + RowGap;
            DrawCraftSortRow(x, row4Y, width);

            // Grid area (between filter rows and bottom panel)
            int gridY = row4Y + RowHeight + 4;
            int gridHeight = height - (gridY - y) - BottomPanelHeight;
            if (gridHeight < GridSlotSize) gridHeight = GridSlotSize;
            DrawRecipeGrid(x, gridY, width, gridHeight);

            // Bottom panel (selected recipe details + craft controls)
            int bottomY = gridY + gridHeight;
            DrawBottomPanel(x, bottomY, width, BottomPanelHeight);

            // Toast notification (centered, above bottom panel)
            if (_toastTimer > 0)
            {
                _toastTimer--;
                DrawToast(x, bottomY - 35, width);
            }

            // Deferred tooltips (drawn last, on top of everything)
            // Item tooltip (full vanilla-style) takes priority
            ItemTooltip.DrawDeferred();

            // Simple text tooltip for buttons/labels
            if (_craftTooltipText != null)
            {
                int pad = 8;
                int tw = TextUtil.MeasureWidth(_craftTooltipText) + pad * 2;
                int th = 24;
                int tx = _craftTooltipX;
                int ty = _craftTooltipY;
                if (tx + tw > WidgetInput.ScreenWidth - 4) tx = WidgetInput.ScreenWidth - tw - 4;
                if (tx < 4) tx = 4;
                UIRenderer.DrawRect(tx, ty, tw, th, UIColors.TooltipBg.WithAlpha(245));
                UIRenderer.DrawRectOutline(tx, ty, tw, th, UIColors.Divider, 1);
                UIRenderer.DrawText(_craftTooltipText, tx + pad, ty + 4, UIColors.TextDim);
            }
        }

        private void DrawToast(int x, int y, int width)
        {
            int toastWidth = Math.Min(300, width - 20);
            int toastX = x + (width - toastWidth) / 2;

            // Background
            UIRenderer.DrawRect(toastX, y, toastWidth, 30,
                _toastIsError ? UIColors.Error.WithAlpha(230) : UIColors.Success.WithAlpha(230));

            // Border
            UIRenderer.DrawRectOutline(toastX, y, toastWidth, 30,
                _toastIsError ? UIColors.Error : UIColors.Success, 1);

            // Text
            UIRenderer.DrawText(_toastMessage, toastX + 10, y + 8,
                _toastIsError ? UIColors.Error : UIColors.Success);
        }

        private void DrawRecipeGrid(int x, int y, int width, int height)
        {
            _gridColumns = Math.Max(1, width / GridSlotSize);
            int totalRows = (_filteredRecipes.Count + _gridColumns - 1) / _gridColumns;
            int totalHeight = totalRows * GridSlotSize;

            _scrollPanel.Begin(x, y, width, height, totalHeight);

            // Calculate visible range — only draw items that fit fully within the view
            int visibleRows = height / GridSlotSize;
            int startRow = _scrollPanel.ScrollOffset / GridSlotSize;
            int startIndex = startRow * _gridColumns;

            for (int i = 0; i < visibleRows * _gridColumns && startIndex + i < _filteredRecipes.Count; i++)
            {
                int recipeIdx = startIndex + i;
                int row = i / _gridColumns;
                int col = i % _gridColumns;

                int slotX = x + col * GridSlotSize;
                int slotY = y + row * GridSlotSize;

                var result = _filteredRecipes[recipeIdx];
                bool isSelected = recipeIdx == _selectedRecipeIndex;
                bool isInScrollBounds = WidgetInput.IsMouseOver(x, y, width, height);
                bool isHovered = isInScrollBounds && WidgetInput.IsMouseOver(slotX, slotY, GridSlotSize - 2, GridSlotSize - 2);

                DrawGridSlot(slotX, slotY, result, isSelected, isHovered);

                if (isHovered)
                {
                    // Full vanilla-style item tooltip with craft status
                    string extraLine = null;
                    if (result.Status == CraftStatus.Craftable)
                        extraLine = $"Max: {result.MaxCraftable}";
                    else if (!_showPartial && result.Status == CraftStatus.MissingMaterials)
                        extraLine = "Craftable (recursive)"; // In Craftable view, these are recursively craftable
                    else if (result.MissingMaterials != null && result.MissingMaterials.Count > 0)
                        extraLine = $"-{result.MissingMaterials.Count} mat";

                    if (extraLine != null)
                        ItemTooltip.Set(result.Recipe.OutputItemId, 0, result.Recipe.OutputStack, extraLine);
                    else
                        ItemTooltip.Set(result.Recipe.OutputItemId, 0, result.Recipe.OutputStack);

                    if (WidgetInput.MouseLeftClick)
                    {
                        SelectRecipeAt(recipeIdx);
                        _craftAmount = 1;
                        WidgetInput.ConsumeClick();
                    }
                }
            }

            // Scrollbar (drawn after content)
            _scrollPanel.End();

            // Empty state
            if (_filteredRecipes.Count == 0)
            {
                UIRenderer.DrawText("No craftable recipes found", x + 10, y + 20, UIColors.TextDim);
                UIRenderer.DrawText("Open chests and gather materials", x + 10, y + 40, UIColors.TextHint);
            }
        }

        private void DrawGridSlot(int x, int y, CraftabilityResult result, bool isSelected, bool isHovered)
        {
            const int SlotOuter = GridSlotSize - 2;
            const int IconPad = 4;
            const int IconSize = SlotOuter - IconPad * 2;

            // Background
            Color4 slotBg;
            if (isSelected)
                slotBg = UIColors.ItemHoverBg;
            else if (isHovered)
                slotBg = UIColors.InputFocusBg;
            else
                slotBg = UIColors.ItemBg;
            UIRenderer.DrawRect(x, y, SlotOuter, SlotOuter, slotBg);

            // Selection/hover border
            if (isSelected)
                UIRenderer.DrawRectOutline(x, y, SlotOuter, SlotOuter, UIColors.Accent, 2);
            else if (isHovered)
                UIRenderer.DrawRectOutline(x, y, SlotOuter, SlotOuter, UIColors.Accent, 1);

            // Item icon
            UIRenderer.DrawItem(result.Recipe.OutputItemId, x + IconPad, y + IconPad, IconSize, IconSize);

            // Status indicator bar at bottom
            // In Craftable view, MissingMaterials items are recursively craftable — show green
            Color4 statusColor;
            switch (result.Status)
            {
                case CraftStatus.Craftable: statusColor = UIColors.Success; break;
                case CraftStatus.MissingMaterials: statusColor = !_showPartial ? UIColors.Success : UIColors.Warning; break;
                default: statusColor = UIColors.Error; break;
            }
            UIRenderer.DrawRect(x + 1, y + SlotOuter - 3, SlotOuter - 2, 3, statusColor);

            // Output stack count (top-left, with shadow)
            if (result.Recipe.OutputStack > 1)
            {
                string stackText = result.Recipe.OutputStack.ToString();
                UIRenderer.DrawText(stackText, x + 3, y + 2, 0, 0, 0);
                UIRenderer.DrawText(stackText, x + 2, y + 1, UIColors.Text);
            }

            // Max craftable count removed — user can hover or click for details
        }

        private void DrawBottomPanel(int x, int y, int width, int height)
        {
            // Divider line
            UIRenderer.DrawRect(x, y, width, 1, UIColors.Divider);

            // Background
            UIRenderer.DrawRect(x, y + 1, width, height - 1, UIColors.PanelBg);

            if (_selectedRecipeIndex < 0 || _selectedRecipeIndex >= _filteredRecipes.Count)
            {
                UIRenderer.DrawText("Select a recipe from the grid above", x + 10, y + height / 2 - 6, UIColors.TextHint);
                return;
            }

            var result = _filteredRecipes[_selectedRecipeIndex];
            var recipe = result.Recipe;

            // Check if recursive crafting can handle this recipe
            bool showCraftControls = result.Status == CraftStatus.Craftable;
            if (!showCraftControls && result.Status == CraftStatus.MissingMaterials
                && _modConfig.Get<bool>("recursiveCrafting", true))
            {
                EnsureRecursivePlan(result);
                showCraftControls = _recursiveMaxCraftable > 0;
            }

            // Layout: left side = recipe info, right side = craft controls
            int controlsWidth = showCraftControls ? 220 : 0;
            int infoWidth = width - controlsWidth - (showCraftControls ? 10 : 0);
            int infoX = x + 8;

            // === LEFT SIDE: Recipe Info ===

            // Row 1: Icon + Name + Status
            int lineY = y + 8;
            const int OutputIconSize = 32;
            UIRenderer.DrawItem(recipe.OutputItemId, infoX, lineY, OutputIconSize, OutputIconSize);

            // Tooltip on output icon hover
            if (WidgetInput.IsMouseOver(infoX, lineY, OutputIconSize, OutputIconSize))
                ItemTooltip.Set(recipe.OutputItemId, 0, recipe.OutputStack);

            int nameX = infoX + OutputIconSize + 6;
            string displayName = TextUtil.Truncate(recipe.OutputName, infoWidth - OutputIconSize - 80);
            UIRenderer.DrawText(displayName, nameX, lineY + 2, UIColors.AccentText);

            // Status + stack on second line next to icon
            string statusText;
            Color4 statusColor;
            switch (result.Status)
            {
                case CraftStatus.Craftable:
                    statusText = $"Max: {result.MaxCraftable}";
                    statusColor = UIColors.Success;
                    break;
                case CraftStatus.MissingMaterials:
                    if (_recursiveMaxCraftable > 0)
                    {
                        statusText = $"Max: {_recursiveMaxCraftable}";
                        statusColor = UIColors.Success;
                    }
                    else
                    {
                        statusText = $"-{result.MissingMaterials?.Count ?? 0} mat";
                        statusColor = UIColors.Warning;
                    }
                    break;
                default:
                    statusText = "Cannot craft";
                    statusColor = UIColors.Error;
                    break;
            }
            UIRenderer.DrawText(statusText, nameX, lineY + 18, statusColor);
            if (recipe.OutputStack > 1)
            {
                int statusW = TextUtil.MeasureWidth(statusText);
                UIRenderer.DrawText($"  x{recipe.OutputStack}", nameX + statusW, lineY + 18, UIColors.TextDim);
            }

            // Row 2: Ingredients (horizontal layout with icons)
            lineY = y + 46;
            UIRenderer.DrawText("Ingredients:", infoX, lineY, UIColors.TextDim);
            int ingX = infoX + TextUtil.MeasureWidth("Ingredients:") + 6;
            const int IngIconSize = 18;
            int ingredientsShown = 0;
            int maxIngX = x + infoWidth - 40;

            foreach (var ing in recipe.Ingredients)
            {
                if (ingX + IngIconSize + 30 > maxIngX && ingredientsShown < recipe.Ingredients.Count - 1)
                {
                    int remaining = recipe.Ingredients.Count - ingredientsShown;
                    UIRenderer.DrawText($"+{remaining}", ingX + 4, lineY + 2, UIColors.TextHint);
                    break;
                }

                int have = _checker.GetMaterialCount(ing.ItemId);
                bool hasEnough = have >= ing.RequiredStack;
                Color4 ingColor = hasEnough ? UIColors.Success : UIColors.Error;

                // For recipe groups, use the first valid item ID for the icon
                int iconItemId = ing.ItemId;
                if (ing.IsRecipeGroup && ing.ValidItemIds != null)
                {
                    foreach (int validId in ing.ValidItemIds)
                    {
                        iconItemId = validId;
                        break;
                    }
                }
                UIRenderer.DrawItem(iconItemId, ingX, lineY - 1, IngIconSize, IngIconSize);

                // Tooltip on ingredient icon hover
                if (WidgetInput.IsMouseOver(ingX, lineY - 1, IngIconSize, IngIconSize))
                {
                    if (ing.IsRecipeGroup)
                        ItemTooltip.SetWithName(iconItemId, ing.Name);
                    else
                        ItemTooltip.Set(iconItemId);
                }

                string countStr = $"{have}/{ing.RequiredStack}";
                UIRenderer.DrawText(countStr, ingX + IngIconSize + 2, lineY + 2, ingColor);
                ingX += IngIconSize + TextUtil.MeasureWidth(countStr) + 10;
                ingredientsShown++;
            }

            // Row 3: Stations + Environmental requirements (compact, one line)
            lineY = y + 68;
            int reqX = infoX;

            if (recipe.RequiredTiles.Count > 0)
            {
                UIRenderer.DrawText("Stations:", reqX, lineY, UIColors.TextDim);
                reqX += TextUtil.MeasureWidth("Stations:") + 4;
                int stationsShown = 0;

                foreach (var tile in recipe.RequiredTiles)
                {
                    if (stationsShown >= 3)
                    {
                        UIRenderer.DrawText($"+{recipe.RequiredTiles.Count - 3}", reqX, lineY, UIColors.TextHint);
                        reqX += 30;
                        break;
                    }

                    bool hasStation = _checker.IsStationAvailable(tile);
                    string tileName = TileNames.GetName(tile);
                    if (stationsShown > 0)
                    {
                        UIRenderer.DrawText(",", reqX, lineY, UIColors.TextDim);
                        reqX += 8;
                    }
                    UIRenderer.DrawText(tileName, reqX, lineY, hasStation ? UIColors.Success : UIColors.Error);
                    reqX += TextUtil.MeasureWidth(tileName) + 4;
                    stationsShown++;
                }
            }

            // Environmental requirements on same line
            if (recipe.HasEnvironmentalRequirements)
            {
                if (recipe.RequiredTiles.Count > 0)
                {
                    UIRenderer.DrawText(" | ", reqX, lineY, UIColors.TextDim);
                    reqX += 20;
                }

                bool first = true;
                if (recipe.NeedWater) { DrawEnvInline(ref reqX, lineY, "Water", _config.HasSpecialUnlock("water"), ref first); }
                if (recipe.NeedHoney) { DrawEnvInline(ref reqX, lineY, "Honey", _config.HasSpecialUnlock("honey"), ref first); }
                if (recipe.NeedLava) { DrawEnvInline(ref reqX, lineY, "Lava", _config.HasSpecialUnlock("lava"), ref first); }
                if (recipe.NeedSnowBiome) { DrawEnvInline(ref reqX, lineY, "Snow", _config.HasSpecialUnlock("snow"), ref first); }
                if (recipe.NeedGraveyard) { DrawEnvInline(ref reqX, lineY, "Grave", _config.HasSpecialUnlock("graveyard"), ref first); }
                if (recipe.NeedShimmer) { DrawEnvInline(ref reqX, lineY, "Shimmer", _config.HasSpecialUnlock("shimmer"), ref first); }
            }

            // === RIGHT SIDE: Craft Controls ===
            if (showCraftControls)
            {
                int ctrlX = x + width - controlsWidth;
                DrawCraftControls(ctrlX, y + 6, controlsWidth);
            }
        }

        private void DrawEnvInline(ref int x, int y, string name, bool unlocked, ref bool first)
        {
            if (!first)
            {
                UIRenderer.DrawText(",", x, y, UIColors.TextDim);
                x += 8;
            }
            first = false;
            UIRenderer.DrawText(name, x, y, unlocked ? UIColors.Success : UIColors.Error);
            x += TextUtil.MeasureWidth(name) + 4;
        }

        private void DrawCategoryFilterRow(int x, int y, int width)
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
                _craftTooltipText = tooltip;
                _craftTooltipX = x;
                _craftTooltipY = y + btnHeight + 5;

                if (WidgetInput.MouseLeftClick)
                {
                    _categoryFilter = filter;
                    FilterRecipes();
                    _scrollPanel.ResetScroll();
                    WidgetInput.ConsumeClick();
                }
            }
        }

        private void DrawCraftFilterRow(int x, int y, int width)
        {
            int btnHeight = 25;
            int labelWidth = 50;

            UIRenderer.DrawText("Craft:", x, y + 5, UIColors.TextDim);
            int xPos = x + labelWidth;

            DrawCraftFilterButton(xPos, y, 90, btnHeight, "Craftable", false, UIColors.Success, "Show only fully craftable");
            xPos += 94;

            DrawCraftFilterButton(xPos, y, 160, btnHeight, "Missing Ingredients", true, UIColors.Warning, "Include recipes with missing ingredients");
        }

        private void DrawCraftFilterButton(int x, int y, int btnWidth, int btnHeight, string text,
            bool showPartialValue, Color4 indicatorColor, string tooltip)
        {
            bool isActive = _showPartial == showPartialValue;
            bool isHovered = WidgetInput.IsMouseOver(x, y, btnWidth, btnHeight);

            Color4 bgColor = isActive ? UIColors.Button : (isHovered ? UIColors.ButtonHover : UIColors.InputBg);
            UIRenderer.DrawRect(x, y, btnWidth, btnHeight, bgColor);

            if (!showPartialValue)
                UIRenderer.DrawRect(x, y, btnWidth, 3, indicatorColor);

            if (isActive)
                UIRenderer.DrawRect(x, y + btnHeight - 2, btnWidth, 2, UIColors.Accent);

            UIRenderer.DrawText(text, x + 5, y + 6, UIColors.TextDim);

            if (isHovered)
            {
                _craftTooltipText = tooltip;
                _craftTooltipX = x;
                _craftTooltipY = y + btnHeight + 5;

                if (WidgetInput.MouseLeftClick)
                {
                    _showPartial = showPartialValue;
                    RefreshRecipes();
                    _scrollPanel.ResetScroll();
                    WidgetInput.ConsumeClick();
                }
            }
        }

        private void DrawCraftSortRow(int x, int y, int width)
        {
            int btnHeight = 25;
            int labelWidth = 50;

            UIRenderer.DrawText("Sort:", x, y + 5, UIColors.TextDim);
            int xPos = x + labelWidth;

            DrawCraftSortButton(xPos, y, 80, btnHeight, "Name", CraftSortMode.Name, "Sort alphabetically"); xPos += 84;
            DrawCraftSortButton(xPos, y, 80, btnHeight, "Max", CraftSortMode.Max, "Sort by max craftable"); xPos += 84;
            DrawCraftSortButton(xPos, y, 80, btnHeight, "Status", CraftSortMode.Status, "Sort by craft status"); xPos += 84;
            DrawCraftSortButton(xPos, y, 80, btnHeight, "Type", CraftSortMode.Type, "Sort by item type");
        }

        private void DrawCraftSortButton(int x, int y, int btnWidth, int btnHeight, string text, CraftSortMode mode, string tooltip)
        {
            bool isActive = _craftSort == mode;
            bool isHovered = WidgetInput.IsMouseOver(x, y, btnWidth, btnHeight);

            Color4 bgColor = isActive ? UIColors.Button : (isHovered ? UIColors.ButtonHover : UIColors.InputBg);
            UIRenderer.DrawRect(x, y, btnWidth, btnHeight, bgColor);

            if (isActive)
                UIRenderer.DrawRect(x, y + btnHeight - 2, btnWidth, 2, UIColors.Accent);

            UIRenderer.DrawText(text, x + 5, y + 6, UIColors.TextDim);

            if (isActive)
            {
                string arrow = _craftSortAscending ? "\u25B2" : "\u25BC";
                UIRenderer.DrawText(arrow, x + btnWidth - 19, y + 6, UIColors.Accent);
            }

            if (isHovered)
            {
                _craftTooltipText = tooltip;
                _craftTooltipX = x;
                _craftTooltipY = y + btnHeight + 5;

                if (WidgetInput.MouseLeftClick)
                {
                    if (_craftSort == mode)
                        _craftSortAscending = !_craftSortAscending;
                    else
                    {
                        _craftSort = mode;
                        _craftSortAscending = true;
                    }
                    FilterRecipes();
                    WidgetInput.ConsumeClick();
                }
            }
        }

        private void DrawCraftControls(int x, int y, int width)
        {
            if (_selectedRecipeIndex < 0 || _selectedRecipeIndex >= _filteredRecipes.Count)
                return;

            var result = _filteredRecipes[_selectedRecipeIndex];
            int maxCraftable = GetEffectiveMaxCraftable();
            bool isRecursive = result.Status == CraftStatus.MissingMaterials;

            // Row 1: Amount display
            int row1Y = y + 2;
            UIRenderer.DrawText("Amount:", x + 5, row1Y, UIColors.TextDim);
            int amountLabelW = TextUtil.MeasureWidth("Amount:") + 6;
            string countPart = _craftAmount.ToString();
            UIRenderer.DrawText(countPart, x + 5 + amountLabelW, row1Y, UIColors.AccentText);
            int countW = TextUtil.MeasureWidth(countPart);
            UIRenderer.DrawText($" / {maxCraftable}", x + 5 + amountLabelW + countW, row1Y, UIColors.Success);

            // Layout: 4 buttons per row, evenly spaced
            int btnH = 20;
            int gap = 2;
            int totalBtnW = width - 10;
            int btnW = (totalBtnW - 3 * gap) / 4;

            // Row 2: +1, +10, +100, Max
            int row2Y = y + 22;
            int cx = x + 5;
            DrawSmallButton(cx, row2Y, btnW, btnH, "+1", () => _craftAmount++); cx += btnW + gap;
            DrawSmallButton(cx, row2Y, btnW, btnH, "+10", () => _craftAmount += 10); cx += btnW + gap;
            DrawSmallButton(cx, row2Y, btnW, btnH, "+100", () => _craftAmount += 100); cx += btnW + gap;
            DrawSmallButton(cx, row2Y, btnW, btnH, "Max", () => _craftAmount = maxCraftable);

            // Row 3: -1, -10, -100, Reset
            int row3Y = row2Y + btnH + gap;
            cx = x + 5;
            DrawSmallButton(cx, row3Y, btnW, btnH, "-1", () => _craftAmount = Math.Max(1, _craftAmount - 1)); cx += btnW + gap;
            DrawSmallButton(cx, row3Y, btnW, btnH, "-10", () => _craftAmount = Math.Max(1, _craftAmount - 10)); cx += btnW + gap;
            DrawSmallButton(cx, row3Y, btnW, btnH, "-100", () => _craftAmount = Math.Max(1, _craftAmount - 100)); cx += btnW + gap;
            DrawSmallButton(cx, row3Y, btnW, btnH, "Reset", () => _craftAmount = 1);

            // Clamp amount
            if (_craftAmount > maxCraftable)
                _craftAmount = maxCraftable;
            if (_craftAmount < 1)
                _craftAmount = 1;

            // Row 4: Craft button (full width)
            int row4Y = row3Y + btnH + 4;
            int craftBtnW = width - 10;
            int craftBtnH = 24;
            bool craftHover = WidgetInput.IsMouseOver(x + 5, row4Y, craftBtnW, craftBtnH);
            Color4 craftBtnColor = isRecursive ? UIColors.Info : UIColors.Success;
            UIRenderer.DrawRect(x + 5, row4Y, craftBtnW, craftBtnH,
                craftHover ? craftBtnColor : craftBtnColor.WithAlpha(180));
            string craftLabel = isRecursive ? "RECURSIVE CRAFT" : "CRAFT";
            int craftTextW = TextUtil.MeasureWidth(craftLabel);
            UIRenderer.DrawText(craftLabel, x + 5 + (craftBtnW - craftTextW) / 2, row4Y + 6, UIColors.Text);

            if (craftHover && WidgetInput.MouseLeftClick)
            {
                OnCraftClicked();
                WidgetInput.ConsumeClick();
            }
        }

        private void DrawSmallButton(int x, int y, int width, int height, string text, Action onClick)
        {
            bool hover = WidgetInput.IsMouseOver(x, y, width, height);
            UIRenderer.DrawRect(x, y, width, height,
                hover ? UIColors.ButtonHover : UIColors.Button);

            // Center text horizontally and vertically (font height ~12px)
            int textW = TextUtil.MeasureWidth(text);
            int textX = x + (width - textW) / 2;
            int textY = y + (height - 12) / 2;
            if (textX < x + 2) textX = x + 2;
            UIRenderer.DrawText(text, textX, textY, UIColors.TextDim);

            if (hover && WidgetInput.MouseLeftClick)
            {
                onClick();
                WidgetInput.ConsumeClick();
            }
        }


        private void OnCraftClicked()
        {
            if (_selectedRecipeIndex < 0 || _selectedRecipeIndex >= _filteredRecipes.Count)
                return;

            var result = _filteredRecipes[_selectedRecipeIndex];
            bool useRecursive = _modConfig.Get<bool>("recursiveCrafting", true);

            // Must be directly craftable, or recursively craftable for MissingMaterials
            if (result.Status != CraftStatus.Craftable
                && !(useRecursive && result.Status == CraftStatus.MissingMaterials))
                return;

            // Calculate actual craft count
            int maxCraftable = GetEffectiveMaxCraftable();
            int actualCount = Math.Min(_craftAmount, maxCraftable);
            if (actualCount <= 0) return;

            bool success;
            int totalOutput;

            if (useRecursive)
            {
                // Use RecursiveCrafter to build and execute crafting plan
                int depth = _modConfig.Get<int>("recursiveCraftingDepth", 0);
                var plan = _crafter.CalculatePlan(result.Recipe.OriginalIndex, actualCount, depth);
                if (plan == null)
                {
                    _toastMessage = $"Failed to plan craft for {result.Recipe.OutputName}";
                    _toastIsError = true;
                    _toastTimer = ToastDuration;
                    return;
                }

                if (!plan.CanCraft)
                {
                    _toastMessage = plan.ErrorMessage ?? "Cannot craft (missing materials)";
                    _toastIsError = true;
                    _toastTimer = ToastDuration;
                    return;
                }

                success = _crafter.ExecutePlan(plan);
                // Use long to prevent overflow
                long totalOutputLong = (long)plan.TargetCount * result.Recipe.OutputStack;
                totalOutput = totalOutputLong > int.MaxValue ? int.MaxValue : (int)totalOutputLong;
            }
            else
            {
                // Simple (non-recursive) crafting - just execute the single recipe
                success = _executor.ExecuteCraft(result.Recipe, actualCount);
                // Use long to prevent overflow
                long totalOutputLong = (long)actualCount * result.Recipe.OutputStack;
                totalOutput = totalOutputLong > int.MaxValue ? int.MaxValue : (int)totalOutputLong;
            }

            if (success)
            {
                _toastMessage = $"Crafted {totalOutput}x {result.Recipe.OutputName}";
                _toastIsError = false;
                _toastTimer = ToastDuration;
            }
            else
            {
                _toastMessage = $"Failed to craft {result.Recipe.OutputName}";
                _toastIsError = true;
                _toastTimer = ToastDuration;
            }

            // Refresh after crafting
            MarkDirty();

            // Notify parent that storage was modified (for Items tab refresh)
            OnStorageModified?.Invoke();
        }

        private void RefreshRecipes()
        {
            _log.Debug("[CraftTab] RefreshRecipes called");
            _checker.RefreshMaterials();
            _checker.RefreshStations();

            // Load item traits from ContentSamples (once)
            if (_itemTraits == null)
            {
                _itemTraits = ItemSearchTraitsBuilder.GetAllFromContentSamples(_log);
            }

            // Always get directly craftable recipes first
            _craftableRecipes = _checker.GetCraftableRecipes();
            CraftableCount = _craftableRecipes.Count;

            if (_showPartial)
            {
                // "Missing Ingredients" view: show partial recipes (have some but not all materials)
                // Exclude recipes that are recursively craftable (those belong in the Craftable view)
                var partial = _checker.GetPartialRecipes();

                if (_modConfig.Get<bool>("recursiveCrafting", true))
                {
                    var recursiveSet = new HashSet<int>();
                    foreach (var r in GetRecursiveCandidates())
                        recursiveSet.Add(r.Recipe.OriginalIndex);
                    partial.RemoveAll(p => recursiveSet.Contains(p.Recipe.OriginalIndex));
                }

                _log.Debug($"[CraftTab] Found {_craftableRecipes.Count} craftable, {partial.Count} partial recipes");
                _craftableRecipes.AddRange(partial);
            }
            else
            {
                // "Craftable" view: directly craftable + recursively craftable
                if (_modConfig.Get<bool>("recursiveCrafting", true))
                {
                    var recursiveCandidates = GetRecursiveCandidates();
                    if (recursiveCandidates.Count > 0)
                    {
                        _log.Debug($"[CraftTab] Found {recursiveCandidates.Count} recursive candidate recipes");
                        _craftableRecipes.AddRange(recursiveCandidates);
                    }
                }
                _log.Debug($"[CraftTab] Found {_craftableRecipes.Count} craftable recipes (including recursive)");
            }

            FilterRecipes();
        }

        /// <summary>
        /// Find recipes that can be crafted through recursive sub-steps.
        /// Validates each candidate with CalculatePlan to ensure the full chain is feasible
        /// (has raw materials, stations available, etc.) rather than just checking recipe existence.
        /// </summary>
        private List<CraftabilityResult> GetRecursiveCandidates()
        {
            var results = new List<CraftabilityResult>();

            // Track existing recipes to avoid duplicates
            var existing = new HashSet<int>();
            foreach (var r in _craftableRecipes)
                existing.Add(r.Recipe.OriginalIndex);

            int depth = _modConfig.Get<int>("recursiveCraftingDepth", 0);

            foreach (var recipe in _recipeIndex.GetAllRecipes())
            {
                if (existing.Contains(recipe.OriginalIndex)) continue;

                var result = _checker.CanCraft(recipe);
                if (result.Status != CraftStatus.MissingMaterials) continue;
                if (result.MissingMaterials.Count == 0) continue;

                // Main recipe must have stations and environment available
                // (Status=MissingMaterials takes priority over MissingStation, so check explicitly)
                if ((result.MissingStations != null && result.MissingStations.Count > 0) ||
                    (result.MissingEnvironment != null && result.MissingEnvironment.Count > 0))
                    continue;

                // Quick pre-filter: all missing materials must have at least one sub-recipe
                bool allHaveSubRecipes = true;
                foreach (var missing in result.MissingMaterials)
                {
                    if (!HasSubRecipeFor(missing.ItemId, recipe))
                    {
                        allHaveSubRecipes = false;
                        break;
                    }
                }
                if (!allHaveSubRecipes) continue;

                // Full validation: run CalculatePlan to verify the recursive chain is feasible
                // (checks raw material availability, station access for sub-recipes, virtual pool)
                var plan = _crafter.CalculatePlan(recipe.OriginalIndex, 1, depth);
                if (plan != null && plan.CanCraft)
                    results.Add(result);
            }

            return results;
        }

        /// <summary>
        /// Check if a missing material can be sub-crafted.
        /// Handles recipe groups by checking each valid item in the group.
        /// </summary>
        private bool HasSubRecipeFor(int itemId, RecipeInfo recipe)
        {
            // Recipe group: fake ID >= FakeItemIdOffset — check each valid item
            if (_recipeIndex.HasRecipeGroupSupport && itemId >= _recipeIndex.FakeItemIdOffset)
            {
                foreach (var ing in recipe.Ingredients)
                {
                    if (ing.ItemId == itemId && ing.IsRecipeGroup && ing.ValidItemIds != null)
                    {
                        foreach (int validId in ing.ValidItemIds)
                        {
                            if (_recipeIndex.GetRecipesByOutput(validId).Count > 0)
                                return true;
                        }
                        return false;
                    }
                }
                return false;
            }

            return _recipeIndex.GetRecipesByOutput(itemId).Count > 0;
        }

        private void FilterRecipes()
        {
            var query = MagicSearchQuery.Parse(_searchBar.Text);
            _filteredRecipes = new List<CraftabilityResult>();

            foreach (var result in _craftableRecipes)
            {
                var traits = GetTraitsForItem(result.Recipe.OutputItemId);

                // Apply text/tag search
                if (!query.Matches(result.Recipe.OutputName, tag => MagicSearchQuery.MatchesTag(traits, tag, false)))
                    continue;

                // Apply category filter
                if (_categoryFilter != CategoryFilter.All && traits.PrimaryCategory != _categoryFilter)
                    continue;

                _filteredRecipes.Add(result);
            }

            // Apply sorting
            int dir = _craftSortAscending ? 1 : -1;
            switch (_craftSort)
            {
                case CraftSortMode.Name:
                    _filteredRecipes.Sort((a, b) => dir * string.Compare(
                        a.Recipe.OutputName, b.Recipe.OutputName, StringComparison.OrdinalIgnoreCase));
                    break;
                case CraftSortMode.Max:
                    _filteredRecipes.Sort((a, b) =>
                    {
                        int cmp = dir * a.MaxCraftable.CompareTo(b.MaxCraftable);
                        if (cmp != 0) return cmp;
                        return string.Compare(a.Recipe.OutputName, b.Recipe.OutputName, StringComparison.OrdinalIgnoreCase);
                    });
                    break;
                case CraftSortMode.Status:
                    _filteredRecipes.Sort((a, b) =>
                    {
                        int cmp = dir * a.Status.CompareTo(b.Status);
                        if (cmp != 0) return cmp;
                        return string.Compare(a.Recipe.OutputName, b.Recipe.OutputName, StringComparison.OrdinalIgnoreCase);
                    });
                    break;
                case CraftSortMode.Type:
                    _filteredRecipes.Sort((a, b) =>
                    {
                        int cmp = dir * a.Recipe.OutputItemId.CompareTo(b.Recipe.OutputItemId);
                        if (cmp != 0) return cmp;
                        return string.Compare(a.Recipe.OutputName, b.Recipe.OutputName, StringComparison.OrdinalIgnoreCase);
                    });
                    break;
            }

            // Restore selection by stable recipe identity (not list position)
            RestoreSelection();
        }

        private ItemSearchTraits GetTraitsForItem(int itemId)
        {
            if (_itemTraits != null && _itemTraits.TryGetValue(itemId, out var traits))
                return traits;
            return ItemSearchTraits.Default;
        }

        private enum CraftSortMode
        {
            Name,   // Alphabetical by output name
            Max,    // By max craftable count
            Status, // By craftability status
            Type    // By output item type ID
        }
    }
}
