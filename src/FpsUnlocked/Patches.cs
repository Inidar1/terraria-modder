using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using Terraria;
using Terraria.Enums;
using Terraria.GameInput;
using Terraria.Graphics.CameraModifiers;
using TerrariaModder.Core.Logging;

namespace FpsUnlocked
{
    /// <summary>
    /// Harmony patches for unlocked timing and render interpolation.
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
        private static uint _gameUpdateCountBeforeUpdate;
        private static double _targetFrameTime;
        private static bool _firstKeyframeCaptured;
        private static bool _doUpdateVSyncWritesPatched;
        private static bool _displayModeVSyncWritePatched;
        private static int _diagFrameCount;
        private static int _diagFullTickCount;

        // Saved vanilla state for restoring when switching to VSync mode
        private static FrameSkipMode _savedFrameSkipMode;
        private static bool _wasOverriding;

        // Track interpolation state transitions
        private static bool _wasInterpolating;

        private static bool? _vsyncApplied;
        private static bool _renderTargetsNeedRebuild;
        private static bool _rebuildingRenderTargets;

        // Camera modifiers mutate internal duration and offset state when ApplyTo runs.
        // Partial draw frames reuse the most recent full-tick offset.
        private static Vector2 _lastCameraModifierOffset;
        private static bool _hasCameraModifierOffset;

        // Stopwatch-based frame limiter (more accurate than XNA's IsFixedTimeStep)
        private static readonly Stopwatch _frameLimiter = Stopwatch.StartNew();

        public static void ApplyAll(Harmony harmony, ILogger log)
        {
            _log = log;

            // Read TARGET_FRAME_TIME constant directly
            _targetFrameTime = Main.TARGET_FRAME_TIME;
            _vsyncApplied = GetAppliedVSync();

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
            MethodInfo updateMethod = typeof(Main).GetMethod("Update",
                BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly,
                null, new[] { typeof(GameTime) }, null);
            if (updateMethod != null)
            {
                harmony.Patch(updateMethod,
                    postfix: new HarmonyMethod(typeof(Patches), nameof(Update_Postfix)));
                log.Info("Patch 2: Main.Update timing postfix");
            }

            // --- Patch 3 & 4: DoUpdate prefix/postfix (keyframe capture) ---
            MethodInfo doUpdateMethod = typeof(Main).GetMethod("DoUpdate",
                BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly,
                null, new[] { typeof(GameTime).MakeByRefType() }, null);
            if (doUpdateMethod != null)
            {
                harmony.Patch(doUpdateMethod,
                    prefix: new HarmonyMethod(typeof(Patches), nameof(DoUpdate_Prefix)) { priority = Priority.First },
                    postfix: new HarmonyMethod(typeof(Patches), nameof(DoUpdate_Postfix)) { priority = Priority.Last },
                    transpiler: new HarmonyMethod(typeof(Patches), nameof(DoUpdate_Transpiler)));
                log.Info("Patch 3+4: Main.DoUpdate timing, keyframes, and VSync transpiler");
            }
            else
            {
                log.Error("Patch 3+4: Main.DoUpdate(ref GameTime) not found; unlocked timing is disabled");
            }

            MethodInfo setDisplayModeMethod = typeof(Main).GetMethod(nameof(Main.SetDisplayMode),
                BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly,
                null, new[] { typeof(int), typeof(int), typeof(bool) }, null);
            if (setDisplayModeMethod != null)
            {
                harmony.Patch(setDisplayModeMethod,
                    transpiler: new HarmonyMethod(typeof(Patches), nameof(SetDisplayMode_Transpiler)));
                log.Info("Patch 3a: Main.SetDisplayMode VSync transpiler");
            }
            else
            {
                log.Error("Patch 3a: Main.SetDisplayMode(int, int, bool) not found; unlocked timing is disabled");
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

            MethodInfo drawMethod = typeof(Main).GetMethod("Draw",
                BindingFlags.NonPublic | BindingFlags.Instance);
            if (drawMethod != null)
            {
                harmony.Patch(drawMethod,
                    prefix: new HarmonyMethod(typeof(Patches), nameof(MainDraw_Prefix)) { priority = Priority.First });
                log.Info("Patch 5a: Main.Draw render-target recovery");
            }
            else
            {
                log.Warn("Patch 5a: Main.Draw not found");
            }

            // --- Patch 7: Camera sub-pixel (remove integer snap via transpiler) ---
            var cameraMethod = typeof(Main).GetMethod("DoDraw_UpdateCameraPosition",
                BindingFlags.NonPublic | BindingFlags.Static);
            if (cameraMethod != null)
            {
                harmony.Patch(cameraMethod,
                    prefix: new HarmonyMethod(typeof(Patches), nameof(CameraPosition_Prefix)) { priority = Priority.First },
                    postfix: new HarmonyMethod(typeof(Patches), nameof(CameraPosition_Postfix)) { priority = Priority.Last },
                    transpiler: new HarmonyMethod(typeof(Patches), nameof(CameraPosition_Transpiler)));
                log.Info("Patch 7: camera position timing and sub-pixel transpiler");
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
                log.Info("Patch 8: Lighting.LightTiles full-tick updates");
            }
            else
            {
                log.Warn("Patch 8: Lighting.LightTiles not found - lighting may advance at render rate");
            }

            var updateMainMouse = typeof(PlayerInput).GetMethod(nameof(PlayerInput.UpdateMainMouse),
                BindingFlags.Public | BindingFlags.Static);
            if (updateMainMouse != null)
            {
                harmony.Patch(updateMainMouse,
                    postfix: new HarmonyMethod(typeof(Patches), nameof(UpdateMainMouse_Postfix)) { priority = Priority.Last });
                log.Info("Patch 9: PlayerInput.UpdateMainMouse postfix");
            }
            else
            {
                log.Warn("Patch 9: PlayerInput.UpdateMainMouse not found");
            }

            var cameraModifiers = typeof(CameraModifierStack).GetMethod(nameof(CameraModifierStack.ApplyTo),
                BindingFlags.Public | BindingFlags.Instance);
            if (cameraModifiers != null)
            {
                harmony.Patch(cameraModifiers,
                    prefix: new HarmonyMethod(typeof(Patches), nameof(CameraModifiers_Prefix)) { priority = Priority.First },
                    postfix: new HarmonyMethod(typeof(Patches), nameof(CameraModifiers_Postfix)) { priority = Priority.Last });
                log.Info("Patch 10: CameraModifierStack.ApplyTo full-tick updates");
            }
            else
            {
                log.Warn("Patch 10: CameraModifierStack.ApplyTo not found");
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
        /// Block SuppressDraw while unlocked timing is active so partial frames are rendered.
        /// </summary>
        public static bool SuppressDraw_Prefix()
        {
            if (!ShouldUseUnlockedTiming()) return true;

            // Allow discrete or interpolated draws between 60hz game ticks.
            return false;
        }

        #endregion

        #region Patch 2: Update postfix (timing overrides)

        /// <summary>
        /// Apply VSync, frame-skip, and vanilla-mode timing state after each update.
        /// Runs after Main.Update (which includes DoUpdate).
        /// Settings take effect on the NEXT frame's DoUpdate.
        /// </summary>
        public static IEnumerable<CodeInstruction> DoUpdate_Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var codes = ReplaceVSyncWrites(instructions, out int replaced);
            _doUpdateVSyncWritesPatched = replaced == 2;
            if (_doUpdateVSyncWritesPatched)
                _log?.Info("Main.DoUpdate VSync transpiler replaced 2 setter calls");
            else
                _log?.Error($"Main.DoUpdate VSync transpiler expected 2 setter calls, replaced {replaced}; unlocked timing is disabled");
            return codes;
        }

        public static IEnumerable<CodeInstruction> SetDisplayMode_Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var codes = ReplaceVSyncWrites(instructions, out int replaced);
            _displayModeVSyncWritePatched = replaced == 1;
            if (_displayModeVSyncWritePatched)
                _log?.Info("Main.SetDisplayMode VSync transpiler replaced 1 setter call");
            else
                _log?.Error($"Main.SetDisplayMode VSync transpiler expected 1 setter call, replaced {replaced}; unlocked timing is disabled");
            return codes;
        }

        private static List<CodeInstruction> ReplaceVSyncWrites(
            IEnumerable<CodeInstruction> instructions, out int replaced)
        {
            var codes = new List<CodeInstruction>(instructions);
            MethodInfo replacement = AccessTools.Method(typeof(Patches), nameof(SetTerrariaVSyncRequest));
            replaced = 0;

            if (replacement == null)
                return codes;

            foreach (var code in codes)
            {
                string called = code.operand?.ToString() ?? "";
                if ((code.opcode != OpCodes.Call && code.opcode != OpCodes.Callvirt) ||
                    called.IndexOf("set_SynchronizeWithVerticalRetrace", StringComparison.Ordinal) < 0)
                    continue;

                code.opcode = OpCodes.Call;
                code.operand = replacement;
                replaced++;
            }

            return codes;
        }

        public static void SetTerrariaVSyncRequest(GraphicsDeviceManager graphics, bool requested)
        {
            if (graphics == null || ReflectionCache.VSyncProp == null)
                return;

            if (ShouldUseUnlockedTiming())
                requested = false;

            try
            {
                bool current = (bool)ReflectionCache.VSyncProp.GetValue(graphics, null);
                if (current != requested)
                    ReflectionCache.VSyncProp.SetValue(graphics, requested, null);
            }
            catch (Exception ex)
            {
                _log?.Debug($"[FpsUnlocked] VSync request: {ex}");
            }
        }

        public static void Update_Postfix(object __instance)
        {
            try
            {
                bool shouldOverride = ShouldUseUnlockedTiming();

                if (shouldOverride && !_wasOverriding)
                {
                    _savedFrameSkipMode = Main.FrameSkipMode;
                    _wasOverriding = true;
                    ResetTransitionState();
                    _log?.Info($"FPS override activated - Mode: {Mod.Mode}, MaxFPS: {Mod.MaxFps}, " +
                        $"Interpolation: {Mod.InterpolationEnabled}");
                }
                else if (!shouldOverride && _wasOverriding)
                {
                    Main.FrameSkipMode = _savedFrameSkipMode;
                    _wasOverriding = false;
                    ResetTransitionState();
                    FrameState.Reset();
                    _log?.Info("FPS override deactivated, restoring 60fps");
                }

                if (!shouldOverride)
                {
                    SetVSync(true);
                    ReflectionCache.IsFixedTimeStepProp?.SetValue(__instance, true, null);
                    ReflectionCache.TargetElapsedTimeProp?.SetValue(__instance,
                        TimeSpan.FromSeconds(_targetFrameTime), null);
                    return;
                }

                SetVSync(false);

                // The accumulator in FrameSkipMode.Off keeps game logic at 60hz.
                // Interpolation controls presentation only and must never change game speed.
                Main.FrameSkipMode = FrameSkipMode.Off;
                // DoUpdate selects variable step while focused and fixed 60hz catch-up
                // while inactive. Preserve that choice so background play does not slow.
            }
            catch (Exception ex) { _log?.Debug($"[FpsUnlocked] Update_Postfix: {ex}"); }
        }

        private static void SetVSync(bool enabled)
        {
            try
            {
                if (ReflectionCache.VSyncProp == null)
                    return;

                var gdm = Main.graphics;
                if (gdm == null) return;

                // Main.DoUpdate writes this requested value to true every frame without
                // necessarily applying it. Compare against the graphics device's actual
                // presentation interval before deciding whether a reset is needed.
                bool requested = (bool)ReflectionCache.VSyncProp.GetValue(gdm, null);
                bool? applied = GetAppliedVSync();
                if (applied.HasValue)
                    _vsyncApplied = applied;
                else if (!_vsyncApplied.HasValue)
                    _vsyncApplied = requested;

                if (_vsyncApplied.HasValue && _vsyncApplied.Value == enabled)
                {
                    if (requested != enabled)
                        ReflectionCache.VSyncProp.SetValue(gdm, enabled, null);
                    return;
                }

                if (ReflectionCache.ApplyChangesMethod == null ||
                    ReflectionCache.InitTargetsMethod == null)
                {
                    _log?.Error("VSync transition requires ApplyChanges and Main.InitTargets; unlocked timing is disabled");
                    return;
                }

                if (requested != enabled)
                    ReflectionCache.VSyncProp.SetValue(gdm, enabled, null);

                ReflectionCache.ApplyChangesMethod.Invoke(gdm, null);
                _renderTargetsNeedRebuild = true;
                _vsyncApplied = GetAppliedVSync();
                _log?.Info($"VSync requested {enabled}; applied presentation interval is {GetPresentationInterval()}");
            }
            catch (Exception ex) { _log?.Debug($"[FpsUnlocked] SetVSync: {ex}"); }
        }

        public static bool MainDraw_Prefix()
        {
            if (TerrariaModder.Core.PluginLoader.IsShuttingDown)
                return false;

            if (!_renderTargetsNeedRebuild || _rebuildingRenderTargets)
                return true;

            if (!RebuildRenderTargets())
                return false;

            _renderTargetsNeedRebuild = false;
            return true;
        }

        private static bool RebuildRenderTargets()
        {
            if (Main.instance == null || Main.dedServ || ReflectionCache.InitTargetsMethod == null)
                return false;

            _rebuildingRenderTargets = true;
            bool preventUpdatingTargets = Main.PreventUpdatingTargets;
            try
            {
                Main.PreventUpdatingTargets = true;
                ReflectionCache.InitTargetsMethod.Invoke(Main.instance, null);
                Main.instance.ResetAllContentBasedRenderTargets();
                _log?.Info("Render targets rebuilt before drawing after graphics device reset");
                return true;
            }
            catch (Exception ex)
            {
                _log?.Error($"Render-target rebuild failed after graphics device reset: {ex}");
                return false;
            }
            finally
            {
                Main.PreventUpdatingTargets = preventUpdatingTargets;
                _rebuildingRenderTargets = false;
            }
        }

        internal static bool VSyncWritesPatched => _doUpdateVSyncWritesPatched && _displayModeVSyncWritePatched;

        internal static string GetPresentationInterval()
        {
            try
            {
                return Main.instance?.GraphicsDevice?.PresentationParameters?.PresentationInterval.ToString()
                    ?? "Unavailable";
            }
            catch
            {
                return "Unavailable";
            }
        }

        internal static bool? GetAppliedVSync()
        {
            string interval = GetPresentationInterval();
            if (interval == "Unavailable") return null;
            return interval != PresentInterval.Immediate.ToString();
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
                if (ShouldUseUnlockedTiming() && Mod.Mode == "Capped")
                {
                    double targetMs = 1000.0 / Math.Max(1, Mod.MaxFps);
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

                if (!ShouldUseUnlockedTiming()) return;

                _gameUpdateCountBeforeUpdate = Main.GameUpdateCount;
            }
            catch (Exception ex) { _log?.Debug($"[FpsUnlocked] DoUpdate_Prefix: {ex}"); }
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
                bool timingActive = ShouldUseUnlockedTiming();
                FrameState.TimingActive = timingActive;
                if (!timingActive)
                {
                    FrameState.Active = false;
                    FrameState.WasFullTick = false;
                    FrameState.IsPartialTick = false;
                    return;
                }

                FrameState.FrameCount++;
                double accAfter = Main.UpdateTimeAccumulator;
                bool wasFullTick = Main.GameUpdateCount != _gameUpdateCountBeforeUpdate;

                FrameState.WasFullTick = wasFullTick;
                FrameState.IsPartialTick = !wasFullTick;
                if (wasFullTick)
                    FrameState.TickCount++;

                bool interpolating = Mod.InterpolationEnabled && !IsGamePaused();
                if (interpolating != _wasInterpolating)
                {
                    KeyframeStore.Clear();
                    _firstKeyframeCaptured = false;
                    _hasCameraModifierOffset = false;
                }
                _wasInterpolating = interpolating;

                if (interpolating && wasFullTick)
                {
                    if (_firstKeyframeCaptured)
                        SwapKeyframes();

                    KeyframeStore.CaptureEndKeyframe();
                    if (!_firstKeyframeCaptured)
                    {
                        CopyEndToBegin();
                        _firstKeyframeCaptured = true;
                    }
                }

                float pt = (float)(accAfter / _targetFrameTime);
                if (pt < 0f) pt = 0f;
                if (pt > 1f) pt = 1f;
                FrameState.PartialTick = pt;
                FrameState.Active = interpolating && _firstKeyframeCaptured;

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
            catch (Exception ex) { _log?.Debug($"[FpsUnlocked] DoUpdate_Postfix: {ex}"); }
        }

        #endregion

        #region Patch 5: DoDraw prefix

        /// <summary>
        /// Apply interpolated positions to all entities before rendering.
        /// Priority.First ensures this runs before Core's DoDraw prefix.
        /// </summary>
        public static bool DoDraw_Prefix()
        {
            try
            {
                if (!FrameState.Active) return true;

                Interpolator.ApplyAll();
            }
            catch (Exception ex) { _log?.Debug($"[FpsUnlocked] DoDraw_Prefix: {ex}"); }
            return true;
        }

        /// <summary>
        /// Poll hardware mouse position and update Main.mouseX/mouseY every render frame.
        /// Vanilla only updates these during DoUpdate (60hz). This gives responsive cursor at display rate.
        /// </summary>
        private static bool _mouseLoggedOnce;

        public static void UpdateMainMouse_Postfix()
        {
            if (!Mod.MouseEveryFrame) return;
            if (!FrameState.TimingActive || !FrameState.IsPartialTick) return;
            if (Main.instance == null || !Main.instance.IsActive) return;

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

                PlayerInput.MouseX = mouseX;
                PlayerInput.MouseY = mouseY;
                Main.mouseX = mouseX;
                Main.mouseY = mouseY;

                if (!_mouseLoggedOnce)
                {
                    _log?.Info($"Responsive mouse active - raw=({rawX},{rawY}), scaled=({mouseX},{mouseY})");
                    _mouseLoggedOnce = true;
                }
            }
            catch (Exception ex)
            {
                if (!_mouseLoggedOnce)
                {
                    _log?.Error($"Responsive mouse error: {ex.Message}");
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

        #region Patch 7: Camera timing and sub-pixel positioning

        public static void CameraPosition_Prefix(out Vector2 __state)
        {
            __state = new Vector2(Main.cameraX, Main.cameraY);
        }

        public static void CameraPosition_Postfix(Vector2 __state)
        {
            if (!FrameState.TimingActive || !FrameState.IsPartialTick) return;
            Main.cameraX = __state.X;
            Main.cameraY = __state.Y;
        }

        public static bool CameraModifiers_Prefix(ref Vector2 cameraPosition, out Vector2 __state)
        {
            __state = cameraPosition;
            if (!FrameState.TimingActive || !FrameState.IsPartialTick)
                return true;

            if (_hasCameraModifierOffset)
                cameraPosition += _lastCameraModifierOffset;
            return false;
        }

        public static void CameraModifiers_Postfix(ref Vector2 cameraPosition, Vector2 __state)
        {
            if (!FrameState.TimingActive || FrameState.IsPartialTick) return;
            _lastCameraModifierOffset = cameraPosition - __state;
            _hasCameraModifierOffset = true;
        }

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

        // Lighting state advances with game logic. Partial draw frames reuse the
        // current light map instead of advancing the lighting engine again.
        public static bool LightTiles_Prefix()
        {
            if (!FrameState.TimingActive)
                return true;

            return FrameState.WasFullTick;
        }

        #endregion

        #region Helpers

        private static bool ShouldUseUnlockedTiming()
        {
            return VSyncWritesPatched &&
                   ReflectionCache.ApplyChangesMethod != null &&
                   ReflectionCache.InitTargetsMethod != null &&
                   Mod.Enabled && Mod.Mode != "VSync (Vanilla)" && !Main.gameMenu;
        }

        public static void ResetTransitionState()
        {
            _wasInterpolating = false;
            _firstKeyframeCaptured = false;
            _hasCameraModifierOffset = false;
            _lastCameraModifierOffset = Vector2.Zero;
            _diagFrameCount = 0;
            _diagFullTickCount = 0;
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
