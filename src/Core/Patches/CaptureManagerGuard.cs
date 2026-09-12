using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Terraria;
using TerrariaModder.Core.Logging;

namespace TerrariaModder.Core.Patches
{
    /// <summary>
    /// Guard against CaptureManager static constructor crash during early initialization.
    ///
    /// Problem: LegacyLighting.Rebuild() accesses CaptureManager.Instance.IsCapturing,
    /// which triggers CaptureManager's static field initializer (cctor). The cctor runs
    /// new CaptureManager(), whose constructor accesses Main.instance.GraphicsDevice.
    /// If GraphicsDevice is null (intermittent timing issue during startup with Harmony
    /// patching), the constructor throws NullReferenceException. Because it's in a cctor,
    /// the TypeInitializationException is permanent — every future access to CaptureManager
    /// throws for the rest of the session.
    ///
    /// Fix: Harmony patches on CaptureManager:
    /// 1. Finalizer on constructor — swallows NRE so cctor completes. _camera stays null.
    /// 2. Prefix on every method/property that accesses _camera — if null, return safe default.
    ///    (IsCapturing, DrawTick, Dispose, GetProgress, Capture)
    ///
    /// Applied during LoadPlugins() — before Main.Initialize() runs.
    /// </summary>
    internal static class CaptureManagerGuard
    {
        private static ILogger _log;
        private static FieldInfo _cameraField;
        private static bool _applied;

        public static void Apply(ILogger logger, bool gameReady = false)
        {
            _log = logger;
            if (_applied) return;
            // Mono can initialize the target type while Harmony builds a patch. Doing that
            // before GraphicsDevice exists poisons CaptureManager's static initializer.
            if (!gameReady && Type.GetType("Mono.Runtime") != null)
            {
                _log?.Debug("CaptureManagerGuard deferred until graphics are ready on Mono");
                return;
            }

            try
            {
                var captureManagerType = typeof(Main).Assembly.GetType("Terraria.Graphics.Capture.CaptureManager");
                if (captureManagerType == null)
                {
                    _log?.Debug("CaptureManagerGuard: CaptureManager type not found, skipping");
                    return;
                }

                _cameraField = captureManagerType.GetField("_camera",
                    BindingFlags.NonPublic | BindingFlags.Instance);

                var ctor = captureManagerType.GetConstructor(
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                    null, Type.EmptyTypes, null);

                if (ctor == null)
                {
                    _log?.Debug("CaptureManagerGuard: CaptureManager() ctor not found");
                    return;
                }

                var harmony = new Harmony("com.terrariamodder.core.capturemanagerguard");

                // Patch 1: Finalizer on constructor — swallows NRE
                harmony.Patch(ctor, finalizer: new HarmonyMethod(
                    typeof(CaptureManagerGuard).GetMethod(nameof(CaptureManager_Finalizer),
                        BindingFlags.NonPublic | BindingFlags.Static)));

                // Patch 2-6: Guard every method that accesses _camera
                PatchMethods(harmony, captureManagerType, "get_IsCapturing", nameof(CameraNull_BoolFalse));
                PatchMethods(harmony, captureManagerType, "DrawTick", nameof(CameraNull_Skip));
                PatchMethods(harmony, captureManagerType, "Dispose", nameof(CameraNull_Skip));
                PatchMethods(harmony, captureManagerType, "GetProgress", nameof(CameraNull_FloatZero));
                PatchMethods(harmony, captureManagerType, "Capture", nameof(CameraNull_Skip));

                _applied = true;
                _log?.Info("CaptureManagerGuard applied — ctor finalizer + null-camera guards");
            }
            catch (Exception ex)
            {
                _log?.Warn($"CaptureManagerGuard failed to apply: {ex.Message}");
            }
        }

        private static void PatchMethods(Harmony harmony, Type type, string methodName, string prefixName)
        {
            var methods = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .Where(method => method.Name == methodName)
                .ToArray();

            if (methods.Length > 0)
            {
                var prefix = new HarmonyMethod(typeof(CaptureManagerGuard).GetMethod(prefixName,
                    BindingFlags.NonPublic | BindingFlags.Static));
                foreach (var method in methods)
                {
                    harmony.Patch(method, prefix: prefix);
                }
            }
            else
            {
                _log?.Debug($"CaptureManagerGuard: {methodName} not found, skipping");
            }
        }

        private static bool HasCamera(object instance)
        {
            if (_cameraField == null) return true; // Can't check, let original run
            return _cameraField.GetValue(instance) != null;
        }

        // --- Finalizer ---

        private static Exception CaptureManager_Finalizer(Exception __exception)
        {
            if (__exception != null)
            {
                _log?.Warn($"CaptureManagerGuard: CaptureManager ctor threw {__exception.GetType().Name} — swallowed (capture mode unavailable this session)");
                return null;
            }
            return null;
        }

        // --- Prefixes for _camera-dependent methods ---

        /// <summary>Skip method entirely when _camera is null (void methods).</summary>
        private static bool CameraNull_Skip(object __instance)
        {
            return HasCamera(__instance);
        }

        /// <summary>Return false when _camera is null (bool properties like IsCapturing).</summary>
        private static bool CameraNull_BoolFalse(object __instance, ref bool __result)
        {
            if (!HasCamera(__instance))
            {
                __result = false;
                return false;
            }
            return true;
        }

        /// <summary>Return 0f when _camera is null (float methods like GetProgress).</summary>
        private static bool CameraNull_FloatZero(object __instance, ref float __result)
        {
            if (!HasCamera(__instance))
            {
                __result = 0f;
                return false;
            }
            return true;
        }
    }
}
