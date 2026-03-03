using System;
using HarmonyLib;
using Terraria;
using TerrariaModder.Core.Reflection;

namespace Randomizer.Modules
{
    /// <summary>
    /// Periodically changes effective gravity by applying extra vertical force.
    /// Player.Update() resets gravity = defaultGravity (0.2f) every frame,
    /// so we add/subtract velocity.Y each frame to simulate different gravity.
    /// </summary>
    public class GravityModule : ModuleBase, IUpdatable
    {
        public override string Id => "gravity";
        public override string Name => "Gravity Chaos";
        public override string Description => "Gravity shifts every 30-60 seconds";
        public override string Tooltip => "Gravity randomly changes between 0.25x (floaty) and 2.5x (heavy) every 30-60 seconds. Can be very disorienting — use with caution!";
        public override bool IsDangerous => true;

        internal static GravityModule Instance;

        private int _tickCounter;
        private int _nextChangeAt;
        private float _gravityMultiplier = 1.0f; // 1.0 = normal
        private Random _rng;

        private const float DefaultGravity = 0.2f;

        public override void BuildShuffleMap()
        {
            Instance = this;
            _rng = new Random(Seed.Seed ^ Id.GetHashCode());
            _tickCounter = 0;
            _nextChangeAt = 60 * (30 + _rng.Next(31)); // 30-60 seconds at 60fps
            _gravityMultiplier = 1.0f;
        }

        public void OnUpdate()
        {
            if (!Enabled || !Game.InWorld) return;

            _tickCounter++;
            if (_tickCounter >= _nextChangeAt)
            {
                _tickCounter = 0;
                _nextChangeAt = 60 * (30 + _rng.Next(31));

                // Multiplier: 0.25x to 2.5x gravity (default is 1.0x)
                _gravityMultiplier = 0.25f + (float)_rng.NextDouble() * 2.25f;
                Log.Info($"[Randomizer] Gravity multiplier changed to {_gravityMultiplier:F2}x");
            }

            // Apply extra gravity force each frame
            // Since Player.Update resets gravity to 0.2f, we add the DIFFERENCE
            // between desired and default gravity to the player's velocity.
            // This runs in OnPreUpdate (before Player.Update), so the extra force
            // stacks with the normal gravity applied during Player.Update.
            float extraForce = DefaultGravity * (_gravityMultiplier - 1.0f);
            if (Math.Abs(extraForce) < 0.001f) return;

            try
            {
                var player = Main.player[Main.myPlayer];
                if (player == null) return;

                player.velocity.Y += extraForce;
            }
            catch (Exception ex)
            {
                Log?.Error($"[Randomizer] Gravity update error: {ex.Message}");
            }
        }

        public override void ApplyPatches(Harmony harmony)
        {
            // No Harmony patches needed — gravity is applied per-frame in OnUpdate
        }

        public override void RemovePatches(Harmony harmony)
        {
            _gravityMultiplier = 1.0f;
        }
    }
}
