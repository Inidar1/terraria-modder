using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace StorageHub.Crafting
{
    // A branch owns its pool and steps. Continuations include later ingredients, so
    // a locally feasible recipe is not accepted until the complete request succeeds.
    internal sealed class RecursivePlanSearch
    {
        internal sealed class State
        {
            internal Dictionary<int, long> Pool = new Dictionary<int, long>();
            internal List<CraftStep> Steps = new List<CraftStep>();
            internal State Copy() => new State { Pool = new Dictionary<int, long>(Pool), Steps = new List<CraftStep>(Steps) };
        }
        private readonly RecipeIndex _index;
        private readonly CraftabilityChecker _checker;
        private readonly int _depth;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private int _work;
        private readonly Dictionary<int, CraftabilityResult> _availability;
        internal bool Limited { get; private set; }
        private bool _stopped;
        internal RecursivePlanSearch(RecipeIndex index, CraftabilityChecker checker, int depth,
            Dictionary<int, CraftabilityResult> availability = null)
        {
            _index = index;
            _checker = checker;
            _depth = depth;
            _availability = availability ?? new Dictionary<int, CraftabilityResult>();
        }

        private bool Visit()
        {
            if (_stopped) return false;
            if (++_work > 20000 || _clock.ElapsedMilliseconds > 50)
            { Limited = _stopped = true; return false; }
            return true;
        }
        internal State Find(RecipeInfo recipe, int crafts, State initial)
        {
            State found = null;
            Craft(recipe, crafts, 0, initial, new HashSet<int>(), state => { found = state; return true; });
            return found;
        }
        private bool Craft(RecipeInfo recipe, int crafts, int depth, State state, HashSet<int> path, Func<State, bool> next)
        {
            if (!Visit()) return false;
            if (depth > _depth || state.Steps.Count >= 128 || path.Contains(recipe.OriginalIndex))
            { Limited = true; return false; }
            long output = (long)recipe.OutputStack * crafts;
            if (crafts <= 0 || recipe.OutputItemId <= 0 || output <= 0 || output > int.MaxValue) return false;
            var availability = Availability(recipe);
            if (availability.MissingStations?.Count > 0 || availability.MissingEnvironment?.Count > 0) return false;
            var branchPath = new HashSet<int>(path) { recipe.OriginalIndex };
            return Ingredients(recipe, crafts, depth, 0, state, branchPath, new Dictionary<int, long>(), ready =>
            {
                var completed = ready.Copy();
                completed.Pool.TryGetValue(recipe.OutputItemId, out long existing);
                completed.Pool[recipe.OutputItemId] = existing + output;
                return next(completed);
            });
        }
        private bool Ingredients(RecipeInfo recipe, int crafts, int depth, int index, State state,
            HashSet<int> path, Dictionary<int, long> selected, Func<State, bool> next)
        {
            if (!Visit()) return false;
            if (index == recipe.Ingredients.Count)
            {
                var complete = state.Copy();
                complete.Steps.Add(new CraftStep { Recipe = recipe, CraftCount = crafts,
                    OutputCount = checked(recipe.OutputStack * crafts), Depth = depth,
                    SelectedMaterials = new Dictionary<int, long>(selected) });
                return next(complete);
            }
            var ingredient = recipe.Ingredients[index];
            long needed = ingredient == null ? 0 : (long)ingredient.RequiredStack * crafts;
            if (needed <= 0 || needed > int.MaxValue) return false;
            var ids = ingredient.IsRecipeGroup
                ? new List<int>(ingredient.ValidItemIds ?? new HashSet<int>()) : new List<int> { ingredient.ItemId };
            ids.Sort();
            return Supply(ids, needed, depth, state, path, new Dictionary<int, long>(), (ready, used) =>
            {
                var all = new Dictionary<int, long>(selected);
                foreach (var pair in used) { all.TryGetValue(pair.Key, out long previous); all[pair.Key] = previous + pair.Value; }
                return Ingredients(recipe, crafts, depth, index + 1, ready, path, all, next);
            });
        }
        private bool Supply(List<int> ids, long needed, int depth, State state, HashSet<int> path,
            Dictionary<int, long> used, Func<State, Dictionary<int, long>, bool> next)
            => Stock(ids, 0, needed, depth, state, path, used, next);

        private bool Stock(List<int> ids, int index, long needed, int depth, State state, HashSet<int> path,
            Dictionary<int, long> used, Func<State, Dictionary<int, long>, bool> next)
        {
            if (!Visit()) return false;
            if (needed == 0) return next(state, used);
            if (index == ids.Count) return Produce(ids, needed, depth, state, path, used, next);
            int id = ids[index];
            state.Pool.TryGetValue(id, out long have);
            for (long take = Math.Min(have, needed); take >= 0; take--)
            {
                if (!Visit()) return false;
                var branch = state.Copy();
                var choices = new Dictionary<int, long>(used);
                Take(branch, choices, id, take);
                if (Stock(ids, index + 1, needed - take, depth, branch, path, choices, next)) return true;
            }
            return false;
        }
        private bool Produce(List<int> ids, long needed, int depth, State state, HashSet<int> path,
            Dictionary<int, long> used, Func<State, Dictionary<int, long>, bool> next)
        {
            var candidates = new List<RecipeInfo>();
            var seen = new HashSet<int>();
            foreach (int id in ids)
            foreach (int index in _index.GetRecipesByOutput(id))
            {
                if (!Visit()) return false;
                var candidate = _index.GetRecipe(index);
                if (candidate != null && candidate.OutputStack > 0 && seen.Add(candidate.OriginalIndex)) candidates.Add(candidate);
            }
            // Prefer a recipe already supported by the snapshot over a detour through
            // conversions. This is ordering only: continuation failure still retries
            // every alternative within the explicit search budget.
            candidates.Sort((a, b) =>
            {
                int priority = (Availability(a).Status == CraftStatus.Craftable ? 0 : 1)
                    .CompareTo(Availability(b).Status == CraftStatus.Craftable ? 0 : 1);
                return priority != 0 ? priority : a.OriginalIndex.CompareTo(b.OriginalIndex);
            });
            foreach (var recipe in candidates)
            {
                if (!Visit()) return false;
                int id = recipe.OutputItemId;
                long batches = (needed + recipe.OutputStack - 1) / recipe.OutputStack;
                for (long batch = batches; batch > 0; batch--)
                {
                    if (!Visit()) return false;
                    long output = batch * recipe.OutputStack;
                    if (output > int.MaxValue) continue;
                    if (Craft(recipe, (int)batch, depth + 1, state, path, produced =>
                    {
                        var branch = produced.Copy();
                        var choices = new Dictionary<int, long>(used);
                        long take = Math.Min(needed, output);
                        Take(branch, choices, id, take);
                        return needed == take ? next(branch, choices)
                            : Supply(ids, needed - take, depth, branch, path, choices, next);
                    })) return true;
                }
            }
            return false;
        }
        private CraftabilityResult Availability(RecipeInfo recipe)
        {
            if (!_availability.TryGetValue(recipe.OriginalIndex, out var result))
            { result = _checker.CanCraft(recipe); _availability.Add(recipe.OriginalIndex, result); }
            return result;
        }
        private static void Take(State state, Dictionary<int, long> used, int id, long amount)
        {
            if (amount == 0) return;
            state.Pool[id] -= amount;
            used.TryGetValue(id, out long previous);
            used[id] = previous + amount;
        }
    }
}
