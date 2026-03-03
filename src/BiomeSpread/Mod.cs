using System;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using Terraria;
using TerrariaModder.Core;
using TerrariaModder.Core.Logging;

namespace BiomeSpread
{
    public class Mod : IMod
    {
        public string Id => "biome-spread";
        public string Name => "Biome Spread Control";
        public string Version => "1.0.0";

        private static ILogger _log;
        private static ModContext _context;
        private static Harmony _harmony;
        private static Timer _patchTimer;

        // Config
        internal static bool Enabled = true;
        internal static bool DisableSpread = true;

        public void Initialize(ModContext context)
        {
            _context = context;
            _log = context.Logger;

            LoadConfig();

            _log.Info("Biome Spread Control initializing...");

            try
            {
                _harmony = new Harmony("com.terrariamodder.biomespread");
                _patchTimer = new Timer(PatchAfterDelay, null, 5000, Timeout.Infinite);
                _log.Info("Patches will be applied after 5 seconds...");
            }
            catch (Exception ex)
            {
                _log.Error($"Init failed: {ex.Message}");
            }
        }

        private static void PatchAfterDelay(object state)
        {
            try
            {
                _log?.Info("Applying patches now...");

                var hardUpdateMethod = typeof(WorldGen).GetMethod("hardUpdateWorld",
                    BindingFlags.Public | BindingFlags.Static);

                if (hardUpdateMethod == null)
                {
                    _log?.Error("Could not find WorldGen.hardUpdateWorld method");
                    return;
                }

                var prefix = typeof(Mod).GetMethod("HardUpdateWorld_Prefix",
                    BindingFlags.Public | BindingFlags.Static);

                _harmony.Patch(hardUpdateMethod, prefix: new HarmonyMethod(prefix));
                _log?.Info("Successfully patched WorldGen.hardUpdateWorld");
            }
            catch (Exception ex)
            {
                _log?.Error($"Delayed patch error: {ex.Message}");
            }
        }

        private void LoadConfig()
        {
            Enabled = _context.Config.Get("enabled", true);
            DisableSpread = _context.Config.Get("disableSpread", true);
        }

        public void OnConfigChanged()
        {
            LoadConfig();
            _log.Info($"Config reloaded - Enabled: {Enabled}, DisableSpread: {DisableSpread}");
        }

        public void OnWorldLoad()
        {
            _log.Info($"World loaded - spread {(Enabled && DisableSpread ? "DISABLED" : "enabled")}");
        }

        public void OnWorldUnload() { }

        public void Unload()
        {
            _patchTimer?.Dispose();
            _harmony?.UnpatchAll("com.terrariamodder.biomespread");
            _patchTimer = null;
            _log.Info("Biome Spread Control unloaded");
        }

        public static void HardUpdateWorld_Prefix()
        {
            if (!Enabled || !DisableSpread) return;
            WorldGen.AllowedToSpreadInfections = false;
        }
    }
}
