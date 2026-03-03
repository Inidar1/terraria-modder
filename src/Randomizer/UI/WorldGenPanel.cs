using System;
using System.Collections.Generic;
using TerrariaModder.Core.Input;
using TerrariaModder.Core.Logging;
using TerrariaModder.Core.UI;
using TerrariaModder.Core.UI.Widgets;

namespace Randomizer.UI
{
    /// <summary>
    /// Menu panel for configuring world-gen randomizer modules before creating a world.
    /// Accessible via numpad / in the main menu. Settings are "armed" and applied to
    /// the next world entered, then locked permanently for that world.
    /// </summary>
    public class WorldGenPanel
    {
        private readonly ILogger _log;
        private readonly Mod _mod;
        private readonly WorldGenState _worldGenState;
        private readonly DraggablePanel _panel;
        private readonly TextInput _seedInput;

        // Track last seed for external change detection
        private int _lastSyncedSeed;

        // Layout
        private const int PanelWidth = 380;
        private const int PanelHeight = 340;
        private const int Padding = 8;
        private const int ToggleHeight = 44;

        public bool Visible
        {
            get => _panel.IsOpen;
            set
            {
                if (value && !_panel.IsOpen)
                    _panel.Open();
                else if (!value && _panel.IsOpen)
                    Close();
            }
        }

        public WorldGenPanel(ILogger log, Mod mod, WorldGenState worldGenState)
        {
            _log = log;
            _mod = mod;
            _worldGenState = worldGenState;
            _panel = new DraggablePanel("randomizer-worldgen", "Randomizer - World Gen", PanelWidth, PanelHeight);
            _panel.ClipContent = true;

            _seedInput = new TextInput("Enter seed...", 12);
            _seedInput.KeyBlockId = "randomizer-wg";
            _seedInput.Text = _worldGenState.ArmedSeed != 0
                ? _worldGenState.ArmedSeed.ToString()
                : _mod.Seed.Seed.ToString();
            _lastSyncedSeed = _worldGenState.ArmedSeed != 0
                ? _worldGenState.ArmedSeed
                : _mod.Seed.Seed;
        }

        public void Close()
        {
            _seedInput.Unfocus();
            _panel.Close();
        }

        public void Update()
        {
            if (!_panel.IsOpen) return;
            _seedInput.Update();

            // Check if user typed a new seed and unfocused
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

                var s = new StackLayout(cx, cy, cw);

                // -- Seed Area --
                s.Label("Seed", UIColors.TextDim, 16);
                int inputY = s.Advance(28);
                _seedInput.Draw(cx, inputY, cw - 80, 26);

                if (Button.Draw(cx + cw - 72, inputY, 72, 26, "Apply"))
                {
                    ApplySeedFromInput();
                    _seedInput.Unfocus();
                }
                s.Space(4);

                // Row: Random + Seed+1
                int halfW = (cw - 8) / 2;
                int btnY = s.Advance(26);
                if (Button.Draw(cx, btnY, halfW, 26, "Random Seed"))
                {
                    int newSeed = new Random().Next(1, int.MaxValue);
                    _worldGenState.SetArmedSeed(newSeed);
                    _seedInput.Text = newSeed.ToString();
                    _lastSyncedSeed = newSeed;
                }
                if (Button.Draw(cx + halfW + 8, btnY, halfW, 26, "Seed + 1"))
                {
                    int current = _worldGenState.ArmedSeed != 0
                        ? _worldGenState.ArmedSeed : _mod.Seed.Seed;
                    int newSeed = current == int.MaxValue ? 1 : current + 1;
                    _worldGenState.SetArmedSeed(newSeed);
                    _seedInput.Text = newSeed.ToString();
                    _lastSyncedSeed = newSeed;
                }

                // -- Divider --
                s.Space(4);
                s.Divider();

                // -- Module Checkboxes --
                s.Label("MODULES", UIColors.TextDim, 16);

                foreach (var m in _mod.Modules)
                {
                    if (!m.IsWorldGen) continue;

                    int toggleY = s.Advance(ToggleHeight);
                    DrawModuleToggle(cx, toggleY, cw, m);
                }

                // -- Footer Status --
                s.Space(8);
                UIRenderer.DrawRect(cx, s.CurrentY, cw, 1, UIColors.Divider);
                s.Space(6);

                if (_worldGenState.HasArmedSettings)
                {
                    s.Label("Settings armed for next world", UIColors.Warning, 16);
                }
                else
                {
                    s.Label("Enable modules above, then enter a world", UIColors.TextHint, 16);
                }
            }
            catch (Exception ex)
            {
                _log.Error($"[Randomizer] WorldGenPanel draw error: {ex.Message}");
            }
            finally
            {
                _panel.EndDraw();
            }
        }

        private void DrawModuleToggle(int x, int y, int width, ModuleBase module)
        {
            bool armed = _worldGenState.IsArmed(module.Id);
            bool hover = !_panel.BlockInput && WidgetInput.IsMouseOver(x, y, width, ToggleHeight);

            // Background
            var bg = armed
                ? (hover ? new Color4(60, 140, 60, 200) : new Color4(40, 110, 40, 150))
                : (hover ? UIColors.ButtonHover : UIColors.Button);
            UIRenderer.DrawRect(x, y, width, ToggleHeight, bg);

            // Checkbox
            int cbSize = 16;
            int cbX = x + 4;
            int cbY = y + (ToggleHeight - cbSize) / 2;
            Checkbox.Draw(cbX, cbY, cbSize, armed);

            // Name + Description (truncated to available width)
            int nameX = cbX + cbSize + 8;
            int textMaxWidth = width - (nameX - x);
            UIRenderer.DrawText(TextUtil.Truncate(module.Name, textMaxWidth), nameX, y + 6,
                armed ? UIColors.Text : UIColors.TextDim);
            UIRenderer.DrawText(TextUtil.Truncate(module.Description, textMaxWidth), nameX, y + 24, UIColors.TextHint);

            // Click handling
            if (hover && WidgetInput.MouseLeftClick)
            {
                _worldGenState.SetArmed(module.Id, !armed);
                WidgetInput.ConsumeClick();
            }

            // Tooltip
            if (hover)
            {
                Tooltip.Set(module.Name, module.Tooltip);
            }
        }

        private void ApplySeedFromInput()
        {
            string text = _seedInput.Text?.Trim();
            if (string.IsNullOrEmpty(text)) return;

            int newSeed;
            if (int.TryParse(text, out newSeed))
            {
                if (newSeed == 0) newSeed = new Random().Next(1, int.MaxValue);
            }
            else
            {
                newSeed = text.GetHashCode();
                if (newSeed == 0) newSeed = 1;
            }

            _worldGenState.SetArmedSeed(newSeed);
            _seedInput.Text = newSeed.ToString();
            _lastSyncedSeed = newSeed;
        }
    }
}
