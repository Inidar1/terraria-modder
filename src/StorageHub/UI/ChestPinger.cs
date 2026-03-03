using System;
using Terraria;
using Microsoft.Xna.Framework;
using TerrariaModder.Core.Logging;

namespace StorageHub.UI
{
    /// <summary>
    /// Handles chest ping visualization - draws a highlight effect at a chest's world position.
    /// </summary>
    public class ChestPinger
    {
        private readonly ILogger _log;

        // Ping state
        private int _pingChestIndex = -1;
        private int _pingTileX = -1;
        private int _pingTileY = -1;
        private int _pingTimer = 0;
        private const int PingDuration = 180; // 3 seconds at 60fps

        public ChestPinger(ILogger log)
        {
            _log = log;
        }

        /// <summary>
        /// Start pinging a chest at the given index.
        /// </summary>
        public void PingChest(int chestIndex)
        {
            if (chestIndex < 0) return;
            _pingChestIndex = chestIndex;
            _pingTileX = -1;
            _pingTileY = -1;
            _pingTimer = PingDuration;
            _log?.Debug($"[ChestPinger] Pinging chest {chestIndex}");
        }

        /// <summary>
        /// Start pinging a specific tile position.
        /// </summary>
        public void PingTile(int tileX, int tileY)
        {
            if (tileX < 0 || tileY < 0) return;
            _pingChestIndex = -1;
            _pingTileX = tileX;
            _pingTileY = tileY;
            _pingTimer = PingDuration;
            _log?.Debug($"[ChestPinger] Pinging tile ({tileX}, {tileY})");
        }

        /// <summary>
        /// Stop the current ping.
        /// </summary>
        public void StopPing()
        {
            _pingChestIndex = -1;
            _pingTileX = -1;
            _pingTileY = -1;
            _pingTimer = 0;
        }

        /// <summary>
        /// Whether a ping is currently active.
        /// </summary>
        public bool IsPinging => _pingTimer > 0;

        /// <summary>
        /// Get the chest index being pinged.
        /// </summary>
        public int PingTargetIndex => _pingChestIndex;

        /// <summary>
        /// Update the ping effect. Call every frame.
        /// </summary>
        public void Update()
        {
            if (_pingTimer <= 0) return;

            _pingTimer--;

            int tileX;
            int tileY;
            if (!TryGetActivePingTile(out tileX, out tileY))
            {
                _pingTimer = 0;
                return;
            }

            // Spawn dust every few frames for visual effect
            if (_pingTimer % 3 == 0)
            {
<<<<<<< HEAD
                SpawnPingDust(tileX, tileY);
            }
        }

        private bool TryGetActivePingTile(out int tileX, out int tileY)
        {
            tileX = 0;
            tileY = 0;

            if (_pingChestIndex >= 0)
            {
                // Spawn dust particles at chest location
                try
=======
                var chestArray = Main.chest;
                if (chestArray == null || _pingChestIndex >= chestArray.Length) return;

                var chest = chestArray[_pingChestIndex];
                if (chest == null) return;

                int chestX = chest.x;
                int chestY = chest.y;

                // Spawn dust every few frames for visual effect
                if (_pingTimer % 3 == 0)
>>>>>>> inidar-main
                {
                    var chestArray = _chestArrayField?.GetValue(null) as Array;
                    if (chestArray == null || _pingChestIndex >= chestArray.Length)
                        return false;

                    var chest = chestArray.GetValue(_pingChestIndex);
                    if (chest == null)
                        return false;

                    // Null check fields before accessing
                    if (_chestXField == null || _chestYField == null)
                        return false;

                    var xVal = _chestXField.GetValue(chest);
                    var yVal = _chestYField.GetValue(chest);
                    if (xVal == null || yVal == null)
                        return false;

                    tileX = (int)xVal;
                    tileY = (int)yVal;
                    return true;
                }
                catch (Exception ex)
                {
                    _log?.Debug($"[ChestPinger] Update error: {ex.Message}");
                    return false;
                }
            }

            if (_pingTileX >= 0 && _pingTileY >= 0)
            {
                tileX = _pingTileX;
                tileY = _pingTileY;
                return true;
            }

            return false;
        }

        private void SpawnPingDust(int tileX, int tileY)
        {
            try
            {
                // Convert tile coords to world coords (center of 2x2 chest)
                float worldX = (tileX + 1) * 16;
                float worldY = (tileY + 1) * 16;

                // Spawn multiple dust particles in a ring pattern
                var rng = new Random();
                for (int i = 0; i < 2; i++)
                {
                    float offsetX = (float)(rng.NextDouble() * 32 - 16);
                    float offsetY = (float)(rng.NextDouble() * 32 - 16);

                    var position = new Vector2(worldX + offsetX, worldY + offsetY);

                    // Dust type 204 is golden sparkle
                    Dust.NewDust(position, 16, 16, 204, 0f, 0f, 0, new Color(255, 220, 100, 255), 1.5f);
                }
            }
            catch
            {
                // Silently fail - dust is just visual flair
            }
        }

        /// <summary>
        /// Get chest world coordinates for the currently pinged chest.
        /// Returns null if not pinging or chest not found.
        /// </summary>
        public (int x, int y)? GetPingWorldPosition()
        {
            if (_pingChestIndex < 0 && (_pingTileX < 0 || _pingTileY < 0))
                return null;

            if (_pingTileX >= 0 && _pingTileY >= 0)
                return (_pingTileX, _pingTileY);

            try
            {
                var chestArray = Main.chest;
                if (chestArray == null || _pingChestIndex >= chestArray.Length) return null;

                var chest = chestArray[_pingChestIndex];
                if (chest == null) return null;

                int chestX = chest.x;
                int chestY = chest.y;

                return (chestX, chestY);
            }
            catch
            {
                return null;
            }
        }
    }
}
