using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using StorageHub.DedicatedBlocks;
using StorageHub.Storage;
using TerrariaModder.Core.Assets;
using TerrariaModder.Core.Logging;
using TerrariaModder.TileRuntime;
using RuntimeTileContainers = TerrariaModder.TileRuntime.CustomTileContainers;

namespace StorageHub.Patches
{
    /// <summary>
    /// Draws runtime slot indicators on placed Storage Drives.
    /// Rendering is patched into Main.DrawTiles so coordinates stay in tile/world space.
    /// </summary>
    internal static class DriveVisualPatch
    {
        private static ILogger _log;
        private static bool _applied;
        private static bool _drawErrorLogged;
        private static bool _drawHookSeenLogged;
        private static bool _batchBeginFailedLogged;
        private static bool _noVisibleDriveLogged;
        private static bool _drawModeLogged;
        private static bool _firstVisibleDriveLogged;

        private static Harmony _harmony;
        private static MethodBase _patchedDrawTilesMethod;

        private static Func<DriveStorageState> _stateProvider;

        // Cached Terraria reflection
        private static Type _mainType;
        private static FieldInfo _mainGameMenuField;
        private static FieldInfo _mainDedServField;
        private static FieldInfo _mainMaxTilesXField;
        private static FieldInfo _mainMaxTilesYField;
        private static FieldInfo _mainSpriteBatchField;
        private static FieldInfo _mainChestArrayField;
        private static FieldInfo _mainTileArrayField;
        private static FieldInfo _mainScreenPositionField;
        private static FieldInfo _mainScreenWidthField;
        private static FieldInfo _mainScreenHeightField;
        private static FieldInfo _mainDrawToScreenField;
        private static FieldInfo _mainOffScreenRangeField;

        private static FieldInfo _vectorXField;
        private static FieldInfo _vectorYField;

        private static FieldInfo _chestXField;
        private static FieldInfo _chestYField;
        private static FieldInfo _chestItemField;

        private static FieldInfo _itemTypeField;
        private static FieldInfo _itemStackField;
        private static FieldInfo _itemPrefixField;

        private static MethodInfo _tileActiveMethod;
        private static FieldInfo _tileTypeField;
        private static FieldInfo _tileFrameXField;
        private static FieldInfo _tileFrameYField;
        private static MethodInfo _tileIndexerMethod;

        // Draw reflection
        private static ConstructorInfo _rectangleCtor;
        private static ConstructorInfo _colorCtorRgba;
        private static MethodInfo _spriteBatchDrawRectColor;
        private static MethodInfo _spriteBatchBeginSimpleMethod;
        private static MethodInfo _spriteBatchEndMethod;
        private static FieldInfo _spriteBatchBeginCalledField;
        private static FieldInfo _spriteBatchTransformMatrixField;
        private static FieldInfo _matrixM11Field;
        private static FieldInfo _matrixM22Field;
        private static FieldInfo _matrixM41Field;
        private static FieldInfo _matrixM42Field;
        private static object _cachedPixelTexture;

        // Cached color instances (XNA Color boxed)
        private static object _emptyLedColor;
        private static object _ledGreen;
        private static object _ledYellow;
        private static object _ledOrange;
        private static object _ledRed;

        private static int _storageDriveTileType = -1;
        private static int _resolveTypeCooldown;

        // Drive LED layout inside a 2x2 tile (32x32 px):
        // 2 columns x 4 rows, matching the drive texture slot grid.
        private const int SlotColumns = 2;
        private const int SlotRows = 4;
        private const int SlotCount = SlotColumns * SlotRows; // 8
        private const int LedStartX = 12; // first LED x
        private const int LedStartY = 4;  // first LED y
        private const int LedStepX = 15;  // next column
        private const int LedStepY = 7;   // next row
        private const int LedSize = 2;    // 2x2 = 4 pixels

        public static void Initialize(ILogger log)
        {
            _log = log;
            if (_applied)
                return;

            try
            {
                if (!ApplyDrawTilesPatch())
                {
                    _log?.Warn("[DriveVisualPatch] Failed to patch DrawTiles");
                    return;
                }

                _applied = true;
                _log?.Info("[DriveVisualPatch] Patched Main.DrawTiles for world-space drive LEDs");
            }
            catch (Exception ex)
            {
                _log?.Warn($"[DriveVisualPatch] Failed to initialize: {ex.Message}");
            }
        }

        public static void SetStateProvider(Func<DriveStorageState> stateProvider)
        {
            _stateProvider = stateProvider;
        }

        public static void ClearStateProvider()
        {
            _stateProvider = null;
        }

        public static void Unload()
        {
            ClearStateProvider();
            _storageDriveTileType = -1;
            _resolveTypeCooldown = 0;
            _cachedPixelTexture = null;

            try
            {
                if (_harmony != null && _patchedDrawTilesMethod != null)
                {
                    _harmony.Unpatch(_patchedDrawTilesMethod, HarmonyPatchType.Postfix, _harmony.Id);
                }
            }
            catch
            {
                // Best effort.
            }
            finally
            {
                _patchedDrawTilesMethod = null;
                _harmony = null;
                _applied = false;
            }
        }

        private static bool ApplyDrawTilesPatch()
        {
            Type mainType = ResolveMainType();
            if (mainType == null)
                return false;

            var methods = mainType
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(m => string.Equals(m.Name, "DrawTiles", StringComparison.Ordinal))
                .ToArray();
            if (methods.Length == 0)
                return false;

            // Prefer the known 3-param overload, fallback to the first one.
            MethodInfo drawTiles = methods.FirstOrDefault(m => m.GetParameters().Length == 3) ?? methods[0];

            MethodInfo postfix = typeof(DriveVisualPatch).GetMethod(
                nameof(DrawTiles_Postfix),
                BindingFlags.NonPublic | BindingFlags.Static);
            if (drawTiles == null || postfix == null)
                return false;

            _harmony = new Harmony("com.storagehub.drivevisual.drawtiles");
            _harmony.Patch(drawTiles, postfix: new HarmonyMethod(postfix));
            _patchedDrawTilesMethod = drawTiles;

            _log?.Info($"[DriveVisualPatch] Applied to {FormatMethodSignature(drawTiles)}");
            return true;
        }

        private static void DrawTiles_Postfix()
        {
            if (!_drawHookSeenLogged)
            {
                _drawHookSeenLogged = true;
                _log?.Info("[DriveVisualPatch] DrawTiles postfix invoked");
            }
            DrawInTileSpace();
        }

        private static void DrawInTileSpace()
        {
            try
            {
                if (!EnsureReflection())
                    return;

                if (!ShouldDraw())
                    return;

                ResolveDriveTileType();
                if (_storageDriveTileType < 0)
                    return;

                object spriteBatch = _mainSpriteBatchField?.GetValue(null);
                if (spriteBatch == null)
                    return;

                if (!EnsureDrawReflection(spriteBatch))
                    return;

                bool openedBatch = false;
                if (!IsSpriteBatchInBeginState(spriteBatch) && !TryBeginSpriteBatch(spriteBatch, out openedBatch))
                {
                    if (!_batchBeginFailedLogged)
                    {
                        _batchBeginFailedLogged = true;
                        _log?.Warn("[DriveVisualPatch] Draw skipped: failed to begin sprite batch");
                    }
                    return;
                }

                object pixel = GetMagicPixelTexture();
                if (pixel == null)
                {
                    if (openedBatch)
                        TryEndSpriteBatch(spriteBatch);
                    return;
                }

                var chests = _mainChestArrayField?.GetValue(null) as Array;
                var tiles = _mainTileArrayField?.GetValue(null);
                if (chests == null || tiles == null || _tileIndexerMethod == null)
                    return;

                int maxTilesX = GetSafeInt(_mainMaxTilesXField, null, 0);
                int maxTilesY = GetSafeInt(_mainMaxTilesYField, null, 0);
                if (maxTilesX <= 0 || maxTilesY <= 0)
                    return;

                float screenX = GetVectorComponent(_mainScreenPositionField?.GetValue(null), _vectorXField);
                float screenY = GetVectorComponent(_mainScreenPositionField?.GetValue(null), _vectorYField);
                int screenW = GetSafeInt(_mainScreenWidthField, null, 1920);
                int screenH = GetSafeInt(_mainScreenHeightField, null, 1080);
                bool drawToScreen = GetSafeBool(_mainDrawToScreenField, null, true);
                int offScreenRange = GetSafeInt(_mainOffScreenRangeField, null, 0);

                var state = _stateProvider?.Invoke();
                var chestLookup = BuildChestLookup(chests);
                int drawn = 0;
                float m11 = 1f;
                float m22 = 1f;
                float m41 = 0f;
                float m42 = 0f;
                bool drawIntoWorldTransformedBatch = !openedBatch && IsWorldSpaceBatch(spriteBatch, out m11, out m22, out m41, out m42);

                try
                {
                    if (!_drawModeLogged)
                    {
                        _drawModeLogged = true;
                        _log?.Info($"[DriveVisualPatch] Draw mode={(drawIntoWorldTransformedBatch ? "world" : "screen")} matrix=({m11:0.###},{m22:0.###},{m41:0.###},{m42:0.###}) drawToScreen={drawToScreen} offScreenRange={offScreenRange}");
                    }

                    int minTileX = Clamp((int)(screenX / 16f) - 2, 0, maxTilesX - 1);
                    int maxTileX = Clamp((int)((screenX + screenW) / 16f) + 2, 0, maxTilesX - 1);
                    int minTileY = Clamp((int)(screenY / 16f) - 2, 0, maxTilesY - 1);
                    int maxTileY = Clamp((int)((screenY + screenH) / 16f) + 2, 0, maxTilesY - 1);

                    for (int y = minTileY; y <= maxTileY; y++)
                    {
                        for (int x = minTileX; x <= maxTileX; x++)
                        {
                            object tile = _tileIndexerMethod.Invoke(tiles, new object[] { x, y });
                            if (tile == null || !IsTileActive(tile))
                                continue;

                            int tileType = GetSafeInt(_tileTypeField, tile, -1);
                            if (tileType != _storageDriveTileType)
                                continue;

                            ResolveTopLeft(x, y, tile, out int topX, out int topY);
                            if (topX != x || topY != y)
                                continue;

                            int worldX = topX * 16;
                            int worldY = topY * 16;
                            int drawX = drawIntoWorldTransformedBatch
                                ? worldX
                                : (int)(worldX - screenX);
                            int drawY = drawIntoWorldTransformedBatch
                                ? worldY
                                : (int)(worldY - screenY);
                            if (!drawIntoWorldTransformedBatch && !drawToScreen && offScreenRange != 0)
                            {
                                drawX += offScreenRange;
                                drawY += offScreenRange;
                            }

                            long key = ((long)topX << 32) | (uint)topY;
                            object chest = null;
                            if (chestLookup.TryGetValue(key, out var chestEntry))
                                chest = chestEntry.Chest;

                            if (!_firstVisibleDriveLogged)
                            {
                                _firstVisibleDriveLogged = true;
                                _log?.Info($"[DriveVisualPatch] First visible drive topLeft=({topX},{topY}) draw=({drawX},{drawY}) world=({worldX},{worldY}) screen=({screenX:0.##},{screenY:0.##})");
                            }

                            DrawDriveIndicators(spriteBatch, pixel, drawX, drawY, chest, state);
                            drawn++;
                        }
                    }
                }
                finally
                {
                    if (openedBatch)
                        TryEndSpriteBatch(spriteBatch);
                }

                if (drawn == 0 && !_noVisibleDriveLogged)
                {
                    _noVisibleDriveLogged = true;
                    _log?.Info("[DriveVisualPatch] No visible storage drives matched draw pass");
                }
            }
            catch (Exception ex)
            {
                if (_drawErrorLogged)
                    return;

                _drawErrorLogged = true;
                string detail = ex.Message;
                if (ex.InnerException != null)
                    detail += $" | Inner: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}";
                _log?.Warn($"[DriveVisualPatch] Draw error: {detail}");
            }
        }

        private static bool EnsureReflection()
        {
            if (_mainType != null && _mainSpriteBatchField != null && _mainChestArrayField != null)
                return true;

            try
            {
                _mainType = ResolveMainType();
                if (_mainType == null)
                    return false;

                _mainGameMenuField = _mainType.GetField("gameMenu", BindingFlags.Public | BindingFlags.Static);
                _mainDedServField = _mainType.GetField("dedServ", BindingFlags.Public | BindingFlags.Static);
                _mainMaxTilesXField = _mainType.GetField("maxTilesX", BindingFlags.Public | BindingFlags.Static);
                _mainMaxTilesYField = _mainType.GetField("maxTilesY", BindingFlags.Public | BindingFlags.Static);
                _mainSpriteBatchField = _mainType.GetField("spriteBatch", BindingFlags.Public | BindingFlags.Static);
                _mainChestArrayField = _mainType.GetField("chest", BindingFlags.Public | BindingFlags.Static);
                _mainTileArrayField = _mainType.GetField("tile", BindingFlags.Public | BindingFlags.Static);
                _mainScreenPositionField = _mainType.GetField("screenPosition", BindingFlags.Public | BindingFlags.Static);
                _mainScreenWidthField = _mainType.GetField("screenWidth", BindingFlags.Public | BindingFlags.Static);
                _mainScreenHeightField = _mainType.GetField("screenHeight", BindingFlags.Public | BindingFlags.Static);
                _mainDrawToScreenField = _mainType.GetField("drawToScreen", BindingFlags.Public | BindingFlags.Static);
                _mainOffScreenRangeField = _mainType.GetField("offScreenRange", BindingFlags.Public | BindingFlags.Static);

                object screenPos = _mainScreenPositionField?.GetValue(null);
                if (screenPos != null)
                {
                    Type vectorType = screenPos.GetType();
                    _vectorXField = vectorType.GetField("X", BindingFlags.Public | BindingFlags.Instance);
                    _vectorYField = vectorType.GetField("Y", BindingFlags.Public | BindingFlags.Instance);
                }

                Type chestType = Type.GetType("Terraria.Chest, Terraria")
                    ?? Assembly.Load("Terraria").GetType("Terraria.Chest");
                if (chestType != null)
                {
                    _chestXField = chestType.GetField("x", BindingFlags.Public | BindingFlags.Instance);
                    _chestYField = chestType.GetField("y", BindingFlags.Public | BindingFlags.Instance);
                    _chestItemField = chestType.GetField("item", BindingFlags.Public | BindingFlags.Instance);
                }

                Type itemType = Type.GetType("Terraria.Item, Terraria")
                    ?? Assembly.Load("Terraria").GetType("Terraria.Item");
                if (itemType != null)
                {
                    _itemTypeField = itemType.GetField("type", BindingFlags.Public | BindingFlags.Instance);
                    _itemStackField = itemType.GetField("stack", BindingFlags.Public | BindingFlags.Instance);
                    _itemPrefixField = itemType.GetField("prefix", BindingFlags.Public | BindingFlags.Instance);
                }

                Type tileType = Type.GetType("Terraria.Tile, Terraria")
                    ?? Assembly.Load("Terraria").GetType("Terraria.Tile");
                if (tileType != null)
                {
                    _tileActiveMethod = tileType.GetMethod("active", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                    _tileTypeField = tileType.GetField("type", BindingFlags.Public | BindingFlags.Instance);
                    _tileFrameXField = tileType.GetField("frameX", BindingFlags.Public | BindingFlags.Instance);
                    _tileFrameYField = tileType.GetField("frameY", BindingFlags.Public | BindingFlags.Instance);
                }

                object tiles = _mainTileArrayField?.GetValue(null);
                if (tiles != null)
                {
                    Type tilesType = tiles.GetType();
                    _tileIndexerMethod = tilesType.GetMethod("Get", new[] { typeof(int), typeof(int) })
                        ?? tilesType.GetMethod("get_Item", new[] { typeof(int), typeof(int) });
                }

                return _mainSpriteBatchField != null && _mainChestArrayField != null && _mainTileArrayField != null;
            }
            catch
            {
                return false;
            }
        }

        private static bool EnsureDrawReflection(object spriteBatch)
        {
            try
            {
                if (_rectangleCtor == null || _colorCtorRgba == null)
                {
                    Type rectType = FindType("Microsoft.Xna.Framework.Rectangle");
                    Type colorType = FindType("Microsoft.Xna.Framework.Color");
                    if (rectType == null || colorType == null)
                        return false;

                    _rectangleCtor = rectType.GetConstructor(new[] { typeof(int), typeof(int), typeof(int), typeof(int) });
                    _colorCtorRgba = colorType.GetConstructor(new[] { typeof(byte), typeof(byte), typeof(byte), typeof(byte) });
                    if (_rectangleCtor == null || _colorCtorRgba == null)
                        return false;

                    _emptyLedColor = CreateColor(70, 72, 72, 230);
                    _ledGreen = CreateColor(106, 190, 48, 255);
                    _ledYellow = CreateColor(241, 230, 76, 255);
                    _ledOrange = CreateColor(255, 164, 61, 255);
                    _ledRed = CreateColor(255, 76, 76, 255);
                }

                if (_spriteBatchDrawRectColor == null)
                {
                    Type sbType = spriteBatch.GetType();
                    foreach (var method in sbType.GetMethods(BindingFlags.Public | BindingFlags.Instance))
                    {
                        if (!string.Equals(method.Name, "Draw", StringComparison.Ordinal))
                            continue;

                        var p = method.GetParameters();
                        if (p.Length != 3)
                            continue;

                        if (!IsRectangleType(p[1].ParameterType) || !IsColorType(p[2].ParameterType))
                            continue;

                        _spriteBatchDrawRectColor = method;
                        break;
                    }
                }

                if (_spriteBatchBeginSimpleMethod == null || _spriteBatchEndMethod == null)
                {
                    Type sbType = spriteBatch.GetType();
                    _spriteBatchBeginSimpleMethod = sbType.GetMethod(
                        "Begin",
                        BindingFlags.Public | BindingFlags.Instance,
                        null,
                        Type.EmptyTypes,
                        null);
                    _spriteBatchEndMethod = sbType.GetMethod(
                        "End",
                        BindingFlags.Public | BindingFlags.Instance,
                        null,
                        Type.EmptyTypes,
                        null);
                }

                if (_spriteBatchBeginCalledField == null)
                {
                    Type sbType = spriteBatch.GetType();
                    _spriteBatchBeginCalledField = sbType.GetField("inBeginEndPair", BindingFlags.NonPublic | BindingFlags.Instance);
                    if (_spriteBatchBeginCalledField == null)
                    {
                        _spriteBatchBeginCalledField = sbType
                            .GetFields(BindingFlags.NonPublic | BindingFlags.Instance)
                            .FirstOrDefault(f => f.FieldType == typeof(bool) &&
                                                 f.Name.IndexOf("begin", StringComparison.OrdinalIgnoreCase) >= 0);
                    }
                }

                if (_spriteBatchTransformMatrixField == null)
                {
                    Type sbType = spriteBatch.GetType();
                    _spriteBatchTransformMatrixField = sbType.GetField("transformMatrix", BindingFlags.NonPublic | BindingFlags.Instance);
                    if (_spriteBatchTransformMatrixField != null)
                    {
                        Type matrixType = _spriteBatchTransformMatrixField.FieldType;
                        _matrixM11Field = matrixType.GetField("M11", BindingFlags.Public | BindingFlags.Instance);
                        _matrixM22Field = matrixType.GetField("M22", BindingFlags.Public | BindingFlags.Instance);
                        _matrixM41Field = matrixType.GetField("M41", BindingFlags.Public | BindingFlags.Instance);
                        _matrixM42Field = matrixType.GetField("M42", BindingFlags.Public | BindingFlags.Instance);
                    }
                }

                return _spriteBatchDrawRectColor != null &&
                       _spriteBatchBeginSimpleMethod != null &&
                       _spriteBatchEndMethod != null;
            }
            catch
            {
                return false;
            }
        }

        private static object GetMagicPixelTexture()
        {
            if (_cachedPixelTexture != null)
                return _cachedPixelTexture;

            try
            {
                Type textureAssetsType = Type.GetType("Terraria.GameContent.TextureAssets, Terraria")
                    ?? Assembly.Load("Terraria").GetType("Terraria.GameContent.TextureAssets");
                if (textureAssetsType == null)
                    return null;

                object magicPixelAsset = null;
                var field = textureAssetsType.GetField("MagicPixel", BindingFlags.Public | BindingFlags.Static);
                if (field != null)
                    magicPixelAsset = field.GetValue(null);

                if (magicPixelAsset == null)
                {
                    var prop = textureAssetsType.GetProperty("MagicPixel", BindingFlags.Public | BindingFlags.Static);
                    if (prop != null && prop.CanRead)
                        magicPixelAsset = prop.GetValue(null, null);
                }

                if (magicPixelAsset == null)
                    return null;

                var valueProp = magicPixelAsset.GetType().GetProperty("Value", BindingFlags.Public | BindingFlags.Instance);
                _cachedPixelTexture = valueProp?.GetValue(magicPixelAsset, null);
                return _cachedPixelTexture;
            }
            catch
            {
                return null;
            }
        }

        private static void DrawDriveIndicators(object spriteBatch, object pixelTexture, int drawX, int drawY, object chest, DriveStorageState state)
        {
            Array items = null;
            if (chest != null && _chestItemField != null)
                items = _chestItemField.GetValue(chest) as Array;

            int slotLimit = items != null ? Math.Min(SlotCount, items.Length) : 0;
            for (int slot = 0; slot < SlotCount; slot++)
            {
                int col = slot % SlotColumns;
                int row = slot / SlotColumns;
                if (row >= SlotRows)
                    break;

                int ledX = drawX + LedStartX + col * LedStepX;
                int ledY = drawY + LedStartY + row * LedStepY;

                object ledColor = _emptyLedColor;
                if (slot < slotLimit)
                {
                    object diskItem = items.GetValue(slot);
                    ledColor = ResolveSlotColor(diskItem, state);
                }

                DrawRect(spriteBatch, pixelTexture, ledX, ledY, LedSize, LedSize, ledColor);
            }
        }

        private static Dictionary<long, ChestDrawEntry> BuildChestLookup(Array chests)
        {
            var lookup = new Dictionary<long, ChestDrawEntry>();
            if (chests == null)
                return lookup;

            for (int i = 0; i < chests.Length; i++)
            {
                object chest = chests.GetValue(i);
                if (chest == null)
                    continue;

                int chestX = GetSafeInt(_chestXField, chest, -1);
                int chestY = GetSafeInt(_chestYField, chest, -1);
                if (chestX < 0 || chestY < 0)
                    continue;

                long key = ((long)chestX << 32) | (uint)chestY;
                int score = GetDriveCandidateScore(chest);
                if (!lookup.TryGetValue(key, out var existing) || score > existing.Score)
                    lookup[key] = new ChestDrawEntry { Chest = chest, Score = score };
            }

            return lookup;
        }

        private static void ResolveTopLeft(int tileX, int tileY, object tile, out int topX, out int topY)
        {
            topX = tileX;
            topY = tileY;

            if (RuntimeTileContainers.TryGetTileDefinition(tileX, tileY, out var definition, out int tileType) &&
                tileType == _storageDriveTileType &&
                definition != null &&
                RuntimeTileContainers.TryGetTopLeft(tileX, tileY, definition, out int resolvedX, out int resolvedY))
            {
                topX = resolvedX;
                topY = resolvedY;
                return;
            }

            if (tile == null)
                return;

            // Fallback for safety if runtime TileObjectData lookup is unavailable.
            int frameX = GetSafeInt(_tileFrameXField, tile, 0);
            int frameY = GetSafeInt(_tileFrameYField, tile, 0);
            int localX = PositiveModulo(frameX / 18, 2);
            int localY = PositiveModulo(frameY / 18, 2);
            topX = tileX - localX;
            topY = tileY - localY;
        }

        private static int GetDriveCandidateScore(object chest)
        {
            if (chest == null || _chestItemField == null)
                return int.MinValue;

            var items = _chestItemField.GetValue(chest) as Array;
            if (items == null)
                return int.MinValue;

            int score = 0;
            int limit = Math.Min(SlotCount, items.Length);
            for (int i = 0; i < limit; i++)
            {
                object item = items.GetValue(i);
                if (item == null)
                    continue;

                int type = GetSafeInt(_itemTypeField, item, 0);
                int stack = GetSafeInt(_itemStackField, item, 0);
                if (type <= 0 || stack <= 0)
                    continue;

                // Prefer candidates that actually contain disk items.
                score += DedicatedBlocksManager.TryGetDiskTierForItemType(type, out _) ? 1000 : 1;
            }

            return score;
        }

        private static object ResolveSlotColor(object diskItem, DriveStorageState state)
        {
            if (diskItem == null || _itemTypeField == null || _itemStackField == null || _itemPrefixField == null)
                return _emptyLedColor;

            int itemType = GetSafeInt(_itemTypeField, diskItem, 0);
            int stack = GetSafeInt(_itemStackField, diskItem, 0);
            if (itemType <= 0 || stack <= 0)
                return _emptyLedColor;

            if (!DedicatedBlocksManager.TryGetDiskTierForItemType(itemType, out _))
                return _emptyLedColor;

            int uid = GetSafeInt(_itemPrefixField, diskItem, 0);
            if (uid <= 0 || state == null)
                return _ledGreen;

            if (!state.TryGetDisk(itemType, uid, out var disk) || disk == null || disk.Capacity <= 0)
                return _ledGreen;

            float fillRatio = disk.Items.Count / (float)disk.Capacity;
            if (fillRatio >= 0.999f)
                return _ledRed;
            if (fillRatio >= 0.85f)
                return _ledOrange;
            if (fillRatio >= 0.60f)
                return _ledYellow;
            return _ledGreen;
        }

        private static void DrawRect(object spriteBatch, object texture, int x, int y, int w, int h, object color)
        {
            if (spriteBatch == null || texture == null || color == null || _rectangleCtor == null || _spriteBatchDrawRectColor == null)
                return;

            object rect = _rectangleCtor.Invoke(new object[] { x, y, w, h });
            _spriteBatchDrawRectColor.Invoke(spriteBatch, new[] { texture, rect, color });
        }

        private static bool ShouldDraw()
        {
            bool inMenu = GetSafeBool(_mainGameMenuField, null, true);
            bool dedServ = GetSafeBool(_mainDedServField, null, false);
            int maxTilesX = GetSafeInt(_mainMaxTilesXField, null, 0);
            int maxTilesY = GetSafeInt(_mainMaxTilesYField, null, 0);

            return !inMenu && !dedServ && maxTilesX > 0 && maxTilesY > 0;
        }

        private static void ResolveDriveTileType()
        {
            if (_storageDriveTileType >= 0)
                return;

            if (_resolveTypeCooldown > 0)
            {
                _resolveTypeCooldown--;
                return;
            }

            _storageDriveTileType = DedicatedBlocksManager.ResolveStorageUnitTileType();
            _resolveTypeCooldown = 60;
        }

        private static bool IsTileActive(object tile)
        {
            try
            {
                if (_tileActiveMethod == null)
                    return false;

                object activeObj = _tileActiveMethod.Invoke(tile, null);
                return activeObj is bool active && active;
            }
            catch
            {
                return false;
            }
        }

        private static bool IsSpriteBatchInBeginState(object spriteBatch)
        {
            if (spriteBatch == null)
                return false;

            if (_spriteBatchBeginCalledField == null)
                return true;

            try
            {
                object value = _spriteBatchBeginCalledField.GetValue(spriteBatch);
                if (value is bool began)
                    return began;
            }
            catch
            {
                // ignored
            }

            return true;
        }

        private static bool TryBeginSpriteBatch(object spriteBatch, out bool opened)
        {
            opened = false;
            if (spriteBatch == null || _spriteBatchBeginSimpleMethod == null)
                return false;

            try
            {
                if (IsSpriteBatchInBeginState(spriteBatch))
                    return true;

                _spriteBatchBeginSimpleMethod.Invoke(spriteBatch, null);
                opened = true;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static void TryEndSpriteBatch(object spriteBatch)
        {
            if (spriteBatch == null || _spriteBatchEndMethod == null)
                return;

            try
            {
                if (IsSpriteBatchInBeginState(spriteBatch))
                    _spriteBatchEndMethod.Invoke(spriteBatch, null);
            }
            catch
            {
                // Best effort.
            }
        }

        private static bool IsWorldSpaceBatch(object spriteBatch, out float m11, out float m22, out float m41, out float m42)
        {
            m11 = 1f;
            m22 = 1f;
            m41 = 0f;
            m42 = 0f;

            if (spriteBatch == null || _spriteBatchTransformMatrixField == null)
                return true;

            try
            {
                object matrix = _spriteBatchTransformMatrixField.GetValue(spriteBatch);
                if (matrix == null)
                    return true;

                m11 = GetSafeFloat(_matrixM11Field, matrix, 1f);
                m22 = GetSafeFloat(_matrixM22Field, matrix, 1f);
                m41 = GetSafeFloat(_matrixM41Field, matrix, 0f);
                m42 = GetSafeFloat(_matrixM42Field, matrix, 0f);

                bool hasScale = Math.Abs(m11 - 1f) > 0.001f || Math.Abs(m22 - 1f) > 0.001f;
                bool hasTranslation = Math.Abs(m41) > 0.001f || Math.Abs(m42) > 0.001f;
                return hasScale || hasTranslation;
            }
            catch
            {
                return true;
            }
        }

        private static object CreateColor(byte r, byte g, byte b, byte a)
        {
            try
            {
                return _colorCtorRgba?.Invoke(new object[] { r, g, b, a });
            }
            catch
            {
                return null;
            }
        }

        private static float GetVectorComponent(object vector, FieldInfo componentField)
        {
            if (vector == null || componentField == null)
                return 0f;

            try
            {
                object value = componentField.GetValue(vector);
                if (value == null)
                    return 0f;

                return Convert.ToSingle(value);
            }
            catch
            {
                return 0f;
            }
        }

        private static int GetSafeInt(FieldInfo field, object target, int fallback)
        {
            if (field == null)
                return fallback;

            try
            {
                object value = field.GetValue(target);
                if (value == null)
                    return fallback;

                return Convert.ToInt32(value);
            }
            catch
            {
                return fallback;
            }
        }

        private static float GetSafeFloat(FieldInfo field, object target, float fallback)
        {
            if (field == null)
                return fallback;

            try
            {
                object value = field.GetValue(target);
                if (value == null)
                    return fallback;

                return Convert.ToSingle(value);
            }
            catch
            {
                return fallback;
            }
        }

        private static int Clamp(int value, int min, int max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }

        private static int PositiveModulo(int value, int modulus)
        {
            if (modulus <= 0)
                return 0;

            int result = value % modulus;
            if (result < 0)
                result += modulus;
            return result;
        }

        private static bool GetSafeBool(FieldInfo field, object target, bool fallback)
        {
            if (field == null)
                return fallback;

            try
            {
                object value = field.GetValue(target);
                if (value == null)
                    return fallback;

                return Convert.ToBoolean(value);
            }
            catch
            {
                return fallback;
            }
        }

        private static bool IsRectangleType(Type type)
        {
            return type != null && string.Equals(type.FullName, "Microsoft.Xna.Framework.Rectangle", StringComparison.Ordinal);
        }

        private static bool IsColorType(Type type)
        {
            return type != null && string.Equals(type.FullName, "Microsoft.Xna.Framework.Color", StringComparison.Ordinal);
        }

        private static Type FindType(string fullName)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var type = asm.GetType(fullName);
                if (type != null)
                    return type;
            }

            return null;
        }

        private static Type ResolveMainType()
        {
            return Type.GetType("Terraria.Main, Terraria")
                   ?? Assembly.Load("Terraria").GetType("Terraria.Main");
        }

        private static string FormatMethodSignature(MethodInfo method)
        {
            if (method == null)
                return "<null>";

            var pars = method.GetParameters();
            return $"{method.Name}({pars.Length} params)";
        }

        private sealed class ChestDrawEntry
        {
            public object Chest;
            public int Score;
        }
    }
}
