using System;
using System.Threading;
using HarmonyLib;
using TerrariaModder.Core;
using TerrariaModder.Core.Logging;

namespace FpsUnlocked
{
    public class Mod : IMod
    {
        public string Id => "fps-unlocked";
        public string Name => "FPS Unlocked";
        public string Version => "1.0.0";

        private static ILogger _log;
        private static ModContext _context;
        private Harmony _harmony;
        private Timer _patchTimer;

        // Config (accessed by Patches)
        internal static bool Enabled = true;
        internal static string Mode = "VSync (Vanilla)";
        internal static int MaxFps = 144;
        internal static bool InterpolationEnabled = true;
        internal static bool MouseEveryFrame = true;

        public void Initialize(ModContext context)
        {
            _context = context;
            _log = context.Logger;
            LoadConfig();

            _harmony = new Harmony("com.terrariamodder.fpsunlocked");

            // Delay patching to ensure Terraria types are loaded
            _patchTimer = new Timer(ApplyPatches, null, 5000, Timeout.Infinite);
            _log.Info($"FPS Unlocked v2 initializing - Mode: {Mode}, MaxFPS: {MaxFps}, " +
                $"Interpolation: {InterpolationEnabled}");
        }

        private void LoadConfig()
        {
            Enabled = _context.Config.Get("enabled", true);
            Mode = _context.Config.Get("mode", "VSync (Vanilla)");
            MaxFps = _context.Config.Get("maxFps", 144);
            InterpolationEnabled = _context.Config.Get("interpolation", true);
            MouseEveryFrame = _context.Config.Get("mouseEveryFrame", true);
        }

        public void OnConfigChanged()
        {
            LoadConfig();
            _log.Info($"Config reloaded - Enabled: {Enabled}, Mode: {Mode}, MaxFPS: {MaxFps}, " +
                $"Interpolation: {InterpolationEnabled}");
        }

        private void ApplyPatches(object state)
        {
            try
            {
                // Initialize reflection cache (finds all types and builds IL accessors)
                if (!ReflectionCache.Initialize(_log))
                {
                    _log.Error("ReflectionCache initialization failed - mod disabled");
                    return;
                }

                // Allocate keyframe storage arrays
                KeyframeStore.Allocate();
                _log.Info("KeyframeStore allocated");

                // Initialize interpolator save/restore arrays
                Interpolator.Initialize(_log);
                _log.Info("Interpolator initialized");

                // Apply all 7 Harmony patches
                Patches.ApplyAll(_harmony, _log);

                _log.Info("FPS Unlocked v2 fully initialized");
            }
            catch (Exception ex)
            {
                _log.Error($"Patch error: {ex}");
            }
        }

        public void OnWorldLoad()
        {
            // Clear keyframe arrays to prevent stale data from previous world
            KeyframeStore.Clear();
            FrameState.Reset();
            _log.Info("World loaded - keyframes cleared");
        }

        public void OnWorldUnload()
        {
            KeyframeStore.Clear();
            FrameState.Reset();
            _log.Info("World unloaded - keyframes cleared");
        }

        public void Unload()
        {
            FrameState.Reset();
            _patchTimer?.Dispose();
            _harmony?.UnpatchAll("com.terrariamodder.fpsunlocked");
            _patchTimer = null;
            _log?.Info("FPS Unlocked v2 unloaded");
        }
    }
}
