using System;
using Terraria;
using Terraria.Utilities;
using TerrariaModder.Core.Logging;

namespace SeedLab.Gen
{
    /// <summary>
    /// Gog biome — a blue-painted infectious biome that spawns where Surface Mushroom
    /// meets Forest. Tiles resist mining below Mythril-tier pickaxe power.
    ///
    /// Worldgen (via FinalPassPatch):
    ///   SpawnAtBorders() — scan surface (y &lt; worldSurface) for MushroomGrass (70)
    ///                      adjacent to Grass (2); 1/20 chance → paint blob.
    ///   SpawnAtSpawn()   — paint a larger blob around the world spawn point.
    ///
    /// Runtime (via Mod.OnUpdate each frame):
    ///   UpdateSpread()   — 200 random surface-zone checks/frame; Gog tiles spread
    ///                      to active neighbours within ±3 tiles. Matches vanilla
    ///                      hardmode corruption rate on surface tiles.
    ///
    /// Verified IDs (decomp):
    ///   PaintID.DeepBluePaint = 21
    ///   TileID.Grass          = 2
    ///   TileID.MushroomGrass  = 70
    ///   Mythril pickaxe power = 150
    /// </summary>
    public static class GogGen
    {
        // PaintID.DeepBluePaint = 21 (verified from decomp)
        public const byte GogPaint = 21;

        // Minimum pickaxe power to mine Gog tiles (Mythril/Orichalcum threshold)
        public const int MythrilPickPower = 150;

        private const int SpreadChecksPerFrame = 200;
        private const int SpreadRadius = 3;
        private const int BorderBlobRadius = 12;
        private const int SpawnBlobRadius = 30;

        // TileID values (verified from decomp)
        private const ushort TileGrass = 2;
        private const ushort TileMushroomGrass = 70;

        private static ILogger _log;
        private static readonly UnifiedRandom _fallbackRng = new UnifiedRandom();

        public static void Initialize(ILogger log) => _log = log;

        // ── Worldgen ──────────────────────────────────────────────────────────

        /// <summary>
        /// Scan surface for Mushroom/Forest borders; paint Gog blobs at 1/20 hit tiles.
        /// </summary>
        public static void SpawnAtBorders()
        {
            var rng = WorldGen.genRand ?? _fallbackRng;
            int worldW = Main.maxTilesX;
            int surfaceH = (int)Main.worldSurface;
            int blobs = 0;

            for (int x = 5; x < worldW - 5; x++)
            for (int y = 5; y < surfaceH; y++)
            {
                var t = Main.tile[x, y];
                if (t == null || !t.active() || t.type != TileMushroomGrass) continue;
                if (!HasAdjacentGrass(x, y)) continue;
                if (rng.Next(20) != 0) continue;

                PaintBlob(x, y, BorderBlobRadius, rng, surfaceH);
                blobs++;
            }

            _log?.Info($"[SeedLab] GogGen: SpawnAtBorders placed {blobs} blob(s)");
        }

        /// <summary>
        /// Paint a Gog blob around the world spawn point.
        /// </summary>
        public static void SpawnAtSpawn()
        {
            var rng = WorldGen.genRand ?? _fallbackRng;
            int surfaceH = (int)Main.worldSurface;
            PaintBlob(Main.spawnTileX, Main.spawnTileY, SpawnBlobRadius, rng, surfaceH);
            _log?.Info($"[SeedLab] GogGen: SpawnAtSpawn painted blob at ({Main.spawnTileX}, {Main.spawnTileY})");
        }

        // ── Runtime spread ────────────────────────────────────────────────────

        /// <summary>
        /// Called every frame. Picks 200 random surface tiles; any Gog-painted tile
        /// attempts to spread to one random neighbour within ±3 tiles.
        /// </summary>
        public static void UpdateSpread()
        {
            if (Main.gameMenu) return;

            var rng = WorldGen.genRand ?? _fallbackRng;
            int worldW = Main.maxTilesX;
            int surfaceH = (int)Main.worldSurface;

            for (int i = 0; i < SpreadChecksPerFrame; i++)
            {
                int x = rng.Next(5, worldW - 5);
                int y = rng.Next(5, surfaceH);

                var t = Main.tile[x, y];
                if (t == null || !t.active() || t.color() != GogPaint) continue;

                int nx = x + rng.Next(-SpreadRadius, SpreadRadius + 1);
                int ny = y + rng.Next(-SpreadRadius, SpreadRadius + 1);
                if (nx < 5 || nx >= worldW - 5 || ny < 5 || ny >= surfaceH) continue;

                var nt = Main.tile[nx, ny];
                if (nt == null || !nt.active() || nt.color() == GogPaint) continue;

                WorldGen.paintTile(nx, ny, GogPaint, broadCast: false, paintEffects: false);
            }
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private static bool HasAdjacentGrass(int x, int y) =>
            IsGrass(x - 1, y) || IsGrass(x + 1, y) ||
            IsGrass(x, y - 1) || IsGrass(x, y + 1);

        private static bool IsGrass(int x, int y)
        {
            if (x < 0 || x >= Main.maxTilesX || y < 0 || y >= Main.maxTilesY) return false;
            var t = Main.tile[x, y];
            return t != null && t.active() && t.type == TileGrass;
        }

        /// <summary>
        /// Paint an organic blob centred at (cx, cy). Probability falls off from 1→0
        /// as distance approaches radius, with randomness creating irregular edges.
        /// </summary>
        private static void PaintBlob(int cx, int cy, int radius, UnifiedRandom rng, int maxY)
        {
            int worldW = Main.maxTilesX;
            for (int dx = -radius; dx <= radius; dx++)
            for (int dy = -radius; dy <= radius; dy++)
            {
                float dist = (float)Math.Sqrt(dx * dx + dy * dy);
                if (dist > radius) continue;

                float prob = 1f - dist / radius;
                if ((float)rng.NextDouble() > prob) continue;

                int nx = cx + dx, ny = cy + dy;
                if (nx < 5 || nx >= worldW - 5 || ny < 5 || ny >= maxY) continue;

                var t = Main.tile[nx, ny];
                if (t == null || !t.active()) continue;

                WorldGen.paintTile(nx, ny, GogPaint, broadCast: false, paintEffects: false);
            }
        }
    }
}
