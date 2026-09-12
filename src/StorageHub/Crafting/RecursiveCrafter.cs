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

        internal sealed class PlanningContext
        {
            internal RecursivePlanSearch.State Initial { get; set; }
            internal Dictionary<int, CraftabilityResult> Availability { get; set; }
            internal string ErrorMessage { get; set; }
        }

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
        /// Forward hotbar protection setting to the underlying executor.
        /// </summary>
        public bool ProtectHotbar
        {
            get => _executor?.ProtectHotbar ?? false;
            set { if (_executor != null) _executor.ProtectHotbar = value; }
        }

        /// <summary>
        /// Calculate the crafting tree for a recipe.
        /// Returns ordered list of craft steps from raw materials to final product.
        /// </summary>
        /// <param name="targetOriginalIndex">The Terraria recipe array index (OriginalIndex) of the target recipe.</param>
        /// <param name="craftCount">Number of complete executions of the target recipe.</param>
        /// <param name="maxDepth">Max recursion depth. 0 = unlimited (up to safety cap).</param>
        /// <returns>Ordered list of crafting steps, or null if impossible.</returns>
        public CraftingPlan CalculateCraftPlan(int targetOriginalIndex, int craftCount = 1, int maxDepth = 0)
        {
            var recipe = _recipeIndex.GetRecipeByOriginalIndex(targetOriginalIndex);
            if (recipe == null) return null;
            long output = (long)recipe.OutputStack * craftCount;
            if (craftCount <= 0 || recipe.OutputStack <= 0 || output > int.MaxValue)
                return new CraftingPlan { TargetRecipe = recipe, CanCraft = false, ErrorMessage = "Invalid crafting quantity" };
            return CalculatePlanCore(targetOriginalIndex, (int)output, maxDepth, CreatePlanningContext());
        }

        internal CraftingPlan CalculateCraftPlan(int targetOriginalIndex, int craftCount, int maxDepth,
            PlanningContext context)
        {
            var recipe = _recipeIndex.GetRecipeByOriginalIndex(targetOriginalIndex);
            if (recipe == null) return null;
            long output = (long)recipe.OutputStack * craftCount;
            if (craftCount <= 0 || recipe.OutputStack <= 0 || output > int.MaxValue)
                return new CraftingPlan { TargetRecipe = recipe, CanCraft = false, ErrorMessage = "Invalid crafting quantity" };
            return CalculatePlanCore(targetOriginalIndex, (int)output, maxDepth, context);
        }

        /// <summary>
        /// Plan at least targetCount output items, rounding to complete recipe batches.
        /// Use CalculateCraftPlan when the caller specifies a number of crafts.
        /// </summary>
        public CraftingPlan CalculatePlan(int targetOriginalIndex, int targetCount = 1, int maxDepth = 0)
        {
            return CalculatePlanCore(targetOriginalIndex, targetCount, maxDepth, CreatePlanningContext());
        }

        internal PlanningContext CreatePlanningContext(IEnumerable<CraftabilityResult> availability = null)
        {
            var context = new PlanningContext
            {
                Initial = new RecursivePlanSearch.State(),
                Availability = new Dictionary<int, CraftabilityResult>()
            };
            if (availability != null)
            {
                foreach (var result in availability)
                {
                    if (result?.Recipe != null)
                        context.Availability[result.Recipe.OriginalIndex] = result;
                }
            }

            var locations = new HashSet<(int, int)>();
            foreach (var item in _checker.Materials.Read())
            {
                if (item.IsEmpty) continue;
                if (item.SourceSlot < 0 || !locations.Add((item.SourceChestIndex, item.SourceSlot)))
                {
                    context.ErrorMessage = "Invalid or duplicate storage location";
                    break;
                }
                if (ProtectHotbar && item.IsFromInventory && item.SourceSlot < 10) continue;
                context.Initial.Pool.TryGetValue(item.ItemId, out long quantity);
                context.Initial.Pool[item.ItemId] = quantity + item.Stack;
            }
            return context;
        }

        private CraftingPlan CalculatePlanCore(int targetOriginalIndex, int targetCount, int maxDepth,
            PlanningContext context)
        {
            // Set effective depth: 0 = unlimited (safety cap), 1-5 = user-configured
            int currentMaxDepth = (maxDepth > 0) ? Math.Min(maxDepth, SafetyMaxDepth) : SafetyMaxDepth;

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

            if (targetCount <= 0)
            {
                plan.ErrorMessage = "Invalid crafting quantity";
                return plan;
            }

            if (context == null || context.Initial == null)
            {
                plan.ErrorMessage = "Crafting material snapshot unavailable";
                return plan;
            }
            if (!string.IsNullOrEmpty(context.ErrorMessage))
            {
                plan.ErrorMessage = context.ErrorMessage;
                return plan;
            }

            if (targetRecipe.OutputStack <= 0 ||
                ((long)targetCount + targetRecipe.OutputStack - 1) / targetRecipe.OutputStack * targetRecipe.OutputStack > int.MaxValue)
            { plan.ErrorMessage = "Invalid crafting quantity"; return plan; }
            var search = new RecursivePlanSearch(_recipeIndex, _checker, currentMaxDepth, context.Availability);
            int crafts = (int)(((long)targetCount + targetRecipe.OutputStack - 1) / targetRecipe.OutputStack);
            var result = search.Find(targetRecipe, crafts, context.Initial);
            plan.SearchIncomplete = search.Limited && result == null;
            if (result == null)
            {
                plan.ErrorMessage = plan.SearchIncomplete ? "Recursive search limit reached" : "No feasible crafting plan";
                return plan;
            }
            // Initial stock needed by this exact sequence, allowing earlier output
            // to cover later consumption. Keep the existing int-valued public summary
            // truthful; an unrepresentable request is rejected, never saturated.
            var balance = new Dictionary<int, long>();
            foreach (var step in result.Steps)
            {
                foreach (var choice in step.SelectedMaterials)
                {
                    balance.TryGetValue(choice.Key, out long available);
                    long shortfall = Math.Max(0, choice.Value - available);
                    plan.RawMaterialsNeeded.TryGetValue(choice.Key, out int existing);
                    if (shortfall + existing > int.MaxValue)
                    { plan.ErrorMessage = "Crafting material quantity exceeds supported range"; return plan; }
                    if (shortfall > 0) plan.RawMaterialsNeeded[choice.Key] = existing + (int)shortfall;
                    balance[choice.Key] = Math.Max(0, available - choice.Value);
                }
                balance.TryGetValue(step.Recipe.OutputItemId, out long output);
                balance[step.Recipe.OutputItemId] = output + step.OutputCount;
            }
            plan.Steps = result.Steps;
            plan.CanCraft = true;
            return plan;
        }

        /// <summary>
        /// Return a quantity actually proved feasible. A bounded search may leave the
        /// true maximum unknown; callers must not label that lower bound as a maximum.
        /// </summary>
        public int CalculateCraftableLimit(int originalIndex, int maxDepth, out bool exact)
        {
            exact = false;
            var recipe = _recipeIndex.GetRecipeByOriginalIndex(originalIndex);
            if (recipe == null || recipe.OutputStack <= 0) return 0;
            int cap = int.MaxValue / recipe.OutputStack;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            int low = 0, high = 1;
            while (true)
            {
                var plan = CalculateCraftPlan(originalIndex, high, maxDepth);
                if (plan == null || !plan.CanCraft)
                {
                    if (plan?.SearchIncomplete == true) return low;
                    break;
                }
                low = high;
                if (low == cap) { exact = true; return low; }
                if (clock.ElapsedMilliseconds >= 75) return low;
                high = (int)Math.Min(cap, (long)high * 2);
            }
            while (high - low > 1)
            {
                if (clock.ElapsedMilliseconds >= 75) return low;
                int middle = low + (high - low) / 2;
                var plan = CalculateCraftPlan(originalIndex, middle, maxDepth);
                if (plan?.CanCraft == true) low = middle;
                else if (plan?.SearchIncomplete == true) return low;
                else high = middle;
            }
            exact = true;
            return low;
        }

        /// <summary>
        /// Execute a crafting plan.
        /// </summary>
        /// <param name="plan">The plan to execute.</param>
        /// <returns>True if successful.</returns>
        public bool ExecutePlan(CraftingPlan plan)
        {
            if (plan == null || _executor == null) return false;
            return _executor.ExecuteTransaction(() => ExecutePlanSteps(plan));
        }

        private bool ExecutePlanSteps(CraftingPlan plan)
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
            // Retain the planned dependency sequence; depth alone is not execution order.
            int completedSteps = 0;
            for (int i = 0; i < plan.Steps.Count; i++)
            {
                var step = plan.Steps[i];
                bool isLastStep = (i == plan.Steps.Count - 1);

                // Refresh material counts before each step (intermediate products now available)
                _checker.MarkDirty();

                // Intermediate steps place output directly in inventory so the next step
                // can immediately consume them. Final step uses QuickSpawnItem (normal behavior).
                bool success = step.SelectedMaterials == null
                    ? _executor.ExecuteCraft(step.Recipe, step.CraftCount, directToInventory: !isLastStep)
                    : _executor.ExecuteCraft(step.Recipe, step.CraftCount, step.SelectedMaterials, directToInventory: !isLastStep);
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
        /// <summary>Requested output item count, not the number of recipe executions.</summary>
        public int TargetCount { get; set; }
        public bool CanCraft { get; set; }
        public string ErrorMessage { get; set; }
        public bool SearchIncomplete { get; set; }

        /// <summary>
        /// Ordered list of craft steps (execute in order).
        /// </summary>
        public List<CraftStep> Steps { get; set; } = new List<CraftStep>();

        /// <summary>
        /// Initial real item quantities needed by the selected execution sequence.
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
        /// <summary>
        /// Exact real item quantities chosen for this step. Execution validates these
        /// against the original recipe and fresh eligible sources. Null supports older
        /// callers constructing plans without bound material choices.
        /// </summary>
        public IReadOnlyDictionary<int, long> SelectedMaterials { get; set; }
        public int Depth { get; set; } // 0 = final product, higher = sub-recipes
    }
}
