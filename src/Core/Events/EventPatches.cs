using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Terraria;
using TerrariaModder.Core.Assets;
using TerrariaModder.Core.Logging;
using TerrariaModder.Core.Reflection;
using TerrariaModder.Core.UI;

namespace TerrariaModder.Core.Events
{
    /// <summary>
    /// Harmony patches that fire framework events.
    /// </summary>
    internal static class EventPatches
    {
        private static Harmony _harmony;
        private static ILogger _log;
        private static bool _patchesApplied = false;

        // Time tracking for day/night transitions
        private static bool _wasDay = true;
        private static bool _wasInWorld = false;

        public static void Initialize(ILogger logger)
        {
            _log = logger;
            _harmony = new Harmony("com.terrariamodder.events");
            EventDispatcher.Initialize(logger);
        }

        /// <summary>
        /// Apply all event patches. Called after game initialization.
        /// </summary>
        public static void ApplyPatches()
        {
            if (_patchesApplied) return;

            try
            {
                _log?.Info("[Events] Applying event patches...");

                // Patch DoUpdate for frame events and state tracking
                PatchDoUpdate();

                // Patch Draw for draw events (skip on server - no graphics)
                if (!Game.IsServer)
                {
                    PatchDraw();

                    // Patch DrawCursor for UI overlay (draws on top of everything)
                    PatchDrawCursor();

                    // Patch ItemSlot.Handle to prevent click-through on modal UIs
                    PatchItemSlotHandle();

                }

                // Patch HandleUseRequest to fix SpriteBatch state corruption
                if (!Game.IsServer)
                    PatchHandleUseRequest();

                // Patch world loading/unloading
                PatchWorldIO();

                // Patch player events
                PatchPlayerEvents();

                // Patch NPC events
                PatchNPCEvents();

                _patchesApplied = true;
                _log?.Info("[Events] Event patches applied successfully");
            }
            catch (Exception ex)
            {
                _log?.Error($"[Events] Failed to apply patches: {ex.Message}");
            }
        }

        private static void PatchDoUpdate()
        {
            try
            {
                var doUpdate = typeof(Main).GetMethod("DoUpdate",
                    BindingFlags.NonPublic | BindingFlags.Instance);

                if (doUpdate != null)
                {
                    var prefix = typeof(EventPatches).GetMethod(nameof(DoUpdate_Prefix),
                        BindingFlags.NonPublic | BindingFlags.Static);
                    var postfix = typeof(EventPatches).GetMethod(nameof(DoUpdate_Postfix),
                        BindingFlags.NonPublic | BindingFlags.Static);

                    _harmony.Patch(doUpdate,
                        prefix: new HarmonyMethod(prefix),
                        postfix: new HarmonyMethod(postfix));
                }
            }
            catch (Exception ex)
            {
                _log?.Warn($"[Events] Failed to patch DoUpdate: {ex.Message}");
            }
        }

        private static void PatchDraw()
        {
            try
            {
                var draw = typeof(Main).GetMethod("DoDraw",
                    BindingFlags.NonPublic | BindingFlags.Instance);

                if (draw != null)
                {
                    var prefix = typeof(EventPatches).GetMethod(nameof(DoDraw_Prefix),
                        BindingFlags.NonPublic | BindingFlags.Static);
                    var postfix = typeof(EventPatches).GetMethod(nameof(DoDraw_Postfix),
                        BindingFlags.NonPublic | BindingFlags.Static);

                    _harmony.Patch(draw,
                        prefix: new HarmonyMethod(prefix),
                        postfix: new HarmonyMethod(postfix));
                }
            }
            catch (Exception ex)
            {
                _log?.Warn($"[Events] Failed to patch DoDraw: {ex.Message}");
            }
        }

        private static void PatchWorldIO()
        {
            try
            {
                // Use typeof since we have a reference to Terraria.exe
                var worldGenType = typeof(Terraria.WorldGen);

                // WorldGen.playWorldCallBack - called when world finishes loading
                // Method is public static
                var playWorldCallback = worldGenType.GetMethod("playWorldCallBack",
                    BindingFlags.Public | BindingFlags.Static);

                if (playWorldCallback != null)
                {
                    var postfix = typeof(EventPatches).GetMethod(nameof(WorldLoad_Postfix),
                        BindingFlags.NonPublic | BindingFlags.Static);
                    _harmony.Patch(playWorldCallback, postfix: new HarmonyMethod(postfix));
                }

                // WorldGen.SaveAndQuit - called when returning to menu
                var saveAndQuit = worldGenType.GetMethod("SaveAndQuit",
                    BindingFlags.Public | BindingFlags.Static)
                    ?? worldGenType.GetMethod("SaveAndQuit",
                        BindingFlags.NonPublic | BindingFlags.Static);

                if (saveAndQuit != null)
                {
                    var prefix = typeof(EventPatches).GetMethod(nameof(SaveAndQuit_Prefix),
                        BindingFlags.NonPublic | BindingFlags.Static);
                    _harmony.Patch(saveAndQuit, prefix: new HarmonyMethod(prefix));
                }
            }
            catch (Exception ex)
            {
                _log?.Warn($"[Events] Failed to patch world IO: {ex.Message}");
            }
        }

        private static void PatchPlayerEvents()
        {
            try
            {
                // Player.Spawn
                var spawn = typeof(Player).GetMethod("Spawn",
                    BindingFlags.Public | BindingFlags.Instance, null, new Type[] { typeof(PlayerSpawnContext) }, null);

                if (spawn != null)
                {
                    var postfix = typeof(EventPatches).GetMethod(nameof(PlayerSpawn_Postfix),
                        BindingFlags.NonPublic | BindingFlags.Static);
                    _harmony.Patch(spawn, postfix: new HarmonyMethod(postfix));
                }

                // Player.KillMe
                var killMe = typeof(Player).GetMethod("KillMe",
                    BindingFlags.Public | BindingFlags.Instance);

                if (killMe != null)
                {
                    var postfix = typeof(EventPatches).GetMethod(nameof(PlayerKillMe_Postfix),
                        BindingFlags.NonPublic | BindingFlags.Static);
                    _harmony.Patch(killMe, postfix: new HarmonyMethod(postfix));
                }

                // Player.AddBuff
                var addBuff = typeof(Player).GetMethod("AddBuff",
                    BindingFlags.Public | BindingFlags.Instance);

                if (addBuff != null)
                {
                    var postfix = typeof(EventPatches).GetMethod(nameof(PlayerAddBuff_Postfix),
                        BindingFlags.NonPublic | BindingFlags.Static);
                    _harmony.Patch(addBuff, postfix: new HarmonyMethod(postfix));
                }
            }
            catch (Exception ex)
            {
                _log?.Warn($"[Events] Failed to patch player events: {ex.Message}");
            }
        }

        private static void PatchDrawCursor()
        {
            try
            {
                // Find Main.DrawCursor(Vector2, bool) - static method
                var drawCursorMethod = typeof(Main).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                    .FirstOrDefault(m =>
                        m.Name == "DrawCursor" &&
                        m.GetParameters().Length == 2 &&
                        m.GetParameters()[0].ParameterType.Name == "Vector2" &&
                        m.GetParameters()[1].ParameterType == typeof(bool));

                if (drawCursorMethod != null)
                {
                    var prefix = typeof(EventPatches).GetMethod(nameof(DrawCursor_Prefix),
                        BindingFlags.NonPublic | BindingFlags.Static);
                    var postfix = typeof(EventPatches).GetMethod(nameof(DrawCursor_Postfix),
                        BindingFlags.NonPublic | BindingFlags.Static);
                    _harmony.Patch(drawCursorMethod,
                        prefix: new HarmonyMethod(prefix),
                        postfix: new HarmonyMethod(postfix));
                }
            }
            catch (Exception ex)
            {
                _log?.Warn($"[Events] Failed to patch DrawCursor: {ex.Message}");
            }
        }

        // Menu click-through prevention: save click state before DrawMenu consumes it,
        // restore in DrawCursor_Prefix so our panels can process clicks
        private static bool _menuClickSaved;
        private static bool _savedMouseLeftRelease;
        private static bool _savedMouseRightRelease;

        private static void PatchHandleUseRequest()
        {
            try
            {
                // SpriteBatch.Begin throws if called while already in a begun state.
                // Our DoDraw patches can leave SpriteBatch in inconsistent state between frames.
                // Fix: patch SpriteBatch.Begin itself to auto-End before re-Beginning.
                // This is aggressive but prevents ALL double-Begin crashes in the draw pipeline.
                var sbType = typeof(Main).Assembly.GetType("Microsoft.Xna.Framework.Graphics.SpriteBatch");
                if (sbType == null)
                {
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        sbType = asm.GetType("Microsoft.Xna.Framework.Graphics.SpriteBatch");
                        if (sbType != null) break;
                    }
                }

                if (sbType == null)
                {
                    _log?.Warn("[Events] SpriteBatch type not found in any loaded assembly");
                    return;
                }

                _log?.Info($"[Events] SpriteBatch type found: {sbType.FullName} in {sbType.Assembly.GetName().Name}");

                // Dump ALL fields for diagnostic purposes
                var allFields = sbType.GetFields(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                _log?.Info($"[Events] SpriteBatch has {allFields.Length} instance fields:");
                foreach (var f in allFields)
                {
                    _log?.Info($"[Events]   {f.FieldType.Name} {f.Name} ({(f.IsPublic ? "public" : "private")})");
                }

                // Cache the begin state field for the prefix
                // XNA 4.0 uses "inBeginEndPair", FNA uses "beginCalled"
                _sbBeginCalledField = sbType.GetField("inBeginEndPair",
                    BindingFlags.NonPublic | BindingFlags.Instance)
                    ?? sbType.GetField("beginCalled",
                        BindingFlags.NonPublic | BindingFlags.Instance)
                    ?? sbType.GetField("_beginCalled",
                        BindingFlags.NonPublic | BindingFlags.Instance);
                _sbEndMethod = sbType.GetMethod("End",
                    BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);

                if (_sbBeginCalledField == null)
                {
                    // Search for any bool field with "begin" in the name
                    foreach (var f in allFields)
                    {
                        if (f.FieldType == typeof(bool) && f.Name.ToLower().Contains("begin"))
                        {
                            _sbBeginCalledField = f;
                            _log?.Info($"[Events] Found SpriteBatch begin field via search: {f.Name}");
                            break;
                        }
                    }
                }

                if (_sbBeginCalledField == null)
                {
                    // Even broader: any bool field at all
                    _log?.Warn("[Events] SpriteBatch: no 'begin' bool field found. Bool fields:");
                    foreach (var f in allFields)
                    {
                        if (f.FieldType == typeof(bool))
                            _log?.Warn($"[Events]   bool {f.Name}");
                    }
                    _log?.Warn("[Events] Will use try-catch fallback for SpriteBatch.Begin safety");
                }
                else
                {
                    _log?.Info($"[Events] SpriteBatch begin field: {_sbBeginCalledField.Name}");
                }

                // Patch ALL Begin overloads — any of them could be called when state is corrupted
                var prefix = typeof(EventPatches).GetMethod(nameof(SpriteBatchBegin_Prefix),
                    BindingFlags.NonPublic | BindingFlags.Static);
                var beginMethods = sbType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .Where(m => m.Name == "Begin");
                int patchedCount = 0;
                foreach (var bm in beginMethods)
                {
                    _harmony.Patch(bm, prefix: new HarmonyMethod(prefix));
                    patchedCount++;
                    _log?.Info($"[Events] Patched SpriteBatch.Begin({bm.GetParameters().Length}) (auto-recovery)");
                }
                _log?.Info($"[Events] Patched {patchedCount} SpriteBatch.Begin overloads total");
            }
            catch (Exception ex)
            {
                _log?.Warn($"[Events] Failed to patch SpriteBatch: {ex.Message}");
            }
        }

        private static void PatchItemSlotHandle()
        {
            try
            {
                // Find ItemSlot type in Terraria.UI namespace
                var itemSlotType = typeof(Main).Assembly.GetType("Terraria.UI.ItemSlot");
                if (itemSlotType == null)
                {
                    _log?.Warn("[Events] ItemSlot type not found - click-through prevention unavailable");
                    return;
                }

                // Find the Handle(Item[] inv, int context, int slot, bool allowInteract) method
                // This is the main entry point that calls LeftClick, RightClick, and MouseHover
                var handleMethod = itemSlotType.GetMethod("Handle",
                    BindingFlags.Public | BindingFlags.Static,
                    null,
                    new Type[] { typeof(Item[]), typeof(int), typeof(int), typeof(bool) },
                    null);

                if (handleMethod != null)
                {
                    var prefix = typeof(EventPatches).GetMethod(nameof(ItemSlotHandle_Prefix),
                        BindingFlags.NonPublic | BindingFlags.Static);
                    _harmony.Patch(handleMethod, prefix: new HarmonyMethod(prefix));
                    UIRenderer.LogItemSlotPatchApplied();
                }
                else
                {
                    _log?.Warn("[Events] ItemSlot.Handle method not found - click-through prevention unavailable");
                }
            }
            catch (Exception ex)
            {
                _log?.Warn($"[Events] Failed to patch ItemSlot.Handle: {ex.Message}");
            }
        }

        private static void PatchNPCEvents()
        {
            try
            {
                // NPC.NewNPC
                var newNPC = typeof(NPC).GetMethod("NewNPC",
                    BindingFlags.Public | BindingFlags.Static);

                if (newNPC != null)
                {
                    var postfix = typeof(EventPatches).GetMethod(nameof(NPCNewNPC_Postfix),
                        BindingFlags.NonPublic | BindingFlags.Static);
                    _harmony.Patch(newNPC, postfix: new HarmonyMethod(postfix));
                }

                // NPC.checkDead (called when NPC dies)
                var checkDead = typeof(NPC).GetMethod("checkDead",
                    BindingFlags.Public | BindingFlags.Instance);

                if (checkDead != null)
                {
                    var postfix = typeof(EventPatches).GetMethod(nameof(NPCCheckDead_Postfix),
                        BindingFlags.NonPublic | BindingFlags.Static);
                    _harmony.Patch(checkDead, postfix: new HarmonyMethod(postfix));
                }
            }
            catch (Exception ex)
            {
                _log?.Warn($"[Events] Failed to patch NPC events: {ex.Message}");
            }
        }

        #region Patch Callbacks

        // Frame events
        private static void DoUpdate_Prefix(Main __instance)
        {
            try
            {
                if (Main.gameMenu) return;
                FrameEvents.FirePreUpdate();
                CheckTimeTransitions();
            }
            catch (Exception ex)
            {
                _log?.Error($"[Events] DoUpdate_Prefix error: {ex.Message}");
            }
        }

        private static void DoUpdate_Postfix(Main __instance)
        {
            try
            {
                if (Main.gameMenu) return;
                FrameEvents.FirePostUpdate();
            }
            catch (Exception ex)
            {
                _log?.Error($"[Events] DoUpdate_Postfix error: {ex.Message}");
            }
        }

        // SpriteBatch safety: cached reflection to reset state before each DoDraw frame
        private static FieldInfo _sbBeginCalledField;
        private static MethodInfo _sbEndMethod;
        private static object _sbInstance;
        private static bool _sbInstanceResolved;

        private static void DoDraw_Prefix(Main __instance)
        {
            try
            {
                // Safety: if a previous frame's exception left SpriteBatch in begun state,
                // end it now before vanilla code tries to Begin again (prevents cascading errors)
                EnsureSpriteBatchClean(__instance);

                if (Main.gameMenu)
                {
                    // Menu click-through prevention: when a mod panel is open, save the click
                    // state then consume it. DrawMenu runs next (can't click vanilla buttons).
                    // DrawCursor_Prefix restores it so our panels can process clicks.
                    _menuClickSaved = false;
                    if (UIRenderer.IsBlocking)
                    {
                        _savedMouseLeftRelease = Main.mouseLeftRelease;
                        _savedMouseRightRelease = Main.mouseRightRelease;
                        Main.mouseLeftRelease = false;
                        Main.mouseRightRelease = false;
                        _menuClickSaved = true;
                    }
                    return;
                }
                FrameEvents.FirePreDraw();
            }
            catch (Exception ex)
            {
                _log?.Error($"[Events] DoDraw_Prefix error: {ex.Message}");
            }
        }

        /// <summary>
        /// Directly check and reset SpriteBatch state using cached reflection.
        /// Reuses _sbBeginCalledField and _sbEndMethod set by PatchHandleUseRequest().
        /// Only resolves the Main.spriteBatch instance on first call.
        /// </summary>
        private static void EnsureSpriteBatchClean(Main instance)
        {
            if (!_sbInstanceResolved)
            {
                _sbInstanceResolved = true;
                try
                {
                    var sbField = typeof(Main).GetField("spriteBatch",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    _sbInstance = sbField?.GetValue(instance);
                }
                catch { }
            }

            if (_sbInstance == null || _sbEndMethod == null) return;

            try
            {
                if (_sbBeginCalledField != null)
                {
                    // Fast path: check the field before calling End
                    if ((bool)_sbBeginCalledField.GetValue(_sbInstance))
                    {
                        _sbEndMethod.Invoke(_sbInstance, null);
                    }
                }
                else
                {
                    // Fallback: try calling End() — if not begun, it throws and we catch
                    try { _sbEndMethod.Invoke(_sbInstance, null); }
                    catch { }
                }
            }
            catch { }
        }

        private static void DoDraw_Postfix(Main __instance)
        {
            try
            {
                if (Main.gameMenu) return;
                FrameEvents.FirePostDraw();
            }
            catch (Exception ex)
            {
                _log?.Error($"[Events] DoDraw_Postfix error: {ex.Message}");
            }
        }

        // UI drawing - called just before cursor is drawn (ensures UI is on top)
        private static bool _drawCursorErrorLogged;
        private static void DrawCursor_Prefix(object bonus, bool smart)
        {
            try
            {
                // Restore click state that was consumed in DoDraw_Prefix for menu click-through prevention.
                // This lets our panels process clicks even though vanilla menu buttons were blocked.
                if (_menuClickSaved)
                {
                    Main.mouseLeftRelease = _savedMouseLeftRelease;
                    Main.mouseRightRelease = _savedMouseRightRelease;
                    _menuClickSaved = false;
                }

                // Fire UI overlay event (for non-panel draw subscribers)
                FrameEvents.FireUIOverlay();

                // Draw all registered panels in z-order (back to front)
                UIRenderer.DrawAllPanels();

                // Set mouseInterface during Draw when mouse is over a mod panel.
                // This feeds into lastMouseInterface next frame, suppressing sign tooltips,
                // smart cursor hover, and other world hover effects.
                UIRenderer.SetMouseInterfaceIfOverPanel();
            }
            catch (Exception ex)
            {
                if (!_drawCursorErrorLogged)
                {
                    _log?.Error($"[UI] DrawCursor error: {ex.Message}\n{ex.StackTrace}");
                    if (ex.InnerException != null)
                        _log?.Error($"[UI] Inner: {ex.InnerException.Message}\n{ex.InnerException.StackTrace}");
                    _drawCursorErrorLogged = true;
                }
            }
        }

        private static void DrawCursor_Postfix(object bonus, bool smart)
        {
            // Nothing needed - click consumption happens in each modal's own input handling
        }

        // SpriteBatch.Begin auto-recovery: if already begun, End() first, then let Begin proceed
        private static void SpriteBatchBegin_Prefix(object __instance)
        {
            try
            {
                if (_sbBeginCalledField != null)
                {
                    // Fast path: check the field
                    if ((bool)_sbBeginCalledField.GetValue(__instance))
                    {
                        _sbEndMethod?.Invoke(__instance, null);
                    }
                }
                else if (_sbEndMethod != null)
                {
                    // Fallback: try End() blindly — if not begun, it throws and we catch
                    try { _sbEndMethod.Invoke(__instance, null); }
                    catch { }
                }
            }
            catch { }
        }

        // ItemSlot click-through prevention
        // Returns false to skip the original method when our modal UI should capture the click
        private static bool ItemSlotHandle_Prefix(Item[] inv, int context, int slot, bool allowInteract)
        {
            try
            {
                // If our modal UI is open and mouse is over it, block inventory slot interactions
                if (UIRenderer.ShouldBlockItemSlot())
                {
                    return false; // Skip original method
                }
            }
            catch (Exception ex)
            {
                _log?.Error($"[Events] ItemSlotHandle_Prefix error: {ex.Message}");
            }
            return true; // Run original method
        }

        // World events
        private static void WorldLoad_Postfix()
        {
            try
            {
                _wasInWorld = true;
                _wasDay = Main.dayTime;
            }
            catch (Exception ex)
            {
                _log?.Error($"[Events] WorldLoad_Postfix state init error: {ex.Message}");
            }

            // Fire the event for subscribers - isolated so PluginLoader still runs on failure
            try
            {
                GameEvents.FireWorldLoad();
            }
            catch (Exception ex)
            {
                _log?.Error($"[Events] WorldLoad event error: {ex.Message}");
            }

            // Also notify mods via IMod.OnWorldLoad()
            try
            {
                PluginLoader.NotifyWorldLoad();
            }
            catch (Exception ex)
            {
                _log?.Error($"[Events] WorldLoad mod notification error: {ex.Message}");
            }
        }

        private static void SaveAndQuit_Prefix()
        {
            if (!_wasInWorld) return;

            // Notify mods via IMod.OnWorldUnload() first - isolated so event still fires on failure
            try
            {
                PluginLoader.NotifyWorldUnload();
            }
            catch (Exception ex)
            {
                _log?.Error($"[Events] WorldUnload mod notification error: {ex.Message}");
            }

            // Then fire the event
            try
            {
                GameEvents.FireWorldUnload();
            }
            catch (Exception ex)
            {
                _log?.Error($"[Events] WorldUnload event error: {ex.Message}");
            }

            _wasInWorld = false;
        }

        // Time transitions
        private static void CheckTimeTransitions()
        {
            try
            {
                bool isDay = Main.dayTime;
                if (isDay != _wasDay)
                {
                    if (isDay)
                        GameEvents.FireDayStart();
                    else
                        GameEvents.FireNightStart();
                    _wasDay = isDay;
                }
            }
            catch (Exception ex)
            {
                _log?.Error($"[Events] CheckTimeTransitions error: {ex.Message}");
            }
        }

        // Player events
        private static void PlayerSpawn_Postfix(Player __instance)
        {
            try
            {
                if (__instance.whoAmI != Main.myPlayer) return;

                PlayerEvents.FirePlayerSpawn(new PlayerSpawnEventArgs
                {
                    PlayerIndex = __instance.whoAmI,
                    Player = __instance,
                    SpawnX = (int)__instance.position.X,
                    SpawnY = (int)__instance.position.Y
                });
            }
            catch (Exception ex)
            {
                _log?.Error($"[Events] PlayerSpawn_Postfix error: {ex.Message}");
            }
        }

        private static void PlayerKillMe_Postfix(Player __instance, double dmg, int hitDirection, bool pvp)
        {
            try
            {
                if (__instance.whoAmI != Main.myPlayer) return;

                PlayerEvents.FirePlayerDeath(new PlayerDeathEventArgs
                {
                    PlayerIndex = __instance.whoAmI,
                    Player = __instance,
                    Damage = (int)dmg,
                    DeathReason = pvp ? "PvP" : "Killed"
                });
            }
            catch (Exception ex)
            {
                _log?.Error($"[Events] PlayerKillMe_Postfix error: {ex.Message}");
            }
        }

        private static void PlayerAddBuff_Postfix(Player __instance, int type, int time)
        {
            try
            {
                if (__instance.whoAmI != Main.myPlayer) return;

                PlayerEvents.FireBuffApplied(new BuffEventArgs
                {
                    PlayerIndex = __instance.whoAmI,
                    Player = __instance,
                    BuffType = type,
                    Duration = time
                });
            }
            catch (Exception ex)
            {
                _log?.Error($"[Events] PlayerAddBuff_Postfix error: {ex.Message}");
            }
        }

        // NPC events
        private static void NPCNewNPC_Postfix(int __result, int X, int Y, int Type)
        {
            try
            {
                if (__result < 0 || __result >= Main.npc.Length) return;

                var npc = Main.npc[__result];
                if (npc == null || !npc.active) return;

                var args = new NPCSpawnEventArgs
                {
                    NPCIndex = __result,
                    NPC = npc,
                    NPCType = Type,
                    SpawnX = X,
                    SpawnY = Y
                };

                NPCEvents.FireNPCSpawn(args);

                // Check if it's a boss
                if (npc.boss)
                {
                    NPCEvents.FireBossSpawn(new BossSpawnEventArgs
                    {
                        NPCIndex = __result,
                        NPC = npc,
                        NPCType = Type,
                        BossName = npc.FullName ?? npc.GivenName ?? "Unknown Boss"
                    });
                }
            }
            catch (Exception ex)
            {
                _log?.Error($"[Events] NPCNewNPC_Postfix error: {ex.Message}");
            }
        }

        private static void NPCCheckDead_Postfix(NPC __instance)
        {
            try
            {
                if (!__instance.active && __instance.life <= 0)
                {
                    NPCEvents.FireNPCDeath(new NPCDeathEventArgs
                    {
                        NPCIndex = __instance.whoAmI,
                        NPC = __instance,
                        NPCType = __instance.type,
                        LastDamage = 0,
                        KillerPlayerIndex = -1
                    });
                }
            }
            catch (Exception ex)
            {
                _log?.Error($"[Events] NPCCheckDead_Postfix error: {ex.Message}");
            }
        }

        #endregion

        /// <summary>
        /// Cleanup all patches and event subscriptions.
        /// </summary>
        public static void Cleanup()
        {
            _harmony?.UnpatchAll("com.terrariamodder.events");
            _patchesApplied = false;

            GameEvents.ClearAll();
            PlayerEvents.ClearAll();
            NPCEvents.ClearAll();
            ItemEvents.ClearAll();
            FrameEvents.ClearAll();
        }
    }
}
