using System;
using System.Collections.Generic;
using System.IO;
using Terraria;
using TerrariaModder.Core;
using TerrariaModder.Core.Config;
using TerrariaModder.Core.Debug;
using TerrariaModder.Core.Events;
using TerrariaModder.Core.Logging;
using TerrariaModder.Core.UI;
using StorageHub.Config;
using StorageHub.Storage;
using StorageHub.Patches;
using StorageHub.UI;
using StorageHub.Crafting;
using StorageHub.Relay;
using StorageHub.Debug;
using StorageHub.PaintingChest;

namespace StorageHub
{
    /// <summary>
    /// Storage Hub - Unified storage access, crafting, and item management.
    ///
    /// CORE DESIGN PRINCIPLES:
    ///
    /// 1. IStorageProvider Abstraction:
    ///    - Multiplayer requires different access patterns than singleplayer
    ///    - UI calls interface methods, doesn't know if it's direct access or packets
    ///    - Built from day one to avoid retrofitting later
    ///
    /// 2. Data Safety:
    ///    - Passive operations (viewing/sorting) = ZERO writes
    ///    - UI holds ItemSnapshot copies, never references to real items
    ///    - Active operations (take/craft) are explicit user actions
    ///
    /// 3. Progression:
    ///    - 4 tiers using consumable items (Shadow Scale -> Luminite)
    ///    - Station memory at Tier 3+ (endgame convenience)
    ///    - Relays extend range without tier upgrade
    ///
    /// 4. Chest Registration:
    ///    - Must manually open chest first (prevents remote looting world-gen chests)
    ///    - Locked/trapped chests excluded
    /// </summary>
    public class Mod : IMod, IModLifecycle, IModStateProvider, IModActionProvider
    {
        public string Id => "storage-hub";
        public string Name => "Storage Hub";
        public string Version => "2.0.1";

        private ILogger _log;
        private static ILogger _sLog;
        private ModContext _context;
        private bool _enabled;
        private bool _paintingChestEnabled;
        private StorageHubModConfig _modConfig;

        // Configuration
        private StorageHubConfig _hubConfig;
        private string _modFolder;

        // Core components
        private ChestRegistry _registry;
        private IStorageProvider _storageProvider;
        private ChestOpenDetector _chestDetector;

        // Crafting system
        private RecipeIndex _recipeIndex;
        private CraftabilityChecker _craftChecker;
        private CraftingExecutor _craftExecutor;
        private RecursiveCrafter _recursiveCrafter;

        // Range/Relay system
        private RangeCalculator _rangeCalc;

        // UI
        private StorageHubUI _ui;

        // Debug
        private DebugDumper _debugDumper;
        public static DebugDumper Dumper { get; private set; }

        public void Initialize(ModContext context)
        {
            _log = context.Logger;
            _sLog = _log;
            _context = context;
            _modConfig = context.GetConfig<StorageHubModConfig>();

            LoadConfig();

            if (!_enabled)
            {
                _log.Info("StorageHub is disabled in config");
                return;
            }

            // Set up mod folder path (context.ModFolder points to this mod's folder)
            _modFolder = context.ModFolder;

            // Use injector env var to detect ded server — Main.dedServ triggers TypeInitializationException
            // on TerrariaServer.exe (Terraria.exe's XNA cctor fails headless).
            bool isDedServMode = Environment.GetEnvironmentVariable("TERRARIA_MODDER_DEDSERV") == "1";

            // Mysterious Chest uses tile type 21 (vanilla chest) — tileContainer[21] is
            // already true in vanilla. No need to set it. No server-side patches needed
            // because vanilla handles type 21 chest save/load/sync natively.
            _paintingChestEnabled = _modConfig != null ? _modConfig.PaintingChest : true;
            if (_paintingChestEnabled)
            {
                if (!isDedServMode)
                {
                    // Client: full initialization (item registration, UI, patches, texture extension)
                    PaintingChestManager.Initialize(_log, _context);
                }
                else
                {
                    // Server: register the item so CustomItemSave recognizes it
                    context.RegisterItem("painting-chest", new TerrariaModder.Core.Assets.ItemDefinition
                    {
                        DisplayName = "Mysterious Chest",
                        Tooltip = new[] { "A chest that holds far more than it appears" },
                        CreateTile = PaintingChestManager.TILE_TYPE,
                        PlaceStyle = PaintingChestManager.OUR_PLACE_STYLE,
                        Width = 32, Height = 32, MaxStack = 99, Consumable = true,
                        UseStyle = 1, UseTime = 10, UseAnimation = 15, AutoReuse = true,
                        Rarity = 2, Value = 10000
                    });
                    _log.Info("Mysterious Chest: server — item registered");

                    // Server also needs GetItemDrop_Chests prefix and TileObject.Place postfix
                    // so that broken chests drop our custom item and placed chests get named
                    try
                    {
                        PaintingChestPatches.ApplyPatches(_log);
                        _log.Info("Mysterious Chest: server patches applied (item drop + placement)");
                    }
                    catch (Exception ex)
                    {
                        _log.Warn($"Mysterious Chest: server patch failed: {ex.Message}");
                    }
                }
            }

            if (!isDedServMode)
            {
                // Client / host: register UI and input
                context.RegisterKeybind("toggle", "Toggle Storage Hub", "Open/close the Storage Hub UI", "F5", OnToggleUI);
                FrameEvents.OnPreUpdate += OnUpdate;
                UIRenderer.RegisterPanelDraw("storage-hub", OnDraw);

                // Subscribe to storage response for denial feedback
                TerrariaModder.Core.Net.NetSync.OnStorageResponse += OnStorageResponse;

                _log.Info("StorageHub initialized - Press F5 to open");
            }
            else
            {
                // Dedicated server: OnWorldLoad may not fire reliably on TerrariaServer.exe.
                // Create a fallback provider immediately using an empty registry — TakeItem reads
                // Main.chest[] directly so no registry is needed for take/move operations.
                // OnWorldLoad (if it fires) will overwrite with a proper config-loaded provider.
                _registry = new ChestRegistry(_log, null);
                _storageProvider = new MultiplayerProvider(_log, _registry, null);
                TerrariaModder.Core.Net.NetSync.OnStorageRequest = HandleServerStorageRequest;
                _log.Info("StorageHub initialized (dedicated server — fallback provider created, storage handler registered)");
            }

            context.RegisterStateProvider(this);
            context.RegisterActionProvider(this);

            // Status command accessible via HTTP API (useful when F5 keybind is unavailable)
            context.RegisterCommand("status", "Show StorageHub status (provider, chests, netMode)", OnStatusCommand);
        }

        private void LoadConfig()
        {
            _enabled = _modConfig != null ? _modConfig.Enabled : true;
        }

        private void OnToggleUI()
        {
            if (_ui == null) return;

            _ui.Toggle();
        }

        private void OnUpdate()
        {
            // Painting chest deferred patching must tick even when UI disabled
            if (_paintingChestEnabled)
                PaintingChestManager.Update();

            if (!_enabled) return;

            // Check for chest opens
            _chestDetector?.Update();

            // Update UI
            _ui?.Update();
        }

        private void OnDraw()
        {
            if (!_enabled) return;

            // Draw UI (handles its own visibility check)
            _ui?.Draw();
        }

        private void OnStatusCommand(string[] args)
        {
            string providerType = _storageProvider?.GetType().Name ?? "None";
            int chestCount = _registry?.Count ?? 0;
            bool isDedServ = Environment.GetEnvironmentVariable("TERRARIA_MODDER_DEDSERV") == "1";
            TerrariaModder.Core.Debug.CommandRegistry.Write(
                $"[storage-hub] provider={providerType} chests={chestCount} uiReady={_ui != null} enabled={_enabled} dedServ={isDedServ}");
        }

        private void OnStorageResponse(bool success, string operation, string resultPayload)
        {
            if (success) return;

            try
            {
                string reason = string.IsNullOrEmpty(resultPayload) ? "Unknown reason" : resultPayload;
                Main.NewText($"[StorageHub] Storage request denied: {reason}", 255, 80, 80);
                _log.Warn($"[StorageHub] Storage {operation} denied by server: {reason}");
            }
            catch (Exception ex)
            {
                _log.Warn($"[StorageHub] OnStorageResponse error: {ex.Message}");
            }
        }

        public void OnContentReady(ModContext context)
        {
            // Skip on dedicated server / H&P server — no GraphicsDevice, no textures to extend.
            bool isDedServ = Environment.GetEnvironmentVariable("TERRARIA_MODDER_DEDSERV") == "1";
            if (isDedServ) return;

            // Extend tile texture NOW — GraphicsDevice is ready at this point.
            // Must happen before any world loads. During H&P, tile data is received
            // before the Update loop runs, so the deferred timer is too late.
            // If the tile renders with an un-extended texture, the game freezes.
            if (_paintingChestEnabled)
            {
                TileTextureExtender.TryExtend();
                _log?.Info("Painting chest: early texture extension in OnContentReady");
            }
        }

        public void OnWorldLoad()
        {
            if (!_enabled) return;

            try
            {
                string worldName = GetWorldName();
                string charName = GetCharacterName();

                // On dedicated server there is no character — only need worldName.
                // On client/host, both names must be resolved to load per-character config.
                bool isDedServ = Environment.GetEnvironmentVariable("TERRARIA_MODDER_DEDSERV") == "1";
                if (worldName == "Unknown" || (!isDedServ && charName == "Unknown"))
                {
                    _log.Warn($"World load with incomplete names: world={worldName}, char={charName}");
                    _log.Warn("Config may not load correctly - please re-enter world");
                }

                _log.Info($"World loaded: {worldName}, Character: {charName}");

                // Initialize config
                _hubConfig = new StorageHubConfig(_log, _modFolder);
                _hubConfig.Load(worldName, charName);

                // Initialize registry
                _registry = new ChestRegistry(_log, _hubConfig);
                _registry.LoadFromConfig();
                // Wire up save callback so registrations persist immediately (survives force quit)
                _registry.OnRegistrationChanged = () => _hubConfig.Save();

                // Initialize storage provider based on network mode:
                //   Server (netMode==2) or H&P host (netMode==1 + IsHostAndPlay):
                //     MultiplayerProvider — direct chest access + packet 32/5 broadcasts to clients.
                //     Also registers OnStorageRequest so joining clients' requests are handled.
                //   Pure client (netMode==1, not H&P): ClientProvider — sends StorageRequest packets.
                //   Singleplayer (netMode==0): SingleplayerProvider — direct array access.
                int netMode = isDedServ ? 2 : Main.netMode;
                bool isHp = !isDedServ && netMode == 1 && Terraria.Netplay.IsHostAndPlay;
                if (netMode == 2 || isHp)
                {
                    _storageProvider = new MultiplayerProvider(_log, _registry, _hubConfig);
                    TerrariaModder.Core.Net.NetSync.OnStorageRequest = HandleServerStorageRequest;
                    _log.Info($"[StorageHub] Registered server-side storage request handler (H&P={isHp})");
                }
                else if (netMode == 1)
                    _storageProvider = new ClientProvider(_log, _registry, _hubConfig);
                else
                    _storageProvider = new SingleplayerProvider(_log, _registry, _hubConfig);

                // Initialize chest detector
                _chestDetector = new ChestOpenDetector(_log, _registry);
                _chestDetector.Initialize();

                // Initialize range calculator (must be before crafting system)
                _rangeCalc = new RangeCalculator(_log, _hubConfig);

                // Initialize crafting system
                _recipeIndex = new RecipeIndex(_log);
                _recipeIndex.Build();
                var craftingMaterials = new CraftingMaterialSource(_storageProvider, _rangeCalc,
                    () => (_modConfig?.BlockHotbarFromCrafting ?? false) || _hubConfig.HotbarProtection);
                _craftChecker = new CraftabilityChecker(_log, _recipeIndex, _storageProvider, _hubConfig, _rangeCalc, craftingMaterials);
                _craftExecutor = new CraftingExecutor(_log, _storageProvider, craftingMaterials);
                _recursiveCrafter = new RecursiveCrafter(_log, _recipeIndex, _craftChecker);
                _recursiveCrafter.SetExecutor(_craftExecutor);

                // Initialize debug dumper (clears old dumps)
                _debugDumper = new DebugDumper(_log, _modFolder);
                Dumper = _debugDumper;

                // Initialize UI
                _ui = new StorageHubUI(_log, _storageProvider, _hubConfig, _recipeIndex, _craftChecker, _recursiveCrafter, _rangeCalc, _modConfig);

                // Initialize painting chest
                if (_paintingChestEnabled)
                {
                    PaintingChestManager.OnWorldLoad(_hubConfig);
                }

                _log.Info($"StorageHub ready - {_registry.Count} registered chests, Tier {_hubConfig.Tier}");

                // Dump initial state
                _debugDumper.DumpState("WORLD_LOAD");
            }
            catch (Exception ex)
            {
                _log.Error($"OnWorldLoad error: {ex.Message}");
            }
        }

        public void OnWorldUnload()
        {
            if (!_enabled) return;

            try
            {
                // Close UI
                _ui?.Close();

                // Save config
                if (_registry != null && _hubConfig != null)
                {
                    _registry.SaveToConfig();
                    _hubConfig.Save();
                    _log.Info("StorageHub config saved");
                }

                // Reset detector
                _chestDetector?.Reset();

                // Write debug session summary
                _debugDumper?.WriteSessionSummary();

                // Unregister server-side storage handler (covers dedServ, netMode==2, and H&P host)
                TerrariaModder.Core.Net.NetSync.OnStorageRequest = null;

                // Clear singleton and null out references to prevent stale polling between worlds
                ChestRegistry.ClearInstance();
                _ui = null;
                _hubConfig = null;
                _registry = null;
                _storageProvider = null;
                _chestDetector = null;
                _recipeIndex = null;
                _craftChecker = null;
                _craftExecutor = null;
                _recursiveCrafter = null;
                _rangeCalc = null;
                _debugDumper = null;
                Dumper = null;
            }
            catch (Exception ex)
            {
                _log.Error($"OnWorldUnload error: {ex.Message}");
            }
        }

        public Dictionary<string, object> GetModState()
        {
            return new Dictionary<string, object>
            {
                { "enabled", _enabled },
                { "panelOpen", _ui?.IsOpen ?? false },
                { "activeTab", _ui?.ActiveTabName ?? "" },
                { "craftableCount", _ui?.CraftableCount ?? 0 },
                { "recursiveCandidateCount", _ui?.RecursiveCandidateCount ?? 0 },
                { "lastCraftRefreshMs", _ui?.LastCraftRefreshMilliseconds ?? 0 },
                { "lastRecursiveScanMs", _ui?.LastRecursiveScanMilliseconds ?? 0 },
                { "lastChangeProbeMs", _ui?.LastChangeProbeMilliseconds ?? 0 },
                { "skippedPeriodicRefreshes", _ui?.SkippedPeriodicRefreshes ?? 0 },
                { "detectedExternalChanges", _ui?.DetectedExternalChanges ?? 0 },
                { "registeredChestCount", _registry?.Count ?? 0 },
                { "paintingChestEnabled", _paintingChestEnabled },
                { "hasProvider", _storageProvider != null }
            };
        }

        public List<ModActionInfo> GetActions()
        {
            return new List<ModActionInfo>
            {
                new ModActionInfo("open_panel", "Open the Storage Hub panel"),
                new ModActionInfo("close_panel", "Close the Storage Hub panel"),
                new ModActionInfo("register_nearby", "Register all nearby chests within scan radius"),
                new ModActionInfo("set_tab", "Switch panel to a tab (params: tab=Items|Crafting|Recipes|Shimmer|Unlocks|Network)"),
                new ModActionInfo("get_tab", "Get current active tab name"),
            };
        }

        public ModActionResult ExecuteAction(string name, Dictionary<string, string> args)
        {
            switch (name)
            {
                case "open_panel":
                    if (_ui == null) return ModActionResult.Fail("UI not initialized");
                    if (!_ui.IsOpen) OnToggleUI();
                    EventLog.Emit("storage-hub", "open_panel", "{\"open\":true}");
                    return ModActionResult.Ok("Panel opened");
                case "close_panel":
                    if (_ui != null && _ui.IsOpen) OnToggleUI();
                    EventLog.Emit("storage-hub", "close_panel", "{\"open\":false}");
                    return ModActionResult.Ok("Panel closed");
                case "set_tab":
                {
                    if (_ui == null) return ModActionResult.Fail("UI not initialized");
                    string tab;
                    if (!args.TryGetValue("tab", out tab)) return ModActionResult.Fail("Missing 'tab' param");
                    _ui.SetActiveTab(tab);
                    return ModActionResult.Ok($"Switched to tab: {_ui.ActiveTabName}");
                }
                case "get_tab":
                    if (_ui == null) return ModActionResult.Fail("UI not initialized");
                    return ModActionResult.Ok($"Active tab: {_ui.ActiveTabName}");
                case "register_nearby":
                {
                    if (_registry == null) return ModActionResult.Fail("Registry not initialized");
                    int registered = 0;
                    for (int i = 0; i < 8000; i++)
                    {
                        var chest = Main.chest[i];
                        if (chest == null) continue;
                        if (_registry.RegisterChest(chest.x, chest.y))
                            registered++;
                    }
                    return ModActionResult.Ok($"Registered {registered} new chest(s), total {_registry.Count}");
                }
                default:
                    return null;
            }
        }

        public void Unload()
        {
            NativePickupNotifications.Unload();
            if (Environment.GetEnvironmentVariable("TERRARIA_MODDER_DEDSERV") != "1")
            {
                FrameEvents.OnPreUpdate -= OnUpdate;
                UIRenderer.UnregisterPanelDraw("storage-hub");
                TerrariaModder.Core.Net.NetSync.OnStorageResponse -= OnStorageResponse;
                _ui?.Close();
                _ui = null;
            }

            _hubConfig = null;
            _registry = null;
            _storageProvider = null;
            _chestDetector = null;
            _recipeIndex = null;
            _craftChecker = null;
            _craftExecutor = null;
            _recursiveCrafter = null;
            _rangeCalc = null;

            if (_paintingChestEnabled)
            {
                PaintingChestManager.Unload();
            }

            _log.Info("StorageHub unloaded");
        }

        /// <summary>
        /// Server-side handler for StorageRequest packets from connected clients.
        /// Registered as NetSync.OnStorageRequest when netMode==2.
        ///
        /// The server executes the requested storage operation via its own MultiplayerProvider,
        /// which writes to Main.chest[] and broadcasts packet 32 (SyncChestItem) to ALL clients
        /// — including the requester — confirming or correcting the optimistic client-side update.
        ///
        /// Operations:
        ///   "take"    — remove items from a chest slot: payload="{chestIndex}:{slot}:{count}"
        ///   "move"    — same as take (client handles the inventory side): payload="{chestIndex}:{slot}:{count}"
        ///   "deposit" — add items to a chest: payload="{chestIndex}:{itemId}:{count}:{prefix}"
        /// </summary>
        private static readonly bool _isDedServMode =
            Environment.GetEnvironmentVariable("TERRARIA_MODDER_DEDSERV") == "1";

        private void HandleServerStorageRequest(int callerSlot, string operation, string payload)
        {
            if (!(_modConfig?.TrustAllPlayers ?? true)
                && !TerrariaModder.Core.Permissions.PermissionService.IsAdmin(callerSlot)
                && !TerrariaModder.Core.Permissions.PermissionService.HasModAccess(callerSlot, "storage-hub"))
            {
                _log.Debug($"[StorageHub] Storage request denied for slot {callerSlot}: not admin or granted");
                TerrariaModder.Core.Net.NetSync.SendStorageResponseTo(callerSlot, false, operation, "Denied: requires Admin or StorageHub grant");
                return;
            }

            // On TerrariaServer.exe, Terraria.exe's XNA cctor fails headlessly — direct Main.chest/player
            // access throws TypeInitializationException. Route all chest ops through DedServProxy on dedServ.
            if (_isDedServMode)
            {
                HandleServerStorageRequestDedServ(callerSlot, operation, payload);
                return;
            }

            if (_storageProvider == null)
            {
                _log.Warn($"[StorageHub] HandleServerStorageRequest: _storageProvider is null — request from slot {callerSlot} dropped ({operation} {payload})");
                return;
            }

            try
            {
                switch (operation?.ToLowerInvariant())
                {
                    case "take":
                    case "move":
                    {
                        // payload: "{chestIndex}:{slot}:{count}"
                        var parts = payload?.Split(':');
                        if (parts == null || parts.Length < 3) return;
                        if (!int.TryParse(parts[0], out int chestIndex) ||
                            !int.TryParse(parts[1], out int slot) ||
                            !int.TryParse(parts[2], out int count)) return;

                        // Execute via server's MultiplayerProvider — broadcasts packet 32 to all clients
                        bool took = _storageProvider.TakeItem(chestIndex, slot, count, out _);
                        _log.Debug($"[StorageHub] {operation} from slot {callerSlot}: chest={chestIndex} slot={slot} count={count} result={took}");
                        TerrariaModder.Core.Net.NetSync.SendStorageResponseTo(callerSlot, took, operation, took ? "ok" : "empty");
                        break;
                    }

                    case "deposit":
                    {
                        // payload: "{chestIndex}:{itemId}:{count}:{prefix}"
                        var parts = payload?.Split(':');
                        if (parts == null || parts.Length < 4) return;
                        if (!int.TryParse(parts[0], out int chestIndex) ||
                            !int.TryParse(parts[1], out int itemId) ||
                            !int.TryParse(parts[2], out int count) ||
                            !int.TryParse(parts[3], out int prefix)) return;

                        // G3: defensive bounds — reject out-of-range type IDs before they can index arrays
                        const int MaxItemType = 32767; // TypeExtension extended count
                        if (itemId < 0 || itemId > MaxItemType)
                        {
                            _log.Warn($"[StorageHub] Slot {callerSlot} sent out-of-range itemId {itemId} in deposit — ignoring");
                            TerrariaModder.Core.Net.NetSync.SendStorageResponseTo(callerSlot, false, "deposit", "invalid_type");
                            break;
                        }

                        bool success = DepositToChestDirect(chestIndex, itemId, count, prefix);
                        TerrariaModder.Core.Net.NetSync.SendStorageResponseTo(callerSlot, success, "deposit", success ? "ok" : "no_space");
                        break;
                    }

                    default:
                        _log.Warn($"[StorageHub] Unknown StorageRequest operation '{operation}' from slot {callerSlot}");
                        break;
                }
            }
            catch (Exception ex)
            {
                _log.Warn($"[StorageHub] HandleServerStorageRequest error: {ex.Message}");
            }
        }

        /// <summary>
        /// Dedicated-server variant: all chest ops go through DedServProxy to avoid
        /// TypeInitializationException from Terraria.exe's XNA static constructor.
        /// </summary>
        private void HandleServerStorageRequestDedServ(int callerSlot, string operation, string payload)
        {
            try
            {
                switch (operation?.ToLowerInvariant())
                {
                    case "take":
                    case "move":
                    {
                        var parts = payload?.Split(':');
                        if (parts == null || parts.Length < 3) return;
                        if (!int.TryParse(parts[0], out int chestIndex) ||
                            !int.TryParse(parts[1], out int slot) ||
                            !int.TryParse(parts[2], out int count)) return;

                        int taken = TerrariaModder.Core.Server.DedServProxy.TakeFromChest(chestIndex, slot, count);
                        _log.Debug($"[StorageHub] {operation} (dedServ) from slot {callerSlot}: chest={chestIndex} slot={slot} count={count} taken={taken}");
                        TerrariaModder.Core.Net.NetSync.SendStorageResponseTo(callerSlot, taken > 0, operation, taken > 0 ? "ok" : "empty");
                        break;
                    }

                    case "deposit":
                    {
                        var parts = payload?.Split(':');
                        if (parts == null || parts.Length < 4) return;
                        if (!int.TryParse(parts[0], out int chestIndex) ||
                            !int.TryParse(parts[1], out int itemId) ||
                            !int.TryParse(parts[2], out int count) ||
                            !int.TryParse(parts[3], out int prefix)) return;

                        const int MaxItemType = 32767;
                        if (itemId < 0 || itemId > MaxItemType)
                        {
                            _log.Warn($"[StorageHub] (dedServ) Slot {callerSlot} sent out-of-range itemId {itemId} in deposit — ignoring");
                            TerrariaModder.Core.Net.NetSync.SendStorageResponseTo(callerSlot, false, "deposit", "invalid_type");
                            break;
                        }

                        int deposited = TerrariaModder.Core.Server.DedServProxy.DepositToChest(chestIndex, itemId, count, prefix);
                        _log.Debug($"[StorageHub] deposit (dedServ) from slot {callerSlot}: chest={chestIndex} itemId={itemId} count={count} deposited={deposited}");
                        bool success = deposited > 0;
                        TerrariaModder.Core.Net.NetSync.SendStorageResponseTo(callerSlot, success, "deposit", success ? "ok" : "no_space");
                        break;
                    }

                    default:
                        _log.Warn($"[StorageHub] (dedServ) Unknown StorageRequest operation '{operation}' from slot {callerSlot}");
                        break;
                }
            }
            catch (Exception ex)
            {
                _log.Warn($"[StorageHub] HandleServerStorageRequestDedServ error: {ex.Message}");
            }
        }

        /// <summary>
        /// Deposit items directly into a specific chest by index.
        /// First tries to stack with existing items, then fills empty slots.
        /// Broadcasts packet 32 for each modified slot.
        /// </summary>
        private static bool DepositToChestDirect(int chestIndex, int itemId, int count, int prefix)
        {
            try
            {
                var chests = Main.chest;
                if (chests == null || chestIndex < 0 || chestIndex >= chests.Length) return false;
                var chest = chests[chestIndex];
                if (chest?.item == null) return false;

                // Pass 1: stack with matching items
                for (int i = 0; i < chest.item.Length && count > 0; i++)
                {
                    var slot = chest.item[i];
                    if (slot == null || slot.type != itemId || slot.prefix != prefix) continue;
                    if (slot.stack >= slot.maxStack) continue;

                    int canTake = slot.maxStack - slot.stack;
                    int take = Math.Min(canTake, count);
                    slot.stack += take;
                    count -= take;
                    NetMessage.TrySendData(32, -1, -1, null, chestIndex, i);
                }

                if (count <= 0) return true;

                // Pass 2: fill empty slots
                for (int i = 0; i < chest.item.Length && count > 0; i++)
                {
                    var slot = chest.item[i];
                    if (slot == null || slot.type != 0) continue;

                    slot.SetDefaults(itemId);
                    int toPlace = Math.Min(count, slot.maxStack);
                    slot.stack = toPlace;
                    slot.prefix = (byte)prefix;
                    NetMessage.TrySendData(32, -1, -1, null, chestIndex, i);
                    count -= toPlace;
                }

                return count <= 0;
            }
            catch (Exception ex)
            {
                _sLog?.Warn($"DepositToChestDirect failed: {ex.Message}");
                return false;
            }
        }

        private string GetWorldName()
        {
            try
            {
                var worldName = Main.worldName;
                if (string.IsNullOrEmpty(worldName))
                {
                    _log.Warn("World name is empty or null");
                    return "Unknown";
                }
                return worldName;
            }
            catch (Exception ex)
            {
                _log.Error($"GetWorldName error: {ex.Message}");
                return "Unknown";
            }
        }

        private string GetCharacterName()
        {
            try
            {
                var player = Main.player[Main.myPlayer];
                if (player == null)
                {
                    _log.Warn("Player is null");
                    return "Unknown";
                }

                var charName = player.name;
                if (string.IsNullOrEmpty(charName))
                {
                    _log.Warn("Character name is empty or null");
                    return "Unknown";
                }
                return charName;
            }
            catch (Exception ex)
            {
                _log.Error($"GetCharacterName error: {ex.Message}");
                return "Unknown";
            }
        }
    }
}
