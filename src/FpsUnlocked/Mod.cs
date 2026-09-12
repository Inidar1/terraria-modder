using System;
using HarmonyLib;
using System.Collections.Generic;
using Terraria;
using TerrariaModder.Core;
using TerrariaModder.Core.Debug;
using TerrariaModder.Core.Logging;

namespace FpsUnlocked
{
    public class Mod : IMod, IModLifecycle, IModStateProvider, IModActionProvider
    {
        public string Id => "fps-unlocked";
        public string Name => "FPS Unlocked";
        public string Version => "2.0.0";

        private static ILogger _log;
        private static ModContext _context;
        private Harmony _harmony;
        private bool _patchesApplied;
        private FpsUnlockedConfig _config;

        private static bool _initFailed;
        private static bool _initFailureShown;

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
            _config = context.GetConfig<FpsUnlockedConfig>();
            LoadConfig();

            if (Environment.GetEnvironmentVariable("TERRARIA_MODDER_DEDSERV") == "1")
            {
                _log.Info("FpsUnlocked: dedicated server — skipping client init");
                return;
            }

            context.RegisterStateProvider(this);
            context.RegisterActionProvider(this);
            _harmony = new Harmony("com.terrariamodder.fpsunlocked");

            _log.Info($"FPS Unlocked v2 initializing - Mode: {Mode}, MaxFPS: {MaxFps}, " +
                $"Interpolation: {InterpolationEnabled}");
        }

        private void LoadConfig()
        {
            if (_config == null) return;
            Enabled = _config.Enabled;
            Mode = _config.Mode;
            MaxFps = _config.MaxFps;
            InterpolationEnabled = _config.Interpolation;
            MouseEveryFrame = _config.MouseEveryFrame;
        }

        public void OnConfigChanged()
        {
            LoadConfig();
            _log.Info($"Config reloaded - Enabled: {Enabled}, Mode: {Mode}, MaxFPS: {MaxFps}, " +
                $"Interpolation: {InterpolationEnabled}");
        }

        private void ApplyPatches()
        {
            if (_patchesApplied || _harmony == null) return;

            try
            {
                // Initialize reflection cache (finds all types and builds IL accessors)
                if (!ReflectionCache.Initialize(_log))
                {
                    _log.Error("ReflectionCache initialization failed - mod disabled");
                    _initFailed = true;
                    return;
                }

                // Allocate keyframe storage arrays
                KeyframeStore.Allocate();
                _log.Info("KeyframeStore allocated");

                // Initialize interpolator save/restore arrays
                Interpolator.Initialize(_log);
                _log.Info("Interpolator initialized");

                // Apply all 8 Harmony patches
                Patches.ApplyAll(_harmony, _log);
                _patchesApplied = true;

                _log.Info("FPS Unlocked v2 fully initialized");
            }
            catch (Exception ex)
            {
                _log.Error($"Patch error: {ex}");
                _initFailed = true;
            }
        }

        public Dictionary<string, object> GetModState()
        {
            return new Dictionary<string, object>
            {
                { "enabled", Enabled },
                { "mode", Mode },
                { "maxFps", MaxFps },
                { "interpolationEnabled", InterpolationEnabled },
                { "mouseEveryFrame", MouseEveryFrame },
                { "timingActive", FrameState.TimingActive },
                { "interpolationActive", FrameState.Active },
                { "isPartialTick", FrameState.IsPartialTick },
                { "partialTick", FrameState.PartialTick },
                { "frameCount", FrameState.FrameCount },
                { "tickCount", FrameState.TickCount },
                { "gameFocused", Main.instance != null && Main.instance.IsActive },
                { "presentationInterval", Patches.GetPresentationInterval() },
                { "vsyncWritesPatched", Patches.VSyncWritesPatched }
            };
        }

        public List<ModActionInfo> GetActions()
        {
            return new List<ModActionInfo>
            {
                new ModActionInfo("set_mode", "Set FPS mode",
                    new ModActionParam("mode", "string", true, "VSync (Vanilla), Uncapped, or Capped")),
            };
        }

        public ModActionResult ExecuteAction(string name, Dictionary<string, string> args)
        {
            switch (name)
            {
                case "set_mode":
                    string mode = args != null && args.ContainsKey("mode") ? args["mode"] : null;
                    if (string.IsNullOrEmpty(mode)) return ModActionResult.Fail("Missing 'mode' param");
                    if (mode != "VSync (Vanilla)" && mode != "Capped" && mode != "Uncapped")
                        return ModActionResult.Fail($"Invalid mode '{mode}'. Must be one of: VSync (Vanilla), Capped, Uncapped");
                    Mode = mode;
                    if (_config != null) { _config.Mode = mode; _config.Save(); }
                    EventLog.Emit("fps-unlocked", "set_mode", $"{{\"mode\":\"{Mode}\"}}");
                    return ModActionResult.Ok($"FPS mode set to {Mode}");
                default:
                    return null;
            }
        }

        public void OnContentReady(ModContext context)
        {
            ApplyPatches();
        }

        public void OnWorldLoad()
        {
            // Notify user once if initialization failed
            if (_initFailed && !_initFailureShown)
            {
                _initFailureShown = true;
                try { Main.NewText("[FpsUnlocked] Failed to initialize — check logs.", 255, 80, 80); }
                catch { /* Main.NewText may not be available */ }
            }

            // Clear keyframe arrays to prevent stale data from previous world
            KeyframeStore.Clear();
            FrameState.Reset();
            Patches.ResetTransitionState();
            _log.Info("World loaded - keyframes cleared");
        }

        public void OnWorldUnload()
        {
            KeyframeStore.Clear();
            FrameState.Reset();
            Patches.ResetTransitionState();
            _log.Info("World unloaded - keyframes cleared");
        }

        public void Unload()
        {
            FrameState.Reset();
            Patches.ResetTransitionState();
            _harmony?.UnpatchAll("com.terrariamodder.fpsunlocked");
            _patchesApplied = false;
            _log?.Info("FPS Unlocked v2 unloaded");
        }
    }
}
