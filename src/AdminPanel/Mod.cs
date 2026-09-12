using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Terraria;
using TerrariaModder.Core;
using TerrariaModder.Core.Debug;
using TerrariaModder.Core.Events;
using TerrariaModder.Core.Input;
using TerrariaModder.Core.Logging;
using TerrariaModder.Core.UI;
using TerrariaModder.Core.UI.Widgets;

namespace AdminPanel
{
    public class Mod : IMod, IModLifecycle, IModStateProvider, IModActionProvider
    {
        public string Id => "admin-panel";
        public string Name => "Admin Panel";
        public string Version => "2.0.0";

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
        private AdminPanelConfig _config;

        private Action _pendingAction;
        private DraggablePanel _panel = new DraggablePanel("admin-panel", "Admin Panel", 380, 620);
        private Slider _timeSlider = new Slider();
        private Slider _normalRespawnSlider = new Slider();
        private Slider _bossRespawnSlider = new Slider();
        private Slider _moveSpeedSlider = new Slider();

        private int _normalRespawnIndex = NormalDefaultIndex;
        private int _bossRespawnIndex = BossDefaultIndex;

        // Dungeon coordinates — requested from server in MP since Main.dungeonX/Y are not
        // populated on MP clients (they come from WorldFile.Load, not from network packets).
        private int _dungeonX = -1;
        private int _dungeonY = -1;

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
        private bool _prevRightClickSpawn;
        private string _prevBossFavs = "";
        private string _prevNpcFavs = "";

        #endregion

        #region Static State (for Harmony patches)

        private static bool _godModeActive;
        private static int _timeSpeedMultiplier = 1;
        private static float _normalRespawnMult = 1.0f;
        private static float _bossRespawnMult = 1.0f;
        private static double _respawnTickFraction;
        private static int _lastRespawnTimer;
        private static Player _respawnPlayer;
        private static bool _inBossFight;
        private static int _moveSpeedMultiplier = 1;

        private static Harmony _harmony;
        private static readonly object _patchLock = new object();
        private static bool _patchesApplied;

        #endregion

        #region IMod Implementation

        public void Initialize(ModContext context)
        {
            _log = context.Logger;
            _context = context;
            _config = context.GetConfig<AdminPanelConfig>();

            // No UI on dedicated server — all admin ops are server-authoritative
            if (Environment.GetEnvironmentVariable("TERRARIA_MODDER_DEDSERV") == "1")
            {
                _log.Info("AdminPanel: skipping UI init (dedicated server)");
                return;
            }

            _enabled = _config != null ? _config.Enabled : true;

            if (!_enabled)
            {
                _log.Info("AdminPanel is disabled in config");
                return;
            }

            LoadSettings();

            context.RegisterKeybind("toggle", "Toggle Panel", "Open/close admin panel", "OemBackslash", OnToggleUI);
            context.RegisterKeybind("god-mode", "Toggle God Mode", "Toggle invincibility", "F9", OnToggleGodMode);

            _panel.ClipContent = false; // Content fits within panel; BeginClip causes transform issues
            _panel.RegisterDrawCallback(OnDraw);
            FrameEvents.OnPreUpdate += ExecutePendingAction;
            FrameEvents.OnPreUpdate += UpdateNPCSpawner;

            _harmony = new Harmony("com.terrariamodder.adminpanel");

            NPCSpawner.Init(_log);
            TerrariaModder.Core.Net.NetSync.OnServerCommandResponse += OnServerCommandResponse;

            context.RegisterStateProvider(this);
            context.RegisterActionProvider(this);

            _log.Info("AdminPanel initialized - Press \\ to open panel, F9 for god mode");
        }

        public Dictionary<string, object> GetModState()
        {
            return new Dictionary<string, object>
            {
                ["panelOpen"] = _panel.IsOpen,
                ["godMode"] = _godModeActive,
                ["timeSpeed"] = _timeSpeedMultiplier,
                ["moveSpeed"] = _moveSpeedMultiplier,
            };
        }

        public List<ModActionInfo> GetActions()
        {
            return new List<ModActionInfo>
            {
                new ModActionInfo("toggle_panel", "Open/close the admin panel"),
                new ModActionInfo("toggle_god_mode", "Toggle invincibility"),
                new ModActionInfo("heal", "Restore health to full"),
                new ModActionInfo("restore_mana", "Restore mana to full"),
                new ModActionInfo("instant_respawn", "Set respawn timer to 0"),
                new ModActionInfo("set_time", "Set time of day",
                    new ModActionParam("preset", "string", true, "dawn, noon, dusk, or night")),
                new ModActionInfo("set_time_speed", "Set time speed multiplier",
                    new ModActionParam("value", "int", true, "1-60")),
                new ModActionInfo("set_move_speed", "Set movement speed multiplier",
                    new ModActionParam("value", "int", true, "1-10")),
                new ModActionInfo("teleport", "Teleport to a location",
                    new ModActionParam("target", "string", true, "spawn, dungeon, hell, beach, bed, or random")),
            };
        }

        public ModActionResult ExecuteAction(string name, Dictionary<string, string> args)
        {
            switch (name)
            {
                case "toggle_panel":
                    OnToggleUI();
                    EventLog.Emit("admin-panel", "toggle_panel", $"{{\"open\":{(_panel.IsOpen ? "true" : "false")}}}");
                    return ModActionResult.Ok(_panel.IsOpen ? "Panel opened" : "Panel closed");
                case "toggle_god_mode":
                    OnToggleGodMode();
                    EventLog.Emit("admin-panel", "toggle_god_mode", $"{{\"enabled\":{(_godModeActive ? "true" : "false")}}}");
                    return ModActionResult.Ok(_godModeActive ? "God mode ON" : "God mode OFF");
                case "heal":
                    RestoreHealth();
                    EventLog.Emit("admin-panel", "heal", "{\"action\":\"health_restored\"}");
                    return ModActionResult.Ok("Health restored");
                case "restore_mana":
                    RestoreMana();
                    EventLog.Emit("admin-panel", "restore_mana", "{\"action\":\"mana_restored\"}");
                    return ModActionResult.Ok("Mana restored");
                case "instant_respawn":
                    InstantRespawn();
                    EventLog.Emit("admin-panel", "instant_respawn", "{\"action\":\"respawn_cleared\"}");
                    return ModActionResult.Ok("Respawn timer cleared");
                case "set_time":
                    string preset = args != null && args.ContainsKey("preset") ? args["preset"] : null;
                    if (string.IsNullOrEmpty(preset))
                        return ModActionResult.Fail("Missing 'preset' param (dawn/noon/dusk/night)");
                    if (!SetTimePreset(preset))
                        return ModActionResult.Fail("Permission denied or not in a world");
                    EventLog.Emit("admin-panel", "set_time", $"{{\"preset\":\"{preset}\"}}");
                    return ModActionResult.Ok($"Time set to {preset}");
                case "set_time_speed":
                    if (Main.netMode != 0 && !IsLocalAdmin())
                        return ModActionResult.Fail("Time speed control requires Admin in multiplayer");
                    if (args == null || !args.ContainsKey("value") || !int.TryParse(args["value"], out int ts))
                        return ModActionResult.Fail("Missing or invalid 'value' param (1-60)");
                    _timeSpeedMultiplier = Math.Max(1, Math.Min(60, ts));
                    EventLog.Emit("admin-panel", "set_time_speed", $"{{\"value\":{_timeSpeedMultiplier}}}");
                    return ModActionResult.Ok($"Time speed set to {_timeSpeedMultiplier}x");
                case "set_move_speed":
                    if (args == null || !args.ContainsKey("value") || !int.TryParse(args["value"], out int ms))
                        return ModActionResult.Fail("Missing or invalid 'value' param (1-10)");
                    _moveSpeedMultiplier = Math.Max(1, Math.Min(10, ms));
                    EventLog.Emit("admin-panel", "set_move_speed", $"{{\"value\":{_moveSpeedMultiplier}}}");
                    return ModActionResult.Ok($"Move speed set to {_moveSpeedMultiplier}x");
                case "teleport":
                    string target = args != null && args.ContainsKey("target") ? args["target"] : null;
                    if (string.IsNullOrEmpty(target))
                        return ModActionResult.Fail("Missing 'target' param (spawn/dungeon/hell/beach/bed/random)");
                    try
                    {
                        switch (target.ToLower())
                        {
                            case "spawn": TeleportToSpawn(); break;
                            case "dungeon": TeleportToDungeon(); break;
                            case "hell": TeleportToHell(); break;
                            case "beach": TeleportToBeach(); break;
                            case "bed": TeleportToBed(); break;
                            case "random": TeleportRandom(); break;
                            default: return ModActionResult.Fail($"Unknown teleport target: {target}");
                        }
                    }
                    catch (Exception ex)
                    {
                        return ModActionResult.Fail(ex.Message);
                    }
                    EventLog.Emit("admin-panel", "teleport", $"{{\"target\":\"{target}\"}}");
                    return ModActionResult.Ok($"Teleported to {target}");
                default:
                    return null;
            }
        }

        public void OnContentReady(ModContext context) { ApplyPatches(null); }

        private void OnServerCommandResponse(string type, string result)
        {
            if (type != "worldcoords") return;
            // payload: "dungeonX,dungeonY"
            var parts = result.Split(',');
            if (parts.Length == 2 &&
                int.TryParse(parts[0], out int dx) &&
                int.TryParse(parts[1], out int dy))
            {
                _dungeonX = dx;
                _dungeonY = dy;
                _log.Info($"[AdminPanel] Received dungeon coords: ({dx}, {dy})");

                // If a dungeon teleport was deferred, execute it now
                if (_pendingDungeonTeleport)
                {
                    _pendingDungeonTeleport = false;
                    _pendingAction = DoTeleportToDungeon;
                }
            }
        }

        private bool _pendingDungeonTeleport;

        public void OnConfigChanged()
        {
            if (_enabled) LoadSettings();
        }

        public void OnWorldLoad()
        {
            _respawnPlayer = null;
            _respawnTickFraction = 0;
            _lastRespawnTimer = 0;
            _inBossFight = false;
            if (!_enabled) return;
            // Ensure patches are applied (timer may not have fired yet)
            if (!_patchesApplied) ApplyPatches(null);
        }

        public void OnWorldUnload()
        {
            _panel.Close();
            _inBossFight = false;
            _dungeonX = -1;
            _dungeonY = -1;
            _pendingDungeonTeleport = false;
            // Reset game state but keep settings
            try { Main.dayRate = 1; } catch { }
        }

        public void Unload()
        {
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
            _normalRespawnMult = 1.0f;
            _bossRespawnMult = 1.0f;
            try { Main.dayRate = 1; } catch { }
            TerrariaModder.Core.Net.NetSync.OnServerCommandResponse -= OnServerCommandResponse;
            NPCSpawner.Unload();
            _log.Info("AdminPanel unloaded");
        }

        #endregion

        #region Initialization

        private void LoadSettings()
        {
            try
            {
                _godModeActive = _config != null ? _config.GodMode : false;
                _timeSpeedMultiplier = _config != null ? _config.TimeSpeed : 1;
                _normalRespawnIndex = _config != null ? _config.NormalRespawnIndex : NormalDefaultIndex;
                _bossRespawnIndex = _config != null ? _config.BossRespawnIndex : BossDefaultIndex;
                _moveSpeedMultiplier = _config != null ? _config.MoveSpeed : 1;

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

                // NPC Spawner favourites
                string bossFavs = _config != null ? _config.BossFavourites : "";
                string npcFavs = _config != null ? _config.NpcFavourites : "";
                _prevBossFavs = bossFavs;
                _prevNpcFavs = npcFavs;
                NPCSpawner.LoadFavourites(bossFavs, npcFavs);

                bool rightClickSpawn = _config != null ? _config.RightClickSpawn : false;
                _prevRightClickSpawn = rightClickSpawn;
                NPCSpawner.LoadRightClickSpawn(rightClickSpawn);

                _log.Info($"Settings loaded - god:{_godModeActive} time:{_timeSpeedMultiplier}x respawn:{NormalRespawnSeconds[_normalRespawnIndex]}s/{BossRespawnSeconds[_bossRespawnIndex]}s move:{_moveSpeedMultiplier}x");
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
                    if (_config != null)
                    {
                        string propName = char.ToUpper(key[0]) + key.Substring(1);
                        var prop = _config.GetType().GetProperty(propName);
                        prop?.SetValue(_config, current);
                        _config.Save();
                    }
                }
                catch { }
            }
        }

        private void SaveConfigSetting<T>(string key, T current, ref T previous) where T : IEquatable<T>
        {
            SaveSettingIfChanged(key, current, ref previous);
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

            if (_harmony == null) return;

            try
            {
                // Player.ResetEffects postfix - god mode immunity each frame
                var resetEffectsMethod = typeof(Player).GetMethod("ResetEffects", BindingFlags.Public | BindingFlags.Instance);
                if (resetEffectsMethod != null)
                {
                    var postfix = typeof(Mod).GetMethod(nameof(ResetEffects_Postfix), BindingFlags.NonPublic | BindingFlags.Static);
                    _harmony.Patch(resetEffectsMethod, postfix: new HarmonyMethod(postfix));
                    _log.Debug("Patched Player.ResetEffects for god mode");
                }

                // Adjust only the native countdown; keep all death effects and respawn ownership native.
                var updateDeadMethod = typeof(Player).GetMethod("UpdateDead", BindingFlags.Public | BindingFlags.Instance);
                if (updateDeadMethod != null)
                {
                    var prefix = typeof(Mod).GetMethod(nameof(UpdateDead_Prefix), BindingFlags.NonPublic | BindingFlags.Static);
                    _harmony.Patch(updateDeadMethod, prefix: new HarmonyMethod(prefix));
                    _log.Debug("Patched Player.UpdateDead for respawn time");
                }

                // Main.UpdateTimeRate postfix - time speed multiplier
                var updateTimeRateMethod = typeof(Main).GetMethod("UpdateTimeRate", BindingFlags.Public | BindingFlags.Static);
                if (updateTimeRateMethod != null)
                {
                    var postfix = typeof(Mod).GetMethod(nameof(UpdateTimeRate_Postfix), BindingFlags.NonPublic | BindingFlags.Static);
                    _harmony.Patch(updateTimeRateMethod, postfix: new HarmonyMethod(postfix));
                    _log.Debug("Patched Main.UpdateTimeRate for time speed");
                }

                // Player.HorizontalMovement prefix - movement speed multiplier
                var horizontalMovementMethod = typeof(Player).GetMethod("HorizontalMovement", BindingFlags.Public | BindingFlags.Instance);
                if (horizontalMovementMethod != null)
                {
                    var prefix = typeof(Mod).GetMethod(nameof(HorizontalMovement_Prefix), BindingFlags.NonPublic | BindingFlags.Static);
                    _harmony.Patch(horizontalMovementMethod, prefix: new HarmonyMethod(prefix));
                    _log.Debug("Patched Player.HorizontalMovement for movement speed");
                }

            }
            catch (Exception ex)
            {
                _log.Error($"Harmony patch error: {ex.Message}");
            }
        }

        private static void ResetEffects_Postfix(Player __instance)
        {
            if (!_godModeActive) return;

            try
            {
                if (__instance == Main.player[Main.myPlayer])
                {
                    __instance.immune = true;
                    __instance.immuneTime = 2;
                    __instance.immuneNoBlink = true;
                    __instance.creativeGodMode = true;
                }
            }
            catch { }
        }

        private static void UpdateDead_Prefix(Player __instance)
        {
            if (__instance != Main.LocalPlayer) return;
            int current = __instance.respawnTimer;
            if (_respawnPlayer != __instance || current > _lastRespawnTimer || current <= 0)
            {
                _respawnPlayer = __instance;
                _respawnTickFraction = 0;
            }
            if (current <= 0) { _lastRespawnTimer = current; return; }
            _inBossFight = DetectBossFight(__instance);
            double mult = _inBossFight ? _bossRespawnMult : _normalRespawnMult;
            if (mult <= 0 || double.IsNaN(mult) || double.IsInfinity(mult)) mult = 1;
            _respawnTickFraction += 1.0 / mult;
            int elapsed = (int)Math.Floor(_respawnTickFraction + 0.000001);
            _respawnTickFraction = Math.Max(0, _respawnTickFraction - elapsed);
            // UpdateDead subtracts one and performs the actual respawn at zero.
            // Compensate before that subtraction, including fractional slow countdowns.
            __instance.respawnTimer = Math.Max(1, current + 1 - elapsed);
            _lastRespawnTimer = __instance.respawnTimer - 1;
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
                int current = Main.dayRate;
                if (current > 0) // Don't multiply if frozen (dayRate=0)
                {
                    Main.dayRate = current * _timeSpeedMultiplier;
                }
            }
            catch { }
        }

        /// <summary>
        /// Prefix for Player.HorizontalMovement - multiplies movement speed fields
        /// after all equipment/buff effects have been applied.
        /// </summary>
        private static void HorizontalMovement_Prefix(Player __instance)
        {
            if (_moveSpeedMultiplier <= 1) return;

            try
            {
                if (__instance != Main.player[Main.myPlayer]) return;

                __instance.maxRunSpeed *= _moveSpeedMultiplier;
                __instance.runAcceleration *= _moveSpeedMultiplier;
            }
            catch { }
        }

        private static bool DetectBossFight(Player player)
        {
            Vector2 playerCenter = player.Center;

            for (int i = 0; i < Math.Min(Main.npc.Length, 200); i++)
            {
                NPC npc = Main.npc[i];
                if (npc == null || !npc.active) continue;

                if ((npc.boss || npc.type == 13 || npc.type == 14 || npc.type == 15) && npc.type != 395)
                {
                    Vector2 npcCenter = npc.Center;
                    if (Math.Abs(playerCenter.X - npcCenter.X) + Math.Abs(playerCenter.Y - npcCenter.Y) < 4000f)
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
                    if (_config != null) { _config.BossFavourites = bossFavs; _config.Save(); }
                }

                string npcFavs = NPCSpawner.SaveNPCFavourites();
                if (npcFavs != _prevNpcFavs)
                {
                    _prevNpcFavs = npcFavs;
                    if (_config != null) { _config.NpcFavourites = npcFavs; _config.Save(); }
                }

                bool rcs = NPCSpawner.RightClickSpawnEnabled;
                if (rcs != _prevRightClickSpawn)
                {
                    _prevRightClickSpawn = rcs;
                    if (_config != null) { _config.RightClickSpawn = rcs; _config.Save(); }
                }
            }
            catch { }
        }

        #endregion

        #region Keybind Handlers

        private void OnToggleUI()
        {
            _panel.Toggle();
        }

        private void OnToggleGodMode()
        {
            _godModeActive = !_godModeActive;
            _log.Info($"God mode: {(_godModeActive ? "ON" : "OFF")}");

            try
            {
                Player player = Main.player[Main.myPlayer];
                player.immune = _godModeActive;
                player.immuneTime = _godModeActive ? 2 : 0;
                player.immuneNoBlink = _godModeActive;
            }
            catch (Exception ex)
            {
                _log.Error($"Failed to toggle god mode: {ex.Message}");
            }

            SaveSettingIfChanged("godMode", _godModeActive, ref _prevGodMode);
        }

        #endregion

        #region UI Drawing

        private void OnDraw()
        {
            // Draw god mode HUD indicator regardless of panel state
            if (_godModeActive)
            {
                UIRenderer.DrawText("GOD", UIRenderer.ScreenWidth - 50, 8, 255, 215, 0, 200);
            }

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
            bool isAdmin = IsLocalAdmin();
            bool inMp = Main.netMode != 0;
            string adminSuffix = (inMp && !isAdmin) ? " [Admin]" : "";
            s.SectionHeader($"TIME{adminSuffix}");
            int qw = (s.Width - 24) / 4;
            bool canSetTime = !(inMp && !isAdmin);
            if (s.ButtonAt(s.X, qw, "Dawn")               && canSetTime) SetTimePreset("dawn");
            if (s.ButtonAt(s.X + qw + 8, qw, "Noon")      && canSetTime) SetTimePreset("noon");
            if (s.ButtonAt(s.X + (qw + 8) * 2, qw, "Dusk") && canSetTime) SetTimePreset("dusk");
            if (s.ButtonAt(s.X + (qw + 8) * 3, qw, "Night") && canSetTime) SetTimePreset("night");
            s.Advance(26);

            if (canSetTime)
            {
                bool isPureMpClient = Main.netMode == 1 && !Netplay.IsHostAndPlay;
                sy = s.Advance(SliderHeight);
                if (isPureMpClient)
                {
                    UIRenderer.DrawText("Speed: Host only", s.X, sy + 2, UIColors.TextHint);
                }
                else
                {
                    UIRenderer.DrawText("Speed:", s.X, sy + 2, UIColors.TextDim);
                    _timeSpeedMultiplier = _timeSlider.Draw(s.X + 50, sy, s.Width - 50 - labelW, SliderHeight,
                        _timeSpeedMultiplier, 1, 60);
                    string timeLabel = _timeSpeedMultiplier == 1 ? "1x (normal)" : $"{_timeSpeedMultiplier}x";
                    var timeLabelColor = _timeSpeedMultiplier == 1 ? UIColors.TextHint : UIColors.AccentText;
                    UIRenderer.DrawText(timeLabel, s.X + s.Width - UIRenderer.MeasureText(timeLabel), sy + 2, timeLabelColor);
                    SaveSettingIfChanged("timeSpeed", _timeSpeedMultiplier, ref _prevTimeSpeed);
                }
            }

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
                Player player = Main.player[Main.myPlayer];
                player.statLife = player.statLifeMax2;
                _log.Info($"Health restored to {player.statLifeMax2}");
            }
            catch (Exception ex) { _log.Error($"Failed to restore health: {ex.Message}"); }
        }

        private void RestoreMana()
        {
            try
            {
                Player player = Main.player[Main.myPlayer];
                player.statMana = player.statManaMax2;
                _log.Info($"Mana restored to {player.statManaMax2}");
            }
            catch (Exception ex) { _log.Error($"Failed to restore mana: {ex.Message}"); }
        }

        private bool IsLocalAdmin()
        {
            if (Main.netMode == 0) return true; // singleplayer: always admin
            return TerrariaModder.Core.Net.NetSync.LocalPlayerIsAdmin;
        }

        private bool SetTimePreset(string preset)
        {
            if (Main.netMode != 0 && !Netplay.IsHostAndPlay)
            {
                // Pure multiplayer client: send server command (server validates admin, applies, broadcasts)
                if (!IsLocalAdmin())
                {
                    try { Main.NewText("[AdminPanel] Time control requires Admin.", 255, 80, 80); } catch { }
                    return false;
                }
                TerrariaModder.Core.Net.NetSync.SendServerCommandRequest("time", preset);
                return true;
            }

            // SP or H&P host: apply directly and broadcast to clients
            try
            {
                switch (preset)
                {
                    case "dawn":  Main.dayTime = true;  Main.time = 0.0;     break;
                    case "noon":  Main.dayTime = true;  Main.time = 27000.0; break;
                    case "dusk":  Main.dayTime = false; Main.time = 0.0;     break;
                    case "night": Main.dayTime = false; Main.time = 16200.0; break;
                }
                if (Main.netMode != 0)
                    NetMessage.SendData(7); // broadcast time to all clients
                _log.Info($"Time set to {preset}");
                return true;
            }
            catch (Exception ex) { _log.Error($"Failed to set time: {ex.Message}"); return false; }
        }

        private void InstantRespawn()
        {
            try
            {
                Main.player[Main.myPlayer].respawnTimer = 0;
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
                Player player = Main.player[Main.myPlayer];
                player.Shellphone_Spawn();
                _log.Info("Teleported to spawn (shellphone)");
            }
            catch (Exception ex) { _log.Error($"Failed to teleport to spawn: {ex.Message}"); }
        }

        private void TeleportToDungeon()
        {
            try
            {
                if (Main.netMode == 0 || Main.netMode == 2)
                {
                    // SP or ded server host: Main.dungeonX/Y populated from WorldFile.Load
                    if (Main.dungeonX <= 0 || Main.dungeonY <= 0)
                    {
                        _log.Info("[AdminPanel] Dungeon not found in this world (dungeonX/Y = -1)");
                        throw new InvalidOperationException("Dungeon not found in this world");
                    }
                    TeleportPlayer(Main.dungeonX * 16f + 8f - 10f, Main.dungeonY * 16f - 42f);
                }
                else
                {
                    // Client (netMode==1, including H&P host): Main.dungeonX/Y are NOT populated.
                    // If we have cached coords, use them. Otherwise request from server.
                    if (_dungeonX > 0 || _dungeonY > 0)
                    {
                        DoTeleportToDungeon();
                    }
                    else
                    {
                        _log.Info("[AdminPanel] Requesting dungeon coords from server...");
                        _pendingDungeonTeleport = true;
                        TerrariaModder.Core.Net.NetSync.SendServerCommandRequest("worldcoords", "");
                        throw new InvalidOperationException("Dungeon coords requested from server — teleport will complete when response arrives");
                    }
                }
            }
            catch (Exception ex) { _log.Error($"Failed to teleport to dungeon: {ex.Message}"); throw; }
        }

        private void DoTeleportToDungeon()
        {
            if (_dungeonX <= 0 || _dungeonY <= 0)
            {
                _log.Info("[AdminPanel] Dungeon not found in this world (coords = -1)");
                return;
            }
            TeleportPlayer(_dungeonX * 16f + 8f - 10f, _dungeonY * 16f - 42f);
        }

        private void TeleportToHell()
        {
            try
            {
                if (Main.netMode == 0 || Main.netMode == 2)
                {
                    // SP or ded server host: full world loaded, call directly
                    var player = Main.player[Main.myPlayer];
                    var posBefore = player.position;
                    player.DemonConch();
                    if (player.position == posBefore)
                        throw new InvalidOperationException("Hell teleport failed — no valid underworld location found (small world?)");
                    _log.Info("Teleported to hell (DemonConch)");
                }
                else
                {
                    // Client (netMode==1, including H&P host): send packet 73 byte=2 — server calls DemonConch()
                    // with all tiles loaded, broadcasts result via packet 65.
                    NetMessage.SendData(73, -1, -1, null, 2);
                    _log.Info("Teleported to hell (packet 73/DemonConch)");
                }
            }
            catch (Exception ex) { _log.Error($"Failed to teleport to hell: {ex.Message}"); throw; }
        }

        private void TeleportToBeach()
        {
            try
            {
                if (Main.netMode == 0 || Main.netMode == 2)
                {
                    // SP or ded server host: full world loaded, call directly
                    var player = Main.player[Main.myPlayer];
                    var posBefore = player.position;
                    player.MagicConch();
                    if (player.position == posBefore)
                        throw new InvalidOperationException("Beach teleport failed — no valid ocean location found (small world?)");
                    _log.Info("Teleported to beach (MagicConch)");
                }
                else
                {
                    // Client (netMode==1, including H&P host): send packet 73 byte=1 — server calls MagicConch()
                    // with all tiles loaded, broadcasts result via packet 65.
                    NetMessage.SendData(73, -1, -1, null, 1);
                    _log.Info("Teleported to beach (packet 73/MagicConch)");
                }
            }
            catch (Exception ex) { _log.Error($"Failed to teleport to beach: {ex.Message}"); throw; }
        }

        private void TeleportToBed()
        {
            try
            {
                Player player = Main.player[Main.myPlayer];
                if (player.SpawnX == -1 || player.SpawnY == -1)
                {
                    _log.Info("No bed spawn set, teleporting to world spawn");
                    TeleportToSpawn();
                }
                else
                {
                    TeleportPlayer(player.SpawnX * 16f + 8f - 10f, player.SpawnY * 16f - 42f);
                    _log.Info($"Teleported to bed ({player.SpawnX}, {player.SpawnY})");
                }
            }
            catch (Exception ex) { _log.Error($"Failed to teleport to bed: {ex.Message}"); }
        }

        private void TeleportRandom()
        {
            try
            {
                Player player = Main.player[Main.myPlayer];
                player.TeleportationPotion();
                _log.Info("Random teleport (teleportation potion)");
            }
            catch (Exception ex) { _log.Error($"Failed to random teleport: {ex.Message}"); }
        }

        private void TeleportPlayer(float worldX, float worldY)
        {
            try
            {
                Player player = Main.player[Main.myPlayer];
                player.Teleport(new Vector2(worldX, worldY), 1, 0);
                _log.Info($"Teleported to ({worldX / 16:F0}, {worldY / 16:F0})");
            }
            catch (Exception ex) { _log.Error($"Failed to teleport: {ex.Message}"); }
        }

        #endregion
    }
}
