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

        // Original recipe outputs (for reverting) — stores (type, stack) tuple
        private Dictionary<int, (int type, int stack)> _originalOutputs = new Dictionary<int, (int type, int stack)>();

        public override void BuildShuffleMap()
        {
            Instance = this;

            try
            {
                int numRecipes = Recipe.numRecipes;
                var recipes = Main.recipe;
                if (recipes == null) return;

                // Revert to originals before re-shuffling (if previously shuffled)
                if (_originalOutputs.Count > 0)
                {
                    foreach (var kvp in _originalOutputs)
                    {
                        var r = recipes[kvp.Key];
                        if (r?.createItem != null)
                        {
                            r.createItem.SetDefaults(kvp.Value.type);
                            r.createItem.stack = kvp.Value.stack;
                        }
                    }
                }

                // Record original outputs only on the first call
                if (_originalOutputs.Count == 0)
                {
                    for (int i = 0; i < numRecipes; i++)
                    {
                        var recipe = recipes[i];
                        if (recipe?.createItem == null) continue;
                        int itemType = recipe.createItem.type;
                        int itemStack = recipe.createItem.stack;
                        if (itemType > 0)
                            _originalOutputs[i] = (itemType, itemStack);
                    }
                }

                // Build pool from original types
                var pool = new List<int>();
                foreach (var kvp in _originalOutputs)
                {
                    if (!pool.Contains(kvp.Value.type))
                        pool.Add(kvp.Value.type);
                }

                ShuffleMap = Seed.BuildShuffleMap(pool, Id);

                // Apply shuffle to recipes using original types as source
                int changed = 0;
                foreach (var kvp in _originalOutputs)
                {
                    var recipe = recipes[kvp.Key];
                    if (recipe?.createItem == null) continue;

                    int origType = kvp.Value.type;
                    if (ShuffleMap.TryGetValue(origType, out int newType) && newType != origType)
                    {
                        int originalStack = recipe.createItem.stack;
                        recipe.createItem.SetDefaults(newType);
                        recipe.createItem.stack = originalStack;
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
                    createItem.SetDefaults(kvp.Value.type);
                    createItem.stack = kvp.Value.stack;
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
