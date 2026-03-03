using System;
using System.Collections.Generic;
using HarmonyLib;
using Terraria;

namespace Randomizer.Modules
{
    /// <summary>
    /// Shuffles recipe outputs so crafting item X produces item Y instead.
    /// </summary>
    public class RecipeModule : ModuleBase
    {
        public override string Id => "recipes";
        public override string Name => "Recipe Shuffle";
        public override string Description => "Crafting recipes produce different items";
        public override string Tooltip => "Recipe outputs are shuffled. Crafting item X gives you item Y instead. Ingredients are unchanged. Same seed = consistent results.";

        internal static RecipeModule Instance;

        // Original recipe outputs (for reverting)
        private Dictionary<int, int> _originalOutputs = new Dictionary<int, int>();

        public override void BuildShuffleMap()
        {
            Instance = this;

            try
            {
                int numRecipes = Recipe.numRecipes;
                var recipes = Main.recipe;
                if (recipes == null) return;

                // Collect all recipe output item types
                var pool = new List<int>();
                _originalOutputs.Clear();

                for (int i = 0; i < numRecipes; i++)
                {
                    var recipe = recipes[i];
                    if (recipe == null) continue;

                    var createItem = recipe.createItem;
                    if (createItem == null) continue;

                    int itemType = createItem.type;
                    if (itemType > 0)
                    {
                        _originalOutputs[i] = itemType;
                        if (!pool.Contains(itemType))
                            pool.Add(itemType);
                    }
                }

                ShuffleMap = Seed.BuildShuffleMap(pool, Id);

                // Apply shuffle to recipes
                int changed = 0;
                for (int i = 0; i < numRecipes; i++)
                {
                    var recipe = recipes[i];
                    if (recipe == null) continue;

                    var createItem = recipe.createItem;
                    if (createItem == null) continue;

                    int itemType = createItem.type;
                    if (itemType > 0 && ShuffleMap.TryGetValue(itemType, out int newType) && newType != itemType)
                    {
                        createItem.SetDefaults(newType);
                        changed++;
                    }
                }

                // Refresh the recipe list UI
                Recipe.UpdateRecipeList();

                Log.Info($"[Randomizer] Recipes: shuffled {changed}/{numRecipes} recipe outputs");
            }
            catch (Exception ex)
            {
                Log.Error($"[Randomizer] Recipe shuffle error: {ex.Message}");
            }
        }

        public override void ApplyPatches(Harmony harmony)
        {
            // Recipe shuffle is applied directly to the recipe array, no runtime patches needed
        }

        public override void RemovePatches(Harmony harmony)
        {
            // Revert recipes to original outputs
            try
            {
                if (_originalOutputs.Count == 0) return;
                var recipes = Main.recipe;
                if (recipes == null) return;

                foreach (var kvp in _originalOutputs)
                {
                    var recipe = recipes[kvp.Key];
                    if (recipe == null) continue;
                    var createItem = recipe.createItem;
                    if (createItem == null) continue;
                    createItem.SetDefaults(kvp.Value);
                }
                Recipe.UpdateRecipeList();
                _originalOutputs.Clear();
            }
            catch (Exception ex)
            {
                Log.Error($"[Randomizer] Recipe revert error: {ex.Message}");
            }
        }
    }
}
