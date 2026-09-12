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
    public class Mod : IMod, IModLifecycle
    {
        public string Id => "randomizer";
        public string Name => "Randomizer";
        public string Version => "2.0.0";

        private ILogger _log;
        private ModContext _context;
        private bool _enabled;
        private RandomizerConfig _config;

        private RandomSeed _seed;
        private RandomizerPanel _panel;
        private WorldGenPanel _worldGenPanel;
        private WorldGenState _worldGenState;
        private List<ModuleBase> _modules;

        private static Harmony _harmony;
        private bool _patchesApplied;


        // Static reference for Harmony patches to access modules
        internal static Mod Instance { get; private set; }

        public IReadOnlyList<ModuleBase> Modules => _modules;
        public RandomSeed Seed => _seed;

        public void Initialize(ModContext context)
        {
            _log = context.Logger;
            _context = context;
            Instance = this;
            _config = context.GetConfig<RandomizerConfig>();

            _enabled = _config != null ? _config.Enabled : true;
            if (!_enabled)
            {
                _log.Info("[Randomizer] Disabled in config");
                return;
            }

            if (Environment.GetEnvironmentVariable("TERRARIA_MODDER_DEDSERV") == "1")
            {
                _log.Info("[Randomizer] Dedicated server — skipping client init");
                return;
            }

            // Initialize seed system
            int seedValue = _config != null ? _config.Seed : 0;
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
                    module.Enabled = GetModuleEnabled(module.Id);
            }

            // Initialize per-world state persistence
            _worldGenState = new WorldGenState(_log, context.ModFolder);

            // Create UI panels
            _panel = new RandomizerPanel(_log, this);
            _worldGenPanel = new WorldGenPanel(_log, this, _worldGenState);

            // Core owns input polling in both title-screen and world contexts.
            context.RegisterKeybind("toggle", "Toggle Panel", "Open/close Randomizer config", "NumDiv", OnToggle).AllowInMenu = true;

            // Subscribe to events (fires in both menu and world)
            FrameEvents.OnPreUpdate += OnUpdate;
            UIRenderer.RegisterPanelDraw("randomizer", OnDraw);

            // Harmony instance — patches applied on first update (game thread, safe)
            _harmony = new Harmony("com.terrariamodder.randomizer");

            _log.Info($"[Randomizer] Initialized with seed {_seed.Seed} — Press Numpad / to configure");
        }

        private bool GetModuleEnabled(string moduleId)
        {
            if (_config == null) return false;
            switch (moduleId)
            {
                case "chest_loot": return _config.ModuleChestLoot;
                case "enemy_drops": return _config.ModuleEnemyDrops;
                case "recipes": return _config.ModuleRecipes;
                case "shops": return _config.ModuleShops;
                case "fishing": return _config.ModuleFishing;
                case "tile_drops": return _config.ModuleTileDrops;
                case "spawns": return _config.ModuleSpawns;
                case "item_stats": return _config.ModuleItemStats;
                case "starting_items": return _config.ModuleStartingItems;
                case "gravity": return _config.ModuleGravity;
                case "weather": return _config.ModuleWeather;
                default: return false;
            }
        }

        private void SetModuleEnabled(string moduleId, bool value)
        {
            if (_config == null) return;
            switch (moduleId)
            {
                case "chest_loot": _config.ModuleChestLoot = value; break;
                case "enemy_drops": _config.ModuleEnemyDrops = value; break;
                case "recipes": _config.ModuleRecipes = value; break;
                case "shops": _config.ModuleShops = value; break;
                case "fishing": _config.ModuleFishing = value; break;
                case "tile_drops": _config.ModuleTileDrops = value; break;
                case "spawns": _config.ModuleSpawns = value; break;
                case "item_stats": _config.ModuleItemStats = value; break;
                case "starting_items": _config.ModuleStartingItems = value; break;
                case "gravity": _config.ModuleGravity = value; break;
                case "weather": _config.ModuleWeather = value; break;
            }
        }

        public void OnContentReady(ModContext context) { }

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

            // Unlock world-gen modules and reset per-world state
            foreach (var module in _modules)
            {
                if (module.IsWorldGen)
                {
                    module.IsLocked = false;
                    module.Enabled = false;
                }

                if (module is Modules.StartingItemsModule sim)
                    sim.ResetForNewWorld();
            }

            _log.Info("[Randomizer] World unloaded");
        }

        public void Unload()
        {
            FrameEvents.OnPreUpdate -= OnUpdate;
            UIRenderer.UnregisterPanelDraw("randomizer");

            // Revert module data mutations (recipes, chests, weather) before removing patches
            if (_modules != null)
            {
                foreach (var module in _modules)
                {
                    try { module.RemovePatches(_harmony); }
                    catch (Exception ex) { _log.Error($"[Randomizer] {module.Name} cleanup error: {ex.Message}"); }
                }
            }

            _harmony?.UnpatchAll("com.terrariamodder.randomizer");
            _patchesApplied = false;
            _panel?.Close();
            _worldGenPanel?.Close();
            Instance = null;

            // Null out static Instance fields on all modules to prevent stale references
            if (_modules != null)
            {
                foreach (var module in _modules)
                {
                    if (module is EnemyDropsModule) EnemyDropsModule.Instance = null;
                    else if (module is RecipeModule) RecipeModule.Instance = null;
                    else if (module is ShopModule) ShopModule.Instance = null;
                    else if (module is FishingModule) FishingModule.Instance = null;
                    else if (module is TileDropsModule) TileDropsModule.Instance = null;
                    else if (module is SpawnModule) SpawnModule.Instance = null;
                    else if (module is ItemStatsModule) ItemStatsModule.Instance = null;
                    else if (module is StartingItemsModule) StartingItemsModule.Instance = null;
                    else if (module is GravityModule) GravityModule.Instance = null;
                    else if (module is WeatherModule) WeatherModule.Instance = null;
                }
                _modules = null;
            }

            _log.Info("[Randomizer] Unloaded");
        }

        /// <summary>
        /// Keybind callback for the panel appropriate to the current game context.
        /// </summary>
        private void OnToggle()
        {
            if (Game.InMenu)
            {
                if (_worldGenPanel != null) _worldGenPanel.Visible = !_worldGenPanel.Visible;
                return;
            }
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
                _worldGenPanel?.Draw();
            }
            else
            {
                _panel?.Draw();
            }
        }



        private void ApplyPatches()
        {
            foreach (var module in _modules)
            {
                // Only apply patches for enabled modules to avoid hot-path overhead
                if (!module.Enabled) continue;

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
                SetModuleEnabled(module.Id, module.Enabled);
                _config?.Save();
            }

            if (module.Enabled && Game.InWorld)
            {
                try
                {
                    module.BuildShuffleMap();
                    module.ApplyPatches(_harmony);
                    _log.Info($"[Randomizer] {module.Name}: enabled, shuffle map built + patches applied");
                }
                catch (Exception ex)
                {
                    _log.Error($"[Randomizer] {module.Name} error: {ex.Message}");
                }
            }
            else if (!module.Enabled && _harmony != null)
            {
                try
                {
                    module.RemovePatches(_harmony);
                    _log.Info($"[Randomizer] {module.Name}: disabled, patches removed");
                }
                catch (Exception ex)
                {
                    _log.Error($"[Randomizer] {module.Name} cleanup error: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Called when seed changes in the UI.
        /// </summary>
        public void OnSeedChanged(int newSeed)
        {
            _seed.SetSeed(newSeed);
            if (_config != null) { _config.Seed = _seed.Seed; _config.Save(); }

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
