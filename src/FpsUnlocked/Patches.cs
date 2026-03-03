using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using HarmonyLib;
using Microsoft.Xna.Framework.Input;
using Terraria;
using Terraria.Enums;
using Terraria.GameInput;
using TerrariaModder.Core.Logging;

namespace FpsUnlocked
{
    /// <summary>
    /// All 7 Harmony patches for the FPS Unlocked interpolation system.
    ///
    /// Patch lifecycle per frame:
    ///   1. DoUpdate_Prefix  — save accumulator, prepare for tick detection
    ///   2. DoUpdate body    — accumulator logic + maybe game logic
    ///   3. DoUpdate_Postfix — detect full/partial tick, capture keyframes, compute PartialTick
    ///   4. Update_Postfix   — override VSync, timing, FrameSkipMode for next frame
    ///   5. DoDraw_Prefix    — apply interpolated positions before rendering
    ///   6. DoDraw body      — renders entities at interpolated positions
    ///   7. DoDraw_Postfix   — restore real positions after rendering
    /// </summary>
    public static class Patches
    {
        private static ILogger _log;
        private static double _accumulatorBeforeUpdate;
        private static double _targetFrameTime;
        private static bool _firstKeyframeCaptured;
        private static int _diagFrameCount;
        private static int _diagFullTickCount;

        // Saved vanilla state for restoring when switching to VSync mode
        private static FrameSkipMode _savedFrameSkipMode;
        private static bool _wasOverriding;

        // Track interpolation state transitions
        private static bool _wasInterpolating;

        // VSync state tracking — ApplyChanges() triggers device reset (disposes render targets)
        // so we must only call it when the value actually changes, and skip the next Draw
        private static bool _vsyncCurrent = true; // vanilla default
        private static bool _skipNextDraw;

        // Stopwatch-based frame limiter (more accurate than XNA's IsFixedTimeStep)
        private static readonly Stopwatch _frameLimiter = Stopwatch.StartNew();

        public static void ApplyAll(Harmony harmony, ILogger log)
        {
            _log = log;

            // Read TARGET_FRAME_TIME constant directly
            _targetFrameTime = Main.TARGET_FRAME_TIME;

            // --- Patch 1: Block SuppressDraw ---
            var suppressDraw = FindSuppressDraw();
            if (suppressDraw != null)
            {
                harmony.Patch(suppressDraw,
                    prefix: new HarmonyMethod(typeof(Patches), nameof(SuppressDraw_Prefix)));
                log.Info($"Patch 1: SuppressDraw on {suppressDraw.DeclaringType.Name}");
            }
            else
            {
                log.Warn("Patch 1: SuppressDraw not found - partial ticks won't render");
            }

            // --- Patch 2: Update postfix (timing overrides) ---
            MethodInfo updateMethod = null;
            foreach (var m in typeof(Main).GetMethods(BindingFlags.NonPublic | BindingFlags.Instance))
            {
                if (m.Name == "Update" && m.GetParameters().Length == 1)
                {
                    updateMethod = m;
                    break;
                }
            }
            if (updateMethod != null)
            {
                harmony.Patch(updateMethod,
                    postfix: new HarmonyMethod(typeof(Patches), nameof(Update_Postfix)));
                log.Info("Patch 2: Main.Update postfix");
            }

            // --- Patch 3 & 4: DoUpdate prefix/postfix (keyframe capture) ---
            MethodInfo doUpdateMethod = null;
            foreach (var m in typeof(Main).GetMethods(BindingFlags.NonPublic | BindingFlags.Instance))
            {
                if (m.Name == "DoUpdate" && m.GetParameters().Length == 1)
                {
                    doUpdateMethod = m;
                    break;
                }
            }
            if (doUpdateMethod != null)
            {
                harmony.Patch(doUpdateMethod,
                    prefix: new HarmonyMethod(typeof(Patches), nameof(DoUpdate_Prefix)) { priority = Priority.First },
                    postfix: new HarmonyMethod(typeof(Patches), nameof(DoUpdate_Postfix)) { priority = Priority.Last });
                log.Info("Patch 3+4: Main.DoUpdate prefix (First) + postfix (Last)");
            }

            // --- Patch 5 & 6: DoDraw prefix/postfix (interpolation) ---
            MethodInfo doDrawMethod = null;
            foreach (var m in typeof(Main).GetMethods(BindingFlags.NonPublic | BindingFlags.Instance))
            {
                if (m.Name == "DoDraw" && m.GetParameters().Length == 1)
                {
                    doDrawMethod = m;
                    break;
                }
            }
            if (doDrawMethod != null)
            {
                harmony.Patch(doDrawMethod,
                    prefix: new HarmonyMethod(typeof(Patches), nameof(DoDraw_Prefix)) { priority = Priority.First },
                    finalizer: new HarmonyMethod(typeof(Patches), nameof(DoDraw_Finalizer)) { priority = Priority.Last });
                log.Info("Patch 5+6: Main.DoDraw prefix (First) + finalizer (Last)");
            }

            // --- Patch 7: Camera sub-pixel (remove integer snap via transpiler) ---
            var cameraMethod = typeof(Main).GetMethod("DoDraw_UpdateCameraPosition",
                BindingFlags.NonPublic | BindingFlags.Static);
            if (cameraMethod != null)
            {
                harmony.Patch(cameraMethod,
                    transpiler: new HarmonyMethod(typeof(Patches), nameof(CameraPosition_Transpiler)));
                log.Info("Patch 7: DoDraw_UpdateCameraPosition transpiler");
            }

            // --- Patch 8: Skip lighting engine on partial ticks ---
            // Terraria's LightingEngine uses a 4-state cycle (Scan->Blur->...) that advances
            // once per DoDraw call. Held-item lights (torches) are added via Lighting.AddLight
            // during Player.Update (60hz), but cleared after every Blur state.
            // At >240fps, the Blur state fires more than 60 times/sec -> excess Blurs have
            // empty per-frame lights -> held torch brightness oscillates.
            // Fix: skip lighting recalculation on partial ticks (reuse last full-tick map).
            var lightTilesMethod = typeof(Lighting).GetMethod("LightTiles",
                BindingFlags.Public | BindingFlags.Static);
            if (lightTilesMethod != null)
            {
                harmony.Patch(lightTilesMethod,
                    prefix: new HarmonyMethod(typeof(Patches), nameof(LightTiles_Prefix)));
                log.Info("Patch 8: Lighting.LightTiles prefix (cap at 240/sec)");
            }
            else
            {
                log.Warn("Patch 8: Lighting.LightTiles not found - torch flicker at >240fps");
            }

            log.Info("All patches applied successfully");
        }

        private static MethodInfo FindSuppressDraw()
        {
            // Walk up Main's type hierarchy to find Game.SuppressDraw
            var type = typeof(Main) as Type;
            while (type != null)
            {
                var method = type.GetMethod("SuppressDraw",
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                if (method != null)
                    return method;
                type = type.BaseType;
            }
            return null;
        }

        #region Patch 1: SuppressDraw

        /// <summary>
        /// Block SuppressDraw when interpolation is active.
        /// This ensures Draw runs on every frame (including partial ticks).
        /// </summary>
        public static bool SuppressDraw_Prefix()
        {
            if (!Mod.Enabled) return true;
            if (Mod.Mode == "VSync (Vanilla)") return true;
            if (!Mod.InterpolationEnabled) return true;

            // Don't interfere with title screen / menus
            if (Main.gameMenu) return true;

            // Don't block until at least one full tick has captured keyframes
            if (!_firstKeyframeCaptured) return true;

            // Block SuppressDraw -> allow Draw to proceed on partial ticks
            return false;
        }

        #endregion

        #region Patch 2: Update postfix (timing overrides)

        /// <summary>
        /// Override VSync, FrameSkipMode, IsFixedTimeStep, TargetElapsedTime every frame.
        /// Runs after Main.Update (which includes DoUpdate).
        /// Settings take effect on the NEXT frame's DoUpdate.
        /// </summary>
        public static void Update_Postfix(object __instance)
        {
            try
            {
                bool shouldOverride = Mod.Enabled && Mod.Mode != "VSync (Vanilla)" && !Main.gameMenu;

                // Activation transition
                if (shouldOverride && !_wasOverriding)
                {
                    _savedFrameSkipMode = Main.FrameSkipMode;
                    _wasOverriding = true;
                    _firstKeyframeCaptured = false;
                    _log?.Info($"FPS override activated - Mode: {Mod.Mode}, MaxFPS: {Mod.MaxFps}, " +
                        $"Interpolation: {Mod.InterpolationEnabled}");
                }
                // Deactivation transition
                else if (!shouldOverride && _wasOverriding)
                {
                    // Restore vanilla settings
                    Main.FrameSkipMode = _savedFrameSkipMode;

                    SetVSync(true);

                    // Restore vanilla timing
                    ReflectionCache.IsFixedTimeStepProp?.SetValue(__instance, true, null);
                    ReflectionCache.TargetElapsedTimeProp?.SetValue(__instance,
                        TimeSpan.FromSeconds(_targetFrameTime), null);

                    _wasOverriding = false;
                    _vsyncCurrent = true; // back to vanilla
                    _firstKeyframeCaptured = false;
                    FrameState.Reset();
                    _log?.Info("FPS override deactivated, restored vanilla settings");
                    return;
                }

                if (!shouldOverride) return;

                // --- Apply overrides every frame ---

                // Disable VSync
                SetVSync(false);

                // FrameSkipMode depends on interpolation
                if (Mod.InterpolationEnabled)
                {
                    // FrameSkipMode.Off enables the accumulator in DoUpdate -> 60hz game logic
                    Main.FrameSkipMode = FrameSkipMode.Off;
                }
                else
                {
                    // FrameSkipMode.On skips accumulator -> every frame is a full tick -> game speed scales
                    Main.FrameSkipMode = FrameSkipMode.On;
                }

                // Both Capped and Uncapped use variable timestep.
                // Capped mode uses our Stopwatch-based limiter in DoUpdate_Prefix (more accurate).
                ReflectionCache.IsFixedTimeStepProp?.SetValue(__instance, false, null);

                // Handle interpolation state transitions
                bool interpolating = Mod.InterpolationEnabled && !IsGamePaused();
                if (interpolating && !_wasInterpolating)
                {
                    // Just enabled interpolation — clear stale keyframes
                    KeyframeStore.Clear();
                    _firstKeyframeCaptured = false;
                }
                _wasInterpolating = interpolating;
            }
            catch { }
        }

        private static void SetVSync(bool enabled)
        {
            try
            {
                if (ReflectionCache.VSyncProp == null)
                    return;

                // Only call ApplyChanges when VSync state actually changes.
                // ApplyChanges() triggers a device reset which disposes ALL render targets.
                // If we call it every frame, we'd crash on the next Draw.
                if (enabled == _vsyncCurrent) return;

                var gdm = Main.graphics;
                if (gdm == null) return;
                ReflectionCache.VSyncProp.SetValue(gdm, enabled, null);
                ReflectionCache.ApplyChangesMethod?.Invoke(gdm, null);
                _vsyncCurrent = enabled;

                // Device reset just disposed all render targets.
                // Skip the next Draw call (same Tick) to avoid ObjectDisposedException.
                // Terraria will recreate render targets on the next full-tick Draw.
                _skipNextDraw = true;
                _log?.Info($"VSync changed to {enabled}, skipping next draw for device reset");
            }
            catch { }
        }

        #endregion

        #region Patch 3: DoUpdate prefix

        /// <summary>
        /// Save accumulator value before DoUpdate body modifies it.
        /// Priority.First ensures this runs before Core's EventPatches prefix.
        /// </summary>
        public static void DoUpdate_Prefix()
        {
            try
            {
                // Stopwatch-based frame limiter for Capped mode
                // (XNA's IsFixedTimeStep has ~7fps overshoot due to timer granularity)
                if (Mod.Enabled && Mod.Mode == "Capped" && !Main.gameMenu)
                {
                    double targetMs = 1000.0 / Mod.MaxFps;
                    double elapsed = _frameLimiter.Elapsed.TotalMilliseconds;
                    if (elapsed < targetMs)
                    {
                        // Sleep for most of the wait (saves CPU), spin-wait for the last bit (precision)
                        double remaining = targetMs - elapsed;
                        if (remaining > 2.0)
                            Thread.Sleep((int)(remaining - 1.5));
                        while (_frameLimiter.Elapsed.TotalMilliseconds < targetMs)
                            Thread.SpinWait(100);
                    }
                    _frameLimiter.Restart();
                }

                if (!ShouldInterpolate()) return;

                _accumulatorBeforeUpdate = Main.UpdateTimeAccumulator;
            }
            catch { }
        }

        #endregion

        #region Patch 4: DoUpdate postfix

        /// <summary>
        /// After DoUpdate: detect if game logic ran, capture keyframes, compute PartialTick.
        /// Priority.Last ensures this runs after Core's EventPatches postfix.
        /// </summary>
        public static void DoUpdate_Postfix()
        {
            try
            {
                if (!ShouldInterpolate())
                {
                    FrameState.Active = false;
                    return;
                }

                double accAfter = Main.UpdateTimeAccumulator;

                // Detect full tick: accumulator decreased (normal case at >60fps)
                // OR accumulator >= TARGET_FRAME_TIME (lag case: drained but elapsed > TFT)
                bool wasFullTick = (accAfter < _accumulatorBeforeUpdate) ||
                                   (accAfter >= _targetFrameTime);

                if (wasFullTick)
                {
                    if (_firstKeyframeCaptured)
                    {
                        // Shift: previous End becomes new Begin
                        SwapKeyframes();
                    }

                    // Capture End keyframe (current entity state after game logic)
                    KeyframeStore.CaptureEndKeyframe();

                    if (!_firstKeyframeCaptured)
                    {
                        // First tick: copy End to Begin (no prior state to interpolate from)
                        CopyEndToBegin();
                        _firstKeyframeCaptured = true;
                    }

                    FrameState.WasFullTick = true;
                    FrameState.TickCount++;
                }
                else
                {
                    FrameState.WasFullTick = false;
                }

                // Compute PartialTick: how far between Begin and End are we?
                // accAfter / TARGET_FRAME_TIME gives 0..1 progression
                float pt = (float)(accAfter / _targetFrameTime);
                if (pt < 0f) pt = 0f;
                if (pt > 1f) pt = 1f;
                FrameState.PartialTick = pt;
                FrameState.IsPartialTick = !wasFullTick;
                FrameState.Active = _firstKeyframeCaptured;

                // Periodic diagnostics (every ~5 seconds at 60 UPS)
                _diagFrameCount++;
                if (wasFullTick) _diagFullTickCount++;
                if (_diagFullTickCount >= 300)
                {
                    _log?.Info($"[diag] {_diagFrameCount} frames / {_diagFullTickCount} ticks " +
                        $"({_diagFrameCount / (float)_diagFullTickCount:F1}x), " +
                        $"Active={FrameState.Active}, pt={pt:F3}");
                    _diagFrameCount = 0;
                    _diagFullTickCount = 0;
                }
            }
            catch { }
        }

        #endregion

        #region Patch 5: DoDraw prefix

        /// <summary>
        /// Apply interpolated positions to all entities before rendering.
        /// Priority.First ensures this runs before Core's DoDraw prefix.
        /// Returns false to skip the entire DoDraw when render targets are invalid (device reset).
        /// </summary>
        public static bool DoDraw_Prefix()
        {
            try
            {
                // After a device reset (VSync change), render targets are disposed.
                // Skip the entire DoDraw to avoid ObjectDisposedException on RenderTarget2D.
                // Terraria recreates render targets during the next Draw that actually runs.
                if (_skipNextDraw)
                {
                    _skipNextDraw = false;
                    return false;
                }

                if (!FrameState.Active) return true;

                Interpolator.ApplyAll();
                PollMouse();
            }
            catch { }
            return true;
        }

        /// <summary>
        /// Poll hardware mouse position and update Main.mouseX/mouseY every render frame.
        /// Vanilla only updates these during DoUpdate (60hz). This gives responsive cursor at display rate.
        /// </summary>
        private static bool _mouseLoggedOnce;

        private static void PollMouse()
        {
            if (!Mod.MouseEveryFrame) return;
            if (!FrameState.IsPartialTick) return; // Full ticks handle mouse normally

            try
            {
                // Mouse.GetState() -> MouseState struct
                var mouseState = Mouse.GetState();
                int rawX = mouseState.X;
                int rawY = mouseState.Y;

                // Apply RawMouseScale (usually 1.0, changes with DPI scaling)
                var scale = PlayerInput.RawMouseScale;
                float scaleX = scale.X;
                float scaleY = scale.Y;

                int mouseX = (int)(rawX * scaleX);
                int mouseY = (int)(rawY * scaleY);

                Main.mouseX = mouseX;
                Main.mouseY = mouseY;

                if (!_mouseLoggedOnce)
                {
                    _log?.Info($"PollMouse: first poll OK - raw=({rawX},{rawY}), scaled=({mouseX},{mouseY})");
                    _mouseLoggedOnce = true;
                }
            }
            catch (Exception ex)
            {
                if (!_mouseLoggedOnce)
                {
                    _log?.Error($"PollMouse error: {ex.Message}");
                    _mouseLoggedOnce = true;
                }
            }
        }

        #endregion

        #region Patch 6: DoDraw finalizer

        /// <summary>
        /// Restore real (non-interpolated) positions after rendering.
        /// Uses a Harmony FINALIZER instead of postfix — finalizers run even when
        /// the original method throws an exception. This prevents interpolated
        /// positions from being permanently stuck on entities after a DoDraw crash
        /// (which would corrupt game logic on subsequent ticks).
        /// </summary>
        public static Exception DoDraw_Finalizer(Exception __exception)
        {
            try
            {
                if (FrameState.Active)
                    Interpolator.RestoreAll();
            }
            catch { }

            if (__exception != null)
                _log?.Error($"DoDraw threw: {__exception.GetType().Name}: {__exception.Message}");

            return __exception; // propagate original exception
        }

        #endregion

        #region Patch 7: Camera sub-pixel

        /// <summary>
        /// Transpiler that removes the integer snap on screenPosition in DoDraw_UpdateCameraPosition.
        /// Vanilla does: screenPosition.X = (int)screenPosition.X; (and Y)
        /// IL pattern: conv.i4 (float->int) + conv.r4 (int->float) = truncation.
        /// We NOP both instructions to preserve the sub-pixel fractional position.
        /// </summary>
        public static IEnumerable<CodeInstruction> CameraPosition_Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var codes = new List<CodeInstruction>(instructions);
            int patched = 0;

            for (int i = 0; i < codes.Count - 1; i++)
            {
                if (codes[i].opcode == OpCodes.Conv_I4 && codes[i + 1].opcode == OpCodes.Conv_R4)
                {
                    codes[i].opcode = OpCodes.Nop;
                    codes[i + 1].opcode = OpCodes.Nop;
                    patched++;
                }
            }

            _log?.Info($"Camera transpiler: removed {patched} integer snaps");
            return codes;
        }

        #endregion

        #region Patch 8: Lighting engine skip

        // Lighting engine runs a 4-state cycle advancing once per LightTiles call.
        // At 60 ticks/sec, it needs exactly 4 calls/tick = 240 calls/sec.
        // Above 240fps, excess calls cause per-frame lights (held torch) to flicker
        // because the Blur state clears the light list each pass.
        // Fix: cap at 4 LightTiles calls per game tick (1 full + 3 partial).
        private static int _lightCallsThisTick;
        private static long _lastLightTick;
        private const int MAX_LIGHT_CALLS_PER_TICK = 4;

        public static bool LightTiles_Prefix()
        {
            if (!Mod.Enabled || Mod.Mode == "VSync (Vanilla)" || !Mod.InterpolationEnabled)
                return true;

            if (!FrameState.Active)
                return true;

            long currentTick = FrameState.TickCount;
            if (currentTick != _lastLightTick)
            {
                _lastLightTick = currentTick;
                _lightCallsThisTick = 0;
            }

            _lightCallsThisTick++;
            if (_lightCallsThisTick > MAX_LIGHT_CALLS_PER_TICK)
                return false; // cap reached — skip to prevent torch flicker

            return true;
        }

        #endregion

        #region Helpers

        private static bool ShouldInterpolate()
        {
            return Mod.Enabled && Mod.Mode != "VSync (Vanilla)" &&
                   Mod.InterpolationEnabled && !IsGamePaused();
        }

        private static bool IsGamePaused()
        {
            try
            {
                return Main.gamePaused || Main.gameMenu;
            }
            catch { return true; }
        }

        /// <summary>
        /// Shift keyframes: End becomes the new Begin for the next interpolation cycle.
        /// </summary>
        private static void SwapKeyframes()
        {
            Array.Copy(KeyframeStore.PlayerEnd, KeyframeStore.PlayerBegin, KeyframeStore.PlayerEnd.Length);
            Array.Copy(KeyframeStore.NpcEnd, KeyframeStore.NpcBegin, KeyframeStore.NpcEnd.Length);
            Array.Copy(KeyframeStore.ProjEnd, KeyframeStore.ProjBegin, KeyframeStore.ProjEnd.Length);
            // Dust disabled
            Array.Copy(KeyframeStore.GoreEnd, KeyframeStore.GoreBegin, KeyframeStore.GoreEnd.Length);
            Array.Copy(KeyframeStore.ItemEnd, KeyframeStore.ItemBegin, KeyframeStore.ItemEnd.Length);
            Array.Copy(KeyframeStore.CombatTextEnd, KeyframeStore.CombatTextBegin, KeyframeStore.CombatTextEnd.Length);
            Array.Copy(KeyframeStore.PopupTextEnd, KeyframeStore.PopupTextBegin, KeyframeStore.PopupTextEnd.Length);
            // Trail disabled

            // Copy active states
            Array.Copy(KeyframeStore.NpcActiveEnd, KeyframeStore.NpcActiveBegin, KeyframeStore.NpcActiveEnd.Length);
            Array.Copy(KeyframeStore.ProjActiveEnd, KeyframeStore.ProjActiveBegin, KeyframeStore.ProjActiveEnd.Length);
            // Dust disabled
            Array.Copy(KeyframeStore.GoreActiveEnd, KeyframeStore.GoreActiveBegin, KeyframeStore.GoreActiveEnd.Length);
            Array.Copy(KeyframeStore.ItemActiveEnd, KeyframeStore.ItemActiveBegin, KeyframeStore.ItemActiveEnd.Length);
            Array.Copy(KeyframeStore.CombatTextActiveEnd, KeyframeStore.CombatTextActiveBegin, KeyframeStore.CombatTextActiveEnd.Length);
            Array.Copy(KeyframeStore.PopupTextActiveEnd, KeyframeStore.PopupTextActiveBegin, KeyframeStore.PopupTextActiveEnd.Length);

            // Copy player velocity for teleport detection
            // (velocity at end of tick = velocity at start of next tick)
            var players = Main.player;
            if (players != null)
            {
                int count = Math.Min(players.Length, ReflectionCache.MaxPlayers);
                for (int i = 0; i < count; i++)
                {
                    var p = players[i];
                    if (p == null) continue;
                    KeyframeStore.PlayerVelBegin[i * 2 + 0] = ReflectionCache.PlayerVelX(p);
                    KeyframeStore.PlayerVelBegin[i * 2 + 1] = ReflectionCache.PlayerVelY(p);
                }
            }
        }

        /// <summary>
        /// Copy End keyframe to Begin (used for first tick when there's no prior state).
        /// </summary>
        private static void CopyEndToBegin()
        {
            Array.Copy(KeyframeStore.PlayerEnd, KeyframeStore.PlayerBegin, KeyframeStore.PlayerEnd.Length);
            Array.Copy(KeyframeStore.NpcEnd, KeyframeStore.NpcBegin, KeyframeStore.NpcEnd.Length);
            Array.Copy(KeyframeStore.ProjEnd, KeyframeStore.ProjBegin, KeyframeStore.ProjEnd.Length);
            Array.Copy(KeyframeStore.GoreEnd, KeyframeStore.GoreBegin, KeyframeStore.GoreEnd.Length);
            Array.Copy(KeyframeStore.ItemEnd, KeyframeStore.ItemBegin, KeyframeStore.ItemEnd.Length);
            Array.Copy(KeyframeStore.CombatTextEnd, KeyframeStore.CombatTextBegin, KeyframeStore.CombatTextEnd.Length);
            Array.Copy(KeyframeStore.PopupTextEnd, KeyframeStore.PopupTextBegin, KeyframeStore.PopupTextEnd.Length);

            Array.Copy(KeyframeStore.NpcActiveEnd, KeyframeStore.NpcActiveBegin, KeyframeStore.NpcActiveEnd.Length);
            Array.Copy(KeyframeStore.ProjActiveEnd, KeyframeStore.ProjActiveBegin, KeyframeStore.ProjActiveEnd.Length);
            Array.Copy(KeyframeStore.GoreActiveEnd, KeyframeStore.GoreActiveBegin, KeyframeStore.GoreActiveEnd.Length);
            Array.Copy(KeyframeStore.ItemActiveEnd, KeyframeStore.ItemActiveBegin, KeyframeStore.ItemActiveEnd.Length);
            Array.Copy(KeyframeStore.CombatTextActiveEnd, KeyframeStore.CombatTextActiveBegin, KeyframeStore.CombatTextActiveEnd.Length);
            Array.Copy(KeyframeStore.PopupTextActiveEnd, KeyframeStore.PopupTextActiveBegin, KeyframeStore.PopupTextActiveEnd.Length);
        }

        #endregion
    }
}
