using System;
using System.Reflection;
using Microsoft.Xna.Framework.Graphics;
using Terraria;
using Terraria.GameContent;
using TerrariaModder.Core.Assets;
using TerrariaModder.Core.Logging;

namespace StorageHub.PaintingChest
{
    internal static class TileTextureExtender
    {
        private static ILogger _log;
        private static bool _tileExtended;
        private static bool _itemExtended;
        private static bool _failed;

        // GraphicsDevice is from XNA (Microsoft.Xna.Framework.Graphics), directly referenced.
        // Asset<T> is from ReLogic.Content — NOT visible at compile time (embedded in Terraria.exe
        // with no separate ReLogic.Content.dll). Tile/item arrays stay as Array for the same reason.
        private static GraphicsDevice _graphicsDevice;

        private const int SOURCE_STYLE = 3; // "Good Morning" painting
        private const int FRAME_WIDTH = 54;
        private const int FRAME_HEIGHT = 36;

        public static void Initialize(ILogger logger)
        {
            _log = logger;
        }

        public static void TryExtend()
        {
            if ((_tileExtended && _itemExtended) || _failed) return;

            try
            {
                if (_graphicsDevice == null)
                {
                    _graphicsDevice = Main.instance?.GraphicsDevice;
                    if (_graphicsDevice == null) return;
                }

                if (!_tileExtended)
                    _tileExtended = ExtendTileSpritesheet();
                if (!_itemExtended)
                    _itemExtended = GenerateItemTexture();
            }
            catch (Exception ex)
            {
                _log?.Error($"TileTextureExtender failed: {ex.Message}");
                _failed = true;
            }
        }

        // Asset<T>.Value — ReLogic type, must get via reflection.
        private static Texture2D GetTexture2DFromAsset(object asset)
        {
            if (asset == null) return null;
            return asset.GetType().GetProperty("Value")?.GetValue(asset) as Texture2D;
        }

        // Main.Assets is IAssetRepository (ReLogic type) — must call Request<T> via reflection.
        private static Texture2D ForceLoadAsset(string assetName)
        {
            try
            {
                object repo = typeof(Main).GetField("Assets", BindingFlags.Public | BindingFlags.Static)?.GetValue(null)
                    ?? typeof(Main).GetProperty("Assets", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
                if (repo == null) return null;

                MethodInfo requestMethod = null;
                foreach (var m in repo.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (m.Name == "Request" && m.IsGenericMethod) { requestMethod = m; break; }
                }
                if (requestMethod == null) return null;

                var requestGeneric = requestMethod.MakeGenericMethod(typeof(Texture2D));
                var modeType = requestGeneric.GetParameters()[1].ParameterType;
                var immediateLoad = Enum.ToObject(modeType, 1);

                var asset = requestGeneric.Invoke(repo, new object[] { assetName, immediateLoad });
                return GetTexture2DFromAsset(asset);
            }
            catch { return null; }
        }

        private static bool ExtendTileSpritesheet()
        {
            // TextureAssets.Tile is Asset<Texture2D>[] — Asset<T> not visible at compile time, access as Array.
            var tileArray = typeof(TextureAssets).GetField("Tile", BindingFlags.Public | BindingFlags.Static)
                ?.GetValue(null) as Array;
            if (tileArray == null || PaintingChestManager.TILE_TYPE >= tileArray.Length) return false;

            var existingAsset = tileArray.GetValue(PaintingChestManager.TILE_TYPE);
            var existingTexture = ForceLoadAsset("Images/Tiles_" + PaintingChestManager.TILE_TYPE);
            if (existingTexture == null) return false;

            int origWidth = existingTexture.Width;
            int origHeight = existingTexture.Height;
            var origPixels = new uint[origWidth * origHeight];
            existingTexture.GetData(origPixels);

            int gmFrameY = SOURCE_STYLE * FRAME_HEIGHT;
            var framePixels = new uint[FRAME_WIDTH * FRAME_HEIGHT];
            for (int row = 0; row < FRAME_HEIGHT; row++)
                for (int col = 0; col < FRAME_WIDTH; col++)
                {
                    int srcIdx = (gmFrameY + row) * origWidth + col;
                    if (srcIdx < origPixels.Length)
                        framePixels[row * FRAME_WIDTH + col] = origPixels[srcIdx];
                }

            RecolorFrame(framePixels, FRAME_WIDTH, FRAME_HEIGHT);

            int newHeight = origHeight + FRAME_HEIGHT;
            var newTexture = new Texture2D(_graphicsDevice, origWidth, newHeight);
            var newPixels = new uint[origWidth * newHeight];
            Array.Copy(origPixels, 0, newPixels, 0, origPixels.Length);

            for (int row = 0; row < FRAME_HEIGHT; row++)
                for (int col = 0; col < FRAME_WIDTH; col++)
                    newPixels[(origHeight + row) * origWidth + col] = framePixels[row * FRAME_WIDTH + col];

            newTexture.SetData(newPixels);

            var originalName = GetAssetName(existingAsset) ?? "Images/Tiles_" + PaintingChestManager.TILE_TYPE;
            var newAsset = CreateAssetWrapper(existingAsset.GetType(), newTexture, originalName);
            if (newAsset != null)
            {
                tileArray.SetValue(newAsset, PaintingChestManager.TILE_TYPE);
                _log?.Info($"Extended tile 246 texture to {origWidth}x{newHeight}");
                return true;
            }
            return false;
        }

        private static void RecolorFrame(uint[] pixels, int width, int height)
        {
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    uint pixel = pixels[y * width + x];
                    uint alpha = (pixel >> 24) & 0xFF;
                    if (alpha == 0) continue;

                    // XNA SurfaceFormat.Color stores as ABGR in uint32:
                    // bits 0-7 = R, bits 8-15 = G, bits 16-23 = B, bits 24-31 = A
                    uint r = pixel & 0xFF;
                    uint g = (pixel >> 8) & 0xFF;
                    uint b = (pixel >> 16) & 0xFF;

                    bool isInterior = x >= 6 && x < width - 6 && y >= 5 && y < height - 5;
                    if (isInterior)
                    {
                        // Blue tint — reduce red, keep some green, boost blue
                        uint newR = r * 80 / 255;
                        uint newG = g * 150 / 255;
                        uint newB = (uint)Math.Min(255, b * 200 / 255 + g * 80 / 255);
                        r = newR; g = newG; b = newB;
                    }
                    else
                    {
                        // Frame: dark cool blue-grey
                        r = r * 80 / 255;
                        g = g * 90 / 255;
                        b = b * 130 / 255;
                    }

                    pixels[y * width + x] = (alpha << 24) | (b << 16) | (g << 8) | r;
                }
            }
        }

        private static bool GenerateItemTexture()
        {
            int ourType = ItemRegistry.GetRuntimeType(PaintingChestManager.FULL_ITEM_ID);
            if (ourType < 0) return false;

            // TextureAssets.Item is Asset<Texture2D>[] — Asset<T> not visible at compile time, access as Array.
            var itemArray = typeof(TextureAssets).GetField("Item", BindingFlags.Public | BindingFlags.Static)
                ?.GetValue(null) as Array;
            if (itemArray == null) return false;

            const int SOURCE_ITEM_ID = 1482; // "Good Morning"
            if (SOURCE_ITEM_ID >= itemArray.Length || ourType >= itemArray.Length) return false;

            var gmTexture = ForceLoadAsset("Images/Item_" + SOURCE_ITEM_ID);
            if (gmTexture == null) return false;

            int itemW = gmTexture.Width;
            int itemH = gmTexture.Height;
            var pixels = new uint[itemW * itemH];
            gmTexture.GetData(pixels);
            var newPixels = new uint[pixels.Length];
            Array.Copy(pixels, newPixels, pixels.Length);

            for (int i = 0; i < newPixels.Length; i++)
            {
                uint pixel = newPixels[i];
                uint alpha = (pixel >> 24) & 0xFF;
                if (alpha == 0) continue;
                // XNA ABGR: bits 0-7 = R, 8-15 = G, 16-23 = B
                uint origR = pixel & 0xFF;
                uint origG = (pixel >> 8) & 0xFF;
                uint origB = (pixel >> 16) & 0xFF;
                // Blue tint matching tile
                uint r = origR * 80 / 255;
                uint g = origG * 150 / 255;
                uint b = (uint)Math.Min(255, origB * 200 / 255 + origG * 80 / 255);
                newPixels[i] = (alpha << 24) | (b << 16) | (g << 8) | r;
            }

            var newTexture = new Texture2D(_graphicsDevice, itemW, itemH);
            newTexture.SetData(newPixels);

            var existingItemAsset = itemArray.GetValue(SOURCE_ITEM_ID);
            var originalItemName = GetAssetName(existingItemAsset) ?? "Images/Item_" + SOURCE_ITEM_ID;
            var newAsset = CreateAssetWrapper(existingItemAsset.GetType(), newTexture, originalItemName);
            if (newAsset != null)
            {
                itemArray.SetValue(newAsset, ourType);
                _log?.Info($"Generated painting chest item texture (type {ourType})");
                return true;
            }
            return false;
        }

        private static string GetAssetName(object asset)
        {
            return asset?.GetType().GetProperty("Name")?.GetValue(asset) as string;
        }

        // Asset<T> has internal constructor and private backing fields — must use reflection.
        private static object CreateAssetWrapper(Type assetType, Texture2D texture, string assetName)
        {
            try
            {
                var instance = Activator.CreateInstance(assetType, BindingFlags.NonPublic | BindingFlags.Instance, null,
                    new object[] { assetName }, null)
                    ?? Activator.CreateInstance(assetType, true);
                if (instance == null) return null;

                var valueField = assetType.GetField("<Value>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance)
                    ?? assetType.GetField("_value", BindingFlags.NonPublic | BindingFlags.Instance)
                    ?? assetType.GetField("ownValue", BindingFlags.NonPublic | BindingFlags.Instance);
                valueField?.SetValue(instance, texture);

                var stateField = assetType.GetField("<State>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance)
                    ?? assetType.GetField("_state", BindingFlags.NonPublic | BindingFlags.Instance)
                    ?? assetType.GetField("state", BindingFlags.NonPublic | BindingFlags.Instance);

                if (stateField != null)
                {
                    if (stateField.FieldType.IsEnum) stateField.SetValue(instance, Enum.ToObject(stateField.FieldType, 2));
                    else if (stateField.FieldType == typeof(int)) stateField.SetValue(instance, 2);
                }
                else
                {
                    foreach (var field in assetType.GetFields(BindingFlags.NonPublic | BindingFlags.Instance))
                    {
                        if (field.FieldType.IsEnum) { field.SetValue(instance, Enum.ToObject(field.FieldType, 2)); break; }
                    }
                }

                return instance;
            }
            catch { return null; }
        }
    }
}
