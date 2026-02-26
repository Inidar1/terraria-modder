using System;
using System.Reflection;
using TerrariaModder.Core.Config;
using TerrariaModder.Core.Logging;

namespace TerrariaModder.Core.Assets
{
    /// <summary>
    /// Main orchestrator for custom item + tile asset pipelines.
    /// </summary>
    public static class AssetSystem
    {
        private static ILogger _log;
        private static bool _initialized;
        private static bool _patchesApplied;
        private static bool _itemTexturesLoaded;
        private static bool _tileTexturesLoaded;
        private static int _textureRetryCount;
        private const int MAX_TEXTURE_RETRIES = 300; // ~5s at 60fps

        private static bool _hasCustomItems;
        private static bool _hasCustomTiles;
        private static bool _experimentalCustomTiles;

        /// <summary>Whether all texture injection is complete (items + tiles).</summary>
        public static bool TexturesStable => _itemTexturesLoaded && _tileTexturesLoaded;

        /// <summary>Whether experimental custom tile support is enabled in core config.</summary>
        public static bool ExperimentalCustomTiles => _experimentalCustomTiles;

        public static void Initialize(ILogger logger)
        {
            if (_initialized) return;

            _log = logger;
            _experimentalCustomTiles = CoreConfig.Instance.ExperimentalCustomTiles;

            AppDomain.CurrentDomain.UnhandledException += (sender, args) =>
            {
                var ex = args.ExceptionObject as Exception;
                _log?.Error($"[AssetSystem] UNHANDLED EXCEPTION: {ex?.Message}");
                _log?.Error($"[AssetSystem] Stack: {ex?.StackTrace}");
                if (ex?.InnerException != null)
                    _log?.Error($"[AssetSystem] Inner: {ex.InnerException.Message}\n{ex.InnerException.StackTrace}");
            };

            int vanillaItemCount = ReadVanillaItemCount();
            int vanillaTileCount = ReadVanillaTileCount();
            _log?.Info($"[AssetSystem] Vanilla ItemID.Count={vanillaItemCount}, TileID.Count={vanillaTileCount}");
            _log?.Info($"[AssetSystem] experimental_custom_tiles={_experimentalCustomTiles}");

            ItemRegistry.Initialize(logger, vanillaItemCount);
            TileRegistry.Initialize(logger, vanillaTileCount);

            ModdataFile.Initialize(logger);
            TextureLoader.Initialize(logger);
            TileTextureLoader.Initialize(logger);

            SetDefaultsPatch.Initialize(logger);
            LangPatches.Initialize(logger);
            TooltipPatches.Initialize(logger);
            PlayerSavePatches.Initialize(logger);
            WorldSavePatches.Initialize(logger);
            TileSavePatches.Initialize(logger);
            RecipeRegistrar.Initialize(logger);
            ContentPatches.Initialize(logger);
            ItemBehaviorPatches.Initialize(logger);
            ItemProtectionPatches.Initialize(logger);
            DrawPatches.Initialize(logger);
            TileObjectRegistrar.Initialize(logger);
            TileBehaviorPatches.Initialize(logger);
            PlayerAdjTileSafetyPatches.Initialize(logger);
            PendingItemsUI.Initialize(logger);

            _initialized = true;
            _log?.Info("[AssetSystem] Initialized");
        }

        public static void ApplyPatches()
        {
            if (_patchesApplied || !_initialized) return;

            try
            {
                // Step 1: assign deterministic runtime IDs
                ItemRegistry.AssignRuntimeTypes();
                if (_experimentalCustomTiles)
                    TileRegistry.AssignRuntimeTypes();

                _hasCustomItems = ItemRegistry.Count > 0;
                _hasCustomTiles = _experimentalCustomTiles && TileRegistry.Count > 0;

                // Item protection patches are safe to apply unconditionally.
                ItemProtectionPatches.ApplyPatches();

                if (!_hasCustomItems && !_hasCustomTiles)
                {
                    _itemTexturesLoaded = true;
                    _tileTexturesLoaded = true;
                    _patchesApplied = true;
                    _log?.Info("[AssetSystem] No custom items/tiles registered");
                    return;
                }

                // Save/load interception before array extension.
                if (_hasCustomItems)
                {
                    PlayerSavePatches.ApplyPatches();
                    WorldSavePatches.ApplyPatches();
                }
                if (_hasCustomTiles)
                    TileSavePatches.ApplyPatches();

                // Extend item arrays.
                if (_hasCustomItems)
                {
                    int newItemCount = ItemRegistry.VanillaItemCount + ItemRegistry.Count + 64;
                    TypeExtension.Apply(_log, newItemCount);
                }

                // Extend tile arrays + register tile metadata.
                if (_hasCustomTiles)
                {
                    int newTileCount = TileRegistry.VanillaTileCount + TileRegistry.Count + 256;
                    int result = TileTypeExtension.Apply(_log, newTileCount, failFast: true);
                    if (result < 0)
                        throw new InvalidOperationException("TileTypeExtension failed");

                    TileTextureLoader.ApplyPatches();
                    TileObjectRegistrar.ApplyDefinitions();
                    TileBehaviorPatches.ApplyPatches();
                    PlayerAdjTileSafetyPatches.ApplyPatches();
                }

                // Item runtime behavior patches.
                if (_hasCustomItems)
                {
                    SetDefaultsPatch.ApplyPatches();
                    LangPatches.ApplyPatches();
                    TooltipPatches.ApplyPatches();
                    DrawPatches.ApplyPatches();

                    // Required for item tooltip properties that read ContentSamples.
                    PopulateContentSamples();

                    ContentPatches.ApplyPatches();
                    ItemBehaviorPatches.ApplyPatches();
                }

                // Recipes can reference custom items and/or custom tiles.
                RecipeRegistrar.ApplyPatches();

                _patchesApplied = true;
                _log?.Info($"[AssetSystem] All patches applied (items={ItemRegistry.Count}, tiles={TileRegistry.Count})");
            }
            catch (Exception ex)
            {
                _log?.Error($"[AssetSystem] ApplyPatches failed: {ex.Message}\n{ex.StackTrace}");
            }
        }

        public static void OnContentLoaded()
        {
            if (!_patchesApplied) return;
            if (TexturesStable) return;

            try
            {
                if (!_hasCustomItems)
                {
                    _itemTexturesLoaded = true;
                }
                else if (!_itemTexturesLoaded)
                {
                    int loadedItems = TextureLoader.InjectAllTextures();
                    if (loadedItems > 0)
                        _log?.Info($"[AssetSystem] Initial item texture injection ({loadedItems})");
                    else
                        _itemTexturesLoaded = true; // nothing to inject
                }

                if (!_hasCustomTiles)
                {
                    _tileTexturesLoaded = true;
                }
                else if (!_tileTexturesLoaded)
                {
                    int loadedTiles = TileTextureLoader.InjectAllTextures();
                    if (loadedTiles > 0)
                        _log?.Info($"[AssetSystem] Initial tile texture injection ({loadedTiles})");
                    else
                        _tileTexturesLoaded = true; // nothing to inject
                }
            }
            catch (Exception ex)
            {
                _log?.Error($"[AssetSystem] OnContentLoaded texture injection failed: {ex.Message}");
            }
        }

        public static void OnUpdate()
        {
            if (!_patchesApplied) return;

            // TileObjectData can become available later in startup (during splash init).
            // Retry failed custom tile metadata registration until it succeeds.
            if (_hasCustomTiles)
                TileObjectRegistrar.ApplyDefinitions();

            if (TexturesStable) return;

            _textureRetryCount++;

            if (_textureRetryCount % 10 == 0)
            {
                if (_hasCustomItems && !_itemTexturesLoaded)
                    TextureLoader.ReinjectCachedTextures();
                if (_hasCustomTiles && !_tileTexturesLoaded)
                    TileTextureLoader.ReinjectCachedTextures();
            }

            if (_textureRetryCount >= MAX_TEXTURE_RETRIES)
            {
                if (_hasCustomItems && !_itemTexturesLoaded)
                    TextureLoader.ReinjectCachedTextures();
                if (_hasCustomTiles && !_tileTexturesLoaded)
                    TileTextureLoader.ReinjectCachedTextures();

                _itemTexturesLoaded = true;
                _tileTexturesLoaded = true;
                _log?.Info("[AssetSystem] Texture re-injection complete");
            }
        }

        private static int ReadVanillaItemCount()
        {
            try
            {
                var countField = typeof(Terraria.ID.ItemID).GetField("Count", BindingFlags.Public | BindingFlags.Static);
                if (countField != null)
                {
                    object val = countField.GetValue(null);
                    if (val is short s) return s;
                    if (val is int i) return i;
                }
            }
            catch (Exception ex)
            {
                _log?.Error($"[AssetSystem] Failed to read ItemID.Count: {ex.Message}");
            }

            return 6145; // Terraria 1.4.5
        }

        private static int ReadVanillaTileCount()
        {
            try
            {
                var countField = typeof(Terraria.ID.TileID).GetField("Count", BindingFlags.Public | BindingFlags.Static);
                if (countField != null)
                {
                    object val = countField.GetValue(null);
                    if (val is ushort us) return us;
                    if (val is short s) return s;
                    if (val is int i) return i;
                }
            }
            catch (Exception ex)
            {
                _log?.Error($"[AssetSystem] Failed to read TileID.Count: {ex.Message}");
            }

            return 700; // safe fallback
        }

        /// <summary>
        /// Add custom items to ContentSamples.ItemsByType dictionary.
        /// </summary>
        private static void PopulateContentSamples()
        {
            try
            {
                var samplesType = typeof(Terraria.Main).Assembly.GetType("Terraria.ID.ContentSamples");
                if (samplesType == null)
                {
                    _log?.Warn("[AssetSystem] ContentSamples type not found");
                    return;
                }

                var dictField = samplesType.GetField("ItemsByType", BindingFlags.Public | BindingFlags.Static);
                if (dictField == null)
                {
                    _log?.Warn("[AssetSystem] ContentSamples.ItemsByType not found");
                    return;
                }

                var dict = dictField.GetValue(null);
                if (dict == null)
                {
                    _log?.Warn("[AssetSystem] ContentSamples.ItemsByType is null");
                    return;
                }

                var dictType = dict.GetType();
                var containsKey = dictType.GetMethod("ContainsKey");
                var itemProp = dictType.GetProperty("Item");

                int added = 0;
                foreach (var fullId in ItemRegistry.AllIds)
                {
                    int runtimeType = ItemRegistry.GetRuntimeType(fullId);
                    if (runtimeType < 0) continue;

                    if (containsKey != null && (bool)containsKey.Invoke(dict, new object[] { runtimeType }))
                        continue;

                    var item = new Terraria.Item();
                    item.SetDefaults(runtimeType);

                    if (itemProp != null)
                    {
                        itemProp.SetValue(dict, item, new object[] { runtimeType });
                        added++;
                    }
                }

                _log?.Info($"[AssetSystem] Added {added} items to ContentSamples.ItemsByType");
            }
            catch (Exception ex)
            {
                _log?.Error($"[AssetSystem] PopulateContentSamples failed: {ex.Message}");
            }
        }

        // ---- Public registration API (called by ModContext) ----

        public static bool RegisterItem(string modId, string itemName, ItemDefinition definition)
        {
            return ItemRegistry.Register(modId, itemName, definition);
        }

        public static bool RegisterTile(string modId, string tileName, TileDefinition definition)
        {
            if (!_experimentalCustomTiles)
            {
                _log?.Warn($"[AssetSystem] Ignoring tile registration {modId}:{tileName} because experimental_custom_tiles=false");
                return false;
            }

            return TileRegistry.Register(modId, tileName, definition);
        }

        public static void RegisterModFolder(string modId, string folderPath)
        {
            ItemRegistry.RegisterModFolder(modId, folderPath);
            TileRegistry.RegisterModFolder(modId, folderPath);
        }

        public static void RegisterRecipe(RecipeDefinition recipe)
        {
            RecipeRegistrar.RegisterRecipe(recipe);
        }

        public static void RegisterDrop(DropDefinition drop)
        {
            ContentPatches.RegisterDrop(drop);
        }

        public static void RegisterShopItem(ShopDefinition shop)
        {
            ContentPatches.RegisterShopItem(shop);
        }

        public static bool IsCustomItem(int type) => ItemRegistry.IsCustomItem(type);
        public static string GetFullId(int runtimeType) => ItemRegistry.GetFullId(runtimeType);
        public static int GetRuntimeType(string fullId) => ItemRegistry.GetRuntimeType(fullId);
        public static ItemDefinition GetDefinition(int runtimeType) => ItemRegistry.GetDefinition(runtimeType);

        public static bool IsCustomTile(int tileType) => TileRegistry.IsCustomTile(tileType);
        public static string GetTileFullId(int runtimeTileType) => TileRegistry.GetFullId(runtimeTileType);
        public static int GetTileRuntimeType(string fullId) => TileRegistry.GetRuntimeType(fullId);
        public static TileDefinition GetTileDefinition(int runtimeTileType) => TileRegistry.GetDefinition(runtimeTileType);
        public static int ResolveTileType(string tileRef) => TileRegistry.ResolveTileType(tileRef);

        public static int PendingItemCount => PendingItemStore.TotalCount;
        public static void TogglePendingItemsUI() => PendingItemsUI.Toggle();
    }
}
