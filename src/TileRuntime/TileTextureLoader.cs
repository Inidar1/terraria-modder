using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;
using TerrariaModder.Core.Logging;

namespace TerrariaModder.TileRuntime
{
    /// <summary>
    /// Loads and injects runtime-owned custom tile textures into TextureAssets.Tile[runtimeTileId].
    /// </summary>
    internal static class TileTextureLoader
    {
        private static ILogger _log;
        private static Type _texture2dType;
        private static MethodInfo _fromStreamMethod;
        private static object _graphicsDevice;
        private static bool _reflectionReady;
        private static bool _reflectionFailed;
        private static bool _patchesApplied;
        private static Harmony _harmony;

        private static readonly Dictionary<int, object> _assetCache = new Dictionary<int, object>();

        public static void Initialize(ILogger logger)
        {
            _log = logger;
            _harmony = new Harmony("com.terrariamodder.tileruntime.tiletextures");
        }

        public static void ApplyPatches()
        {
            if (_patchesApplied) return;

            try
            {
                var loadTiles = typeof(Terraria.Main).GetMethod("LoadTiles",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                    null,
                    new[] { typeof(int) },
                    null);

                if (loadTiles == null)
                {
                    _log?.Warn("[TileRuntime.TileTextureLoader] Main.LoadTiles(int) not found");
                    return;
                }

                var prefix = typeof(TileTextureLoader).GetMethod(nameof(LoadTiles_Prefix), BindingFlags.NonPublic | BindingFlags.Static);
                _harmony.Patch(loadTiles, prefix: new HarmonyMethod(prefix));

                _patchesApplied = true;
                _log?.Info("[TileRuntime.TileTextureLoader] Patched Main.LoadTiles(int) for custom tile IDs");
            }
            catch (Exception ex)
            {
                _log?.Error($"[TileRuntime.TileTextureLoader] Failed to patch Main.LoadTiles: {ex.Message}");
            }
        }

        public static int InjectAllTextures()
        {
            if (!InitializeReflection())
            {
                _log?.Warn("[TileRuntime.TileTextureLoader] Cannot inject textures - XNA reflection not ready");
                return 0;
            }

            int injected = 0;
            int placeholders = 0;

            foreach (var fullId in TileRegistry.AllIds)
            {
                int runtimeType = TileRegistry.GetRuntimeType(fullId);
                if (runtimeType < 0) continue;

                var def = TileRegistry.GetDefinitionById(fullId);
                if (def == null) continue;

                bool hasTexture = false;
                if (!string.IsNullOrEmpty(def.TexturePath))
                {
                    int colon = fullId.IndexOf(':');
                    if (colon > 0)
                    {
                        string modId = fullId.Substring(0, colon);
                        string modFolder = TileRegistry.GetModFolder(modId);
                        if (string.IsNullOrEmpty(modFolder))
                        {
                            _log?.Warn($"[TileRuntime.TileTextureLoader] No mod folder registered for {modId} while loading {fullId}");
                        }
                        else
                        {
                            string path = Path.Combine(modFolder, def.TexturePath.Replace('/', Path.DirectorySeparatorChar));
                            if (File.Exists(path))
                            {
                                if (InjectTexture(runtimeType, path))
                                {
                                    injected++;
                                    hasTexture = true;
                                }
                            }
                            else
                            {
                                _log?.Warn($"[TileRuntime.TileTextureLoader] Texture not found: {path} for {fullId}");
                            }
                        }
                    }
                }

                if (!hasTexture && InjectPlaceholder(runtimeType))
                    placeholders++;
            }

            _log?.Info($"[TileRuntime.TileTextureLoader] Injected {injected} textures, {placeholders} placeholders");
            return injected + placeholders;
        }

        public static int ReinjectCachedTextures()
        {
            if (_assetCache.Count == 0) return 0;

            try
            {
                var field = typeof(Terraria.GameContent.TextureAssets).GetField("Tile", BindingFlags.Public | BindingFlags.Static);
                var arr = field?.GetValue(null) as Array;
                if (arr == null) return 0;

                int restored = 0;
                foreach (var kvp in _assetCache)
                {
                    if (kvp.Key >= 0 && kvp.Key < arr.Length)
                    {
                        arr.SetValue(kvp.Value, kvp.Key);
                        restored++;
                    }
                }
                return restored;
            }
            catch
            {
                return 0;
            }
        }

        private static bool InjectTexture(int runtimeType, string pngPath)
        {
            try
            {
                object texture;
                using (var stream = File.OpenRead(pngPath))
                {
                    texture = _fromStreamMethod.Invoke(null, new object[] { _graphicsDevice, stream });
                }

                if (texture == null)
                    return false;

                var field = typeof(Terraria.GameContent.TextureAssets).GetField("Tile", BindingFlags.Public | BindingFlags.Static);
                var arr = field?.GetValue(null) as Array;
                if (arr == null || runtimeType < 0 || runtimeType >= arr.Length)
                    return false;

                var assetType = arr.GetType().GetElementType();
                var asset = CreateAssetWrapper(assetType, texture, $"TerrariaModder/CustomTile_{runtimeType}");
                if (asset == null) return false;

                arr.SetValue(asset, runtimeType);
                _assetCache[runtimeType] = asset;
                return true;
            }
            catch (TargetInvocationException tie) when (tie.InnerException != null)
            {
                _log?.Warn($"[TileRuntime.TileTextureLoader] Failed to load {pngPath}: {tie.InnerException.Message}");
                return false;
            }
            catch (Exception ex)
            {
                _log?.Warn($"[TileRuntime.TileTextureLoader] Failed to load {pngPath}: {ex.Message}");
                return false;
            }
        }

        private static bool InjectPlaceholder(int runtimeType)
        {
            try
            {
                var field = typeof(Terraria.GameContent.TextureAssets).GetField("Tile", BindingFlags.Public | BindingFlags.Static);
                var arr = field?.GetValue(null) as Array;
                if (arr == null || runtimeType < 0 || runtimeType >= arr.Length)
                {
                    _log?.Warn($"[TileRuntime.TileTextureLoader] Placeholder injection unavailable for tile {runtimeType}: TextureAssets.Tile not ready");
                    return false;
                }

                object placeholder = arr.GetValue(0);
                if (placeholder == null)
                {
                    _log?.Warn($"[TileRuntime.TileTextureLoader] Placeholder texture slot 0 is null for tile {runtimeType}");
                    return false;
                }

                arr.SetValue(placeholder, runtimeType);
                _assetCache[runtimeType] = placeholder;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool LoadTiles_Prefix(int i)
        {
            try
            {
                if (i < TileRegistry.VanillaTileCount)
                    return true;

                ForceTileTextureSlot(i);
                return false;
            }
            catch
            {
                return false;
            }
        }

        private static void ForceTileTextureSlot(int tileType)
        {
            var field = typeof(Terraria.GameContent.TextureAssets).GetField("Tile", BindingFlags.Public | BindingFlags.Static);
            var arr = field?.GetValue(null) as Array;
            if (arr == null || tileType < 0 || tileType >= arr.Length)
                return;

            if (_assetCache.TryGetValue(tileType, out var cached) && cached != null)
            {
                arr.SetValue(cached, tileType);
                return;
            }

            object placeholder = arr.GetValue(0);
            if (placeholder != null)
            {
                arr.SetValue(placeholder, tileType);
                _assetCache[tileType] = placeholder;
            }
        }

        private static object CreateAssetWrapper(Type assetType, object texture, string assetName)
        {
            try
            {
                var instance = Activator.CreateInstance(assetType, BindingFlags.NonPublic | BindingFlags.Instance, null,
                    new object[] { assetName }, null);
                if (instance == null)
                    instance = Activator.CreateInstance(assetType, true);
                if (instance == null)
                    return null;

                var valueField = assetType.GetField("<Value>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance)
                    ?? assetType.GetField("_value", BindingFlags.NonPublic | BindingFlags.Instance)
                    ?? assetType.GetField("ownValue", BindingFlags.NonPublic | BindingFlags.Instance);
                valueField?.SetValue(instance, texture);

                var stateField = assetType.GetField("<State>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance)
                    ?? assetType.GetField("_state", BindingFlags.NonPublic | BindingFlags.Instance)
                    ?? assetType.GetField("state", BindingFlags.NonPublic | BindingFlags.Instance);
                if (stateField != null)
                {
                    if (stateField.FieldType.IsEnum)
                        stateField.SetValue(instance, Enum.ToObject(stateField.FieldType, 2));
                    else if (stateField.FieldType == typeof(int))
                        stateField.SetValue(instance, 2);
                }

                return instance;
            }
            catch
            {
                return null;
            }
        }

        private static bool InitializeReflection()
        {
            if (_reflectionFailed) return false;
            if (_reflectionReady) return true;

            try
            {
                var mainType = typeof(Terraria.Main);
                var assembly = mainType.Assembly;

                _texture2dType = assembly.GetType("Microsoft.Xna.Framework.Graphics.Texture2D");
                if (_texture2dType == null)
                {
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        _texture2dType = asm.GetType("Microsoft.Xna.Framework.Graphics.Texture2D");
                        if (_texture2dType != null) break;
                    }
                }
                if (_texture2dType == null)
                {
                    _reflectionFailed = true;
                    return false;
                }

                var gdType = _texture2dType.Assembly.GetType("Microsoft.Xna.Framework.Graphics.GraphicsDevice");
                if (gdType == null)
                {
                    _reflectionFailed = true;
                    return false;
                }

                _fromStreamMethod = _texture2dType.GetMethod("FromStream",
                    BindingFlags.Public | BindingFlags.Static, null,
                    new[] { gdType, typeof(Stream) }, null);
                if (_fromStreamMethod == null)
                {
                    _reflectionFailed = true;
                    return false;
                }

                var graphicsProp = typeof(Terraria.Main).GetProperty("graphics", BindingFlags.Public | BindingFlags.Static);
                if (graphicsProp != null)
                {
                    var gm = graphicsProp.GetValue(null);
                    var gdProp = gm?.GetType().GetProperty("GraphicsDevice");
                    _graphicsDevice = gdProp?.GetValue(gm);
                }

                if (_graphicsDevice == null)
                {
                    var instField = typeof(Terraria.Main).GetField("instance", BindingFlags.Public | BindingFlags.Static);
                    var inst = instField?.GetValue(null);
                    var gdProp = inst?.GetType().GetProperty("GraphicsDevice");
                    _graphicsDevice = gdProp?.GetValue(inst);
                }

                if (_graphicsDevice == null)
                    return false;

                _reflectionReady = true;
                _log?.Info("[TileRuntime.TileTextureLoader] XNA reflection initialized");
                return true;
            }
            catch (Exception ex)
            {
                _log?.Error($"[TileRuntime.TileTextureLoader] Reflection init failed: {ex.Message}");
                _reflectionFailed = true;
                return false;
            }
        }
    }
}
