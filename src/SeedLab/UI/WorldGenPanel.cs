using System;
using System.Collections.Generic;
using TerrariaModder.Core.Input;
using TerrariaModder.Core.Logging;
using TerrariaModder.Core.UI;
using TerrariaModder.Core.UI.Widgets;

namespace SeedLab.UI
{
    /// <summary>
    /// Tabbed sidebar panel for configuring world-generation seed feature overrides.
    /// Three tabs: By Seed, By Category, Presets.
    /// Two-state checkboxes: unchecked (default) or checked (force on).
    /// Easy/Advanced mode toggle collapses related groups.
    /// </summary>
    public class WorldGenPanel
    {
        // Layout
        private const int PanelWidth = 480;
        private const int TabBarHeight = 28;
        private const int ToolbarHeight = 30;
        private const int FooterHeight = 60;
        private const int MaxPanelHeight = 650;
        private const int Padding = 8;
        private const int SeedHeaderHeight = 26;
        private const int GroupRowHeight = 24;
        private const int CategoryHeaderHeight = 24;
        private const int PresetRowHeight = 32;
        private const int RowSpacing = 2;
        private const int SectionSpacing = 6;
        private const int CheckboxSize = 16;

        // Tabs
        private static readonly string[] TabNames = { "By Seed", "By Category", "Presets" };
        private int _activeTab;

        // Mode
        private bool _advancedMode = true;

        private readonly ILogger _log;
        private readonly WorldGenOverrideManager _manager;

        // UI components
        private readonly DraggablePanel _panel;
        private readonly ScrollView[] _scrollViews = { new ScrollView(), new ScrollView(), new ScrollView() };
        private readonly TextInput _presetNameInput;
        private bool _presetNaming;

        // Category cache
        private Dictionary<string, List<GroupSeedPair>> _categoryGroups;
        private Dictionary<string, List<CategoryEasyPair>> _categoryEasyGroups;

        public bool Visible
        {
            get => _panel.IsOpen;
            set
            {
                if (value && !_panel.IsOpen)
                {
                    foreach (var sv in _scrollViews) sv.ResetScroll();
                    _categoryGroups = _manager.GetGroupsByCategory();
                    _categoryEasyGroups = _manager.GetEasyGroupsByCategory();
                    if (_panel.X < 0)
                    {
                        int panelH = CalculatePanelHeight();
                        _panel.Open(UIRenderer.ScreenWidth - PanelWidth - 20,
                            Math.Max(20, (UIRenderer.ScreenHeight - panelH) / 2));
                    }
                    else
                    {
                        _panel.Open(_panel.X, _panel.Y);
                    }
                }
                else if (!value && _panel.IsOpen)
                {
                    _panel.Close();
                }
            }
        }

        public WorldGenPanel(ILogger log, WorldGenOverrideManager manager)
        {
            _log = log;
            _manager = manager;
            _panel = new DraggablePanel("seed-lab-worldgen", "Seed Lab", PanelWidth, MaxPanelHeight);
            _panel.ClipContent = false; // We manage our own clip for the scrollable content area
            _panel.OnClose = OnPanelClose;
            _presetNameInput = new TextInput("Preset name...", 24);
            _presetNameInput.KeyBlockId = "seed-lab-wg-preset";
        }

        private void OnPanelClose()
        {
            if (_presetNaming) ExitPresetNaming();
        }

        /// <summary>
        /// Call in Update phase to maintain text input state (WritingText flag).
        /// Only active when on the Presets tab (2) and naming is in progress.
        /// </summary>
        public void Update()
        {
            if (_panel.IsOpen && _presetNaming && _activeTab == 2)
                _presetNameInput.Update();
        }

        public void Draw()
        {
            // Handle preset naming escape before panel escape
            if (_panel.IsOpen && _presetNaming && InputState.IsKeyJustPressed(KeyCode.Escape))
            {
                ExitPresetNaming();
                return;
            }

            // Update dynamic height and title
            int panelH = CalculatePanelHeight();
            _panel.Height = panelH;
            int count = _manager.ActiveCount;
            _panel.Title = count > 0 ? $"Seed Lab ({count} active)" : "Seed Lab";

            if (!_panel.BeginDraw()) return;

            try
            {
                int x = _panel.X;
                int y = _panel.ContentY;

                // Tab bar
                _activeTab = TabBar.Draw(x, y, PanelWidth, TabNames, _activeTab, TabBarHeight);
                y += TabBarHeight;

                // Toolbar (Easy/Adv toggle)
                DrawToolbar(x, y);
                y += ToolbarHeight;

                // Content area with scroll + clip
                int contentAreaH = panelH - _panel.HeaderHeight - TabBarHeight - ToolbarHeight - FooterHeight;
                int contentH = CalculateContentHeight();
                var sv = _scrollViews[_activeTab];
                sv.Begin(x + Padding, y, PanelWidth - Padding * 2, contentAreaH, contentH);

                // No BeginClip — virtual scrolling via IsRowVisible handles culling.
                // BeginClip restarts SpriteBatch with GameViewMatrix which differs from
                // the UI transform on the title screen, causing content to offset.
                switch (_activeTab)
                {
                    case 0: DrawBySeedContent(sv); break;
                    case 1: DrawByCategoryContent(sv); break;
                    case 2: DrawPresetsContent(sv); break;
                }
                sv.End();

                // Footer
                DrawFooter(x, y + contentAreaH);
            }
            catch (Exception ex)
            {
                _log.Error($"[SeedLab] WorldGenPanel draw error: {ex}");
            }

            _panel.EndDraw();
        }

        private int CalculatePanelHeight()
        {
            int contentH = CalculateContentHeight();
            int contentArea = Math.Min(contentH, MaxPanelHeight - _panel.HeaderHeight - TabBarHeight - ToolbarHeight - FooterHeight);
            return _panel.HeaderHeight + TabBarHeight + ToolbarHeight + contentArea + FooterHeight;
        }

        private int CalculateContentHeight()
        {
            switch (_activeTab)
            {
                case 0: return CalculateBySeedHeight();
                case 1: return CalculateByCategoryHeight();
                case 2: return CalculatePresetsHeight();
                default: return 100;
            }
        }

        #region Toolbar

        private void DrawToolbar(int x, int y)
        {
            UIRenderer.DrawRect(x, y, PanelWidth, ToolbarHeight, UIColors.PanelBg);

            // Easy/Advanced toggle (relevant for By Seed and By Category tabs)
            if (_activeTab == 0 || _activeTab == 1)
            {
                string modeText = _advancedMode ? "[Advanced]" : "[Easy]";
                int modeW = TextUtil.MeasureWidth(modeText) + 12;
                int modeX = x + PanelWidth - modeW - Padding;
                int modeY = y + 4;

                if (Button.Draw(modeX, modeY, modeW, 22, modeText,
                    UIColors.Button, UIColors.ButtonHover,
                    _advancedMode ? UIColors.Info : UIColors.TextDim))
                {
                    _advancedMode = !_advancedMode;
                    // Refresh category caches for the new mode
                    _categoryEasyGroups = _manager.GetEasyGroupsByCategory();
                }

                if (WidgetInput.IsMouseOver(modeX, modeY, modeW, 22))
                    Tooltip.Set(_advancedMode
                        ? "Switch to Easy mode (grouped features)"
                        : "Switch to Advanced mode (all features)");
            }

            UIRenderer.DrawRect(x + Padding, y + ToolbarHeight - 1, PanelWidth - Padding * 2, 1, UIColors.Divider);
        }

        #endregion

        #region Footer

        private void DrawFooter(int x, int y)
        {
            UIRenderer.DrawRect(x, y, PanelWidth, FooterHeight, UIColors.PanelBg);
            UIRenderer.DrawRect(x + Padding, y, PanelWidth - Padding * 2, 1, UIColors.Divider);

            int cy = y + 6;
            bool hasAny = _manager.HasOverrides;

            if (hasAny)
            {
                if (Button.Draw(x + Padding, cy, 90, 24, "Clear All"))
                {
                    _manager.ClearAll();
                    _categoryGroups = _manager.GetGroupsByCategory();
                    _categoryEasyGroups = _manager.GetEasyGroupsByCategory();
                }
            }
            else
            {
                // Disabled button
                UIRenderer.DrawRect(x + Padding, cy, 90, 24, UIColors.Button.WithAlpha(100));
                string clearText = "Clear All";
                int clearTextW = TextUtil.MeasureWidth(clearText);
                UIRenderer.DrawText(clearText, x + Padding + (90 - clearTextW) / 2, cy + 5, UIColors.TextHint);
            }

            UIRenderer.DrawText("Applies to next world created", x + Padding, cy + 30, UIColors.Warning);
        }

        #endregion

        #region By Seed Content

        private int CalculateBySeedHeight()
        {
            int height = Padding;
            string lastCategory = null;

            foreach (var seed in WorldGenFeatureCatalog.Seeds)
            {
                string category = seed.Kind == SeedKind.SpecialSeed ? "Special Seeds" : "Secret Seeds";
                if (category != lastCategory)
                {
                    if (lastCategory != null) height += SectionSpacing;
                    height += CategoryHeaderHeight;
                    lastCategory = category;
                }

                height += SeedHeaderHeight + RowSpacing;

                if (!_manager.IsSeedCollapsed(seed.Id))
                {
                    if (_advancedMode)
                        height += seed.Groups.Length * (GroupRowHeight + RowSpacing);
                    else
                        height += _manager.GetEasyGroups(seed.Id).Count * (GroupRowHeight + RowSpacing);
                    height += SectionSpacing;
                }
            }

            return height;
        }

        private void DrawBySeedContent(ScrollView sv)
        {
            int relY = Padding;
            int contentW = sv.ContentWidth;
            string lastCategory = null;

            foreach (var seed in WorldGenFeatureCatalog.Seeds)
            {
                string category = seed.Kind == SeedKind.SpecialSeed ? "Special Seeds" : "Secret Seeds";

                if (category != lastCategory)
                {
                    if (lastCategory != null) relY += SectionSpacing;
                    if (IsRowVisible(sv, relY, CategoryHeaderHeight))
                        DrawCategoryDivider(sv.ContentX, sv.ContentY + relY, contentW, category);
                    relY += CategoryHeaderHeight;
                    lastCategory = category;
                }

                if (IsRowVisible(sv, relY, SeedHeaderHeight))
                    DrawSeedHeader(sv.ContentX, sv.ContentY + relY, contentW, seed);
                relY += SeedHeaderHeight + RowSpacing;

                if (_manager.IsSeedCollapsed(seed.Id)) continue;

                if (_advancedMode)
                {
                    foreach (var group in seed.Groups)
                    {
                        if (IsRowVisible(sv, relY, GroupRowHeight))
                            DrawGroupCheckbox(sv.ContentX + 16, sv.ContentY + relY, contentW - 16,
                                group.Id, group.DisplayName, group.Description, group.Conflicts);
                        relY += GroupRowHeight + RowSpacing;
                    }
                }
                else
                {
                    var easyGroups = _manager.GetEasyGroups(seed.Id);
                    foreach (var eg in easyGroups)
                    {
                        if (IsRowVisible(sv, relY, GroupRowHeight))
                            DrawEasyGroupCheckbox(sv.ContentX + 16, sv.ContentY + relY, contentW - 16, eg);
                        relY += GroupRowHeight + RowSpacing;
                    }
                }
                relY += SectionSpacing;
            }
        }

        private void DrawSeedHeader(int x, int y, int width, WGSeedDef seed)
        {
            bool collapsed = _manager.IsSeedCollapsed(seed.Id);
            bool allChecked = _manager.IsAllCheckedForSeed(seed.Id);
            bool anyChecked = _manager.HasAnyCheckedForSeed(seed.Id);

            UIRenderer.DrawRect(x, y, width, SeedHeaderHeight, UIColors.HeaderBg.WithAlpha(180));

            // "All" checkbox
            int cbX = x + 4;
            int cbY = y + (SeedHeaderHeight - CheckboxSize) / 2;
            if (Checkbox.Draw(cbX, cbY, CheckboxSize, allChecked, anyChecked && !allChecked))
                _manager.ToggleSeedOverride(seed.Id);

            // Collapse arrow + seed name
            string arrow = collapsed ? ">" : "v";
            UIRenderer.DrawText(arrow, x + CheckboxSize + 10, y + 5, UIColors.TextDim);
            UIRenderer.DrawTextShadow(seed.DisplayName.ToUpper(), x + CheckboxSize + 24, y + 5,
                anyChecked ? UIColors.TextTitle : UIColors.TextDim);

            if (anyChecked)
                UIRenderer.DrawRect(x, y, 3, SeedHeaderHeight, UIColors.Success);

            // Click name area to collapse/expand
            int nameX = x + CheckboxSize + 8;
            if (WidgetInput.IsMouseOver(nameX, y, width - CheckboxSize - 8, SeedHeaderHeight))
            {
                Tooltip.Set("Click to " + (collapsed ? "expand" : "collapse") + ". Use checkbox to select all.");
                if (WidgetInput.MouseLeftClick)
                {
                    _manager.ToggleSeedCollapsed(seed.Id);
                    WidgetInput.ConsumeClick();
                }
            }
        }

        private void DrawGroupCheckbox(int x, int y, int width, string groupId,
            string displayName, string description, string[] conflicts)
        {
            bool isChecked = _manager.IsGroupChecked(groupId);
            bool hover = WidgetInput.IsMouseOver(x, y, width, GroupRowHeight);

            // Row background
            Color4 bg = isChecked ? UIColors.Success.WithAlpha(40) : UIColors.Button;
            if (hover) bg = new Color4((byte)Math.Min(bg.R + 20, 255), (byte)Math.Min(bg.G + 20, 255),
                (byte)Math.Min(bg.B + 20, 255), bg.A);
            UIRenderer.DrawRect(x, y, width, GroupRowHeight, bg);

            // Checkbox
            int cbX = x + 4;
            int cbY = y + (GroupRowHeight - CheckboxSize) / 2;
            if (Checkbox.Draw(cbX, cbY, CheckboxSize, isChecked, false))
                _manager.ToggleGroupOverride(groupId);

            // Name (truncated with real font measurement)
            int textY = y + (GroupRowHeight - 16) / 2;
            int maxTextWidth = width - CheckboxSize - 14;
            UIRenderer.DrawText(TextUtil.Truncate(displayName, maxTextWidth), x + CheckboxSize + 10, textY,
                isChecked ? UIColors.Text : UIColors.TextDim);

            // Left accent bar
            if (isChecked)
                UIRenderer.DrawRect(x, y, 3, GroupRowHeight, UIColors.Success);

            // Conflict warning
            var activeConflicts = _manager.GetActiveConflicts(groupId);
            if (activeConflicts != null && activeConflicts.Count > 0)
                UIRenderer.DrawText("!", x + width - 16, textY, UIColors.Warning);

            // Tooltip + row click
            if (hover)
            {
                string tip = description;
                if (activeConflicts != null && activeConflicts.Count > 0)
                {
                    tip += "\n\nConflicts with:";
                    foreach (var cid in activeConflicts)
                        tip += "\n  " + _manager.GetSeedDisplayNameForGroup(cid) + " - " + _manager.GetGroupDisplayName(cid);
                }
                Tooltip.Set(tip);

                if (WidgetInput.MouseLeftClick)
                {
                    _manager.ToggleGroupOverride(groupId);
                    WidgetInput.ConsumeClick();
                }
            }
        }

        private void DrawEasyGroupCheckbox(int x, int y, int width, EasyGroupEntry eg, string labelOverride = null)
        {
            bool hover = WidgetInput.IsMouseOver(x, y, width, GroupRowHeight);

            Color4 bg = eg.AllChecked ? UIColors.Success.WithAlpha(40) :
                        eg.AnyChecked ? UIColors.Success.WithAlpha(20) : UIColors.Button;
            if (hover) bg = new Color4((byte)Math.Min(bg.R + 20, 255), (byte)Math.Min(bg.G + 20, 255),
                (byte)Math.Min(bg.B + 20, 255), bg.A);
            UIRenderer.DrawRect(x, y, width, GroupRowHeight, bg);

            int cbX = x + 4;
            int cbY = y + (GroupRowHeight - CheckboxSize) / 2;
            if (Checkbox.Draw(cbX, cbY, CheckboxSize, eg.AllChecked, eg.AnyChecked && !eg.AllChecked))
                _manager.SetEasyGroupChecked(eg.GroupIds, !eg.AllChecked);

            string displayText = labelOverride ?? eg.DisplayName;
            int textY = y + (GroupRowHeight - 16) / 2;
            int maxTextWidth = width - CheckboxSize - 14;
            UIRenderer.DrawText(TextUtil.Truncate(displayText, maxTextWidth), x + CheckboxSize + 10, textY,
                eg.AllChecked ? UIColors.Text : UIColors.TextDim);

            if (eg.AllChecked || eg.AnyChecked)
                UIRenderer.DrawRect(x, y, 3, GroupRowHeight, eg.AllChecked ? UIColors.Success : UIColors.Warning);

            if (hover)
            {
                string tip = eg.Description;
                if (eg.GroupIds.Length > 1)
                    tip += $"\n\n({eg.GroupIds.Length} sub-features in Advanced mode)";
                Tooltip.Set(tip);

                if (WidgetInput.MouseLeftClick)
                {
                    _manager.SetEasyGroupChecked(eg.GroupIds, !eg.AllChecked);
                    WidgetInput.ConsumeClick();
                }
            }
        }

        #endregion

        #region By Category Content

        private int CalculateByCategoryHeight()
        {
            int height = Padding;

            if (_advancedMode)
            {
                if (_categoryGroups == null) return 100;
                foreach (var cat in WorldGenOverrideManager.StandardCategories)
                {
                    if (!_categoryGroups.ContainsKey(cat)) continue;
                    height += CategoryHeaderHeight + RowSpacing;
                    height += _categoryGroups[cat].Count * (GroupRowHeight + RowSpacing);
                    height += SectionSpacing;
                }
            }
            else
            {
                // Recompute each frame — easy groups contain AllChecked/AnyChecked state
                _categoryEasyGroups = _manager.GetEasyGroupsByCategory();
                foreach (var cat in WorldGenOverrideManager.StandardCategories)
                {
                    if (!_categoryEasyGroups.ContainsKey(cat)) continue;
                    height += CategoryHeaderHeight + RowSpacing;
                    height += _categoryEasyGroups[cat].Count * (GroupRowHeight + RowSpacing);
                    height += SectionSpacing;
                }
            }

            return height;
        }

        private void DrawByCategoryContent(ScrollView sv)
        {
            if (_categoryGroups == null)
                _categoryGroups = _manager.GetGroupsByCategory();
            if (_categoryEasyGroups == null)
                _categoryEasyGroups = _manager.GetEasyGroupsByCategory();

            int relY = Padding;
            int contentW = sv.ContentWidth;

            if (_advancedMode)
            {
                foreach (var cat in WorldGenOverrideManager.StandardCategories)
                {
                    if (!_categoryGroups.TryGetValue(cat, out var pairs)) continue;
                    if (pairs.Count == 0) continue;

                    if (IsRowVisible(sv, relY, CategoryHeaderHeight))
                        DrawCategoryDivider(sv.ContentX, sv.ContentY + relY, contentW, cat);
                    relY += CategoryHeaderHeight + RowSpacing;

                    foreach (var pair in pairs)
                    {
                        if (IsRowVisible(sv, relY, GroupRowHeight))
                        {
                            string label = pair.Seed.DisplayName + " \u2014 " + pair.Group.DisplayName;
                            DrawGroupCheckbox(sv.ContentX + 8, sv.ContentY + relY, contentW - 8,
                                pair.Group.Id, label, pair.Group.Description, pair.Group.Conflicts);
                        }
                        relY += GroupRowHeight + RowSpacing;
                    }
                    relY += SectionSpacing;
                }
            }
            else
            {
                foreach (var cat in WorldGenOverrideManager.StandardCategories)
                {
                    if (!_categoryEasyGroups.TryGetValue(cat, out var pairs)) continue;
                    if (pairs.Count == 0) continue;

                    if (IsRowVisible(sv, relY, CategoryHeaderHeight))
                        DrawCategoryDivider(sv.ContentX, sv.ContentY + relY, contentW, cat);
                    relY += CategoryHeaderHeight + RowSpacing;

                    foreach (var pair in pairs)
                    {
                        if (IsRowVisible(sv, relY, GroupRowHeight))
                        {
                            string label = pair.Seed.DisplayName + " \u2014 " + pair.EasyGroup.DisplayName;
                            DrawEasyGroupCheckbox(sv.ContentX + 8, sv.ContentY + relY, contentW - 8, pair.EasyGroup, label);
                        }
                        relY += GroupRowHeight + RowSpacing;
                    }
                    relY += SectionSpacing;
                }
            }
        }

        #endregion

        #region Presets Content

        private static readonly PresetDef[] BuiltInPresets;

        static WorldGenPanel()
        {
            var presets = new List<PresetDef>();

            foreach (var seed in WorldGenFeatureCatalog.Seeds)
            {
                if (seed.Kind != SeedKind.SpecialSeed) continue;
                presets.Add(new PresetDef
                {
                    Name = "Full " + seed.DisplayName,
                    Description = "Enable all " + seed.DisplayName + " features",
                    SeedId = seed.Id
                });
            }

            presets.Add(new PresetDef
            {
                Name = "Visual Effects",
                Description = "Paint, coat, echo, illuminant, rainbow, and error world effects",
                GroupIds = new[] { "ss_paint_gray", "ss_paint_negative", "ss_coat_echo",
                    "ss_coat_illuminant", "ss_rainbow", "ss_error_world" }
            });

            presets.Add(new PresetDef
            {
                Name = "Biome Chaos",
                Description = "Dual evil, world infected, hallow surface, no infection removal",
                GroupIds = new[] { "drunk_evil", "ss_world_infected", "ss_hallow_surface",
                    "ss_world_frozen", "ss_start_hardmode" }
            });

            BuiltInPresets = presets.ToArray();
        }

        private int CalculatePresetsHeight()
        {
            int h = Padding;
            h += CategoryHeaderHeight + RowSpacing;
            h += BuiltInPresets.Length * (PresetRowHeight + RowSpacing);
            h += SectionSpacing;
            h += PresetRowHeight + RowSpacing; // Save button or naming input
            if (_manager.CustomPresets.Count > 0)
            {
                h += SectionSpacing + CategoryHeaderHeight + RowSpacing;
                h += _manager.CustomPresets.Count * (PresetRowHeight + RowSpacing);
            }
            h += Padding;
            return h;
        }

        private void DrawPresetsContent(ScrollView sv)
        {
            int relY = Padding;
            int contentW = sv.ContentWidth;

            // Built-in presets
            if (IsRowVisible(sv, relY, CategoryHeaderHeight))
                DrawCategoryDivider(sv.ContentX, sv.ContentY + relY, contentW, "Built-in Presets");
            relY += CategoryHeaderHeight + RowSpacing;

            foreach (var preset in BuiltInPresets)
            {
                if (IsRowVisible(sv, relY, PresetRowHeight))
                    DrawPresetRow(sv.ContentX, sv.ContentY + relY, contentW, preset);
                relY += PresetRowHeight + RowSpacing;
            }
            relY += SectionSpacing;

            // Save current as preset (or naming input)
            if (IsRowVisible(sv, relY, PresetRowHeight))
            {
                if (_presetNaming)
                    DrawPresetNamingRow(sv.ContentX, sv.ContentY + relY, contentW);
                else
                    DrawSavePresetButton(sv.ContentX, sv.ContentY + relY, contentW);
            }
            relY += PresetRowHeight + RowSpacing;

            // Custom presets
            if (_manager.CustomPresets.Count > 0)
            {
                relY += SectionSpacing;
                if (IsRowVisible(sv, relY, CategoryHeaderHeight))
                    DrawCategoryDivider(sv.ContentX, sv.ContentY + relY, contentW, "Custom Presets");
                relY += CategoryHeaderHeight + RowSpacing;

                var customKeys = new List<string>(_manager.CustomPresets.Keys);
                string toDelete = null;

                foreach (var presetName in customKeys)
                {
                    string[] ids;
                    if (!_manager.CustomPresets.TryGetValue(presetName, out ids)) continue;

                    if (IsRowVisible(sv, relY, PresetRowHeight))
                    {
                        string deleted = DrawCustomPresetRow(sv.ContentX, sv.ContentY + relY, contentW, presetName, ids);
                        if (deleted != null) toDelete = deleted;
                    }
                    relY += PresetRowHeight + RowSpacing;
                }

                if (toDelete != null) _manager.DeleteCustomPreset(toDelete);
            }
        }

        private void DrawPresetRow(int x, int y, int width, PresetDef preset)
        {
            bool hover = WidgetInput.IsMouseOver(x, y, width, PresetRowHeight);
            UIRenderer.DrawRect(x, y, width, PresetRowHeight, hover ? UIColors.ButtonHover : UIColors.Button);

            // Apply button
            int btnW = 60;
            int btnX = x + 4;
            int btnY = y + (PresetRowHeight - 22) / 2;
            if (Button.Draw(btnX, btnY, btnW, 22, "Apply", UIColors.SectionBg, UIColors.Accent, UIColors.TextDim))
                ApplyPreset(preset);

            // Preset name
            int nameX = btnX + btnW + 10;
            int nameMaxW = width - btnW - 18;
            UIRenderer.DrawText(TextUtil.Truncate(preset.Name, nameMaxW),
                nameX, y + (PresetRowHeight - 16) / 2, UIColors.Text);

            if (hover) Tooltip.Set(preset.Description);
        }

        private void DrawSavePresetButton(int x, int y, int width)
        {
            bool hasActive = _manager.HasOverrides;
            string saveLabel = "Save Current as Preset";
            int saveLabelW = TextUtil.MeasureWidth(saveLabel) + 16;

            if (hasActive)
            {
                if (Button.Draw(x, y, saveLabelW, PresetRowHeight, saveLabel))
                {
                    _presetNaming = true;
                    _presetNameInput.Clear();
                    _presetNameInput.Focus();
                }
            }
            else
            {
                UIRenderer.DrawRect(x, y, saveLabelW, PresetRowHeight, UIColors.Button.WithAlpha(100));
                UIRenderer.DrawText(saveLabel, x + 8, y + (PresetRowHeight - 16) / 2, UIColors.TextHint);
                if (WidgetInput.IsMouseOver(x, y, saveLabelW, PresetRowHeight))
                    Tooltip.Set("Check some features first to save a preset");
            }
        }

        private void DrawPresetNamingRow(int x, int y, int width)
        {
            int inputW = 200;
            _presetNameInput.Draw(x, y, inputW, PresetRowHeight);

            int lx = x + inputW + 4;

            // Save button
            int saveBtnW = 60;
            bool canSave = _presetNameInput.Text.Length > 0;
            if (canSave)
            {
                if (Button.Draw(lx, y, saveBtnW, PresetRowHeight, "Save",
                    UIColors.Button, UIColors.Success.WithAlpha(150), UIColors.Text))
                {
                    _manager.SaveCustomPreset(_presetNameInput.Text);
                    ExitPresetNaming();
                }
            }
            else
            {
                UIRenderer.DrawRect(lx, y, saveBtnW, PresetRowHeight, UIColors.Button);
                int saveTextW = TextUtil.MeasureWidth("Save");
                UIRenderer.DrawText("Save", lx + (saveBtnW - saveTextW) / 2,
                    y + (PresetRowHeight - 16) / 2, UIColors.TextHint);
            }
            lx += saveBtnW + 4;

            // Cancel button
            if (Button.Draw(lx, y, 70, PresetRowHeight, "Cancel"))
                ExitPresetNaming();

            // Enter to save
            if (_presetNameInput.IsFocused && canSave && InputState.IsKeyJustPressed(KeyCode.Enter))
            {
                _manager.SaveCustomPreset(_presetNameInput.Text);
                ExitPresetNaming();
            }
        }

        private string DrawCustomPresetRow(int x, int y, int width, string presetName, string[] ids)
        {
            bool rowHover = WidgetInput.IsMouseOver(x, y, width, PresetRowHeight);
            UIRenderer.DrawRect(x, y, width, PresetRowHeight, rowHover ? UIColors.ButtonHover : UIColors.Button);

            // Apply button
            int btnW = 60;
            int btnX = x + 4;
            int btnY = y + (PresetRowHeight - 22) / 2;
            if (Button.Draw(btnX, btnY, btnW, 22, "Apply", UIColors.SectionBg, UIColors.Accent, UIColors.TextDim))
            {
                _manager.ApplyCustomPreset(presetName);
                _categoryGroups = _manager.GetGroupsByCategory();
                _categoryEasyGroups = _manager.GetEasyGroupsByCategory();
            }

            // Name + feature count
            int nameX = btnX + btnW + 10;
            UIRenderer.DrawText(presetName, nameX, y + (PresetRowHeight - 16) / 2, UIColors.Text);
            string countText = $"({ids.Length})";
            int nameW = TextUtil.MeasureWidth(presetName);
            UIRenderer.DrawText(countText, nameX + nameW + 4,
                y + (PresetRowHeight - 16) / 2, UIColors.TextDim);

            // Delete button (right side)
            int delW = 24;
            int delX = x + width - delW - 4;
            int delY = y + (PresetRowHeight - 22) / 2;
            bool delHover = WidgetInput.IsMouseOver(delX, delY, delW, 22);
            UIRenderer.DrawRect(delX, delY, delW, 22, delHover ? UIColors.CloseBtnHover : UIColors.Button);
            UIRenderer.DrawText("X", delX + 7, delY + 3, delHover ? UIColors.Text : UIColors.TextDim);

            if (rowHover) Tooltip.Set($"Preset with {ids.Length} features");

            if (delHover && WidgetInput.MouseLeftClick)
            {
                WidgetInput.ConsumeClick();
                return presetName;
            }
            return null;
        }

        private void ApplyPreset(PresetDef preset)
        {
            _manager.ClearAll();

            if (preset.SeedId != null)
                _manager.SetAllForSeed(preset.SeedId, true);
            else if (preset.GroupIds != null)
                foreach (var gid in preset.GroupIds)
                    _manager.SetGroupChecked(gid, true);

            _categoryGroups = _manager.GetGroupsByCategory();
            _categoryEasyGroups = _manager.GetEasyGroupsByCategory();
        }

        private void ExitPresetNaming()
        {
            _presetNaming = false;
            _presetNameInput.Unfocus();
            _presetNameInput.Clear();
        }

        private class PresetDef
        {
            public string Name;
            public string Description;
            public string SeedId;
            public string[] GroupIds;
        }

        #endregion

        #region Drawing Helpers

        /// <summary>
        /// IsVisible with strict top-edge guard: items whose top is above the viewport
        /// are hidden. This prevents overflow when GPU scissor clipping isn't available
        /// (e.g., title screen where GameViewMatrix is null).
        /// </summary>
        private static bool IsRowVisible(ScrollView sv, int relY, int height)
        {
            return relY >= sv.ViewTop && sv.IsVisible(relY, height);
        }

        private void DrawCategoryDivider(int x, int y, int width, string text)
        {
            int textW = TextUtil.MeasureWidth(text);
            UIRenderer.DrawRect(x, y + CategoryHeaderHeight / 2, (width - textW) / 2 - 8, 1, UIColors.Divider);
            UIRenderer.DrawText(text, x + (width - textW) / 2, y + 4, UIColors.TextHint);
            int rightX = x + (width + textW) / 2 + 8;
            UIRenderer.DrawRect(rightX, y + CategoryHeaderHeight / 2, width - (rightX - x), 1, UIColors.Divider);
        }

        #endregion
    }
}
