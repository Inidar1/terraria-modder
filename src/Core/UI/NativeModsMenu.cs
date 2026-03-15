using Terraria;
using Terraria.Audio;
using Terraria.UI;
using TerrariaModder.Core.Logging;

namespace TerrariaModder.Core.UI
{
    internal static class NativeModsMenu
    {
        private static ILogger _log;
        private static NativeModsService _service;
        private static bool _initialized;

        public static void Initialize(ILogger log)
        {
            if (_initialized || Reflection.Game.IsServer) return;

            _log = log;
            _service = new NativeModsService(log);
            NativeModsMenuPatches.Initialize(log);
            _initialized = true;
        }

        public static void ApplyPatches()
        {
            if (_initialized)
                NativeModsMenuPatches.Apply();
        }

        public static NativeModsService Service => _service;

        public static void OpenFromTitle()
        {
            Main.menuMode = 888;
            Main.MenuUI.SetState(new ModsHubState(_service, previousState: null, inGame: false));
            SoundEngine.PlaySound(10);
        }

        public static void OpenIngame()
        {
            IngameFancyUI.OpenUIState(new ModsHubState(_service, Main.InGameUI.CurrentState, inGame: true));
            SoundEngine.PlaySound(10);
        }
    }
}
