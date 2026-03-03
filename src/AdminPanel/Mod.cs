using System;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Terraria;
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
        public string Version => "1.1.1";

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

        private static Harmony _harmony;
        private static Timer _patchTimer;
        private static readonly object _patchLock = new object();
        private static bool _patchesApplied;

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

            LoadSettings();

            context.RegisterKeybind("toggle", "Toggle Panel", "Open/close admin panel", "OemBackslash", OnToggleUI);
            context.RegisterKeybind("god-mode", "Toggle God Mode", "Toggle invincibility", "F9", OnToggleGodMode);

            _panel.ClipContent = false; // Content fits within panel; BeginClip causes transform issues
            _panel.RegisterDrawCallback(OnDraw);
            FrameEvents.OnPreUpdate += ExecutePendingAction;
            FrameEvents.OnPreUpdate += UpdateNPCSpawner;

            _harmony = new Harmony("com.terrariamodder.adminpanel");
            _patchTimer = new Timer(ApplyPatches, null, 5000, Timeout.Infinite);

            NPCSpawner.Init(_log);

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
            try { Main.dayRate = 1; } catch { }
            // Restore biome spread for save safety
            try { WorldGen.AllowedToSpreadInfections = true; } catch { }
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
            try { WorldGen.AllowedToSpreadInfections = true; } catch { }
            try { Main.dayRate = 1; } catch { }
            NPCSpawner.Unload();
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

                // Player.UpdateDead postfix - custom respawn times
                var updateDeadMethod = typeof(Player).GetMethod("UpdateDead", BindingFlags.Public | BindingFlags.Instance);
                if (updateDeadMethod != null)
                {
                    var postfix = typeof(Mod).GetMethod(nameof(UpdateDead_Postfix), BindingFlags.NonPublic | BindingFlags.Static);
                    _harmony.Patch(updateDeadMethod, postfix: new HarmonyMethod(postfix));
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

                // WorldGen.hardUpdateWorld prefix - biome spread disable
                var hardUpdateMethod = typeof(WorldGen).GetMethod("hardUpdateWorld", BindingFlags.Public | BindingFlags.Static);
                if (hardUpdateMethod != null)
                {
                    var prefix = typeof(Mod).GetMethod(nameof(HardUpdateWorld_Prefix), BindingFlags.NonPublic | BindingFlags.Static);
                    _harmony.Patch(hardUpdateMethod, prefix: new HarmonyMethod(prefix));
                    _log.Debug("Patched WorldGen.hardUpdateWorld for biome spread control");
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
                }
            }
            catch { }
        }

        private static void UpdateDead_Postfix(Player __instance)
        {
            try
            {
                if (__instance != Main.player[Main.myPlayer]) return;

                _inBossFight = DetectBossFight(__instance);

                float mult = _inBossFight ? _bossRespawnMult : _normalRespawnMult;
                if (mult >= 1.0f) return;

                int currentTimer = __instance.respawnTimer;
                if (currentTimer > 0)
                {
                    int extraReduction = (int)((1.0f / mult) - 1);
                    if (extraReduction > 0)
                    {
                        __instance.respawnTimer = Math.Max(0, currentTimer - extraReduction);
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

        /// <summary>
        /// Prefix for WorldGen.hardUpdateWorld - blocks biome spread when toggle is on.
        /// Also sets AllowedToSpreadInfections to false for grass growth methods.
        /// </summary>
        private static bool HardUpdateWorld_Prefix()
        {
            if (!_biomeSpreadDisabled) return true;

            // Also suppress the AllowedToSpreadInfections flag for grass-related spread
            try { WorldGen.AllowedToSpreadInfections = false; } catch { }
            return false; // Skip vanilla hardUpdateWorld entirely
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
            if (_singleplayerOnly && Main.netMode != 0)
            {
                _log.Warn("AdminPanel is disabled in multiplayer");
                return;
            }

            _panel.Toggle();
        }

        private void OnToggleGodMode()
        {
            if (_singleplayerOnly && Main.netMode != 0)
            {
                _log.Warn("God mode is disabled in multiplayer");
                return;
            }

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
                    try { WorldGen.AllowedToSpreadInfections = true; } catch { }
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

        private void SetTime(bool dayTime, double time)
        {
            try
            {
                Main.dayTime = dayTime;
                Main.time = time;
                _log.Info($"Time set to {(dayTime ? "day" : "night")} {time}");
            }
            catch (Exception ex) { _log.Error($"Failed to set time: {ex.Message}"); }
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
                TeleportPlayer(Main.dungeonX * 16f + 8f - 10f, Main.dungeonY * 16f - 42f);
            }
            catch (Exception ex) { _log.Error($"Failed to teleport to dungeon: {ex.Message}"); }
        }

        private void TeleportToHell()
        {
            try
            {
                Player player = Main.player[Main.myPlayer];
                player.DemonConch();
                _log.Info("Teleported to hell (demon conch)");
            }
            catch (Exception ex) { _log.Error($"Failed to teleport to hell: {ex.Message}"); }
        }

        private void TeleportToBeach()
        {
            try
            {
                Player player = Main.player[Main.myPlayer];
                player.MagicConch();
                _log.Info("Teleported to beach (magic conch)");
            }
            catch (Exception ex) { _log.Error($"Failed to teleport to beach: {ex.Message}"); }
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
