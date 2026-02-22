using System;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using TerrariaModder.Core;
using TerrariaModder.Core.Events;
using TerrariaModder.Core.Input;
using TerrariaModder.Core.Logging;
using TerrariaModder.Core.UI;
using TerrariaModder.Core.UI.Widgets;

namespace AdminPanel
{
    public class Mod : IMod
    {
        public string Id => "admin-panel";
        public string Name => "Admin Panel";
        public string Version => "1.1.0";

        #region Constants

        private const int SliderHeight = 22;

        private static readonly int[] NormalRespawnSeconds = { 1, 2, 3, 5, 10, 15, 20, 30, 45 };
        private static readonly int[] BossRespawnSeconds = { 2, 5, 7, 10, 20, 30, 45, 60, 90 };
        private const int NormalDefaultIndex = 4; // 10s
        private const int BossDefaultIndex = 4;   // 20s

        #endregion

        #region Instance State

        private ILogger _log;
        private ModContext _context;
        private bool _enabled;
        private bool _singleplayerOnly;

        private Action _pendingAction;
        private DraggablePanel _panel = new DraggablePanel("admin-panel", "Admin Panel", 380, 620);
        private Slider _timeSlider = new Slider();
        private Slider _normalRespawnSlider = new Slider();
        private Slider _bossRespawnSlider = new Slider();
        private Slider _moveSpeedSlider = new Slider();

        private int _normalRespawnIndex = NormalDefaultIndex;
        private int _bossRespawnIndex = BossDefaultIndex;

        // Tab state
        private int _activeTab;
        private static readonly string[] TabNames = { "Main", "Bosses", "NPCs" };
        private const int TabBarHeight = 30;

        // Previous values for dirty detection (avoid saving every frame during drag)
        private bool _prevGodMode;
        private int _prevTimeSpeed = 1;
        private int _prevNormalRespawnIndex = NormalDefaultIndex;
        private int _prevBossRespawnIndex = BossDefaultIndex;
        private int _prevMoveSpeed = 1;
        private bool _prevBiomeSpread;
        private bool _prevRightClickSpawn;
        private string _prevBossFavs = "";
        private string _prevNpcFavs = "";

        #endregion

        #region Static State (for Harmony patches)

        private static bool _godModeActive;
        private static int _timeSpeedMultiplier = 1;
        private static float _normalRespawnMult = 1.0f;
        private static float _bossRespawnMult = 1.0f;
        private static bool _inBossFight;
        private static int _moveSpeedMultiplier = 1;
        private static bool _biomeSpreadDisabled;

        // Biome spread reflection
        private static FieldInfo _allowedToSpreadInfectionsField;

        private static Harmony _harmony;
        private static Timer _patchTimer;
        private static readonly object _patchLock = new object();
        private static bool _patchesApplied;

        #endregion

        #region Reflection Cache

        private static Type _mainType;
        private static Type _playerType;
        private static Type _vector2Type;

        // Main fields
        private static FieldInfo _netModeField;
        private static FieldInfo _myPlayerField;
        private static FieldInfo _playerArrayField;
        private static FieldInfo _dayTimeField;
        private static FieldInfo _timeField;
        private static FieldInfo _dayRateField;
        private static FieldInfo _spawnTileXField;
        private static FieldInfo _spawnTileYField;
        private static FieldInfo _dungeonXField;
        private static FieldInfo _dungeonYField;
        private static FieldInfo _maxTilesXField;
        private static FieldInfo _maxTilesYField;
        private static FieldInfo _worldSurfaceField;

        // Player fields
        private static FieldInfo _statLifeField;
        private static FieldInfo _statLifeMax2Field;
        private static FieldInfo _statManaField;
        private static FieldInfo _statManaMax2Field;
        private static FieldInfo _spawnXField;
        private static FieldInfo _spawnYField;
        private static FieldInfo _immuneField;
        private static FieldInfo _immuneTimeField;
        private static FieldInfo _immuneNoBlink;
        private static FieldInfo _respawnTimerField;
        private static FieldInfo _positionField;
        private static PropertyInfo _playerCenterProp;
        private static MethodInfo _teleportMethod;

        // Movement speed fields
        private static FieldInfo _maxRunSpeedField;
        private static FieldInfo _runAccelerationField;

        // Vanilla teleport methods (shellphone-matching destinations)
        private static MethodInfo _shellphoneSpawnMethod;
        private static MethodInfo _magicConchMethod;
        private static MethodInfo _demonConchMethod;
        private static MethodInfo _teleportationPotionMethod;

        // Vector2 fields
        private static FieldInfo _vector2XField;
        private static FieldInfo _vector2YField;

        // NPC fields (for boss detection)
        private static FieldInfo _npcArrayField;
        private static FieldInfo _npcActiveField;
        private static FieldInfo _npcBossField;
        private static FieldInfo _npcTypeField;
        private static PropertyInfo _npcCenterProp;

        #endregion

        #region IMod Implementation

        public void Initialize(ModContext context)
        {
            _log = context.Logger;
            _context = context;

            _enabled = context.Config.Get<bool>("enabled", true);
            _singleplayerOnly = context.Config.Get<bool>("singleplayerOnly", true);

            if (!_enabled)
            {
                _log.Info("AdminPanel is disabled in config");
                return;
            }

            InitReflection();
            LoadSettings();

            context.RegisterKeybind("toggle", "Toggle Panel", "Open/close admin panel", "OemBackslash", OnToggleUI);
            context.RegisterKeybind("god-mode", "Toggle God Mode", "Toggle invincibility", "F9", OnToggleGodMode);

            _panel.RegisterDrawCallback(OnDraw);
            FrameEvents.OnPreUpdate += ExecutePendingAction;
            FrameEvents.OnPreUpdate += UpdateNPCSpawner;

            _harmony = new Harmony("com.terrariamodder.adminpanel");
            _patchTimer = new Timer(ApplyPatches, null, 5000, Timeout.Infinite);

            _log.Info("AdminPanel initialized - Press \\ to open panel, F9 for god mode");
        }

        public void OnWorldLoad()
        {
            _inBossFight = false;
            if (!_enabled) return;
            // Ensure patches are applied (timer may not have fired yet)
            if (!_patchesApplied) ApplyPatches(null);
        }

        public void OnWorldUnload()
        {
            _panel.Close();
            _inBossFight = false;
            // Reset game state but keep settings
            try { _dayRateField?.SetValue(null, 1); } catch { }
            // Restore biome spread for save safety
            try { _allowedToSpreadInfectionsField?.SetValue(null, true); } catch { }
        }

        public void Unload()
        {
            _patchTimer?.Dispose();
            _patchTimer = null;
            FrameEvents.OnPreUpdate -= ExecutePendingAction;
            FrameEvents.OnPreUpdate -= UpdateNPCSpawner;
            _pendingAction = null;
            _panel.UnregisterDrawCallback();
            _panel.Close();
            _harmony?.UnpatchAll("com.terrariamodder.adminpanel");
            _patchesApplied = false;
            _godModeActive = false;
            _timeSpeedMultiplier = 1;
            _moveSpeedMultiplier = 1;
            _biomeSpreadDisabled = false;
            // Restore biome spread on unload
            try { _allowedToSpreadInfectionsField?.SetValue(null, true); } catch { }
            try { _dayRateField?.SetValue(null, 1); } catch { }
            NPCSpawner.Unload();
            ClearReflectionCache();
            _log.Info("AdminPanel unloaded");
        }

        #endregion

        #region Initialization

        private void LoadSettings()
        {
            try
            {
                _godModeActive = _context.Config.Get<bool>("godMode", false);
                _timeSpeedMultiplier = _context.Config.Get<int>("timeSpeed", 1);
                _normalRespawnIndex = _context.Config.Get<int>("normalRespawnIndex", NormalDefaultIndex);
                _bossRespawnIndex = _context.Config.Get<int>("bossRespawnIndex", BossDefaultIndex);
                _moveSpeedMultiplier = _context.Config.Get<int>("moveSpeed", 1);

                // Clamp to valid ranges
                _timeSpeedMultiplier = Math.Max(1, Math.Min(60, _timeSpeedMultiplier));
                _normalRespawnIndex = Math.Max(0, Math.Min(NormalRespawnSeconds.Length - 1, _normalRespawnIndex));
                _bossRespawnIndex = Math.Max(0, Math.Min(BossRespawnSeconds.Length - 1, _bossRespawnIndex));
                _moveSpeedMultiplier = Math.Max(1, Math.Min(10, _moveSpeedMultiplier));

                // Derive respawn multipliers
                _normalRespawnMult = NormalRespawnSeconds[_normalRespawnIndex] / 10f;
                _bossRespawnMult = BossRespawnSeconds[_bossRespawnIndex] / 20f;

                // Sync prev values
                _prevGodMode = _godModeActive;
                _prevTimeSpeed = _timeSpeedMultiplier;
                _prevNormalRespawnIndex = _normalRespawnIndex;
                _prevBossRespawnIndex = _bossRespawnIndex;
                _prevMoveSpeed = _moveSpeedMultiplier;

                // Biome spread
                _biomeSpreadDisabled = _context.Config.Get<bool>("biomeSpreadDisabled", false);
                _prevBiomeSpread = _biomeSpreadDisabled;

                // NPC Spawner favourites
                string bossFavs = _context.Config.Get<string>("bossFavourites", "");
                string npcFavs = _context.Config.Get<string>("npcFavourites", "");
                _prevBossFavs = bossFavs;
                _prevNpcFavs = npcFavs;
                NPCSpawner.LoadFavourites(bossFavs, npcFavs);

                bool rightClickSpawn = _context.Config.Get<bool>("rightClickSpawn", false);
                _prevRightClickSpawn = rightClickSpawn;
                NPCSpawner.LoadRightClickSpawn(rightClickSpawn);

                _log.Info($"Settings loaded - god:{_godModeActive} time:{_timeSpeedMultiplier}x respawn:{NormalRespawnSeconds[_normalRespawnIndex]}s/{BossRespawnSeconds[_bossRespawnIndex]}s move:{_moveSpeedMultiplier}x biomeSpread:{(_biomeSpreadDisabled ? "blocked" : "normal")}");
            }
            catch (Exception ex)
            {
                _log.Error($"Failed to load settings: {ex.Message}");
            }
        }

        private void SaveSettingIfChanged<T>(string key, T current, ref T previous) where T : IEquatable<T>
        {
            if (!current.Equals(previous))
            {
                previous = current;
                try
                {
                    _context.Config.Set<T>(key, current);
                    _context.Config.Save();
                }
                catch { }
            }
        }

        private void InitReflection()
        {
            try
            {
                _mainType = Type.GetType("Terraria.Main, Terraria")
                    ?? Assembly.Load("Terraria").GetType("Terraria.Main");
                _playerType = Type.GetType("Terraria.Player, Terraria")
                    ?? Assembly.Load("Terraria").GetType("Terraria.Player");

                if (_mainType != null)
                {
                    _netModeField = _mainType.GetField("netMode", BindingFlags.Public | BindingFlags.Static);
                    _myPlayerField = _mainType.GetField("myPlayer", BindingFlags.Public | BindingFlags.Static);
                    _playerArrayField = _mainType.GetField("player", BindingFlags.Public | BindingFlags.Static);
                    _dayTimeField = _mainType.GetField("dayTime", BindingFlags.Public | BindingFlags.Static);
                    _timeField = _mainType.GetField("time", BindingFlags.Public | BindingFlags.Static);
                    _dayRateField = _mainType.GetField("dayRate", BindingFlags.Public | BindingFlags.Static);
                    _spawnTileXField = _mainType.GetField("spawnTileX", BindingFlags.Public | BindingFlags.Static);
                    _spawnTileYField = _mainType.GetField("spawnTileY", BindingFlags.Public | BindingFlags.Static);
                    _dungeonXField = _mainType.GetField("dungeonX", BindingFlags.Public | BindingFlags.Static);
                    _dungeonYField = _mainType.GetField("dungeonY", BindingFlags.Public | BindingFlags.Static);
                    _maxTilesXField = _mainType.GetField("maxTilesX", BindingFlags.Public | BindingFlags.Static);
                    _maxTilesYField = _mainType.GetField("maxTilesY", BindingFlags.Public | BindingFlags.Static);
                    _worldSurfaceField = _mainType.GetField("worldSurface", BindingFlags.Public | BindingFlags.Static);
                }

                if (_playerType != null)
                {
                    _statLifeField = _playerType.GetField("statLife", BindingFlags.Public | BindingFlags.Instance);
                    _statLifeMax2Field = _playerType.GetField("statLifeMax2", BindingFlags.Public | BindingFlags.Instance);
                    _statManaField = _playerType.GetField("statMana", BindingFlags.Public | BindingFlags.Instance);
                    _statManaMax2Field = _playerType.GetField("statManaMax2", BindingFlags.Public | BindingFlags.Instance);
                    _spawnXField = _playerType.GetField("SpawnX", BindingFlags.Public | BindingFlags.Instance);
                    _spawnYField = _playerType.GetField("SpawnY", BindingFlags.Public | BindingFlags.Instance);
                    _immuneField = _playerType.GetField("immune", BindingFlags.Public | BindingFlags.Instance);
                    _immuneTimeField = _playerType.GetField("immuneTime", BindingFlags.Public | BindingFlags.Instance);
                    _immuneNoBlink = _playerType.GetField("immuneNoBlink", BindingFlags.Public | BindingFlags.Instance);
                    _respawnTimerField = _playerType.GetField("respawnTimer", BindingFlags.Public | BindingFlags.Instance);
                    _positionField = _playerType.GetField("position", BindingFlags.Public | BindingFlags.Instance);
                    _playerCenterProp = _playerType.GetProperty("Center", BindingFlags.Public | BindingFlags.Instance);

                    // Movement speed fields
                    _maxRunSpeedField = _playerType.GetField("maxRunSpeed", BindingFlags.Public | BindingFlags.Instance);
                    _runAccelerationField = _playerType.GetField("runAcceleration", BindingFlags.Public | BindingFlags.Instance);

                    // Find Player.Teleport(Vector2, int, int)
                    foreach (var method in _playerType.GetMethods(BindingFlags.Public | BindingFlags.Instance))
                    {
                        if (method.Name == "Teleport")
                        {
                            var parms = method.GetParameters();
                            if (parms.Length >= 1 && parms[0].ParameterType.Name == "Vector2")
                            {
                                _teleportMethod = method;
                                _vector2Type = parms[0].ParameterType;
                                _vector2XField = _vector2Type.GetField("X");
                                _vector2YField = _vector2Type.GetField("Y");
                                break;
                            }
                        }
                    }

                    // Vanilla teleport methods (exact shellphone behavior)
                    _shellphoneSpawnMethod = _playerType.GetMethod("Shellphone_Spawn", BindingFlags.Public | BindingFlags.Instance);
                    _magicConchMethod = _playerType.GetMethod("MagicConch", BindingFlags.Public | BindingFlags.Instance);
                    _demonConchMethod = _playerType.GetMethod("DemonConch", BindingFlags.Public | BindingFlags.Instance);
                    _teleportationPotionMethod = _playerType.GetMethod("TeleportationPotion", BindingFlags.Public | BindingFlags.Instance);
                }

                // NPC fields for boss detection
                var npcType = _mainType?.Assembly.GetType("Terraria.NPC");
                if (npcType != null)
                {
                    _npcArrayField = _mainType.GetField("npc", BindingFlags.Public | BindingFlags.Static);
                    _npcActiveField = npcType.GetField("active", BindingFlags.Public | BindingFlags.Instance);
                    _npcBossField = npcType.GetField("boss", BindingFlags.Public | BindingFlags.Instance);
                    _npcTypeField = npcType.GetField("type", BindingFlags.Public | BindingFlags.Instance);
                    _npcCenterProp = npcType.GetProperty("Center", BindingFlags.Public | BindingFlags.Instance);
                }

                // Biome spread control
                var worldGenType = _mainType?.Assembly.GetType("Terraria.WorldGen");
                if (worldGenType != null)
                    _allowedToSpreadInfectionsField = worldGenType.GetField("AllowedToSpreadInfections", BindingFlags.Public | BindingFlags.Static);

                // Initialize NPC spawner
                NPCSpawner.Init(_log, _mainType, _playerType, _vector2Type,
                    _vector2XField, _vector2YField, _myPlayerField, _playerArrayField, _playerCenterProp);

                _log.Debug("Reflection initialized");
            }
            catch (Exception ex)
            {
                _log.Error($"Reflection init error: {ex.Message}");
            }
        }

        private void ClearReflectionCache()
        {
            _mainType = null;
            _playerType = null;
            _vector2Type = null;
            _netModeField = null;
            _myPlayerField = null;
            _playerArrayField = null;
            _dayTimeField = null;
            _timeField = null;
            _dayRateField = null;
            _spawnTileXField = null;
            _spawnTileYField = null;
            _dungeonXField = null;
            _dungeonYField = null;
            _maxTilesXField = null;
            _maxTilesYField = null;
            _worldSurfaceField = null;
            _statLifeField = null;
            _statLifeMax2Field = null;
            _statManaField = null;
            _statManaMax2Field = null;
            _spawnXField = null;
            _spawnYField = null;
            _immuneField = null;
            _immuneTimeField = null;
            _immuneNoBlink = null;
            _respawnTimerField = null;
            _teleportMethod = null;
            _vector2XField = null;
            _vector2YField = null;
            _positionField = null;
            _playerCenterProp = null;
            _maxRunSpeedField = null;
            _runAccelerationField = null;
            _shellphoneSpawnMethod = null;
            _magicConchMethod = null;
            _demonConchMethod = null;
            _teleportationPotionMethod = null;
            _npcArrayField = null;
            _npcActiveField = null;
            _npcBossField = null;
            _npcTypeField = null;
            _npcCenterProp = null;
            _allowedToSpreadInfectionsField = null;
        }

        #endregion

        #region Harmony Patches

        private void ApplyPatches(object state)
        {
            lock (_patchLock)
            {
                if (_patchesApplied) return;
                _patchesApplied = true;
            }

            // Guard against race with Unload clearing these
            if (_harmony == null || _playerType == null || _mainType == null) return;

            try
            {
                // Player.ResetEffects postfix - god mode immunity each frame
                var resetEffectsMethod = _playerType.GetMethod("ResetEffects", BindingFlags.Public | BindingFlags.Instance);
                if (resetEffectsMethod != null)
                {
                    var postfix = typeof(Mod).GetMethod(nameof(ResetEffects_Postfix), BindingFlags.NonPublic | BindingFlags.Static);
                    _harmony.Patch(resetEffectsMethod, postfix: new HarmonyMethod(postfix));
                    _log.Debug("Patched Player.ResetEffects for god mode");
                }

                // Player.UpdateDead postfix - custom respawn times
                var updateDeadMethod = _playerType.GetMethod("UpdateDead", BindingFlags.Public | BindingFlags.Instance);
                if (updateDeadMethod != null)
                {
                    var postfix = typeof(Mod).GetMethod(nameof(UpdateDead_Postfix), BindingFlags.NonPublic | BindingFlags.Static);
                    _harmony.Patch(updateDeadMethod, postfix: new HarmonyMethod(postfix));
                    _log.Debug("Patched Player.UpdateDead for respawn time");
                }

                // Main.UpdateTimeRate postfix - time speed multiplier
                var updateTimeRateMethod = _mainType.GetMethod("UpdateTimeRate", BindingFlags.Public | BindingFlags.Static);
                if (updateTimeRateMethod != null)
                {
                    var postfix = typeof(Mod).GetMethod(nameof(UpdateTimeRate_Postfix), BindingFlags.NonPublic | BindingFlags.Static);
                    _harmony.Patch(updateTimeRateMethod, postfix: new HarmonyMethod(postfix));
                    _log.Debug("Patched Main.UpdateTimeRate for time speed");
                }

                // Player.HorizontalMovement prefix - movement speed multiplier
                var horizontalMovementMethod = _playerType.GetMethod("HorizontalMovement", BindingFlags.Public | BindingFlags.Instance);
                if (horizontalMovementMethod != null)
                {
                    var prefix = typeof(Mod).GetMethod(nameof(HorizontalMovement_Prefix), BindingFlags.NonPublic | BindingFlags.Static);
                    _harmony.Patch(horizontalMovementMethod, prefix: new HarmonyMethod(prefix));
                    _log.Debug("Patched Player.HorizontalMovement for movement speed");
                }

                // WorldGen.hardUpdateWorld prefix - biome spread disable
                var worldGenType = _mainType.Assembly.GetType("Terraria.WorldGen");
                if (worldGenType != null)
                {
                    var hardUpdateMethod = worldGenType.GetMethod("hardUpdateWorld", BindingFlags.Public | BindingFlags.Static);
                    if (hardUpdateMethod != null)
                    {
                        var prefix = typeof(Mod).GetMethod(nameof(HardUpdateWorld_Prefix), BindingFlags.NonPublic | BindingFlags.Static);
                        _harmony.Patch(hardUpdateMethod, prefix: new HarmonyMethod(prefix));
                        _log.Debug("Patched WorldGen.hardUpdateWorld for biome spread control");
                    }
                }
            }
            catch (Exception ex)
            {
                _log.Error($"Harmony patch error: {ex.Message}");
            }
        }

        private static void ResetEffects_Postfix(object __instance)
        {
            if (!_godModeActive) return;

            try
            {
                var localPlayer = GetLocalPlayer();
                if (__instance == localPlayer)
                {
                    _immuneField?.SetValue(__instance, true);
                    _immuneTimeField?.SetValue(__instance, 2);
                    _immuneNoBlink?.SetValue(__instance, true);
                }
            }
            catch { }
        }

        private static void UpdateDead_Postfix(object __instance)
        {
            try
            {
                var localPlayer = GetLocalPlayer();
                if (__instance != localPlayer) return;

                _inBossFight = DetectBossFight(__instance);

                float mult = _inBossFight ? _bossRespawnMult : _normalRespawnMult;
                if (mult >= 1.0f) return;

                int currentTimer = (int)_respawnTimerField.GetValue(__instance);
                if (currentTimer > 0)
                {
                    int extraReduction = (int)((1.0f / mult) - 1);
                    if (extraReduction > 0)
                    {
                        _respawnTimerField.SetValue(__instance, Math.Max(0, currentTimer - extraReduction));
                    }
                }
            }
            catch { }
        }

        /// <summary>
        /// Postfix for Main.UpdateTimeRate - applies our speed multiplier after vanilla
        /// sets dayRate. This fixes the bug where UpdateTimeRate overwrites our dayRate
        /// value every frame.
        /// </summary>
        private static void UpdateTimeRate_Postfix()
        {
            if (_timeSpeedMultiplier <= 1) return;

            try
            {
                int current = (int)_dayRateField.GetValue(null);
                if (current > 0) // Don't multiply if frozen (dayRate=0)
                {
                    _dayRateField.SetValue(null, current * _timeSpeedMultiplier);
                }
            }
            catch { }
        }

        /// <summary>
        /// Prefix for Player.HorizontalMovement - multiplies movement speed fields
        /// after all equipment/buff effects have been applied.
        /// </summary>
        private static void HorizontalMovement_Prefix(object __instance)
        {
            if (_moveSpeedMultiplier <= 1) return;

            try
            {
                var localPlayer = GetLocalPlayer();
                if (__instance != localPlayer) return;

                float maxRun = (float)_maxRunSpeedField.GetValue(__instance);
                float runAccel = (float)_runAccelerationField.GetValue(__instance);
                _maxRunSpeedField.SetValue(__instance, maxRun * _moveSpeedMultiplier);
                _runAccelerationField.SetValue(__instance, runAccel * _moveSpeedMultiplier);
            }
            catch { }
        }

        /// <summary>
        /// Prefix for WorldGen.hardUpdateWorld - blocks biome spread when toggle is on.
        /// Also sets AllowedToSpreadInfections to false for grass growth methods.
        /// </summary>
        private static bool HardUpdateWorld_Prefix()
        {
            if (!_biomeSpreadDisabled) return true;

            // Also suppress the AllowedToSpreadInfections flag for grass-related spread
            try { _allowedToSpreadInfectionsField?.SetValue(null, false); } catch { }
            return false; // Skip vanilla hardUpdateWorld entirely
        }

        private static object GetLocalPlayer()
        {
            int myPlayer = (int)_myPlayerField.GetValue(null);
            var players = (Array)_playerArrayField.GetValue(null);
            return players.GetValue(myPlayer);
        }

        private static bool DetectBossFight(object player)
        {
            if (_npcArrayField == null || _playerCenterProp == null) return false;

            var playerCenter = _playerCenterProp.GetValue(player);
            float playerX = (float)_vector2XField.GetValue(playerCenter);
            float playerY = (float)_vector2YField.GetValue(playerCenter);

            var npcs = (Array)_npcArrayField.GetValue(null);
            for (int i = 0; i < Math.Min(npcs.Length, 200); i++)
            {
                var npc = npcs.GetValue(i);
                if (npc == null) continue;

                bool active = (bool)_npcActiveField.GetValue(npc);
                if (!active) continue;

                bool isBoss = (bool)_npcBossField.GetValue(npc);
                int npcTypeId = (int)_npcTypeField.GetValue(npc);

                if ((isBoss || npcTypeId == 13 || npcTypeId == 14 || npcTypeId == 15) && npcTypeId != 395)
                {
                    var npcCenter = _npcCenterProp.GetValue(npc);
                    float npcX = (float)_vector2XField.GetValue(npcCenter);
                    float npcY = (float)_vector2YField.GetValue(npcCenter);

                    if (Math.Abs(playerX - npcX) + Math.Abs(playerY - npcY) < 4000f)
                        return true;
                }
            }
            return false;
        }

        #endregion

        #region Pending Action Queue

        private void ExecutePendingAction()
        {
            var action = _pendingAction;
            _pendingAction = null;
            action?.Invoke();
        }

        private void UpdateNPCSpawner()
        {
            NPCSpawner.Update(_panel.IsOpen);
            SaveFavouritesIfChanged();
        }

        private void SaveFavouritesIfChanged()
        {
            try
            {
                string bossFavs = NPCSpawner.SaveBossFavourites();
                if (bossFavs != _prevBossFavs)
                {
                    _prevBossFavs = bossFavs;
                    _context.Config.Set("bossFavourites", bossFavs);
                    _context.Config.Save();
                }

                string npcFavs = NPCSpawner.SaveNPCFavourites();
                if (npcFavs != _prevNpcFavs)
                {
                    _prevNpcFavs = npcFavs;
                    _context.Config.Set("npcFavourites", npcFavs);
                    _context.Config.Save();
                }

                bool rcs = NPCSpawner.RightClickSpawnEnabled;
                if (rcs != _prevRightClickSpawn)
                {
                    _prevRightClickSpawn = rcs;
                    _context.Config.Set("rightClickSpawn", rcs);
                    _context.Config.Save();
                }
            }
            catch { }
        }

        #endregion

        #region Keybind Handlers

        private void OnToggleUI()
        {
            if (_singleplayerOnly && IsMultiplayer())
            {
                _log.Warn("AdminPanel is disabled in multiplayer");
                return;
            }

            _panel.Toggle();
        }

        private void OnToggleGodMode()
        {
            if (_singleplayerOnly && IsMultiplayer())
            {
                _log.Warn("God mode is disabled in multiplayer");
                return;
            }

            _godModeActive = !_godModeActive;
            _log.Info($"God mode: {(_godModeActive ? "ON" : "OFF")}");

            try
            {
                var player = GetLocalPlayer();
                _immuneField?.SetValue(player, _godModeActive);
                _immuneTimeField?.SetValue(player, _godModeActive ? 2 : 0);
                _immuneNoBlink?.SetValue(player, _godModeActive);
            }
            catch (Exception ex)
            {
                _log.Error($"Failed to toggle god mode: {ex.Message}");
            }

            SaveSettingIfChanged("godMode", _godModeActive, ref _prevGodMode);
        }

        private bool IsMultiplayer()
        {
            try { return (int)_netModeField.GetValue(null) != 0; }
            catch { return false; }
        }

        #endregion

        #region UI Drawing

        private void OnDraw()
        {
            if (!_panel.BeginDraw()) return;
            try
            {
                // Tab bar at top of content area
                int tabY = _panel.ContentY;
                var newTab = TabBar.Draw(_panel.X, tabY, _panel.Width, TabNames, _activeTab, TabBarHeight);
                if (newTab != _activeTab)
                    _activeTab = newTab;

                int contentY = tabY + TabBarHeight + 5;
                var s = new StackLayout(_panel.ContentX, contentY, _panel.ContentWidth);

                switch (_activeTab)
                {
                    case 0: DrawMainTab(ref s); break;
                    case 1: NPCSpawner.DrawBossTab(ref s); break;
                    case 2: NPCSpawner.DrawNPCTab(ref s); break;
                }

                // Status line at bottom (always visible)
                string status = _godModeActive ? "God Mode: ACTIVE" : "God Mode: OFF";
                if (_moveSpeedMultiplier > 1) status += $"  |  Speed: {_moveSpeedMultiplier}x";
                UIRenderer.DrawText(status,
                    _panel.ContentX, _panel.Y + _panel.Height - 25,
                    _godModeActive ? UIColors.Success : UIColors.TextDim);
            }
            catch (Exception ex)
            {
                _log.Error($"Draw error: {ex.Message}");
            }
            finally
            {
                _panel.EndDraw();
            }
        }

        private void DrawMainTab(ref StackLayout s)
        {
            // ---- PLAYER ----
            s.SectionHeader("PLAYER");
            if (s.Toggle("God Mode", _godModeActive)) OnToggleGodMode();

            int hw = (s.Width - 8) / 2;
            if (s.ButtonAt(s.X, hw, "Full Health")) RestoreHealth();
            if (s.ButtonAt(s.X + hw + 8, hw, "Full Mana")) RestoreMana();
            s.Advance(26);

            // ---- MOVEMENT ----
            s.SectionHeader("MOVEMENT");
            int labelW = 100;
            int sy = s.Advance(SliderHeight);
            UIRenderer.DrawText("Speed:", s.X, sy + 2, UIColors.TextDim);
            _moveSpeedMultiplier = _moveSpeedSlider.Draw(s.X + 50, sy, s.Width - 50 - labelW, SliderHeight,
                _moveSpeedMultiplier, 1, 10);
            string moveLabel = _moveSpeedMultiplier == 1 ? "1x (normal)" : $"{_moveSpeedMultiplier}x";
            var moveLabelColor = _moveSpeedMultiplier == 1 ? UIColors.TextHint : UIColors.AccentText;
            UIRenderer.DrawText(moveLabel, s.X + s.Width - UIRenderer.MeasureText(moveLabel), sy + 2, moveLabelColor);
            SaveSettingIfChanged("moveSpeed", _moveSpeedMultiplier, ref _prevMoveSpeed);

            // ---- TIME ----
            s.SectionHeader("TIME");
            int qw = (s.Width - 24) / 4;
            if (s.ButtonAt(s.X, qw, "Dawn")) SetTime(true, 0);
            if (s.ButtonAt(s.X + qw + 8, qw, "Noon")) SetTime(true, 27000);
            if (s.ButtonAt(s.X + (qw + 8) * 2, qw, "Dusk")) SetTime(false, 0);
            if (s.ButtonAt(s.X + (qw + 8) * 3, qw, "Night")) SetTime(false, 16200);
            s.Advance(26);

            sy = s.Advance(SliderHeight);
            UIRenderer.DrawText("Speed:", s.X, sy + 2, UIColors.TextDim);
            _timeSpeedMultiplier = _timeSlider.Draw(s.X + 50, sy, s.Width - 50 - labelW, SliderHeight,
                _timeSpeedMultiplier, 1, 60);
            string timeLabel = _timeSpeedMultiplier == 1 ? "1x (normal)" : $"{_timeSpeedMultiplier}x";
            var timeLabelColor = _timeSpeedMultiplier == 1 ? UIColors.TextHint : UIColors.AccentText;
            UIRenderer.DrawText(timeLabel, s.X + s.Width - UIRenderer.MeasureText(timeLabel), sy + 2, timeLabelColor);
            SaveSettingIfChanged("timeSpeed", _timeSpeedMultiplier, ref _prevTimeSpeed);

            // ---- TELEPORT ----
            s.SectionHeader("TELEPORT");
            qw = (s.Width - 24) / 4;
            // Queue teleports for Update phase — executing during Draw gets
            // rolled back by FpsUnlocked's position save/restore interpolation.
            if (s.ButtonAt(s.X, qw, "Spawn")) _pendingAction = TeleportToSpawn;
            if (s.ButtonAt(s.X + qw + 8, qw, "Dungeon")) _pendingAction = TeleportToDungeon;
            if (s.ButtonAt(s.X + (qw + 8) * 2, qw, "Hell")) _pendingAction = TeleportToHell;
            if (s.ButtonAt(s.X + (qw + 8) * 3, qw, "Beach")) _pendingAction = TeleportToBeach;
            s.Advance(26);
            if (s.ButtonAt(s.X, hw, "Bed")) _pendingAction = TeleportToBed;
            if (s.ButtonAt(s.X + hw + 8, hw, "Random")) _pendingAction = TeleportRandom;
            s.Advance(26);

            // ---- RESPAWN ----
            s.SectionHeader("RESPAWN");
            int sliderX = 60;
            int sliderW = s.Width - sliderX - labelW;

            sy = s.Advance(SliderHeight);
            UIRenderer.DrawText("Normal:", s.X, sy + 3, UIColors.TextDim);
            _normalRespawnIndex = _normalRespawnSlider.Draw(s.X + sliderX, sy, sliderW, SliderHeight,
                _normalRespawnIndex, 0, NormalRespawnSeconds.Length - 1);
            _normalRespawnMult = NormalRespawnSeconds[_normalRespawnIndex] / 10f;
            bool normalDefault = _normalRespawnIndex == NormalDefaultIndex;
            string normalLabel = FormatRespawnLabel(NormalRespawnSeconds[_normalRespawnIndex], normalDefault);
            UIRenderer.DrawText(normalLabel, s.X + s.Width - UIRenderer.MeasureText(normalLabel),
                sy + 3, normalDefault ? UIColors.TextHint : UIColors.AccentText);
            SaveSettingIfChanged("normalRespawnIndex", _normalRespawnIndex, ref _prevNormalRespawnIndex);

            sy = s.Advance(SliderHeight);
            UIRenderer.DrawText("Boss:", s.X, sy + 3, UIColors.TextDim);
            _bossRespawnIndex = _bossRespawnSlider.Draw(s.X + sliderX, sy, sliderW, SliderHeight,
                _bossRespawnIndex, 0, BossRespawnSeconds.Length - 1);
            _bossRespawnMult = BossRespawnSeconds[_bossRespawnIndex] / 20f;
            bool bossDefault = _bossRespawnIndex == BossDefaultIndex;
            string bossLabel = FormatRespawnLabel(BossRespawnSeconds[_bossRespawnIndex], bossDefault);
            if (_inBossFight && !bossDefault) bossLabel += "*";
            var bossLabelColor = _inBossFight ? UIColors.Warning : (bossDefault ? UIColors.TextHint : UIColors.AccentText);
            UIRenderer.DrawText(bossLabel, s.X + s.Width - UIRenderer.MeasureText(bossLabel), sy + 3, bossLabelColor);
            SaveSettingIfChanged("bossRespawnIndex", _bossRespawnIndex, ref _prevBossRespawnIndex);

            if (s.ButtonAt(s.X, hw, "Instant Respawn")) InstantRespawn();
            s.Advance(26);

            // ---- WORLD ----
            s.SectionHeader("WORLD");
            if (s.Toggle("Disable Biome Spread", _biomeSpreadDisabled))
            {
                _biomeSpreadDisabled = !_biomeSpreadDisabled;
                // Restore AllowedToSpreadInfections when re-enabling
                if (!_biomeSpreadDisabled)
                {
                    try { _allowedToSpreadInfectionsField?.SetValue(null, true); } catch { }
                }
                _log.Info($"Biome spread: {(_biomeSpreadDisabled ? "DISABLED" : "enabled")}");
                SaveSettingIfChanged("biomeSpreadDisabled", _biomeSpreadDisabled, ref _prevBiomeSpread);
            }
        }

        private string FormatRespawnLabel(int seconds, bool isDefault)
        {
            return isDefault ? $"{seconds}s (default)" : $"{seconds}s";
        }

        #endregion

        #region Game Actions

        private void RestoreHealth()
        {
            try
            {
                var player = GetLocalPlayer();
                int max = (int)_statLifeMax2Field.GetValue(player);
                _statLifeField.SetValue(player, max);
                _log.Info($"Health restored to {max}");
            }
            catch (Exception ex) { _log.Error($"Failed to restore health: {ex.Message}"); }
        }

        private void RestoreMana()
        {
            try
            {
                var player = GetLocalPlayer();
                int max = (int)_statManaMax2Field.GetValue(player);
                _statManaField.SetValue(player, max);
                _log.Info($"Mana restored to {max}");
            }
            catch (Exception ex) { _log.Error($"Failed to restore mana: {ex.Message}"); }
        }

        private void SetTime(bool dayTime, double time)
        {
            try
            {
                _dayTimeField.SetValue(null, dayTime);
                _timeField.SetValue(null, time);
                _log.Info($"Time set to {(dayTime ? "day" : "night")} {time}");
            }
            catch (Exception ex) { _log.Error($"Failed to set time: {ex.Message}"); }
        }

        private void InstantRespawn()
        {
            try
            {
                _respawnTimerField?.SetValue(GetLocalPlayer(), 0);
                _log.Info("Respawn timer set to 0");
            }
            catch (Exception ex) { _log.Error($"Failed to instant respawn: {ex.Message}"); }
        }

        #endregion

        #region Teleportation

        private void TeleportToSpawn()
        {
            try
            {
                var player = GetLocalPlayer();
                if (_shellphoneSpawnMethod != null)
                {
                    _shellphoneSpawnMethod.Invoke(player, null);
                    _log.Info("Teleported to spawn (shellphone)");
                }
                else
                {
                    // Fallback: manual coordinate teleport
                    int tx = (int)_spawnTileXField.GetValue(null);
                    int ty = (int)_spawnTileYField.GetValue(null);
                    TeleportPlayer(tx * 16f + 8f - 10f, ty * 16f - 42f);
                }
            }
            catch (Exception ex) { _log.Error($"Failed to teleport to spawn: {ex.Message}"); }
        }

        private void TeleportToDungeon()
        {
            try
            {
                int dx = (int)_dungeonXField.GetValue(null);
                int dy = (int)_dungeonYField.GetValue(null);
                // Center player on dungeon tile with proper offset
                TeleportPlayer(dx * 16f + 8f - 10f, dy * 16f - 42f);
            }
            catch (Exception ex) { _log.Error($"Failed to teleport to dungeon: {ex.Message}"); }
        }

        private void TeleportToHell()
        {
            try
            {
                var player = GetLocalPlayer();
                if (_demonConchMethod != null)
                {
                    _demonConchMethod.Invoke(player, null);
                    _log.Info("Teleported to hell (demon conch)");
                }
                else
                {
                    // Fallback: manual calculation
                    float px = (_positionField != null && _vector2XField != null)
                        ? (float)_vector2XField.GetValue(_positionField.GetValue(player))
                        : (int)_spawnTileXField.GetValue(null) * 16f;
                    int hellY = (int)_maxTilesYField.GetValue(null) - 200;
                    TeleportPlayer(px, hellY * 16f);
                }
            }
            catch (Exception ex) { _log.Error($"Failed to teleport to hell: {ex.Message}"); }
        }

        private void TeleportToBeach()
        {
            try
            {
                var player = GetLocalPlayer();
                if (_magicConchMethod != null)
                {
                    _magicConchMethod.Invoke(player, null);
                    _log.Info("Teleported to beach (magic conch)");
                }
                else
                {
                    // Fallback: rough beach estimate
                    double surface = (double)_worldSurfaceField.GetValue(null);
                    TeleportPlayer(200 * 16f, ((int)surface - 5) * 16f);
                }
            }
            catch (Exception ex) { _log.Error($"Failed to teleport to beach: {ex.Message}"); }
        }

        private void TeleportToBed()
        {
            try
            {
                var player = GetLocalPlayer();
                int sx = (int)_spawnXField.GetValue(player);
                int sy = (int)_spawnYField.GetValue(player);

                if (sx == -1 || sy == -1)
                {
                    _log.Info("No bed spawn set, teleporting to world spawn");
                    TeleportToSpawn();
                }
                else
                {
                    TeleportPlayer(sx * 16f + 8f - 10f, sy * 16f - 42f);
                    _log.Info($"Teleported to bed ({sx}, {sy})");
                }
            }
            catch (Exception ex) { _log.Error($"Failed to teleport to bed: {ex.Message}"); }
        }

        private void TeleportRandom()
        {
            try
            {
                var player = GetLocalPlayer();
                if (_teleportationPotionMethod != null)
                {
                    _teleportationPotionMethod.Invoke(player, null);
                    _log.Info("Random teleport (teleportation potion)");
                }
                else
                {
                    _log.Warn("TeleportationPotion method not found");
                }
            }
            catch (Exception ex) { _log.Error($"Failed to random teleport: {ex.Message}"); }
        }

        private void TeleportPlayer(float worldX, float worldY)
        {
            try
            {
                var player = GetLocalPlayer();
                if (_teleportMethod != null && _vector2Type != null)
                {
                    var pos = Activator.CreateInstance(_vector2Type, new object[] { worldX, worldY });
                    var parms = _teleportMethod.GetParameters();

                    if (parms.Length == 1) _teleportMethod.Invoke(player, new[] { pos });
                    else if (parms.Length == 2) _teleportMethod.Invoke(player, new object[] { pos, 1 });
                    else if (parms.Length >= 3) _teleportMethod.Invoke(player, new object[] { pos, 1, 0 });

                    _log.Info($"Teleported to ({worldX / 16:F0}, {worldY / 16:F0})");
                }
            }
            catch (Exception ex) { _log.Error($"Failed to teleport: {ex.Message}"); }
        }

        #endregion
    }
}
