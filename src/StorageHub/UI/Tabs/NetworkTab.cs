using System;
using System.Collections.Generic;
using TerrariaModder.Core.UI;
using TerrariaModder.Core.Logging;
using StorageHub.Storage;
using StorageHub.Config;
using StorageHub.Crafting;
using StorageHub.Relay;
using TerrariaModder.Core.UI.Widgets;

namespace StorageHub.UI.Tabs
{
    /// <summary>
    /// Network tab - shows crafting station availability and connected storage drives.
    ///
    /// Sections:
    /// A) Crafting Stations — 34 tile stations + 5 environment conditions
    /// B) Connected Storage Drives — with ping, deregister, range status
    /// </summary>
    public class NetworkTab
    {
        private readonly ILogger _log;
        private readonly StorageHubConfig _config;
        private readonly CraftabilityChecker _checker;
        private readonly IStorageProvider _storage;
        private readonly ChestRegistry _registry;
        private readonly ChestPinger _pinger;
        private readonly RangeCalculator _rangeCalc;

        // UI components
        private readonly ScrollView _scrollView = new ScrollView();

        // Layout constants
        private const int SectionHeight = 28;
        private const int StationRowHeight = 24;
        private const int ChestRowHeight = 28;
        private const int LineHeight = 20;
        private const int DotSize = 10;

        // State
        private bool _needsRefresh = true;
        private int _confirmingChestIndex = -1;
        private int _confirmTimer = 0;
        private const int ConfirmTimeout = 120; // 2 seconds at 60fps

        // Cached data
        private int _availableStationCount;
        private int _availableEnvironmentCount;

        // Tile stations to display (excludes 355 and 699 which are equivalence-only)
        private static readonly int[] DisplayStationTiles = {
            13, 16, 17, 18, 26, 77, 86, 94, 96, 101,
            106, 114, 125, 133, 134, 215, 217, 218, 220, 228,
            243, 247, 283, 300, 301, 302, 303, 304, 305, 306,
            307, 308, 412, 622
        };

        // Environment condition names
        private static readonly string[] EnvironmentNames = { "Water", "Honey", "Lava", "Snow Biome", "Graveyard" };

        /// <summary>
        /// Callback when storage is modified (storage node deregistered).
        /// </summary>
        public Action OnStorageModified { get; set; }

        public NetworkTab(ILogger log, StorageHubConfig config, CraftabilityChecker checker,
            IStorageProvider storage, ChestRegistry registry, ChestPinger pinger, RangeCalculator rangeCalc)
        {
            _log = log;
            _config = config;
            _checker = checker;
            _storage = storage;
            _registry = registry;
            _pinger = pinger;
            _rangeCalc = rangeCalc;
        }

        public void MarkDirty()
        {
            _needsRefresh = true;
        }

        // Cached clip bounds
        private int _clipTop;
        private int _clipBottom;

        public void Draw(int x, int y, int width, int height)
        {
            if (_needsRefresh)
            {
                RefreshCounts();
                _needsRefresh = false;
            }

            // Update confirm timer
            if (_confirmTimer > 0)
            {
                _confirmTimer--;
                if (_confirmTimer <= 0)
                    _confirmingChestIndex = -1;
            }

            int contentHeight = CalculateContentHeight();
            _scrollView.Begin(x, y, width, height, contentHeight);

            _clipTop = y;
            _clipBottom = y + height;

            int contentWidth = _scrollView.ContentWidth;
            int drawY = y - _scrollView.ScrollOffset;

            drawY = DrawStationsSection(x, drawY, contentWidth);
            drawY = DrawChestsSection(x, drawY, contentWidth);

            _scrollView.End();
        }

        private bool IsVisible(int drawY, int itemHeight = 20)
        {
            return drawY + itemHeight > _clipTop && drawY < _clipBottom;
        }

        private void RefreshCounts()
        {
            _availableStationCount = 0;
            foreach (int tileId in DisplayStationTiles)
            {
                if (_checker.IsStationAvailable(tileId))
                    _availableStationCount++;
            }

            _availableEnvironmentCount = 0;
            var env = _checker.EnvironmentState;
            if (env.HasWater || _config.HasSpecialUnlock("water")) _availableEnvironmentCount++;
            if (env.HasHoney || _config.HasSpecialUnlock("honey")) _availableEnvironmentCount++;
            if (env.HasLava || _config.HasSpecialUnlock("lava")) _availableEnvironmentCount++;
            if (env.InSnow || _config.HasSpecialUnlock("snow")) _availableEnvironmentCount++;
            if (env.InGraveyard || _config.HasSpecialUnlock("graveyard")) _availableEnvironmentCount++;
        }

        private int CalculateContentHeight()
        {
            int height = 0;

            // Stations header + spacing
            height += SectionHeight + 5;

            // Station rows
            if (_config.ShowStationSpoilers)
            {
                height += (DisplayStationTiles.Length + EnvironmentNames.Length) * StationRowHeight;
            }
            else
            {
                height += (_availableStationCount + _availableEnvironmentCount) * StationRowHeight;
                if (_availableStationCount + _availableEnvironmentCount == 0)
                    height += LineHeight; // "No stations" message
            }
            height += 15; // spacing

            // Chests header + player position
            height += SectionHeight + LineHeight + 5;

            // Chest rows
            var chests = _storage.GetRegisteredChests();
            height += Math.Max(1, chests.Count) * ChestRowHeight;
            height += 20; // bottom padding

            return height;
        }

        private int DrawStationsSection(int x, int y, int width)
        {
            int totalAvailable = _availableStationCount + _availableEnvironmentCount;
            int totalStations = DisplayStationTiles.Length + EnvironmentNames.Length; // 34 + 5 = 39

            // Section header
            string headerText;
            if (_config.ShowStationSpoilers)
                headerText = $"Crafting Stations ({totalAvailable}/{totalStations} available)";
            else
                headerText = $"Crafting Stations ({totalAvailable} available)";

            if (IsVisible(y, SectionHeight))
            {
                UIRenderer.DrawRect(x, y, width, SectionHeight, UIColors.HeaderBg);
                UIRenderer.DrawText(headerText, x + 10, y + 6, UIColors.TextTitle);

                // Spoilers toggle button on right
                int toggleWidth = 80;
                int toggleX = x + width - toggleWidth - 10;
                bool toggleHover = WidgetInput.IsMouseOver(toggleX, y + 3, toggleWidth, 22);
                UIRenderer.DrawRect(toggleX, y + 3, toggleWidth, 22,
                    _config.ShowStationSpoilers
                        ? (toggleHover ? UIColors.ButtonHover : UIColors.Button)
                        : (toggleHover ? UIColors.ButtonHover : UIColors.SectionBg));
                UIRenderer.DrawText(_config.ShowStationSpoilers ? "Spoilers" : "Spoilers",
                    toggleX + 8, y + 7,
                    _config.ShowStationSpoilers ? UIColors.AccentText : UIColors.TextHint);

                if (toggleHover && WidgetInput.MouseLeftClick)
                {
                    _config.ShowStationSpoilers = !_config.ShowStationSpoilers;
                    _config.Save();
                    _needsRefresh = true;
                    WidgetInput.ConsumeClick();
                }
            }
            y += SectionHeight + 5;

            // Tile stations
            foreach (int tileId in DisplayStationTiles)
            {
                bool available = _checker.IsStationAvailable(tileId);
                if (!_config.ShowStationSpoilers && !available)
                    continue;

                if (IsVisible(y, StationRowHeight))
                {
                    DrawStationRow(x, y, width, TileNames.GetName(tileId), available);
                }
                y += StationRowHeight;
            }

            // Environment conditions
            var env = _checker.EnvironmentState;
            for (int i = 0; i < EnvironmentNames.Length; i++)
            {
                bool proximity = GetEnvironmentProximity(env, i);
                string unlockKey = GetEnvironmentUnlockKey(i);
                bool unlocked = _config.HasSpecialUnlock(unlockKey);
                bool available = proximity || unlocked;

                if (!_config.ShowStationSpoilers && !available)
                    continue;

                if (IsVisible(y, StationRowHeight))
                {
                    string source = "";
                    if (available)
                    {
                        if (proximity && unlocked) source = "(both)";
                        else if (proximity) source = "(nearby)";
                        else source = "(unlocked)";
                    }
                    DrawEnvironmentRow(x, y, width, EnvironmentNames[i], available, source);
                }
                y += StationRowHeight;
            }

            // No stations message when spoilers off and nothing available
            if (!_config.ShowStationSpoilers && _availableStationCount + _availableEnvironmentCount == 0)
            {
                if (IsVisible(y, LineHeight))
                {
                    UIRenderer.DrawText("No stations available. Walk near crafting stations.", x + 10, y, UIColors.TextHint);
                }
                y += LineHeight;
            }

            return y + 15;
        }

        private void DrawStationRow(int x, int y, int width, string name, bool available)
        {
            // Status dot
            int dotX = x + 10;
            int dotY = y + (StationRowHeight - DotSize) / 2;
            UIRenderer.DrawRect(dotX, dotY, DotSize, DotSize, available ? UIColors.Success : UIColors.Error);

            // Station name
            UIRenderer.DrawText(name, x + 30, y + 4, available ? UIColors.Text : UIColors.TextHint);
        }

        private void DrawEnvironmentRow(int x, int y, int width, string name, bool available, string source)
        {
            // Status dot
            int dotX = x + 10;
            int dotY = y + (StationRowHeight - DotSize) / 2;
            UIRenderer.DrawRect(dotX, dotY, DotSize, DotSize, available ? UIColors.Success : UIColors.Error);

            // Environment name (italic style — prefix with tilde to distinguish from tile stations)
            UIRenderer.DrawText($"~ {name}", x + 30, y + 4, available ? UIColors.Info : UIColors.TextHint);

            // Source indicator
            if (!string.IsNullOrEmpty(source))
            {
                UIRenderer.DrawText(source, x + width - 120, y + 4, UIColors.TextDim);
            }
        }

        private bool GetEnvironmentProximity(EnvironmentState env, int index)
        {
            switch (index)
            {
                case 0: return env.HasWater;
                case 1: return env.HasHoney;
                case 2: return env.HasLava;
                case 3: return env.InSnow;
                case 4: return env.InGraveyard;
                default: return false;
            }
        }

        private string GetEnvironmentUnlockKey(int index)
        {
            switch (index)
            {
                case 0: return "water";
                case 1: return "honey";
                case 2: return "lava";
                case 3: return "snow";
                case 4: return "graveyard";
                default: return "";
            }
        }

        private int DrawChestsSection(int x, int y, int width)
        {
            var chests = _storage.GetRegisteredChests();

            // Section header
            if (IsVisible(y, SectionHeight))
            {
                UIRenderer.DrawRect(x, y, width, SectionHeight, UIColors.HeaderBg);
                UIRenderer.DrawText($"Connected Storage Drives ({chests.Count})", x + 10, y + 6, UIColors.TextTitle);

                // Station memory toggle (right side of header, next to spoilers-style placement)
                if (ProgressionTier.HasStationMemory(_config.Tier))
                {
                    bool memEnabled = _config.StationMemoryEnabled;
                    int memToggleW = 100;
                    int memToggleX = x + width - memToggleW - 10;
                    bool memHover = WidgetInput.IsMouseOver(memToggleX, y + 3, memToggleW, 22);

                    UIRenderer.DrawRect(memToggleX, y + 3, memToggleW, 22,
                        memEnabled ? (memHover ? UIColors.ButtonHover : UIColors.Button)
                                   : (memHover ? UIColors.ButtonHover : UIColors.SectionBg));
                    UIRenderer.DrawText(memEnabled ? "Memory ON" : "Memory OFF",
                        memToggleX + 8, y + 7,
                        memEnabled ? UIColors.Success : UIColors.TextHint);

                    if (memHover && WidgetInput.MouseLeftClick)
                    {
                        _config.StationMemoryEnabled = !_config.StationMemoryEnabled;
                        _config.Save();
                        _needsRefresh = true;
                        WidgetInput.ConsumeClick();
                    }
                }
            }
            y += SectionHeight + 5;

            // Player position
            if (IsVisible(y, LineHeight))
            {
                var pos = _rangeCalc.GetPlayerPosition();
                int tileX = (int)(pos.x / 16);
                int tileY = (int)(pos.y / 16);
                UIRenderer.DrawText($"Player: ({tileX}, {tileY})", x + 10, y, UIColors.TextDim);
            }
            y += LineHeight;

            if (chests.Count == 0)
            {
                if (IsVisible(y, ChestRowHeight))
                {
                    UIRenderer.DrawText("No connected storage drives in this network.", x + 10, y + 4, UIColors.TextHint);
                }
                y += ChestRowHeight;
            }
            else
            {
                for (int i = 0; i < chests.Count; i++)
                {
                    var chest = chests[i];

                    if (IsVisible(y, ChestRowHeight))
                    {
                        if (_confirmingChestIndex == chest.ChestIndex)
                        {
                            DrawChestConfirmRow(x, y, width, chest);
                        }
                        else
                        {
                            DrawChestRow(x, y, width, chest);
                        }
                    }
                    y += ChestRowHeight;
                }
            }

            return y + 20;
        }

        private void DrawChestRow(int x, int y, int width, ChestInfo chest)
        {
            // Status dot (in range / out of range)
            int dotX = x + 10;
            int dotY = y + (ChestRowHeight - DotSize) / 2;
            UIRenderer.DrawRect(dotX, dotY, DotSize, DotSize, chest.IsInRange ? UIColors.Success : UIColors.Error);

            // Chest name (truncated to fit before buttons on right)
            string name = string.IsNullOrEmpty(chest.Name) ? $"Storage Drive at ({chest.X}, {chest.Y})" : chest.Name;
            int nameMaxW = width - 200; // reserve space for info + ping + X buttons
            UIRenderer.DrawText(TextUtil.Truncate(name, nameMaxW), x + 30, y + 6, chest.IsInRange ? UIColors.Text : UIColors.TextDim);

            // Position + item count (right-aligned before buttons)
            string info = $"({chest.X}, {chest.Y}) ({chest.ItemCount} items)";
            int infoW = TextUtil.MeasureWidth(info);
            int infoX = x + width - 110 - infoW - 4;
            UIRenderer.DrawText(info, infoX, y + 6, UIColors.TextHint);

            // Ping button
            int pingBtnWidth = 55;
            int pingBtnX = x + width - 115;
            bool pingHover = WidgetInput.IsMouseOver(pingBtnX, y + 2, pingBtnWidth, ChestRowHeight - 4);
            UIRenderer.DrawRect(pingBtnX, y + 2, pingBtnWidth, ChestRowHeight - 4,
                pingHover ? UIColors.ButtonHover : UIColors.Button);
            UIRenderer.DrawText("Ping", pingBtnX + 14, y + 6, UIColors.TextDim);

            if (pingHover && WidgetInput.MouseLeftClick)
            {
                if (SourceIndex.IsStorageDrive(chest.ChestIndex))
                    _pinger.PingTile(chest.X, chest.Y);
                else
                    _pinger.PingChest(chest.ChestIndex);
                WidgetInput.ConsumeClick();
            }

            // X (deregister) button
            int xBtnWidth = 24;
            int xBtnX = x + width - 50;
            bool xHover = WidgetInput.IsMouseOver(xBtnX, y + 2, xBtnWidth, ChestRowHeight - 4);
            UIRenderer.DrawRect(xBtnX, y + 2, xBtnWidth, ChestRowHeight - 4,
                xHover ? UIColors.CloseBtnHover : UIColors.CloseBtn);
            UIRenderer.DrawText("X", xBtnX + 8, y + 6, UIColors.Text);

            if (xHover && WidgetInput.MouseLeftClick)
            {
                _confirmingChestIndex = chest.ChestIndex;
                _confirmTimer = ConfirmTimeout;
                WidgetInput.ConsumeClick();
            }
        }

        private void DrawChestConfirmRow(int x, int y, int width, ChestInfo chest)
        {
            // Confirmation background
            UIRenderer.DrawRect(x + 5, y + 1, width - 10, ChestRowHeight - 2, UIColors.SectionBg);

            string name = string.IsNullOrEmpty(chest.Name) ? $"Storage Drive at ({chest.X}, {chest.Y})" : chest.Name;
            string confirmText = TextUtil.Truncate($"Remove {name}?", width - 140);
            UIRenderer.DrawText(confirmText, x + 10, y + 6, UIColors.Warning);

            // Yes button
            int yesBtnWidth = 40;
            int yesBtnX = x + width - 120;
            bool yesHover = WidgetInput.IsMouseOver(yesBtnX, y + 2, yesBtnWidth, ChestRowHeight - 4);
            UIRenderer.DrawRect(yesBtnX, y + 2, yesBtnWidth, ChestRowHeight - 4,
                yesHover ? UIColors.CloseBtnHover : UIColors.CloseBtn);
            UIRenderer.DrawText("Yes", yesBtnX + 8, y + 6, UIColors.Text);

            if (yesHover && WidgetInput.MouseLeftClick)
            {
                _registry.UnregisterChest(chest.X, chest.Y);
                _confirmingChestIndex = -1;
                _confirmTimer = 0;
                _needsRefresh = true;
                OnStorageModified?.Invoke();
                WidgetInput.ConsumeClick();
            }

            // No button
            int noBtnWidth = 40;
            int noBtnX = x + width - 65;
            bool noHover = WidgetInput.IsMouseOver(noBtnX, y + 2, noBtnWidth, ChestRowHeight - 4);
            UIRenderer.DrawRect(noBtnX, y + 2, noBtnWidth, ChestRowHeight - 4,
                noHover ? UIColors.ButtonHover : UIColors.Button);
            UIRenderer.DrawText("No", noBtnX + 10, y + 6, UIColors.Text);

            if (noHover && WidgetInput.MouseLeftClick)
            {
                _confirmingChestIndex = -1;
                _confirmTimer = 0;
                WidgetInput.ConsumeClick();
            }
        }
    }
}
