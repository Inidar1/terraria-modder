using System;
using System.Collections.Generic;
using TerrariaModder.Core.Logging;
using TerrariaModder.Core.UI;
using TerrariaModder.Core.UI.Widgets;

namespace Randomizer.UI
{
    /// <summary>
    /// Main in-game UI panel for the Randomizer mod.
    /// Shows seed input, preset buttons, runtime module toggles, and locked world-gen status.
    /// </summary>
    public class RandomizerPanel
    {
        private readonly ILogger _log;
        private readonly Mod _mod;
        private readonly DraggablePanel _panel;
        private readonly TextInput _seedInput;
        private readonly ScrollView _scrollView = new ScrollView();

        // Collapsible section state
        private readonly HashSet<string> _collapsedSections = new HashSet<string>();

        // Track last seed to detect external changes (Random button, +1)
        private int _lastSyncedSeed;

        // Layout constants
        private const int PanelWidth = 420;
        private const int PanelHeight = 540;
        private const int Padding = 8;
        private const int SeedAreaHeight = 120;
        private const int ToggleHeight = 44;
        private const int SectionHeight = 22;

        // Preset definitions (runtime modules only)
        private static readonly HashSet<string> SafeModules = new HashSet<string>
        {
            "enemy_drops", "recipes", "shops", "fishing", "weather"
        };

        public RandomizerPanel(ILogger log, Mod mod)
        {
            _log = log;
            _mod = mod;
            _panel = new DraggablePanel("randomizer", "Randomizer", PanelWidth, PanelHeight);
            _panel.ClipContent = false; // We manage clipping for scroll region

            _seedInput = new TextInput("Enter seed...", 12);
            _seedInput.KeyBlockId = "randomizer";
            _seedInput.Text = _mod.Seed.Seed.ToString();
            _lastSyncedSeed = _mod.Seed.Seed;
        }

        public void Toggle()
        {
            _panel.Toggle();
        }

        public void Close()
        {
            _seedInput.Unfocus();
            _panel.Close();
        }

        /// <summary>
        /// Call from the Update phase to process TextInput keyboard events.
        /// </summary>
        public void Update()
        {
            if (!_panel.IsOpen) return;
            _seedInput.Update();

            // Check if seed changed externally (Random/+1 buttons)
            if (_mod.Seed.Seed != _lastSyncedSeed)
            {
                _seedInput.Text = _mod.Seed.Seed.ToString();
                _lastSyncedSeed = _mod.Seed.Seed;
            }

            // Check if user typed a new seed
            if (_seedInput.HasChanged && !_seedInput.IsFocused)
            {
                ApplySeedFromInput();
            }
        }

        public void Draw()
        {
            if (!_panel.BeginDraw()) return;

            try
            {
                int cx = _panel.ContentX;
                int cy = _panel.ContentY;
                int cw = _panel.ContentWidth;

                // -- Seed Area (fixed, not scrolled) --
                DrawSeedArea(cx, cy, cw);

                // -- Divider --
                int dividerY = cy + SeedAreaHeight;
                UIRenderer.DrawRect(cx, dividerY, cw, 1, UIColors.Divider);

                // -- Scrollable Module List (clipped to prevent overflow) --
                int listTop = dividerY + 4;
                int listHeight = _panel.ContentHeight - SeedAreaHeight - 4;

                int contentH = CalculateContentHeight();
                _scrollView.Begin(cx, listTop, cw, listHeight, contentH);

                // No BeginClip — virtual scrolling via IsVisible handles culling.
                // BeginClip restarts SpriteBatch with an incorrect transform on some screens.
                DrawModuleList(_scrollView.ContentX, _scrollView.ContentY, _scrollView.ContentWidth);
                _scrollView.End();
            }
            catch (Exception ex)
            {
                _log.Error($"[Randomizer] Panel draw error: {ex.Message}");
            }
            finally
            {
                _panel.EndDraw();
            }
        }

        private void DrawSeedArea(int x, int y, int width)
        {
            var s = new StackLayout(x, y, width);

            // Seed label + text input
            s.Label("Seed", UIColors.TextDim, 16);
            int inputY = s.Advance(28);
            _seedInput.Draw(x, inputY, width - 80, 26);

            // Apply button next to input
            if (Button.Draw(x + width - 72, inputY, 72, 26, "Apply"))
            {
                ApplySeedFromInput();
                _seedInput.Unfocus();
            }
            s.Space(4);

            // Row: Random + Seed+1
            int halfW = (width - 8) / 2;
            int btnY = s.Advance(26);
            if (Button.Draw(x, btnY, halfW, 26, "Random Seed"))
            {
                _mod.OnSeedChanged(0);
                _seedInput.Text = _mod.Seed.Seed.ToString();
                _lastSyncedSeed = _mod.Seed.Seed;
            }
            if (Button.Draw(x + halfW + 8, btnY, halfW, 26, "Seed + 1"))
            {
                int next = _mod.Seed.Seed == int.MaxValue ? 1 : _mod.Seed.Seed + 1;
                _mod.OnSeedChanged(next);
                _seedInput.Text = _mod.Seed.Seed.ToString();
                _lastSyncedSeed = _mod.Seed.Seed;
            }
            s.Space(4);

            // Preset row: Safe / Chaos / All Off (runtime modules only)
            int thirdW = (width - 16) / 3;
            int presetY = s.Advance(26);
            if (Button.Draw(x, presetY, thirdW, 26, "Safe"))
            {
                ApplyPreset(SafeModules);
            }
            if (Button.Draw(x + thirdW + 8, presetY, thirdW, 26, "Chaos"))
            {
                ApplyPresetAll(true);
            }
            if (Button.Draw(x + thirdW * 2 + 16, presetY, thirdW, 26, "All Off"))
            {
                ApplyPresetAll(false);
            }
        }

        private void DrawModuleList(int x, int y, int width)
        {
            int cy = y;
            var modules = _mod.Modules;

            // -- Runtime Modules --
            string lastSection = null;
            for (int i = 0; i < modules.Count; i++)
            {
                var m = modules[i];
                if (m.IsWorldGen) continue; // Skip world-gen modules in main list

                string section = GetSection(m.Id);

                if (section != lastSection)
                {
                    cy = DrawSectionHeader(x, cy, width, section);
                    lastSection = section;
                }

                if (_collapsedSections.Contains(section)) continue;

                cy = DrawModuleToggle(x, cy, width, m);
            }

            // -- World Generation Section --
            cy = DrawWorldGenSection(x, cy, width);
        }

        private int DrawSectionHeader(int x, int y, int width, string section)
        {
            string label = GetSectionLabel(section);
            bool collapsed = _collapsedSections.Contains(section);
            string prefix = collapsed ? "> " : "v ";

            if (_scrollView.IsVisible(y - _scrollView.ContentY, SectionHeight))
            {
                bool hover = !_panel.BlockInput && WidgetInput.IsMouseOver(x, y, width, SectionHeight);
                if (hover)
                {
                    UIRenderer.DrawRect(x, y, width, SectionHeight, UIColors.SectionBg);
                }

                SectionHeader.Draw(x, y, width, prefix + label, SectionHeight);

                if (hover && WidgetInput.MouseLeftClick)
                {
                    if (collapsed)
                        _collapsedSections.Remove(section);
                    else
                        _collapsedSections.Add(section);
                    WidgetInput.ConsumeClick();
                }
            }

            return y + SectionHeight + 4;
        }

        private int DrawModuleToggle(int x, int y, int width, ModuleBase module)
        {
            if (_scrollView.IsVisible(y - _scrollView.ContentY, ToggleHeight))
            {
                bool hover = !_panel.BlockInput && WidgetInput.IsMouseOver(x, y, width, ToggleHeight);
                var bg = module.Enabled
                    ? (hover ? new Color4(60, 140, 60, 200) : new Color4(40, 110, 40, 150))
                    : (hover ? UIColors.ButtonHover : UIColors.Button);
                UIRenderer.DrawRect(x, y, width, ToggleHeight, bg);

                // Warning badge for dangerous modules
                int textStartX = x + 4;
                if (module.IsDangerous)
                {
                    UIRenderer.DrawRect(x + 4, y + 4, 16, 16, UIColors.Warning);
                    UIRenderer.DrawText("!", x + 9, y + 3, UIColors.PanelBg);
                    textStartX = x + 24;
                }

                // Checkbox
                int cbSize = 16;
                int cbX = textStartX;
                int cbY = y + (ToggleHeight - cbSize) / 2;
                Checkbox.Draw(cbX, cbY, cbSize, module.Enabled);

                // Name + Description (truncated to available width)
                int nameX = cbX + cbSize + 8;
                int textMaxWidth = width - (nameX - x);
                UIRenderer.DrawText(TextUtil.Truncate(module.Name, textMaxWidth), nameX, y + 6,
                    module.Enabled ? UIColors.Text : UIColors.TextDim);
                UIRenderer.DrawText(TextUtil.Truncate(module.Description, textMaxWidth), nameX, y + 24, UIColors.TextHint);

                // Click handling
                if (hover && WidgetInput.MouseLeftClick)
                {
                    module.Enabled = !module.Enabled;
                    _mod.OnModuleToggled(module);
                    WidgetInput.ConsumeClick();
                }

                // Tooltip on hover
                if (hover)
                {
                    string tipTitle = module.Name;
                    if (module.IsDangerous) tipTitle += " (Caution)";
                    Tooltip.Set(tipTitle, module.Tooltip);
                }
            }

            return y + ToggleHeight + 2;
        }

        /// <summary>
        /// Draw the world generation section at the bottom of the module list.
        /// Shows locked status for world-gen modules.
        /// </summary>
        private int DrawWorldGenSection(int x, int y, int width)
        {
            // Section header
            if (_scrollView.IsVisible(y - _scrollView.ContentY, SectionHeight))
            {
                SectionHeader.Draw(x, y, width, "WORLD GENERATION", SectionHeight);
            }
            y += SectionHeight + 4;

            // Draw each world-gen module
            foreach (var m in _mod.Modules)
            {
                if (!m.IsWorldGen) continue;

                if (_scrollView.IsVisible(y - _scrollView.ContentY, ToggleHeight))
                {
                    DrawWorldGenModuleRow(x, y, width, m);
                }
                y += ToggleHeight + 2;
            }

            return y;
        }

        private void DrawWorldGenModuleRow(int x, int y, int width, ModuleBase module)
        {
            bool hover = !_panel.BlockInput && WidgetInput.IsMouseOver(x, y, width, ToggleHeight);

            int cbSize = 16;
            int cbX = x + 8;
            int cbY = y + (ToggleHeight - cbSize) / 2;
            int nameX = cbX + cbSize + 8;
            int textMaxWidth = width - (nameX - x);

            if (module.IsLocked)
            {
                // Active and locked — green background, non-interactive
                var bg = new Color4(30, 80, 30, 150);
                UIRenderer.DrawRect(x, y, width, ToggleHeight, bg);

                // Left accent bar
                UIRenderer.DrawRect(x, y, 3, ToggleHeight, UIColors.Success);

                // Checkbox (checked, grayed)
                UIRenderer.DrawRect(cbX, cbY, cbSize, cbSize, UIColors.InputBg);
                UIRenderer.DrawRectOutline(cbX, cbY, cbSize, cbSize, UIColors.Border);
                UIRenderer.DrawRect(cbX + 3, cbY + 3, cbSize - 6, cbSize - 6, UIColors.Success);

                UIRenderer.DrawText(TextUtil.Truncate(module.Name, textMaxWidth), nameX, y + 6, UIColors.Text);
                UIRenderer.DrawText("Active - set at world creation", nameX, y + 24, UIColors.Success);
            }
            else
            {
                // Not active — grayed out
                UIRenderer.DrawRect(x, y, width, ToggleHeight, UIColors.Button.WithAlpha(100));

                // Checkbox (unchecked, dimmed)
                UIRenderer.DrawRect(cbX, cbY, cbSize, cbSize, UIColors.InputBg.WithAlpha(100));
                UIRenderer.DrawRectOutline(cbX, cbY, cbSize, cbSize, UIColors.Border.WithAlpha(100));

                UIRenderer.DrawText(TextUtil.Truncate(module.Name, textMaxWidth), nameX, y + 6, UIColors.TextHint);
                UIRenderer.DrawText("Not enabled for this world", nameX, y + 24, UIColors.TextHint);
            }

            // Tooltip
            if (hover)
            {
                if (module.IsLocked)
                {
                    Tooltip.Set(module.Name + " (Locked)",
                        module.Tooltip + "\n\nThis module was enabled at world creation and cannot be changed.");
                }
                else
                {
                    Tooltip.Set(module.Name + " (Inactive)",
                        module.Tooltip + "\n\nConfigure in the world creation menu (Numpad /) before entering a new world.");
                }
            }
        }

        private int CalculateContentHeight()
        {
            var modules = _mod.Modules;
            int height = 0;
            string lastSection = null;

            // Runtime modules
            for (int i = 0; i < modules.Count; i++)
            {
                if (modules[i].IsWorldGen) continue;

                string section = GetSection(modules[i].Id);
                if (section != lastSection)
                {
                    height += SectionHeight + 4;
                    lastSection = section;
                }
                if (!_collapsedSections.Contains(section))
                {
                    height += ToggleHeight + 2;
                }
            }

            // World-gen section header
            height += SectionHeight + 4;

            // World-gen modules
            for (int i = 0; i < modules.Count; i++)
            {
                if (modules[i].IsWorldGen)
                    height += ToggleHeight + 2;
            }

            return height + 8; // Bottom padding
        }

        private void ApplySeedFromInput()
        {
            string text = _seedInput.Text?.Trim();
            if (string.IsNullOrEmpty(text)) return;

            if (int.TryParse(text, out int newSeed))
            {
                _mod.OnSeedChanged(newSeed);
                _lastSyncedSeed = _mod.Seed.Seed;
                _seedInput.Text = _mod.Seed.Seed.ToString();
            }
            else
            {
                int hashSeed = text.GetHashCode();
                if (hashSeed == 0) hashSeed = 1; // Prevent zero triggering random seed
                _mod.OnSeedChanged(hashSeed);
                _lastSyncedSeed = _mod.Seed.Seed;
                _seedInput.Text = _mod.Seed.Seed.ToString();
            }
        }

        /// <summary>
        /// Presets only affect runtime modules, not locked world-gen modules.
        /// </summary>
        private void ApplyPreset(HashSet<string> enabledModules)
        {
            foreach (var m in _mod.Modules)
            {
                if (m.IsWorldGen) continue; // Skip world-gen modules
                bool shouldEnable = enabledModules.Contains(m.Id);
                if (m.Enabled != shouldEnable)
                {
                    m.Enabled = shouldEnable;
                    _mod.OnModuleToggled(m);
                }
            }
        }

        /// <summary>
        /// Presets only affect runtime modules, not locked world-gen modules.
        /// </summary>
        private void ApplyPresetAll(bool enabled)
        {
            foreach (var m in _mod.Modules)
            {
                if (m.IsWorldGen) continue; // Skip world-gen modules
                if (m.Enabled != enabled)
                {
                    m.Enabled = enabled;
                    _mod.OnModuleToggled(m);
                }
            }
        }

        private string GetSection(string moduleId)
        {
            switch (moduleId)
            {
                case "enemy_drops":
                case "recipes":
                    return "loot";
                case "shops":
                case "fishing":
                case "tile_drops":
                case "spawns":
                    return "world";
                default:
                    return "chaos";
            }
        }

        private string GetSectionLabel(string section)
        {
            switch (section)
            {
                case "loot": return "LOOT RANDOMIZATION";
                case "world": return "WORLD RANDOMIZATION";
                case "chaos": return "CHAOS MODIFIERS";
                default: return section.ToUpper();
            }
        }
    }
}
