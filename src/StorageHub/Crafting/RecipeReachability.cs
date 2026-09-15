using System.Collections.Generic;

namespace StorageHub.Crafting
{
    /// <summary>Conservative item-type reachability; quantities and execution remain the planner's responsibility.</summary>
    internal static class RecipeReachability
    {
        private sealed class PendingRecipe
        {
            internal int Output;
            internal int Missing;
        }

        private sealed class Requirement
        {
            internal PendingRecipe Recipe;
            internal bool Satisfied;
        }

        internal static HashSet<int> Build(IEnumerable<RecipeInfo> recipes, IReadOnlyDictionary<int, int> counts)
        {
            var available = new HashSet<int>();
            foreach (var stock in counts)
                if (stock.Key > 0 && stock.Value > 0) available.Add(stock.Key);

            var waiting = new Dictionary<int, List<Requirement>>();
            var ready = new Queue<int>();
            foreach (var recipe in recipes)
            {
                if (recipe.OutputItemId <= 0 || recipe.OutputStack <= 0) continue;
                var pending = new PendingRecipe { Output = recipe.OutputItemId };
                foreach (var ingredient in recipe.Ingredients)
                {
                    if (ingredient.RequiredStack <= 0 || Matches(ingredient, available)) continue;
                    var requirement = new Requirement { Recipe = pending };
                    pending.Missing++;
                    if (ingredient.IsRecipeGroup)
                    {
                        if (ingredient.ValidItemIds != null)
                            foreach (int type in ingredient.ValidItemIds) Subscribe(type, requirement, waiting);
                    }
                    else Subscribe(ingredient.ItemId, requirement, waiting);
                }
                if (pending.Missing == 0) ready.Enqueue(pending.Output);
            }

            // Each requirement is satisfied once, even when several group members
            // become reachable. Unseeded conversion cycles cannot create items.
            while (ready.Count > 0)
            {
                int output = ready.Dequeue();
                if (!available.Add(output) || !waiting.TryGetValue(output, out var requirements)) continue;
                foreach (var requirement in requirements)
                {
                    if (requirement.Satisfied) continue;
                    requirement.Satisfied = true;
                    if (--requirement.Recipe.Missing == 0) ready.Enqueue(requirement.Recipe.Output);
                }
            }
            return available;
        }

        internal static bool CanSupply(RecipeInfo recipe, HashSet<int> available)
        {
            foreach (var ingredient in recipe.Ingredients)
                if (ingredient.RequiredStack > 0 && !Matches(ingredient, available)) return false;
            return true;
        }

        private static bool Matches(IngredientInfo ingredient, HashSet<int> available)
        {
            if (!ingredient.IsRecipeGroup) return available.Contains(ingredient.ItemId);
            if (ingredient.ValidItemIds != null)
                foreach (int type in ingredient.ValidItemIds)
                    if (available.Contains(type)) return true;
            return false;
        }

        private static void Subscribe(int type, Requirement requirement, Dictionary<int, List<Requirement>> waiting)
        {
            if (type <= 0) return;
            if (!waiting.TryGetValue(type, out var requirements))
                waiting.Add(type, requirements = new List<Requirement>());
            requirements.Add(requirement);
        }
    }
}
