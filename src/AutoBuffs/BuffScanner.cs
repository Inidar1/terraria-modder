using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Terraria;

namespace AutoBuffs
{
    /// <summary>
    /// Optimized tile scanner for buff-granting furniture.
    /// </summary>
    public static class BuffScanner
    {
        // Tile IDs (verified for 1.4.5)
        private const ushort TILE_CRYSTAL_BALL = 125;
        private const ushort TILE_AMMO_BOX = 287;
        private const ushort TILE_BEWITCHING_TABLE = 354;
        private const ushort TILE_SHARPENING_STATION = 377;
        private const ushort TILE_WAR_TABLE = 464;
        private const ushort TILE_SLICE_OF_CAKE = 621;

        // Buff IDs (verified for 1.4.5)
        private const int BUFF_CLAIRVOYANCE = 29;
        private const int BUFF_AMMO_BOX = 93;
        private const int BUFF_BEWITCHED = 150;
        private const int BUFF_SHARPENED = 159;
        private const int BUFF_SUGAR_RUSH = 192;
        private const int BUFF_WAR_TABLE = 348;

        // Timing
        private const int SCAN_CADENCE_TICKS = 10;
        private const int EFFECTIVELY_INFINITE_BUFF_TICKS = 60 * 60 * 24;
        private const int SUGAR_RUSH_DURATION_TICKS = 60 * 60 * 2;
        private const int SUGAR_RUSH_REAPPLY_THRESHOLD = 60 * 2;

        private static int _tickCounter = 0;
        private static bool _initialized = false;
        private static bool _hasLoggedFirstScan = false;

        // Fast lookup for tile types we care about
        private static readonly HashSet<ushort> _buffTileTypes = new HashSet<ushort>
        {
            TILE_CRYSTAL_BALL, TILE_AMMO_BOX, TILE_BEWITCHING_TABLE,
            TILE_SHARPENING_STATION, TILE_WAR_TABLE, TILE_SLICE_OF_CAKE
        };

        // Mapping from tile type to buff info
        private struct BuffInfo
        {
            public int BuffId;
            public bool IsSugarRush;
            public Func<bool> IsEnabled;
        }

        private static readonly Dictionary<ushort, BuffInfo> _tileToBuffMap = new Dictionary<ushort, BuffInfo>();

        public static void Reset()
        {
            _tickCounter = 0;
            _hasLoggedFirstScan = false;
        }

        private static bool EnsureInitialized()
        {
            if (_initialized) return true;

            try
            {
                // Initialize tile-to-buff mapping
                _tileToBuffMap[TILE_CRYSTAL_BALL] = new BuffInfo
                {
                    BuffId = BUFF_CLAIRVOYANCE,
                    IsSugarRush = false,
                    IsEnabled = () => Mod.EnableCrystalBall
                };
                _tileToBuffMap[TILE_AMMO_BOX] = new BuffInfo
                {
                    BuffId = BUFF_AMMO_BOX,
                    IsSugarRush = false,
                    IsEnabled = () => Mod.EnableAmmoBox
                };
                _tileToBuffMap[TILE_BEWITCHING_TABLE] = new BuffInfo
                {
                    BuffId = BUFF_BEWITCHED,
                    IsSugarRush = false,
                    IsEnabled = () => Mod.EnableBewitchingTable
                };
                _tileToBuffMap[TILE_SHARPENING_STATION] = new BuffInfo
                {
                    BuffId = BUFF_SHARPENED,
                    IsSugarRush = false,
                    IsEnabled = () => Mod.EnableSharpeningStation
                };
                _tileToBuffMap[TILE_WAR_TABLE] = new BuffInfo
                {
                    BuffId = BUFF_WAR_TABLE,
                    IsSugarRush = false,
                    IsEnabled = () => Mod.EnableWarTable
                };
                _tileToBuffMap[TILE_SLICE_OF_CAKE] = new BuffInfo
                {
                    BuffId = BUFF_SUGAR_RUSH,
                    IsSugarRush = true,
                    IsEnabled = () => Mod.EnableSliceOfCake
                };

                _initialized = true;
                Mod.Log("BuffScanner initialized");
            }
            catch (Exception ex)
            {
                Mod.Log($"BuffScanner init error: {ex.Message}");
                _initialized = false;
            }

            return _initialized;
        }

        public static void TryScan(Player player, int scanRadius)
        {
            if (!EnsureInitialized()) return;

            // Throttle scans for performance
            if (++_tickCounter < SCAN_CADENCE_TICKS)
                return;
            _tickCounter = 0;

            try
            {
                ScanAndApplyBuffs(player, scanRadius);
            }
            catch (Exception ex)
            {
                Mod.Log($"Scan error: {ex.Message}");
            }
        }

        private static void ScanAndApplyBuffs(Player player, int scanRadius)
        {
            // Get player tile position
            Vector2 position = player.position;
            int playerTileX = (int)(position.X / 16f);
            int playerTileY = (int)(position.Y / 16f);

            // Get world bounds
            int maxTilesX = Main.maxTilesX;
            int maxTilesY = Main.maxTilesY;

            // Calculate scan bounds
            int minX = Math.Max(0, playerTileX - scanRadius);
            int maxX = Math.Min(maxTilesX - 1, playerTileX + scanRadius);
            int minY = Math.Max(0, playerTileY - scanRadius);
            int maxY = Math.Min(maxTilesY - 1, playerTileY + scanRadius);

            int radiusSq = scanRadius * scanRadius;

            // Pre-check which buffs we need
            var neededTiles = new HashSet<ushort>();
            var foundTiles = new HashSet<ushort>();

            foreach (var kvp in _tileToBuffMap)
            {
                if (!kvp.Value.IsEnabled()) continue;

                int buffIndex = player.FindBuffIndex(kvp.Value.BuffId);

                if (kvp.Value.IsSugarRush)
                {
                    if (buffIndex < 0)
                    {
                        neededTiles.Add(kvp.Key);
                    }
                    else
                    {
                        if (player.buffTime[buffIndex] <= SUGAR_RUSH_REAPPLY_THRESHOLD)
                        {
                            neededTiles.Add(kvp.Key);
                        }
                    }
                }
                else
                {
                    if (buffIndex < 0)
                    {
                        neededTiles.Add(kvp.Key);
                    }
                }
            }

            // Early exit if no buffs needed
            if (neededTiles.Count == 0)
            {
                Mod.LogDebug("All buffs active, skipping scan");
                return;
            }

            Mod.LogDebug($"Scanning for {neededTiles.Count} buff tiles around ({playerTileX}, {playerTileY})");

            // Scan tiles
            for (int x = minX; x <= maxX && foundTiles.Count < neededTiles.Count; x++)
            {
                int dx = x - playerTileX;
                int dxSq = dx * dx;

                // Skip entire column if too far
                if (dxSq > radiusSq) continue;

                for (int y = minY; y <= maxY; y++)
                {
                    int dy = y - playerTileY;
                    if (dxSq + dy * dy > radiusSq)
                        continue;

                    Tile tile = Main.tile[x, y];

                    // Check if tile is active
                    if (!tile.active()) continue;

                    // Get tile type
                    ushort tileType = tile.type;

                    // Check if this is a tile we need and haven't found yet
                    if (neededTiles.Contains(tileType) && !foundTiles.Contains(tileType))
                    {
                        foundTiles.Add(tileType);

                        // Early exit if we found everything
                        if (foundTiles.Count >= neededTiles.Count)
                            break;
                    }
                }
            }

            // Log first scan
            if (!_hasLoggedFirstScan && foundTiles.Count > 0)
            {
                _hasLoggedFirstScan = true;
                Mod.Log($"First scan complete - found {foundTiles.Count} furniture");
            }

            // Apply buffs for found tiles
            foreach (ushort tileType in foundTiles)
            {
                if (_tileToBuffMap.TryGetValue(tileType, out BuffInfo info))
                {
                    ApplyBuff(player, info);
                }
            }

            if (foundTiles.Count > 0)
            {
                Mod.LogDebug($"Applied {foundTiles.Count} buffs");
            }
        }

        private static void ApplyBuff(Player player, BuffInfo info)
        {
            int duration = info.IsSugarRush ? SUGAR_RUSH_DURATION_TICKS : EFFECTIVELY_INFINITE_BUFF_TICKS;

            try
            {
                player.AddBuff(info.BuffId, duration, true);
            }
            catch (Exception ex)
            {
                Mod.Log($"Failed to apply buff {info.BuffId}: {ex.Message}");
            }
        }
    }
}
