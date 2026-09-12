using System;
using System.Collections.Generic;
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
        private static int _textureStableFrames;
        private const int STABLE_FRAMES_REQUIRED = 5; // 5 consecutive checks with no overwrites → done
        private const int MAX_TEXTURE_RETRIES = 300;  // safety ceiling ~5s at 60fps

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
                if (ex != null)
                {
                    // Log full ToString() which includes type, message, and complete stack with all frames
                    _log?.Error($"[AssetSystem] UNHANDLED EXCEPTION:\n{ex}");
                    // Walk inner exceptions
                    var inner = ex.InnerException;
                    int depth = 0;
                    while (inner != null && depth < 5)
                    {
                        _log?.Error($"[AssetSystem] INNER[{depth}]:\n{inner}");
                        inner = inner.InnerException;
                        depth++;
                    }
                }
                else
                {
                    _log?.Error($"[AssetSystem] UNHANDLED EXCEPTION (non-Exception): {args.ExceptionObject}");
                }
            };

            // Read vanilla ItemID.Count before anything modifies it
            int vanillaCount = ReadVanillaItemCount();
            _log?.Info($"[AssetSystem] Vanilla ItemID.Count = {vanillaCount}");

            // Initialize subsystems — always (server + client)
            ItemRegistry.Initialize(logger, vanillaCount);

            // Register ItemRegistry with the ContentRegistry coordinator.
            // Future content types (tiles, NPCs) register their adapters here too.
            ContentRegistry.Register(new ItemRegistry.Adapter());
            ModdataFile.Initialize(logger);
            SetDefaultsPatch.Initialize(logger);
            WorldSavePatches.Initialize(logger);
            ShimmerRegistry.Initialize(logger);

            // LangPatches runs on ALL processes — server needs item.Name to be non-empty
            // for custom items, otherwise NetMessage.SendData(5) resets them to air.
            LangPatches.Initialize(logger);

            // PlayerSavePatches runs on ALL processes — server needs to load custom items
            // from .plr sidecar files so they survive H&P inventory sync. No XNA dependency.
            PlayerSavePatches.Initialize(logger);

            // Client-only subsystems (no XNA / UI / texture access on dedicated server)
            if (!PluginLoader.IsDedicatedServer)
            {
                TextureLoader.Initialize(logger);
                TooltipPatches.Initialize(logger);
                RecipeRegistrar.Initialize(logger);
                ContentPatches.Initialize(logger);
                ItemBehaviorPatches.Initialize(logger);
                ItemProtectionPatches.Initialize(logger);
                DrawPatches.Initialize(logger);
                PendingItemsUI.Initialize(logger);
            }

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
                // Step 1: Assign deterministic runtime types for all content types
                ContentRegistry.AssignAllRuntimeTypes();

                // Apply protection patches unconditionally — protect even if 0 custom
                // items are registered now, since items may exist from a previous session
                ItemProtectionPatches.ApplyPatches();

                if (ContentRegistry.TotalCount == 0)
                {
                    _log?.Info("[AssetSystem] No custom content registered, skipping remaining patches");
                    _patchesApplied = true;
                    return;
                }

                // Step 2: Patch world save BEFORE TypeExtension resizes arrays.
                // WorldSavePatches always runs — dedicated server owns Main.chest[].
                // PlayerSavePatches is client-only — clients save their own inventories.
                WorldSavePatches.ApplyPatches();

                // Step 3: Extend type system (resize arrays, NOT ItemID.Count).
                // FNV-1a hash IDs live in [VanillaItemCount, 32767) — arrays extended to 32767.
                // The descriptor lists the Sets class to scan + named fields to resize explicitly.
                var descriptor = new ContentTypeDescriptor
                {
                    IdClass = typeof(Terraria.ID.ItemID),
                    VanillaCount = ItemRegistry.VanillaItemCount,
                    ExtendedCount = 32767,
                    AdditionalFields = new[]
                    {
                        // TextureAssets: Item textures and flame overlays
                        new ContentTypeDescriptor.NamedField(typeof(Terraria.GameContent.TextureAssets), "Item"),
                        new ContentTypeDescriptor.NamedField(typeof(Terraria.GameContent.TextureAssets), "ItemFlame"),
                        // Lang caches: display name and tooltip text
                        new ContentTypeDescriptor.NamedField(typeof(Terraria.Lang), "_itemNameCache"),
                        new ContentTypeDescriptor.NamedField(typeof(Terraria.Lang), "_itemTooltipCache"),
                        // Main: animation data
                        new ContentTypeDescriptor.NamedField(typeof(Terraria.Main), "itemAnimations"),
                        // Item: staff and claw bool arrays indexed by type
                        new ContentTypeDescriptor.NamedField(typeof(Terraria.Item), "staff"),
                        new ContentTypeDescriptor.NamedField(typeof(Terraria.Item), "claw"),
                    }
                };
                if (TypeExtension.Apply(_log, descriptor) < 0)
                    throw new InvalidOperationException("Custom content type extension failed; remaining asset patches were not applied");

                // Invalidate any cached array refs that TypeExtension may have resized
                LangPatches.InvalidateCaches();
                SetDefaultsPatch.InvalidateMaterialCache();

                // J3: Shimmer registry — server-safe (shimmer transforms run server-side in multiplayer)
                ShimmerRegistry.ApplyPatches();

                // SetDefaultsPatch runs on ALL processes (including dedServ) — required for custom items
                // in chests to survive world load (Item.SetDefaults intercept). No XNA dependency.
                SetDefaultsPatch.ApplyPatches();

                // LangPatches runs on ALL processes — server needs item.Name for custom items
                // to be non-empty, otherwise NetMessage.SendData(5) resets them to air (type 0).
                LangPatches.ApplyPatches();

                // PlayerSavePatches runs on ALL processes — server needs to extract/inject
                // custom items from .plr sidecar files during H&P player sync.
                PlayerSavePatches.ApplyPatches();

                // Step 3: Client-only patches (XNA / texture / UI / recipe systems not present on dedicated server)
                if (!PluginLoader.IsDedicatedServer)
                {
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
                }

                _patchesApplied = true;
                _log?.Info($"[AssetSystem] All patches applied ({ContentRegistry.TotalCount} custom items)");
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
        /// Verifies custom texture slots are still intact (they should be — DrawPatches.LoadItem_Prefix
        /// already blocks vanilla LoadItem for custom types). If a slot was overwritten for any
        /// unexpected reason, re-injects from cache. Once STABLE_FRAMES_REQUIRED consecutive checks
        /// pass cleanly, marks textures as stable and stops polling.
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

            // Check every 10 frames (~167ms) — no need for every-frame polling
            if (_textureRetryCount % 10 != 0) return;

            bool stable = TextureLoader.VerifyTextures();

            if (stable)
            {
                _textureStableFrames++;
                if (_textureStableFrames >= STABLE_FRAMES_REQUIRED)
                {
                    _texturesLoaded = true;
                    _log?.Info($"[AssetSystem] Texture injection stable after {_textureRetryCount} frames");
                }
            }
            else
            {
                // Unexpected overwrite — re-inject and reset stability counter
                _textureStableFrames = 0;
                TextureLoader.ReinjectCachedTextures();
                _log?.Debug($"[AssetSystem] Texture overwrite detected at frame {_textureRetryCount} — re-injecting");
            }

            // Safety ceiling: stop retrying regardless of stability
            if (_textureRetryCount >= MAX_TEXTURE_RETRIES)
            {
                if (!stable) TextureLoader.ReinjectCachedTextures();
                _texturesLoaded = true;
                _log?.Info($"[AssetSystem] Texture stability check ceiling reached ({MAX_TEXTURE_RETRIES} frames)");
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
        /// Register a shimmer transform.
        /// </summary>
        public static void RegisterShimmer(ShimmerDefinition shimmer)
        {
            ShimmerRegistry.Register(shimmer);
        }

        /// <summary>
        /// Register a global tooltip modifier callback.
        /// Only called client-side (no tooltip rendering on server).
        /// </summary>
        public static void RegisterTooltipModifier(Action<int, List<string>> modifier)
        {
            if (!PluginLoader.IsDedicatedServer)
                TooltipRegistry.Register(modifier);
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
