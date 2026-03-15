using System;
using System.Globalization;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.GameInput;
using Terraria.GameContent.UI.Elements;
using Terraria.UI;
using TerrariaModder.Core.Config;
using TerrariaModder.Core.Input;

namespace TerrariaModder.Core.UI
{
    internal sealed class ModDetailState : NativeModsStateBase
    {
        private readonly ModInfo _mod;
        private string _editingFieldKey;
        private string _editingBuffer;
        private bool _editingIsNumber;
        private bool _capturingKeybind;
        private string _capturingKeybindId;

        public ModDetailState(NativeModsService service, UIState previousState, bool inGame, ModInfo mod)
            : base(service, previousState, inGame)
        {
            _mod = mod;
        }

        protected override string GetTitle() => _mod.Manifest.Name;

        public override void Update(GameTime gameTime)
        {
            base.Update(gameTime);

            if (_capturingKeybind)
            {
                UpdateKeyCapture();
                return;
            }

            if (!string.IsNullOrEmpty(_editingFieldKey))
                UpdateTextEditing();
        }

        protected override void RebuildList()
        {
            List.Clear();

            List.Add(CreateInfoRow($"ID: {_mod.Manifest.Id}"));
            List.Add(CreateInfoRow($"Version: {_mod.Manifest.Version ?? "?"}"));
            List.Add(CreateInfoRow($"Author: {_mod.Manifest.Author ?? "Unknown"}"));
            List.Add(CreateInfoRow($"State: {_mod.State}"));
            if (Service.ModNeedsRestart(_mod))
                List.Add(CreateInfoRow("Restart required for some changes to apply."));

            if (_mod.Context?.Config != null && _mod.Context.Config.Schema.Count > 0)
            {
                List.Add(CreateInfoRow("Configuration"));
                foreach (var field in _mod.Context.Config.Schema.Values)
                    List.Add(CreateConfigRow(field));

                List.Add(CreateButton("Reset Config To Defaults", () =>
                {
                    Service.ResetConfigToDefaults(_mod);
                    SetStatus("Config reset to defaults.");
                    RebuildList();
                }));
            }

            var keybinds = Service.GetKeybinds(_mod.Manifest.Id);
            bool anyKeybinds = false;
            foreach (var keybind in keybinds)
            {
                if (!anyKeybinds)
                {
                    List.Add(CreateInfoRow("Keybinds"));
                    anyKeybinds = true;
                }

                List.Add(CreateKeybindRow(keybind));
            }
        }

        private UIElement CreateConfigRow(ConfigField field)
        {
            string label = field.Label ?? field.Key;
            string suffix = string.IsNullOrEmpty(field.Description) ? string.Empty : $" - {field.Description}";
            var row = CreateButton($"{label}: {FormatConfigValue(field)}{suffix}", () => ActivateField(field), 44f);
            row.OnRightClick += (_, __) =>
            {
                DecrementField(field);
                RebuildList();
            };
            return row;
        }

        private UIElement CreateKeybindRow(Keybind keybind)
        {
            var row = CreateButton($"{keybind.Label}: {keybind.CurrentKey}", () =>
            {
                _capturingKeybind = true;
                _capturingKeybindId = keybind.Id;
                SetStatus($"Press a key for {keybind.Label}. Escape cancels, Backspace clears.");
            }, 40f);

            row.OnRightClick += (_, __) =>
            {
                Service.ResetKeybind(keybind);
                SetStatus($"{keybind.Label} reset.");
                RebuildList();
            };

            return row;
        }

        private string FormatConfigValue(ConfigField field)
        {
            try
            {
                switch (field.Type)
                {
                    case ConfigFieldType.Bool:
                        return _mod.Context.Config.Get<bool>(field.Key) ? "On" : "Off";
                    case ConfigFieldType.Int:
                        return _mod.Context.Config.Get<int>(field.Key).ToString(CultureInfo.InvariantCulture);
                    case ConfigFieldType.Float:
                        return _mod.Context.Config.Get<float>(field.Key).ToString("0.###", CultureInfo.InvariantCulture);
                    default:
                        return _mod.Context.Config.Get<string>(field.Key, string.Empty);
                }
            }
            catch (Exception ex)
            {
                return $"(error: {ex.Message})";
            }
        }

        private void ActivateField(ConfigField field)
        {
            switch (field.Type)
            {
                case ConfigFieldType.Bool:
                    Service.SetConfigValue(_mod, field, !_mod.Context.Config.Get<bool>(field.Key));
                    SetStatus($"{field.Label ?? field.Key} updated.");
                    RebuildList();
                    break;
                case ConfigFieldType.Enum:
                    CycleEnum(field, 1);
                    RebuildList();
                    break;
                case ConfigFieldType.Int:
                case ConfigFieldType.Float:
                    if (field.Min.HasValue && field.Max.HasValue)
                    {
                        ApplyNumericDelta(field, 1);
                        RebuildList();
                    }
                    else
                    {
                        BeginEditing(field.Key, FormatConfigValue(field), isNumber: true);
                    }
                    break;
                case ConfigFieldType.String:
                case ConfigFieldType.Key:
                    BeginEditing(field.Key, FormatConfigValue(field), isNumber: false);
                    break;
            }
        }

        private void DecrementField(ConfigField field)
        {
            switch (field.Type)
            {
                case ConfigFieldType.Enum:
                    CycleEnum(field, -1);
                    SetStatus($"{field.Label ?? field.Key} updated.");
                    break;
                case ConfigFieldType.Int:
                case ConfigFieldType.Float:
                    if (field.Min.HasValue && field.Max.HasValue)
                    {
                        ApplyNumericDelta(field, -1);
                        SetStatus($"{field.Label ?? field.Key} updated.");
                    }
                    break;
            }
        }

        private void ApplyNumericDelta(ConfigField field, int direction)
        {
            double step = field.Step ?? 1.0;
            if (field.Type == ConfigFieldType.Int)
            {
                int current = _mod.Context.Config.Get<int>(field.Key);
                Service.SetConfigValue(_mod, field, current + (int)Math.Round(step) * direction);
                return;
            }

            float value = _mod.Context.Config.Get<float>(field.Key);
            Service.SetConfigValue(_mod, field, value + (float)step * direction);
        }

        private void CycleEnum(ConfigField field, int direction)
        {
            if (field.Options == null || field.Options.Count == 0) return;

            string current = _mod.Context.Config.Get<string>(field.Key, field.Options[0]);
            int index = field.Options.IndexOf(current);
            if (index < 0) index = 0;
            index = (index + direction + field.Options.Count) % field.Options.Count;
            Service.SetConfigValue(_mod, field, field.Options[index]);
            SetStatus($"{field.Label ?? field.Key} updated.");
        }

        private void BeginEditing(string fieldKey, string value, bool isNumber)
        {
            _editingFieldKey = fieldKey;
            _editingBuffer = value ?? string.Empty;
            _editingIsNumber = isNumber;
            Main.clrInput();
            SetStatus($"Editing {fieldKey}. Press Enter to save, Escape to cancel.");
        }

        private void UpdateTextEditing()
        {
            PlayerInput.WritingText = true;
            Main.instance.HandleIME();
            _editingBuffer = Main.GetInputText(_editingBuffer);

            if (InputState.IsKeyJustPressed(KeyCode.Escape))
            {
                CancelEditing();
                return;
            }

            if (!Main.inputTextEnter) return;

            var field = _mod.Context.Config.Schema[_editingFieldKey];
            try
            {
                if (_editingIsNumber)
                {
                    if (field.Type == ConfigFieldType.Int && int.TryParse(_editingBuffer, out int intValue))
                        Service.SetConfigValue(_mod, field, intValue);
                    else if (field.Type == ConfigFieldType.Float && float.TryParse(_editingBuffer, NumberStyles.Float, CultureInfo.InvariantCulture, out float floatValue))
                        Service.SetConfigValue(_mod, field, floatValue);
                    else
                    {
                        SetStatus("Invalid numeric value.");
                        Main.clrInput();
                        return;
                    }
                }
                else
                {
                    Service.SetConfigValue(_mod, field, _editingBuffer);
                }

                SetStatus($"{field.Label ?? field.Key} saved.");
            }
            catch (Exception ex)
            {
                SetStatus($"Save failed: {ex.Message}");
            }

            CancelEditing(rebuild: true);
        }

        private void CancelEditing(bool rebuild = false)
        {
            _editingFieldKey = null;
            _editingBuffer = null;
            _editingIsNumber = false;
            Main.clrInput();
            PlayerInput.WritingText = false;
            if (rebuild)
                RebuildList();
        }

        private void UpdateKeyCapture()
        {
            if (InputState.IsKeyJustPressed(KeyCode.Escape))
            {
                _capturingKeybind = false;
                _capturingKeybindId = null;
                SetStatus("Keybind capture cancelled.");
                return;
            }

            if (InputState.IsKeyJustPressed(KeyCode.Back))
            {
                var keybind = KeybindManager.GetKeybind(_capturingKeybindId);
                Service.SetKeybind(keybind, new KeyCombo());
                _capturingKeybind = false;
                _capturingKeybindId = null;
                SetStatus("Keybind cleared.");
                RebuildList();
                return;
            }

            foreach (int keyCode in KeyCaptureCandidates)
            {
                if (keyCode == KeyCode.None ||
                    keyCode == KeyCode.LeftControl ||
                    keyCode == KeyCode.RightControl ||
                    keyCode == KeyCode.LeftShift ||
                    keyCode == KeyCode.RightShift ||
                    keyCode == KeyCode.LeftAlt ||
                    keyCode == KeyCode.RightAlt)
                {
                    continue;
                }

                if (!InputState.IsKeyJustPressed(keyCode))
                    continue;

                var combo = new KeyCombo(keyCode, InputState.IsCtrlDown(), InputState.IsShiftDown(), InputState.IsAltDown());
                var keybind = KeybindManager.GetKeybind(_capturingKeybindId);
                Service.SetKeybind(keybind, combo);
                _capturingKeybind = false;
                _capturingKeybindId = null;
                SetStatus($"{keybind.Label} rebound to {combo}.");
                RebuildList();
                return;
            }
        }

        private static readonly int[] KeyCaptureCandidates = BuildKeyCaptureCandidates();

        private static int[] BuildKeyCaptureCandidates()
        {
            var keys = new System.Collections.Generic.List<int>();
            for (int i = KeyCode.A; i <= KeyCode.Z; i++) keys.Add(i);
            for (int i = KeyCode.D0; i <= KeyCode.D9; i++) keys.Add(i);
            for (int i = KeyCode.F1; i <= KeyCode.F12; i++) keys.Add(i);
            for (int i = KeyCode.NumPad0; i <= KeyCode.NumPad9; i++) keys.Add(i);

            keys.Add(KeyCode.Space);
            keys.Add(KeyCode.Enter);
            keys.Add(KeyCode.Tab);
            keys.Add(KeyCode.Insert);
            keys.Add(KeyCode.Delete);
            keys.Add(KeyCode.Home);
            keys.Add(KeyCode.End);
            keys.Add(KeyCode.PageUp);
            keys.Add(KeyCode.PageDown);
            keys.Add(KeyCode.Up);
            keys.Add(KeyCode.Down);
            keys.Add(KeyCode.Left);
            keys.Add(KeyCode.Right);
            keys.Add(KeyCode.OemTilde);
            keys.Add(KeyCode.OemMinus);
            keys.Add(KeyCode.OemPlus);
            keys.Add(KeyCode.OemOpenBrackets);
            keys.Add(KeyCode.OemCloseBrackets);
            keys.Add(KeyCode.OemPipe);
            keys.Add(KeyCode.OemSemicolon);
            keys.Add(KeyCode.OemQuotes);
            keys.Add(KeyCode.OemComma);
            keys.Add(KeyCode.OemPeriod);
            keys.Add(KeyCode.OemQuestion);
            keys.Add(KeyCode.MouseLeft);
            keys.Add(KeyCode.MouseRight);
            keys.Add(KeyCode.MouseMiddle);
            return keys.ToArray();
        }
    }
}
