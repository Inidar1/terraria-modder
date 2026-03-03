using System;
using System.Collections.Generic;
using Terraria;
using TerrariaModder.Core.Logging;
using StorageHub.Config;
using StorageHub.Shared;

namespace StorageHub.Crafting
{
    /// <summary>
    /// Detects nearby crafting stations by scanning tiles around the player.
    ///
    /// Design:
    /// - Fast path: Reads Player.adjTile[] which Terraria maintains every frame
    ///   with all TileCountsAs equivalences resolved (free, covers vanilla range)
    /// - Extended scan: For tier ranges beyond vanilla (~5 tiles), scans tiles
    ///   directly and applies TileCountsAs equivalences
    /// - At Tier 3+, detected stations are remembered
    /// - Environment conditions: Reads Player.adjWaterSource/adjHoney/adjLava/ZoneSnow/ZoneGraveyard
    /// </summary>
    public class StationDetector
    {
        private readonly ILogger _log;
        private readonly StorageHubConfig _config;

        // Vanilla adjTile scan range is ~5 tiles (Player.tileRangeX/Y)
        private const int VanillaRangeTiles = 5;

        // Maximum scan range in tiles (capped for performance)
        private const int MaxScanRange = 100;

        /// <summary>
        /// Get the effective scan range for station detection (in tiles).
        /// Uses tier-based range, capped at MaxScanRange for performance.
        /// </summary>
        private int GetScanRange()
        {
            int tierRange = ProgressionTier.GetRange(_config.Tier);

            // Convert pixels to tiles
            int rangeInTiles = tierRange == int.MaxValue ? MaxScanRange : tierRange / 16;

            // Cap at max for performance
            return Math.Min(rangeInTiles, MaxScanRange);
        }

        public StationDetector(ILogger log, StorageHubConfig config)
        {
            _log = log;
            _config = config;
        }

        /// <summary>
        /// Scan tiles around the player and return detected crafting stations.
        /// Uses two strategies:
        /// 1. Read Player.adjTile[] for vanilla-range stations (free, includes equivalences)
        /// 2. Extended tile scan for tier ranges beyond vanilla
        /// </summary>
        public HashSet<int> ScanNearbyStations()
        {
            var stations = new HashSet<int>();

            try
            {
                var player = Main.player[Main.myPlayer];
                if (player == null) return stations;

                // Fast path: read Player.adjTile[] (maintained by Terraria every frame)
                // This covers vanilla range (~5 tiles) with ALL equivalences resolved
                GetAdjTileStations(player, stations);

                // Extended scan: only if tier range exceeds vanilla range
                int scanRange = GetScanRange();
                if (scanRange > VanillaRangeTiles)
                {
                    ScanExtendedRange(player, stations, scanRange);
                }

                // Remember stations for Tier 3+
                if (stations.Count > 0 && ProgressionTier.HasStationMemory(_config.Tier) && _config.StationMemoryEnabled)
                {
                    foreach (var tileId in stations)
                    {
                        if (_config.RememberedStations.Add(tileId))
                        {
                            _log.Debug($"Remembered crafting station tile: {tileId}");
                        }
                    }
                }

                _log.Debug($"[StationDetector] Scan complete: {stations.Count} station types (Tier {_config.Tier}, range {scanRange} tiles, adjTile=fast, extended={scanRange > VanillaRangeTiles})");
                if (stations.Count > 0)
                {
                    _log.Debug($"[StationDetector] Stations found: [{string.Join(", ", stations)}]");
                }
            }
            catch (Exception ex)
            {
                _log.Error($"ScanNearbyStations failed: {ex.Message}");
            }

            return stations;
        }

        /// <summary>
        /// Scan environment conditions from the local player.
        /// Reads adjWater, adjHoney, adjLava, ZoneSnow, ZoneGraveyard.
        /// </summary>
        public EnvironmentState ScanEnvironmentConditions()
        {
            var state = new EnvironmentState();
            try
            {
                var player = Main.player[Main.myPlayer];
                if (player == null) return state;

                state.HasWater = player.adjWaterSource;
                state.HasHoney = player.adjHoney;
                state.HasLava = player.adjLava;
                state.InSnow = player.ZoneSnow;
                state.InGraveyard = player.ZoneGraveyard;
            }
            catch (Exception ex)
            {
                _log.Error($"ScanEnvironmentConditions failed: {ex.Message}");
            }
            return state;
        }

        /// <summary>
        /// Read Player.adjTile[] to get all stations within vanilla range.
        /// Terraria resolves TileCountsAs equivalences in SetAdjTile(), so
        /// adjTile[17] (Furnace) will be true if an Adamantite Forge is nearby.
        /// </summary>
        private void GetAdjTileStations(Player player, HashSet<int> stations)
        {
            try
            {
                var adjTile = player.adjTile;
                if (adjTile == null) return;

                for (int i = 0; i < adjTile.Length; i++)
                {
                    if (adjTile[i] && CraftingStations.AllTileIds.Contains(i))
                    {
                        stations.Add(i);
                    }
                }
            }
            catch (Exception ex)
            {
                _log.Error($"GetAdjTileStations failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Scan tiles beyond vanilla range using direct tile array access.
        /// Applies TileCountsAs equivalences for found stations.
        /// </summary>
        private void ScanExtendedRange(Player player, HashSet<int> stations, int scanRange)
        {
            try
            {
                int playerTileX = (int)(player.position.X / 16);
                int playerTileY = (int)(player.position.Y / 16);

                var tiles = Main.tile;
                if (tiles == null) return;

                int maxTilesX = Main.maxTilesX;
                int maxTilesY = Main.maxTilesY;

                var tileCountsAs = Recipe.TileCountsAs;

                // Only scan the extended area (skip vanilla range already covered by adjTile)
                int innerRange = VanillaRangeTiles;

                for (int x = playerTileX - scanRange; x <= playerTileX + scanRange; x++)
                {
                    if (x < 0 || x >= maxTilesX) continue;

                    for (int y = playerTileY - scanRange; y <= playerTileY + scanRange; y++)
                    {
                        if (y < 0 || y >= maxTilesY) continue;

                        // Skip vanilla range (already covered by adjTile)
                        if (x >= playerTileX - innerRange && x <= playerTileX + innerRange &&
                            y >= playerTileY - innerRange && y <= playerTileY + innerRange)
                            continue;

                        try
                        {
                            var tile = tiles[x, y];
                            if (tile == null) continue;
                            if (!tile.active()) continue;

                            int tileType = tile.type;
                            if (tileType <= 0) continue;

                            if (CraftingStations.AllTileIds.Contains(tileType))
                            {
                                stations.Add(tileType);
                                // Apply equivalences (e.g., Adamantite Forge → Hellforge → Furnace → Campfire)
                                ApplyEquivalences(tileType, stations, tileCountsAs);
                            }
                        }
                        catch
                        {
                            // Skip tiles that cause errors
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _log.Error($"ScanExtendedRange failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Recursively apply TileCountsAs equivalences (mirrors Player.SetAdjTile logic).
        /// E.g., Adamantite Forge (133) → Hellforge (77) → Furnace (17) → Campfire (215)
        /// </summary>
        private void ApplyEquivalences(int tileType, HashSet<int> stations, List<int>[] tileCountsAs)
        {
            if (tileCountsAs == null || tileType < 0 || tileType >= tileCountsAs.Length) return;

            try
            {
                var list = tileCountsAs[tileType];
                if (list == null) return;

                foreach (int equivType in list)
                {
                    if (stations.Add(equivType))
                    {
                        // Recurse for chained equivalences
                        ApplyEquivalences(equivType, stations, tileCountsAs);
                    }
                }
            }
            catch
            {
                // Equivalence lookup failure is non-fatal
            }
        }
    }

    /// <summary>
    /// Environment conditions near the player (proximity-based).
    /// </summary>
    public struct EnvironmentState
    {
        public bool HasWater;
        public bool HasHoney;
        public bool HasLava;
        public bool InSnow;
        public bool InGraveyard;
    }
}
