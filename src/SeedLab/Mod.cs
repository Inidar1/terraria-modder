using System;
using System.IO;
using System.Threading;
using HarmonyLib;
using SeedLab.Patches;
using SeedLab.UI;
using TerrariaModder.Core;
using TerrariaModder.Core.Events;
using TerrariaModder.Core.Input;
using TerrariaModder.Core.Logging;
using Terraria;
using TerrariaModder.Core.UI;

namespace SeedLab
{
    /// <summary>
    /// Seed Lab mod - mix and match individual features from Terraria's secret seeds.
    /// Supports both in-world runtime overrides and pre-generation world-gen overrides.
    /// </summary>
    public class Mod : IMod
    {
        public string Id => "seed-lab";
        public string Name => "Seed Lab";
        public string Version => "1.0.0";

        private ILogger _log;
        private ModContext _context;
        private bool _enabled;

        private FeatureManager _featureManager;
        private PresetManager _presetManager;
        private WorldGenOverrideManager _worldGenOverrideManager;
        private InGamePanel _panel;
        private WorldGenPanel _worldGenPanel;

        private static Harmony _harmony;
        private static Timer _patchTimer;

        // Track F10 previous state for menu-context edge detection
        private bool _f10WasDown;

        public void Initialize(ModContext context)
        {
            _log = context.Logger;
            _context = context;

            _enabled = context.Config.Get<bool>("enabled");
            if (!_enabled)
            {
                _log.Info("[SeedLab] Disabled in config");
                return;
            }

            // Initialize managers
            string modDir = context.ModFolder;
            string configPath = Path.Combine(modDir, "state.json");
            string presetsPath = Path.Combine(modDir, "presets.json");
            string worldGenConfigPath = Path.Combine(modDir, "state-worldgen.json");

            _featureManager = new FeatureManager(_log, configPath);
            _presetManager = new PresetManager(_log, presetsPath);
            _worldGenOverrideManager = new WorldGenOverrideManager(_log, worldGenConfigPath);
            _panel = new InGamePanel(_log, _featureManager, _presetManager);
            _worldGenPanel = new WorldGenPanel(_log, _worldGenOverrideManager);

            // Register keybind (works in-world only via KeybindManager)
            context.RegisterKeybind("toggle", "Toggle Panel", "Open/close the Seed Lab panel", "F10", OnToggle);
            SeedLabCommands.Register(context, _featureManager, _presetManager, _log);

            // Subscribe to draw event (fires in both menu and world)
            FrameEvents.OnPreUpdate += OnUpdate;
            UIRenderer.RegisterPanelDraw("seed-lab", OnDraw);

            // Defer Harmony patching to avoid startup crashes
            _harmony = new Harmony("com.terrariamodder.seedlab");
            _patchTimer = new Timer(ApplyPatches, null, 5000, Timeout.Infinite);

            _log.Info("[SeedLab] Initialized - Press F10 to open panel (works in menus too)");
        }

        public void OnWorldLoad()
        {
            if (!_enabled || _featureManager == null) return;

            // Close world-gen panel when entering a world
            if (_worldGenPanel != null) _worldGenPanel.Visible = false;

            // Initialize feature states from the world's actual seed flags
            _featureManager.InitFromWorldFlags();
            _log.Info("[SeedLab] World loaded - features initialized from world seed flags");
        }

        public void OnWorldUnload()
        {
            if (!_enabled || _featureManager == null) return;

            _panel.Visible = false;
            _featureManager.SaveState();
            _featureManager.Reset();
        }

        public void Unload()
        {
            // Dispose timer first to prevent race condition with re-patching
            _patchTimer?.Dispose();
            _patchTimer = null;

            FrameEvents.OnPreUpdate -= OnUpdate;
            UIRenderer.UnregisterPanelDraw("seed-lab");
            _harmony?.UnpatchAll("com.terrariamodder.seedlab");
            if (_panel != null) _panel.Visible = false;
            if (_worldGenPanel != null) _worldGenPanel.Visible = false;
            _log.Info("[SeedLab] Unloaded");
        }

        /// <summary>
        /// Keybind callback (in-world only, via KeybindManager).
        /// </summary>
        private void OnToggle()
        {
            if (_featureManager == null || !_featureManager.Initialized)
            {
                _log.Warn("[SeedLab] Must be in a world to use Seed Lab");
                return;
            }

            _panel.Visible = !_panel.Visible;
        }

        private void OnUpdate()
        {
            _panel?.Update();
            _worldGenPanel?.Update();
        }

        private void OnDraw()
        {
            // In menu: poll keyboard manually (KeybindManager doesn't update in menus)
            // and handle F10 for world-gen panel
            if (Main.gameMenu)
            {
                PollMenuInput();
                _worldGenPanel?.Draw();
            }
            else
            {
                _panel?.Draw();
            }
        }

        /// <summary>
        /// Manual keyboard polling for menu context.
        /// KeybindManager skips input when Main.gameMenu=true, so we poll directly
        /// using InputState's reflection-based keyboard access.
        /// </summary>
        private void PollMenuInput()
        {
            // Update input state manually since KeybindManager won't do it in menus
            InputState.Update();

            bool f10Down = InputState.IsKeyDown(KeyCode.F10);
            bool f10JustPressed = f10Down && !_f10WasDown;
            _f10WasDown = f10Down;

            if (f10JustPressed)
            {
                _worldGenPanel.Visible = !_worldGenPanel.Visible;
            }
        }

        private void ApplyPatches(object state)
        {
            try
            {
                SeedFeaturePatches.Apply(_harmony, _featureManager, _log);
                WorldGenResetPatch.Apply(_harmony, _worldGenOverrideManager, _log);
                WorldGenPassPatch.Apply(_harmony, _worldGenOverrideManager, _log);
                FinalizeSecretSeedsPatch.Apply(_harmony, _worldGenOverrideManager, _log);
            }
            catch (Exception ex)
            {
                _log.Error($"[SeedLab] Harmony patch error: {ex.Message}");
            }
        }
    }
}
