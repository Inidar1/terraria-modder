using Microsoft.Xna.Framework;
using Terraria;
using Terraria.Audio;
using Terraria.GameContent.UI.Elements;
using Terraria.UI;

namespace TerrariaModder.Core.UI
{
    internal sealed class ModsHubState : NativeModsStateBase
    {
        public ModsHubState(NativeModsService service, UIState previousState, bool inGame)
            : base(service, previousState, inGame)
        {
        }

        public override void OnInitialize()
        {
            base.OnInitialize();
            List.ManualSortMethod = PreserveInsertionOrder;
            RebuildList();
        }

        protected override string GetTitle() => "Mods";

        protected override void RebuildList()
        {
            List.Clear();
            List.Add(CreateInfoRow($"TerrariaModder v{PluginLoader.FrameworkVersion}"));

            List.Add(CreateSectionRow("Utilities"));
            List.Add(CreateUtilityRow("Load Order", "Adjust startup order and dependency placement.", () => OpenState(new LoadOrderState(Service, this, InGame))));
            List.Add(CreateUtilityRow("Conflicts", "Review patch collisions and keybind conflicts.", () => OpenState(new ConflictsState(Service, this, InGame))));
            List.Add(CreateUtilityRow("Logs", "Inspect recent framework and mod log entries.", () => OpenState(new LogsState(Service, this, InGame))));
            if (!InGame)
            {
                List.Add(CreateUtilityRow("Browse Nexus", "Browse, install, update, and delete Nexus mods.", () => OpenState(new NexusBrowseState(Service, this, inGame: false))));
                List.Add(CreateUtilityRow("Nexus Settings", "Manage Nexus API key, browser login, and auth status.", () => OpenState(new NexusSettingsState(Service, this, inGame: false))));
            }
            else
            {
                List.Add(CreateInfoRow("Nexus browsing and installs are only available from the title screen.", 44f));
            }

            List.Add(CreateSectionRow("Mods"));
            foreach (var mod in Service.GetMods())
                List.Add(CreateModRow(mod));
        }

        private UIPanel CreateSectionRow(string title)
        {
            var panel = new UIPanel();
            panel.Width.Set(0f, 1f);
            panel.Height.Set(48f, 0f);
            panel.BackgroundColor = new Color(45, 61, 108) * 0.95f;
            panel.BorderColor = new Color(100, 123, 184);

            var text = new UIText(title, 0.72f, large: true);
            text.HAlign = 0.5f;
            text.VAlign = 0.5f;
            text.Top.Set(-4f, 0f);
            panel.Append(text);
            return panel;
        }

        private UIPanel CreateUtilityRow(string title, string subtitle, System.Action onClick)
        {
            var panel = CreateInteractivePanel(68f, onClick);

            var heading = new UIText(title, 0.74f, large: false);
            heading.Left.Set(16f, 0f);
            heading.Top.Set(10f, 0f);
            panel.Append(heading);

            var detail = new UIText(subtitle, 0.56f, large: false);
            detail.Left.Set(16f, 0f);
            detail.Top.Set(38f, 0f);
            detail.TextColor = new Color(186, 197, 228);
            panel.Append(detail);

            var button = new UITextPanel<string>("Open", 0.65f, large: false);
            button.Width.Set(94f, 0f);
            button.Height.Set(38f, 0f);
            button.Left.Set(-108f, 1f);
            button.VAlign = 0.5f;
            button.BackgroundColor = new Color(52, 71, 121) * 0.95f;
            button.BorderColor = new Color(126, 147, 208);
            panel.Append(button);

            return panel;
        }

        private UIPanel CreateModRow(ModInfo mod)
        {
            var panel = CreateInteractivePanel(78f, () => OpenState(new ModDetailState(Service, this, InGame, mod)));

            var name = new UIText(mod.Manifest.Name ?? mod.Manifest.Id, 0.76f, large: false);
            name.Left.Set(16f, 0f);
            name.Top.Set(11f, 0f);
            panel.Append(name);

            string subtitleText = mod.Manifest.Id;
            if (Service.ModNeedsRestart(mod))
                subtitleText += "  •  restart required";
            else if (!string.IsNullOrWhiteSpace(mod.ErrorMessage))
                subtitleText += $"  •  {mod.ErrorMessage}";

            var subtitle = new UIText(subtitleText, 0.56f, large: false);
            subtitle.Left.Set(16f, 0f);
            subtitle.Top.Set(42f, 0f);
            subtitle.TextColor = new Color(186, 197, 228);
            panel.Append(subtitle);

            Color statusColor = GetStatusColor(mod.State);
            string statusText = GetStatusText(mod.State);

            var statusLabel = new UIText(statusText, 0.62f, large: false);
            statusLabel.Left.Set(-146f, 1f);
            statusLabel.Top.Set(18f, 0f);
            statusLabel.Width.Set(108f, 0f);
            statusLabel.TextOriginX = 1f;
            statusLabel.TextColor = statusColor;
            panel.Append(statusLabel);

            var indicator = new UIPanel();
            indicator.Width.Set(18f, 0f);
            indicator.Height.Set(18f, 0f);
            indicator.Left.Set(-30f, 1f);
            indicator.Top.Set(18f, 0f);
            indicator.BackgroundColor = statusColor;
            indicator.BorderColor = statusColor * 1.1f;
            panel.Append(indicator);

            return panel;
        }

        private UIPanel CreateInteractivePanel(float height, System.Action onClick)
        {
            var panel = new UIPanel();
            panel.Width.Set(0f, 1f);
            panel.Height.Set(height, 0f);
            panel.BackgroundColor = new Color(63, 82, 151) * 0.7f;
            panel.BorderColor = Color.Black;
            panel.OnMouseOver += FadedMouseOver;
            panel.OnMouseOut += FadedMouseOut;
            panel.OnLeftClick += (_, __) => onClick();
            return panel;
        }

        private static Color GetStatusColor(ModState state)
        {
            switch (state)
            {
                case ModState.Loaded:
                    return new Color(96, 208, 124);
                case ModState.Disabled:
                    return new Color(215, 88, 88);
                case ModState.Errored:
                    return new Color(238, 123, 71);
                case ModState.DependencyError:
                    return new Color(242, 188, 84);
                default:
                    return new Color(186, 197, 228);
            }
        }

        private static string GetStatusText(ModState state)
        {
            switch (state)
            {
                case ModState.Loaded:
                    return "Loaded";
                case ModState.Disabled:
                    return "Disabled";
                case ModState.Errored:
                    return "Errored";
                case ModState.DependencyError:
                    return "Missing deps";
                case ModState.Loading:
                    return "Loading";
                default:
                    return "Discovered";
            }
        }
        private static void PreserveInsertionOrder(System.Collections.Generic.List<UIElement> items)
        {
            _ = items;
        }
    }
}
