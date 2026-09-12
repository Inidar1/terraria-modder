using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using TerrariaModder.Core.Config;
using TerrariaModder.Core.Conflicts;
using TerrariaModder.Core.Input;
using TerrariaModder.Core.Logging;
using TerrariaModder.Core.Manifest;
using TerrariaModder.Core.Net;
using TerrariaModder.Core.Permissions;
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
        private static bool _isEditingValue;
        private static bool _valueEditDrawnThisFrame;
        private static int _valueEditX, _valueEditY, _valueEditWidth, _valueEditHeight;
        private static string _editingValueField;
        private static string _editingValueModId;
        private static string _editingValueText = "";
        private static bool _editingValueFirstKeyPressed; // True after first key replaces old value

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

        // Config tab inner sub-tab: 0=Client, 1=Server
        private static int _configInnerTab = 0;

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

        // Tabs — Players tab added dynamically when admin is connected in multiplayer
        private static readonly string[] TabsBase = { "Mods", "Config", "Keybinds", "Load Order", "Logs" };
        private static readonly string[] TabsWithPlayers = { "Mods", "Config", "Keybinds", "Load Order", "Logs", "Players" };

        private static string[] GetVisibleTabs()
            => (Game.IsMultiplayer && NetSync.LocalPlayerIsAdmin) ? TabsWithPlayers : TabsBase;

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
            _isEditingValue = false;
            _editingValueField = null;
            _editingValueModId = null;
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

            // Clear tooltip and track whether the active value editor remains visible.
            Tooltip.Clear();
            _valueEditDrawnThisFrame = false;

            // Check if a higher-z-order panel should block our input
            _blockInput = UIRenderer.ShouldBlockForHigherPriorityPanel("mod-menu");

            if (_isEditingValue && !_blockInput && UIRenderer.MouseLeftClick &&
                !UIRenderer.IsMouseOver(_valueEditX, _valueEditY, _valueEditWidth, _valueEditHeight))
                CommitActiveValueEdit();

            // Handle Escape to close menu (unless rebinding or editing)
            if (!_isRebinding && !_isEditingConfigKey && !_isEditingValue)
            {
                if (InputState.IsKeyJustPressed(KeyCode.Escape) && !UIRenderer.IsReleasingTextInput)
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
            if (_isRebinding || _isEditingConfigKey || _isEditingValue)
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
            var tabs = GetVisibleTabs();
            if (_selectedTab >= tabs.Length) _selectedTab = 0;
            int tabWidth = (MenuWidth - Padding * 2) / tabs.Length;
            for (int i = 0; i < tabs.Length; i++)
            {
                int tabX = menuX + Padding + i * tabWidth;
                bool isActive = i == _selectedTab;
                bool isHover = UIRenderer.IsMouseOver(tabX, tabY, tabWidth - 2, TabHeight) && !_blockInput;

                var tabColor = isActive ? UIColors.ItemActiveBg : (isHover ? UIColors.ItemHoverBg : UIColors.SectionBg);
                UIRenderer.DrawRect(tabX, tabY, tabWidth - 2, TabHeight, tabColor);
                string tabLabel = TextUtil.Truncate(tabs[i], tabWidth - 12);
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
                case 5: DrawPlayersTab(menuX + Padding, contentY, MenuWidth - Padding * 2, contentHeight); break;
            }

            if (_isEditingValue && !_valueEditDrawnThisFrame) CancelValueEdit();

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
            if (InputState.IsKeyJustPressed(KeyCode.Escape) && !UIRenderer.IsReleasingTextInput)
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
                else if (_isEditingValue)
                {
                    CancelValueEdit();
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
                    // Check if this mod has keybinds — expand detail panel if so
                    var modKeybinds = KeybindManager.GetKeybindsForMod(selectedMod.Manifest.Id).ToList();
                    bool hasKeybind = modKeybinds.Count > 0;
                    int detailHeight = hasKeybind ? 90 : 75;
                    int detailY = y + height - detailHeight;
                    UIRenderer.DrawRect(x, detailY, width, detailHeight, UIColors.HeaderBg);

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

                    if (hasKeybind)
                    {
                        var kb = modKeybinds[0];
                        string keybindText = "Keybind: " + kb.CurrentKey + " (" + kb.Label + ")";
                        UIRenderer.DrawText(keybindText, x + 5, detailY + 72, UIColors.TextDim);
                    }
                }
            }
        }

        private static void DrawConfigTab(int x, int y, int width, int height)
        {
            // Inner sub-tabs: Client | Server
            int tabBtnW = 70;
            int tabBtnH = 22;
            DrawConfigInnerTab("Client", 0, x, y, tabBtnW, tabBtnH);
            DrawConfigInnerTab("Server", 1, x + tabBtnW + 4, y, tabBtnW, tabBtnH);
            y += tabBtnH + 6;

            int listHeight = height - tabBtnH - 6;
            int visibleItems = listHeight / ItemHeight;
            int scrollIndicatorWidth = 10;
            int contentWidth = width - scrollIndicatorWidth;

            var scope = _configInnerTab == 0 ? Config.ConfigScope.Client : Config.ConfigScope.Server;

            // Get mods with at least one property matching the active scope
            var modsWithConfig = PluginLoader.Mods
                .Where(m => m.Context?.Config != null &&
                            m.Context.Config.GetPropertyMetadata().Any(p => p.Scope == scope))
                .ToList();

            // Build flat display list
            var displayItems = new List<(string type, object data, ModInfo mod)>();

            // Framework theme selector appears in Client tab only
            if (_configInnerTab == 0)
            {
                displayItems.Add(("header", "Framework", null));
                displayItems.Add(("theme", null, null));
            }

            foreach (var mod in modsWithConfig)
            {
                displayItems.Add(("header", mod.Manifest.Name, mod));
                foreach (var meta in mod.Context.Config.GetPropertyMetadata())
                {
                    if (meta.Scope == scope)
                        displayItems.Add(("property", meta, mod));
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
                    string modName = (string)item.data;

                    // Check if this mod has pending restart-required changes
                    bool needsRestart = false;
                    if (item.mod != null)
                    {
                        needsRestart = item.mod.Context?.Config?.HasRestartRequiredChanges() ?? false;
                    }

                    int headerHeight = ItemHeight - 4;
                    UIRenderer.DrawRect(x, currentY, contentWidth, headerHeight, UIColors.SectionBg);
                    UIRenderer.DrawRect(x, currentY, contentWidth, 2, UIColors.Divider);
                    UIRenderer.DrawTextShadow(modName, x + 8, currentY + 5, UIColors.Warning);

                    if (needsRestart)
                    {
                        int restartTagX = x + 8 + TextUtil.MeasureWidth(modName) + 5;
                        UIRenderer.DrawText("(Restart Required)", restartTagX, currentY + 5, UIColors.Error);
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

                    currentY += headerHeight + 4;
                }
                else if (item.type == "theme")
                {
                    DrawThemeSelector(x, currentY, contentWidth);
                    currentY += ItemHeight;
                }
                else if (item.type == "property")
                {
                    var meta = (Config.ConfigPropertyMeta)item.data;
                    var config = item.mod.Context.Config;
                    DrawConfigProperty(x, currentY, contentWidth, meta, config, item.mod.Manifest.Id);
                    currentY += ItemHeight;
                }
            }

            DrawScrollIndicator(x + contentWidth + 2, y, listHeight, _scrollOffset, totalDisplayItems, visibleItems);
        }

        private static void DrawConfigInnerTab(string label, int index, int x, int y, int w, int h)
        {
            bool isActive = _configInnerTab == index;
            bool hover = UIRenderer.IsMouseOver(x, y, w, h) && !_blockInput;
            var bg = isActive ? UIColors.SectionBg : (hover ? UIColors.ButtonHover : UIColors.Button);
            UIRenderer.DrawRect(x, y, w, h, bg);
            if (isActive)
                UIRenderer.DrawRect(x, y + h - 2, w, 2, UIColors.Warning);
            int textX = x + Math.Max(0, (w - TextUtil.MeasureWidth(label)) / 2);
            UIRenderer.DrawText(label, textX, y + 4, isActive ? UIColors.Text : UIColors.TextDim);
            if (!isActive && hover && UIRenderer.MouseLeftClick)
            {
                _configInnerTab = index;
                _scrollOffset = 0;
                UIRenderer.ConsumeClick();
            }
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

        private static void DrawConfigProperty(int x, int y, int width, Config.ConfigPropertyMeta meta, Config.ModConfig config, string modId)
        {
            bool isHover = UIRenderer.IsMouseOver(x, y, width, ItemHeight - 2) && !_blockInput;
            UIRenderer.DrawRect(x, y, width, ItemHeight - 2, isHover ? UIColors.ItemHoverBg : UIColors.ItemBg);

            string label = TextUtil.Truncate(meta.Label, width - 190);
            UIRenderer.DrawTextShadow(label, x + 5, y + 5, UIColors.Text);

            if (isHover && !string.IsNullOrEmpty(meta.Description))
                Tooltip.Set(meta.Description);

            int valueX = x + width - 180;
            int valueWidth = 170;
            object currentValue = meta.GetValue(config);
            var t = meta.PropertyType;

            // Multiplayer client: [Server] fields are read-only (server-authoritative)
            bool isServerField = meta.Scope == Config.ConfigScope.Server;
            bool isClient = Game.IsClient;

            if (isServerField && isClient)
            {
                DrawServerLockedField(valueX, y, valueWidth, currentValue, isHover);
                return;
            }

            // [Server] + [RestartRequired] fields: locked mid-session when multiplayer is active
            // (too dangerous to change while clients are connected — would desync immediately)
            if (isServerField && meta.RestartRequired && Game.IsMultiplayer)
            {
                if (isHover)
                    Tooltip.Set("This [Server] value requires restart and cannot be changed while multiplayer is active.");
                DrawServerLockedField(valueX, y, valueWidth, currentValue, isHover);
                return;
            }

            if (t == typeof(bool))
            {
                DrawBoolField(valueX, y, currentValue, meta, config, modId);
                if (isHover && UIRenderer.MouseLeftClick && !_isEditingValue && !_isEditingConfigKey && !_isRebinding)
                {
                    bool bv = currentValue is bool bb && bb;
                    meta.SetValue(config, !bv);
                    AutoSaveConfig(modId, meta, config);
                    UIRenderer.ConsumeClick();
                }
            }
            else if (t == typeof(int))
            {
                DrawIntField(valueX, y, valueWidth, currentValue, meta, config, modId);
            }
            else if (t == typeof(float) || t == typeof(double))
            {
                DrawFloatField(valueX, y, valueWidth, currentValue, meta, config, modId);
            }
            // String fields with either static [Options] or a runtime option provider
            // render as left/right selectors. We query on demand so mods can surface
            // values discovered after initial config metadata construction.
            else if (t == typeof(string) && meta.HasOptions)
            {
                DrawEnumField(valueX, y, valueWidth, currentValue, meta, config, modId);
            }
            else if (t == typeof(string))
            {
                DrawStringField(valueX, y, valueWidth, currentValue, meta, config, modId);
            }
            else
            {
                // Read-only display for unsupported types
                string valueStr = TextUtil.Truncate(currentValue?.ToString() ?? "null", valueWidth - 10);
                UIRenderer.DrawRect(valueX, y + 2, valueWidth, ItemHeight - 6, UIColors.InputBg);
                UIRenderer.DrawText(valueStr, valueX + 5, y + 5, UIColors.TextDim);
            }
        }

        /// <summary>
        /// Draw a locked read-only indicator for [Server] config fields on multiplayer clients.
        /// </summary>
        private static void DrawServerLockedField(int x, int y, int width, object value, bool isHover)
        {
            UIRenderer.DrawRect(x, y + 2, width, ItemHeight - 6, UIColors.InputBg);
            string valStr = TextUtil.Truncate(value?.ToString() ?? "null", width - 60);
            UIRenderer.DrawText(valStr, x + 5, y + 5, UIColors.TextDim);
            UIRenderer.DrawText("[Server]", x + width - 52, y + 5, UIColors.Warning);
            if (isHover)
                Tooltip.Set("This value is controlled by the server and cannot be changed here.");
        }

        private static void DrawBoolField(int x, int y, object value, Config.ConfigPropertyMeta meta, Config.ModConfig config, string modId)
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

        private static void DrawIntField(int x, int y, int width, object value, Config.ConfigPropertyMeta meta, Config.ModConfig config, string modId)
        {
            int intVal = value is int i ? i : (value != null ? Convert.ToInt32(value) : 0);
            int btnWidth = 25;
            bool isEditing = _isEditingValue && _editingValueField == meta.Key && _editingValueModId == modId;
            if (isEditing) _valueEditDrawnThisFrame = true;
            string fieldId = $"{modId}.{meta.Key}";

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
                    AdjustIntValue(meta, config, modId, intVal, -1);
                    UIRenderer.ConsumeClick();
                }
            }

            // Value display / edit area
            int valX = x + btnWidth + 5;
            int valWidth = width - btnWidth * 2 - 10;
            if (isEditing) TrackValueBounds(valX, y + 2, valWidth, ItemHeight - 6);
            bool valHover = UIRenderer.IsMouseOver(valX, y + 2, valWidth, ItemHeight - 6) && !_isRebinding && !_isEditingConfigKey && !_blockInput;

            if (isEditing)
            {
                UIRenderer.DrawRect(valX, y + 2, valWidth, ItemHeight - 6, UIColors.Warning.WithAlpha(180));
                string displayText = _editingValueText + "_";
                UIRenderer.DrawText(displayText, valX + 5, y + 5, UIColors.Text);

                UIRenderer.EnableTextInput();
                HandleNumericInput(meta, config, modId, false);
            }
            else
            {
                UIRenderer.DrawRect(valX, y + 2, valWidth, ItemHeight - 6, valHover ? UIColors.InputFocusBg : UIColors.InputBg);
                string valStr = intVal.ToString();
                UIRenderer.DrawText(valStr, valX + 5, y + 5, UIColors.Text);

                if (valHover && UIRenderer.MouseLeftClick)
                {
                    TrackValueBounds(valX, y + 2, valWidth, ItemHeight - 6);
                    _isEditingValue = true;
                    _valueEditDrawnThisFrame = true;
                    _editingValueField = meta.Key;
                    _editingValueModId = modId;
                    _editingValueText = intVal.ToString();
                    _editingValueFirstKeyPressed = false;
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

                    AdjustIntValue(meta, config, modId, intVal, 1);
                    UIRenderer.ConsumeClick();
                }
            }
        }

        private static void AdjustIntValue(Config.ConfigPropertyMeta meta, Config.ModConfig config, string modId, int currentVal, int direction)
        {
            int newVal = currentVal + direction;
            if (meta.Min.HasValue && newVal < meta.Min.Value) newVal = (int)meta.Min.Value;
            if (meta.Max.HasValue && newVal > meta.Max.Value) newVal = (int)meta.Max.Value;
            meta.SetValue(config, newVal);
            AutoSaveConfig(modId, meta, config);
        }

        private static void DrawFloatField(int x, int y, int width, object value, Config.ConfigPropertyMeta meta, Config.ModConfig config, string modId)
        {
            float floatVal = value is float f ? f : (value is double d ? (float)d : 0f);
            int btnWidth = 25;
            bool isEditing = _isEditingValue && _editingValueField == meta.Key && _editingValueModId == modId;
            if (isEditing) _valueEditDrawnThisFrame = true;
            string fieldId = $"{modId}.{meta.Key}";

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

                    AdjustFloatValue(meta, config, modId, floatVal, -1);
                    UIRenderer.ConsumeClick();
                }
            }

            // Value display / edit area
            int valX = x + btnWidth + 5;
            int valWidth = width - btnWidth * 2 - 10;
            if (isEditing) TrackValueBounds(valX, y + 2, valWidth, ItemHeight - 6);
            bool valHover = UIRenderer.IsMouseOver(valX, y + 2, valWidth, ItemHeight - 6) && !_isRebinding && !_isEditingConfigKey && !_blockInput;

            if (isEditing)
            {
                UIRenderer.DrawRect(valX, y + 2, valWidth, ItemHeight - 6, UIColors.Warning.WithAlpha(180));
                string displayText = _editingValueText + "_";
                UIRenderer.DrawText(displayText, valX + 5, y + 5, UIColors.Text);

                UIRenderer.EnableTextInput();
                HandleNumericInput(meta, config, modId, true);
            }
            else
            {
                UIRenderer.DrawRect(valX, y + 2, valWidth, ItemHeight - 6, valHover ? UIColors.InputFocusBg : UIColors.InputBg);
                string valStr = floatVal.ToString("F2");
                UIRenderer.DrawText(valStr, valX + 5, y + 5, UIColors.Text);

                if (valHover && UIRenderer.MouseLeftClick)
                {
                    TrackValueBounds(valX, y + 2, valWidth, ItemHeight - 6);
                    _isEditingValue = true;
                    _valueEditDrawnThisFrame = true;
                    _editingValueField = meta.Key;
                    _editingValueModId = modId;
                    _editingValueText = floatVal.ToString("F2");
                    _editingValueFirstKeyPressed = false;
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

                    AdjustFloatValue(meta, config, modId, floatVal, 1);
                    UIRenderer.ConsumeClick();
                }
            }
        }

        private static void AdjustFloatValue(Config.ConfigPropertyMeta meta, Config.ModConfig config, string modId, float currentVal, int direction)
        {
            float newVal = currentVal + (0.1f * direction);
            if (meta.Min.HasValue && newVal < (float)meta.Min.Value) newVal = (float)meta.Min.Value;
            if (meta.Max.HasValue && newVal > (float)meta.Max.Value) newVal = (float)meta.Max.Value;
            meta.SetValue(config, newVal);
            AutoSaveConfig(modId, meta, config);
        }

        private static void TrackValueBounds(int x, int y, int width, int height)
        {
            _valueEditX = x; _valueEditY = y; _valueEditWidth = width; _valueEditHeight = height;
        }

        private static void CommitActiveValueEdit()
        {
            var config = PluginLoader.GetMod(_editingValueModId)?.Context?.Config;
            var meta = config?.GetPropertyMetadata().FirstOrDefault(item => item.Key == _editingValueField);
            if (meta == null) { CancelValueEdit(); return; }
            if (meta.PropertyType == typeof(string))
            {
                if ((meta.GetValue(config) as string ?? "") != _editingValueText)
                {
                    meta.SetValue(config, _editingValueText);
                    AutoSaveConfig(_editingValueModId, meta, config);
                }
                CancelValueEdit();
            }
            else ApplyNumericEdit(meta, config, _editingValueModId,
                meta.PropertyType == typeof(float) || meta.PropertyType == typeof(double));
        }

        private static void CancelValueEdit()
        {
            _isEditingValue = false;
            _editingValueField = null;
            _editingValueModId = null;
            UIRenderer.DisableTextInput();
            if (!_isRebinding && !_isEditingConfigKey) UIRenderer.UnregisterKeyInputBlock("mod-menu");
        }

        private static void DrawStringField(int x, int y, int width, object value,
            Config.ConfigPropertyMeta meta, Config.ModConfig config, string modId)
        {
            string current = value as string ?? "";
            bool editing = _isEditingValue && _editingValueField == meta.Key && _editingValueModId == modId;
            bool hover = UIRenderer.IsMouseOver(x, y + 2, width, ItemHeight - 6) &&
                !_isRebinding && !_isEditingConfigKey && !_blockInput;
            if (editing)
            {
                _valueEditDrawnThisFrame = true;
                TrackValueBounds(x, y + 2, width, ItemHeight - 6);
                UIRenderer.EnableTextInput();
                UIRenderer.RegisterKeyInputBlock("mod-menu");
                if (!_blockInput && !InputState.IsKeyJustPressed(KeyCode.Escape))
                {
                    UIRenderer.HandleIME();
                    string text = UIRenderer.GetInputText(_editingValueText);
                    // Preserve pre-existing long values; bound newly entered text without splitting UTF-16 pairs.
                    int limit = Math.Max(4096, current.Length);
                    if (text.Length > limit)
                    {
                        if (limit > 0 && char.IsHighSurrogate(text[limit - 1])) limit--;
                        text = text.Substring(0, limit);
                    }
                    _editingValueText = text;
                    if (InputState.IsKeyJustPressed(KeyCode.Enter) || InputState.IsKeyJustPressed(KeyCode.NumPadEnter) ||
                        (UIRenderer.MouseLeftClick && !hover))
                    {
                        CommitActiveValueEdit();
                    }
                }
            }
            else if (hover && UIRenderer.MouseLeftClick)
            {
                _isEditingValue = true;
                _editingValueField = meta.Key;
                _editingValueModId = modId;
                _editingValueText = current;
                TrackValueBounds(x, y + 2, width, ItemHeight - 6);
                _valueEditDrawnThisFrame = true;
                UIRenderer.ClearInput();
                UIRenderer.RegisterKeyInputBlock("mod-menu");
                UIRenderer.ConsumeClick();
                editing = true;
            }
            UIRenderer.DrawRect(x, y + 2, width, ItemHeight - 6,
                editing ? UIColors.InputFocusBg : UIColors.InputBg);
            string shown = editing ? TextUtil.VisibleTail(_editingValueText + "|", width - 10) : TextUtil.Truncate(current, width - 10);
            UIRenderer.DrawText(shown, x + 5, y + 5, UIColors.Text);
            if (hover) Tooltip.Set("Click to edit. Enter or click outside to save; Escape to cancel.");
        }

        private static void DrawEnumField(int x, int y, int width, object value, Config.ConfigPropertyMeta meta, Config.ModConfig config, string modId)
        {
            string currentVal = value?.ToString() ?? "";
            // Re-fetch options each frame so a selector can reflect live registries or
            // lazily loaded content without needing the config screen to be rebuilt.
            var options = new List<string>(meta.GetOptions(config));
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
                meta.SetValue(config, options[currentIndex]);
                AutoSaveConfig(modId, meta, config);
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
                meta.SetValue(config, options[currentIndex]);
                AutoSaveConfig(modId, meta, config);
                UIRenderer.ConsumeClick();
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
                        string sevLabel;
                        Color4 sevColor;
                        switch (item.Severity)
                        {
                            case ConflictSeverity.High:
                                sevLabel = "! HIGH";
                                sevColor = UIColors.Error;
                                break;
                            case ConflictSeverity.Medium:
                                sevLabel = "~ MED";
                                sevColor = UIColors.Warning;
                                break;
                            default:
                                sevLabel = "  LOW";
                                sevColor = UIColors.TextDim;
                                break;
                        }
                        UIRenderer.DrawRect(x, currentY, contentWidth, ItemHeight - 2, UIColors.ItemBg);
                        UIRenderer.DrawText(sevLabel, x + 5, currentY + 5, sevColor);
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

        private static void DrawPlayersTab(int x, int y, int width, int height)
        {
            // Header row
            UIRenderer.DrawTextShadow("Connected Players", x, y, UIColors.Text);

            y += 30;

            var players = NetSync.PlayerList;
            if (players.Count == 0)
            {
                UIRenderer.DrawText("No player data — connects trigger automatic updates", x, y, UIColors.TextDim);
                return;
            }

            int rowHeight = 34;
            int visibleRows = (height - 30) / rowHeight;
            int maxScroll = Math.Max(0, players.Count - visibleRows);
            _scrollOffset = Math.Min(_scrollOffset, maxScroll);

            for (int i = 0; i < visibleRows && i + _scrollOffset < players.Count; i++)
            {
                var (slot, name, guid, role) = players[i + _scrollOffset];
                int rowY = y + i * rowHeight;

                bool isAdmin = role == PermissionService.PlayerRole.Admin;
                bool isSelf = slot == Game.MyPlayerIndex;

                // Row background
                UIRenderer.DrawRect(x, rowY, width, rowHeight - 2, UIColors.ItemBg);

                // Player name
                UIRenderer.DrawTextShadow(name + (isSelf ? " (you)" : ""), x + 6, rowY + 8, UIColors.Text);

                // Role badge
                string roleLabel = isAdmin ? "Admin" : "Player";
                var roleColor = isAdmin ? UIColors.Warning : UIColors.TextDim;
                UIRenderer.DrawTextShadow(roleLabel, x + 180, rowY + 8, roleColor);

                // Action buttons (not for self)
                if (!isSelf)
                {
                    int btnX = x + width - 155;

                    // Promote / Demote
                    string opLabel = isAdmin ? "Demote" : "Promote";
                    bool opHover = UIRenderer.IsMouseOver(btnX, rowY + 5, 70, ButtonHeight) && !_blockInput;
                    UIRenderer.DrawRect(btnX, rowY + 5, 70, ButtonHeight, opHover ? UIColors.ItemHoverBg : UIColors.SectionBg);
                    UIRenderer.DrawTextSmall(opLabel, btnX + 6, rowY + 8, UIColors.Text);
                    if (opHover && UIRenderer.MouseLeftClick)
                    {
                        string cmd = isAdmin ? "deop" : "op";
                        NetSync.SendServerCommandRequest(cmd, name);
                        UIRenderer.ConsumeClick();
                    }

                    // Kick
                    int kickX = btnX + 76;
                    bool kickHover = UIRenderer.IsMouseOver(kickX, rowY + 5, 50, ButtonHeight) && !_blockInput;
                    UIRenderer.DrawRect(kickX, rowY + 5, 50, ButtonHeight, kickHover ? UIColors.Error.WithAlpha(200) : UIColors.SectionBg);
                    UIRenderer.DrawTextSmall("Kick", kickX + 8, rowY + 8, kickHover ? UIColors.Text : UIColors.TextDim);
                    if (kickHover && UIRenderer.MouseLeftClick)
                    {
                        NetSync.SendServerCommandRequest("kick", name);
                        UIRenderer.ConsumeClick();
                    }
                }
            }

            DrawScrollIndicator(x + width - 8, y, height - 30, _scrollOffset, players.Count, visibleRows);
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
                var method = Config.ConfigHotReload.FindCallback(mod.Instance);

                // Inherited callbacks have the same contract as directly declared callbacks.
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
                var method = Config.ConfigHotReload.FindCallback(mod.Instance);

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
        private static void AutoSaveConfig(string modId, Config.ConfigPropertyMeta changedMeta = null, Config.ModConfig changedConfig = null)
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

                    // Server in multiplayer: broadcast [Server] field changes to all clients
                    // Game.IsServer only checks dedServ env var — also need to handle H&P host
                    bool isHostAndPlay = false;
                    try { isHostAndPlay = Terraria.Netplay.IsHostAndPlay; } catch { }
                    if (changedMeta != null && changedConfig != null &&
                        changedMeta.Scope == Config.ConfigScope.Server &&
                        Game.IsMultiplayer && (Game.IsServer || isHostAndPlay))
                    {
                        NetSync.BroadcastConfigChange(modId, changedMeta, changedConfig);
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

                var meta = mod.Context.Config.GetPropertyMetadata()
                    .FirstOrDefault(m => m.Key == fieldKey);
                if (meta == null)
                {
                    _holdingButtonField = null;
                    return;
                }

                // Adjust the value
                if (meta.PropertyType == typeof(int))
                {
                    int currentVal = Convert.ToInt32(meta.GetValue(mod.Context.Config) ?? 0);
                    AdjustIntValue(meta, mod.Context.Config, _holdingButtonModId, currentVal, _holdingButtonDirection);
                }
                else if (meta.PropertyType == typeof(float) || meta.PropertyType == typeof(double))
                {
                    float currentVal = Convert.ToSingle(meta.GetValue(mod.Context.Config) ?? 0f);
                    AdjustFloatValue(meta, mod.Context.Config, _holdingButtonModId, currentVal, _holdingButtonDirection);
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
        private static void HandleNumericInput(Config.ConfigPropertyMeta meta, Config.ModConfig config, string modId, bool isFloat)
        {
            // Check for Enter to confirm
            if (InputState.IsKeyJustPressed(KeyCode.Enter) || InputState.IsKeyJustPressed(KeyCode.NumPadEnter))
            {
                ApplyNumericEdit(meta, config, modId, isFloat);
                return;
            }

            // Check for number keys (including numpad)
            for (int i = 0; i <= 9; i++)
            {
                // Regular number keys (D0-D9)
                if (InputState.IsKeyJustPressed(KeyCode.D0 + i))
                {
                    // First key replaces the old value
                    if (!_editingValueFirstKeyPressed)
                    {
                        _editingValueText = i.ToString();
                        _editingValueFirstKeyPressed = true;
                    }
                    else if (_editingValueText.Length < 10) // Max 10 digits
                    {
                        _editingValueText += i.ToString();
                    }
                    return;
                }
                // Numpad keys
                if (InputState.IsKeyJustPressed(KeyCode.NumPad0 + i))
                {
                    if (!_editingValueFirstKeyPressed)
                    {
                        _editingValueText = i.ToString();
                        _editingValueFirstKeyPressed = true;
                    }
                    else if (_editingValueText.Length < 10)
                    {
                        _editingValueText += i.ToString();
                    }
                    return;
                }
            }

            // Decimal point for floats
            if (isFloat && (InputState.IsKeyJustPressed(KeyCode.OemPeriod) || InputState.IsKeyJustPressed(KeyCode.Decimal)))
            {
                if (!_editingValueFirstKeyPressed)
                {
                    _editingValueText = "0.";
                    _editingValueFirstKeyPressed = true;
                }
                else if (!_editingValueText.Contains(".") && _editingValueText.Length < 10)
                {
                    _editingValueText += ".";
                }
                return;
            }

            // Minus sign - toggle negative (doesn't replace old value)
            if (InputState.IsKeyJustPressed(KeyCode.OemMinus) || InputState.IsKeyJustPressed(KeyCode.Subtract))
            {
                if (_editingValueText.StartsWith("-"))
                    _editingValueText = _editingValueText.Substring(1);
                else
                    _editingValueText = "-" + _editingValueText;
                return;
            }

            // Backspace - now we're editing, not replacing
            if (InputState.IsKeyJustPressed(KeyCode.Back))
            {
                _editingValueFirstKeyPressed = true; // User is editing, not replacing
                if (_editingValueText.Length > 0)
                    _editingValueText = _editingValueText.Substring(0, _editingValueText.Length - 1);
                return;
            }

            // Click outside to confirm
            if (UIRenderer.MouseLeftClick)
            {
                ApplyNumericEdit(meta, config, modId, isFloat);
            }
        }

        /// <summary>
        /// Apply the edited number value.
        /// </summary>
        private static void ApplyNumericEdit(Config.ConfigPropertyMeta meta, Config.ModConfig config, string modId, bool isFloat)
        {
            if (isFloat)
            {
                if (float.TryParse(_editingValueText, out float newVal))
                {
                    if (meta.Min.HasValue && newVal < (float)meta.Min.Value) newVal = (float)meta.Min.Value;
                    if (meta.Max.HasValue && newVal > (float)meta.Max.Value) newVal = (float)meta.Max.Value;
                    meta.SetValue(config, newVal);
                    AutoSaveConfig(modId, meta, config);
                }
            }
            else
            {
                if (int.TryParse(_editingValueText, out int newVal))
                {
                    if (meta.Min.HasValue && newVal < (int)meta.Min.Value) newVal = (int)meta.Min.Value;
                    if (meta.Max.HasValue && newVal > (int)meta.Max.Value) newVal = (int)meta.Max.Value;
                    meta.SetValue(config, newVal);
                    AutoSaveConfig(modId, meta, config);
                }
            }

            CancelValueEdit();
        }

        private static void ResetConfigToDefaults(ModInfo mod)
        {
            if (mod?.Context?.Config == null) return;

            try
            {
                var config = mod.Context.Config;
                config.ResetToDefaults();
                config.Save();

                if (ModSupportsHotReload(mod))
                {
                    CallOnConfigChanged(mod);
                }

                // Broadcast all [Server] properties in multiplayer
                bool isHostAndPlay = false;
                try { isHostAndPlay = Terraria.Netplay.IsHostAndPlay; } catch { }
                if (Game.IsMultiplayer && (Game.IsServer || isHostAndPlay))
                {
                    foreach (var meta in config.GetPropertyMetadata())
                    {
                        if (meta.Scope == Config.ConfigScope.Server)
                        {
                            NetSync.BroadcastConfigChange(mod.Manifest.Id, meta, config);
                        }
                    }
                }

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
