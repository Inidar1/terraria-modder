using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using HarmonyLib;
using SeedLab.Gen;
using SeedLab.Patches;
using SeedLab.UI;
using TerrariaModder.Core;
using TerrariaModder.Core.Debug;
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
    public class Mod : IMod, IModLifecycle, IModStateProvider, IModActionProvider
    {
        public string Id => "seed-lab";
        public string Name => "Seed Lab";
        public string Version => "2.0.1";

        private ILogger _log;
        private ModContext _context;
        private bool _enabled;
        private SeedLabConfig _config;

        private FeatureManager _featureManager;
        private PresetManager _presetManager;
        private WorldGenOverrideManager _worldGenOverrideManager;
        private InGamePanel _panel;
        private WorldGenPanel _worldGenPanel;

        private static Harmony _harmony;



        public void Initialize(ModContext context)
        {
            _log = context.Logger;
            _context = context;
            _config = context.GetConfig<SeedLabConfig>();

            _enabled = _config != null ? _config.Enabled : true;
            if (!_enabled)
            {
                _log.Info("[SeedLab] Disabled in config");
                return;
            }

            if (Environment.GetEnvironmentVariable("TERRARIA_MODDER_DEDSERV") == "1")
            {
                _log.Info("[SeedLab] Dedicated server — skipping client init");
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
            UnderwaterSurfaceGen.Initialize(_log);
            GogGen.Initialize(_log);

            // Core owns input polling in both title-screen and world contexts.
            context.RegisterKeybind("toggle", "Toggle Panel", "Open/close the Seed Lab panel", "F10", OnToggle).AllowInMenu = true;
            SeedLabCommands.Register(context, _featureManager, _presetManager, _log);

            // Subscribe to draw event (fires in both menu and world)
            FrameEvents.OnPreUpdate += OnUpdate;
            UIRenderer.RegisterPanelDraw("seed-lab", OnDraw);

            // Create Harmony instance — patches applied in OnContentReady (after game init,
            // before any world loads) to avoid the race with MenuNavigator's fast enter_world.
            _harmony = new Harmony("com.terrariamodder.seedlab");

            context.RegisterStateProvider(this);
            context.RegisterActionProvider(this);
            _log.Info("[SeedLab] Initialized - Press F10 to open panel (works in menus too)");
        }

        public Dictionary<string, object> GetModState()
        {
            var state = new Dictionary<string, object>
            {
                { "enabled", _enabled },
                { "panelOpen", _panel?.Visible ?? false },
                { "worldGenPanelOpen", _worldGenPanel?.Visible ?? false }
            };
            if (_featureManager != null)
            {
                var allStates = _featureManager.GetAllStates();
                var activeList = new System.Collections.Generic.List<string>();
                foreach (var kv in allStates)
                    if (kv.Value) activeList.Add(kv.Key);
                state["activeFeatures"] = activeList;
                state["activeFeatureCount"] = activeList.Count;
            }
            return state;
        }

        public List<ModActionInfo> GetActions()
        {
            return new List<ModActionInfo>
            {
                new ModActionInfo("open_panel", "Open the Seed Lab panel"),
                new ModActionInfo("close_panel", "Close the Seed Lab panel"),
                new ModActionInfo("enable_feature", "Enable a seed feature",
                    new ModActionParam("name", "string", true, "Feature ID")),
                new ModActionInfo("disable_feature", "Disable a seed feature",
                    new ModActionParam("name", "string", true, "Feature ID")),
            };
        }

        public ModActionResult ExecuteAction(string name, Dictionary<string, string> args)
        {
            switch (name)
            {
                case "open_panel":
                    if (_panel != null) _panel.Visible = true;
                    EventLog.Emit("seed-lab", "open_panel", "{\"open\":true}");
                    return ModActionResult.Ok("Panel opened");
                case "close_panel":
                    if (_panel != null) _panel.Visible = false;
                    EventLog.Emit("seed-lab", "close_panel", "{\"open\":false}");
                    return ModActionResult.Ok("Panel closed");
                case "enable_feature":
                    if (_featureManager == null) return ModActionResult.Fail("Feature manager not initialized");
                    if (!_featureManager.Initialized) return ModActionResult.Fail("Not in a world");
                    string eName = args != null && args.ContainsKey("name") ? args["name"] : null;
                    if (string.IsNullOrEmpty(eName)) return ModActionResult.Fail("Missing 'name' param");
                    if (!_featureManager.FeaturesById.ContainsKey(eName)) return ModActionResult.Fail($"Unknown feature '{eName}'");
                    _featureManager.SetFeature(eName, true);
                    EventLog.Emit("seed-lab", "enable_feature", $"{{\"feature\":\"{eName}\",\"enabled\":true}}");
                    return ModActionResult.Ok($"Feature '{eName}' enabled");
                case "disable_feature":
                    if (_featureManager == null) return ModActionResult.Fail("Feature manager not initialized");
                    if (!_featureManager.Initialized) return ModActionResult.Fail("Not in a world");
                    string dName = args != null && args.ContainsKey("name") ? args["name"] : null;
                    if (string.IsNullOrEmpty(dName)) return ModActionResult.Fail("Missing 'name' param");
                    if (!_featureManager.FeaturesById.ContainsKey(dName)) return ModActionResult.Fail($"Unknown feature '{dName}'");
                    _featureManager.SetFeature(dName, false);
                    EventLog.Emit("seed-lab", "disable_feature", $"{{\"feature\":\"{dName}\",\"enabled\":false}}");
                    return ModActionResult.Ok($"Feature '{dName}' disabled");
                default:
                    return null;
            }
        }

        public void OnContentReady(ModContext context)
        {
            if (Environment.GetEnvironmentVariable("TERRARIA_MODDER_DEDSERV") == "1") return;
            // Apply Harmony patches here — guaranteed before any world loading can start,
            // and after game content is fully initialized (safe to patch WorldGen methods).
            ApplyPatches(null);
        }

        public void OnWorldLoad()
        {
            if (!_enabled || _featureManager == null) return;

            // Close world-gen panel when entering a world
            if (_worldGenPanel != null) _worldGenPanel.Visible = false;
            if (Main.netMode != 0)
            {
                _featureManager.Reset();
                GogGen.SpreadActive = false;
                _log.Info("[SeedLab] Multiplayer world loaded - runtime overrides remain inactive");
                return;
            }

            // Initialize feature states from the world's actual seed flags
            _featureManager.InitFromWorldFlags();

            // Reactivate Gog spread if existing Gog tiles are found on reload
            GogGen.CheckForExistingGogTiles();

            _log.Info("[SeedLab] World loaded - features initialized from world seed flags");
        }

        public void OnWorldUnload()
        {
            if (!_enabled || _featureManager == null) return;

            if (_panel != null) _panel.Visible = false;
            _featureManager.SaveState();
            _featureManager.Reset();
            GogGen.SpreadActive = false;
        }

        public void Unload()
        {
            _featureManager?.Reset();
            FrameEvents.OnPreUpdate -= OnUpdate;
            UIRenderer.UnregisterPanelDraw("seed-lab");
            _harmony?.UnpatchAll("com.terrariamodder.seedlab");
            if (_panel != null) _panel.Visible = false;
            if (_worldGenPanel != null) _worldGenPanel.Visible = false;
            _log.Info("[SeedLab] Unloaded");
        }

        /// <summary>
        /// Keybind callback for the panel appropriate to the current game context.
        /// </summary>
        private void OnToggle()
        {
            if (Main.gameMenu)
            {
                if (_worldGenPanel != null) _worldGenPanel.Visible = !_worldGenPanel.Visible;
                return;
            }
            if (Main.netMode != 0)
            {
                _log.Warn("[SeedLab] Runtime features are available in singleplayer only");
                return;
            }
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
            if (!Main.gameMenu && Main.netMode == 0) GogGen.UpdateSpread();
        }

        private void OnDraw()
        {
            if (Main.gameMenu)
            {
                _worldGenPanel?.Draw();
            }
            else
            {
                _panel?.Draw();
            }
        }



        private void ApplyPatches(object state)
        {
            try
            {
                SeedFeaturePatches.Apply(_harmony, _featureManager, _log);
                DontStarveFeaturePatches.Apply(_harmony, _featureManager, _log);
                WorldGenResetPatch.Apply(_harmony, _worldGenOverrideManager, _log);
                WorldGenPassPatch.Apply(_harmony, _worldGenOverrideManager, _log);
                FinalizeSecretSeedsPatch.Apply(_harmony, _worldGenOverrideManager, _log);
                FinalPassPatch.Apply(_harmony, _worldGenOverrideManager, _log);
                GogMiningPatch.Apply(_harmony, _log);
                WorldSaveFlagProtection.Apply(_harmony, _featureManager, _log);
            }
            catch (Exception ex)
            {
                _log.Error($"[SeedLab] Harmony patch error: {ex.Message}");
            }
        }
    }
}
