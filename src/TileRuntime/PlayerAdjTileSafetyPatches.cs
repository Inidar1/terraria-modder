using System;
using System.Reflection;
using HarmonyLib;
using Terraria;
using Terraria.ID;
using TerrariaModder.Core.Logging;

namespace TerrariaModder.TileRuntime
{
    /// <summary>
    /// Guards Player adjacency tile bookkeeping from custom tile type overflows.
    /// </summary>
    internal static class PlayerAdjTileSafetyPatches
    {
        private static Harmony _harmony;
        private static ILogger _log;
        private static bool _applied;

        private static FieldInfo _adjTileField;
        private static FieldInfo _oldAdjTileField;
        private static FieldInfo _tileIdCountField;
        private static bool _loggedMissingFields;

        public static void Initialize(ILogger logger)
        {
            _log = logger;
            _harmony = new Harmony("com.terrariamodder.tileruntime.player-adjtile-safety");

            var playerType = typeof(Player);
            _adjTileField = playerType.GetField("adjTile", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            _oldAdjTileField = playerType.GetField("oldAdjTile", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            _tileIdCountField = typeof(TileID).GetField("Count", BindingFlags.Public | BindingFlags.Static);
        }

        public static void ApplyPatches()
        {
            if (_applied)
                return;

            try
            {
                int patched = 0;

                var setAdjTile = typeof(Player).GetMethod("SetAdjTile",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                    null, new[] { typeof(int) }, null);
                if (setAdjTile != null)
                {
                    _harmony.Patch(setAdjTile,
                        prefix: new HarmonyMethod(typeof(PlayerAdjTileSafetyPatches), nameof(SetAdjTile_Prefix)));
                    patched++;
                }

                var updateNearbyCraftingTiles = typeof(Player).GetMethod("UpdateNearbyCraftingTiles",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                    null, Type.EmptyTypes, null);
                if (updateNearbyCraftingTiles != null)
                {
                    _harmony.Patch(updateNearbyCraftingTiles,
                        prefix: new HarmonyMethod(typeof(PlayerAdjTileSafetyPatches), nameof(UpdateNearbyCraftingTiles_Prefix)));
                    patched++;
                }

                _applied = true;
                _log?.Info($"[TileRuntime.PlayerAdjTileSafetyPatches] Applied {patched} patches");
            }
            catch (Exception ex)
            {
                _log?.Warn($"[TileRuntime.PlayerAdjTileSafetyPatches] Failed to apply: {ex.Message}");
            }
        }

        private static void UpdateNearbyCraftingTiles_Prefix(Player __instance)
        {
            if (__instance == null)
                return;

            EnsureAdjTileArrays(__instance, GetTileCount());
        }

        private static bool SetAdjTile_Prefix(Player __instance, int tileType)
        {
            if (__instance == null || tileType < 0)
                return false;

            int tileCount = GetTileCount();
            EnsureAdjTileArrays(__instance, tileCount);

            if (tileType >= tileCount)
                return false;

            if (_adjTileField != null)
            {
                var adj = _adjTileField.GetValue(__instance) as bool[];
                if (adj == null || tileType >= adj.Length)
                    return false;
            }

            return true;
        }

        private static void EnsureAdjTileArrays(Player player, int requiredLength)
        {
            if (requiredLength <= 0)
                return;

            if (_adjTileField == null)
            {
                if (!_loggedMissingFields)
                {
                    _loggedMissingFields = true;
                    _log?.Warn("[TileRuntime.PlayerAdjTileSafetyPatches] Player.adjTile field not found");
                }
                return;
            }

            try
            {
                bool resizedAny = false;

                var adj = _adjTileField.GetValue(player) as bool[];
                if (adj == null || adj.Length < requiredLength)
                {
                    var resized = new bool[requiredLength];
                    if (adj != null)
                        Array.Copy(adj, resized, Math.Min(adj.Length, resized.Length));
                    _adjTileField.SetValue(player, resized);
                    resizedAny = true;
                }

                if (_oldAdjTileField != null)
                {
                    var oldAdj = _oldAdjTileField.GetValue(player) as bool[];
                    if (oldAdj == null || oldAdj.Length < requiredLength)
                    {
                        var resizedOld = new bool[requiredLength];
                        if (oldAdj != null)
                            Array.Copy(oldAdj, resizedOld, Math.Min(oldAdj.Length, resizedOld.Length));
                        _oldAdjTileField.SetValue(player, resizedOld);
                        resizedAny = true;
                    }
                }

                if (resizedAny)
                    _log?.Info($"[TileRuntime.PlayerAdjTileSafetyPatches] Resized player adjTile arrays to {requiredLength}");
            }
            catch (Exception ex)
            {
                _log?.Debug($"[TileRuntime.PlayerAdjTileSafetyPatches] Resize error: {ex.Message}");
            }
        }

        private static int GetTileCount()
        {
            try
            {
                if (_tileIdCountField != null)
                {
                    object value = _tileIdCountField.GetValue(null);
                    if (value is int i) return i;
                    if (value is short s) return s;
                    if (value is ushort us) return us;
                }
            }
            catch
            {
            }

            return Math.Max(700, TileTypeExtension.ExtendedCount);
        }
    }
}
