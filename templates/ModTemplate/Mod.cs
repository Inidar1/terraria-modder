using TerrariaModder.Core;
using TerrariaModder.Core.Config;
using TerrariaModder.Core.Events;
using TerrariaModder.Core.Logging;

namespace ModTemplate
{
    /// <summary>
    /// Mod configuration. Subclass ModConfig and declare typed properties with attributes.
    /// The framework generates the settings UI automatically from this class.
    /// Config is stored in TerrariaModder/core/configs/my-mod.client.json.
    /// </summary>
    public class MyModConfig : ModConfig
    {
        public override int Version => 1;

        [Client, Label("Enabled"), Description("Enable or disable this mod's features.")]
        public bool Enabled { get; set; } = true;

        [Client, Label("Example Number"), Description("An example numeric setting."), Range(1, 100)]
        public int ExampleNumber { get; set; } = 10;

        [Client, Label("Example Scale"), Description("An example decimal setting."), Range(0.1f, 5.0f)]
        public float ExampleFloat { get; set; } = 1.0f;
    }

    /// <summary>
    /// Main mod class. Rename this namespace and class to match your mod.
    /// </summary>
    public class Mod : ModBase, IModLifecycle
    {
        private ILogger _log;
        private ModContext _context;
        private MyModConfig _config;

        public override void Initialize(ModContext context)
        {
            _log = context.Logger;
            _context = context;
            _config = context.GetConfig<MyModConfig>();

            _log.Info($"{Name} v{Version} initialized!");

            // Register keybinds
            context.RegisterKeybind("toggle", "Toggle Feature", "Toggle the main feature", "F7", OnTogglePressed);

            // Subscribe to game events (uncomment what you need)
            // GameEvents.OnWorldLoad += OnWorldLoad;
            // GameEvents.OnWorldUnload += OnWorldUnload;
            // GameEvents.OnDayStart += () => _log.Info("Day started!");
            // GameEvents.OnNightStart += () => _log.Info("Night started!");
            // FrameEvents.OnPostUpdate += OnUpdate;
            // PlayerEvents.OnPlayerSpawn += OnPlayerSpawn;
            // PlayerEvents.OnPlayerDeath += OnPlayerDeath;
            // NPCEvents.OnBossSpawn += OnBossSpawn;
        }

        /// <summary>
        /// Called after all mods have initialized and runtime type IDs are assigned.
        /// Use for cross-mod lookups (context.GetItemType, context.TryGetItemType).
        /// </summary>
        public void OnContentReady(ModContext context)
        {
            // Cross-mod item lookups are valid here:
            // int swordType = context.GetItemType("other-mod", "cool-sword");
        }

        private void OnTogglePressed()
        {
            _config.Enabled = !_config.Enabled;
            _config.Save();
            _log.Info($"Feature is now: {(_config.Enabled ? "ENABLED" : "DISABLED")}");
        }

        #region Config

        /// <summary>
        /// Optional: called when config is changed via the mod menu (F6).
        /// Detected via reflection - not part of the IMod interface.
        /// Implement this to support live config updates without restart.
        /// If omitted, the mod menu shows "restart required" for config changes.
        /// </summary>
        public void OnConfigChanged()
        {
            _log.Info($"Config changed: Enabled={_config.Enabled}, ExampleNumber={_config.ExampleNumber}");
        }

        #endregion

        #region Lifecycle

        public void OnWorldLoad()
        {
            _log.Info("World loaded!");
        }

        public void OnWorldUnload()
        {
            _log.Info("World unloaded!");
        }

        public override void Unload()
        {
            // Unsubscribe from events to prevent memory leaks
            // GameEvents.OnWorldLoad -= OnWorldLoad;
            // FrameEvents.OnPostUpdate -= OnUpdate;

            _log.Info($"{Name} unloaded!");
        }

        #endregion

        #region Event Handlers (uncomment and use as needed)

        // private void OnUpdate()
        // {
        //     if (!_config.Enabled) return;
        //     // Called every frame - be careful with performance!
        // }

        // private void OnPlayerSpawn(PlayerSpawnEventArgs args)
        // {
        //     _log.Info($"Player spawned at ({args.SpawnX}, {args.SpawnY})");
        // }

        // private void OnPlayerDeath(PlayerDeathEventArgs args)
        // {
        //     _log.Info($"Player died! Reason: {args.DeathReason}");
        // }

        // private void OnBossSpawn(BossSpawnEventArgs args)
        // {
        //     _log.Info($"Boss spawned: {args.BossName}");
        // }

        #endregion
    }
}
