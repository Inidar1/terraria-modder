using System;
using System.Collections.Generic;
using HarmonyLib;
using Randomizer.Modules;
using Randomizer.UI;
using Terraria;
using TerrariaModder.Core;
using TerrariaModder.Core.Events;
using TerrariaModder.Core.Input;
using TerrariaModder.Core.Logging;
using TerrariaModder.Core.Reflection;
using TerrariaModder.Core.UI;

namespace Randomizer
{
    public class Mod : IMod
    {
        public string Id => "randomizer";
        public string Name => "Randomizer";
        public string Version => "1.0.0";

        private ILogger _log;
        private ModContext _context;
        private bool _enabled;

        private RandomSeed _seed;
        private RandomizerPanel _panel;
        private WorldGenPanel _worldGenPanel;
        private WorldGenState _worldGenState;
        private List<ModuleBase> _modules;

        private static Harmony _harmony;
        private bool _patchesApplied;

        // Menu hotkey edge detection (KeybindManager skips menus)
        private bool _hotKeyWasDown;

        // Static reference for Harmony patches to access modules
        internal static Mod Instance { get; private set; }

        public IReadOnlyList<ModuleBase> Modules => _modules;
        public RandomSeed Seed => _seed;

        public void Initialize(ModContext context)
        {
            _log = context.Logger;
            _context = context;
            Instance = this;

            _enabled = context.Config.Get<bool>("enabled");
            if (!_enabled)
            {
                _log.Info("[Randomizer] Disabled in config");
                return;
            }

            // Initialize seed system
            int seedValue = context.Config.Get("seed", 0);
            _seed = new RandomSeed(seedValue);

            // Register all modules
            _modules = new List<ModuleBase>
            {
                new ChestLootModule(),
                new EnemyDropsModule(),
                new RecipeModule(),
                new ShopModule(),
                new FishingModule(),
                new TileDropsModule(),
                new SpawnModule(),
                new ItemStatsModule(),
                new StartingItemsModule(),
                new GravityModule(),
                new WeatherModule(),
            };

            foreach (var module in _modules)
            {
                module.Init(_log, _seed);
                // Load enabled state from config (runtime modules only)
                if (!module.IsWorldGen)
                    module.Enabled = context.Config.Get($"module_{module.Id}", false);
            }

            // Initialize per-world state persistence
            _worldGenState = new WorldGenState(_log, context.ModFolder);

            // Create UI panels
            _panel = new RandomizerPanel(_log, this);
            _worldGenPanel = new WorldGenPanel(_log, this, _worldGenState);

            // Register keybind (in-world only via KeybindManager)
            context.RegisterKeybind("toggle", "Toggle Panel", "Open/close Randomizer config", "Divide", OnToggle);

            // Subscribe to events (fires in both menu and world)
            FrameEvents.OnPreUpdate += OnUpdate;
            UIRenderer.RegisterPanelDraw("randomizer", OnDraw);

            // Harmony instance — patches applied on first update (game thread, safe)
            _harmony = new Harmony("com.terrariamodder.randomizer");

            _log.Info($"[Randomizer] Initialized with seed {_seed.Seed} — Press Numpad / to configure");
        }

        public void OnWorldLoad()
        {
            if (!_enabled || _modules == null) return;

            // Close world-gen panel when entering a world
            _worldGenPanel?.Close();

            // Load or apply per-world state
            string worldName = GetWorldName();
            int worldSeed = _worldGenState.OnWorldLoad(worldName);

            // Lock world-gen modules based on per-world state
            foreach (var module in _modules)
            {
                if (!module.IsWorldGen) continue;

                if (_worldGenState.IsLocked(module.Id))
                {
                    module.IsLocked = true;
                    module.Enabled = true;
                    _log.Info($"[Randomizer] {module.Name}: locked on for this world");
                }
                else
                {
                    module.IsLocked = false;
                    module.Enabled = false;
                }
            }

            // Use world-gen seed if available, otherwise use configured seed
            if (worldSeed != 0)
            {
                _seed.SetSeed(worldSeed);
                _log.Info($"[Randomizer] Using world-gen seed: {worldSeed}");
            }

            // Build shuffle maps for all enabled modules
            foreach (var module in _modules)
            {
                if (module.Enabled)
                {
                    try
                    {
                        module.BuildShuffleMap();
                        _log.Info($"[Randomizer] {module.Name}: shuffle map built ({module.Id})");
                    }
                    catch (Exception ex)
                    {
                        _log.Error($"[Randomizer] {module.Name} BuildShuffleMap error: {ex.Message}");
                    }
                }
            }

            _log.Info($"[Randomizer] World loaded — seed {_seed.Seed}");
        }

        public void OnWorldUnload()
        {
            if (!_enabled) return;

            _panel?.Close();
            _worldGenState?.OnWorldUnload();

            // Unlock world-gen modules
            foreach (var module in _modules)
            {
                if (module.IsWorldGen)
                {
                    module.IsLocked = false;
                    module.Enabled = false;
                }
            }

            _log.Info("[Randomizer] World unloaded");
        }

        public void Unload()
        {
            FrameEvents.OnPreUpdate -= OnUpdate;
            UIRenderer.UnregisterPanelDraw("randomizer");

            _harmony?.UnpatchAll("com.terrariamodder.randomizer");
            _panel?.Close();
            _worldGenPanel?.Close();
            Instance = null;

            _log.Info("[Randomizer] Unloaded");
        }

        /// <summary>
        /// Keybind callback (in-world only, via KeybindManager).
        /// </summary>
        private void OnToggle()
        {
            if (_panel == null) return;
            _panel.Toggle();
        }

        private void OnUpdate()
        {
            if (!_enabled || _modules == null) return;

            // Apply Harmony patches once on first update (game thread, safe timing)
            if (!_patchesApplied)
            {
                _patchesApplied = true;
                ApplyPatches();
            }

            // Update both panels (handles TextInput keyboard events)
            _panel?.Update();
            _worldGenPanel?.Update();

            foreach (var module in _modules)
            {
                if (module.Enabled && module is IUpdatable updatable)
                {
                    try
                    {
                        updatable.OnUpdate();
                    }
                    catch (Exception ex)
                    {
                        _log.Error($"[Randomizer] {module.Name} update error: {ex.Message}");
                    }
                }
            }
        }

        private void OnDraw()
        {
            if (Game.InMenu)
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
        /// KeybindManager skips input when Main.gameMenu=true, so we poll directly.
        /// </summary>
        private void PollMenuInput()
        {
            InputState.Update();

            bool keyDown = InputState.IsKeyDown(KeyCode.Divide);
            bool justPressed = keyDown && !_hotKeyWasDown;
            _hotKeyWasDown = keyDown;

            if (justPressed)
            {
                _worldGenPanel.Visible = !_worldGenPanel.Visible;
            }
        }

        private void ApplyPatches()
        {
            foreach (var module in _modules)
            {
                try
                {
                    module.ApplyPatches(_harmony);
                    _log.Info($"[Randomizer] {module.Name}: patches applied");
                }
                catch (Exception ex)
                {
                    _log.Error($"[Randomizer] {module.Name} patch error: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Called when a module toggle changes in the UI.
        /// Saves config and rebuilds shuffle map if in world.
        /// </summary>
        public void OnModuleToggled(ModuleBase module)
        {
            // Don't save world-gen module state to config (managed by WorldGenState)
            if (!module.IsWorldGen)
            {
                _context.Config.Set($"module_{module.Id}", module.Enabled);
                _context.Config.Save();
            }

            if (module.Enabled && Game.InWorld)
            {
                try
                {
                    module.BuildShuffleMap();
                    _log.Info($"[Randomizer] {module.Name}: enabled, shuffle map built");
                }
                catch (Exception ex)
                {
                    _log.Error($"[Randomizer] {module.Name} error: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Called when seed changes in the UI.
        /// </summary>
        public void OnSeedChanged(int newSeed)
        {
            _seed.SetSeed(newSeed);
            _context.Config.Set("seed", _seed.Seed);
            _context.Config.Save();

            // Rebuild all enabled shuffle maps
            if (Game.InWorld)
            {
                foreach (var module in _modules)
                {
                    if (module.Enabled)
                    {
                        try
                        {
                            module.BuildShuffleMap();
                        }
                        catch (Exception ex)
                        {
                            _log.Error($"[Randomizer] {module.Name} rebuild error: {ex.Message}");
                        }
                    }
                }
            }

            _log.Info($"[Randomizer] Seed changed to {_seed.Seed}");
        }

        private static string GetWorldName()
        {
            return Main.worldName ?? "unknown";
        }
    }

    /// <summary>
    /// Interface for modules that need per-frame updates (gravity, weather chaos).
    /// </summary>
    public interface IUpdatable
    {
        void OnUpdate();
    }
}
