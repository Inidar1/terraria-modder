using System;
using System.Collections.Generic;
using TerrariaModder.Core.Logging;
using StorageHub.Storage;
using StorageHub.Config;
using StorageHub.Relay;

namespace StorageHub.Crafting
{
    /// <summary>
    /// Determines if a recipe can be crafted with available materials and stations.
    ///
    /// Design principles:
    /// - Checks materials from IStorageProvider (safe snapshots)
    /// - Station access based on range + memory (tier-dependent)
    /// - Environmental requirements handled via special unlocks
    /// </summary>
    public class CraftabilityChecker
    {
        private readonly ILogger _log;
        private readonly RecipeIndex _recipeIndex;
        private readonly StorageHubConfig _config;
        private readonly StationDetector _stationDetector;
        public CraftingMaterialSource Materials { get; }

        // Cached material counts for performance
        private Dictionary<int, int> _materialCounts = new Dictionary<int, int>();
        private readonly Dictionary<int, long> _planningCounts = new Dictionary<int, long>();
        private bool _invalidMaterialSources;
        private bool _materialsDirty = true;

        // Available stations (tile IDs) â€” includes both remembered AND nearby
        private HashSet<int> _availableStations = new HashSet<int>();
        private bool _stationsDirty = true;

        // Environment conditions (proximity-based)
        private EnvironmentState _environmentState;

        /// <summary>
        /// Check if a station tile ID is currently available (remembered + nearby).
        /// Auto-refreshes if dirty so NetworkTab gets current data without CraftTab being active.
        /// </summary>
        public bool IsStationAvailable(int tileId)
        {
            if (_stationsDirty) RefreshStations();
            return _availableStations.Contains(tileId);
        }

        /// <summary>
        /// Get the current environment state (proximity-based).
        /// Auto-refreshes if dirty.
        /// </summary>
        public EnvironmentState EnvironmentState
        {
            get
            {
                if (_stationsDirty) RefreshStations();
                return _environmentState;
            }
        }

        public CraftabilityChecker(ILogger log, RecipeIndex recipeIndex, IStorageProvider storage, StorageHubConfig config, RangeCalculator rangeCalc)
            : this(log, recipeIndex, storage, config, rangeCalc,
                new CraftingMaterialSource(storage, rangeCalc, () => config.HotbarProtection)) { }

        public CraftabilityChecker(ILogger log, RecipeIndex recipeIndex, IStorageProvider storage, StorageHubConfig config,
            RangeCalculator rangeCalc, CraftingMaterialSource materials)
        {
            _log = log;
            _recipeIndex = recipeIndex;
            _config = config;
            Materials = materials ?? throw new ArgumentNullException(nameof(materials));
            _stationDetector = new StationDetector(log, config);
        }

        /// <summary>
        /// Mark caches as dirty (call when storage or stations change).
        /// </summary>
        public void MarkDirty()
        {
            _materialsDirty = true;
            _stationsDirty = true;
        }

        /// <summary>
        /// Refresh material counts from storage.
        /// After counting real items, adds fake group counts (sum of all valid items per group)
        /// so recipe group ingredients can be matched.
        /// </summary>
        public void RefreshMaterials()
        {
            _materialCounts.Clear();
            _planningCounts.Clear();
            _invalidMaterialSources = false;

            var items = Materials.Read();
            var locations = new HashSet<(int, int)>();
            foreach (var item in items)
            {
                if (item.IsEmpty) continue;
                if (item.SourceSlot < 0 || !locations.Add((item.SourceChestIndex, item.SourceSlot)))
                    _invalidMaterialSources = true;
                _planningCounts.TryGetValue(item.ItemId, out long total);
                _planningCounts[item.ItemId] = total + item.Stack;

                // Group by item ID (ignore prefix for crafting materials)
                if (_materialCounts.TryGetValue(item.ItemId, out int count))
                {
                    // Use long to prevent overflow
                    long newCount = (long)count + item.Stack;
                    _materialCounts[item.ItemId] = newCount > int.MaxValue ? int.MaxValue : (int)newCount;
                }
                else
                {
                    _materialCounts[item.ItemId] = item.Stack;
                }
            }

            // Add fake group counts â€” mirrors vanilla AddFakeCountsForItemGroups()
            AddFakeCountsForRecipeGroups();

            _materialsDirty = false;
        }

        /// <summary>
        /// For each recipe group, sum up all valid items we have and store under the fake group ID.
        /// This allows GetMaterialCount(fakeGroupId) to return the total count of all matching items.
        /// </summary>
        private void AddFakeCountsForRecipeGroups()
        {
            if (!_recipeIndex.HasRecipeGroupSupport) return;

            // Iterate all recipes and find unique group ingredients
            foreach (var recipe in _recipeIndex.GetAllRecipes())
            {
                foreach (var ing in recipe.Ingredients)
                {
                    if (!ing.IsRecipeGroup || ing.ValidItemIds == null) continue;
                    if (_materialCounts.ContainsKey(ing.ItemId)) continue; // Already computed

                    long total = 0;
                    foreach (int validId in ing.ValidItemIds)
                    {
                        if (_materialCounts.TryGetValue(validId, out int count))
                            total += count;
                    }
                    _materialCounts[ing.ItemId] = total > int.MaxValue ? int.MaxValue : (int)total;
                }
            }
        }

        /// <summary>
        /// Get a copy of all material counts (for virtual pool validation).
        /// </summary>
        public Dictionary<int, int> GetAllMaterialCounts()
        {
            if (_materialsDirty) RefreshMaterials();
            return new Dictionary<int, int>(_materialCounts);
        }

        /// <summary>
        /// Refresh available crafting stations.
        /// Saves config if new stations were remembered (survives crash/alt-F4).
        /// </summary>
        public void RefreshStations()
        {
            _availableStations.Clear();

            // Track remembered count before scan to detect changes
            int rememberedBefore = _config.RememberedStations.Count;

            // Add remembered stations (Tier 3+ with station memory enabled)
            if (ProgressionTier.HasStationMemory(_config.Tier) && _config.StationMemoryEnabled)
            {
                foreach (var tile in _config.RememberedStations)
                {
                    _availableStations.Add(tile);
                }
            }

            // Demon/Crimson Altar special unlock â€” tile 26 is both Demon and Crimson Altar
            // (same tile type, different style). These can't always be "visited" (destroyed in hardmode).
            if (_config.HasSpecialUnlock("demonAltar") || _config.HasSpecialUnlock("crimsonAltar"))
            {
                _availableStations.Add(26);
            }

            // Scan nearby tiles for actual stations
            // (ScanNearbyStations also adds to RememberedStations for Tier 3+)
            var nearbyStations = _stationDetector.ScanNearbyStations();
            int nearbyCount = nearbyStations.Count;
            foreach (var tile in nearbyStations)
            {
                _availableStations.Add(tile);
            }

            // Scan environment conditions (proximity-based)
            _environmentState = _stationDetector.ScanEnvironmentConditions();

            _log.Debug($"[CraftabilityChecker] Stations refreshed: {_availableStations.Count} total ({nearbyCount} nearby, {_availableStations.Count - nearbyCount} from memory)");

            // Save config if new stations were remembered (crash protection)
            if (_config.RememberedStations.Count > rememberedBefore)
            {
                _log.Debug($"[CraftabilityChecker] {_config.RememberedStations.Count - rememberedBefore} new stations remembered, saving config");
                try { _config.Save(); } catch { /* non-fatal */ }
            }

            _stationsDirty = false;
        }

        /// <summary>
        /// Capture the currently available stations and recipe environment without
        /// mutating station memory. Used to avoid full catalog reevaluation when the
        /// player's crafting context has not actually changed.
        /// </summary>
        public ulong CaptureContextSignature()
        {
            var available = _stationDetector.ScanNearbyStations(rememberStations: false);

            if (ProgressionTier.HasStationMemory(_config.Tier) && _config.StationMemoryEnabled)
            {
                foreach (int tile in _config.RememberedStations)
                    available.Add(tile);
            }

            if (_config.HasSpecialUnlock("demonAltar") || _config.HasSpecialUnlock("crimsonAltar"))
                available.Add(26);

            var orderedStations = new List<int>(available);
            orderedStations.Sort();

            ulong hash = 14695981039346656037UL;
            AddSignatureValue(ref hash, _config.Tier);
            AddSignatureValue(ref hash, _config.StationMemoryEnabled ? 1 : 0);
            AddSignatureValue(ref hash, orderedStations.Count);
            foreach (int tile in orderedStations)
                AddSignatureValue(ref hash, tile);

            var environment = _stationDetector.ScanEnvironmentConditions();
            AddSignatureValue(ref hash, environment.HasWater ? 1 : 0);
            AddSignatureValue(ref hash, environment.HasHoney ? 1 : 0);
            AddSignatureValue(ref hash, environment.HasLava ? 1 : 0);
            AddSignatureValue(ref hash, environment.InSnow ? 1 : 0);
            AddSignatureValue(ref hash, environment.InGraveyard ? 1 : 0);

            string[] unlocks =
            {
                "water", "honey", "lava", "snow", "graveyard", "shimmer",
                "demonAltar", "crimsonAltar"
            };
            foreach (string unlock in unlocks)
                AddSignatureValue(ref hash, _config.HasSpecialUnlock(unlock) ? 1 : 0);

            return hash;
        }

        private static void AddSignatureValue(ref ulong hash, int value)
        {
            unchecked
            {
                hash ^= (uint)value;
                hash *= 1099511628211UL;
                hash ^= (uint)(value >> 16);
                hash *= 1099511628211UL;
            }
        }
        /// <summary>
        /// Get total count of a specific material.
        /// </summary>
        public int GetMaterialCount(int itemId)
        {
            if (_materialsDirty) RefreshMaterials();
            return _materialCounts.TryGetValue(itemId, out int count) ? count : 0;
        }

        /// <summary>
        /// Check if a recipe can be crafted.
        /// </summary>
        public CraftabilityResult CanCraft(int recipeIndex)
        {
            return CanCraft(_recipeIndex.GetRecipe(recipeIndex));
        }

        /// <summary>
        /// Check if a recipe can be crafted.
        /// </summary>
        public CraftabilityResult CanCraft(RecipeInfo recipe)
        {
            if (recipe == null)
                return new CraftabilityResult { Status = CraftStatus.InvalidRecipe };

            if (_materialsDirty) RefreshMaterials();
            if (_stationsDirty) RefreshStations();

            if (_invalidMaterialSources)
                return new CraftabilityResult { Recipe = recipe, Status = CraftStatus.InvalidRecipe };

            var result = new CraftabilityResult
            {
                Recipe = recipe,
                MaxCraftable = int.MaxValue
            };

            // Independent per-ingredient totals are only an upper bound: groups and
            // repeated requirements may share the same stock. Use the execution allocator
            // both for shortage details and the exact feasible batch count.
            result.MissingMaterials = new List<MissingMaterial>();
            bool validMaterials = true;
            int upper = int.MaxValue;
            bool independentCosts = true;
            var usedTypes = new HashSet<int>();
            var missing = new int[recipe.Ingredients.Count];
            for (int ingredientIndex = 0; ingredientIndex < recipe.Ingredients.Count; ingredientIndex++)
            {
                var ing = recipe.Ingredients[ingredientIndex];
                if (ing == null || ing.RequiredStack <= 0 ||
                    (ing.IsRecipeGroup && (ing.ValidItemIds == null || ing.ValidItemIds.Count == 0)))
                { validMaterials = false; break; }
                long have = 0;
                if (ing.IsRecipeGroup)
                {
                    foreach (int id in ing.ValidItemIds)
                    {
                        have += GetMaterialCount(id);
                        if (!usedTypes.Add(id)) independentCosts = false;
                    }
                }
                else
                {
                    have = GetMaterialCount(ing.ItemId);
                    if (!usedTypes.Add(ing.ItemId)) independentCosts = false;
                }
                upper = (int)Math.Min(upper, have / ing.RequiredStack);
                upper = Math.Min(upper, int.MaxValue / ing.RequiredStack);
                missing[ingredientIndex] = (int)Math.Max(0, ing.RequiredStack - have);
            }
            IReadOnlyList<ItemSnapshot> availabilitySources = null;
            bool enoughMaterials = validMaterials && upper > 0;
            // Separate costs have no allocation conflicts: their minimum ratio is exact.
            // Shared types still use residual flow and quantity probes.
            if (validMaterials && !independentCosts)
            {
                availabilitySources = ConsumptionPlanner.CreateAvailabilitySources(recipe, _planningCounts);
                enoughMaterials = ConsumptionPlanner.TryPlan(recipe, 1, availabilitySources,
                    false, out _, out _, out missing);
            }
            if (!enoughMaterials)
            {
                if (missing == null) validMaterials = false;
                else for (int i = 0; i < missing.Length; i++)
                {
                    if (missing[i] == 0) continue;
                    var ing = recipe.Ingredients[i];
                    result.MissingMaterials.Add(new MissingMaterial { ItemId = ing.ItemId,
                        Name = ing.Name, Required = ing.RequiredStack, Have = ing.RequiredStack - missing[i] });
                }
            }
            result.MaxCraftable = 0;
            if (enoughMaterials)
            {
                // Keep the existing zero-material display convention.
                if (recipe.Ingredients.Count > 0)
                {
                    int low = 1, high = upper;
                    while (!independentCosts && low < high)
                    {
                        int middle = low + (int)(((long)high - low + 1) / 2);
                        if (ConsumptionPlanner.TryPlan(recipe, middle, availabilitySources, false, out _, out _)) low = middle;
                        else high = middle - 1;
                    }
                    result.MaxCraftable = independentCosts ? upper : low;
                }
            }

            // Check stations
            result.MissingStations = new List<int>();
            foreach (var tile in recipe.RequiredTiles)
            {
                if (tile >= 0 && !_availableStations.Contains(tile))
                {
                    result.MissingStations.Add(tile);
                }
            }

            // Check environmental requirements
            result.MissingEnvironment = new List<string>();

            if (recipe.NeedWater && !_config.HasSpecialUnlock("water") && !_environmentState.HasWater)
                result.MissingEnvironment.Add("Water");
            if (recipe.NeedHoney && !_config.HasSpecialUnlock("honey") && !_environmentState.HasHoney)
                result.MissingEnvironment.Add("Honey");
            if (recipe.NeedLava && !_config.HasSpecialUnlock("lava") && !_environmentState.HasLava)
                result.MissingEnvironment.Add("Lava");
            if (recipe.NeedSnowBiome && !_config.HasSpecialUnlock("snow") && !_environmentState.InSnow)
                result.MissingEnvironment.Add("Snow Biome");
            if (recipe.NeedGraveyard && !_config.HasSpecialUnlock("graveyard") && !_environmentState.InGraveyard)
                result.MissingEnvironment.Add("Graveyard");
            if (recipe.NeedShimmer && !_config.HasSpecialUnlock("shimmer"))
                result.MissingEnvironment.Add("Shimmer");

            // Determine overall status
            if (!validMaterials)
            {
                result.Status = CraftStatus.InvalidRecipe;
            }
            else if (!enoughMaterials)
            {
                result.Status = CraftStatus.MissingMaterials;
            }
            else if (result.MissingStations.Count > 0)
            {
                result.Status = CraftStatus.MissingStation;
            }
            else if (result.MissingEnvironment.Count > 0)
            {
                result.Status = CraftStatus.MissingEnvironment;
            }
            else
            {
                result.Status = CraftStatus.Craftable;
            }

            return result;
        }

        /// <summary>
        /// Evaluate every indexed recipe against the current material/station snapshot.
        /// Callers that need multiple filtered views should reuse this result instead of
        /// rescanning storage and rebuilding per-recipe allocation state for each view.
        /// </summary>
        public List<CraftabilityResult> GetRecipeResults()
        {
            var results = new List<CraftabilityResult>();

            foreach (var recipe in _recipeIndex.GetAllRecipes())
                results.Add(CanCraft(recipe));

            return results;
        }

        /// <summary>
        /// Get all craftable recipes.
        /// </summary>
        public List<CraftabilityResult> GetCraftableRecipes()
        {
            var results = new List<CraftabilityResult>();

            foreach (var result in GetRecipeResults())
            {
                if (result.Status == CraftStatus.Craftable)
                {
                    results.Add(result);
                }
            }

            // Sort by output name
            results.Sort((a, b) => string.Compare(a.Recipe.OutputName, b.Recipe.OutputName, StringComparison.OrdinalIgnoreCase));

            return results;
        }

        /// <summary>
        /// Get recipes with partial materials (for "almost craftable" view).
        /// </summary>
        public List<CraftabilityResult> GetPartialRecipes()
        {
            var results = new List<CraftabilityResult>();

            foreach (var result in GetRecipeResults())
            {
                if (result.Status == CraftStatus.MissingMaterials && result.MissingMaterials.Count < result.Recipe.Ingredients.Count)
                {
                    // Has some but not all materials
                    results.Add(result);
                }
            }

            // Sort by number of missing materials (fewer first)
            results.Sort((a, b) => a.MissingMaterials.Count.CompareTo(b.MissingMaterials.Count));

            return results;
        }

        /// <summary>
        /// Register a crafting station as available.
        /// Used when player visits a station.
        /// </summary>
        public void RegisterStation(int tileId)
        {
            if (ProgressionTier.HasStationMemory(_config.Tier))
            {
                _config.RememberedStations.Add(tileId);
            }
            _availableStations.Add(tileId);
        }
    }

    /// <summary>
    /// Result of a craftability check.
    /// </summary>
    public class CraftabilityResult
    {
        public RecipeInfo Recipe { get; set; }
        public CraftStatus Status { get; set; }
        public int MaxCraftable { get; set; }
        public List<MissingMaterial> MissingMaterials { get; set; }
        public List<int> MissingStations { get; set; }
        public List<string> MissingEnvironment { get; set; }
    }

    /// <summary>
    /// Information about a missing material.
    /// </summary>
    public class MissingMaterial
    {
        public int ItemId { get; set; }
        public string Name { get; set; }
        public int Required { get; set; }
        public int Have { get; set; }
        public int Missing => Required - Have;
    }

    /// <summary>
    /// Status of a recipe's craftability.
    /// </summary>
    public enum CraftStatus
    {
        Craftable,          // Can craft now
        MissingMaterials,   // Need more ingredients
        MissingStation,     // Need crafting station
        MissingEnvironment, // Need water/honey/lava/etc.
        InvalidRecipe       // Recipe doesn't exist
    }
}
