using System;
using HarmonyLib;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
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
        public string Version => "2.0.1";

        private static ILogger _log;
        private static ModContext _context;
        private Harmony _harmony;
        private bool _patchesApplied;
        private bool _contentReady;
        private bool _resourcesReady;
        private const string HarmonyId = "com.terrariamodder.fpsunlocked";
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
            _harmony = new Harmony(HarmonyId);
            Main.OnPostDraw += SynchronizePatches;

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
            if (_patchesApplied || _harmony == null || _initFailed) return;

            try
            {
                if (!_resourcesReady)
                {
                    if (!ReflectionCache.Initialize(_log))
                    {
                        _log.Error("ReflectionCache initialization failed - mod disabled");
                        _initFailed = true;
                        return;
                    }

                    KeyframeStore.Allocate();
                    Interpolator.Initialize(_log);
                    _resourcesReady = true;
                }

                FrameState.Reset();
                Patches.ResetTransitionState();
                Patches.ApplyAll(_harmony, _log);
                _patchesApplied = true;

                _log.Info("FPS Unlocked v2 fully initialized");
            }
            catch (Exception ex)
            {
                _harmony.UnpatchAll(HarmonyId);
                _log.Error($"Patch error: {ex}");
                _initFailed = true;
            }
        }

        private void SynchronizePatches(GameTime gameTime)
        {
            if (!_contentReady || _harmony == null) return;
            try
            {
                if (Enabled && Mode != "VSync (Vanilla)" && !Main.gameMenu)
                {
                    ApplyPatches();
                    return;
                }

                // Main.OnPostDraw runs after DoDraw's interpolation finalizer. Keep the
                // recovery hooks until the update restored VSync and Draw rebuilt targets.
                if (!_patchesApplied || !Patches.CanSuspend) return;
                _harmony.UnpatchAll(HarmonyId);
                _patchesApplied = false;
                FrameState.Reset();
                Patches.ResetTransitionState();
                _log.Info("FPS feature patches removed; vanilla frame pacing active");
            }
            catch (Exception ex)
            {
                _contentReady = false;
                _initFailed = true;
                _log.Error($"FPS patch transition failed: {ex}");
            }
        }

        public Dictionary<string, object> GetModState()
        {
            return new Dictionary<string, object>
            {
                { "enabled", Enabled },
                { "patchesApplied", _patchesApplied },
                { "resourcesReady", _resourcesReady },
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
                { "vsyncWritesPatched", _patchesApplied && Patches.VSyncWritesPatched }
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
            _contentReady = true;
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
            Main.OnPostDraw -= SynchronizePatches;
            _contentReady = false;
            FrameState.Reset();
            Patches.ResetTransitionState();
            _harmony?.UnpatchAll(HarmonyId);
            _patchesApplied = false;
            _log?.Info("FPS Unlocked v2 unloaded");
        }
    }
}
