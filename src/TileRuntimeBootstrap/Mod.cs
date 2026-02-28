using TerrariaModder.Core;
using TerrariaModder.Core.Events;
using TerrariaModder.Core.Logging;
using TerrariaModder.TileRuntime;

namespace TileRuntimeBootstrap
{
    /// <summary>
    /// Bootstrap mod that owns the shared tile runtime lifecycle hooks.
    /// </summary>
    public class Mod : IMod
    {
        private static ILogger _log;
        private static bool _subscribed;

        public string Id => "tile-runtime";
        public string Name => "Tile Runtime";
        public string Version => "0.1.0";

        public void Initialize(ModContext context)
        {
            _log = context.Logger;
            TileRuntimeApi.Initialize(_log);
        }

        public void OnWorldLoad()
        {
        }

        public void OnWorldUnload()
        {
        }

        public void Unload()
        {
            if (_subscribed)
            {
                FrameEvents.OnPostUpdate -= HandlePostUpdate;
                _subscribed = false;
            }
        }

        public static void OnGameReady()
        {
            if (!_subscribed)
            {
                FrameEvents.OnPostUpdate += HandlePostUpdate;
                _subscribed = true;
            }

            TileRuntimeApi.OnGameReady();
        }

        public static void OnContentLoaded()
        {
            TileRuntimeApi.OnContentLoaded();
        }

        public static void OnFirstUpdate()
        {
            TileRuntimeApi.OnUpdate();
        }

        private static void HandlePostUpdate()
        {
            try
            {
                TileRuntimeApi.OnUpdate();
            }
            catch (System.Exception ex)
            {
                _log?.Error($"[TileRuntimeBootstrap] PostUpdate failed: {ex.Message}");
            }
        }
    }
}
