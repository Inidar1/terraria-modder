using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Terraria;
using TerrariaModder.Core.Logging;

namespace TerrariaModder.Core.Assets
{
    /// <summary>
    /// Registers custom recipes into Terraria's Recipe system.
    /// Postfixes Recipe.SetupRecipes() to inject modded recipes into Main.recipe[].
    /// </summary>
    internal static class RecipeRegistrar
    {
        private static Harmony _harmony;
        private static ILogger _log;
        private static bool _applied;

        private static readonly List<RecipeDefinition> _recipes = new List<RecipeDefinition>();

        // Tile name → TileID cache
        private static Dictionary<string, int> _tileNameCache;

        public static void Initialize(ILogger logger)
        {
            _log = logger;
            _harmony = new Harmony("com.terrariamodder.assets.v3.recipes");
        }

        /// <summary>
        /// Register a recipe to be added after vanilla recipe setup.
        /// </summary>
        public static void RegisterRecipe(RecipeDefinition recipe)
        {
            if (recipe == null) return;
            _recipes.Add(recipe);
        }

        public static void ApplyPatches()
        {
            if (_applied) return;
            if (_recipes.Count == 0)
            {
                _log?.Debug("[RecipeRegistrar] No recipes registered, skipping patch");
                return;
            }

            try
            {
                var setupMethod = typeof(Recipe).GetMethod("SetupRecipes",
                    BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic);

                if (setupMethod == null)
                {
                    _log?.Warn("[RecipeRegistrar] Recipe.SetupRecipes not found");
                    return;
                }

                _harmony.Patch(setupMethod,
                    postfix: new HarmonyMethod(typeof(RecipeRegistrar), nameof(SetupRecipes_Postfix)));
                _applied = true;
                _log?.Info($"[RecipeRegistrar] Patched, {_recipes.Count} recipes pending");
            }
            catch (Exception ex)
            {
                _log?.Error($"[RecipeRegistrar] Patch failed: {ex.Message}");
            }
        }

        /// <summary>
        /// After vanilla recipes are set up, inject our custom recipes.
        /// </summary>
        private static void SetupRecipes_Postfix()
        {
            int added = 0;
            foreach (var recipeDef in _recipes)
            {
                try
                {
                    if (AddRecipe(recipeDef))
                        added++;
                }
                catch (Exception ex)
                {
                    _log?.Error($"[RecipeRegistrar] Failed to add recipe for {recipeDef.Result}: {ex.Message}");
                }
            }
            _log?.Info($"[RecipeRegistrar] Added {added}/{_recipes.Count} recipes");
        }

        private static bool AddRecipe(RecipeDefinition def)
        {
            // Resolve result item
            int resultType = ItemRegistry.ResolveItemType(def.Result);
            if (resultType < 0)
            {
                _log?.Warn($"[RecipeRegistrar] Cannot resolve result: {def.Result}");
                return false;
            }

            // Check capacity
            if (Recipe.numRecipes >= Recipe.maxRecipes)
            {
                _log?.Warn("[RecipeRegistrar] Recipe limit reached");
                return false;
            }

            // Build recipe
            var recipe = new Recipe();

            // Initialize requiredItem array (new Recipe() should do this, but be safe)
            if (recipe.requiredItem == null)
            {
                recipe.requiredItem = new Item[Recipe.maxRequirements];
                for (int i = 0; i < recipe.requiredItem.Length; i++)
                    recipe.requiredItem[i] = new Item();
            }

            // Set result
            recipe.createItem.SetDefaults(resultType);
            recipe.createItem.stack = def.ResultStack;

            // Add ingredients
            int idx = 0;
            foreach (var kvp in def.Ingredients)
            {
                if (idx >= Recipe.maxRequirements) break;

                int ingredientType = ItemRegistry.ResolveItemType(kvp.Key);
                if (ingredientType < 0)
                {
                    _log?.Warn($"[RecipeRegistrar] Cannot resolve ingredient: {kvp.Key}");
                    return false;
                }

                recipe.requiredItem[idx].SetDefaults(ingredientType);
                recipe.requiredItem[idx].stack = kvp.Value;
                idx++;
            }

            // Set crafting station
            if (!string.IsNullOrEmpty(def.Station))
            {
                int tileType = ResolveTileType(def.Station);
                if (tileType >= 0)
                {
                    recipe.requiredTile = tileType;
                    if (tileType == 13) // Placed Bottle → alchemy
                        recipe.alchemy = true;
                }
                else
                {
                    _log?.Warn($"[RecipeRegistrar] Cannot resolve station: {def.Station}");
                }
            }

            // Build quick lookup for recipe matching
            BuildQuickLookup(recipe);

            // Add to recipe array (mimics Recipe.AddRecipe())
            Main.recipe[Recipe.numRecipes] = recipe;
            if (recipe.requiredTile >= 0 && recipe.requiredTile < Recipe.TileUsedInRecipes.Length)
                Recipe.TileUsedInRecipes[recipe.requiredTile] = true;
            Recipe.numRecipes++;

            _log?.Debug($"[RecipeRegistrar] Added recipe: {def.Result} (type {resultType})");
            return true;
        }

        /// <summary>
        /// Build the requiredItemQuickLookup array that Terraria uses for fast recipe matching.
        /// </summary>
        private static void BuildQuickLookup(Recipe recipe)
        {
            try
            {
                if (recipe.requiredItemQuickLookup == null)
                    recipe.requiredItemQuickLookup = new Recipe.RequiredItemEntry[Recipe.maxRequirements];

                for (int i = 0; i < recipe.requiredItem.Length; i++)
                {
                    if (recipe.requiredItem[i] != null && recipe.requiredItem[i].type > 0)
                    {
                        recipe.requiredItemQuickLookup[i] = new Recipe.RequiredItemEntry(
                            recipe.requiredItem[i].type,
                            recipe.requiredItem[i].stack);
                    }
                }
            }
            catch (Exception ex)
            {
                _log?.Debug($"[RecipeRegistrar] QuickLookup build failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Resolve tile name to TileID. Supports "WorkBench", "Anvil", "Furnace", etc.
        /// Also supports raw int IDs as strings.
        /// </summary>
        private static int ResolveTileType(string name)
        {
            if (string.IsNullOrEmpty(name)) return -1;

            // Try as int first
            if (int.TryParse(name, out int tileId))
                return tileId;

            // Build cache on first use
            if (_tileNameCache == null)
            {
                _tileNameCache = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    var tileIdType = typeof(Terraria.ID.TileID);
                    foreach (var field in tileIdType.GetFields(BindingFlags.Public | BindingFlags.Static))
                    {
                        if (field.FieldType == typeof(ushort))
                        {
                            ushort val = (ushort)field.GetValue(null);
                            _tileNameCache[field.Name] = val;
                        }
                    }
                    _log?.Debug($"[RecipeRegistrar] Built tile cache: {_tileNameCache.Count} tiles");
                }
                catch (Exception ex)
                {
                    _log?.Warn($"[RecipeRegistrar] Failed to build tile cache: {ex.Message}");
                }
            }

            return _tileNameCache.TryGetValue(name, out int id) ? id : -1;
        }

        public static void Clear()
        {
            _recipes.Clear();
            _tileNameCache = null;
        }
    }
}
