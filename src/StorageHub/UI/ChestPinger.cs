using System;
using System.Linq;
using System.Reflection;
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

        // Reflection cache
        private static Type _mainType;
        private static Type _dustType;
        private static FieldInfo _chestArrayField;
        private static FieldInfo _chestXField;
        private static FieldInfo _chestYField;
        private static MethodInfo _newDustMethod;
        private static bool _initialized;

        public ChestPinger(ILogger log)
        {
            _log = log;
            InitReflection();
        }

        private void InitReflection()
        {
            if (_initialized) return;

            try
            {
                _mainType = Type.GetType("Terraria.Main, Terraria")
                    ?? Assembly.Load("Terraria").GetType("Terraria.Main");

                if (_mainType != null)
                {
                    _chestArrayField = _mainType.GetField("chest", BindingFlags.Public | BindingFlags.Static);
                }

                var chestType = Type.GetType("Terraria.Chest, Terraria")
                    ?? Assembly.Load("Terraria").GetType("Terraria.Chest");

                if (chestType != null)
                {
                    _chestXField = chestType.GetField("x", BindingFlags.Public | BindingFlags.Instance);
                    _chestYField = chestType.GetField("y", BindingFlags.Public | BindingFlags.Instance);
                }

                _dustType = Type.GetType("Terraria.Dust, Terraria")
                    ?? Assembly.Load("Terraria").GetType("Terraria.Dust");

                if (_dustType != null)
                {
                    // Dust.NewDust(Vector2, int, int, int, ...) - many overloads
                    _newDustMethod = _dustType.GetMethod("NewDust", BindingFlags.Public | BindingFlags.Static,
                        null, new[] { typeof(object), typeof(int), typeof(int), typeof(int), typeof(float), typeof(float), typeof(int), typeof(object), typeof(float) }, null);

                    // Try simpler overload
                    if (_newDustMethod == null)
                    {
                        var methods = _dustType.GetMethods(BindingFlags.Public | BindingFlags.Static);
                        foreach (var m in methods)
                        {
                            if (m.Name == "NewDust")
                            {
                                var parms = m.GetParameters();
                                if (parms.Length >= 4 && parms.Length <= 10)
                                {
                                    _newDustMethod = m;
                                    break;
                                }
                            }
                        }
                    }
                }

                _initialized = true;
            }
            catch (Exception ex)
            {
                _log?.Warn($"[ChestPinger] InitReflection failed: {ex.Message}");
                _initialized = true;
            }
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

                // Create Vector2 for position
                var vector2Type = AppDomain.CurrentDomain.GetAssemblies()
                    .Select(a => a.GetType("Microsoft.Xna.Framework.Vector2"))
                    .FirstOrDefault(t => t != null);

                if (vector2Type == null) return;

                var vector2Ctor = vector2Type.GetConstructor(new[] { typeof(float), typeof(float) });
                if (vector2Ctor == null) return;

                // Spawn multiple dust particles in a ring pattern
                for (int i = 0; i < 2; i++)
                {
                    float offsetX = (float)(new Random().NextDouble() * 32 - 16);
                    float offsetY = (float)(new Random().NextDouble() * 32 - 16);

                    var position = vector2Ctor.Invoke(new object[] { worldX + offsetX, worldY + offsetY });

                    // Try simple NewDust call - Dust type 204 is golden sparkle
                    if (_newDustMethod != null)
                    {
                        var parms = _newDustMethod.GetParameters();
                        object[] args;

                        // Build arguments based on method signature
                        if (parms.Length == 4)
                        {
                            args = new object[] { position, 0, 0, 204 };
                        }
                        else if (parms.Length == 5)
                        {
                            args = new object[] { position, 0, 0, 204, 1.5f };
                        }
                        else if (parms.Length >= 9)
                        {
                            // Full overload with color
                            var colorType = AppDomain.CurrentDomain.GetAssemblies()
                                .Select(a => a.GetType("Microsoft.Xna.Framework.Color"))
                                .FirstOrDefault(t => t != null);

                            object color = null;
                            if (colorType != null)
                            {
                                var colorCtor = colorType.GetConstructor(new[] { typeof(int), typeof(int), typeof(int), typeof(int) });
                                color = colorCtor?.Invoke(new object[] { 255, 220, 100, 255 });
                            }

                            args = new object[] { position, 16, 16, 204, 0f, 0f, 0, color, 1.5f };
                        }
                        else
                        {
                            args = new object[] { position, 16, 16, 204 };
                        }

                        _newDustMethod.Invoke(null, args);
                    }
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
                var chestArray = _chestArrayField?.GetValue(null) as Array;
                if (chestArray == null || _pingChestIndex >= chestArray.Length) return null;

                var chest = chestArray.GetValue(_pingChestIndex);
                if (chest == null) return null;

                // Null check fields before accessing
                if (_chestXField == null || _chestYField == null) return null;

                var xVal = _chestXField.GetValue(chest);
                var yVal = _chestYField.GetValue(chest);
                if (xVal == null || yVal == null) return null;

                int chestX = (int)xVal;
                int chestY = (int)yVal;

                return (chestX, chestY);
            }
            catch
            {
                return null;
            }
        }
    }
}
