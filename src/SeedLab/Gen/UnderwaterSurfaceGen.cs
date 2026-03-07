using System;
using Terraria;
using Terraria.Utilities;
using TerrariaModder.Core.Logging;

namespace SeedLab.Gen
{
    /// <summary>
    /// Runs as the very last worldgen step (via FinalPassPatch, after Final Cleanup).
    ///
    /// Layout (Y increases downward):
    ///   clearFromY    ← Y=5 (world top) — must clear all the way up; surface terrain reaches Y≈50+
    ///   ...clear sky...
    ///   seaLevelY     ← sea surface (55 tiles above seal)
    ///   ...ocean water...
    ///   sealTopY      ← stone seal, 4 tiles thick (worldSurface + 15)
    ///   (underground — completely untouched)
    ///
    /// Steps:
    ///   1. Erase entire zone (clearFromY → sealTopY) — removes all surface terrain
    ///   2. Place hermetic stone seal at sealTopY — prevents water draining underground
    ///   3. Build all-sand islands above seaLevelY (center rect + scatter triangular)
    ///   4. Flood ocean zone (seaLevelY → sealTopY) with water
    ///   5. Grow palm trees on sand above sea
    ///   6. Set spawn point on center island top
    /// </summary>
    public static class UnderwaterSurfaceGen
    {
        private static ILogger _log;

        private const ushort TileStone = 1;
        private const ushort TileSand  = 53;

        public static void Initialize(ILogger log) => _log = log;

        public static void DoUnderwaterSurface()
        {
            _log?.Info("[SeedLab] UnderwaterSurfaceGen: Starting");
            try
            {
                Run();
                _log?.Info("[SeedLab] UnderwaterSurfaceGen: Done");
            }
            catch (Exception ex)
            {
                _log?.Error($"[SeedLab] UnderwaterSurfaceGen: Error: {ex}");
            }
        }

        private static void Run()
        {
            var rng     = WorldGen.genRand;
            int worldW  = Main.maxTilesX;
            int centerX = worldW / 2;

            // Seal placed 15 tiles into underground — solidly below all surface terrain.
            int sealTopY   = (int)Main.worldSurface + 15;
            int sealBotY   = sealTopY + 4;
            int seaLevelY  = sealTopY - 55;    // ~110 feet of water
            int clearFromY = 5;               // clear from world top — surface terrain can reach Y≈50+

            _log?.Info($"[SeedLab] worldSurface={Main.worldSurface:F0}  sealTopY={sealTopY}  seaLevelY={seaLevelY}  clearFromY={clearFromY}");

            // ── Step 1: Erase entire zone ──────────────────────────────────────
            // Clears all surface terrain, trees, grass, walls. Underground stays.
            for (int x = 5; x < worldW - 5; x++)
                for (int y = clearFromY; y < sealTopY; y++)
                {
                    var t = Main.tile[x, y];
                    t.active(false);
                    t.liquid = 0;
                    t.wall   = 0;
                    t.slope(0);
                }

            // ── Step 2: Hermetic stone seal (full width, 4 tiles) ──────────────
            for (int x = 5; x < worldW - 5; x++)
                for (int y = sealTopY; y <= sealBotY; y++)
                {
                    var t = Main.tile[x, y];
                    t.active(true);
                    t.type = TileStone;
                    t.slope(0);
                    t.liquid = 0;
                }

            // ── Step 3: Center rectangular island (all sand) ───────────────────
            int centerHalfW = rng.Next(40, 61);            // 80–120 tiles wide
            int centerPeakH = rng.Next(20, 31);            // 20–30 tiles above sea
            int centerLeft  = centerX - centerHalfW;
            int centerRight = centerX + centerHalfW;
            int centerTopY  = seaLevelY - centerPeakH;

            BuildRectIsland(centerLeft, centerRight, centerTopY, sealTopY);

            // ── Step 4: Near-center spawn islands ─────────────────────────────
            int spawnOffset = rng.Next(70, 101);
            int spawnHalfW  = rng.Next(9, 14);
            int spawnPeakH  = rng.Next(8, 15);
            BuildTriIsland(centerX - spawnOffset, spawnHalfW, spawnPeakH, seaLevelY, sealTopY, worldW);
            BuildTriIsland(centerX + spawnOffset, spawnHalfW, spawnPeakH, seaLevelY, sealTopY, worldW);

            // ── Step 5: Scatter small islands ─────────────────────────────────
            int exclusionR = centerHalfW + spawnOffset + spawnHalfW + 30;
            int smallCount = rng.Next(5, 13);
            for (int i = 0; i < smallCount; i++)
            {
                int islandCX;
                int tries = 0;
                do
                {
                    islandCX = rng.Next(2) == 0
                        ? rng.Next(30, Math.Max(31, centerX - exclusionR))
                        : rng.Next(Math.Min(centerX + exclusionR, worldW - 31), worldW - 30);
                    tries++;
                }
                while (tries < 25 && (islandCX < 30 || islandCX > worldW - 30));

                BuildTriIsland(islandCX, rng.Next(8, 21), rng.Next(10, 21), seaLevelY, sealTopY, worldW);
            }

            // ── Step 6: Dungeon marker tower ──────────────────────────────────
            BuildDungeonTower(worldW, seaLevelY, sealTopY);

            // ── Step 7: Flood ocean zone (seaLevelY → sealTopY) ───────────────
            for (int x = 5; x < worldW - 5; x++)
                for (int y = seaLevelY; y < sealTopY; y++)
                {
                    var t = Main.tile[x, y];
                    if (!t.active())
                    {
                        t.liquid = 255;
                        t.liquidType(0); // 0 = water
                    }
                }

            // ── Step 8: Palm trees ────────────────────────────────────────────
            PlantPalmTrees(rng, worldW, seaLevelY, centerLeft, centerRight);

            // ── Step 9: Spawn — set last so nothing can override it ───────────
            Main.spawnTileX = centerX;
            Main.spawnTileY = centerTopY;
            _log?.Info($"[SeedLab] Spawn set to ({centerX}, {centerTopY})");
        }

        // ────────────────────────────────────────────────────────────────────────
        // Rectangular island: flat plateau with 5-tile taper on each edge.
        // All sand. Column fills from colTopY down to sealTopY.
        // ────────────────────────────────────────────────────────────────────────
        private static void BuildRectIsland(int left, int right, int topY, int sealTopY)
        {
            for (int x = left; x <= right; x++)
            {
                int edge    = Math.Min(x - left, right - x);
                int taper   = edge < 5 ? (5 - edge) : 0;
                int colTopY = topY + taper;

                for (int y = colTopY; y < sealTopY; y++)
                {
                    var t = Main.tile[x, y];
                    t.active(true);
                    t.type = TileSand;
                    t.slope(0);
                }
            }
        }

        // ────────────────────────────────────────────────────────────────────────
        // Triangular island: height falls off as t^1.3 from the center peak.
        // All sand. Column fills from colTopY down to sealTopY.
        // ────────────────────────────────────────────────────────────────────────
        private static void BuildTriIsland(int islandCX, int halfW, int peakH, int seaLevelY, int sealTopY, int worldW)
        {
            for (int x = islandCX - halfW; x <= islandCX + halfW; x++)
            {
                if (x < 5 || x >= worldW - 5) continue;

                float frac   = (float)Math.Abs(x - islandCX) / halfW;
                int   localH = (int)(peakH * (1.0 - Math.Pow(frac, 1.3)));
                if (localH <= 0) continue;

                int colTopY = seaLevelY - localH;
                for (int y = colTopY; y < sealTopY; y++)
                {
                    var t = Main.tile[x, y];
                    t.active(true);
                    t.type = TileSand;
                    t.slope(0);
                }
            }
        }

        // ────────────────────────────────────────────────────────────────────────
        // Dungeon marker tower: hollow stone shaft above Main.dungeonX.
        // Cap is 12 tiles above sea; floods naturally with ocean water.
        // ────────────────────────────────────────────────────────────────────────
        private static void BuildDungeonTower(int worldW, int seaLevelY, int sealTopY)
        {
            int dungeonX     = Main.dungeonX;
            int platformTopY = seaLevelY - 12;
            int shaftL       = Math.Max(6, dungeonX - 2);
            int shaftR       = Math.Min(worldW - 7, dungeonX + 2);

            for (int x = shaftL; x <= shaftR; x++)
            {
                bool isWall = (x == shaftL || x == shaftR);
                for (int y = platformTopY; y < sealTopY; y++)
                {
                    var t = Main.tile[x, y];
                    if (y <= platformTopY + 2 || isWall)
                    {
                        t.active(true);
                        t.type = TileStone;
                        t.slope(0);
                    }
                    else
                    {
                        t.active(false);
                        t.slope(0);
                    }
                }
            }
        }

        // ────────────────────────────────────────────────────────────────────────
        // Palm trees: scan for sand above sea level, plant probabilistically.
        // ────────────────────────────────────────────────────────────────────────
        private static void PlantPalmTrees(UnifiedRandom rng, int worldW, int seaLevelY, int centerLeft, int centerRight)
        {
            int x = 5;
            while (x < worldW - 5)
            {
                int topY = FindSandSurface(x, seaLevelY - 2, seaLevelY);
                if (topY >= 0)
                {
                    double prob = (x >= centerLeft && x <= centerRight) ? 0.06 : 0.14;
                    if (rng.NextDouble() < prob)
                    {
                        WorldGen.GrowPalmTree(x, topY);
                        x += rng.Next(3, 6);
                        continue;
                    }
                }
                x++;
            }
        }

        private static int FindSandSurface(int x, int startY, int endY)
        {
            for (int y = startY; y < endY; y++)
            {
                var t = Main.tile[x, y];
                if (t.active() && t.type == TileSand)
                    return y;
            }
            return -1;
        }
    }
}
