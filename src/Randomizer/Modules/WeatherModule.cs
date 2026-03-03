using System;
using HarmonyLib;
using Terraria;
using TerrariaModder.Core.Reflection;

namespace Randomizer.Modules
{
    /// <summary>
    /// Randomly triggers weather events (rain, blood moon, eclipse) periodically.
    /// Uses Terraria's own StartRain/StopRain/StartSlimeRain etc. for proper state management.
    /// </summary>
    public class WeatherModule : ModuleBase, IUpdatable
    {
        public override string Id => "weather";
        public override string Name => "Weather Chaos";
        public override string Description => "Random weather events every 2-5 minutes";
        public override string Tooltip => "Randomly triggers rain, blood moon, eclipse, or slime rain every 2-5 minutes. Includes a 'chaos combo' mode. Blood moon only triggers at night, eclipse only during day.";

        internal static WeatherModule Instance;

        private int _tickCounter;
        private int _nextChangeAt;
        private Random _rng;

        public override void BuildShuffleMap()
        {
            Instance = this;
            _rng = new Random(Seed.Seed ^ Id.GetHashCode());
            _tickCounter = 0;
            _nextChangeAt = 60 * (120 + _rng.Next(181)); // 2-5 minutes
        }

        public void OnUpdate()
        {
            if (!Enabled || !Game.InWorld) return;

            _tickCounter++;
            if (_tickCounter < _nextChangeAt) return;

            _tickCounter = 0;
            _nextChangeAt = 60 * (120 + _rng.Next(181));

            try
            {
                bool isDaytime = Main.dayTime;
                bool isRaining = Main.raining;
                bool isSlimeRain = Main.slimeRain;

                int choice = _rng.Next(6);
                switch (choice)
                {
                    case 0: // Toggle rain using proper methods
                        if (isRaining)
                        {
                            Main.StopRain();
                            Log.Info("[Randomizer] Weather: rain stopped");
                        }
                        else
                        {
                            Main.StartRain();
                            Log.Info("[Randomizer] Weather: rain started");
                        }
                        break;

                    case 1: // Toggle blood moon (only at night)
                        if (!isDaytime)
                        {
                            Main.bloodMoon = !Main.bloodMoon;
                            Log.Info($"[Randomizer] Weather: blood moon {(Main.bloodMoon ? "ON" : "OFF")}");
                        }
                        break;

                    case 2: // Toggle eclipse (only during day)
                        if (isDaytime)
                        {
                            Main.eclipse = !Main.eclipse;
                            Log.Info($"[Randomizer] Weather: eclipse {(Main.eclipse ? "ON" : "OFF")}");
                        }
                        break;

                    case 3: // Toggle slime rain using proper methods
                        if (isSlimeRain)
                        {
                            Main.StopSlimeRain(true);
                            Log.Info("[Randomizer] Weather: slime rain stopped");
                        }
                        else
                        {
                            Main.StartSlimeRain(true);
                            Log.Info("[Randomizer] Weather: slime rain started");
                        }
                        break;

                    case 4: // Clear all weather
                        Main.StopRain();
                        Main.StopSlimeRain(false);
                        Main.bloodMoon = false;
                        Main.eclipse = false;
                        Log.Info("[Randomizer] Weather: cleared all events");
                        break;

                    case 5: // Chaos — start rain + blood moon or eclipse
                        Main.StartRain();
                        if (isDaytime)
                            Main.eclipse = true;
                        else
                            Main.bloodMoon = true;
                        Log.Info("[Randomizer] Weather: chaos combo!");
                        break;
                }
            }
            catch (Exception ex)
            {
                Log?.Error($"[Randomizer] Weather update error: {ex.Message}");
            }
        }

        public override void ApplyPatches(Harmony harmony)
        {
            // No Harmony patches needed — weather is applied per-frame in OnUpdate
        }

        public override void RemovePatches(Harmony harmony)
        {
            // Clear weather on unload
            try
            {
                Main.StopRain();
                Main.StopSlimeRain(false);
                Main.bloodMoon = false;
                Main.eclipse = false;
            }
            catch (Exception ex)
            {
                Log?.Error($"[Randomizer] Weather cleanup error: {ex.Message}");
            }
        }
    }
}
