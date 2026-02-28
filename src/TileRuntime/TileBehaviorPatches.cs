using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;
using Terraria;
using TerrariaModder.Core.Assets;
using TerrariaModder.Core.Logging;

namespace TerrariaModder.TileRuntime
{
    internal static class TileBehaviorPatches
    {
        private static Harmony _harmony;
        private static ILogger _log;
        private static bool _applied;

        private static MethodInfo _playerOpenChestMethod;
        private static MethodInfo _inTileEntityInteractionRange;
        private static object _tileReachSimple;
        private static MethodInfo _getItemSourceFromTileBreak;
        private static MethodInfo _newItemMethod;

        [ThreadStatic] private static bool _pendingBreak;
        [ThreadStatic] private static int _pendingBreakTopX;
        [ThreadStatic] private static int _pendingBreakTopY;
        [ThreadStatic] private static TileDefinition _pendingBreakDef;

        public static void Initialize(ILogger logger)
        {
            _log = logger;
            _harmony = new Harmony("com.terrariamodder.tileruntime.tiles.behavior");
            CacheReflection();
        }

        public static void ApplyPatches()
        {
            if (_applied) return;

            int patched = 0;
            patched += PatchTileInteractionsUse();
            patched += PatchTileObjectPlace();
            patched += PatchKillTile();
            patched += PatchInteractionRange();

            _applied = true;
            _log?.Info($"[TileRuntime.TileBehaviorPatches] Applied {patched} patches");
        }

        internal static MethodInfo PlayerOpenChestMethod => _playerOpenChestMethod;

        private static void CacheReflection()
        {
            var playerType = typeof(Player);
            _playerOpenChestMethod = playerType.GetMethod("OpenChest", BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { typeof(int), typeof(int), typeof(int) }, null);

            var tileReachType = playerType.Assembly.GetType("Terraria.DataStructures.TileReachCheckSettings");
            if (tileReachType != null)
            {
                _tileReachSimple = tileReachType.GetField("Simple", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
                _inTileEntityInteractionRange = playerType.GetMethod("InTileEntityInteractionRange", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(int), typeof(int), typeof(int), typeof(int), tileReachType }, null);
            }

            _getItemSourceFromTileBreak = typeof(WorldGen).GetMethod("GetItemSource_FromTileBreak", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(int), typeof(int) }, null);

            foreach (var method in typeof(Item).GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                if (method.Name != "NewItem") continue;
                var parms = method.GetParameters();
                if (parms.Length >= 6 && parms[1].ParameterType == typeof(int) && parms[2].ParameterType == typeof(int) &&
                    parms[3].ParameterType == typeof(int) && parms[4].ParameterType == typeof(int) && parms[5].ParameterType == typeof(int))
                {
                    _newItemMethod = method;
                    break;
                }
            }
        }

        private static int PatchTileInteractionsUse()
        {
            var method = typeof(Player).GetMethod("TileInteractionsUse", BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { typeof(int), typeof(int) }, null);
            if (method == null) return 0;
            _harmony.Patch(method, prefix: new HarmonyMethod(typeof(TileBehaviorPatches), nameof(TileInteractionsUse_Prefix)));
            return 1;
        }

        private static int PatchTileObjectPlace()
        {
            var tileObjectType = typeof(Main).Assembly.GetType("Terraria.TileObject");
            var placeMethod = tileObjectType?.GetMethod("Place", BindingFlags.Public | BindingFlags.Static);
            if (placeMethod == null) return 0;
            _harmony.Patch(placeMethod, postfix: new HarmonyMethod(typeof(TileBehaviorPatches), nameof(TileObjectPlace_Postfix)));
            return 1;
        }

        private static int PatchKillTile()
        {
            var method = typeof(WorldGen).GetMethod("KillTile", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(int), typeof(int), typeof(bool), typeof(bool), typeof(bool) }, null);
            if (method == null) return 0;
            _harmony.Patch(method,
                prefix: new HarmonyMethod(typeof(TileBehaviorPatches), nameof(KillTile_Prefix)),
                postfix: new HarmonyMethod(typeof(TileBehaviorPatches), nameof(KillTile_Postfix)));
            return 1;
        }

        private static int PatchInteractionRange()
        {
            var method = typeof(Player).GetMethod("IsInInteractionRangeToMultiTileHitbox", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(int), typeof(int) }, null);
            if (method == null || _inTileEntityInteractionRange == null || _tileReachSimple == null)
                return 0;

            _harmony.Patch(method, prefix: new HarmonyMethod(typeof(TileBehaviorPatches), nameof(IsInInteractionRange_Prefix)));
            return 1;
        }

        private static bool TileInteractionsUse_Prefix(Player __instance, int myX, int myY)
        {
            try
            {
                if (!CustomTileContainers.TryGetTileDefinition(myX, myY, out var def, out int tileType))
                    return true;

                if (!__instance.releaseUseTile || !__instance.tileInteractAttempted)
                    return true;

                if (def.IsContainer)
                {
                    if (!def.ContainerInteractable)
                    {
                        __instance.releaseUseTile = false;
                        return false;
                    }

                    if (CustomTileContainers.TryOpenContainer(__instance, myX, myY, def))
                    {
                        __instance.releaseUseTile = false;
                        return false;
                    }
                }

                if (def.OnRightClick != null)
                {
                    bool handled = def.OnRightClick(__instance, myX, myY);
                    if (handled)
                    {
                        __instance.releaseUseTile = false;
                        return false;
                    }
                }
            }
            catch (Exception ex)
            {
                _log?.Debug($"[TileRuntime.TileBehaviorPatches] TileInteractionsUse error: {ex.Message}");
            }

            return true;
        }

        private static void TileObjectPlace_Postfix(bool __result, object toBePlaced)
        {
            if (!__result || toBePlaced == null) return;

            try
            {
                var toType = toBePlaced.GetType();
                int type = (int)toType.GetField("type")?.GetValue(toBePlaced);
                if (!TileRegistry.IsCustomTile(type)) return;

                var def = TileRegistry.GetDefinition(type);
                if (def == null) return;

                int xCoord = (int)toType.GetField("xCoord")?.GetValue(toBePlaced);
                int yCoord = (int)toType.GetField("yCoord")?.GetValue(toBePlaced);
                if (!CustomTileContainers.TryGetTopLeft(xCoord, yCoord, def, out int topX, out int topY))
                {
                    topX = xCoord;
                    topY = yCoord;
                }

                NormalizeMultiTileFrames(topX, topY, type, def);
                if (def.IsContainer)
                    CustomTileContainers.EnsureContainerChest(topX, topY, def, out _);

                def.OnPlace?.Invoke(topX, topY);
            }
            catch (Exception ex)
            {
                _log?.Debug($"[TileRuntime.TileBehaviorPatches] TileObject.Place error: {ex.Message}");
            }
        }

        private static bool KillTile_Prefix(int i, int j, bool fail, bool effectOnly, bool noItem)
        {
            _pendingBreak = false;

            try
            {
                if (!CustomTileContainers.TryGetTileDefinition(i, j, out var def, out _))
                    return true;

                if (!CustomTileContainers.TryGetTopLeft(i, j, def, out int topX, out int topY))
                {
                    topX = i;
                    topY = j;
                }

                if (def.IsContainer && def.ContainerRequiresEmptyToBreak && !WorldGen.destroyObject)
                {
                    int chestIndex = Chest.FindChest(topX, topY);
                    if (chestIndex >= 0 && ChestHasItems(chestIndex))
                        return false;
                }

                if ((def.Width > 1 || def.Height > 1) && !effectOnly && !fail)
                {
                    BreakCustomMultiTile(topX, topY, def, effectOnly, noItem);
                    return false;
                }

                _pendingBreak = true;
                _pendingBreakTopX = topX;
                _pendingBreakTopY = topY;
                _pendingBreakDef = def;
            }
            catch (Exception ex)
            {
                _log?.Debug($"[TileRuntime.TileBehaviorPatches] KillTile prefix error: {ex.Message}");
            }

            return true;
        }

        private static void KillTile_Postfix(int i, int j, bool fail, bool effectOnly, bool noItem)
        {
            if (!_pendingBreak) return;

            try
            {
                var tile = Main.tile[i, j];
                bool removed = tile == null || !tile.active() || !TileRegistry.IsCustomTile(tile.type);
                if (!removed || _pendingBreakDef == null)
                    return;

                var topLeftTile = Main.tile[_pendingBreakTopX, _pendingBreakTopY];
                bool topLeftStillPresent = topLeftTile != null && topLeftTile.active() && TileRegistry.IsCustomTile(topLeftTile.type);
                if (topLeftStillPresent)
                    return;

                FinalizeBreak(_pendingBreakTopX, _pendingBreakTopY, _pendingBreakDef, effectOnly, noItem);
            }
            catch (Exception ex)
            {
                _log?.Debug($"[TileRuntime.TileBehaviorPatches] KillTile postfix error: {ex.Message}");
            }
            finally
            {
                _pendingBreak = false;
                _pendingBreakDef = null;
            }
        }

        private static bool IsInInteractionRange_Prefix(Player __instance, int chestPointX, int chestPointY, ref bool __result)
        {
            try
            {
                if (!CustomTileContainers.TryGetTileDefinition(chestPointX, chestPointY, out var def, out _))
                    return true;
                if (!def.IsContainer) return true;

                if (!CustomTileContainers.TryGetTopLeft(chestPointX, chestPointY, def, out int topX, out int topY))
                    return true;

                __result = (bool)_inTileEntityInteractionRange.Invoke(__instance,
                    new object[] { topX, topY, Math.Max(1, def.Width), Math.Max(1, def.Height), _tileReachSimple });
                return false;
            }
            catch
            {
                return true;
            }
        }

        private static void NormalizeMultiTileFrames(int topX, int topY, int type, TileDefinition def)
        {
            int width = Math.Max(1, def.Width);
            int height = Math.Max(1, def.Height);
            if (width <= 1 && height <= 1) return;

            int stepX = Math.Max(1, def.CoordinateWidth + Math.Max(0, def.CoordinatePadding));
            int[] rowOffsets = BuildRowOffsets(def.CoordinateHeights, def.CoordinatePadding, height);

            for (int lx = 0; lx < width; lx++)
            {
                for (int ly = 0; ly < height; ly++)
                {
                    var tile = Main.tile[topX + lx, topY + ly];
                    if (tile == null || !tile.active() || tile.type != type)
                        return;

                    tile.frameX = (short)(lx * stepX);
                    tile.frameY = (short)rowOffsets[ly];
                }
            }

            WorldGen.RangeFrame(topX, topY, topX + width + 1, topY + height + 1);
        }

        private static int[] BuildRowOffsets(int[] coordinateHeights, int coordinatePadding, int height)
        {
            int[] rowHeights = coordinateHeights != null && coordinateHeights.Length > 0 ? coordinateHeights : BuildDefaultCoordinateHeights(height);
            int[] offsets = new int[Math.Max(1, height)];
            int padding = Math.Max(0, coordinatePadding);
            int acc = 0;
            for (int row = 0; row < offsets.Length; row++)
            {
                offsets[row] = acc;
                acc += Math.Max(1, rowHeights[Math.Min(row, rowHeights.Length - 1)]) + padding;
            }
            return offsets;
        }

        private static void BreakCustomMultiTile(int topX, int topY, TileDefinition def, bool effectOnly, bool noItem)
        {
            if (effectOnly) return;

            int width = Math.Max(1, def.Width);
            int height = Math.Max(1, def.Height);
            bool removedAny = false;

            for (int lx = 0; lx < width; lx++)
            {
                for (int ly = 0; ly < height; ly++)
                {
                    var tile = Main.tile[topX + lx, topY + ly];
                    if (tile == null || !tile.active() || !TileRegistry.IsCustomTile(tile.type))
                        continue;

                    tile.active(false);
                    tile.type = 0;
                    tile.frameX = 0;
                    tile.frameY = 0;
                    removedAny = true;
                }
            }

            if (removedAny)
                FinalizeBreak(topX, topY, def, effectOnly, noItem);
        }

        private static void FinalizeBreak(int topX, int topY, TileDefinition def, bool effectOnly, bool noItem)
        {
            if (def.IsContainer)
                RemoveCustomChest(topX, topY);

            TryPlayTileHitSound(def.HitSoundStyle, topX, topY);

            if (!effectOnly && !noItem && !string.IsNullOrEmpty(def.DropItemId))
            {
                int itemType = ItemRegistry.ResolveItemType(def.DropItemId);
                if (itemType > 0)
                    SpawnDropItem(topX, topY, itemType);
            }

            try { def.OnBreak?.Invoke(topX, topY); } catch { }
        }

        private static bool ChestHasItems(int chestIndex)
        {
            var chest = chestIndex >= 0 && chestIndex < Main.maxChests ? Main.chest[chestIndex] : null;
            if (chest?.item == null) return false;
            for (int i = 0; i < chest.maxItems && i < chest.item.Length; i++)
            {
                var item = chest.item[i];
                if (item != null && !item.IsAir && item.stack > 0)
                    return true;
            }
            return false;
        }

        private static void SpawnDropItem(int tileX, int tileY, int itemType)
        {
            try
            {
                if (_newItemMethod == null) return;
                object source = _getItemSourceFromTileBreak?.Invoke(null, new object[] { tileX, tileY });
                _newItemMethod.Invoke(null, new object[] { source, tileX * 16, tileY * 16, 16, 16, itemType, 1, false, 0, false });
            }
            catch { }
        }

        private static void RemoveCustomChest(int topX, int topY)
        {
            int chestIndex = Chest.FindChest(topX, topY);
            if (chestIndex < 0) return;
            Chest.RemoveChest(chestIndex);
            if (Main.player == null) return;
            for (int p = 0; p < Main.player.Length; p++)
            {
                var player = Main.player[p];
                if (player != null && player.chest == chestIndex)
                    player.chest = -1;
            }
        }

        private static void TryPlayTileHitSound(int soundStyle, int tileX, int tileY)
        {
            if (soundStyle < 0) return;
            try
            {
                var seType = typeof(Main).Assembly.GetType("Terraria.Audio.SoundEngine");
                foreach (var m in seType.GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    if (m.Name != "PlaySound") continue;
                    var p = m.GetParameters();
                    if (p.Length == 6 && p[0].ParameterType == typeof(int))
                    {
                        m.Invoke(null, new object[] { 0, tileX * 16, tileY * 16, Math.Max(0, soundStyle), 1f, 0f });
                        break;
                    }
                }
            }
            catch { }
        }

        private static int[] BuildDefaultCoordinateHeights(int height)
        {
            int[] arr = new int[Math.Max(1, height)];
            for (int i = 0; i < arr.Length; i++) arr[i] = 16;
            return arr;
        }
    }

    public static class CustomTileContainers
    {
        public static bool EnsureContainerChest(int topX, int topY, TileDefinition definition, out int chestIndex)
        {
            chestIndex = -1;
            if (definition == null || !definition.IsContainer) return false;
            if (topX < 0 || topX >= Main.maxTilesX || topY < 0 || topY >= Main.maxTilesY) return false;

            chestIndex = Chest.FindChest(topX, topY);
            if (chestIndex < 0)
                chestIndex = Chest.CreateChest(topX, topY, -1);
            if (chestIndex < 0)
                return false;

            var chest = Main.chest[chestIndex];
            if (chest == null) return false;

            if (definition.ContainerCapacity > 0 && chest.maxItems != definition.ContainerCapacity)
            {
                var resize = chest.GetType().GetMethod("Resize", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(int) }, null);
                resize?.Invoke(chest, new object[] { definition.ContainerCapacity });
            }

            chest.name = string.IsNullOrEmpty(definition.ContainerName) ? definition.DisplayName : definition.ContainerName;
            return true;
        }

        public static bool TryGetTileDefinition(int tileX, int tileY, out TileDefinition definition, out int tileType)
        {
            definition = null;
            tileType = -1;

            if (tileX < 0 || tileX >= Main.maxTilesX || tileY < 0 || tileY >= Main.maxTilesY)
                return false;

            var tile = Main.tile[tileX, tileY];
            if (tile == null || !tile.active())
                return false;

            tileType = tile.type;
            if (!TileRegistry.IsCustomTile(tileType))
                return false;

            definition = TileRegistry.GetDefinition(tileType);
            return definition != null;
        }

        public static bool TryGetTopLeft(int tileX, int tileY, TileDefinition definition, out int topX, out int topY)
        {
            topX = tileX;
            topY = tileY;

            if (definition == null) return false;

            int width = Math.Max(1, definition.Width);
            int height = Math.Max(1, definition.Height);
            var tile = Main.tile[tileX, tileY];
            if (tile == null) return false;

            int stepX = Math.Max(1, definition.CoordinateWidth + Math.Max(0, definition.CoordinatePadding));
            int localX = PositiveModulo(tile.frameX / stepX, width);
            int localY = GetLocalFrameRow(tile.frameY, definition.CoordinateHeights, definition.CoordinatePadding, height);

            topX = tileX - localX;
            topY = tileY - localY;
            return topX >= 0 && topY >= 0 && topX + width <= Main.maxTilesX && topY + height <= Main.maxTilesY;
        }

        public static bool TryOpenContainer(Player player, int tileX, int tileY, TileDefinition definition)
        {
            if (player == null || definition == null || !definition.IsContainer)
                return false;

            if (!TryGetTopLeft(tileX, tileY, definition, out int topX, out int topY))
                return false;

            if (!EnsureContainerChest(topX, topY, definition, out int chestIndex))
                return false;

            Main.stackSplit = 600;
            int previousChest = player.chest;

            if (player.chest == chestIndex)
            {
                player.chest = -1;
                TryPlayChestSound(11);
                return true;
            }

            if (TileBehaviorPatches.PlayerOpenChestMethod != null)
            {
                TileBehaviorPatches.PlayerOpenChestMethod.Invoke(player, new object[] { topX, topY, chestIndex });
            }
            else
            {
                player.chest = chestIndex;
                player.chestX = topX;
                player.chestY = topY;
                Main.playerInventory = true;
            }

            TryPlayChestSound(previousChest == -1 ? 10 : 12);
            return true;
        }

        private static int GetLocalFrameRow(int frameY, int[] coordinateHeights, int coordinatePadding, int height)
        {
            int[] rowHeights = coordinateHeights != null && coordinateHeights.Length > 0 ? coordinateHeights : BuildDefaultCoordinateHeights(height);
            int[] rowOffsets = new int[Math.Max(1, height)];
            int padding = Math.Max(0, coordinatePadding);
            int acc = 0;
            for (int row = 0; row < rowOffsets.Length; row++)
            {
                rowOffsets[row] = acc;
                acc += Math.Max(1, rowHeights[Math.Min(row, rowHeights.Length - 1)]) + padding;
            }

            int wrapped = PositiveModulo(frameY, Math.Max(1, acc));
            for (int row = rowOffsets.Length - 1; row >= 0; row--)
            {
                if (wrapped >= rowOffsets[row])
                    return row;
            }

            return 0;
        }

        private static int PositiveModulo(int value, int modulus)
        {
            if (modulus <= 0) return 0;
            int result = value % modulus;
            return result < 0 ? result + modulus : result;
        }

        private static int[] BuildDefaultCoordinateHeights(int height)
        {
            int[] arr = new int[Math.Max(1, height)];
            for (int i = 0; i < arr.Length; i++) arr[i] = 16;
            return arr;
        }

        private static void TryPlayChestSound(int soundId)
        {
            try
            {
                var seType = typeof(Main).Assembly.GetType("Terraria.Audio.SoundEngine");
                foreach (var m in seType.GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    if (m.Name != "PlaySound") continue;
                    var p = m.GetParameters();
                    if (p.Length == 6 && p[0].ParameterType == typeof(int))
                    {
                        m.Invoke(null, new object[] { soundId, -1, -1, 1, 1f, 0f });
                        break;
                    }
                }
            }
            catch { }
        }
    }
}
