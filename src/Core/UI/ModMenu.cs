using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using TerrariaModder.Core.Config;
using TerrariaModder.Core.Conflicts;
using TerrariaModder.Core.Input;
using TerrariaModder.Core.Logging;
using TerrariaModder.Core.Manifest;
using TerrariaModder.Core.Reflection;
using TerrariaModder.Core.UI.Widgets;

namespace TerrariaModder.Core.UI
{
    /// <summary>
    /// In-game mod menu for managing mods, configs, and keybinds.
    /// </summary>
    public static class ModMenu
    {
        private static ILogger _log;
        private static bool _initialized;
        private static bool _visible;
        private static int _selectedTab;
        private static int _scrollOffset;
        private static int _selectedModIndex;
        private static string _selectedModId;

        // Keybind rebinding state
        private static bool _isRebinding;
        private static string _rebindingKeybindId;
        private static bool _rebindWaitingForKey;

        // Config key editing state
        private static bool _isEditingConfigKey;
        private static string _editingConfigKeyField;
        private static string _editingConfigKeyModId;

        // Number field editing state
        private static bool _isEditingNumber;
        private static string _editingNumberField;
        private static string _editingNumberModId;
        private static string _editingNumberText = "";
        private static bool _editingNumberFirstKeyPressed; // True after first key replaces old value

        // Hold-to-repeat state for +/- buttons
        private static string _holdingButtonField;
        private static string _holdingButtonModId;
        private static int _holdingButtonDirection; // -1 or +1
        private static DateTime _holdStartTime;
        private static DateTime _lastRepeatTime;
        private const int HoldDelayMs = 400;      // Initial delay before repeating
        private const int RepeatIntervalMs = 50;  // Interval between repeats

        // Draggable panel state
        private static int _panelX = -1;  // -1 means center on screen
        private static int _panelY = -1;
        private static bool _isDragging = false;
        private static int _dragOffsetX, _dragOffsetY;

        // Scrollbar drag state
        private static bool _scrollDragging;
        private static int _scrollDragStartY;
        private static int _scrollDragStartOffset;
        private static int _scrollDragMaxScroll;
        private static int _scrollDragTrackHeight;

        // Z-order input blocking (set per frame in DrawInternal)
        private static bool _blockInput;

        // Conflict scanner state
        private static ConflictReport _conflictReport;
        private static bool _conflictScanDone;

        // Load order drag state
        private static bool _isDraggingLoadOrder;
        private static int _dragLoadOrderIndex = -1;
        private static int _dragLoadOrderTargetIndex = -1;
        private static int _dragLoadOrderMouseOffsetY;
        private static List<string> _editedLoadOrder;
        private static bool _loadOrderChanged;

        // Menu dimensions
        private const int MenuWidth = 600;
        private const int MenuHeight = 450;
        private const int TabHeight = 30;
        private const int ItemHeight = 28;
        private const int Padding = 10;
        private const int ButtonWidth = 80;
        private const int ButtonHeight = 22;

        // Tabs
        private static readonly string[] Tabs = { "Mods", "Config", "Keybinds", "Load Order", "Logs" };

        public static bool IsVisible => _visible;

        public static void Initialize(ILogger log)
        {
            // Skip on dedicated server - no UI to show
            if (Game.IsServer) return;

            _log = log;
            UIRenderer.Initialize(log);

            // Register menu toggle keybind (works on title screen too)
            var menuKeybind = KeybindManager.RegisterInternal("menu-toggle", "Mod Menu", "Open/close mod menu", "F6", ToggleMenu);
            menuKeybind.AllowInMenu = true;

            // Register draw callback for z-ordered panel drawing
            UIRenderer.RegisterPanelDraw("mod-menu", Draw);

            _initialized = true;
            _log?.Info("[UI] Mod menu initialized (F6 to open)");
        }

        public static void ToggleMenu()
        {
            _log?.Info("[UI] ToggleMenu called!");
            _visible = !_visible;
            _scrollOffset = 0;
            _scrollDragging = false;
            _isRebinding = false;
            _rebindingKeybindId = null;
            _isEditingConfigKey = false;
            _editingConfigKeyField = null;
            _editingConfigKeyModId = null;
            _isEditingNumber = false;
            _editingNumberField = null;
            _editingNumberModId = null;
            _holdingButtonField = null;
            UIRenderer.UnregisterKeyInputBlock("mod-menu");
            UIRenderer.DisableTextInput();

            if (_visible)
            {
                _log?.Info("[UI] Menu opened");
                // Invalidate conflict cache so it re-scans when Conflicts tab is opened
                _conflictScanDone = false;
                _conflictReport = null;

                // Select first mod if none selected
                var mods = PluginLoader.Mods;
                if (mods.Count > 0 && string.IsNullOrEmpty(_selectedModId))
                {
                    _selectedModId = mods[0].Manifest.Id;
                    _selectedModIndex = 0;
                }

                // Register panel bounds - this automatically enables mouse blocking
                // Use current position or center of screen if not set
                int menuX = _panelX >= 0 ? _panelX : (UIRenderer.ScreenWidth - MenuWidth) / 2;
                int menuY = _panelY >= 0 ? _panelY : (UIRenderer.ScreenHeight - MenuHeight) / 2;
                UIRenderer.RegisterPanelBounds("mod-menu", menuX, menuY, MenuWidth, MenuHeight);
                _log?.Debug($"[UI] Registered mod-menu bounds at ({menuX},{menuY}) {MenuWidth}x{MenuHeight}");
            }
            else
            {
                _log?.Debug("[UI] Menu closed");
                // Unregister panel bounds - this automatically disables mouse blocking if no other panels
                UIRenderer.UnregisterPanelBounds("mod-menu");
            }
        }

        /// <summary>
        /// Called every frame to draw the menu.
        /// </summary>
        public static void Draw()
        {
            if (!_initialized || !_visible) return;

            // Begin SpriteBatch if needed
            UIRenderer.BeginDraw();

            try
            {
                DrawInternal();
            }
            catch (Exception ex)
            {
                _log?.Error($"[UI] ModMenu draw error: {ex.Message}");
            }
            finally
            {
                UIRenderer.EndDraw();
            }
        }

        private static void DrawInternal()
        {
            // Ensure mod icons are loaded (lazy, safe to call every frame)
            PluginLoader.LoadModIcons();

            // Clear tooltip each frame
            Tooltip.Clear();

            // Check if a higher-z-order panel should block our input
            _blockInput = UIRenderer.ShouldBlockForHigherPriorityPanel("mod-menu");

            // Handle Escape to close menu (unless rebinding or editing)
            if (!_isRebinding && !_isEditingConfigKey && !_isEditingNumber)
            {
                if (InputState.IsKeyJustPressed(KeyCode.Escape))
                {
                    ToggleMenu();
                    return;
                }
            }

            // Initialize panel position to center if not set
            if (_panelX < 0) _panelX = (UIRenderer.ScreenWidth - MenuWidth) / 2;
            if (_panelY < 0) _panelY = (UIRenderer.ScreenHeight - MenuHeight) / 2;

            // Handle dragging
            HandleDragging();

            // Clamp to screen bounds
            _panelX = Math.Max(0, Math.Min(_panelX, UIRenderer.ScreenWidth - MenuWidth));
            _panelY = Math.Max(0, Math.Min(_panelY, UIRenderer.ScreenHeight - MenuHeight));

            int menuX = _panelX;
            int menuY = _panelY;

            // Register panel bounds for click-through prevention (persists until menu closes)
            UIRenderer.RegisterPanelBounds("mod-menu", menuX, menuY, MenuWidth, MenuHeight);

            // Note: BlockMouseOnly is called in ToggleMenu() when menu opens/closes
            // Don't call it here in Draw - it causes timing issues with MouseCache

            // Register/unregister keyboard block based on editing state
            if (_isRebinding || _isEditingConfigKey || _isEditingNumber)
                UIRenderer.RegisterKeyInputBlock("mod-menu");
            else
                UIRenderer.UnregisterKeyInputBlock("mod-menu");

            // Draw main panel
            UIRenderer.DrawPanel(menuX, menuY, MenuWidth, MenuHeight, UIColors.PanelBg);

            // Draw header
            UIRenderer.DrawRect(menuX, menuY, MenuWidth, 35, UIColors.HeaderBg);
            string headerText = "TerrariaModder v" + PluginLoader.FrameworkVersion;

            // Draw header icon
            int headerTextX = menuX + Padding;
            if (PluginLoader.DefaultIcon != null)
            {
                UIRenderer.DrawTexture(PluginLoader.DefaultIcon, menuX + Padding, menuY + 5, 24, 24);
                headerTextX += 28;
            }

            // Check if any mod needs restart (non-hot-reload mods with baseline changes)
            bool anyNeedsRestart = false;
            foreach (var mod in PluginLoader.Mods)
            {
                if (!ModSupportsHotReload(mod))
                {
                    var config = mod.Context?.Config as ModConfig;
                    bool configChanged = config?.HasChangesFromBaseline() ?? false;
                    if (configChanged)
                    {
                        anyNeedsRestart = true;
                        break;
                    }
                }
            }

            UIRenderer.DrawTextShadow(headerText, headerTextX, menuY + 8, UIColors.Text);

            // Show [Restart Required] tag if any mod needs restart
            if (anyNeedsRestart)
            {
                int tagX = headerTextX + TextUtil.MeasureWidth(headerText) + 10;
                UIRenderer.DrawText("[Game Restart Required]", tagX, menuY + 8, UIColors.Error);
            }

            // Draw close button
            int closeX = menuX + MenuWidth - 30;
            int closeY = menuY + 5;
            bool closeHover = UIRenderer.IsMouseOver(closeX, closeY, 25, 25) && !_blockInput;
            UIRenderer.DrawRect(closeX, closeY, 25, 25, closeHover ? UIColors.CloseBtnHover : UIColors.CloseBtn);
            UIRenderer.DrawTextShadow("X", closeX + 8, closeY + 4, UIColors.Text);

            if (closeHover && UIRenderer.MouseLeftClick)
            {
                ToggleMenu();
                UIRenderer.ConsumeClick();
                return;
            }

            // Draw tabs
            int tabY = menuY + 40;
            int tabWidth = (MenuWidth - Padding * 2) / Tabs.Length;
            for (int i = 0; i < Tabs.Length; i++)
            {
                int tabX = menuX + Padding + i * tabWidth;
                bool isActive = i == _selectedTab;
                bool isHover = UIRenderer.IsMouseOver(tabX, tabY, tabWidth - 2, TabHeight) && !_blockInput;

                var tabColor = isActive ? UIColors.ItemActiveBg : (isHover ? UIColors.ItemHoverBg : UIColors.SectionBg);
                UIRenderer.DrawRect(tabX, tabY, tabWidth - 2, TabHeight, tabColor);
                string tabLabel = TextUtil.Truncate(Tabs[i], tabWidth - 12);
                int tabTextW = TextUtil.MeasureWidth(tabLabel);
                UIRenderer.DrawTextShadow(tabLabel, tabX + (tabWidth - tabTextW) / 2, tabY + 7, UIColors.Text);

                if (isHover && UIRenderer.MouseLeftClick && !_isRebinding && !_isEditingConfigKey)
                {
                    _selectedTab = i;
                    _scrollOffset = 0;
                    _scrollDragging = false;
                    _isRebinding = false;
                    _isEditingConfigKey = false;
                    UIRenderer.ConsumeClick();
                }
            }

            // Draw content area
            int contentY = tabY + TabHeight + Padding;
            int contentHeight = MenuHeight - (contentY - menuY) - Padding;

            switch (_selectedTab)
            {
                case 0: DrawModsTab(menuX + Padding, contentY, MenuWidth - Padding * 2, contentHeight); break;
                case 1: DrawConfigTab(menuX + Padding, contentY, MenuWidth - Padding * 2, contentHeight); break;
                case 2: DrawKeybindsTab(menuX + Padding, contentY, MenuWidth - Padding * 2, contentHeight); break;
                case 3: DrawConflictsTab(menuX + Padding, contentY, MenuWidth - Padding * 2, contentHeight); break;
                case 4: DrawLogsTab(menuX + Padding, contentY, MenuWidth - Padding * 2, contentHeight); break;
            }

            // Handle scrollbar drag (before wheel handling)
            if (_scrollDragging)
            {
                if (UIRenderer.MouseLeft)
                {
                    if (_scrollDragTrackHeight > 0)
                    {
                        int deltaY = UIRenderer.MouseY - _scrollDragStartY;
                        float pct = (float)deltaY / _scrollDragTrackHeight;
                        _scrollOffset = _scrollDragStartOffset + (int)(pct * _scrollDragMaxScroll);
                        _scrollOffset = Math.Max(0, Math.Min(_scrollOffset, _scrollDragMaxScroll));
                    }
                }
                else
                {
                    _scrollDragging = false;
                }
            }

            // Handle scroll - always consume to prevent game from processing
            int scroll = UIRenderer.ScrollWheel;
            if (scroll != 0 && !_blockInput)
            {
                UIRenderer.ConsumeScroll();

                if (!_isRebinding && !_isEditingConfigKey)
                {
                    _scrollOffset -= scroll / 30;
                    if (_scrollOffset < 0) _scrollOffset = 0;
                }
            }

            // ESC to close (or cancel rebind/edit)
            if (InputState.IsKeyJustPressed(KeyCode.Escape))
            {
                if (_isRebinding)
                {
                    _isRebinding = false;
                    _rebindingKeybindId = null;
                    _rebindWaitingForKey = false;
                    UIRenderer.DisableTextInput();
                }
                else if (_isEditingConfigKey)
                {
                    _isEditingConfigKey = false;
                    _editingConfigKeyField = null;
                    _editingConfigKeyModId = null;
                    UIRenderer.DisableTextInput();
                }
                else if (_isEditingNumber)
                {
                    _isEditingNumber = false;
                    _editingNumberField = null;
                    _editingNumberModId = null;
                    UIRenderer.DisableTextInput();
                }
                else
                {
                    ToggleMenu();
                }
            }

            // Catch-all: consume any unhandled clicks within the panel area.
            // Without this, clicking on panel background or dead space passes through to the world.
            // Skip when a higher-z panel overlaps us — let the front panel process the click.
            if (UIRenderer.IsMouseOver(menuX, menuY, MenuWidth, MenuHeight) && !_blockInput)
            {
                if (UIRenderer.MouseLeftClick) UIRenderer.ConsumeClick();
                if (UIRenderer.MouseRightClick) UIRenderer.ConsumeRightClick();
            }

            // Draw tooltip on top of everything
            Tooltip.DrawDeferred();

            // Handle hold-to-repeat for +/- buttons
            HandleHoldRepeat();

            // Handle keybind rebinding
            if (_isRebinding && _rebindWaitingForKey)
            {
                UIRenderer.EnableTextInput();
                HandleKeybindCapture();
            }
        }

        private static void HandleDragging()
        {
            // Check if clicking on header area (drag handle) - exclude close button area
            bool inHeader = UIRenderer.IsMouseOver(_panelX, _panelY, MenuWidth - 35, 35) && !_blockInput;

            if (UIRenderer.MouseLeftClick && inHeader && !_isDragging)
            {
                // Start dragging
                _isDragging = true;
                _dragOffsetX = UIRenderer.MouseX - _panelX;
                _dragOffsetY = UIRenderer.MouseY - _panelY;
            }

            if (_isDragging)
            {
                if (UIRenderer.MouseLeft)
                {
                    // Update position while dragging
                    _panelX = UIRenderer.MouseX - _dragOffsetX;
                    _panelY = UIRenderer.MouseY - _dragOffsetY;
                }
                else
                {
                    // Stop dragging when mouse released
                    _isDragging = false;
                }
            }
        }

        private static void DrawModsTab(int x, int y, int width, int height)
        {
            var mods = PluginLoader.Mods;
            int listHeight = height - 85; // Leave room for details panel
            int visibleItems = listHeight / ItemHeight;
            int maxScroll = Math.Max(0, mods.Count - visibleItems);
            _scrollOffset = Math.Min(_scrollOffset, maxScroll);
            int scrollIndicatorWidth = 10;
            int contentWidth = width - scrollIndicatorWidth;

            for (int i = 0; i < visibleItems && i + _scrollOffset < mods.Count; i++)
            {
                var mod = mods[i + _scrollOffset];
                int itemY = y + i * ItemHeight;
                bool isHover = UIRenderer.IsMouseOver(x, itemY, contentWidth, ItemHeight - 2) && !_blockInput;
                bool isSelected = mod.Manifest.Id == _selectedModId;

                // Background
                var bg = isSelected ? UIColors.ItemActiveBg : (isHover ? UIColors.ItemHoverBg : UIColors.ItemBg);
                UIRenderer.DrawRect(x, itemY, contentWidth, ItemHeight - 2, bg);

                // Mod icon (only if mod provides its own)
                int iconOffset = 0;
                if (mod.IconTexture != null)
                {
                    UIRenderer.DrawTexture(mod.IconTexture, x + 3, itemY + 2, 22, 22);
                    iconOffset = 24;
                }

                // Status indicator symbol
                var statusColor = mod.State == ModState.Loaded ? UIColors.Success :
                                  mod.State == ModState.Errored ? UIColors.Error :
                                  mod.State == ModState.DependencyError ? UIColors.Warning : UIColors.TextDim;
                string statusSymbol = mod.State == ModState.Loaded ? "\u2713" :
                                      mod.State == ModState.Errored ? "X" :
                                      mod.State == ModState.DependencyError ? "\u25CF" : "\u25CF";
                UIRenderer.DrawTextShadow(statusSymbol, x + 5 + iconOffset, itemY + 5, statusColor);

                // Mod name and version
                string name = TextUtil.Truncate(mod.Manifest.Name, contentWidth - 110 - iconOffset);
                UIRenderer.DrawTextShadow(name, x + 22 + iconOffset, itemY + 5, UIColors.Text);
                UIRenderer.DrawText("v" + mod.Manifest.Version, x + contentWidth - 80, itemY + 5, UIColors.TextDim);

                // Click to select
                if (isHover && UIRenderer.MouseLeftClick)
                {
                    _selectedModId = mod.Manifest.Id;
                    _selectedModIndex = i + _scrollOffset;
                    UIRenderer.ConsumeClick();
                }
            }

            // Draw scroll indicator
            DrawScrollIndicator(x + contentWidth + 2, y, listHeight, _scrollOffset, mods.Count, visibleItems);

            // Draw selected mod details at bottom
            if (!string.IsNullOrEmpty(_selectedModId))
            {
                var selectedMod = mods.FirstOrDefault(m => m.Manifest.Id == _selectedModId);
                if (selectedMod != null)
                {
                    int detailY = y + height - 80;
                    UIRenderer.DrawRect(x, detailY, width, 75, UIColors.HeaderBg);

                    UIRenderer.DrawTextShadow(selectedMod.Manifest.Name, x + 5, detailY + 5, UIColors.Text);
                    UIRenderer.DrawText("by " + selectedMod.Manifest.Author, x + 5, detailY + 22, UIColors.TextDim);

                    string status = selectedMod.State.ToString();
                    if (!string.IsNullOrEmpty(selectedMod.ErrorMessage))
                        status += ": " + selectedMod.ErrorMessage;
                    status = TextUtil.Truncate(status, width - 10);

                    var statColor = selectedMod.State == ModState.Loaded ? UIColors.Success : UIColors.Error;
                    UIRenderer.DrawText(status, x + 5, detailY + 40, statColor);

                    string desc = TextUtil.Truncate(selectedMod.Manifest.Description ?? "", width - 10);
                    UIRenderer.DrawText(desc, x + 5, detailY + 55, UIColors.TextDim);
                }
            }
        }

        private static void DrawConfigTab(int x, int y, int width, int height)
        {
            // Header
            UIRenderer.DrawTextShadow("Configuration (all mods)", x, y, UIColors.Text);
            y += 25;

            int listHeight = height - 25;
            int visibleItems = listHeight / ItemHeight;
            int scrollIndicatorWidth = 10;
            int contentWidth = width - scrollIndicatorWidth;

            // Get all mods with config
            var modsWithConfig = PluginLoader.Mods.Where(m => m.Context?.Config?.Schema != null && m.Context.Config.Schema.Count > 0).ToList();

            // Build flat list of display items: Framework section first, then mod configs
            var displayItems = new List<(string type, object data, ModInfo mod)>();

            // Framework settings section (theme selector)
            displayItems.Add(("header", "Framework", null));
            displayItems.Add(("theme", null, null));

            foreach (var mod in modsWithConfig)
            {
                displayItems.Add(("header", mod.Manifest.Name, mod));
                foreach (var field in mod.Context.Config.Schema.Values)
                {
                    displayItems.Add(("field", field, mod));
                }
            }

            int totalDisplayItems = displayItems.Count;

            // Clamp scroll offset
            int maxScroll = Math.Max(0, totalDisplayItems - visibleItems);
            _scrollOffset = Math.Min(_scrollOffset, maxScroll);

            // Draw visible items
            int currentY = y;
            for (int i = _scrollOffset; i < displayItems.Count && currentY < y + listHeight - ItemHeight; i++)
            {
                var item = displayItems[i];

                if (item.type == "header")
                {
                    // Draw section header for mod (or Framework)
                    string modName = (string)item.data;
                    bool supportsHotReload = item.mod != null && ModSupportsHotReload(item.mod);

                    // Check if this mod has pending config changes that require restart
                    bool needsRestart = false;
                    if (item.mod != null && !supportsHotReload)
                    {
                        var config = item.mod.Context?.Config as ModConfig;
                        needsRestart = config?.HasChangesFromBaseline() ?? false;
                    }

                    int headerHeight = ItemHeight - 4;

                    UIRenderer.DrawRect(x, currentY, contentWidth, headerHeight, UIColors.SectionBg);
                    // Divider line at top
                    UIRenderer.DrawRect(x, currentY, contentWidth, 2, UIColors.Divider);
                    UIRenderer.DrawTextShadow(modName, x + 8, currentY + 5, UIColors.Warning);

                    if (needsRestart)
                    {
                        int restartTagX = x + 8 + TextUtil.MeasureWidth(modName) + 5;
                        UIRenderer.DrawText("(Game Restart Required)", restartTagX, currentY + 5, UIColors.Error);
                    }

                    // Per-mod Reset button (only for actual mods, not Framework)
                    if (item.mod != null)
                    {
                        int resetBtnW = 50;
                        int resetBtnH = 18;
                        int resetBtnX = x + contentWidth - resetBtnW - 5;
                        int resetBtnY = currentY + 3;
                        bool resetHover = UIRenderer.IsMouseOver(resetBtnX, resetBtnY, resetBtnW, resetBtnH) && !_blockInput;
                        UIRenderer.DrawRect(resetBtnX, resetBtnY, resetBtnW, resetBtnH, resetHover ? UIColors.ButtonHover : UIColors.Button);
                        UIRenderer.DrawText("Reset", resetBtnX + 8, resetBtnY + 2, UIColors.Text);

                        if (resetHover && UIRenderer.MouseLeftClick)
                        {
                            ResetConfigToDefaults(item.mod);
                            UIRenderer.ConsumeClick();
                        }
                    }

                    currentY += headerHeight + 4; // Add spacing after header
                }
                else if (item.type == "theme")
                {
                    // Framework theme selector
                    DrawThemeSelector(x, currentY, contentWidth);
                    currentY += ItemHeight;
                }
                else if (item.type == "field")
                {
                    var field = (ConfigField)item.data;
                    var config = item.mod.Context.Config;
                    DrawConfigField(x, currentY, contentWidth, field, config, item.mod.Manifest.Id);
                    currentY += ItemHeight;
                }
            }

            // Draw scroll indicator
            DrawScrollIndicator(x + contentWidth + 2, y, listHeight, _scrollOffset, totalDisplayItems, visibleItems);

        }

        /// <summary>
        /// Draw the Framework color theme selector in the Config tab.
        /// </summary>
        private static void DrawThemeSelector(int x, int y, int width)
        {
            bool isHover = UIRenderer.IsMouseOver(x, y, width, ItemHeight - 2) && !_blockInput;
            UIRenderer.DrawRect(x, y, width, ItemHeight - 2, isHover ? UIColors.ItemHoverBg : UIColors.ItemBg);

            // Label
            UIRenderer.DrawTextShadow("Color Theme", x + 5, y + 5, UIColors.Text);

            if (isHover)
                Tooltip.Set("Color palette for mod UIs. Red-Green and Blue-Yellow modes adjust status colors for colorblind accessibility.");

            // Enum-style selector (same pattern as DrawEnumField)
            int valueX = x + width - 180;
            int valueWidth = 170;
            string currentVal = UIColors.CurrentTheme;
            var options = UIColors.ThemeNames;
            int currentIndex = Array.IndexOf(options, currentVal);
            if (currentIndex < 0) currentIndex = 0;

            int btnWidth = 25;

            // Left arrow
            bool leftHover = UIRenderer.IsMouseOver(valueX, y + 2, btnWidth, ItemHeight - 6) && !_blockInput;
            UIRenderer.DrawRect(valueX, y + 2, btnWidth, ItemHeight - 6, leftHover ? UIColors.ButtonHover : UIColors.Button);
            UIRenderer.DrawText("<", valueX + 9, y + 4, UIColors.Text);

            if (leftHover && UIRenderer.MouseLeftClick)
            {
                currentIndex = (currentIndex - 1 + options.Length) % options.Length;
                UIColors.SetTheme(options[currentIndex]);
                UIRenderer.ConsumeClick();
            }

            // Value display
            int valX = valueX + btnWidth + 5;
            int valWidth = valueWidth - btnWidth * 2 - 10;
            UIRenderer.DrawRect(valX, y + 2, valWidth, ItemHeight - 6, UIColors.InputBg);
            string displayVal = TextUtil.Truncate(currentVal, valWidth - 20);
            UIRenderer.DrawText(displayVal, valX + 10, y + 5, UIColors.Text);

            // Right arrow
            int rightX = valueX + valueWidth - btnWidth;
            bool rightHover = UIRenderer.IsMouseOver(rightX, y + 2, btnWidth, ItemHeight - 6) && !_blockInput;
            UIRenderer.DrawRect(rightX, y + 2, btnWidth, ItemHeight - 6, rightHover ? UIColors.ButtonHover : UIColors.Button);
            UIRenderer.DrawText(">", rightX + 8, y + 4, UIColors.Text);

            if (rightHover && UIRenderer.MouseLeftClick)
            {
                currentIndex = (currentIndex + 1) % options.Length;
                UIColors.SetTheme(options[currentIndex]);
                UIRenderer.ConsumeClick();
            }
        }

        private static void DrawConfigField(int x, int y, int width, ConfigField field, IModConfig config, string modId)
        {
            bool isHover = UIRenderer.IsMouseOver(x, y, width, ItemHeight - 2) && !_blockInput;
            UIRenderer.DrawRect(x, y, width, ItemHeight - 2, isHover ? UIColors.ItemHoverBg : UIColors.ItemBg);

            // Field label (truncated to available space before value area)
            string label = TextUtil.Truncate(field.Label ?? field.Key, width - 190);
            UIRenderer.DrawTextShadow(label, x + 5, y + 5, UIColors.Text);

            if (isHover && !string.IsNullOrEmpty(field.Description))
                Tooltip.Set(field.Description);

            // Value area
            int valueX = x + width - 180;
            int valueWidth = 170;
            object currentValue = config.Get<object>(field.Key);

            switch (field.Type)
            {
                case ConfigFieldType.Bool:
                    DrawBoolField(valueX, y, currentValue, field, config, modId);
                    // Whole row clickable for bools
                    if (isHover && UIRenderer.MouseLeftClick && !_isEditingNumber && !_isEditingConfigKey && !_isRebinding)
                    {
                        bool bv = currentValue is bool bb && bb;
                        config.Set(field.Key, !bv);
                        AutoSaveConfig(modId);
                        UIRenderer.ConsumeClick();
                    }
                    break;
                case ConfigFieldType.Int:
                    DrawIntField(valueX, y, valueWidth, currentValue, field, config, modId);
                    break;
                case ConfigFieldType.Float:
                    DrawFloatField(valueX, y, valueWidth, currentValue, field, config, modId);
                    break;
                case ConfigFieldType.Enum:
                    DrawEnumField(valueX, y, valueWidth, currentValue, field, config, modId);
                    break;
                case ConfigFieldType.Key:
                    DrawKeyField(valueX, y, valueWidth, currentValue, field, config, modId);
                    break;
                default:
                    // Read-only display for unsupported types (String, etc)
                    string valueStr = TextUtil.Truncate(currentValue?.ToString() ?? "null", valueWidth - 10);
                    UIRenderer.DrawRect(valueX, y + 2, valueWidth, ItemHeight - 6, UIColors.InputBg);
                    UIRenderer.DrawText(valueStr, valueX + 5, y + 5, UIColors.TextDim);
                    break;
            }
        }

        private static void DrawBoolField(int x, int y, object value, ConfigField field, IModConfig config, string modId)
        {
            bool boolVal = value is bool b && b;
            int boxSize = 18;

            // Checkbox (visual only — click handled by whole row in DrawConfigField)
            UIRenderer.DrawRect(x, y + 3, boxSize, boxSize, UIColors.HeaderBg);
            if (boolVal)
            {
                UIRenderer.DrawRect(x + 4, y + 7, boxSize - 8, boxSize - 8, UIColors.Success);
            }

            UIRenderer.DrawText(boolVal ? "On" : "Off", x + boxSize + 8, y + 5, boolVal ? UIColors.Success : UIColors.TextDim);
        }

        private static void DrawIntField(int x, int y, int width, object value, ConfigField field, IModConfig config, string modId)
        {
            int intVal = value is int i ? i : 0;
            int btnWidth = 25;
            bool isEditing = _isEditingNumber && _editingNumberField == field.Key && _editingNumberModId == modId;
            string fieldId = $"{modId}.{field.Key}";

            // Minus button
            bool minusHover = UIRenderer.IsMouseOver(x, y + 2, btnWidth, ItemHeight - 6) && !isEditing && !_blockInput;
            UIRenderer.DrawRect(x, y + 2, btnWidth, ItemHeight - 6, minusHover ? UIColors.ButtonHover : UIColors.Button);
            UIRenderer.DrawText("-", x + 9, y + 4, UIColors.Text);

            // Handle minus button click and hold
            if (minusHover && UIRenderer.MouseLeft && !isEditing)
            {
                if (UIRenderer.MouseLeftClick)
                {
                    // Start hold tracking
                    _holdingButtonField = fieldId;
                    _holdingButtonModId = modId;
                    _holdingButtonDirection = -1;
                    _holdStartTime = DateTime.Now;
                    _lastRepeatTime = DateTime.Now;

                    // Immediate first click
                    AdjustIntValue(field, config, modId, intVal, -1);
                    UIRenderer.ConsumeClick();
                }
            }

            // Value display / edit area
            int valX = x + btnWidth + 5;
            int valWidth = width - btnWidth * 2 - 10;
            bool valHover = UIRenderer.IsMouseOver(valX, y + 2, valWidth, ItemHeight - 6) && !_isRebinding && !_isEditingConfigKey && !_blockInput;

            if (isEditing)
            {
                // Draw editing state
                UIRenderer.DrawRect(valX, y + 2, valWidth, ItemHeight - 6, UIColors.Warning.WithAlpha(180));
                string displayText = _editingNumberText + "_"; // Show cursor
                UIRenderer.DrawText(displayText, valX + 5, y + 5, UIColors.Text);

                // Handle text input
                UIRenderer.EnableTextInput();
                HandleNumberInput(field, config, modId, false);
            }
            else
            {
                // Draw normal display (left-aligned to match editing)
                UIRenderer.DrawRect(valX, y + 2, valWidth, ItemHeight - 6, valHover ? UIColors.InputFocusBg : UIColors.InputBg);
                string valStr = intVal.ToString();
                UIRenderer.DrawText(valStr, valX + 5, y + 5, UIColors.Text);

                // Click to edit
                if (valHover && UIRenderer.MouseLeftClick)
                {
                    _isEditingNumber = true;
                    _editingNumberField = field.Key;
                    _editingNumberModId = modId;
                    _editingNumberText = intVal.ToString();
                    _editingNumberFirstKeyPressed = false;
                    UIRenderer.ConsumeClick();
                }
            }

            // Plus button
            int plusX = x + width - btnWidth;
            bool plusHover = UIRenderer.IsMouseOver(plusX, y + 2, btnWidth, ItemHeight - 6) && !isEditing && !_blockInput;
            UIRenderer.DrawRect(plusX, y + 2, btnWidth, ItemHeight - 6, plusHover ? UIColors.ButtonHover : UIColors.Button);
            UIRenderer.DrawText("+", plusX + 8, y + 4, UIColors.Text);

            // Handle plus button click and hold
            if (plusHover && UIRenderer.MouseLeft && !isEditing)
            {
                if (UIRenderer.MouseLeftClick)
                {
                    // Start hold tracking
                    _holdingButtonField = fieldId;
                    _holdingButtonModId = modId;
                    _holdingButtonDirection = 1;
                    _holdStartTime = DateTime.Now;
                    _lastRepeatTime = DateTime.Now;

                    // Immediate first click
                    AdjustIntValue(field, config, modId, intVal, 1);
                    UIRenderer.ConsumeClick();
                }
            }
        }

        private static void AdjustIntValue(ConfigField field, IModConfig config, string modId, int currentVal, int direction)
        {
            int step = field.Step > 0 ? (int)field.Step : 1;
            int newVal = currentVal + (step * direction);
            if (field.Min.HasValue && newVal < field.Min.Value) newVal = (int)field.Min.Value;
            if (field.Max.HasValue && newVal > field.Max.Value) newVal = (int)field.Max.Value;
            config.Set(field.Key, newVal);
            AutoSaveConfig(modId);
        }

        private static void DrawFloatField(int x, int y, int width, object value, ConfigField field, IModConfig config, string modId)
        {
            float floatVal = value is float f ? f : (value is double d ? (float)d : 0f);
            int btnWidth = 25;
            bool isEditing = _isEditingNumber && _editingNumberField == field.Key && _editingNumberModId == modId;
            string fieldId = $"{modId}.{field.Key}";

            // Minus button
            bool minusHover = UIRenderer.IsMouseOver(x, y + 2, btnWidth, ItemHeight - 6) && !isEditing && !_blockInput;
            UIRenderer.DrawRect(x, y + 2, btnWidth, ItemHeight - 6, minusHover ? UIColors.ButtonHover : UIColors.Button);
            UIRenderer.DrawText("-", x + 9, y + 4, UIColors.Text);

            // Handle minus button click and hold
            if (minusHover && UIRenderer.MouseLeft && !isEditing)
            {
                if (UIRenderer.MouseLeftClick)
                {
                    _holdingButtonField = fieldId;
                    _holdingButtonModId = modId;
                    _holdingButtonDirection = -1;
                    _holdStartTime = DateTime.Now;
                    _lastRepeatTime = DateTime.Now;

                    AdjustFloatValue(field, config, modId, floatVal, -1);
                    UIRenderer.ConsumeClick();
                }
            }

            // Value display / edit area
            int valX = x + btnWidth + 5;
            int valWidth = width - btnWidth * 2 - 10;
            bool valHover = UIRenderer.IsMouseOver(valX, y + 2, valWidth, ItemHeight - 6) && !_isRebinding && !_isEditingConfigKey && !_blockInput;

            if (isEditing)
            {
                UIRenderer.DrawRect(valX, y + 2, valWidth, ItemHeight - 6, UIColors.Warning.WithAlpha(180));
                string displayText = _editingNumberText + "_"; // Show cursor
                UIRenderer.DrawText(displayText, valX + 5, y + 5, UIColors.Text);

                UIRenderer.EnableTextInput();
                HandleNumberInput(field, config, modId, true);
            }
            else
            {
                // Draw normal display (left-aligned to match editing)
                UIRenderer.DrawRect(valX, y + 2, valWidth, ItemHeight - 6, valHover ? UIColors.InputFocusBg : UIColors.InputBg);
                string valStr = floatVal.ToString("F2");
                UIRenderer.DrawText(valStr, valX + 5, y + 5, UIColors.Text);

                if (valHover && UIRenderer.MouseLeftClick)
                {
                    _isEditingNumber = true;
                    _editingNumberField = field.Key;
                    _editingNumberModId = modId;
                    _editingNumberText = floatVal.ToString("F2");
                    _editingNumberFirstKeyPressed = false;
                    UIRenderer.ConsumeClick();
                }
            }

            // Plus button
            int plusX = x + width - btnWidth;
            bool plusHover = UIRenderer.IsMouseOver(plusX, y + 2, btnWidth, ItemHeight - 6) && !isEditing && !_blockInput;
            UIRenderer.DrawRect(plusX, y + 2, btnWidth, ItemHeight - 6, plusHover ? UIColors.ButtonHover : UIColors.Button);
            UIRenderer.DrawText("+", plusX + 8, y + 4, UIColors.Text);

            if (plusHover && UIRenderer.MouseLeft && !isEditing)
            {
                if (UIRenderer.MouseLeftClick)
                {
                    _holdingButtonField = fieldId;
                    _holdingButtonModId = modId;
                    _holdingButtonDirection = 1;
                    _holdStartTime = DateTime.Now;
                    _lastRepeatTime = DateTime.Now;

                    AdjustFloatValue(field, config, modId, floatVal, 1);
                    UIRenderer.ConsumeClick();
                }
            }
        }

        private static void AdjustFloatValue(ConfigField field, IModConfig config, string modId, float currentVal, int direction)
        {
            float step = field.Step > 0 ? (float)field.Step : 0.1f;
            float newVal = currentVal + (step * direction);
            if (field.Min.HasValue && newVal < field.Min.Value) newVal = (float)field.Min.Value;
            if (field.Max.HasValue && newVal > field.Max.Value) newVal = (float)field.Max.Value;
            config.Set(field.Key, newVal);
            AutoSaveConfig(modId);
        }

        private static void DrawEnumField(int x, int y, int width, object value, ConfigField field, IModConfig config, string modId)
        {
            string currentVal = value?.ToString() ?? "";
            var options = field.Options ?? new List<string>();
            int currentIndex = options.IndexOf(currentVal);
            if (currentIndex < 0) currentIndex = 0;

            int btnWidth = 25;

            // Left arrow
            bool leftHover = UIRenderer.IsMouseOver(x, y + 2, btnWidth, ItemHeight - 6) && !_blockInput;
            UIRenderer.DrawRect(x, y + 2, btnWidth, ItemHeight - 6, leftHover ? UIColors.ButtonHover : UIColors.Button);
            UIRenderer.DrawText("<", x + 9, y + 4, UIColors.Text);

            if (leftHover && UIRenderer.MouseLeftClick && options.Count > 0)
            {
                currentIndex = (currentIndex - 1 + options.Count) % options.Count;
                config.Set(field.Key, options[currentIndex]);
                AutoSaveConfig(modId);
                UIRenderer.ConsumeClick();
            }

            // Value display
            int valX = x + btnWidth + 5;
            int valWidth = width - btnWidth * 2 - 10;
            UIRenderer.DrawRect(valX, y + 2, valWidth, ItemHeight - 6, UIColors.InputBg);
            string displayVal = TextUtil.Truncate(currentVal, valWidth - 20);
            UIRenderer.DrawText(displayVal, valX + 10, y + 5, UIColors.Text);

            // Right arrow
            int rightX = x + width - btnWidth;
            bool rightHover = UIRenderer.IsMouseOver(rightX, y + 2, btnWidth, ItemHeight - 6) && !_blockInput;
            UIRenderer.DrawRect(rightX, y + 2, btnWidth, ItemHeight - 6, rightHover ? UIColors.ButtonHover : UIColors.Button);
            UIRenderer.DrawText(">", rightX + 8, y + 4, UIColors.Text);

            if (rightHover && UIRenderer.MouseLeftClick && options.Count > 0)
            {
                currentIndex = (currentIndex + 1) % options.Count;
                config.Set(field.Key, options[currentIndex]);
                AutoSaveConfig(modId);
                UIRenderer.ConsumeClick();
            }
        }

        private static void DrawKeyField(int x, int y, int width, object value, ConfigField field, IModConfig config, string modId)
        {
            string currentKey = value?.ToString() ?? "";
            bool isEditing = _isEditingConfigKey && _editingConfigKeyField == field.Key && _editingConfigKeyModId == modId;

            bool hover = UIRenderer.IsMouseOver(x, y + 2, width, ItemHeight - 6) && !_isEditingConfigKey && !_isRebinding && !_blockInput;
            var bgColor = isEditing ? UIColors.Warning.WithAlpha(180) : (hover ? UIColors.ButtonHover : UIColors.HeaderBg);
            UIRenderer.DrawRect(x, y + 2, width, ItemHeight - 6, bgColor);

            string displayText = isEditing ? "Press a key..." : currentKey;
            UIRenderer.DrawText(displayText, x + 10, y + 5, isEditing ? UIColors.Text : UIColors.Success);

            if (hover && UIRenderer.MouseLeftClick)
            {
                _isEditingConfigKey = true;
                _editingConfigKeyField = field.Key;
                _editingConfigKeyModId = modId;
                UIRenderer.RegisterKeyInputBlock("mod-menu"); // Set immediately for Update phase blocking
                UIRenderer.ConsumeClick();
            }

            // Handle key capture when editing this field
            if (isEditing)
            {
                UIRenderer.EnableTextInput();

                for (int keyCode = 1; keyCode < 256; keyCode++)
                {
                    if (keyCode == KeyCode.Escape)
                    {
                        if (InputState.IsKeyJustPressed(KeyCode.Escape))
                        {
                            _isEditingConfigKey = false;
                            _editingConfigKeyField = null;
                            _editingConfigKeyModId = null;
                            UIRenderer.DisableTextInput();
                        }
                        continue;
                    }

                    // Skip modifier keys alone
                    if (keyCode == KeyCode.LeftControl || keyCode == KeyCode.RightControl ||
                        keyCode == KeyCode.LeftShift || keyCode == KeyCode.RightShift ||
                        keyCode == KeyCode.LeftAlt || keyCode == KeyCode.RightAlt)
                        continue;

                    if (InputState.IsKeyJustPressed(keyCode))
                    {
                        string keyName = KeyCode.GetName(keyCode);
                        config.Set(field.Key, keyName);
                        AutoSaveConfig(modId);
                        _isEditingConfigKey = false;
                        _editingConfigKeyField = null;
                        _editingConfigKeyModId = null;
                        UIRenderer.DisableTextInput();
                        _log?.Info($"[UI] Config key {field.Key} set to {keyName}");
                        return;
                    }
                }
            }
        }

        private static void DrawKeybindsTab(int x, int y, int width, int height)
        {
            // Header
            if (_isRebinding)
            {
                UIRenderer.DrawTextShadow("Press any key to bind (ESC to cancel)", x, y, UIColors.Warning);
            }
            else
            {
                UIRenderer.DrawTextShadow("Registered Keybinds (click to rebind)", x, y, UIColors.Text);
            }
            y += 25;

            var keybinds = KeybindManager.GetAllKeybinds();
            var conflicts = KeybindManager.GetConflicts();
            int listHeight = height - 60;
            int visibleItems = listHeight / ItemHeight;
            int scrollIndicatorWidth = 10;
            int contentWidth = width - scrollIndicatorWidth;

            // Group keybinds by mod
            var groupedKeybinds = keybinds.GroupBy(kb => kb.ModId).ToList();

            // Calculate total items including section headers
            int totalDisplayItems = 0;
            foreach (var group in groupedKeybinds)
            {
                totalDisplayItems++; // Section header
                totalDisplayItems += group.Count(); // Keybinds in group
            }

            // Clamp scroll offset
            int maxScroll = Math.Max(0, totalDisplayItems - visibleItems);
            _scrollOffset = Math.Min(_scrollOffset, maxScroll);

            // Build flat list of display items with type info
            var displayItems = new List<(string type, object data, string modId)>();
            foreach (var group in groupedKeybinds)
            {
                displayItems.Add(("header", group.Key, group.Key));
                foreach (var kb in group)
                {
                    displayItems.Add(("keybind", kb, kb.ModId));
                }
            }

            // Draw visible items
            int currentY = y;
            for (int i = _scrollOffset; i < displayItems.Count && currentY < y + listHeight - ItemHeight; i++)
            {
                var item = displayItems[i];

                if (item.type == "header")
                {
                    // Draw section header
                    string modName = item.modId;
                    // Try to get friendly name from mod manifest
                    var mod = PluginLoader.Mods.FirstOrDefault(m => m.Manifest.Id == item.modId);
                    if (mod != null) modName = mod.Manifest.Name;

                    int headerHeight = ItemHeight - 4;

                    UIRenderer.DrawRect(x, currentY, contentWidth, headerHeight, UIColors.SectionBg);
                    // Divider line at top
                    UIRenderer.DrawRect(x, currentY, contentWidth, 2, UIColors.Divider);
                    UIRenderer.DrawTextShadow(modName, x + 8, currentY + 5, UIColors.Warning);

                    currentY += headerHeight + 4;
                }
                else if (item.type == "keybind")
                {
                    var kb = (Keybind)item.data;
                    bool isRebindingThis = _isRebinding && _rebindingKeybindId == kb.Id;
                    bool hasConflict = conflicts.Any(c => c.Keybind1.Id == kb.Id || c.Keybind2.Id == kb.Id);
                    bool rowHover = UIRenderer.IsMouseOver(x, currentY, contentWidth, ItemHeight - 2) && !_blockInput;

                    var kbBg = isRebindingThis ? UIColors.Warning.WithAlpha(180) : (rowHover && !_isRebinding ? UIColors.ItemHoverBg : UIColors.ItemBg);
                    UIRenderer.DrawRect(x, currentY, contentWidth, ItemHeight - 2, kbBg);

                    // Keybind label (indented under header, truncated before key + reset buttons)
                    string label = TextUtil.Truncate(kb.Label, contentWidth - 185);
                    UIRenderer.DrawTextShadow(label, x + 15, currentY + 5, UIColors.Text);

                    // Key combo button
                    int resetBtnW = 45;
                    int keyX = x + contentWidth - 120 - resetBtnW - 5;
                    int keyW = 110;
                    var keyBg = hasConflict ? UIColors.Error : UIColors.HeaderBg;
                    UIRenderer.DrawRect(keyX, currentY + 2, keyW, ItemHeight - 6, keyBg);

                    string keyText = isRebindingThis ? "..." : (kb.CurrentKey.Key == KeyCode.None ? "Unbound" : kb.CurrentKey.ToString());
                    var keyTextColor = kb.CurrentKey.Key == KeyCode.None ? UIColors.TextDim : (hasConflict ? UIColors.Text : UIColors.Success);
                    if (isRebindingThis) keyTextColor = UIColors.Text;
                    UIRenderer.DrawText(keyText, keyX + 5, currentY + 5, keyTextColor);

                    // Per-keybind reset button
                    int resetBtnX = x + contentWidth - resetBtnW - 5;
                    int resetBtnY = currentY + 3;
                    int resetBtnH = ItemHeight - 8;
                    bool kbResetHover = UIRenderer.IsMouseOver(resetBtnX, resetBtnY, resetBtnW, resetBtnH) && !_isRebinding && !_blockInput;
                    UIRenderer.DrawRect(resetBtnX, resetBtnY, resetBtnW, resetBtnH, kbResetHover ? UIColors.ButtonHover : UIColors.Button);
                    UIRenderer.DrawText("Reset", resetBtnX + 4, resetBtnY + 1, UIColors.Text);

                    if (kbResetHover && UIRenderer.MouseLeftClick)
                    {
                        KeybindManager.ResetToDefault(kb.Id);
                        UIRenderer.ConsumeClick();
                    }
                    // Click row to start rebinding, or re-click while rebinding to unbind
                    else if (rowHover && UIRenderer.MouseLeftClick && !kbResetHover)
                    {
                        if (isRebindingThis)
                        {
                            // Re-click while waiting = unbind
                            KeybindManager.SetBinding(kb.Id, new KeyCombo());
                            _log?.Info($"[UI] Unbound keybind {kb.Id}");
                            _isRebinding = false;
                            _rebindingKeybindId = null;
                            _rebindWaitingForKey = false;
                            UIRenderer.DisableTextInput();
                        }
                        else if (!_isRebinding)
                        {
                            _isRebinding = true;
                            _rebindingKeybindId = kb.Id;
                            _rebindWaitingForKey = true;
                            UIRenderer.RegisterKeyInputBlock("mod-menu");
                        }
                        UIRenderer.ConsumeClick();
                    }

                    currentY += ItemHeight;
                }
            }

            // Draw scroll indicator
            DrawScrollIndicator(x + contentWidth + 2, y, listHeight, _scrollOffset, totalDisplayItems, visibleItems);

            // Draw buttons at bottom
            int buttonY = y + height - 55;

            // Reset All button
            bool resetHover = UIRenderer.IsMouseOver(x, buttonY, ButtonWidth + 20, ButtonHeight) && !_isRebinding && !_blockInput;
            UIRenderer.DrawRect(x, buttonY, ButtonWidth + 20, ButtonHeight, resetHover ? UIColors.ButtonHover : UIColors.Button);
            UIRenderer.DrawText("Reset All", x + 15, buttonY + 3, UIColors.Text);

            if (resetHover && UIRenderer.MouseLeftClick)
            {
                ResetAllKeybinds();
                UIRenderer.ConsumeClick();
            }

            // Conflict warning
            if (conflicts.Count > 0)
            {
                UIRenderer.DrawText($"! {conflicts.Count} conflict(s) detected", x + width - 180, buttonY + 3, UIColors.Error);
            }
        }

        private static void HandleKeybindCapture()
        {
            // Check for any key press (keyboard keys are 1-255)
            for (int keyCode = 1; keyCode < 256; keyCode++)
            {
                // Skip modifier keys alone
                if (keyCode == KeyCode.LeftControl || keyCode == KeyCode.RightControl ||
                    keyCode == KeyCode.LeftShift || keyCode == KeyCode.RightShift ||
                    keyCode == KeyCode.LeftAlt || keyCode == KeyCode.RightAlt)
                    continue;

                if (InputState.IsKeyJustPressed(keyCode))
                {
                    ApplyRebinding(keyCode);
                    return;
                }
            }

            // Check for mouse button presses (exclude MouseLeft - needed for UI interaction)
            int[] mouseButtons = { KeyCode.MouseRight, KeyCode.MouseMiddle };
            foreach (int mouseButton in mouseButtons)
            {
                if (InputState.IsKeyJustPressed(mouseButton))
                {
                    ApplyRebinding(mouseButton);
                    return;
                }
            }
        }

        private static void ApplyRebinding(int keyCode)
        {
            // Build new key combo
            var newCombo = new KeyCombo(
                keyCode,
                InputState.IsCtrlDown(),
                InputState.IsShiftDown(),
                InputState.IsAltDown()
            );

            // Apply the binding
            KeybindManager.SetBinding(_rebindingKeybindId, newCombo);
            _log?.Info($"[UI] Rebound {_rebindingKeybindId} to {newCombo}");

            _isRebinding = false;
            _rebindingKeybindId = null;
            _rebindWaitingForKey = false;
            UIRenderer.DisableTextInput();
        }

        private static void DrawConflictsTab(int x, int y, int width, int height)
        {
            // Lazy scan on first render
            if (!_conflictScanDone)
            {
                ConflictScanner.Initialize(_log);
                _conflictReport = ConflictScanner.Scan();
                _conflictScanDone = true;

                // Initialize editable load order from current order
                _editedLoadOrder = new List<string>(_conflictReport.LoadOrder);
                _loadOrderChanged = false;
            }

            if (_conflictReport == null) return;

            int scrollIndicatorWidth = 10;
            int contentWidth = width - scrollIndicatorWidth;

            // Build display items list
            var displayItems = new List<ConflictDisplayItem>();

            // Patch conflicts section (always shown)
            displayItems.Add(new ConflictDisplayItem { Type = ConflictItemType.SectionHeader, Label = "Patch Conflicts" });
            if (_conflictReport.PatchConflicts.Count > 0)
            {
                foreach (var pc in _conflictReport.PatchConflicts)
                {
                    displayItems.Add(new ConflictDisplayItem
                    {
                        Type = ConflictItemType.PatchConflictHeader,
                        Label = pc.TargetType + "." + pc.TargetMethod,
                        Severity = pc.Severity
                    });
                    foreach (var p in pc.Patches)
                    {
                        string detail = p.ModName + "  " + p.PatchType.ToLower();
                        if (p.ReturnsBool) detail += " (bool)";
                        displayItems.Add(new ConflictDisplayItem
                        {
                            Type = ConflictItemType.PatchDetail,
                            Label = detail,
                            ModId = p.ModId
                        });
                    }
                }
            }
            else
            {
                displayItems.Add(new ConflictDisplayItem { Type = ConflictItemType.PatchDetail, Label = "No conflicts detected" });
            }
            displayItems.Add(new ConflictDisplayItem { Type = ConflictItemType.Spacer });

            // --- Load Order ---
            if (_loadOrderChanged)
            {
                displayItems.Add(new ConflictDisplayItem
                {
                    Type = ConflictItemType.Warning,
                    Label = "Load Order Changed - Restart Required"
                });
            }

            for (int idx = 0; idx < _editedLoadOrder.Count; idx++)
            {
                var modId = _editedLoadOrder[idx];
                var mod = PluginLoader.Mods.FirstOrDefault(m => m.Manifest.Id == modId);
                string modName = mod != null ? mod.Manifest.Name : modId;
                displayItems.Add(new ConflictDisplayItem
                {
                    Type = ConflictItemType.LoadOrderEntry,
                    Label = (idx + 1) + ". " + modName,
                    ModId = modId,
                    LoadOrderIndex = idx
                });
            }

            // Scrolling
            int buttonBarHeight = _loadOrderChanged ? 32 : 0;
            int listHeight = height - buttonBarHeight;
            int visibleItems = listHeight / ItemHeight;
            int maxScroll = Math.Max(0, displayItems.Count - visibleItems);
            _scrollOffset = Math.Min(_scrollOffset, maxScroll);

            // Handle scroll wheel
            if (UIRenderer.ScrollWheel != 0 && UIRenderer.IsMouseOver(x, y, width, listHeight) && !_blockInput)
            {
                _scrollOffset -= UIRenderer.ScrollWheel > 0 ? 1 : -1;
                _scrollOffset = Math.Max(0, Math.Min(_scrollOffset, maxScroll));
            }

            // Draw items
            int currentY = y;
            for (int i = _scrollOffset; i < displayItems.Count && currentY < y + listHeight - ItemHeight; i++)
            {
                var item = displayItems[i];

                // Skip dragged item in its original position
                if (_isDraggingLoadOrder && item.Type == ConflictItemType.LoadOrderEntry &&
                    item.LoadOrderIndex == _dragLoadOrderIndex)
                {
                    currentY += ItemHeight;
                    continue;
                }

                switch (item.Type)
                {
                    case ConflictItemType.SectionHeader:
                        int headerHeight = ItemHeight - 4;
                        UIRenderer.DrawRect(x, currentY, contentWidth, headerHeight, UIColors.SectionBg);
                        UIRenderer.DrawRect(x, currentY, contentWidth, 2, UIColors.Divider);
                        UIRenderer.DrawTextShadow(item.Label, x + 8, currentY + 5, UIColors.Warning);
                        currentY += headerHeight + 4;
                        break;

                    case ConflictItemType.Spacer:
                        currentY += 10;
                        break;

                    case ConflictItemType.PatchConflictHeader:
                        var sevColor = UIColors.Error;
                        UIRenderer.DrawRect(x, currentY, contentWidth, ItemHeight - 2, UIColors.ItemBg);
                        UIRenderer.DrawText("! HIGH", x + 5, currentY + 5, sevColor);
                        UIRenderer.DrawTextShadow(item.Label, x + 60, currentY + 5, UIColors.Text);
                        currentY += ItemHeight;
                        break;

                    case ConflictItemType.PatchDetail:
                        UIRenderer.DrawText(item.Label, x + 25, currentY + 5, UIColors.TextDim);
                        currentY += ItemHeight;
                        break;

                    case ConflictItemType.Warning:
                        UIRenderer.DrawText(item.Label, x + 8, currentY + 5, UIColors.Error);
                        currentY += ItemHeight;
                        break;

                    case ConflictItemType.LoadOrderEntry:
                        // Draw drop indicator line between items
                        if (_isDraggingLoadOrder && _dragLoadOrderTargetIndex != _dragLoadOrderIndex)
                        {
                            // Moving up: line at top of target row
                            if (_dragLoadOrderTargetIndex < _dragLoadOrderIndex
                                && item.LoadOrderIndex == _dragLoadOrderTargetIndex)
                            {
                                var lineColor = new Color4(255, 255, 255);
                                UIRenderer.DrawRect(x + 2, currentY - 2, contentWidth - 4, 2, lineColor);
                                UIRenderer.DrawRect(x, currentY - 4, 6, 6, lineColor);
                                UIRenderer.DrawRect(x + contentWidth - 6, currentY - 4, 6, 6, lineColor);
                            }
                            // Moving down: line at top of the row AFTER the target
                            if (_dragLoadOrderTargetIndex > _dragLoadOrderIndex
                                && item.LoadOrderIndex == _dragLoadOrderTargetIndex + 1)
                            {
                                var lineColor = new Color4(255, 255, 255);
                                UIRenderer.DrawRect(x + 2, currentY - 2, contentWidth - 4, 2, lineColor);
                                UIRenderer.DrawRect(x, currentY - 4, 6, 6, lineColor);
                                UIRenderer.DrawRect(x + contentWidth - 6, currentY - 4, 6, 6, lineColor);
                            }
                        }
                        DrawLoadOrderEntry(x, currentY, contentWidth, item);
                        currentY += ItemHeight;
                        break;
                }
            }

            // Draw scroll indicator
            DrawScrollIndicator(x + contentWidth + 2, y, listHeight, _scrollOffset, displayItems.Count, visibleItems);

            // Revert Changes button (only shown when order has changed)
            if (_loadOrderChanged)
            {
                int btnW = 120;
                int btnH = ButtonHeight;
                int btnX = x + width - btnW;
                int btnY = y + height - btnH - 4;
                bool btnHover = UIRenderer.IsMouseOver(btnX, btnY, btnW, btnH) && !_blockInput;
                UIRenderer.DrawRect(btnX, btnY, btnW, btnH, btnHover ? UIColors.ButtonHover : UIColors.Button);
                UIRenderer.DrawText("Revert Changes", btnX + 8, btnY + 3, UIColors.Text);

                if (btnHover && UIRenderer.MouseLeftClick)
                {
                    _editedLoadOrder = new List<string>(_conflictReport.LoadOrder);
                    _loadOrderChanged = false;

                    // Delete load-order.json
                    try
                    {
                        var orderPath = DependencyResolver.GetLoadOrderPathFromCore(CoreConfig.Instance.CorePath);
                        if (System.IO.File.Exists(orderPath))
                            System.IO.File.Delete(orderPath);
                        _log?.Info("[UI] Reverted load order changes");
                    }
                    catch (Exception ex)
                    {
                        _log?.Error($"[UI] Failed to delete load-order.json: {ex.Message}");
                    }
                    UIRenderer.ConsumeClick();
                }
            }

            // Handle drag-in-progress (draw floating item)
            if (_isDraggingLoadOrder && _dragLoadOrderIndex >= 0 && _dragLoadOrderIndex < _editedLoadOrder.Count)
            {
                // Draw indicator for "move to last" (no next row to attach to)
                if (_dragLoadOrderTargetIndex != _dragLoadOrderIndex &&
                    _dragLoadOrderTargetIndex > _dragLoadOrderIndex &&
                    _dragLoadOrderTargetIndex == _editedLoadOrder.Count - 1)
                {
                    var lineColor = new Color4(255, 255, 255);
                    UIRenderer.DrawRect(x + 2, currentY - 2, contentWidth - 4, 2, lineColor);
                    UIRenderer.DrawRect(x, currentY - 4, 6, 6, lineColor);
                    UIRenderer.DrawRect(x + contentWidth - 6, currentY - 4, 6, 6, lineColor);
                }

                int floatY = UIRenderer.MouseY - _dragLoadOrderMouseOffsetY;
                var modId = _editedLoadOrder[_dragLoadOrderIndex];
                var mod = PluginLoader.Mods.FirstOrDefault(m => m.Manifest.Id == modId);
                string modName = mod != null ? mod.Manifest.Name : modId;
                string label = (_dragLoadOrderIndex + 1) + ". " + modName;

                UIRenderer.DrawRect(x, floatY, contentWidth, ItemHeight - 2, UIColors.Accent);
                UIRenderer.DrawTextShadow(label, x + 8, floatY + 5, UIColors.Text);

                // Determine target position based on mouse Y
                HandleLoadOrderDragMove(y, displayItems);

                // Drop on mouse release
                if (!UIRenderer.MouseLeft)
                {
                    HandleLoadOrderDrop();
                }
            }
        }

        private static void DrawLoadOrderEntry(int x, int currentY, int contentWidth, ConflictDisplayItem item)
        {
            bool rowHover = UIRenderer.IsMouseOver(x, currentY, contentWidth, ItemHeight - 2) && !_blockInput && !_isDraggingLoadOrder;
            var bg = rowHover ? UIColors.ItemHoverBg : UIColors.ItemBg;
            UIRenderer.DrawRect(x, currentY, contentWidth, ItemHeight - 2, bg);

            // Up/down arrow buttons on the right
            int arrowW = 20;
            int arrowH = ItemHeight - 4;
            int arrowX = x + contentWidth - arrowW * 2 - 8;
            int arrowY = currentY + 1;
            int idx = item.LoadOrderIndex;

            // Up arrow
            bool canMoveUp = idx > 0;
            if (canMoveUp)
            {
                bool upHover = UIRenderer.IsMouseOver(arrowX, arrowY, arrowW, arrowH) && !_blockInput && !_isDraggingLoadOrder;
                UIRenderer.DrawRect(arrowX, arrowY, arrowW, arrowH, upHover ? UIColors.ButtonHover : UIColors.Button);
                UIRenderer.DrawText("^", arrowX + 6, arrowY + 2, UIColors.Text);
                if (upHover && UIRenderer.MouseLeftClick)
                {
                    MoveLoadOrderEntry(idx, idx - 1);
                    UIRenderer.ConsumeClick();
                }
            }

            // Down arrow
            bool canMoveDown = idx < _editedLoadOrder.Count - 1;
            int downX = arrowX + arrowW + 2;
            if (canMoveDown)
            {
                bool downHover = UIRenderer.IsMouseOver(downX, arrowY, arrowW, arrowH) && !_blockInput && !_isDraggingLoadOrder;
                UIRenderer.DrawRect(downX, arrowY, arrowW, arrowH, downHover ? UIColors.ButtonHover : UIColors.Button);
                UIRenderer.DrawText("v", downX + 6, arrowY + 2, UIColors.Text);
                if (downHover && UIRenderer.MouseLeftClick)
                {
                    MoveLoadOrderEntry(idx, idx + 1);
                    UIRenderer.ConsumeClick();
                }
            }

            // Mod label
            UIRenderer.DrawTextShadow(item.Label, x + 8, currentY + 5, UIColors.Text);

            // Show dependency info
            var mod = PluginLoader.Mods.FirstOrDefault(m => m.Manifest.Id == item.ModId);
            if (mod != null && mod.Manifest.Dependencies.Count > 0)
            {
                string depText = "(after " + string.Join(", ", mod.Manifest.Dependencies) + ")";
                depText = TextUtil.Truncate(depText, arrowX - x - 20);
                UIRenderer.DrawText(depText, arrowX - TextUtil.MeasureWidth(depText) - 5, currentY + 5, UIColors.TextDim);
            }

            // Start drag on mouse down (on the label area, not on arrows)
            if (rowHover && UIRenderer.MouseLeftClick &&
                !(UIRenderer.MouseX >= arrowX && UIRenderer.MouseX < downX + arrowW))
            {
                _isDraggingLoadOrder = true;
                _dragLoadOrderIndex = item.LoadOrderIndex;
                _dragLoadOrderTargetIndex = item.LoadOrderIndex;
                _dragLoadOrderMouseOffsetY = UIRenderer.MouseY - currentY;
                UIRenderer.ConsumeClick();
            }
        }

        /// <summary>
        /// Move a load order entry from one index to another, with dependency validation.
        /// </summary>
        private static void MoveLoadOrderEntry(int fromIdx, int toIdx)
        {
            if (fromIdx < 0 || fromIdx >= _editedLoadOrder.Count ||
                toIdx < 0 || toIdx >= _editedLoadOrder.Count)
                return;

            string movingModId = _editedLoadOrder[fromIdx];
            var movingMod = PluginLoader.Mods.FirstOrDefault(m => m.Manifest.Id == movingModId);

            if (movingMod != null && toIdx < fromIdx)
            {
                // Moving up: can't move before a dependency
                foreach (var depId in movingMod.Manifest.Dependencies)
                {
                    int depIdx = _editedLoadOrder.IndexOf(depId);
                    if (depIdx >= 0 && depIdx >= toIdx)
                    {
                        _log?.Warn($"[UI] Can't move {movingModId} before its dependency {depId}");
                        return;
                    }
                }
            }

            if (toIdx > fromIdx)
            {
                // Moving down: can't move after a mod that depends on this one
                string otherId = _editedLoadOrder[toIdx];
                var otherMod = PluginLoader.Mods.FirstOrDefault(m => m.Manifest.Id == otherId);
                if (otherMod != null && otherMod.Manifest.Dependencies.Contains(movingModId))
                {
                    _log?.Warn($"[UI] Can't move {movingModId} after {otherId} (which depends on it)");
                    return;
                }
            }

            // Perform the move
            string entry = _editedLoadOrder[fromIdx];
            _editedLoadOrder.RemoveAt(fromIdx);
            _editedLoadOrder.Insert(toIdx, entry);

            // Save to load-order.json
            try
            {
                var corePath = CoreConfig.Instance.CorePath;
                DependencyResolver.SaveLoadOrder(corePath, _editedLoadOrder);
                _loadOrderChanged = true;
                _log?.Info($"[UI] Saved load order: {string.Join(", ", _editedLoadOrder)}");
            }
            catch (Exception ex)
            {
                _log?.Error($"[UI] Failed to save load order: {ex.Message}");
            }
        }

        private static void HandleLoadOrderDragMove(int listStartY, List<ConflictDisplayItem> displayItems)
        {
            // Find which load order slot the mouse is over.
            // Must match the draw loop exactly — skip dragged item, use correct heights.
            int mouseY = UIRenderer.MouseY;
            int currentY = listStartY;

            for (int i = _scrollOffset; i < displayItems.Count; i++)
            {
                var item = displayItems[i];

                // Skip the dragged item's gap (same as draw loop)
                if (item.Type == ConflictItemType.LoadOrderEntry &&
                    item.LoadOrderIndex == _dragLoadOrderIndex)
                {
                    currentY += ItemHeight;
                    continue;
                }

                // Use same heights as draw loop
                int itemH;
                switch (item.Type)
                {
                    case ConflictItemType.SectionHeader: itemH = ItemHeight; break;
                    case ConflictItemType.Spacer: itemH = 10; break;
                    default: itemH = ItemHeight; break;
                }

                if (item.Type == ConflictItemType.LoadOrderEntry)
                {
                    if (mouseY >= currentY && mouseY < currentY + itemH)
                    {
                        _dragLoadOrderTargetIndex = item.LoadOrderIndex;
                        break;
                    }
                }
                currentY += itemH;
            }
        }

        private static void HandleLoadOrderDrop()
        {
            _isDraggingLoadOrder = false;

            if (_dragLoadOrderIndex == _dragLoadOrderTargetIndex || _dragLoadOrderIndex < 0)
                return;

            int fromIdx = _dragLoadOrderIndex;
            int toIdx = _dragLoadOrderTargetIndex;

            // Validate: can't move a mod before its dependencies
            string movingModId = _editedLoadOrder[fromIdx];
            var movingMod = PluginLoader.Mods.FirstOrDefault(m => m.Manifest.Id == movingModId);

            if (movingMod != null && toIdx < fromIdx)
            {
                // Moving up: check that none of its dependencies are at or after the target position
                foreach (var depId in movingMod.Manifest.Dependencies)
                {
                    int depIdx = _editedLoadOrder.IndexOf(depId);
                    if (depIdx >= 0 && depIdx >= toIdx)
                    {
                        _log?.Warn($"[UI] Can't move {movingModId} before its dependency {depId}");
                        return;
                    }
                }
            }

            if (toIdx > fromIdx)
            {
                // Moving down: check that no mod at or before the target position depends on this mod
                for (int i = fromIdx + 1; i <= toIdx; i++)
                {
                    string otherId = _editedLoadOrder[i];
                    var otherMod = PluginLoader.Mods.FirstOrDefault(m => m.Manifest.Id == otherId);
                    if (otherMod != null && otherMod.Manifest.Dependencies.Contains(movingModId))
                    {
                        _log?.Warn($"[UI] Can't move {movingModId} after {otherId} (which depends on it)");
                        return;
                    }
                }
            }

            // Perform the move
            string item = _editedLoadOrder[fromIdx];
            _editedLoadOrder.RemoveAt(fromIdx);
            _editedLoadOrder.Insert(toIdx, item);

            // Save to load-order.json
            try
            {
                var corePath = CoreConfig.Instance.CorePath;
                DependencyResolver.SaveLoadOrder(corePath, _editedLoadOrder);
                _loadOrderChanged = true;
                _log?.Info($"[UI] Saved load order: {string.Join(", ", _editedLoadOrder)}");
            }
            catch (Exception ex)
            {
                _log?.Error($"[UI] Failed to save load order: {ex.Message}");
            }
        }

        // Display item types for the Load Order tab
        private enum ConflictItemType
        {
            SectionHeader,
            PatchConflictHeader,
            PatchDetail,
            LoadOrderEntry,
            Spacer,
            Warning
        }

        private class ConflictDisplayItem
        {
            public ConflictItemType Type;
            public string Label;
            public string Detail;
            public string ModId;
            public ConflictSeverity Severity;
            public int LoadOrderIndex;
        }

        private static void DrawLogsTab(int x, int y, int width, int height)
        {
            UIRenderer.DrawTextShadow("Recent Log Messages", x, y, UIColors.Text);
            y += 25;

            var logs = LogManager.GetRecentLogs(50);
            int lineHeight = 18;
            int listHeight = height - 30;
            int visibleLines = listHeight / lineHeight;
            int scrollIndicatorWidth = 10;
            int contentWidth = width - scrollIndicatorWidth;

            // Clamp scroll offset
            int maxScroll = Math.Max(0, logs.Count - visibleLines);
            _scrollOffset = Math.Min(_scrollOffset, maxScroll);

            for (int i = 0; i < visibleLines && i + _scrollOffset < logs.Count; i++)
            {
                var entry = logs[i + _scrollOffset];
                int lineY = y + i * lineHeight;

                // Color based on level
                var color = entry.Level == LogLevel.Error ? UIColors.Error :
                            entry.Level == LogLevel.Warn ? UIColors.Warning :
                            entry.Level == LogLevel.Debug ? UIColors.TextDim : UIColors.Text;

                string msg = TextUtil.Truncate($"[{entry.ModId}] {entry.Message}", contentWidth);
                UIRenderer.DrawText(msg, x, lineY, color);
            }

            // Draw scroll indicator
            DrawScrollIndicator(x + contentWidth + 2, y, listHeight, _scrollOffset, logs.Count, visibleLines);

            if (logs.Count == 0)
            {
                UIRenderer.DrawText("No log messages yet", x, y, UIColors.TextDim);
            }
        }

        #region Helper Methods

        /// <summary>
        /// Draws an interactive vertical scrollbar. Supports thumb drag and track click.
        /// </summary>
        private static void DrawScrollIndicator(int x, int y, int height, int scrollOffset, int totalItems, int visibleItems)
        {
            if (totalItems <= visibleItems) return; // No scrolling needed

            int trackWidth = 8;
            int trackX = x;
            int maxScroll = totalItems - visibleItems;

            // Draw track
            UIRenderer.DrawRect(trackX, y, trackWidth, height, UIColors.ScrollTrack);

            // Calculate thumb size and position
            float thumbRatio = (float)visibleItems / totalItems;
            int thumbHeight = Math.Max(20, (int)(height * thumbRatio));
            float scrollRatio = (float)scrollOffset / Math.Max(1, maxScroll);
            int thumbY = y + (int)((height - thumbHeight) * scrollRatio);

            // Draw thumb with hover/drag highlight
            bool thumbHover = UIRenderer.IsMouseOver(trackX, thumbY, trackWidth, thumbHeight) && !_blockInput;
            UIRenderer.DrawRect(trackX, thumbY, trackWidth, thumbHeight,
                (_scrollDragging || thumbHover) ? UIColors.SliderThumbHover : UIColors.ScrollThumb);

            // Click on thumb → start drag
            if (thumbHover && UIRenderer.MouseLeftClick)
            {
                _scrollDragging = true;
                _scrollDragStartY = UIRenderer.MouseY;
                _scrollDragStartOffset = scrollOffset;
                _scrollDragMaxScroll = maxScroll;
                _scrollDragTrackHeight = height - thumbHeight;
                UIRenderer.ConsumeClick();
            }
            // Click on track → jump to position
            else if (!thumbHover && UIRenderer.IsMouseOver(trackX, y, trackWidth, height)
                     && !_blockInput && UIRenderer.MouseLeftClick)
            {
                float clickPct = (float)(UIRenderer.MouseY - y) / height;
                _scrollOffset = (int)(clickPct * maxScroll);
                _scrollOffset = Math.Max(0, Math.Min(_scrollOffset, maxScroll));
                _scrollDragging = true;
                _scrollDragStartY = UIRenderer.MouseY;
                _scrollDragStartOffset = _scrollOffset;
                _scrollDragMaxScroll = maxScroll;
                _scrollDragTrackHeight = height - thumbHeight;
                UIRenderer.ConsumeClick();
            }
        }

        private static bool ModSupportsHotReload(ModInfo mod)
        {
            if (mod?.Instance == null) return false;

            try
            {
                // Check if the mod's type has an OnConfigChanged method declared
                var method = mod.Instance.GetType().GetMethod("OnConfigChanged",
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);

                // If the method exists on the mod's type itself (DeclaredOnly), it supports hot reload
                return method != null;
            }
            catch
            {
                return false;
            }
        }

        private static void CallOnConfigChanged(ModInfo mod)
        {
            if (mod?.Instance == null) return;

            try
            {
                var method = mod.Instance.GetType().GetMethod("OnConfigChanged",
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);

                if (method != null)
                {
                    method.Invoke(mod.Instance, null);
                }
            }
            catch (Exception ex)
            {
                _log?.Error($"[UI] OnConfigChanged failed for {mod.Manifest.Id}: {ex.Message}");
            }
        }

        /// <summary>
        /// Save config immediately and notify mod if it supports hot reload.
        /// </summary>
        private static void AutoSaveConfig(string modId)
        {
            try
            {
                var mod = PluginLoader.Mods.FirstOrDefault(m => m.Manifest.Id == modId);
                if (mod?.Context?.Config != null)
                {
                    mod.Context.Config.Save();

                    if (ModSupportsHotReload(mod))
                    {
                        CallOnConfigChanged(mod);
                    }
                }
            }
            catch (Exception ex)
            {
                _log?.Error($"[UI] AutoSaveConfig failed for {modId}: {ex.Message}");
            }
        }

        /// <summary>
        /// Handle hold-to-repeat for +/- buttons.
        /// </summary>
        private static void HandleHoldRepeat()
        {
            if (string.IsNullOrEmpty(_holdingButtonField))
                return;

            // Stop if mouse released
            if (!UIRenderer.MouseLeft)
            {
                _holdingButtonField = null;
                _holdingButtonModId = null;
                return;
            }

            // Check if we should repeat
            var now = DateTime.Now;
            var holdTime = (now - _holdStartTime).TotalMilliseconds;

            if (holdTime < HoldDelayMs)
                return; // Still in initial delay

            var timeSinceRepeat = (now - _lastRepeatTime).TotalMilliseconds;
            if (timeSinceRepeat < RepeatIntervalMs)
                return; // Not time yet

            try
            {
                // Find the config and field
                var mod = PluginLoader.Mods.FirstOrDefault(m => m.Manifest.Id == _holdingButtonModId);
                if (mod?.Context?.Config == null)
                {
                    _holdingButtonField = null;
                    return;
                }

                // Extract field key from fieldId (modId.fieldKey format)
                string fieldKey = _holdingButtonField;
                if (fieldKey.StartsWith(_holdingButtonModId + "."))
                {
                    fieldKey = fieldKey.Substring(_holdingButtonModId.Length + 1);
                }

                if (!mod.Context.Config.Schema.TryGetValue(fieldKey, out var field))
                {
                    _holdingButtonField = null;
                    return;
                }

                // Adjust the value
                if (field.Type == ConfigFieldType.Int)
                {
                    int currentVal = mod.Context.Config.Get<int>(fieldKey);
                    AdjustIntValue(field, mod.Context.Config, _holdingButtonModId, currentVal, _holdingButtonDirection);
                }
                else if (field.Type == ConfigFieldType.Float)
                {
                    float currentVal = mod.Context.Config.Get<float>(fieldKey);
                    AdjustFloatValue(field, mod.Context.Config, _holdingButtonModId, currentVal, _holdingButtonDirection);
                }
            }
            catch (Exception ex)
            {
                _log?.Error($"[UI] HandleHoldRepeat failed: {ex.Message}");
                _holdingButtonField = null;
                return;
            }

            _lastRepeatTime = now;
        }

        /// <summary>
        /// Handle number input for editing number fields.
        /// </summary>
        private static void HandleNumberInput(ConfigField field, IModConfig config, string modId, bool isFloat)
        {
            // Check for Enter to confirm
            if (InputState.IsKeyJustPressed(KeyCode.Enter) || InputState.IsKeyJustPressed(KeyCode.NumPadEnter))
            {
                ApplyNumberEdit(field, config, modId, isFloat);
                return;
            }

            // Check for number keys (including numpad)
            for (int i = 0; i <= 9; i++)
            {
                // Regular number keys (D0-D9)
                if (InputState.IsKeyJustPressed(KeyCode.D0 + i))
                {
                    // First key replaces the old value
                    if (!_editingNumberFirstKeyPressed)
                    {
                        _editingNumberText = i.ToString();
                        _editingNumberFirstKeyPressed = true;
                    }
                    else if (_editingNumberText.Length < 10) // Max 10 digits
                    {
                        _editingNumberText += i.ToString();
                    }
                    return;
                }
                // Numpad keys
                if (InputState.IsKeyJustPressed(KeyCode.NumPad0 + i))
                {
                    if (!_editingNumberFirstKeyPressed)
                    {
                        _editingNumberText = i.ToString();
                        _editingNumberFirstKeyPressed = true;
                    }
                    else if (_editingNumberText.Length < 10)
                    {
                        _editingNumberText += i.ToString();
                    }
                    return;
                }
            }

            // Decimal point for floats
            if (isFloat && (InputState.IsKeyJustPressed(KeyCode.OemPeriod) || InputState.IsKeyJustPressed(KeyCode.Decimal)))
            {
                if (!_editingNumberFirstKeyPressed)
                {
                    _editingNumberText = "0.";
                    _editingNumberFirstKeyPressed = true;
                }
                else if (!_editingNumberText.Contains(".") && _editingNumberText.Length < 10)
                {
                    _editingNumberText += ".";
                }
                return;
            }

            // Minus sign - toggle negative (doesn't replace old value)
            if (InputState.IsKeyJustPressed(KeyCode.OemMinus) || InputState.IsKeyJustPressed(KeyCode.Subtract))
            {
                if (_editingNumberText.StartsWith("-"))
                    _editingNumberText = _editingNumberText.Substring(1);
                else
                    _editingNumberText = "-" + _editingNumberText;
                return;
            }

            // Backspace - now we're editing, not replacing
            if (InputState.IsKeyJustPressed(KeyCode.Back))
            {
                _editingNumberFirstKeyPressed = true; // User is editing, not replacing
                if (_editingNumberText.Length > 0)
                    _editingNumberText = _editingNumberText.Substring(0, _editingNumberText.Length - 1);
                return;
            }

            // Click outside to confirm
            if (UIRenderer.MouseLeftClick)
            {
                ApplyNumberEdit(field, config, modId, isFloat);
            }
        }

        /// <summary>
        /// Apply the edited number value.
        /// </summary>
        private static void ApplyNumberEdit(ConfigField field, IModConfig config, string modId, bool isFloat)
        {
            if (isFloat)
            {
                if (float.TryParse(_editingNumberText, out float newVal))
                {
                    if (field.Min.HasValue && newVal < field.Min.Value) newVal = (float)field.Min.Value;
                    if (field.Max.HasValue && newVal > field.Max.Value) newVal = (float)field.Max.Value;
                    config.Set(field.Key, newVal);
                    AutoSaveConfig(modId);
                }
            }
            else
            {
                if (int.TryParse(_editingNumberText, out int newVal))
                {
                    if (field.Min.HasValue && newVal < field.Min.Value) newVal = (int)field.Min.Value;
                    if (field.Max.HasValue && newVal > field.Max.Value) newVal = (int)field.Max.Value;
                    config.Set(field.Key, newVal);
                    AutoSaveConfig(modId);
                }
            }

            _isEditingNumber = false;
            _editingNumberField = null;
            _editingNumberModId = null;
            UIRenderer.DisableTextInput();
        }

        private static void ResetConfigToDefaults(ModInfo mod)
        {
            if (mod?.Context?.Config == null) return;

            try
            {
                var config = mod.Context.Config;
                var schema = config.Schema;

                foreach (var field in schema.Values)
                {
                    if (field.Default != null)
                    {
                        config.Set(field.Key, field.Default);
                    }
                }

                AutoSaveConfig(mod.Manifest.Id);
                _log?.Info($"[UI] Reset config to defaults for {mod.Manifest.Id}");
            }
            catch (Exception ex)
            {
                _log?.Error($"[UI] Failed to reset config: {ex.Message}");
            }
        }

        private static void ResetAllKeybinds()
        {
            var keybinds = KeybindManager.GetAllKeybinds();
            foreach (var kb in keybinds)
            {
                KeybindManager.ResetToDefault(kb.Id);
            }
            _log?.Info("[UI] Reset all keybinds to defaults");
        }

        #endregion
    }
}
