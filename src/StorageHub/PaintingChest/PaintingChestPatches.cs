using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Terraria;
using TerrariaModder.Core.Assets;
using TerrariaModder.Core.Logging;

namespace StorageHub.PaintingChest
{
    internal static class PaintingChestPatches
    {
        private static Harmony _harmony;
        private static ILogger _log;

        private static FieldInfo _destroyObjectField;
        private static FieldInfo _tileContainerField;
        private static FieldInfo _releaseUseTileField;
        private static FieldInfo _tileInteractAttemptedField;
        private static FieldInfo _chestField;
        private static FieldInfo _chestXField;
        private static FieldInfo _chestYField;
        private static FieldInfo _playerInventoryField;
        private static FieldInfo _stackSplitField;
        private static MethodInfo _findChestMethod;
        private static MethodInfo _createChestMethod;
        private static MethodInfo _destroyChestMethod;
        private static MethodInfo _killTileMethod;
        private static MethodInfo _getItemSourceMethod;
        private static MethodInfo _newItemMethod;
        private static MethodInfo _resizeMethod;
        private static MethodInfo _setGlowMethod;
        private static MethodInfo _openChestMethod;
        private static MethodInfo _inTileEntityInteractionRange;
        private static object _tileReachSimple;
        private static FieldInfo _chestEntriesField;

        private static Type _mainType;
        private static Type _playerType;
        private static Type _chestType;
        private static Type _worldGenType;
        private static Type _tileObjectType;
        private static Type _chestUIType;

        public static void ApplyPatches(ILogger logger)
        {
            _log = logger;
            _harmony = new Harmony("com.terrariamodder.storagehub.paintingchest");

            _mainType = typeof(Terraria.Main);
            _playerType = typeof(Terraria.Player);
            _chestType = typeof(Terraria.Chest);
            _worldGenType = typeof(Terraria.WorldGen);
            _tileObjectType = typeof(Terraria.TileObject);
            _chestUIType = typeof(Terraria.UI.ChestUI);

            CacheReflection();
            SetTileContainer();

            var patchType = typeof(PaintingChestPatches);

            // Patch 1: TileObject.Place postfix — create chest on placement
            var placeMethod = _tileObjectType.GetMethod("Place", BindingFlags.Public | BindingFlags.Static);
            if (placeMethod != null)
            {
                _harmony.Patch(placeMethod, postfix: new HarmonyMethod(
                    patchType.GetMethod(nameof(TileObjectPlace_Postfix), BindingFlags.Public | BindingFlags.Static)));
                _log?.Info("Patched TileObject.Place");
            }

            // Patch 2: Player.TileInteractionsUse prefix — right-click opens chest
            var interactMethod = _playerType.GetMethod("TileInteractionsUse",
                BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { typeof(int), typeof(int) }, null);
            if (interactMethod != null)
            {
                _harmony.Patch(interactMethod, prefix: new HarmonyMethod(
                    patchType.GetMethod(nameof(TileInteractionsUse_Prefix), BindingFlags.Public | BindingFlags.Static)));
                _log?.Info("Patched Player.TileInteractionsUse");
            }

            // Patch 3: WorldGen.KillTile prefix — break protection
            if (_killTileMethod != null)
            {
                _harmony.Patch(_killTileMethod, prefix: new HarmonyMethod(
                    patchType.GetMethod(nameof(KillTile_Prefix), BindingFlags.Public | BindingFlags.Static)));
                _log?.Info("Patched WorldGen.KillTile");
            }

            // Patch 4: WorldGen.Check3x2Wall prefix — break cleanup + correct item drop
            var check3x2Method = _worldGenType.GetMethod("Check3x2Wall",
                BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(int), typeof(int) }, null);
            if (check3x2Method != null)
            {
                _harmony.Patch(check3x2Method, prefix: new HarmonyMethod(
                    patchType.GetMethod(nameof(Check3x2Wall_Prefix), BindingFlags.Public | BindingFlags.Static)));
                _log?.Info("Patched WorldGen.Check3x2Wall");
            }

            // Patch 5: IsInInteractionRangeToMultiTileHitbox prefix — keep chest open
            var rangeMethod = _playerType.GetMethod("IsInInteractionRangeToMultiTileHitbox",
                BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(int), typeof(int) }, null);
            if (rangeMethod != null && _inTileEntityInteractionRange != null)
            {
                _harmony.Patch(rangeMethod, prefix: new HarmonyMethod(
                    patchType.GetMethod(nameof(IsInInteractionRange_Prefix), BindingFlags.Public | BindingFlags.Static)));
                _log?.Info("Patched IsInInteractionRangeToMultiTileHitbox");
            }
        }

        private static void CacheReflection()
        {
            _destroyObjectField = _worldGenType.GetField("destroyObject", BindingFlags.Public | BindingFlags.Static);
            _tileContainerField = _mainType.GetField("tileContainer", BindingFlags.Public | BindingFlags.Static);
            _releaseUseTileField = _playerType.GetField("releaseUseTile", BindingFlags.Public | BindingFlags.Instance);
            _tileInteractAttemptedField = _playerType.GetField("tileInteractAttempted", BindingFlags.Public | BindingFlags.Instance);
            _chestField = _playerType.GetField("chest", BindingFlags.Public | BindingFlags.Instance);
            _chestXField = _playerType.GetField("chestX", BindingFlags.Public | BindingFlags.Instance);
            _chestYField = _playerType.GetField("chestY", BindingFlags.Public | BindingFlags.Instance);
            _playerInventoryField = _mainType.GetField("playerInventory", BindingFlags.Public | BindingFlags.Static);
            _stackSplitField = _mainType.GetField("stackSplit", BindingFlags.Public | BindingFlags.Static);

            _findChestMethod = _chestType.GetMethod("FindChest",
                BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(int), typeof(int) }, null);
            _createChestMethod = _chestType.GetMethod("CreateChest",
                BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(int), typeof(int), typeof(int) }, null);
            _destroyChestMethod = _chestType.GetMethod("DestroyChest",
                BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(int), typeof(int) }, null);
            _resizeMethod = _chestType.GetMethod("Resize",
                BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(int) }, null);

            _killTileMethod = _worldGenType.GetMethod("KillTile",
                BindingFlags.Public | BindingFlags.Static, null,
                new[] { typeof(int), typeof(int), typeof(bool), typeof(bool), typeof(bool) }, null);

            _getItemSourceMethod = _worldGenType.GetMethod("GetItemSource_FromTileBreak",
                BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(int), typeof(int) }, null);

            foreach (var method in typeof(Item).GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                if (method.Name != "NewItem") continue;
                var parms = method.GetParameters();
                if (parms.Length >= 6 &&
                    parms[1].ParameterType == typeof(int) &&
                    parms[2].ParameterType == typeof(int) &&
                    parms[3].ParameterType == typeof(int) &&
                    parms[4].ParameterType == typeof(int) &&
                    parms[5].ParameterType == typeof(int))
                {
                    _newItemMethod = method;
                    break;
                }
            }

            _setGlowMethod = _chestUIType?.GetMethod("SetGlowForChest", BindingFlags.Public | BindingFlags.Static)
                ?? typeof(Terraria.UI.ItemSlot).GetMethod("SetGlowForChest", BindingFlags.Public | BindingFlags.Static);

            _openChestMethod = _playerType.GetMethod("OpenChest",
                BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { typeof(int), typeof(int), typeof(int) }, null);

            // CoinSlot.ChestEntries — fixed at 200, must be resized for large chests
            var coinSlotType = _mainType.Assembly.GetType("Terraria.UI.CoinSlot");
            _chestEntriesField = coinSlotType?.GetField("ChestEntries", BindingFlags.NonPublic | BindingFlags.Static);

            var tileReachType = _playerType.Assembly.GetType("Terraria.DataStructures.TileReachCheckSettings");
            if (tileReachType != null)
            {
                _tileReachSimple = tileReachType.GetField("Simple", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
                _inTileEntityInteractionRange = _playerType.GetMethod("InTileEntityInteractionRange",
                    BindingFlags.Public | BindingFlags.Instance, null,
                    new[] { typeof(int), typeof(int), typeof(int), typeof(int), tileReachType }, null);
            }

            _log?.Info($"Reflection cached: FindChest={_findChestMethod != null}, CreateChest={_createChestMethod != null}, " +
                $"Resize={_resizeMethod != null}, NewItem={_newItemMethod != null}, OpenChest={_openChestMethod != null}, InTileEntityRange={_inTileEntityInteractionRange != null}");
        }

        public static void SetTileContainer()
        {
            try
            {
                // Resolve tileContainer directly — don't depend on _tileContainerField
                // being cached (which only happens in ApplyPatches after the 5s timer).
                // This must work during OnWorldLoad before patches are applied.
                var field = _tileContainerField
                    ?? (_mainType ?? typeof(Terraria.Main)).GetField("tileContainer",
                        BindingFlags.Public | BindingFlags.Static);
                var arr = field?.GetValue(null) as bool[];
                if (arr != null && PaintingChestManager.TILE_TYPE < arr.Length)
                {
                    arr[PaintingChestManager.TILE_TYPE] = true;
                    _log?.Info($"tileContainer[{PaintingChestManager.TILE_TYPE}] = true (len={arr.Length})");
                }
            }
            catch (Exception ex)
            {
                _log?.Error($"SetTileContainer error: {ex.Message}");
            }
        }

        public static void Unpatch()
        {
            _harmony?.UnpatchAll("com.terrariamodder.storagehub.paintingchest");
        }

        #region Helpers

        /// <summary>
        /// Resize CoinSlot.ChestEntries if it's too small for the given chest capacity.
        /// Vanilla allocates this at 200 — painting chest can go up to 1000.
        /// Without this, SetGlowForChest → CoinSlot.ForceSlotState crashes with IndexOutOfRange.
        /// </summary>
        private static void EnsureCoinSlotCapacity(int requiredSlots)
        {
            if (_chestEntriesField == null) return;
            try
            {
                var arr = _chestEntriesField.GetValue(null) as Array;
                if (arr != null && arr.Length < requiredSlots)
                {
                    var elemType = arr.GetType().GetElementType();
                    var newArr = Array.CreateInstance(elemType, requiredSlots);
                    Array.Copy(arr, newArr, arr.Length);
                    _chestEntriesField.SetValue(null, newArr);
                    _log?.Info($"Resized CoinSlot.ChestEntries: {arr.Length} → {requiredSlots}");
                }
            }
            catch (Exception ex)
            {
                _log?.Error($"EnsureCoinSlotCapacity error: {ex.Message}");
            }
        }

        private static int GetStyleFromTile(int frameY)
        {
            int row = frameY / 18;
            int style = 0;
            while (row >= 2) { row -= 2; style++; }
            return style;
        }

        private static void GetTopLeft(int x, int y, out int topX, out int topY)
        {
            var tile = Main.tile[x, y];
            topX = x - tile.frameX / 18;
            int row = tile.frameY / 18;
            while (row >= 2) row -= 2;
            topY = y - row;
        }

        private static bool ChestHasItems(int topX, int topY)
        {
            int chestIdx = (int)_findChestMethod.Invoke(null, new object[] { topX, topY });
            if (chestIdx < 0) return false;
            var chest = Main.chest[chestIdx];
            if (chest == null) return false;
            for (int i = 0; i < chest.maxItems; i++)
            {
                if (chest.item[i] != null && chest.item[i].type > 0 && chest.item[i].stack > 0)
                    return true;
            }
            return false;
        }

        private static int GetOurItemType()
        {
            return ItemRegistry.GetRuntimeType(PaintingChestManager.FULL_ITEM_ID);
        }

        #endregion

        #region Patch 1: TileObject.Place postfix — create chest on placement

        public static void TileObjectPlace_Postfix(bool __result, object toBePlaced)
        {
            if (!__result || !PaintingChestManager.Enabled) return;

            try
            {
                var toType = toBePlaced.GetType();
                int type = (int)toType.GetField("type").GetValue(toBePlaced);
                if (type != PaintingChestManager.TILE_TYPE) return;

                int style = (int)toType.GetField("style").GetValue(toBePlaced);
                if (style != PaintingChestManager.OUR_PLACE_STYLE) return;

                int xCoord = (int)toType.GetField("xCoord").GetValue(toBePlaced);
                int yCoord = (int)toType.GetField("yCoord").GetValue(toBePlaced);

                GetTopLeft(xCoord, yCoord, out int topX, out int topY);

                int placedStyle = GetStyleFromTile(Main.tile[xCoord, yCoord].frameY);
                if (placedStyle != PaintingChestManager.OUR_PLACE_STYLE) return;

                int chestIdx = (int)_createChestMethod.Invoke(null, new object[] { topX, topY, -1 });
                if (chestIdx < 0) return;

                var chest = Main.chest[chestIdx];
                int capacity = PaintingChestManager.GetCurrentCapacity();
                _resizeMethod.Invoke(chest, new object[] { capacity });
                chest.name = "Mysterious Painting";
                _log?.Info($"Placed painting chest idx={chestIdx} maxItems={chest.maxItems} itemLen={chest.item.Length}");
            }
            catch (Exception ex)
            {
                var inner = ex.InnerException ?? ex;
                _log?.Error($"TileObjectPlace_Postfix error: {inner.Message}\n{inner.StackTrace}");
            }
        }

        #endregion

        #region Patch 2: TileInteractionsUse prefix — right-click opens chest

        public static bool TileInteractionsUse_Prefix(object __instance, int myX, int myY)
        {
            if (!PaintingChestManager.Enabled) return true;

            try
            {
                if (!Main.tile[myX, myY].active()) return true;
                if (Main.tile[myX, myY].type != PaintingChestManager.TILE_TYPE) return true;

                int style = GetStyleFromTile(Main.tile[myX, myY].frameY);
                if (style != PaintingChestManager.OUR_PLACE_STYLE) return true;

                bool releaseUseTile = (bool)_releaseUseTileField.GetValue(__instance);
                bool tileInteractAttempted = (bool)_tileInteractAttemptedField.GetValue(__instance);
                if (!releaseUseTile || !tileInteractAttempted) return true;

                GetTopLeft(myX, myY, out int topX, out int topY);
                int chestIdx = (int)_findChestMethod.Invoke(null, new object[] { topX, topY });
                if (chestIdx < 0) return true;

                int currentChest = (int)_chestField.GetValue(__instance);
                _stackSplitField?.SetValue(null, 600);

                if (currentChest == chestIdx)
                {
                    // Close the currently open chest
                    _chestField.SetValue(__instance, -1);
                    PlaySound(11); // chest close
                }
                else
                {
                    // Resize CoinSlot.ChestEntries before OpenChest — vanilla caps at 200,
                    // painting chest can have up to 1000 slots
                    var chest = Main.chest[chestIdx];
                    if (chest != null)
                        EnsureCoinSlotCapacity(chest.maxItems);

                    _openChestMethod.Invoke(__instance, new object[] { topX, topY, chestIdx });
                    PlaySound(currentChest == -1 ? 10 : 12); // open vs switch
                }

                var openedChest = Main.chest[chestIdx];
                if (openedChest != null && currentChest != chestIdx)
                {
                    _log?.Info($"Opened painting chest idx={chestIdx} maxItems={openedChest.maxItems} itemLen={openedChest.item.Length}");
                }

                _releaseUseTileField.SetValue(__instance, false);
                return false;
            }
            catch (Exception ex)
            {
                var inner = ex.InnerException ?? ex;
                _log?.Error($"TileInteractionsUse_Prefix error: {inner.Message}\n{inner.StackTrace}");
                return true;
            }
        }

        private static void PlaySound(int soundId)
        {
            try
            {
                var soundEngineType = _mainType.Assembly.GetType("Terraria.Audio.SoundEngine");
                // PlaySound(int type, int x = -1, int y = -1, int Style = 1, float volumeScale = 1f, float pitchOffset = 0f)
                // Has 6 formal params — must search by name since GetMethod with 3 types won't match
                var playMethod = soundEngineType?.GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .FirstOrDefault(m => m.Name == "PlaySound" && m.GetParameters().Length == 6
                        && m.GetParameters()[0].ParameterType == typeof(int));
                playMethod?.Invoke(null, new object[] { soundId, -1, -1, 1, 1f, 0f });
            }
            catch { }
        }

        #endregion

        #region Patch 3: KillTile prefix — break protection

        public static bool KillTile_Prefix(int i, int j)
        {
            if (!PaintingChestManager.Enabled) return true;

            try
            {
                var tile = Main.tile[i, j];
                if (tile == null || !tile.active() || tile.type != PaintingChestManager.TILE_TYPE) return true;

                bool destroyObject = (bool)_destroyObjectField.GetValue(null);
                if (destroyObject) return true;

                int style = GetStyleFromTile(tile.frameY);
                if (style != PaintingChestManager.OUR_PLACE_STYLE) return true;

                GetTopLeft(i, j, out int topX, out int topY);
                if (ChestHasItems(topX, topY))
                    return false;

                return true;
            }
            catch (Exception ex)
            {
                var inner = ex.InnerException ?? ex;
                _log?.Error($"KillTile_Prefix error: {inner.Message}\n{inner.StackTrace}");
                return true;
            }
        }

        #endregion

        #region Patch 4: Check3x2Wall prefix — break cleanup + correct item drop

        public static bool Check3x2Wall_Prefix(int x, int y)
        {
            if (!PaintingChestManager.Enabled) return true;

            try
            {
                bool destroyObject = (bool)_destroyObjectField.GetValue(null);
                if (destroyObject) return true;

                var tile = Main.tile[x, y];
                if (tile == null || !tile.active() || tile.type != PaintingChestManager.TILE_TYPE) return true;

                int style = GetStyleFromTile(tile.frameY);
                if (style != PaintingChestManager.OUR_PLACE_STYLE) return true;

                GetTopLeft(x, y, out int topX, out int topY);
                int frameYBase = style * 36;

                bool structureBroken = false;
                for (int ti = topX; ti < topX + 3; ti++)
                {
                    for (int tj = topY; tj < topY + 2; tj++)
                    {
                        var t = Main.tile[ti, tj];
                        if (t.type != PaintingChestManager.TILE_TYPE || !t.active() || t.wall <= 0 ||
                            t.frameY != frameYBase + (tj - topY) * 18 ||
                            t.frameX != (ti - topX) * 18)
                        {
                            structureBroken = true;
                            break;
                        }
                    }
                    if (structureBroken) break;
                }

                if (!structureBroken) return true;

                _destroyChestMethod.Invoke(null, new object[] { topX, topY });
                _destroyObjectField.SetValue(null, true);

                for (int ti = topX; ti < topX + 3; ti++)
                {
                    for (int tj = topY; tj < topY + 2; tj++)
                    {
                        if (Main.tile[ti, tj].type == PaintingChestManager.TILE_TYPE && Main.tile[ti, tj].active())
                            _killTileMethod.Invoke(null, new object[] { ti, tj, false, false, false });
                    }
                }

                int ourItemType = GetOurItemType();
                if (ourItemType > 0 && _newItemMethod != null && _getItemSourceMethod != null)
                {
                    var source = _getItemSourceMethod.Invoke(null, new object[] { x, y });
                    _newItemMethod.Invoke(null, new object[] { source, x * 16, y * 16, 32, 32, ourItemType, 1, false, 0, false });
                }

                _destroyObjectField.SetValue(null, false);
                return false;
            }
            catch (Exception ex)
            {
                var inner = ex.InnerException ?? ex;
                _log?.Error($"Check3x2Wall_Prefix error: {inner.Message}\n{inner.StackTrace}");
                return true;
            }
        }

        #endregion

        #region Patch 5: IsInInteractionRangeToMultiTileHitbox prefix — keep chest open

        public static bool IsInInteractionRange_Prefix(object __instance, int chestPointX, int chestPointY, ref bool __result)
        {
            if (!PaintingChestManager.Enabled) return true;

            try
            {
                var tile = Main.tile[chestPointX, chestPointY];
                if (tile == null || tile.type != PaintingChestManager.TILE_TYPE) return true;

                int style = GetStyleFromTile(tile.frameY);
                if (style != PaintingChestManager.OUR_PLACE_STYLE) return true;

                GetTopLeft(chestPointX, chestPointY, out int topX, out int topY);
                __result = (bool)_inTileEntityInteractionRange.Invoke(__instance,
                    new object[] { topX, topY, 3, 2, _tileReachSimple });
                return false;
            }
            catch (Exception ex)
            {
                var inner = ex.InnerException ?? ex;
                _log?.Error($"IsInInteractionRange_Prefix error: {inner.Message}\n{inner.StackTrace}");
                return true;
            }
        }

        #endregion
    }
}
