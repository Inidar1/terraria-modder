using System;
using System.Collections.Generic;
using System.Globalization;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
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
        private UIElement _summaryArea;
        private UIPanel _iconPanel;
        private UIPanel _metaPanel;

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

        protected override string GetTitle() => "Mod Details";

        public override void OnInitialize()
        {
            base.OnInitialize();
            BuildDetailLayout();
        }

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

        protected override bool CanGoBack()
        {
            return !_capturingKeybind && string.IsNullOrEmpty(_editingFieldKey);
        }

        protected override void RebuildList()
        {
            float scrollPosition = GetScrollPosition();
            List.Clear();

            if (Service.ModNeedsRestart(_mod))
                List.Add(CreateBannerRow("Restart required for some changes to apply."));

            if (_mod.Context?.Config != null && _mod.Context.Config.Schema.Count > 0)
            {
                List.Add(CreateSectionRow("Configuration"));
                foreach (var field in _mod.Context.Config.Schema.Values)
                    List.Add(CreateConfigRow(field));

                List.Add(CreateActionRow("Reset Config To Defaults", "Restore every field in this mod to its schema default.", () =>
                {
                    Service.ResetConfigToDefaults(_mod);
                    SetStatus("Config reset to defaults.");
                    RebuildList();
                }));
            }
            else
            {
                List.Add(CreateBannerRow("This mod does not expose any configuration fields."));
            }

            var keybinds = new List<Keybind>(Service.GetKeybinds(_mod.Manifest.Id));
            if (keybinds.Count > 0)
            {
                List.Add(CreateSectionRow("Keybinds"));
                foreach (var keybind in keybinds)
                    List.Add(CreateKeybindRow(keybind));
            }

            RestoreScrollPosition(scrollPosition);
        }

        private void BuildDetailLayout()
        {
            PluginLoader.LoadModIcons();

            Root.MaxWidth.Set(1040f, 0f);

            Panel.Top.Set(244f, 0f);
            Panel.Height.Set(-352f, 1f);
            Panel.BackgroundColor = new Color(22, 30, 52) * 0.92f;

            List.Top.Set(14f, 0f);
            List.Height.Set(-26f, 1f);
            List.ListPadding = 10f;

            _summaryArea = new UIElement();
            _summaryArea.Width.Set(0f, 1f);
            _summaryArea.Height.Set(208f, 0f);
            _summaryArea.Top.Set(4f, 0f);
            Root.Append(_summaryArea);

            _iconPanel = new UIPanel();
            _iconPanel.Width.Set(188f, 0f);
            _iconPanel.Height.Set(208f, 0f);
            _iconPanel.BackgroundColor = new Color(28, 37, 66) * 0.95f;
            _summaryArea.Append(_iconPanel);

            _metaPanel = new UIPanel();
            _metaPanel.Left.Set(202f, 0f);
            _metaPanel.Width.Set(-202f, 1f);
            _metaPanel.Height.Set(208f, 0f);
            _metaPanel.BackgroundColor = new Color(28, 37, 66) * 0.95f;
            _summaryArea.Append(_metaPanel);

            BuildIconContents();
            BuildMetaContents();
        }

        private void BuildIconContents()
        {
            object texture = _mod.IconTexture ?? PluginLoader.DefaultIcon;
            if (texture != null)
            {
                var image = new ModIconElement(texture);
                image.Width.Set(132f, 0f);
                image.Height.Set(132f, 0f);
                image.HAlign = 0.5f;
                image.VAlign = 0.5f;
                _iconPanel.Append(image);
                return;
            }

            var placeholder = new UIText("MOD\nPICTURE", 0.9f, large: true);
            placeholder.HAlign = 0.5f;
            placeholder.VAlign = 0.5f;
            _iconPanel.Append(placeholder);
        }

        private void BuildMetaContents()
        {
            float top = 18f;

            var name = new UIText(_mod.Manifest.Name ?? _mod.Manifest.Id, 1.0f, large: true);
            name.Left.Set(16f, 0f);
            name.Top.Set(top, 0f);
            _metaPanel.Append(name);
            top += 40f;

            _metaPanel.Append(CreateMetaText($"Mod ID: {_mod.Manifest.Id}", top));
            top += 28f;
            _metaPanel.Append(CreateMetaText($"Version: {_mod.Manifest.Version ?? "?"}", top));
            top += 28f;
            _metaPanel.Append(CreateMetaText($"Author: {_mod.Manifest.Author ?? "Unknown"}", top));
            top += 28f;
            _metaPanel.Append(CreateMetaText($"State: {_mod.State}", top));
            top += 32f;

            string descriptionText = string.IsNullOrWhiteSpace(_mod.Manifest.Description)
                ? "No description provided."
                : _mod.Manifest.Description;
            var description = new UIText(descriptionText, 0.62f, large: false);
            description.Left.Set(16f, 0f);
            description.Top.Set(top, 0f);
            description.Width.Set(-32f, 1f);
            description.Height.Set(-top - 12f, 1f);
            description.IsWrapped = true;
            description.TextColor = new Color(206, 214, 236);
            _metaPanel.Append(description);

            if (!string.IsNullOrWhiteSpace(_mod.ErrorMessage))
            {
                top += 64f;
                var error = CreateMetaText(_mod.ErrorMessage, top);
                error.TextColor = new Color(255, 180, 120);
                _metaPanel.Append(error);
            }
        }

        private static UIText CreateMetaText(string text, float top)
        {
            var uiText = new UIText(text, 0.72f, large: false);
            uiText.Left.Set(16f, 0f);
            uiText.Top.Set(top, 0f);
            return uiText;
        }

        private UIElement CreateSectionRow(string title)
        {
            var panel = new UIPanel();
            panel.Width.Set(0f, 1f);
            panel.Height.Set(48f, 0f);
            panel.BackgroundColor = new Color(45, 61, 108) * 0.95f;
            panel.BorderColor = new Color(100, 123, 184);

            var text = new UIText(title, 0.72f, large: true);
            text.VAlign = 0.5f;
            text.Top.Set(-4f, 0f);
            text.HAlign = 0.5f;
            panel.Append(text);
            return panel;
        }

        private UIElement CreateBannerRow(string text)
        {
            var panel = new UIPanel();
            panel.Width.Set(0f, 1f);
            panel.Height.Set(46f, 0f);
            panel.BackgroundColor = new Color(56, 42, 23) * 0.96f;
            panel.BorderColor = new Color(175, 129, 52);

            var label = new UIText(text, 0.66f, large: false);
            label.Left.Set(14f, 0f);
            label.Top.Set(11f, 0f);
            panel.Append(label);
            return panel;
        }

        private UIElement CreateActionRow(string title, string subtitle, Action onClick)
        {
            var panel = CreateRowContainer(68f);
            var button = CreateControlButton(title);
            button.Width.Set(230f, 0f);
            button.Height.Set(38f, 0f);
            button.VAlign = 0.5f;
            button.HAlign = 1f;
            button.OnLeftClick += (_, __) => onClick();
            panel.Append(button);

            panel.Append(CreateRowTitle(title));
            panel.Append(CreateRowSubtitle(subtitle));
            return panel;
        }

        private UIElement CreateConfigRow(ConfigField field)
        {
            switch (field.Type)
            {
                case ConfigFieldType.Bool:
                    return CreateBoolConfigRow(field);
                case ConfigFieldType.Enum:
                    return CreateEnumConfigRow(field);
                case ConfigFieldType.Int:
                case ConfigFieldType.Float:
                    return CreateNumericConfigRow(field);
                default:
                    return CreateTextConfigRow(field);
            }
        }

        private UIElement CreateBoolConfigRow(ConfigField field)
        {
            bool value = _mod.Context.Config.Get<bool>(field.Key);
            var panel = CreateRowContainer(70f);
            panel.Append(CreateRowTitle(field.Label ?? field.Key));
            panel.Append(CreateRowSubtitle(GetFieldDescription(field)));

            var checkbox = CreateControlButton(value ? "X" : string.Empty);
            checkbox.Width.Set(42f, 0f);
            checkbox.Height.Set(42f, 0f);
            checkbox.VAlign = 0.5f;
            checkbox.HAlign = 1f;
            checkbox.TextScale = 0.95f;
            checkbox.BackgroundColor = value ? new Color(96, 148, 106) * 0.95f : new Color(19, 26, 44) * 0.95f;
            checkbox.BorderColor = value ? new Color(170, 221, 176) : new Color(90, 108, 164);
            checkbox.OnLeftClick += (_, __) =>
            {
                Service.SetConfigValue(_mod, field, !value);
                SetStatus($"{field.Label ?? field.Key} updated.");
                RebuildList();
            };
            panel.Append(checkbox);

            return panel;
        }

        private UIElement CreateNumericConfigRow(ConfigField field)
        {
            var panel = CreateRowContainer(78f);
            panel.Append(CreateRowTitle(field.Label ?? field.Key));
            panel.Append(CreateRowSubtitle(GetNumericDescription(field)));

            var minusButton = CreateControlButton("-");
            minusButton.Width.Set(34f, 0f);
            minusButton.Height.Set(38f, 0f);
            minusButton.Left.Set(-206f, 1f);
            minusButton.Top.Set(20f, 0f);
            minusButton.OnLeftClick += (_, __) =>
            {
                ApplyNumericDelta(field, -1);
                SetStatus($"{field.Label ?? field.Key} updated.");
                RebuildList();
            };
            panel.Append(minusButton);

            var valueButton = CreateControlButton(GetDisplayValue(field));
            valueButton.Width.Set(128f, 0f);
            valueButton.Height.Set(38f, 0f);
            valueButton.Left.Set(-166f, 1f);
            valueButton.Top.Set(20f, 0f);
            valueButton.TextScale = 0.62f;
            valueButton.BackgroundColor = IsEditingField(field.Key)
                ? new Color(101, 77, 32) * 0.95f
                : new Color(19, 26, 44) * 0.95f;
            valueButton.OnLeftClick += (_, __) =>
            {
                BeginEditing(field.Key, GetStoredFieldValue(field), isNumber: true);
                RebuildList();
            };
            panel.Append(valueButton);

            var plusButton = CreateControlButton("+");
            plusButton.Width.Set(34f, 0f);
            plusButton.Height.Set(38f, 0f);
            plusButton.Left.Set(-32f, 1f);
            plusButton.Top.Set(20f, 0f);
            plusButton.OnLeftClick += (_, __) =>
            {
                ApplyNumericDelta(field, 1);
                SetStatus($"{field.Label ?? field.Key} updated.");
                RebuildList();
            };
            panel.Append(plusButton);

            return panel;
        }

        private UIElement CreateEnumConfigRow(ConfigField field)
        {
            var panel = CreateRowContainer(78f);
            panel.Append(CreateRowTitle(field.Label ?? field.Key));
            panel.Append(CreateRowSubtitle(GetFieldDescription(field)));

            var previousButton = CreateControlButton("<");
            previousButton.Width.Set(34f, 0f);
            previousButton.Height.Set(38f, 0f);
            previousButton.Left.Set(-206f, 1f);
            previousButton.Top.Set(20f, 0f);
            previousButton.OnLeftClick += (_, __) =>
            {
                CycleEnum(field, -1);
                RebuildList();
            };
            panel.Append(previousButton);

            var valueButton = CreateControlButton(GetDisplayValue(field));
            valueButton.Width.Set(128f, 0f);
            valueButton.Height.Set(38f, 0f);
            valueButton.Left.Set(-166f, 1f);
            valueButton.Top.Set(20f, 0f);
            valueButton.TextScale = 0.62f;
            valueButton.OnLeftClick += (_, __) =>
            {
                CycleEnum(field, 1);
                RebuildList();
            };
            panel.Append(valueButton);

            var nextButton = CreateControlButton(">");
            nextButton.Width.Set(34f, 0f);
            nextButton.Height.Set(38f, 0f);
            nextButton.Left.Set(-32f, 1f);
            nextButton.Top.Set(20f, 0f);
            nextButton.OnLeftClick += (_, __) =>
            {
                CycleEnum(field, 1);
                RebuildList();
            };
            panel.Append(nextButton);

            return panel;
        }

        private UIElement CreateTextConfigRow(ConfigField field)
        {
            var panel = CreateRowContainer(78f);
            panel.Append(CreateRowTitle(field.Label ?? field.Key));
            panel.Append(CreateRowSubtitle(GetFieldDescription(field)));

            var valueButton = CreateControlButton(GetDisplayValue(field));
            valueButton.Width.Set(168f, 0f);
            valueButton.Height.Set(38f, 0f);
            valueButton.Left.Set(-172f, 1f);
            valueButton.Top.Set(20f, 0f);
            valueButton.TextScale = 0.62f;
            valueButton.BackgroundColor = IsEditingField(field.Key)
                ? new Color(101, 77, 32) * 0.95f
                : new Color(19, 26, 44) * 0.95f;
            valueButton.OnLeftClick += (_, __) =>
            {
                BeginEditing(field.Key, GetStoredFieldValue(field), isNumber: false);
                RebuildList();
            };
            panel.Append(valueButton);

            return panel;
        }

        private UIElement CreateKeybindRow(Keybind keybind)
        {
            var panel = CreateRowContainer(78f);
            panel.Append(CreateRowTitle(keybind.Label));
            panel.Append(CreateRowSubtitle("Click to capture a new binding. Backspace clears, right click resets."));

            string keybindLabel = _capturingKeybind && _capturingKeybindId == keybind.Id
                ? "Press a key..."
                : keybind.CurrentKey == null || keybind.CurrentKey.Key == KeyCode.None ? "Unbound" : keybind.CurrentKey.ToString();
            var valueButton = CreateControlButton(keybindLabel);
            valueButton.Width.Set(168f, 0f);
            valueButton.Height.Set(38f, 0f);
            valueButton.Left.Set(-172f, 1f);
            valueButton.Top.Set(20f, 0f);
            valueButton.TextScale = 0.62f;
            valueButton.BackgroundColor = _capturingKeybind && _capturingKeybindId == keybind.Id
                ? new Color(101, 77, 32) * 0.95f
                : new Color(19, 26, 44) * 0.95f;
            valueButton.OnLeftClick += (_, __) =>
            {
                _capturingKeybind = true;
                _capturingKeybindId = keybind.Id;
                SetStatus($"Press a key for {keybind.Label}. Escape cancels, Backspace clears.");
                RebuildList();
            };
            valueButton.OnRightClick += (_, __) =>
            {
                Service.ResetKeybind(keybind);
                SetStatus($"{keybind.Label} reset.");
                RebuildList();
            };
            panel.Append(valueButton);

            return panel;
        }

        private UIPanel CreateRowContainer(float height)
        {
            var panel = new UIPanel();
            panel.Width.Set(0f, 1f);
            panel.Height.Set(height, 0f);
            panel.BackgroundColor = new Color(29, 38, 66) * 0.96f;
            panel.BorderColor = new Color(68, 86, 140);
            return panel;
        }

        private UIText CreateRowTitle(string text)
        {
            var title = new UIText(text, 0.78f, large: false);
            title.Left.Set(14f, 0f);
            title.Top.Set(12f, 0f);
            return title;
        }

        private UIText CreateRowSubtitle(string text)
        {
            var subtitle = new UIText(text, 0.56f, large: false);
            subtitle.Left.Set(14f, 0f);
            subtitle.Top.Set(40f, 0f);
            subtitle.TextColor = new Color(184, 196, 225);
            return subtitle;
        }

        private UITextPanel<string> CreateControlButton(string text)
        {
            var button = new UITextPanel<string>(text, 0.68f, large: false);
            button.SetPadding(8f);
            button.BackgroundColor = new Color(54, 72, 122) * 0.95f;
            button.BorderColor = new Color(126, 147, 208);
            button.OnMouseOver += (_, __) =>
            {
                button.BackgroundColor = new Color(73, 94, 171);
                button.BorderColor = Color.White;
            };
            button.OnMouseOut += (_, __) =>
            {
                button.BackgroundColor = new Color(54, 72, 122) * 0.95f;
                button.BorderColor = new Color(126, 147, 208);
            };
            return button;
        }

        private bool IsEditingField(string fieldKey) => string.Equals(_editingFieldKey, fieldKey, StringComparison.Ordinal);

        private string GetDisplayValue(ConfigField field)
        {
            if (IsEditingField(field.Key))
                return string.IsNullOrEmpty(_editingBuffer) ? "_" : _editingBuffer + "_";

            return FormatConfigValue(field);
        }

        private string GetStoredFieldValue(ConfigField field)
        {
            switch (field.Type)
            {
                case ConfigFieldType.Bool:
                    return _mod.Context.Config.Get<bool>(field.Key).ToString();
                case ConfigFieldType.Int:
                    return _mod.Context.Config.Get<int>(field.Key).ToString(CultureInfo.InvariantCulture);
                case ConfigFieldType.Float:
                    return _mod.Context.Config.Get<float>(field.Key).ToString("0.###", CultureInfo.InvariantCulture);
                default:
                    return _mod.Context.Config.Get<string>(field.Key, string.Empty);
            }
        }

        private string FormatConfigValue(ConfigField field)
        {
            try
            {
                switch (field.Type)
                {
                    case ConfigFieldType.Bool:
                        return _mod.Context.Config.Get<bool>(field.Key) ? "Checked" : "Unchecked";
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
                return $"error: {ex.Message}";
            }
        }

        private string GetFieldDescription(ConfigField field)
        {
            if (!string.IsNullOrWhiteSpace(field.Description))
                return field.Description;

            if (field.Type == ConfigFieldType.Enum && field.Options != null && field.Options.Count > 0)
                return $"Options: {string.Join(", ", field.Options)}";

            return "No additional description provided.";
        }

        private string GetNumericDescription(ConfigField field)
        {
            string description = GetFieldDescription(field);
            string stepText = field.Type == ConfigFieldType.Float
                ? GetFloatStep(field).ToString("0.###", CultureInfo.InvariantCulture)
                : GetIntStep(field).ToString(CultureInfo.InvariantCulture);

            if (field.Min.HasValue || field.Max.HasValue)
                return $"{description} Range: {FormatLimit(field.Min)} to {FormatLimit(field.Max)}. Step: {stepText}.";

            return $"{description} Step: {stepText}.";
        }

        private static string FormatLimit(double? value)
        {
            if (!value.HasValue)
                return "unbounded";

            return value.Value.ToString("0.###", CultureInfo.InvariantCulture);
        }

        private void ApplyNumericDelta(ConfigField field, int direction)
        {
            if (field.Type == ConfigFieldType.Int)
            {
                int current = _mod.Context.Config.Get<int>(field.Key);
                Service.SetConfigValue(_mod, field, current + (GetIntStep(field) * direction));
                return;
            }

            float currentFloat = _mod.Context.Config.Get<float>(field.Key);
            Service.SetConfigValue(_mod, field, currentFloat + (GetFloatStep(field) * direction));
        }

        private static int GetIntStep(ConfigField field)
        {
            if (field.Step.HasValue)
                return Math.Max(1, (int)Math.Round(field.Step.Value));

            return 1;
        }

        private static float GetFloatStep(ConfigField field)
        {
            return field.Step.HasValue ? (float)field.Step.Value : 0.1f;
        }

        private void CycleEnum(ConfigField field, int direction)
        {
            if (field.Options == null || field.Options.Count == 0)
                return;

            string current = _mod.Context.Config.Get<string>(field.Key, field.Options[0]);
            int index = field.Options.IndexOf(current);
            if (index < 0)
                index = 0;

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
            SetStatus($"Editing {fieldKey}. Enter saves, Escape cancels.");
        }

        private void UpdateTextEditing()
        {
            PlayerInput.WritingText = true;
            Main.instance.HandleIME();

            string newBuffer = Main.GetInputText(_editingBuffer ?? string.Empty);
            if (!string.Equals(newBuffer, _editingBuffer, StringComparison.Ordinal))
            {
                _editingBuffer = newBuffer;
                RebuildList();
            }

            if (InputState.IsKeyJustPressed(KeyCode.Escape))
            {
                CancelEditing(rebuild: true);
                SetStatus("Edit cancelled.");
                return;
            }

            if (!Main.inputTextEnter)
                return;

            var field = _mod.Context.Config.Schema[_editingFieldKey];
            try
            {
                if (_editingIsNumber)
                {
                    if (field.Type == ConfigFieldType.Int)
                    {
                        if (!int.TryParse(_editingBuffer, NumberStyles.Integer, CultureInfo.InvariantCulture, out int intValue))
                            throw new InvalidOperationException("Invalid integer value.");

                        Service.SetConfigValue(_mod, field, intValue);
                    }
                    else
                    {
                        if (!float.TryParse(_editingBuffer, NumberStyles.Float, CultureInfo.InvariantCulture, out float floatValue))
                            throw new InvalidOperationException("Invalid decimal value.");

                        Service.SetConfigValue(_mod, field, floatValue);
                    }
                }
                else
                {
                    Service.SetConfigValue(_mod, field, _editingBuffer ?? string.Empty);
                }

                SetStatus($"{field.Label ?? field.Key} saved.");
                CancelEditing(rebuild: true);
            }
            catch (Exception ex)
            {
                SetStatus($"Save failed: {ex.Message}");
                Main.clrInput();
            }
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
                RebuildList();
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
            var keys = new List<int>();
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

        private sealed class ModIconElement : UIElement
        {
            private readonly object _textureSource;

            public ModIconElement(object textureSource)
            {
                _textureSource = textureSource;
            }

            protected override void DrawSelf(SpriteBatch spriteBatch)
            {
                base.DrawSelf(spriteBatch);

                Texture2D texture = ResolveTexture(_textureSource);
                if (texture == null)
                    return;

                CalculatedStyle dimensions = GetInnerDimensions();
                float scale = Math.Min(dimensions.Width / texture.Width, dimensions.Height / texture.Height);
                int drawWidth = Math.Max(1, (int)(texture.Width * scale));
                int drawHeight = Math.Max(1, (int)(texture.Height * scale));
                var destination = new Rectangle(
                    (int)(dimensions.X + ((dimensions.Width - drawWidth) * 0.5f)),
                    (int)(dimensions.Y + ((dimensions.Height - drawHeight) * 0.5f)),
                    drawWidth,
                    drawHeight);

                spriteBatch.Draw(texture, destination, Color.White);
            }

            private static Texture2D ResolveTexture(object textureSource)
            {
                if (textureSource is Texture2D texture)
                    return texture;

                var sourceType = textureSource?.GetType();
                var valueProperty = sourceType?.GetProperty("Value");
                return valueProperty?.GetValue(textureSource, null) as Texture2D;
            }
        }
    }
}
