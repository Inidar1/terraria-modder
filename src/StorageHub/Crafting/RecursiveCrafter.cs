using System;
using System.Collections.Generic;
using TerrariaModder.Core.Logging;

namespace StorageHub.Crafting
{
    /// <summary>
    /// Calculates multi-step crafting trees.
    /// Given a target item, figures out all sub-recipes needed to craft it
    /// from raw materials.
    ///
    /// Design decision: Always available (not tier-gated)
    /// "Recursive crafting is THE main convenience feature. The time investment
    /// is in gathering materials, not clicking through sub-recipes."
    /// </summary>
    public class RecursiveCrafter
    {
        private readonly ILogger _log;
        private readonly RecipeIndex _recipeIndex;
        private readonly CraftabilityChecker _checker;
        private CraftingExecutor _executor;

        // Safety cap to prevent infinite loops even with depth=0 (unlimited)
        private const int SafetyMaxDepth = 10;

        // Effective max depth for the current plan calculation
        private int _currentMaxDepth = SafetyMaxDepth;

        public RecursiveCrafter(ILogger log, RecipeIndex recipeIndex, CraftabilityChecker checker)
        {
            _log = log;
            _recipeIndex = recipeIndex;
            _checker = checker;
        }

        /// <summary>
        /// Set the executor for plan execution.
        /// (Circular dependency resolved via setter)
        /// </summary>
        public void SetExecutor(CraftingExecutor executor)
        {
            _executor = executor;
        }

        /// <summary>
        /// Calculate the crafting tree for a recipe.
        /// Returns ordered list of craft steps from raw materials to final product.
        /// </summary>
        /// <param name="targetOriginalIndex">The Terraria recipe array index (OriginalIndex) of the target recipe.</param>
        /// <param name="targetCount">Number of final items to craft.</param>
        /// <param name="maxDepth">Max recursion depth. 0 = unlimited (up to safety cap).</param>
        /// <returns>Ordered list of crafting steps, or null if impossible.</returns>
        public CraftingPlan CalculatePlan(int targetOriginalIndex, int targetCount = 1, int maxDepth = 0)
        {
            // Set effective depth: 0 = unlimited (safety cap), 1-5 = user-configured
            _currentMaxDepth = (maxDepth > 0) ? Math.Min(maxDepth, SafetyMaxDepth) : SafetyMaxDepth;

            var targetRecipe = _recipeIndex.GetRecipeByOriginalIndex(targetOriginalIndex);
            if (targetRecipe == null)
            {
                _log.Warn($"RecursiveCrafter: No recipe found for OriginalIndex={targetOriginalIndex}");
                return null;
            }

            var plan = new CraftingPlan
            {
                TargetRecipe = targetRecipe,
                TargetCount = targetCount
            };

            // Track what we're building and what we need
            var visited = new HashSet<int>(); // itemId -> being calculated (for cycle detection)
            var steps = new List<CraftStep>();
            var rawMaterialsNeeded = new Dictionary<int, int>();
            // Track materials already allocated to earlier steps to prevent double-counting.
            // Each ingredient subtracts from the real available count so sibling ingredients
            // and sibling recipes see an accurate remaining quantity.
            var claimedMaterials = new Dictionary<int, int>();

            // Calculate recursively
            bool success = CalculateStepsFor(targetRecipe, targetCount, 0, visited, steps, rawMaterialsNeeded, claimedMaterials);

            if (!success)
            {
                plan.CanCraft = false;
                plan.ErrorMessage = "Missing non-craftable materials";
            }
            else
            {
                plan.CanCraft = true;

                // Deduplicate: diamond dependencies cause the same recipe to appear
                // multiple times (e.g., two branches both needing Stardust Fragment).
                // Merge steps that use the same recipe, keep the highest depth.
                var mergedSteps = new List<CraftStep>();
                var recipeToPos = new Dictionary<int, int>();
                foreach (var step in steps)
                {
                    int idx = step.Recipe.OriginalIndex;
                    if (recipeToPos.TryGetValue(idx, out int pos))
                    {
                        mergedSteps[pos].CraftCount += step.CraftCount;
                        long mergedOutput = (long)mergedSteps[pos].OutputCount + step.OutputCount;
                        mergedSteps[pos].OutputCount = mergedOutput > int.MaxValue ? int.MaxValue : (int)mergedOutput;
                        if (step.Depth > mergedSteps[pos].Depth)
                            mergedSteps[pos].Depth = step.Depth;
                    }
                    else
                    {
                        recipeToPos[idx] = mergedSteps.Count;
                        mergedSteps.Add(step);
                    }
                }

                plan.Steps = mergedSteps;
                plan.RawMaterialsNeeded = rawMaterialsNeeded;

                // Check if we have all raw materials
                plan.MissingRawMaterials = new Dictionary<int, int>();
                foreach (var kvp in rawMaterialsNeeded)
                {
                    int have = _checker.GetMaterialCount(kvp.Key);
                    if (have < kvp.Value)
                    {
                        plan.MissingRawMaterials[kvp.Key] = kvp.Value - have;
                        plan.CanCraft = false;
                    }
                }

                // PHASE 2: Virtual pool validation — simulate execution as a safety net.
                // The claimedMaterials dict handles the primary double-counting prevention
                // during planning, but this second pass catches any edge cases (e.g., merged
                // steps from diamond dependencies) by simulating actual execution order.
                if (plan.CanCraft)
                {
                    ValidateWithVirtualPool(plan);
                }
            }

            return plan;
        }

        private bool CalculateStepsFor(
            RecipeInfo recipe,
            int count,
            int depth,
            HashSet<int> visited,
            List<CraftStep> steps,
            Dictionary<int, int> rawMaterialsNeeded,
            Dictionary<int, int> claimedMaterials)
        {
            if (depth > _currentMaxDepth)
            {
                _log.Warn($"RecursiveCrafter: Max depth {_currentMaxDepth} exceeded for {recipe.OutputName}");
                return false;
            }

            // How many times do we need to run this recipe?
            if (recipe.OutputStack <= 0)
            {
                _log.Warn($"RecursiveCrafter: Recipe for {recipe.OutputName} has OutputStack={recipe.OutputStack}, skipping");
                return false;
            }
            int craftTimes = (count + recipe.OutputStack - 1) / recipe.OutputStack;

            // Process each ingredient
            foreach (var ing in recipe.Ingredients)
            {
                // Use long to prevent overflow
                long totalNeededLong = (long)ing.RequiredStack * craftTimes;
                int totalNeeded = totalNeededLong > int.MaxValue ? int.MaxValue : (int)totalNeededLong;

                // Check how many we have, minus what earlier steps already claimed
                int rawHave = _checker.GetMaterialCount(ing.ItemId);
                int alreadyClaimed = GetClaimed(claimedMaterials, ing.ItemId);
                int have = Math.Max(0, rawHave - alreadyClaimed);

                // If we're already calculating this item, we have a cycle
                if (visited.Contains(ing.ItemId))
                {
                    // Use what we have, need the rest as raw material
                    AddToDict(rawMaterialsNeeded, ing.ItemId, totalNeeded);
                    // Claim what we're consuming from available stock
                    int fromStock = Math.Min(have, totalNeeded);
                    if (fromStock > 0)
                        AddToDict(claimedMaterials, ing.ItemId, fromStock);
                    continue;
                }

                // Check if this ingredient can be crafted
                // For recipe groups, collect sub-recipes from ALL valid item IDs in the group
                IReadOnlyList<int> subRecipes;
                if (ing.IsRecipeGroup && ing.ValidItemIds != null)
                {
                    var groupSubRecipes = new List<int>();
                    foreach (int validId in ing.ValidItemIds)
                    {
                        var recipes = _recipeIndex.GetRecipesByOutput(validId);
                        foreach (int r in recipes)
                        {
                            if (!groupSubRecipes.Contains(r))
                                groupSubRecipes.Add(r);
                        }
                    }
                    subRecipes = groupSubRecipes;
                }
                else
                {
                    subRecipes = _recipeIndex.GetRecipesByOutput(ing.ItemId);
                }

                if (subRecipes.Count == 0)
                {
                    // Cannot be crafted - this is a raw material
                    AddToDict(rawMaterialsNeeded, ing.ItemId, totalNeeded);
                    // Claim the materials we'll consume from storage
                    int fromStock = Math.Min(have, totalNeeded);
                    if (fromStock > 0)
                        AddToDict(claimedMaterials, ing.ItemId, fromStock);
                }
                else
                {
                    // Can be crafted - find the best recipe
                    // For simplicity, use the first craftable recipe
                    bool foundCraftable = false;

                    foreach (int subRecipeIdx in subRecipes)
                    {
                        var subRecipe = _recipeIndex.GetRecipe(subRecipeIdx);
                        if (subRecipe == null) continue;

                        // Check if this sub-recipe's stations/environment are available
                        // Must check MissingStations/MissingEnvironment lists directly,
                        // NOT the Status enum — Status prioritizes MissingMaterials over
                        // MissingStation, so a recipe missing BOTH materials AND station
                        // gets Status=MissingMaterials. We'd then try to recursively
                        // resolve materials while the station is unavailable.
                        var subResult = _checker.CanCraft(subRecipe);
                        if ((subResult.MissingStations != null && subResult.MissingStations.Count > 0) ||
                            (subResult.MissingEnvironment != null && subResult.MissingEnvironment.Count > 0))
                        {
                            continue; // Can't use this recipe — station/environment unavailable
                        }

                        // Calculate how many we actually need to craft (accounting for claims)
                        int amountToCraft = totalNeeded - have;

                        // Claim the materials we're consuming from available stock
                        int fromStock = Math.Min(have, totalNeeded);
                        if (fromStock > 0)
                            AddToDict(claimedMaterials, ing.ItemId, fromStock);

                        // Skip if we already have enough (no crafting needed)
                        if (amountToCraft <= 0)
                        {
                            foundCraftable = true;
                            break;
                        }

                        // Mark as being visited
                        visited.Add(ing.ItemId);

                        // Recursively calculate — pass claimedMaterials so sub-recipes
                        // see what's already been allocated
                        bool subSuccess = CalculateStepsFor(subRecipe, amountToCraft, depth + 1, visited, steps, rawMaterialsNeeded, claimedMaterials);

                        visited.Remove(ing.ItemId);

                        if (subSuccess)
                        {
                            // Sub-recipe step was already added by the recursive call
                            foundCraftable = true;
                            break;
                        }
                        else
                        {
                            // Sub-recipe failed — unclaim the stock we optimistically claimed
                            if (fromStock > 0)
                                SubtractFromDict(claimedMaterials, ing.ItemId, fromStock);
                        }
                    }

                    if (!foundCraftable)
                    {
                        // Couldn't craft it, need as raw material
                        int shortfall = totalNeeded - have;
                        AddToDict(rawMaterialsNeeded, ing.ItemId, shortfall);
                        // Claim what we do have from storage
                        int fromStock = Math.Min(have, totalNeeded);
                        if (fromStock > 0)
                            AddToDict(claimedMaterials, ing.ItemId, fromStock);
                    }
                }
            }

            // Add the main recipe step
            // Use long to prevent overflow on OutputCount
            long mainOutputLong = (long)craftTimes * recipe.OutputStack;
            steps.Add(new CraftStep
            {
                Recipe = recipe,
                CraftCount = craftTimes,
                OutputCount = mainOutputLong > int.MaxValue ? int.MaxValue : (int)mainOutputLong,
                Depth = depth
            });

            return true;
        }

        private void AddToDict(Dictionary<int, int> dict, int key, int value)
        {
            if (value <= 0) return;
            if (dict.ContainsKey(key))
            {
                // Use checked to detect overflow
                try
                {
                    dict[key] = checked(dict[key] + value);
                }
                catch (OverflowException)
                {
                    dict[key] = int.MaxValue;
                }
            }
            else
                dict[key] = value;
        }

        /// <summary>
        /// Subtract a value from a dictionary entry. Clamps to 0 (never goes negative).
        /// Used to "unclaim" materials when a sub-recipe attempt fails.
        /// </summary>
        private void SubtractFromDict(Dictionary<int, int> dict, int key, int value)
        {
            if (value <= 0) return;
            if (dict.TryGetValue(key, out int current))
            {
                int newVal = current - value;
                if (newVal <= 0)
                    dict.Remove(key);
                else
                    dict[key] = newVal;
            }
        }

        /// <summary>
        /// Get the currently claimed amount for a material, or 0 if not yet claimed.
        /// </summary>
        private static int GetClaimed(Dictionary<int, int> claimedMaterials, int itemId)
        {
            return claimedMaterials.TryGetValue(itemId, out int claimed) ? claimed : 0;
        }

        /// <summary>
        /// Validate a crafting plan by simulating execution with a virtual material pool.
        /// Catches double-counting: each step deducts ingredients and adds outputs.
        /// Sets plan.CanCraft = false if any step would fail.
        /// </summary>
        private void ValidateWithVirtualPool(CraftingPlan plan)
        {
            // Build virtual pool from current material snapshot
            var pool = _checker.GetAllMaterialCounts();

            // Sort steps by depth descending (deepest sub-recipes execute first)
            var sortedSteps = new List<CraftStep>(plan.Steps);
            sortedSteps.Sort((a, b) => b.Depth.CompareTo(a.Depth));

            foreach (var step in sortedSteps)
            {
                int craftTimes = step.CraftCount;

                // Deduct ingredients from pool
                foreach (var ing in step.Recipe.Ingredients)
                {
                    long neededLong = (long)ing.RequiredStack * craftTimes;
                    int needed = neededLong > int.MaxValue ? int.MaxValue : (int)neededLong;

                    if (ing.IsRecipeGroup && ing.ValidItemIds != null)
                    {
                        // For recipe groups, consume from valid items in pool
                        int remaining = needed;
                        foreach (int validId in ing.ValidItemIds)
                        {
                            if (remaining <= 0) break;
                            if (pool.TryGetValue(validId, out int have) && have > 0)
                            {
                                int take = Math.Min(have, remaining);
                                pool[validId] = have - take;
                                remaining -= take;
                            }
                        }
                        // Also deduct from the fake group count
                        if (pool.TryGetValue(ing.ItemId, out int groupCount))
                        {
                            pool[ing.ItemId] = Math.Max(0, groupCount - needed);
                        }

                        if (remaining > 0)
                        {
                            plan.CanCraft = false;
                            plan.ErrorMessage = $"Virtual pool: insufficient {ing.Name} (need {needed}, short by {remaining})";
                            _log.Debug($"RecursiveCrafter pool validation: {ing.Name} short by {remaining} for {step.Recipe.OutputName}");
                            return;
                        }
                    }
                    else
                    {
                        // Normal ingredient — exact ID match
                        int have = pool.TryGetValue(ing.ItemId, out int h) ? h : 0;
                        if (have < needed)
                        {
                            plan.CanCraft = false;
                            plan.ErrorMessage = $"Virtual pool: insufficient {ing.Name} (need {needed}, have {have})";
                            _log.Debug($"RecursiveCrafter pool validation: {ing.Name} need {needed} have {have} for {step.Recipe.OutputName}");
                            return;
                        }
                        pool[ing.ItemId] = have - needed;
                    }
                }

                // Add outputs to pool
                long outputLong = (long)step.Recipe.OutputStack * craftTimes;
                int outputCount = outputLong > int.MaxValue ? int.MaxValue : (int)outputLong;
                int outputId = step.Recipe.OutputItemId;

                if (pool.TryGetValue(outputId, out int existing))
                {
                    long newCount = (long)existing + outputCount;
                    pool[outputId] = newCount > int.MaxValue ? int.MaxValue : (int)newCount;
                }
                else
                {
                    pool[outputId] = outputCount;
                }
            }
        }

        /// <summary>
        /// Execute a crafting plan.
        /// </summary>
        /// <param name="plan">The plan to execute.</param>
        /// <returns>True if successful.</returns>
        public bool ExecutePlan(CraftingPlan plan)
        {
            if (!plan.CanCraft)
            {
                _log.Warn("Cannot execute plan: not craftable");
                return false;
            }

            if (_executor == null)
            {
                _log.Error("Cannot execute plan: no executor set");
                return false;
            }

            // Execute each step in order (sub-recipes first, then final product)
            // Steps are already ordered: deepest sub-recipes first, final product last
            int completedSteps = 0;
            for (int i = 0; i < plan.Steps.Count; i++)
            {
                var step = plan.Steps[i];
                bool isLastStep = (i == plan.Steps.Count - 1);

                // Refresh material counts before each step (intermediate products now available)
                _checker.MarkDirty();

                // Intermediate steps place output directly in inventory so the next step
                // can immediately consume them. Final step uses QuickSpawnItem (normal behavior).
                bool success = _executor.ExecuteCraft(step.Recipe, step.CraftCount,
                    directToInventory: !isLastStep);
                if (!success)
                {
                    _log.Error($"Crafting plan failed at step {completedSteps + 1}: {step.Recipe.OutputName}");
                    return false;
                }

                completedSteps++;
            }

            _log.Info($"Crafted {plan.TargetCount}x {plan.TargetRecipe.OutputName} ({plan.Steps.Count} steps)");
            return true;
        }
    }

    /// <summary>
    /// A complete crafting plan from raw materials to final product.
    /// </summary>
    public class CraftingPlan
    {
        public RecipeInfo TargetRecipe { get; set; }
        public int TargetCount { get; set; }
        public bool CanCraft { get; set; }
        public string ErrorMessage { get; set; }

        /// <summary>
        /// Ordered list of craft steps (execute in order).
        /// </summary>
        public List<CraftStep> Steps { get; set; } = new List<CraftStep>();

        /// <summary>
        /// Raw materials needed (items that cannot be crafted).
        /// </summary>
        public Dictionary<int, int> RawMaterialsNeeded { get; set; } = new Dictionary<int, int>();

        /// <summary>
        /// Raw materials we don't have enough of.
        /// </summary>
        public Dictionary<int, int> MissingRawMaterials { get; set; } = new Dictionary<int, int>();
    }

    /// <summary>
    /// A single step in a crafting plan.
    /// </summary>
    public class CraftStep
    {
        public RecipeInfo Recipe { get; set; }
        public int CraftCount { get; set; }
        public int OutputCount { get; set; }
        public int Depth { get; set; } // 0 = final product, higher = sub-recipes
    }
}
