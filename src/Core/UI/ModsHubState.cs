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

        protected override string GetTitle() => "Mods";

        protected override void RebuildList()
        {
            List.Clear();
            List.Add(CreateInfoRow($"TerrariaModder v{PluginLoader.FrameworkVersion}"));
            List.Add(CreateButton("Load Order", () => OpenState(new LoadOrderState(Service, this, InGame))));
            List.Add(CreateButton("Conflicts", () => OpenState(new ConflictsState(Service, this, InGame))));
            List.Add(CreateButton("Logs", () => OpenState(new LogsState(Service, this, InGame))));

            foreach (var mod in Service.GetMods())
            {
                string status = $"{mod.Manifest.Name} [{mod.State}]";
                if (Service.ModNeedsRestart(mod))
                    status += " [Restart Required]";

                List.Add(CreateButton(status, () => OpenState(new ModDetailState(Service, this, InGame, mod))));
            }
        }

        private void OpenState(UIState state)
        {
            SoundEngine.PlaySound(10);
            if (InGame)
                Terraria.Main.InGameUI.SetState(state);
            else
                Terraria.Main.MenuUI.SetState(state);
        }
    }
}
