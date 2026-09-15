using System;
using System.Reflection;
using HarmonyLib;
using Terraria;
using TerrariaModder.Core;
using TerrariaModder.Core.Logging;

namespace SkipIntro
{
    public class Mod : IMod, IModLifecycle
    {
        public string Id => "skip-intro";
        public string Name => "Skip Intro";
        public string Version => "2.0.1";

        private ILogger _log;
        private Harmony _harmony;
        private bool _patchesApplied;
        private static bool _hasSkipped = false;
        private static FieldInfo _isAsyncLoadCompleteField;
        private static FieldInfo _splashCounterField;
        private static ILogger _staticLog;

        // NOTE: This mod intentionally does NOT implement OnConfigChanged
        // to test the "restart required" functionality

        public void Initialize(ModContext context)
        {
            _log = context.Logger;
            _staticLog = _log;

            var config = context.GetConfig<SkipIntroConfig>();
            if (config != null && !config.Enabled)
            {
                _log.Info("Skip Intro is disabled in config");
                return;
            }

            if (Environment.GetEnvironmentVariable("TERRARIA_MODDER_DEDSERV") == "1")
            {
                _log.Info("SkipIntro: dedicated server — skipping");
                return;
            }

            _harmony = new Harmony("com.terrariamodder.skipintro");

            _log.Info("Skip Intro initialized; patches will apply when game content is ready");
        }

        private void ApplyPatches()
        {
            if (_patchesApplied || _harmony == null) return;

            try
            {
                // _isAsyncLoadComplete and splashCounter are private — must use reflection
                _isAsyncLoadCompleteField = typeof(Main).GetField("_isAsyncLoadComplete", BindingFlags.NonPublic | BindingFlags.Static);
                _splashCounterField = typeof(Main).GetField("splashCounter", BindingFlags.NonPublic | BindingFlags.Instance);

                if (_isAsyncLoadCompleteField == null)
                {
                    _log.Warn("Could not find _isAsyncLoadComplete field");
                }

                var doUpdateMethod = typeof(Main).GetMethod("DoUpdate", BindingFlags.NonPublic | BindingFlags.Instance);
                if (doUpdateMethod != null)
                {
                    var postfix = typeof(Mod).GetMethod("DoUpdate_Postfix", BindingFlags.Public | BindingFlags.Static);
                    _harmony.Patch(doUpdateMethod, postfix: new HarmonyMethod(postfix));
                    _patchesApplied = true;
                    _log.Info("Successfully patched Main.DoUpdate");
                }
                else
                {
                    _log.Warn("Could not find Main.DoUpdate method");
                }
            }
            catch (Exception ex)
            {
                _log.Error($"Patch error: {ex.Message}");
            }
        }

        public static void DoUpdate_Postfix(Main __instance)
        {
            try
            {
                if (_hasSkipped) return;

                if (!Main.showSplash)
                {
                    _hasSkipped = true;
                    return;
                }

                if (_isAsyncLoadCompleteField != null)
                {
                    bool isAsyncLoadComplete = (bool)_isAsyncLoadCompleteField.GetValue(null);
                    if (isAsyncLoadComplete)
                    {
                        if (_splashCounterField != null)
                        {
                            _splashCounterField.SetValue(__instance, 99999);
                            _hasSkipped = true;
                            _staticLog?.Info("Intro splash skipped!");
                        }
                        else
                        {
                            Main.showSplash = false;
                            _hasSkipped = true;
                            _staticLog?.Info("Intro splash skipped (fallback)");
                        }
                    }
                }
            }
            catch
            {
                // Silently mark as skipped to prevent repeated attempts on error
                _hasSkipped = true;
            }
        }

        public void OnContentReady(ModContext context)
        {
            ApplyPatches();
        }

        public void OnWorldLoad() { }
        public void OnWorldUnload() { }

        public void Unload()
        {
            _harmony?.UnpatchAll("com.terrariamodder.skipintro");
            _patchesApplied = false;

            // Reset static state for hot-reload support
            _hasSkipped = false;
            _isAsyncLoadCompleteField = null;
            _splashCounterField = null;
            _staticLog = null;

            _log.Info("Skip Intro unloaded");
        }
    }
}
