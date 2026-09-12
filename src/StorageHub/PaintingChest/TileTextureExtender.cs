using System;
using System.Reflection;
using System.Collections.Generic;
using Microsoft.Xna.Framework.Graphics;
using Terraria;
using Terraria.GameContent;
using TerrariaModder.Core.Assets;
using TerrariaModder.Core.Logging;

namespace StorageHub.PaintingChest
{
    /// <summary>
    /// Keeps saved style 69 while packing its animation below the vanilla atlas.
    /// Draw-only coordinates are remapped after native chest animation is resolved.
    /// Both the tile and outline atlas remain within XNA Reach's 2048px limit.
    /// </summary>
    internal static class TileTextureExtender
    {
        private static ILogger _log;
        private static bool _tileExtended;
        private static bool _itemExtended;
        private static bool _failed;
        private static int _nullRefCount;

        public static bool IsTileExtended => _tileExtended;

        private static GraphicsDevice _graphicsDevice;

        private const int STYLE_WIDTH = 36;  // 2 tiles × 18px per tile
        private const int STYLE_HEIGHT = 38; // 2 tiles × 18px + 2px padding
        private const int SOURCE_STYLE = 1;  // Gold chest — base texture to recolor

        public static int AtlasYOffset { get; private set; }

        private sealed class Replacement
        {
            public Array Assets;
            public int Index;
            public object Original;
            public object Installed;
            public Texture2D Texture;
        }

        private static readonly List<Replacement> _replacements = new List<Replacement>();

        public static void Unload()
        {
            // Called on the game thread after the draw patch has been removed.
            foreach (var replacement in _replacements)
            {
                if (ReferenceEquals(replacement.Assets.GetValue(replacement.Index), replacement.Installed))
                {
                    replacement.Assets.SetValue(replacement.Original, replacement.Index);
                    replacement.Texture.Dispose();
                }
                // A later mod may retain our texture. Never dispose resources it now owns.
            }
            _replacements.Clear();
            Main.instance?.TilePaintSystem?.Reset();
            AtlasYOffset = 0;
            _tileExtended = _itemExtended = _failed = false;
            _nullRefCount = 0;
            _graphicsDevice = null;
        }

        private static Replacement PrepareReplacement(Array assets, int index, Texture2D texture)
        {
            var original = assets.GetValue(index);
            var installed = CreateAssetWrapper(original.GetType(), texture, GetAssetName(original));
            if (installed == null)
            {
                texture.Dispose();
                throw new InvalidOperationException("Could not wrap chest texture asset");
            }
            return new Replacement { Assets = assets, Index = index, Original = original,
                Installed = installed, Texture = texture };
        }

        private static void Install(Replacement replacement)
        {
            replacement.Assets.SetValue(replacement.Installed, replacement.Index);
            _replacements.Add(replacement);
        }

        public static void Initialize(ILogger logger)
        {
            _log = logger;
        }

        public static void TryExtend()
        {
            if (_failed) return;
            if (_tileExtended && _itemExtended) return;

            try
            {
                if (_graphicsDevice == null)
                {
                    _graphicsDevice = Main.instance?.GraphicsDevice;
                    if (_graphicsDevice == null) return;
                }

                if (!_tileExtended)
                    _tileExtended = ExtendChestSpritesheet();
                if (!_itemExtended)
                    _itemExtended = GenerateItemTexture();
            }
            catch (Exception ex)
            {
                // NullRef at startup is a timing issue (GraphicsDevice not ready) — suppress after first log
                if (ex is NullReferenceException)
                {
                    if (_nullRefCount++ == 0)
                        _log?.Debug($"TileTextureExtender: waiting for GraphicsDevice (NullRef, will retry)");
                }
                else
                {
                    _log?.Error($"TileTextureExtender failed: {ex.GetType().Name}: {ex.Message}");
                    _failed = true;
                }
            }
        }

        private static Texture2D GetTexture2DFromAsset(object asset)
        {
            if (asset == null) return null;
            return asset.GetType().GetProperty("Value")?.GetValue(asset) as Texture2D;
        }

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
            catch (Exception ex) { _log?.Debug($"ForceLoadAsset failed for {assetName}: {ex.Message}"); return null; }
        }

        private static bool ExtendChestSpritesheet()
        {
            int type = PaintingChestManager.TILE_TYPE;
            var tileArray = typeof(TextureAssets).GetField("Tile").GetValue(null) as Array;
            var outlineArray = typeof(TextureAssets).GetField("HighlightMask").GetValue(null) as Array;
            if (tileArray == null || outlineArray == null) return false;
            // Load originals first, then respect any texture already installed by another mod.
            if (ForceLoadAsset("Images/Tiles_" + type) == null ||
                ForceLoadAsset("Images/Misc/TileOutlines/Tiles_" + type) == null) return false;
            var original = GetTexture2DFromAsset(tileArray.GetValue(type));
            var outline = GetTexture2DFromAsset(outlineArray.GetValue(type));
            if (original == null || outline == null) return false;
            if (original.Height != outline.Height)
                throw new InvalidOperationException("Chest tile and outline animation heights differ");

            var tile = PrepareReplacement(tileArray, type, CreateCompactAtlas(original, true));
            Replacement mask;
            try { mask = PrepareReplacement(outlineArray, type, CreateCompactAtlas(outline, false)); }
            catch { tile.Texture.Dispose(); throw; }
            Install(tile);
            Install(mask);
            AtlasYOffset = original.Height;
            Main.instance?.TilePaintSystem?.Reset();
            _log?.Info($"Compact chest atlas {tile.Texture.Width}x{tile.Texture.Height}; saved style {PaintingChestManager.OUR_PLACE_STYLE}, draw row {AtlasYOffset}");
            return true;
        }

        private static Texture2D CreateCompactAtlas(Texture2D original, bool recolor)
        {
            int width = original.Width;
            int height = original.Height;
            if (width < (SOURCE_STYLE + 1) * STYLE_WIDTH || width > 2048 || height * 2 > 2048)
                throw new InvalidOperationException("Chest source atlas cannot fit the Reach texture limit");
            var source = new uint[width * height];
            original.GetData(source);
            var pixels = new uint[width * height * 2];
            Array.Copy(source, pixels, source.Length);
            for (int y = 0; y < height; y++)
                for (int x = 0; x < STYLE_WIDTH; x++)
                {
                    uint pixel = source[y * width + SOURCE_STYLE * STYLE_WIDTH + x];
                    pixels[(y + height) * width + x] = recolor ? Tint(pixel) : pixel;
                }
            var texture = new Texture2D(_graphicsDevice, width, height * 2);
            try { texture.SetData(pixels); return texture; }
            catch { texture.Dispose(); throw; }
        }

        private static uint Tint(uint pixel)
        {
            uint alpha = pixel >> 24;
            if (alpha == 0) return 0;
            uint r = (pixel & 255) * 60 / 255;
            uint g = ((pixel >> 8) & 255) * 40 / 255;
            uint b = (uint)Math.Min(255, ((pixel >> 16) & 255) * 180 / 255 + 80);
            return (alpha << 24) | (b << 16) | (g << 8) | r;
        }

        private static bool GenerateItemTexture()
        {
            int ourType = ItemRegistry.GetRuntimeType(PaintingChestManager.FULL_ITEM_ID);
            if (ourType < 0) return false;

            var itemArray = typeof(TextureAssets).GetField("Item", BindingFlags.Public | BindingFlags.Static)
                ?.GetValue(null) as Array;
            if (itemArray == null) return false;

            // Use gold chest item (306) as base for our item texture
            const int SOURCE_ITEM_ID = 306; // Gold Chest
            if (SOURCE_ITEM_ID >= itemArray.Length || ourType >= itemArray.Length) return false;

            var srcTexture = ForceLoadAsset("Images/Item_" + SOURCE_ITEM_ID);
            if (srcTexture == null) return false;

            int itemW = srcTexture.Width;
            int itemH = srcTexture.Height;
            var pixels = new uint[itemW * itemH];
            srcTexture.GetData(pixels);

            for (int i = 0; i < pixels.Length; i++) pixels[i] = Tint(pixels[i]);
            var newTexture = new Texture2D(_graphicsDevice, itemW, itemH);
            try { newTexture.SetData(pixels); }
            catch { newTexture.Dispose(); throw; }
            Install(PrepareReplacement(itemArray, ourType, newTexture));
            _log?.Info($"Generated mysterious chest item texture (type {ourType})");
            return true;
        }

        private static string GetAssetName(object asset)
        {
            return asset?.GetType().GetProperty("Name")?.GetValue(asset) as string;
        }

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
                if (valueField == null) return null;
                valueField.SetValue(instance, texture);

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
            catch (Exception ex) { _log?.Warn($"CreateAssetWrapper failed: {ex.Message}"); return null; }
        }
    }
}
