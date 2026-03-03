using System;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using Terraria;
using TerrariaModder.Core;
using TerrariaModder.Core.Logging;

namespace AutoBuffs
{
    public class Mod : IMod
    {
        public string Id => "auto-buffs";
        public string Name => "Auto Furniture Buffs";
        public string Version => "1.0.0";

        private static ILogger _log;
        private static ModContext _context;
        private static Harmony _harmony;
        private static Timer _patchTimer;
        private static int _initDelayFrames = 300;

        // Config values (cached for performance)
        internal static bool Enabled = true;
        internal static int ScanRadius = 40;
        internal static bool EnableCrystalBall = true;
        internal static bool EnableAmmoBox = true;
        internal static bool EnableBewitchingTable = true;
        internal static bool EnableSharpeningStation = true;
        internal static bool EnableWarTable = true;
        internal static bool EnableSliceOfCake = true;
        internal static bool DebugLogging = false;

        public void Initialize(ModContext context)
        {
            _context = context;
            _log = context.Logger;

            LoadConfig();

            _log.Info("Auto Buffs initializing...");

            try
            {
                _harmony = new Harmony("com.terrariamodder.autobuffs");

                // Delay patching by 5 seconds to avoid early initialization issues
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

                var updateMethod = typeof(Player).GetMethod("Update", new Type[] { typeof(int) });
                var postfix = typeof(Mod).GetMethod("PlayerUpdatePostfix", BindingFlags.Public | BindingFlags.Static);

                if (updateMethod != null && postfix != null)
                {
                    _harmony.Patch(updateMethod, postfix: new HarmonyMethod(postfix));
                    _log?.Info("Successfully patched Player.Update");
                }
                else
                {
                    _log?.Error($"Failed to find patch targets - Update: {updateMethod != null}, Postfix: {postfix != null}");
                }
            }
            catch (Exception ex)
            {
                _log?.Error($"Delayed patch error: {ex.Message}");
            }
        }

        private void LoadConfig()
        {
            Enabled = _context.Config.Get("enabled", true);
            ScanRadius = _context.Config.Get("scanRadius", 40);
            EnableCrystalBall = _context.Config.Get("crystalBall", true);
            EnableAmmoBox = _context.Config.Get("ammoBox", true);
            EnableBewitchingTable = _context.Config.Get("bewitchingTable", true);
            EnableSharpeningStation = _context.Config.Get("sharpeningStation", true);
            EnableWarTable = _context.Config.Get("warTable", true);
            EnableSliceOfCake = _context.Config.Get("sliceOfCake", true);
            DebugLogging = _context.Config.Get("debugLogging", false);
        }

        public void OnConfigChanged()
        {
            LoadConfig();
            _log.Info($"Config reloaded - Enabled: {Enabled}, Radius: {ScanRadius}");
        }

        public void OnWorldLoad()
        {
            _initDelayFrames = 300;
            BuffScanner.Reset();
            _log.Info("World loaded - scanner reset");
        }

        public void OnWorldUnload()
        {
            BuffScanner.Reset();
        }

        public void Unload()
        {
            _patchTimer?.Dispose();
            _harmony?.UnpatchAll("com.terrariamodder.autobuffs");
            _patchTimer = null;
            _log.Info("Auto Buffs unloaded");
        }

        internal static void Log(string message) => _log?.Info(message);
        internal static void LogDebug(string message)
        {
            if (DebugLogging) _log?.Debug(message);
        }

        public static void PlayerUpdatePostfix(Player __instance, int i)
        {
            try
            {
                if (__instance == null) return;

                // Only process local player
                if (i != Main.myPlayer) return;

                // Skip if on menu
                if (Main.gameMenu) return;

                // Wait for initialization
                if (_initDelayFrames > 0)
                {
                    _initDelayFrames--;
                    return;
                }

                if (!Enabled) return;

                BuffScanner.TryScan(__instance, ScanRadius);
            }
            catch (Exception ex)
            {
                // Log only once to avoid spam
                LogDebug($"Update error: {ex.Message}");
            }
        }
    }
}
