using System;
using System.Collections.Generic;
using Terraria;
using TerrariaModder.Core.Logging;

namespace StorageHub.Crafting
{
    /// <summary>
    /// Indexes all Terraria recipes for efficient lookup.
    /// Built once at world load, provides fast queries by output item or ingredient.
    ///
    /// Why this exists:
    /// - Iterating all 5000+ recipes every frame is expensive
    /// - Pre-indexed dictionaries enable O(1) lookup
    /// - Supports both "what can I make" and "where is this used" queries
    /// </summary>
    public class RecipeIndex
    {
        private readonly ILogger _log;

        // Recipe data cached from reflection
        private List<RecipeInfo> _allRecipes = new List<RecipeInfo>();

        // Index: Output item type -> List of recipe indices
        private Dictionary<int, List<int>> _byOutput = new Dictionary<int, List<int>>();

        // Index: Ingredient item type -> List of recipe indices that use it
        private Dictionary<int, List<int>> _byIngredient = new Dictionary<int, List<int>>();

        // Index: Required tile type -> List of recipe indices that require it
        private Dictionary<int, List<int>> _byStation = new Dictionary<int, List<int>>();

        // Reverse lookup: OriginalIndex (Terraria recipe array index) -> internal _allRecipes index
        private Dictionary<int, int> _originalToInternal = new Dictionary<int, int>();

        private static int _fakeItemIdOffset;
        private static bool _recipeGroupsAvailable;

        public int TotalRecipes => _allRecipes.Count;

        /// <summary>
        /// The fake item ID offset used by recipe groups (1000000).
        /// IDs >= this value are recipe group fake IDs, not real item IDs.
        /// </summary>
        public int FakeItemIdOffset => _fakeItemIdOffset > 0 ? _fakeItemIdOffset : 1000000;

        /// <summary>Whether recipe groups are available.</summary>
        public bool HasRecipeGroupSupport => _recipeGroupsAvailable;

        public RecipeIndex(ILogger log)
        {
            _log = log;
        }

        /// <summary>
        /// Initialize recipe index support. Always succeeds with direct type access.
        /// </summary>
        public bool InitReflection()
        {
            try
            {
                _fakeItemIdOffset = RecipeGroup.FakeItemIdOffset;
                _recipeGroupsAvailable = true;
                _log.Info($"RecipeIndex: Recipe group support enabled (FakeItemIdOffset={_fakeItemIdOffset})");
                return true;
            }
            catch (Exception ex)
            {
                _log.Error($"RecipeIndex init error: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Build the recipe index from Terraria's recipe array.
        /// Call once at world load.
        /// </summary>
        public void Build()
        {
            _allRecipes.Clear();
            _byOutput.Clear();
            _byIngredient.Clear();
            _byStation.Clear();
            _originalToInternal.Clear();

            try
            {
                var recipeArray = Main.recipe;
                int numRecipes = Recipe.numRecipes;

                if (recipeArray == null)
                {
                    _log.Error("Cannot build recipe index: recipe array is null");
                    return;
                }

                if (numRecipes < 0 || numRecipes > recipeArray.Length)
                {
                    _log.Error($"Invalid numRecipes: {numRecipes} (array length: {recipeArray.Length})");
                    return;
                }

                _log.Info($"Building recipe index from {numRecipes} recipes...");

                for (int i = 0; i < numRecipes; i++)
                {
                    var recipe = recipeArray[i];
                    if (recipe == null) continue;

                    var info = ExtractRecipeInfo(recipe, i);
                    if (info == null || info.OutputItemId <= 0) continue;

                    _allRecipes.Add(info);
                    int recipeIdx = _allRecipes.Count - 1;
                    _originalToInternal[info.OriginalIndex] = recipeIdx;

                    // Index by output
                    if (!_byOutput.TryGetValue(info.OutputItemId, out var outputList))
                    {
                        outputList = new List<int>();
                        _byOutput[info.OutputItemId] = outputList;
                    }
                    outputList.Add(recipeIdx);

                    // Index by ingredient
                    foreach (var ing in info.Ingredients)
                    {
                        if (!_byIngredient.TryGetValue(ing.ItemId, out var ingList))
                        {
                            ingList = new List<int>();
                            _byIngredient[ing.ItemId] = ingList;
                        }
                        ingList.Add(recipeIdx);

                        // For recipe groups, also index by each valid item ID
                        // so GetRecipesUsingIngredient(ironBarId) finds "Any Iron Bar" recipes
                        if (ing.IsRecipeGroup && ing.ValidItemIds != null)
                        {
                            foreach (int validId in ing.ValidItemIds)
                            {
                                if (!_byIngredient.TryGetValue(validId, out var validList))
                                {
                                    validList = new List<int>();
                                    _byIngredient[validId] = validList;
                                }
                                if (!validList.Contains(recipeIdx))
                                    validList.Add(recipeIdx);
                            }
                        }
                    }

                    // Index by station
                    foreach (var tile in info.RequiredTiles)
                    {
                        if (tile <= 0) continue;
                        if (!_byStation.TryGetValue(tile, out var stationList))
                        {
                            stationList = new List<int>();
                            _byStation[tile] = stationList;
                        }
                        stationList.Add(recipeIdx);
                    }
                }

                _log.Info($"Recipe index built: {_allRecipes.Count} recipes, {_byOutput.Count} unique outputs, {_byIngredient.Count} unique ingredients");
            }
            catch (Exception ex)
            {
                _log.Error($"Recipe index build error: {ex.Message}");
            }
        }

        private RecipeInfo ExtractRecipeInfo(Recipe recipe, int originalIndex)
        {
            try
            {
                var info = new RecipeInfo { OriginalIndex = originalIndex };

                // Get output item
                var createItem = recipe.createItem;
                if (createItem != null)
                {
                    info.OutputItemId = createItem.type;
                    info.OutputStack = createItem.stack;
                    info.OutputName = createItem.Name ?? "";
                }

                // Get ingredients — prefer requiredItemQuickLookup (has recipe group info)
                bool usedQuickLookup = false;
                if (_recipeGroupsAvailable)
                {
                    usedQuickLookup = ExtractIngredientsFromQuickLookup(recipe, info);
                }

                if (!usedQuickLookup)
                {
                    // Fallback: use requiredItem[] (no recipe group support)
                    var requiredItems = recipe.requiredItem;
                    if (requiredItems != null)
                    {
                        foreach (var item in requiredItems)
                        {
                            if (item == null) continue;
                            if (item.type <= 0) break; // Empty slot marks end of ingredients

                            info.Ingredients.Add(new IngredientInfo
                            {
                                ItemId = item.type,
                                RequiredStack = item.stack,
                                Name = item.Name ?? ""
                            });
                        }
                    }
                }

                // Get required tile (single int in 1.4.5, -1 = none)
                int tileId = recipe.requiredTile;
                if (tileId >= 0)
                {
                    info.RequiredTiles.Add(tileId);
                }

                // Get environmental requirements
                info.NeedWater = recipe.needWater;
                info.NeedHoney = recipe.needHoney;
                info.NeedLava = recipe.needLava;
                info.NeedSnowBiome = recipe.needSnowBiome;
                info.NeedGraveyard = recipe.needGraveyardBiome;
                // Note: needShimmer does NOT exist on vanilla 1.4.5 Recipe class.
                // Shimmer transmutation is handled separately (not through Recipe objects).
                info.NeedShimmer = false;

                return info;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Extract ingredients from requiredItemQuickLookup, which contains recipe group info.
        /// </summary>
        private bool ExtractIngredientsFromQuickLookup(Recipe recipe, RecipeInfo info)
        {
            try
            {
                var quickLookup = recipe.requiredItemQuickLookup;
                if (quickLookup == null) return false;

                for (int i = 0; i < quickLookup.Length; i++)
                {
                    var entry = quickLookup[i];
                    int idOrGroup = entry.itemIdOrRecipeGroup;
                    int stack = entry.stack;
                    if (idOrGroup <= 0) break; // Empty slot marks end of ingredients

                    bool isGroup = idOrGroup >= _fakeItemIdOffset;
                    var ingredient = new IngredientInfo
                    {
                        ItemId = idOrGroup, // Fake group ID or real item ID
                        RequiredStack = stack,
                        IsRecipeGroup = isGroup
                    };

                    if (isGroup)
                    {
                        // Resolve group info
                        int groupId = idOrGroup - _fakeItemIdOffset;
                        ingredient.ValidItemIds = GetGroupValidItems(groupId);
                        string groupName = GetGroupDisplayName(groupId);
                        // Ensure "Any " prefix for recipe group display
                        if (!groupName.StartsWith("Any ", StringComparison.OrdinalIgnoreCase)
                            && !groupName.StartsWith("Group #"))
                            groupName = "Any " + groupName;
                        ingredient.Name = groupName;
                    }
                    else
                    {
                        // Normal item — get name from requiredItem array (same index)
                        ingredient.Name = GetItemNameFromRequiredItem(recipe, i);
                    }

                    info.Ingredients.Add(ingredient);
                }

                return info.Ingredients.Count > 0;
            }
            catch (Exception ex)
            {
                _log.Warn($"RecipeIndex: QuickLookup extraction failed: {ex.Message}");
                info.Ingredients.Clear();
                return false;
            }
        }

        /// <summary>
        /// Get the ValidItems HashSet for a recipe group by its registered ID.
        /// </summary>
        private HashSet<int> GetGroupValidItems(int groupId)
        {
            try
            {
                if (RecipeGroup.recipeGroups.TryGetValue(groupId, out var group))
                    return group.ValidItems;
                return null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Get display name for a recipe group (e.g., "Any Iron Bar").
        /// </summary>
        private string GetGroupDisplayName(int groupId)
        {
            try
            {
                if (RecipeGroup.recipeGroups.TryGetValue(groupId, out var group))
                {
                    string name = group.GetText?.Invoke();
                    if (!string.IsNullOrEmpty(name))
                        return name;
                }
                return $"Group #{groupId}";
            }
            catch
            {
                return $"Group #{groupId}";
            }
        }

        /// <summary>
        /// Get item name from the requiredItem array at a given index (for non-group ingredients).
        /// </summary>
        private string GetItemNameFromRequiredItem(Recipe recipe, int index)
        {
            try
            {
                var requiredItems = recipe.requiredItem;
                if (requiredItems == null || index >= requiredItems.Length) return "";

                var item = requiredItems[index];
                if (item == null) return "";

                return item.Name ?? "";
            }
            catch
            {
                return "";
            }
        }

        /// <summary>
        /// Get all recipes.
        /// </summary>
        public IReadOnlyList<RecipeInfo> GetAllRecipes() => _allRecipes;

        /// <summary>
        /// Get recipe by internal index (used for sub-recipe lookups from GetRecipesByOutput).
        /// </summary>
        public RecipeInfo GetRecipe(int index)
        {
            if (index < 0 || index >= _allRecipes.Count) return null;
            return _allRecipes[index];
        }

        /// <summary>
        /// Get recipe by its OriginalIndex (Terraria recipe array index).
        /// Use this when looking up a recipe from CraftabilityResult.Recipe.OriginalIndex.
        /// </summary>
        public RecipeInfo GetRecipeByOriginalIndex(int originalIndex)
        {
            if (_originalToInternal.TryGetValue(originalIndex, out int internalIndex))
                return GetRecipe(internalIndex);
            return null;
        }

        /// <summary>
        /// Get recipes that output a specific item.
        /// </summary>
        public IReadOnlyList<int> GetRecipesByOutput(int itemId)
        {
            if (_byOutput.TryGetValue(itemId, out var list))
                return list;
            return Array.Empty<int>();
        }

        /// <summary>
        /// Get recipes that use a specific ingredient.
        /// </summary>
        public IReadOnlyList<int> GetRecipesUsingIngredient(int itemId)
        {
            if (_byIngredient.TryGetValue(itemId, out var list))
                return list;
            return Array.Empty<int>();
        }

        /// <summary>
        /// Get recipes that require a specific station.
        /// </summary>
        public IReadOnlyList<int> GetRecipesByStation(int tileId)
        {
            if (_byStation.TryGetValue(tileId, out var list))
                return list;
            return Array.Empty<int>();
        }

        /// <summary>
        /// Check if any recipe can create the given item.
        /// </summary>
        public bool HasRecipeFor(int itemId) => _byOutput.ContainsKey(itemId);
    }

    /// <summary>
    /// Cached information about a recipe.
    /// </summary>
    public class RecipeInfo
    {
        public int OriginalIndex { get; set; }
        public int OutputItemId { get; set; }
        public int OutputStack { get; set; }
        public string OutputName { get; set; }

        public List<IngredientInfo> Ingredients { get; } = new List<IngredientInfo>();
        public List<int> RequiredTiles { get; } = new List<int>();

        // Environmental requirements
        public bool NeedWater { get; set; }
        public bool NeedHoney { get; set; }
        public bool NeedLava { get; set; }
        public bool NeedSnowBiome { get; set; }
        public bool NeedGraveyard { get; set; }
        public bool NeedShimmer { get; set; }

        /// <summary>
        /// Check if this recipe has any environmental requirements.
        /// </summary>
        public bool HasEnvironmentalRequirements =>
            NeedWater || NeedHoney || NeedLava || NeedSnowBiome || NeedGraveyard || NeedShimmer;
    }

    /// <summary>
    /// Cached information about a recipe ingredient.
    /// ItemId is the real item ID for normal ingredients, or the fake group ID for recipe groups.
    /// </summary>
    public class IngredientInfo
    {
        public int ItemId { get; set; }
        public int RequiredStack { get; set; }
        public string Name { get; set; }

        /// <summary>True if this ingredient accepts any item from a recipe group (e.g., "any iron bar").</summary>
        public bool IsRecipeGroup { get; set; }

        /// <summary>For recipe groups: the set of real item IDs that satisfy this ingredient.</summary>
        public HashSet<int> ValidItemIds { get; set; }
    }
}
