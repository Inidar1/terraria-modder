using System;
using System.Reflection;
using TerrariaModder.Core.Logging;

namespace TerrariaModder.Core.Assets
{
    /// <summary>
    /// Main orchestrator for the v3 asset system.
    ///
    /// Initialization order:
    ///   1. Initialize(logger) — called early during PluginLoader startup
    ///   2. Mods register items/recipes/drops/shops during their Initialize()
    ///   3. ApplyPatches() — after all mods loaded:
    ///      a. AssignRuntimeTypes (deterministic type IDs)
    ///      b. TypeExtension (resize arrays)
    ///      c. SetDefaultsPatch (intercept Item.SetDefaults for custom types)
    ///      d. PlayerSavePatches + WorldSavePatches (save interception)
    ///      e. RecipeRegistrar (inject recipes after SetupRecipes)
    ///      f. ContentPatches (drops + shops)
    ///   4. OnContentLoaded() — called from OnGameReady lifecycle hook:
    ///      - Texture injection (GraphicsDevice ready, patches applied)
    /// </summary>
    public static class AssetSystem
    {
        private static ILogger _log;
        private static bool _initialized;
        private static bool _patchesApplied;
        private static bool _texturesLoaded;
        private static int _textureRetryCount;
        private const int MAX_TEXTURE_RETRIES = 300; // ~5 seconds at 60fps

        /// <summary>Whether texture injection is complete (async loader finished).</summary>
        public static bool TexturesStable => _texturesLoaded;

        /// <summary>
        /// Initialize the asset system. Called early during startup.
        /// </summary>
        public static void Initialize(ILogger logger)
        {
            if (_initialized) return;

            _log = logger;

            // Install unhandled exception handler for crash diagnostics
            AppDomain.CurrentDomain.UnhandledException += (sender, args) =>
            {
                var ex = args.ExceptionObject as Exception;
                _log?.Error($"[AssetSystem] UNHANDLED EXCEPTION: {ex?.Message}");
                _log?.Error($"[AssetSystem] Stack: {ex?.StackTrace}");
                if (ex?.InnerException != null)
                    _log?.Error($"[AssetSystem] Inner: {ex.InnerException.Message}\n{ex.InnerException.StackTrace}");
            };

            // Read vanilla ItemID.Count before anything modifies it
            int vanillaCount = ReadVanillaItemCount();
            _log?.Info($"[AssetSystem] Vanilla ItemID.Count = {vanillaCount}");

            // Initialize subsystems
            ItemRegistry.Initialize(logger, vanillaCount);
            ModdataFile.Initialize(logger);
            TextureLoader.Initialize(logger);
            SetDefaultsPatch.Initialize(logger);
            LangPatches.Initialize(logger);
            TooltipPatches.Initialize(logger);
            PlayerSavePatches.Initialize(logger);
            WorldSavePatches.Initialize(logger);
            RecipeRegistrar.Initialize(logger);
            ContentPatches.Initialize(logger);
            ItemBehaviorPatches.Initialize(logger);
            ItemProtectionPatches.Initialize(logger);
            DrawPatches.Initialize(logger);
            PendingItemsUI.Initialize(logger);

            _initialized = true;
            _log?.Info("[AssetSystem] Initialized");
        }

        /// <summary>
        /// Apply all Harmony patches after mods have registered their content.
        /// </summary>
        public static void ApplyPatches()
        {
            if (_patchesApplied || !_initialized) return;

            try
            {
                // Step 1: Assign deterministic runtime types
                ItemRegistry.AssignRuntimeTypes();

                // Apply protection patches unconditionally — protect even if 0 custom
                // items are registered now, since items may exist from a previous session
                ItemProtectionPatches.ApplyPatches();

                if (ItemRegistry.Count == 0)
                {
                    _log?.Info("[AssetSystem] No custom items registered, skipping remaining patches");
                    _patchesApplied = true;
                    return;
                }

                // Step 1: Patch save methods BEFORE TypeExtension resizes arrays
                // (Harmony can't rewrite SavePlayer/LoadPlayer IL after array modifications)
                PlayerSavePatches.ApplyPatches();
                WorldSavePatches.ApplyPatches();

                // Step 2: Extend type system (resize arrays, NOT ItemID.Count)
                int newCount = ItemRegistry.VanillaItemCount + ItemRegistry.Count + 64;
                TypeExtension.Apply(_log, newCount);

                // Step 3: Apply remaining patches
                SetDefaultsPatch.ApplyPatches();
                LangPatches.ApplyPatches();
                TooltipPatches.ApplyPatches();
                DrawPatches.ApplyPatches();

                // Step 4: Populate ContentSamples.ItemsByType with custom items
                // MUST happen after SetDefaultsPatch so SetDefaults works for custom types.
                // Item.OriginalRarity/OriginalDamage/OriginalDefense access this dictionary
                // and crash with KeyNotFoundException if our types aren't present.
                PopulateContentSamples();

                RecipeRegistrar.ApplyPatches();
                ContentPatches.ApplyPatches();
                ItemBehaviorPatches.ApplyPatches();

                _patchesApplied = true;
                _log?.Info($"[AssetSystem] All patches applied ({ItemRegistry.Count} custom items)");
            }
            catch (Exception ex)
            {
                _log?.Error($"[AssetSystem] ApplyPatches failed: {ex.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Called by lifecycle hook when Main.LoadContent() completes.
        /// GraphicsDevice and SpriteBatch are guaranteed ready.
        /// </summary>
        public static void OnContentLoaded()
        {
            if (_texturesLoaded || !_patchesApplied) return;

            try
            {
                int loaded = TextureLoader.InjectAllTextures();
                // Don't set _texturesLoaded here — OnUpdate() re-injects periodically
                // to survive the async texture loader (AssetInitializer.LoadTextures)
                // which iterates TextureAssets.Item.Length and overwrites our entries.
                if (loaded > 0)
                    _log?.Info($"[AssetSystem] Initial texture injection ({loaded} textures)");
                else
                {
                    _texturesLoaded = true; // Nothing to inject, no retry needed
                    _log?.Info("[AssetSystem] No textures to inject");
                }
            }
            catch (Exception ex)
            {
                _log?.Error($"[AssetSystem] OnContentLoaded texture injection failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Called each frame via FrameEvents.OnPostUpdate.
        /// Re-injects cached textures periodically to survive the async texture loader
        /// (AssetInitializer.LoadTextures) which iterates TextureAssets.Item.Length
        /// and overwrites our custom entries with null/failed Assets.
        /// </summary>
        public static void OnUpdate()
        {
            if (!_patchesApplied || _texturesLoaded) return;
            if (ItemRegistry.Count == 0)
            {
                _texturesLoaded = true;
                return;
            }

            _textureRetryCount++;

            // Re-inject cached textures every 10 frames (~167ms)
            if (_textureRetryCount % 10 == 0)
                TextureLoader.ReinjectCachedTextures();

            if (_textureRetryCount >= MAX_TEXTURE_RETRIES)
            {
                TextureLoader.ReinjectCachedTextures();
                _texturesLoaded = true;
                _log?.Info("[AssetSystem] Texture re-injection complete");
            }
        }

        /// <summary>
        /// Read the original ItemID.Count before any modifications.
        /// </summary>
        private static int ReadVanillaItemCount()
        {
            try
            {
                var countField = typeof(Terraria.ID.ItemID).GetField("Count",
                    BindingFlags.Public | BindingFlags.Static);
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

            return 6145; // Known Terraria 1.4.5 value
        }

        /// <summary>
        /// Add custom items to ContentSamples.ItemsByType dictionary.
        /// Vanilla tooltip code accesses item.OriginalRarity/OriginalDamage/OriginalDefense
        /// which read from this dictionary. Without entries, hovering crashes.
        /// </summary>
        private static void PopulateContentSamples()
        {
            try
            {
                var samplesType = typeof(Terraria.Main).Assembly
                    .GetType("Terraria.ID.ContentSamples");
                if (samplesType == null)
                {
                    _log?.Warn("[AssetSystem] ContentSamples type not found");
                    return;
                }

                var dictField = samplesType.GetField("ItemsByType",
                    BindingFlags.Public | BindingFlags.Static);
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

                // Dictionary<int, Item> — use reflection to call Add/indexer
                var dictType = dict.GetType();
                var containsKey = dictType.GetMethod("ContainsKey");
                var itemProp = dictType.GetProperty("Item"); // indexer

                int added = 0;
                foreach (var fullId in ItemRegistry.AllIds)
                {
                    int runtimeType = ItemRegistry.GetRuntimeType(fullId);
                    if (runtimeType < 0) continue;

                    // Check if already present
                    if (containsKey != null && (bool)containsKey.Invoke(dict, new object[] { runtimeType }))
                        continue;

                    // Create a sample item via SetDefaults (our patch handles custom types)
                    var item = new Terraria.Item();
                    item.SetDefaults(runtimeType);

                    // Add to dictionary via indexer
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

        // ── Public registration API (called by ModContext) ──

        /// <summary>
        /// Register a custom item. Called by mods during Initialize().
        /// </summary>
        public static bool RegisterItem(string modId, string itemName, ItemDefinition definition)
        {
            return ItemRegistry.Register(modId, itemName, definition);
        }

        /// <summary>
        /// Register a mod's folder path for texture loading.
        /// </summary>
        public static void RegisterModFolder(string modId, string folderPath)
        {
            ItemRegistry.RegisterModFolder(modId, folderPath);
        }

        /// <summary>
        /// Register a recipe.
        /// </summary>
        public static void RegisterRecipe(RecipeDefinition recipe)
        {
            RecipeRegistrar.RegisterRecipe(recipe);
        }

        /// <summary>
        /// Register an NPC drop.
        /// </summary>
        public static void RegisterDrop(DropDefinition drop)
        {
            ContentPatches.RegisterDrop(drop);
        }

        /// <summary>
        /// Register a shop item.
        /// </summary>
        public static void RegisterShopItem(ShopDefinition shop)
        {
            ContentPatches.RegisterShopItem(shop);
        }

        /// <summary>
        /// Check if a runtime type is a custom item.
        /// </summary>
        public static bool IsCustomItem(int type) => ItemRegistry.IsCustomItem(type);

        /// <summary>
        /// Get the full ID ("modid:itemname") for a runtime type.
        /// </summary>
        public static string GetFullId(int runtimeType) => ItemRegistry.GetFullId(runtimeType);

        /// <summary>
        /// Get the runtime type for a full ID.
        /// </summary>
        public static int GetRuntimeType(string fullId) => ItemRegistry.GetRuntimeType(fullId);

        /// <summary>
        /// Get definition for a runtime type.
        /// </summary>
        public static ItemDefinition GetDefinition(int runtimeType) => ItemRegistry.GetDefinition(runtimeType);

        /// <summary>
        /// Number of pending items (overflow from load).
        /// </summary>
        public static int PendingItemCount => PendingItemStore.TotalCount;

        /// <summary>
        /// Toggle the pending items UI panel.
        /// </summary>
        public static void TogglePendingItemsUI() => PendingItemsUI.Toggle();
    }
}
