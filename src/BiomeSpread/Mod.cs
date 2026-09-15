using System;
using System.Reflection;
using Terraria.ID;
using HarmonyLib;
using Terraria;
using TerrariaModder.Core;
using TerrariaModder.Core.Logging;

namespace BiomeSpread
{
    public class Mod : IMod, IModLifecycle
    {
        public string Id => "biome-spread";
        public string Name => "Biome Spread Control";
        public string Version => "2.0.1";

        private static ILogger _log;
        private static ModContext _context;
        private static Harmony _harmony;
        private static BiomeSpreadConfig _config;

        // Config
        internal static bool DisableSpread = true;

        public void Initialize(ModContext context)
        {
            _context = context;
            _log = context.Logger;
            _config = context.GetConfig<BiomeSpreadConfig>();

            LoadConfig();

            _log.Info("Biome Spread Control initializing...");

            try
            {
                _harmony = new Harmony("com.terrariamodder.biomespread");
                _log.Info("Patches will be applied when game content is ready.");
            }
            catch (Exception ex)
            {
                _log.Error($"Init failed: {ex.Message}");
            }
        }

        private static void ApplyPatches()
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
                // Native infection suppression deliberately still permits evil grass on
                // bare dirt/mud. Natural SpreadGrass calls use repeat:false; generation
                // and explicit recursive grass operations keep their native behavior.
                _harmony.Patch(typeof(WorldGen).GetMethod("SpreadGrass", BindingFlags.Public | BindingFlags.Static),
                    prefix: new HarmonyMethod(typeof(Mod), nameof(SpreadGrass_Prefix)));
            }
            catch (Exception ex)
            {
                _log?.Error($"Delayed patch error: {ex.Message}");
            }
        }

        private void LoadConfig()
        {
            if (_config == null) return;
            DisableSpread = _config.DisableSpread;
        }

        public void OnConfigChanged()
        {
            LoadConfig();
            if (!DisableSpread)
            {
                try { WorldGen.AllowedToSpreadInfections = true; } catch { }
            }
            _log.Info($"Config reloaded - DisableSpread: {DisableSpread}");
        }

        public void OnContentReady(ModContext context) { if (_harmony != null) ApplyPatches(); }

        public void OnWorldLoad()
        {
            _log.Info($"World loaded - spread {(DisableSpread ? "DISABLED" : "enabled")}");
        }

        public void OnWorldUnload()
        {
            try { Terraria.WorldGen.AllowedToSpreadInfections = true; } catch { }
        }

        public void Unload()
        {
            _harmony?.UnpatchAll("com.terrariamodder.biomespread");
            _log.Info("Biome Spread Control unloaded");
        }

        public static bool SpreadGrass_Prefix(int grass, bool repeat)
        {
            if (!DisableSpread || repeat || WorldGen.isGeneratingOrLoadingWorld) return true;
            return grass != TileID.CorruptGrass && grass != TileID.CrimsonGrass &&
                grass != TileID.HallowedGrass && grass != TileID.GolfGrassHallowed &&
                grass != TileID.CorruptJungleGrass && grass != TileID.CrimsonJungleGrass;
        }

        public static void HardUpdateWorld_Prefix()
        {
            if (!DisableSpread) return;
            WorldGen.AllowedToSpreadInfections = false;
        }
    }
}
