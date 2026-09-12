using System;
using System.Collections.Generic;
using Terraria;
using TerrariaModder.Core.UI;
using TerrariaModder.Core.Logging;
using StorageHub.Storage;
using StorageHub.Config;
using StorageHub.Relay;
using TerrariaModder.Core.UI.Widgets;
using StorageHub.PaintingChest;

namespace StorageHub.UI.Tabs
{
    /// <summary>
    /// Unlocks tab - shows tier progression and special unlocks.
    ///
    /// Features:
    /// - Current tier and range info with station memory toggle
    /// - Relay positions
    /// - Upgrade buttons for tier and special unlocks
    /// </summary>
    public class InfoTab
    {
        private readonly ILogger _log;
        private readonly IStorageProvider _storage;
        private readonly StorageHubConfig _config;
        private readonly RangeCalculator _rangeCalc;
        private readonly StorageCostConsumer _itemConsumer;

        // UI components
        private readonly ScrollView _scrollView = new ScrollView();

        // Layout
        private const int SectionHeight = 28;
        private const int LineHeight = 20;

        // Cached item counts for upgrade checks
        private Dictionary<int, int> _itemCounts = new Dictionary<int, int>();
        private int _chestItemCount;
        private int _chestIconItemId = -1;
        private bool _needsRefresh = true;

        /// <summary>
        /// Callback when storage is modified (tier upgrade/unlock consumed items).
        /// Parent UI should refresh storage data when this is called.
        /// </summary>
        public Action OnStorageModified { get; set; }

        public InfoTab(ILogger log, IStorageProvider storage, StorageHubConfig config, RangeCalculator rangeCalc)
        {
            _log = log;
            _storage = storage;
            _config = config;
            _rangeCalc = rangeCalc;
            _itemConsumer = new StorageCostConsumer(log, storage, GetAccessibleItems);
        }

        public void MarkDirty()
        {
            _needsRefresh = true;
        }

        // Cached bounds for clipping
        private int _clipTop;
        private int _clipBottom;

        /// <summary>
        /// Draw the info tab.
        /// </summary>
        public void Draw(int x, int y, int width, int height)
        {
            ItemTooltip.Clear();

            // Refresh item counts if needed
            if (_needsRefresh)
            {
                RefreshItemCounts();
                _needsRefresh = false;
            }

            int contentHeight = CalculateContentHeight();
            _scrollView.Begin(x, y, width, height, contentHeight);

            // Store clip bounds for drawing methods
            _clipTop = y;
            _clipBottom = y + height;

            // Get content width (accounts for scrollbar if needed)
            int contentWidth = _scrollView.ContentWidth;

            // Start drawing at base Y, adjusted by scroll offset
            // Content at drawY=0 should appear at screen position y when scrollOffset=0
            // As user scrolls down (scrollOffset increases), content moves up (subtract from Y)
            int drawY = y - _scrollView.ScrollOffset;

            // Tier section (includes station memory toggle)
            drawY = DrawTierSection(x, drawY, contentWidth);

            // Painting chest section (only if feature enabled)
            if (PaintingChestManager.Enabled)
            {
                drawY = DrawPaintingChestSection(x, drawY, contentWidth);
            }

            // Relays section
            drawY = DrawRelaysSection(x, drawY, contentWidth);

            // Special unlocks section
            drawY = DrawUnlocksSection(x, drawY, contentWidth);

            _scrollView.End();

            // Deferred item tooltip (drawn last, on top of everything)
            ItemTooltip.DrawDeferred();
        }

        /// <summary>
        /// Check if a Y position is visible within the clipped area.
        /// </summary>
        private bool IsVisible(int drawY, int itemHeight = 20)
        {
            return drawY >= _clipTop && drawY + itemHeight <= _clipBottom;
        }

        private int CalculateContentHeight()
        {
            int height = 0;

            // Tier: header + 4 lines + station memory toggle (if tier 3+) + upgrade button + spacing
            int tierLines = 4; // tier, range, station memory status, relay count
            if (ProgressionTier.HasStationMemory(_config.Tier))
                tierLines++; // extra line for toggle button
            height += SectionHeight + LineHeight * tierLines + 40 + 20;

            // Painting chest section (if enabled)
            if (PaintingChestManager.Enabled)
            {
                // header + capacity line + upgrade row or max text + spacing
                height += SectionHeight + LineHeight + 44 + 20;
            }

            // Relays: header + relay lines + spacing
            height += SectionHeight + Math.Max(1, _config.Relays.Count) * LineHeight + 20;

            // Unlocks: header + unlock rows (50px each with icon)
            height += SectionHeight + SpecialUnlocks.Definitions.Count * 50 + 20;

            return height;
        }

        private int DrawTierSection(int x, int y, int width)
        {
            // Section header
            if (IsVisible(y, SectionHeight))
            {
                UIRenderer.DrawRect(x, y, width, SectionHeight, UIColors.HeaderBg);
                UIRenderer.DrawText("Tier Status", x + 10, y + 6, UIColors.TextTitle);
            }
            y += SectionHeight + 5;

            // Current tier
            if (IsVisible(y, LineHeight))
            {
                string tierName = ProgressionTier.GetTierName(_config.Tier);
                UIRenderer.DrawText($"Current Tier: {_config.Tier} ({tierName})", x + 10, y, UIColors.TextDim);
            }
            y += LineHeight;

            // Range
            if (IsVisible(y, LineHeight))
            {
                int range = ProgressionTier.GetRange(_config.Tier);
                string rangeText = range == int.MaxValue ? "Entire World" : $"{range / 16} tiles";
                UIRenderer.DrawText($"Base Range: {rangeText}", x + 10, y, UIColors.TextDim);
            }
            y += LineHeight;

            // Station memory — interactive toggle if tier 3+, otherwise status text
            bool hasMemory = ProgressionTier.HasStationMemory(_config.Tier);
            if (hasMemory)
            {
                // Show toggle button inline
                const int ToggleBtnWidth = 50;
                const int ToggleBtnHeight = 20;
                bool isEnabled = _config.StationMemoryEnabled;

                if (IsVisible(y, LineHeight))
                {
                    UIRenderer.DrawText("Station Memory:", x + 10, y + 2, UIColors.TextDim);

                    int btnX = x + 140;
                    bool hover = WidgetInput.IsMouseOver(btnX, y, ToggleBtnWidth, ToggleBtnHeight);

                    UIRenderer.DrawRect(btnX, y, ToggleBtnWidth, ToggleBtnHeight,
                        isEnabled ? (hover ? UIColors.ButtonHover : UIColors.Button)
                                  : (hover ? UIColors.CloseBtnHover : UIColors.CloseBtn));
                    UIRenderer.DrawText(isEnabled ? "ON" : "OFF", btnX + (isEnabled ? 16 : 14), y + 2,
                        isEnabled ? UIColors.Success : UIColors.Error);

                    if (hover && WidgetInput.MouseLeftClick)
                    {
                        _config.StationMemoryEnabled = !_config.StationMemoryEnabled;
                        _config.Save();
                        _log.Info($"Station memory toggled: {(_config.StationMemoryEnabled ? "ON" : "OFF")}");
                        WidgetInput.ConsumeClick();
                    }
                }
                y += LineHeight;
            }
            else
            {
                if (IsVisible(y, LineHeight))
                {
                    UIRenderer.DrawText("Station Memory: Requires Tier 3", x + 10, y, UIColors.TextHint);
                }
                y += LineHeight;
            }

            // Relay count
            if (IsVisible(y, LineHeight))
            {
                UIRenderer.DrawText($"Active Relays: {_config.Relays.Count}/{RelayConstants.MaxRelays}", x + 10, y, UIColors.TextDim);
            }
            y += LineHeight + 10;

            // Upgrade button with item icons
            var nextTier = ProgressionTier.GetNextTierRequirement(_config.Tier);
            if (nextTier != null)
            {
                const int IconSize = 28;
                int btnWidth = 200;
                int btnHeight = 34;

                // Check if player has enough items
                int availableCount = CountItemsForTier(nextTier.AcceptedItemIds);
                bool canUpgrade = availableCount >= nextTier.RequiredCount;

                // Only process input if button is visible
                bool hover = IsVisible(y, btnHeight) && WidgetInput.IsMouseOver(x + 10, y, btnWidth, btnHeight);

                if (IsVisible(y, btnHeight))
                {
                    UIRenderer.DrawRect(x + 10, y, btnWidth, btnHeight,
                        canUpgrade ? (hover ? UIColors.ButtonHover : UIColors.Button) : UIColors.SectionBg);
                    UIRenderer.DrawText($"Upgrade to Tier {nextTier.TargetTier}", x + 20, y + 10,
                        canUpgrade ? UIColors.Text : UIColors.TextHint);

                    // Show requirements with item icons
                    int reqX = x + btnWidth + 20;

                    // Draw accepted item icons (show all for dual items like Shadow Scale/Tissue Sample)
                    for (int i = 0; i < nextTier.AcceptedItemIds.Length; i++)
                    {
                        int iconX = reqX + i * (IconSize + 4);
                        UIRenderer.DrawItem(nextTier.AcceptedItemIds[i], iconX, y + 3, IconSize, IconSize);
                        if (WidgetInput.IsMouseOver(iconX, y + 3, IconSize, IconSize))
                            ItemTooltip.Set(nextTier.AcceptedItemIds[i]);
                    }

                    // Count text after icons
                    int countX = reqX + nextTier.AcceptedItemIds.Length * (IconSize + 4) + 8;
                    UIRenderer.DrawText($"x{nextTier.RequiredCount} ({availableCount})", countX, y + 10,
                        canUpgrade ? UIColors.Success : UIColors.Error);
                }

                if (hover && canUpgrade && WidgetInput.MouseLeftClick)
                {
                    // Consume items and upgrade
                    if (ConsumeCost(nextTier.AcceptedItemIds, nextTier.RequiredCount))
                    {
                        _config.Tier = nextTier.TargetTier;
                        _config.Save();
                        _log.Info($"Upgraded to Tier {nextTier.TargetTier}!");
                        _needsRefresh = true;
                        OnStorageModified?.Invoke();
                    }
                    WidgetInput.ConsumeClick();
                }

                y += btnHeight + 10;
            }
            else
            {
                if (IsVisible(y, 30))
                {
                    UIRenderer.DrawText("Max Tier Reached!", x + 10, y, UIColors.Success);
                }
                y += 30;
            }

            return y + 10;
        }

        private int CountItemsForTier(int[] acceptedItemIds)
        {
            long total = 0;
            foreach (int itemId in acceptedItemIds)
            {
                if (_itemCounts.TryGetValue(itemId, out int count))
                {
                    total += count;
                    if (total > int.MaxValue) return int.MaxValue;
                }
            }
            return (int)total;
        }

        private int DrawRelaysSection(int x, int y, int width)
        {
            // Section header
            if (IsVisible(y, SectionHeight))
            {
                UIRenderer.DrawRect(x, y, width, SectionHeight, UIColors.HeaderBg);
                UIRenderer.DrawText($"Relays ({_config.Relays.Count}/{RelayConstants.MaxRelays})", x + 10, y + 6, UIColors.TextTitle);
            }
            y += SectionHeight + 5;

            if (_config.Relays.Count == 0)
            {
                if (IsVisible(y, LineHeight))
                {
                    UIRenderer.DrawText("No relays placed. Relays extend your range to distant areas.", x + 10, y, UIColors.TextHint);
                }
                y += LineHeight;
            }
            else
            {
                if (IsVisible(y, LineHeight))
                {
                    int relayRange = _rangeCalc.GetRelayRangeTiles();
                    UIRenderer.DrawText($"Each relay adds {relayRange} tile radius", x + 10, y, UIColors.TextDim);
                }
                y += LineHeight;

                foreach (var relay in _config.Relays)
                {
                    if (IsVisible(y, LineHeight))
                    {
                        UIRenderer.DrawText($"  Relay at ({relay.X}, {relay.Y})", x + 10, y, UIColors.Info);
                    }
                    y += LineHeight;
                }
            }

            return y + 10;
        }

        private int DrawUnlocksSection(int x, int y, int width)
        {
            const int IconSize = 24;
            const int RowHeight = 50; // Increased for icon display

            // Section header
            if (IsVisible(y, SectionHeight))
            {
                UIRenderer.DrawRect(x, y, width, SectionHeight, UIColors.HeaderBg);
                UIRenderer.DrawText("Special Unlocks", x + 10, y + 6, UIColors.TextTitle);
            }
            y += SectionHeight + 5;

            foreach (var kvp in SpecialUnlocks.Definitions)
            {
                var unlock = kvp.Value;
                bool isUnlocked = _config.HasSpecialUnlock(kvp.Key);
                // Sum counts across all accepted item IDs
                int availableCount = 0;
                foreach (int itemId in unlock.AcceptedItemIds)
                {
                    if (_itemCounts.TryGetValue(itemId, out int count))
                        availableCount += count;
                }
                bool canUnlock = !isUnlocked && availableCount >= unlock.RequiredCount;

                // Background for this unlock row
                if (IsVisible(y, RowHeight))
                {
                    UIRenderer.DrawRect(x, y, width, RowHeight - 2,
                        isUnlocked ? UIColors.SectionBg.WithAlpha(200) : (canUnlock ? UIColors.ItemBg.WithAlpha(200) : UIColors.PanelBg.WithAlpha(200)));
                }

                // Row 1: Icon + Name + Count/Button on same line
                int row1Y = y + 4;

                if (IsVisible(row1Y, IconSize))
                {
                    // Draw required item icon
                    UIRenderer.DrawItem(unlock.RequiredItemId, x + 8, row1Y, IconSize, IconSize);
                    if (WidgetInput.IsMouseOver(x + 8, row1Y, IconSize, IconSize))
                        ItemTooltip.Set(unlock.RequiredItemId);

                    // Unlock name (after icon, truncated if needed)
                    string displayName = unlock.DisplayName;
                    int maxNameWidth = width - 220; // Leave room for button + count
                    UIRenderer.DrawText(displayName, x + 8 + IconSize + 8, row1Y + 3, UIColors.AccentText);
                }

                // Layout: [Count] [Button] from right edge
                // Count on left, button on right
                int btnWidth = 80;
                int btnHeight = 22;
                int btnX = x + width - btnWidth - 10;
                int countX = btnX - 70; // Position count to the left of button
                bool hover = IsVisible(row1Y, btnHeight) && !isUnlocked && WidgetInput.IsMouseOver(btnX, row1Y, btnWidth, btnHeight);

                if (IsVisible(row1Y, btnHeight))
                {
                    // Draw count first (left of button)
                    if (!isUnlocked)
                    {
                        UIRenderer.DrawText($"{availableCount}/{unlock.RequiredCount}", countX, row1Y + 4,
                            canUnlock ? UIColors.Success : UIColors.Error);
                    }

                    // Draw button
                    if (isUnlocked)
                    {
                        UIRenderer.DrawRect(btnX, row1Y, btnWidth, btnHeight, UIColors.Button);
                        UIRenderer.DrawText("Unlocked", btnX + 10, row1Y + 4, UIColors.Success);
                    }
                    else
                    {
                        UIRenderer.DrawRect(btnX, row1Y, btnWidth, btnHeight,
                            canUnlock ? (hover ? UIColors.ButtonHover : UIColors.Button) : UIColors.SectionBg);
                        UIRenderer.DrawText("Unlock", btnX + 18, row1Y + 4,
                            canUnlock ? UIColors.Text : UIColors.TextHint);
                    }
                }

                // Handle unlock click
                if (!isUnlocked && hover && canUnlock && WidgetInput.MouseLeftClick)
                {
                    _log.Info($"[InfoTab] Attempting to unlock '{kvp.Key}': AcceptedItemIds=[{string.Join(",", unlock.AcceptedItemIds)}], RequiredCount={unlock.RequiredCount}, AvailableCount={availableCount}");
                    if (ConsumeCost(unlock.AcceptedItemIds, unlock.RequiredCount))
                    {
                        _config.SetSpecialUnlock(kvp.Key, true);
                        _config.Save();
                        _log.Info($"[InfoTab] Unlocked: {unlock.DisplayName}");
                        _needsRefresh = true;
                        OnStorageModified?.Invoke();
                    }
                    else
                    {
                        _log.Warn($"[InfoTab] Failed to consume items for unlock '{kvp.Key}'");
                    }
                    WidgetInput.ConsumeClick();
                }

                // Row 2: Description
                int row2Y = y + 28;
                if (IsVisible(row2Y, LineHeight))
                {
                    int descMaxW = width - IconSize - 24;
                    UIRenderer.DrawText(TextUtil.Truncate(unlock.Description, descMaxW), x + 8 + IconSize + 8, row2Y, UIColors.TextHint);
                }

                y += RowHeight;
            }

            return y + 10;
        }

        private int DrawPaintingChestSection(int x, int y, int width)
        {
            const int IconSize = 24;
            const int RowHeight = 34;

            // Section header
            if (IsVisible(y, SectionHeight))
            {
                UIRenderer.DrawRect(x, y, width, SectionHeight, UIColors.HeaderBg);
                UIRenderer.DrawText("Mysterious Chest", x + 10, y + 6, UIColors.TextTitle);
            }
            y += SectionHeight + 5;

            // Current capacity
            int currentLevel = _config.PaintingChestLevel;
            int capacity = PaintingChestProgression.GetCapacity(currentLevel);
            if (IsVisible(y, LineHeight))
            {
                UIRenderer.DrawText($"Current Capacity: {capacity} slots", x + 10, y, UIColors.TextDim);
            }
            y += LineHeight + 5;

            // Next tier upgrade
            var nextTier = PaintingChestProgression.GetNextTier(currentLevel);
            if (nextTier != null)
            {
                bool meetsGameTier = _config.Tier >= nextTier.RequiredGameTier;
                bool hasEnough = _chestItemCount >= nextTier.RequiredCount;
                bool canUnlock = meetsGameTier && hasEnough;

                int btnWidth = 80;
                int btnHeight = 22;
                int btnX = x + width - btnWidth - 10;
                bool hover = IsVisible(y, RowHeight) && canUnlock && WidgetInput.IsMouseOver(btnX, y + 4, btnWidth, btnHeight);

                if (IsVisible(y, RowHeight))
                {
                    // Draw chest icon
                    if (_chestIconItemId > 0)
                    {
                        UIRenderer.DrawItem(_chestIconItemId, x + 8, y + 4, IconSize, IconSize);
                        if (WidgetInput.IsMouseOver(x + 8, y + 4, IconSize, IconSize))
                            ItemTooltip.Set(_chestIconItemId);
                    }

                    // Upgrade text
                    int textX = x + 8 + IconSize + 8;
                    UIRenderer.DrawText($"{nextTier.RequiredCount} chests needed to unlock next level ({nextTier.Capacity} Slots)",
                        textX, y + 8, meetsGameTier ? UIColors.AccentText : UIColors.TextHint);

                    // Count or tier requirement
                    int countX = btnX - 70;
                    if (meetsGameTier)
                    {
                        UIRenderer.DrawText($"{_chestItemCount}/{nextTier.RequiredCount}", countX, y + 8,
                            hasEnough ? UIColors.Success : UIColors.Error);
                    }
                    else
                    {
                        UIRenderer.DrawText($"(Tier {nextTier.RequiredGameTier}+)", countX, y + 8, UIColors.TextHint);
                    }

                    // Button
                    UIRenderer.DrawRect(btnX, y + 4, btnWidth, btnHeight,
                        canUnlock ? (hover ? UIColors.ButtonHover : UIColors.Button) : UIColors.SectionBg);
                    UIRenderer.DrawText("Unlock", btnX + 18, y + 8,
                        canUnlock ? UIColors.Text : UIColors.TextHint);
                }

                if (hover && canUnlock && WidgetInput.MouseLeftClick)
                {
                    var chestItemIds = PaintingChestProgression.GetChestItemIds();
                    if (ConsumeCost(chestItemIds, nextTier.RequiredCount))
                    {
                        _config.PaintingChestLevel = nextTier.TargetLevel;
                        _config.Save();
                        int newCapacity = PaintingChestProgression.GetCapacity(nextTier.TargetLevel);
                        PaintingChestManager.ResizeAllPaintingChests(newCapacity);
                        _log.Info($"Painting chest upgraded to level {nextTier.TargetLevel} ({newCapacity} slots)");
                        _needsRefresh = true;
                        OnStorageModified?.Invoke();
                    }
                    WidgetInput.ConsumeClick();
                }

                y += RowHeight;
            }
            else
            {
                if (IsVisible(y, LineHeight))
                {
                    UIRenderer.DrawText("Maximum capacity reached", x + 10, y, UIColors.Success);
                }
                y += LineHeight;
            }

            return y + 10;
        }

        private bool ConsumeCost(int[] acceptedItemIds, int requiredCount)
        {
            // A rejected stale click must refresh the displayed count as well.
            _needsRefresh = true;
            return _itemConsumer.ConsumeItems(acceptedItemIds, requiredCount);
        }

        private List<ItemSnapshot> GetAccessibleItems()
        {
            var items = _storage.GetAllItems();
            if (_rangeCalc == null) return items;
            _rangeCalc.UpdatePlayerPosition();
            return _rangeCalc.FilterItemsByRange(items);
        }

        private void RefreshItemCounts()
        {
            _itemCounts.Clear();

            // Count items in range only (out-of-range items can't be consumed for upgrades)
            var allItems = GetAccessibleItems();
            _log.Debug($"[InfoTab] RefreshItemCounts: Scanning {allItems.Count} in-range items from storage");

            foreach (var item in allItems)
            {
                if (item.IsEmpty) continue;

                if (_itemCounts.ContainsKey(item.ItemId))
                {
                    // Use long to prevent overflow
                    long newCount = (long)_itemCounts[item.ItemId] + item.Stack;
                    _itemCounts[item.ItemId] = newCount > int.MaxValue ? int.MaxValue : (int)newCount;
                }
                else
                    _itemCounts[item.ItemId] = item.Stack;
            }

            // Count chest items for painting chest upgrades
            if (PaintingChestManager.Enabled)
            {
                _chestItemCount = PaintingChestProgression.CountChestItems(_itemCounts);
                var chestIds = PaintingChestProgression.GetChestItemIds();
                _chestIconItemId = chestIds.Length > 0 ? chestIds[0] : -1;
            }

            // Log counts for unlock items for debugging
            foreach (var kvp in SpecialUnlocks.Definitions)
            {
                int reqItemId = kvp.Value.RequiredItemId;
                int count = _itemCounts.TryGetValue(reqItemId, out int c) ? c : 0;
                if (count > 0)
                {
                    _log.Debug($"[InfoTab] Unlock '{kvp.Key}': RequiredItemId={reqItemId} ({kvp.Value.DisplayName}), have={count}, need={kvp.Value.RequiredCount}");
                }
            }
        }
    }

}
