using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Terraria;
using Terraria.ID;
using TerrariaModder.Core.UI;
using TerrariaModder.Core.Logging;
using StorageHub.Storage;
using StorageHub.Config;
using StorageHub.Debug;
using StorageHub.Shared;
using StorageHub.Relay;
using TerrariaModder.Core.UI.Widgets;

namespace StorageHub.UI.Tabs
{
    /// <summary>
    /// Shimmer tab - decrafting items using shimmer.
    /// Requires special unlock (10 Aether Block).
    ///
    /// UI Design:
    /// - Left panel: List of unique shimmerable item types (grouped)
    /// - Right panel: All instances of selected item (different stacks/prefixes)
    /// - Bottom: Amount selector + Shimmer button
    /// </summary>
    public class ShimmerTab
    {
        private readonly ILogger _log;
        private readonly IStorageProvider _storage;
        private readonly StorageHubConfig _config;
        private readonly RangeCalculator _rangeCalc;

        // UI components
        private readonly TextInput _searchBar = new TextInput("Search...", 200);
        private readonly ScrollView _itemTypeScroll = new ScrollView();
        private readonly ScrollView _instanceScroll = new ScrollView();

        // Shimmer data from Terraria
        private static int[] _shimmerTransformToItem;  // Direct transforms (torch -> aether torch)
        private static int[] _isCrafted;                // Recipe index for crafted items (decrafting)
        private static bool _shimmerDataLoaded;
        private static int _shimmerLoadAttempts;
        private static Dictionary<int, string> _itemNames = new Dictionary<int, string>();
        private static Dictionary<int, string> _prefixNames = new Dictionary<int, string>();

        // Recipe data for decrafting — accessed via Main.recipe directly

        // Additional shimmer data
        private static bool[] _commonCoin;           // ItemID.Sets.CommonCoin - coins for Coin Luck
        private static Dictionary<int, short> _makeNPCCache = new Dictionary<int, short>();  // Cache makeNPC values
        private static Dictionary<short, string> _npcNames = new Dictionary<short, string>(); // NPC ID -> name

        // NPC shimmer transforms (critter items release transformed creature)
        private static int[] _shimmerTransformToNPC;  // NPCID.Sets.ShimmerTransformToNPC

        // Boss progression locks (vanilla shimmer mechanics)
        private static bool[] _shimmerPostMoonlord;  // ItemID.Sets.ShimmerPostMoonlord - locked until Moon Lord
        private static int[] _isCraftedCrimson;      // ItemID.Sets.IsCraftedCrimson - crimson world recipe indices
        private static int[] _isCraftedCorruption;   // ItemID.Sets.IsCraftedCorruption - corruption world recipe indices
        private static int[] _shimmerCountsAsItem;   // ItemID.Sets.ShimmerCountsAsItem - item aliasing
        private static int[] _shimmerCountsAsItemForDecraft; // ItemID.Sets.ShimmerCountsAsItemForDecraft

        // Boss state: NPC/WorldGen fields accessed directly (public statics)

        // Cached data
        private List<ShimmerableItemGroup> _shimmerableGroups = new List<ShimmerableItemGroup>();
        private List<ShimmerableItemGroup> _filteredGroups = new List<ShimmerableItemGroup>();
        private bool _needsRefresh = true;

        // Selection state
        private int _selectedGroupIndex = -1;
        private int _selectedInstanceIndex = -1;
        private int _shimmerAmount = 1;

        // Blocked items (itemId << 16 | prefix)
        private HashSet<long> _blockedItems = new HashSet<long>();

        // Storage count cache (rebuilt once per Draw frame)
        private Dictionary<int, int> _storageCountCache;
        private bool _storageCountCacheValid;

        // RNG for alchemy decraft discount (vanilla: each ingredient unit has 1/3 chance to vanish)
        private static readonly Random _alchemyRng = new Random();

        // Layout constants
        private const int ItemHeight = 46;           // Increased from 40 for better spacing
        private const int InstanceHeight = 36;
        private const int DetailsInstanceHeight = 32; // Compact rows for details panel

        // Deferred tooltip state
        private string _shimmerTooltipText;
        private int _shimmerTooltipX, _shimmerTooltipY;

        /// <summary>
        /// Callback when storage is modified (shimmer consumed/created items).
        /// Parent UI should refresh storage data when this is called.
        /// </summary>
        public Action OnStorageModified { get; set; }

        public ShimmerTab(ILogger log, IStorageProvider storage, StorageHubConfig config, RangeCalculator rangeCalc)
        {
            _log = log;
            _storage = storage;
            _config = config;
            _rangeCalc = rangeCalc;

            if (!_shimmerDataLoaded)
            {
                LoadShimmerData();
            }
        }

        /// <summary>
        /// Load shimmer transform data from Terraria via reflection.
        /// </summary>
        private void LoadShimmerData()
        {
            try
            {
                // Load ItemID.Sets shimmer arrays directly
                _shimmerTransformToItem = ItemID.Sets.ShimmerTransformToItem;
                _isCrafted = ItemID.Sets.IsCrafted;
                _commonCoin = ItemID.Sets.CommonCoin;
                _shimmerPostMoonlord = ItemID.Sets.ShimmerPostMoonlord;
                _isCraftedCrimson = ItemID.Sets.IsCraftedCrimson;
                _isCraftedCorruption = ItemID.Sets.IsCraftedCorruption;
                _shimmerCountsAsItem = ItemID.Sets.ShimmerCountsAsItem;
                _shimmerCountsAsItemForDecraft = ItemID.Sets.ShimmerCountsAsItemForDecraft;

                _log.Debug($"Loaded shimmer transform data: {_shimmerTransformToItem?.Length ?? 0} entries");
                _log.Debug($"Loaded IsCrafted data: {_isCrafted?.Length ?? 0} entries");

                // Load NPCID.Sets.ShimmerTransformToNPC for critter shimmer transforms
                _shimmerTransformToNPC = NPCID.Sets.ShimmerTransformToNPC;
                _log.Debug($"Loaded ShimmerTransformToNPC data: {_shimmerTransformToNPC?.Length ?? 0} entries");

                // Verify recipe data is available
                _log.Debug($"Recipe data available: {Main.recipe?.Length ?? 0} recipes");

                // Load prefix names via reflection
                LoadPrefixNames();

                _shimmerDataLoaded = true;
            }
            catch (Exception ex)
            {
                _shimmerLoadAttempts++;
                _log.Error($"Failed to load shimmer data (attempt {_shimmerLoadAttempts}/3): {ex.Message}");
                // Clear all partially-loaded fields to avoid inconsistent state
                _shimmerTransformToItem = null;
                _isCrafted = null;
                _commonCoin = null;
                _shimmerPostMoonlord = null;
                _isCraftedCrimson = null;
                _isCraftedCorruption = null;
                _shimmerCountsAsItem = null;
                _shimmerCountsAsItemForDecraft = null;
                _shimmerTransformToNPC = null;
                if (_shimmerLoadAttempts >= 3)
                {
                    _shimmerDataLoaded = true; // Stop retrying after 3 failures
                    _log.Error("[Shimmer] Max load attempts reached, shimmer tab disabled");
                }
                // else _shimmerDataLoaded stays false, will retry on next constructor call
            }
        }

        /// <summary>
        /// Load prefix names from PrefixID constants via reflection.
        /// </summary>
        private void LoadPrefixNames()
        {
            try
            {
                var prefixIdType = typeof(PrefixID);
                {
                    var fields = prefixIdType.GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
                    foreach (var field in fields)
                    {
                        if (field.FieldType == typeof(int) && field.IsLiteral)
                        {
                            int prefixId = (int)field.GetRawConstantValue();
                            string name = field.Name;
                            // Handle duplicates like Deadly2, Hasty2, etc.
                            if (name.EndsWith("2"))
                                name = name.Substring(0, name.Length - 1);
                            // Convert IllTempered to "Ill-Tempered"
                            if (name == "IllTempered")
                                name = "Ill-Tempered";
                            if (!_prefixNames.ContainsKey(prefixId))
                                _prefixNames[prefixId] = name;
                        }
                    }
                    _log.Debug($"Loaded {_prefixNames.Count} prefix names");
                }
            }
            catch (Exception ex)
            {
                _log.Debug($"[Shimmer] LoadPrefixNames failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Get the display name for a prefix ID.
        /// Returns empty string if prefix cannot be resolved (never shows raw ID).
        /// </summary>
        private string GetPrefixName(int prefixId)
        {
            if (prefixId <= 0)
                return "";
            if (_prefixNames.TryGetValue(prefixId, out string name))
                return name;
            // Don't show raw prefix ID - just skip displaying it
            return "";
        }

        #region Boss Progression and Transform Lock Helpers

        /// <summary>
        /// Get the shimmer equivalent type for an item, handling aliasing.
        /// Matches vanilla Item.GetShimmerEquivalentType().
        /// </summary>
        private int GetShimmerEquivalentType(int itemId, bool forDecrafting = false)
        {
            // For decrafting, check special decraft alias first
            if (forDecrafting && _shimmerCountsAsItemForDecraft != null &&
                itemId >= 0 && itemId < _shimmerCountsAsItemForDecraft.Length)
            {
                int decraftAlias = _shimmerCountsAsItemForDecraft[itemId];
                if (decraftAlias != -1)
                    return decraftAlias;
            }

            // General alias (e.g., world evil variants)
            if (_shimmerCountsAsItem != null && itemId >= 0 && itemId < _shimmerCountsAsItem.Length)
            {
                int alias = _shimmerCountsAsItem[itemId];
                if (alias != -1)
                    return alias;
            }

            return itemId;
        }

        /// <summary>
        /// Get whether the current world is crimson.
        /// </summary>
        private bool IsCrimsonWorld() => WorldGen.crimson;

        /// <summary>
        /// Get boss defeat state.
        /// </summary>
        private bool HasDefeatedSkeletron() => NPC.downedBoss3;
        private bool HasDefeatedGolem() => NPC.downedGolemBoss;
        private bool HasDefeatedMoonLord() => NPC.downedMoonlord;

        /// <summary>
        /// Check if an item's transform is locked (post-Moonlord items).
        /// Matches vanilla ShimmerTransforms.IsItemTransformLocked().
        /// </summary>
        private bool IsItemTransformLocked(int shimmerEquivalentType)
        {
            if (_shimmerPostMoonlord != null &&
                shimmerEquivalentType >= 0 && shimmerEquivalentType < _shimmerPostMoonlord.Length)
            {
                if (_shimmerPostMoonlord[shimmerEquivalentType] && !HasDefeatedMoonLord())
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Get the decrafting recipe index for an item, accounting for world evil variants.
        /// Matches vanilla ShimmerTransforms.GetDecraftingRecipeIndex().
        /// </summary>
        private int GetDecraftingRecipeIndex(int shimmerEquivalentType)
        {
            if (_isCrafted == null || shimmerEquivalentType < 0 || shimmerEquivalentType >= _isCrafted.Length)
                return -1;

            int baseIndex = _isCrafted[shimmerEquivalentType];
            if (baseIndex < 0)
                return -1;

            // Check world evil variant recipe
            bool isCrimson = IsCrimsonWorld();
            if (isCrimson && _isCraftedCrimson != null &&
                shimmerEquivalentType < _isCraftedCrimson.Length)
            {
                int crimsonIndex = _isCraftedCrimson[shimmerEquivalentType];
                if (crimsonIndex >= 0)
                    return crimsonIndex;
            }
            else if (!isCrimson && _isCraftedCorruption != null &&
                     shimmerEquivalentType < _isCraftedCorruption.Length)
            {
                int corruptionIndex = _isCraftedCorruption[shimmerEquivalentType];
                if (corruptionIndex >= 0)
                    return corruptionIndex;
            }

            return baseIndex;
        }

        /// <summary>
        /// Check if a recipe is decraft-locked based on boss progression.
        /// Matches vanilla ShimmerTransforms.IsRecipeIndexDecraftLocked().
        /// </summary>
        private bool IsRecipeIndexDecraftLocked(int recipeIndex)
        {
            var recipes = Main.recipe;
            if (recipeIndex < 0 || recipes == null || recipeIndex >= recipes.Length)
                return false;

            try
            {
                var recipe = recipes[recipeIndex];
                if (recipe == null) return false;

                var requiredItems = recipe.requiredItem;
                if (requiredItems == null) return false;

                // Check for post-Skeletron ingredient (Bone = 154)
                // Check for post-Golem ingredient (LihzahrdBrick = 1101)
                foreach (var reqItem in requiredItems)
                {
                    if (reqItem == null) continue;
                    int reqType = ((Item)reqItem).type;

                    // Bone (154) - requires Skeletron defeated
                    if (reqType == 154 && !HasDefeatedSkeletron())
                        return true;

                    // LihzahrdBrick (1101) - requires Golem defeated
                    if (reqType == 1101 && !HasDefeatedGolem())
                        return true;
                }
            }
            catch (Exception ex)
            {
                _log.Debug($"[Shimmer] IsRecipeIndexDecraftLocked error: {ex.Message}");
            }

            return false;
        }

        #endregion

        /// <summary>
        /// Check if an item can be shimmered (direct transform, decrafting, coin luck, or NPC spawn).
        /// Matches vanilla Item.CanShimmer() logic including all lock checks.
        /// </summary>
        private bool CanShimmer(int itemId)
        {
            // Get shimmer equivalent type (handles item aliasing)
            int shimmerEquiv = GetShimmerEquivalentType(itemId);

            // 1. Check transform lock (post-Moonlord items can't transform until boss defeated)
            if (IsItemTransformLocked(shimmerEquiv))
                return false;

            // 2. Check direct transform (e.g., Torch -> Aether Torch)
            if (_shimmerTransformToItem != null && shimmerEquiv >= 0 && shimmerEquiv < _shimmerTransformToItem.Length)
            {
                int transformTo = _shimmerTransformToItem[shimmerEquiv];
                if (transformTo > 0 && transformTo != shimmerEquiv)
                    return true;
            }

            // 3. Check if coin (vanilla priority: coins before decraft)
            if (_commonCoin != null && shimmerEquiv >= 0 && shimmerEquiv < _commonCoin.Length && _commonCoin[shimmerEquiv])
                return true;

            // 4. Check if item can be decrafted (must check locks)
            int decraftEquiv = GetShimmerEquivalentType(itemId, forDecrafting: true);
            int recipeIndex = GetDecraftingRecipeIndex(decraftEquiv);
            if (recipeIndex >= 0 && !IsRecipeIndexDecraftLocked(recipeIndex))
                return true;

            // 5. Check if NPC spawn item (critters that transform in shimmer)
            if (GetMakeNPC(itemId) > 0)
                return true;

            return false;
        }

        // InvokeSetDefaults is provided by ReflectionCache.InvokeSetDefaults(item, type)

        /// <summary>
        /// Get the makeNPC value for an item (what NPC it spawns when used/shimmered).
        /// </summary>
        private short GetMakeNPC(int itemId)
        {
            if (_makeNPCCache.TryGetValue(itemId, out short cached))
                return cached;

            short result = 0;
            try
            {
                var tempItem = new Item();
                tempItem.SetDefaults(itemId);
                result = tempItem.makeNPC;
            }
            catch
            {
                result = 0;
            }

            _makeNPCCache[itemId] = result;
            return result;
        }

        /// <summary>
        /// Get the NPC ID that a critter transforms into when shimmered.
        /// Returns the shimmer-transformed NPC if one exists, otherwise the base makeNPC.
        /// </summary>
        private int GetShimmerNPC(int itemId)
        {
            short baseNPC = GetMakeNPC(itemId);
            if (baseNPC <= 0) return baseNPC;

            if (_shimmerTransformToNPC != null && baseNPC < _shimmerTransformToNPC.Length)
            {
                int transformed = _shimmerTransformToNPC[baseNPC];
                if (transformed >= 0)
                    return transformed;
            }

            return baseNPC;
        }

        /// <summary>
        /// Get the shimmer type for an item.
        /// Uses item aliasing to determine the correct shimmer action.
        /// </summary>
        private enum ShimmerType { None, DirectTransform, Decraft, CoinLuck, NPCSpawn, Locked }

        private ShimmerType GetShimmerType(int itemId)
        {
            // Get shimmer equivalent type (handles item aliasing)
            int shimmerEquiv = GetShimmerEquivalentType(itemId);

            // Check direct transform first (takes priority)
            if (_shimmerTransformToItem != null && shimmerEquiv >= 0 && shimmerEquiv < _shimmerTransformToItem.Length)
            {
                int transformTo = _shimmerTransformToItem[shimmerEquiv];
                if (transformTo > 0 && transformTo != shimmerEquiv)
                {
                    // Check if transform is locked (post-Moonlord)
                    if (IsItemTransformLocked(shimmerEquiv))
                        return ShimmerType.Locked;
                    return ShimmerType.DirectTransform;
                }
            }

            // Check coin BEFORE decraft (vanilla priority: coins are always CoinLuck, never decraft)
            // Silver/Gold/Platinum coins have recipes but vanilla treats ALL coins as CoinLuck exclusively
            if (_commonCoin != null && shimmerEquiv >= 0 && shimmerEquiv < _commonCoin.Length && _commonCoin[shimmerEquiv])
                return ShimmerType.CoinLuck;

            // Check decrafting
            int decraftEquiv = GetShimmerEquivalentType(itemId, forDecrafting: true);
            int recipeIndex = GetDecraftingRecipeIndex(decraftEquiv);
            if (recipeIndex >= 0)
            {
                // Check if decraft is locked (boss progression)
                if (IsRecipeIndexDecraftLocked(recipeIndex))
                    return ShimmerType.Locked;
                return ShimmerType.Decraft;
            }

            // Check NPC spawn
            if (GetMakeNPC(itemId) > 0)
                return ShimmerType.NPCSpawn;

            return ShimmerType.None;
        }

        /// <summary>
        /// Get the shimmer result description for an item.
        /// </summary>
        private string GetShimmerResultDescription(int itemId)
        {
            var shimmerType = GetShimmerType(itemId);

            if (shimmerType == ShimmerType.DirectTransform)
            {
                int transformTo = GetShimmerResult(itemId);
                if (transformTo <= 0) return "";
                return $"-> {GetItemName(transformTo)}";
            }

            if (shimmerType == ShimmerType.Decraft)
            {
                // Pass createStack as inputStack so decraftCount=1 (not 0 for multi-output recipes)
                var ingredients = GetDecraftIngredients(itemId, GetDecraftInputCount(itemId));
                if (ingredients.Count > 0)
                {
                    // Show first ingredient as preview
                    var first = ingredients[0];
                    string suffix = ingredients.Count > 1 ? $" +{ingredients.Count - 1}" : "";
                    return $"-> {GetItemName(first.ItemId)}{suffix}";
                }
            }

            return "";
        }

        /// <summary>
        /// Get the item ID that this item transforms into via shimmer.
        /// Returns -1 if no direct transform (might be decraft instead).
        /// Uses item aliasing to find the correct transform.
        /// </summary>
        private int GetShimmerResult(int itemId)
        {
            int shimmerEquiv = GetShimmerEquivalentType(itemId);
            if (_shimmerTransformToItem == null || shimmerEquiv < 0 || shimmerEquiv >= _shimmerTransformToItem.Length)
                return -1;
            int result = _shimmerTransformToItem[shimmerEquiv];
            return (result > 0 && result != shimmerEquiv) ? result : -1;
        }

        /// <summary>
        /// Helper struct for decraft ingredients.
        /// </summary>
        private struct DecraftIngredient
        {
            public int ItemId;
            public int Stack;
        }

        /// <summary>
        /// Get the decraft ingredients for an item.
        /// Uses item aliasing and world evil variants to find the correct recipe.
        /// </summary>
        private List<DecraftIngredient> GetDecraftIngredients(int itemId, int inputStack)
        {
            var result = new List<DecraftIngredient>();

            var recipes = Main.recipe;
            if (recipes == null)
                return result;

            // Use item aliasing for decrafting
            int decraftEquiv = GetShimmerEquivalentType(itemId, forDecrafting: true);
            int recipeIndex = GetDecraftingRecipeIndex(decraftEquiv);
            if (recipeIndex < 0 || recipeIndex >= recipes.Length)
                return result;

            try
            {
                var recipe = recipes[recipeIndex];
                if (recipe == null) return result;

                // Get how many items the recipe creates
                var createItem = recipe.createItem;
                int createStack = 1;
                if (createItem != null)
                {
                    createStack = createItem.stack;
                    if (createStack <= 0) createStack = 1;
                }

                // Calculate how many times we can decraft
                int decraftCount = inputStack / createStack;
                if (decraftCount <= 0) return result;

                // Check for custom shimmer results (overrides normal recipe ingredients for some items)
                System.Collections.IList customResults = recipe.customShimmerResults;

                if (customResults != null && customResults.Count > 0)
                {
                    // Use custom shimmer results instead of normal recipe ingredients
                    foreach (var customItem in customResults)
                    {
                        if (customItem is not Item ci) continue;

                        int itemType = ci.type;
                        if (itemType <= 0) continue;

                        int itemStack = ci.stack;
                        if (itemStack <= 0) itemStack = 1;

                        result.Add(new DecraftIngredient
                        {
                            ItemId = itemType,
                            Stack = itemStack * decraftCount
                        });
                    }
                }
                else
                {
                    // Use normal recipe ingredients
                    var requiredItems = recipe.requiredItem;
                    if (requiredItems == null) return result;

                    foreach (var reqItem in requiredItems)
                    {
                        if (reqItem is not Item ri) continue;

                        int reqType = ri.type;
                        if (reqType <= 0) continue;

                        int reqStack = ri.stack;
                        if (reqStack <= 0) reqStack = 1;

                        result.Add(new DecraftIngredient
                        {
                            ItemId = reqType,
                            Stack = reqStack * decraftCount
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                _log.Debug($"[Shimmer] GetDecraftIngredients failed: {ex.Message}");
            }

            return result;
        }

        /// <summary>
        /// Check if an item's decraft recipe is an alchemy recipe (crafted at Placed Bottle / Alchemy Table).
        /// Alchemy recipes have a 1/3 chance per ingredient unit to NOT be returned during shimmer decraft.
        /// </summary>
        private bool IsAlchemyRecipe(int itemId)
        {
            var recipes = Main.recipe;
            if (recipes == null)
                return false;

            int decraftEquiv = GetShimmerEquivalentType(itemId, forDecrafting: true);
            int recipeIndex = GetDecraftingRecipeIndex(decraftEquiv);
            if (recipeIndex < 0 || recipeIndex >= recipes.Length)
                return false;

            try
            {
                var recipe = recipes[recipeIndex];
                if (recipe == null) return false;
                return recipe.alchemy;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Apply alchemy discount to decraft ingredients (vanilla behavior).
        /// Each individual ingredient unit has a 1/3 chance to vanish.
        /// Returns a new list with reduced stacks.
        /// </summary>
        private List<DecraftIngredient> ApplyAlchemyDiscount(List<DecraftIngredient> ingredients)
        {
            var result = new List<DecraftIngredient>(ingredients.Count);
            foreach (var ing in ingredients)
            {
                int remaining = ing.Stack;
                for (int i = ing.Stack; i > 0; i--)
                {
                    if (_alchemyRng.Next(3) == 0)
                        remaining--;
                }
                if (remaining > 0)
                {
                    result.Add(new DecraftIngredient { ItemId = ing.ItemId, Stack = remaining });
                }
            }
            return result;
        }

        /// <summary>
        /// Get how many items are consumed per decraft operation.
        /// Uses item aliasing and world evil variants.
        /// </summary>
        private int GetDecraftInputCount(int itemId)
        {
            var recipes = Main.recipe;
            if (recipes == null)
                return 1;

            int decraftEquiv = GetShimmerEquivalentType(itemId, forDecrafting: true);
            int recipeIndex = GetDecraftingRecipeIndex(decraftEquiv);
            if (recipeIndex < 0 || recipeIndex >= recipes.Length)
                return 1;

            try
            {
                var recipe = recipes[recipeIndex];
                if (recipe == null) return 1;

                var createItem = recipe.createItem;
                if (createItem != null)
                {
                    return createItem.stack > 0 ? createItem.stack : 1;
                }
            }
            catch { }

            return 1;
        }

        /// <summary>
        /// Mark data as needing refresh.
        /// </summary>
        public void MarkDirty()
        {
            _needsRefresh = true;
        }

        /// <summary>
        /// Handle input during Update phase.
        /// </summary>
        public void Update()
        {
            _searchBar.Update();
        }

        /// <summary>
        /// Draw the shimmer tab.
        /// </summary>
        public void Draw(int x, int y, int width, int height)
        {
            // Check unlock - shimmer unlock enables both crafting and decrafting
            if (!_config.HasSpecialUnlock("shimmer"))
            {
                DrawLocked(x, y, width, height);
                return;
            }

            // Invalidate per-frame caches
            _storageCountCacheValid = false;

            // Reset deferred tooltip
            _shimmerTooltipText = null;

            // Refresh if needed
            if (_needsRefresh)
            {
                RefreshItems();
                _needsRefresh = false;
            }

            // Search bar at top
            _searchBar.Draw(x, y, width - 120, 28);
            if (_searchBar.HasChanged)
            {
                FilterItems();
                _itemTypeScroll.ResetScroll();
                _selectedGroupIndex = -1;
                _selectedInstanceIndex = -1;
            }

            // Item count
            UIRenderer.DrawText($"{_filteredGroups.Count} types", x + width - 110, y + 6, UIColors.TextHint);

            // Layout: Left panel (item types) | Right panel (details)
            int panelY = y + 35;
            int panelHeight = height - 110; // Leave room for controls at bottom
            int panelGap = 10;
            int leftWidth = (width - panelGap) * 42 / 100;   // 42%
            int rightWidth = width - leftWidth - panelGap;    // 58%

            // Left panel - Item types
            DrawItemTypePanel(x, panelY, leftWidth, panelHeight);

            // Right panel - Combined details (shimmer result + instances)
            DrawDetailsPanel(x + leftWidth + panelGap, panelY, rightWidth, panelHeight);

            // Bottom controls
            DrawShimmerControls(x, y + height - 72, width);

            // Deferred tooltip (drawn last, on top of everything)
            if (!string.IsNullOrEmpty(_shimmerTooltipText))
            {
                int pad = 8;
                int tw = TextUtil.MeasureWidth(_shimmerTooltipText) + pad * 2;
                int th = 24;
                int tx = _shimmerTooltipX;
                int ty = _shimmerTooltipY;
                if (tx + tw > WidgetInput.ScreenWidth - 4) tx = WidgetInput.ScreenWidth - tw - 4;
                if (tx < 4) tx = 4;
                if (ty + th > WidgetInput.ScreenHeight - 4) ty = _shimmerTooltipY - th - 20;
                UIRenderer.DrawRect(tx, ty, tw, th, UIColors.TooltipBg.WithAlpha(245));
                UIRenderer.DrawRectOutline(tx, ty, tw, th, UIColors.Divider, 1);
                UIRenderer.DrawText(_shimmerTooltipText, tx + pad, ty + 4, UIColors.TextDim);
            }
        }

        private void DrawLocked(int x, int y, int width, int height)
        {
            int centerY = y + height / 2;

            UIRenderer.DrawText("Shimmer Decrafting Locked", x + width / 2 - 100, centerY - 30, UIColors.Warning);
            UIRenderer.DrawText("Consume 10 Aether Block to unlock.", x + width / 2 - 120, centerY, UIColors.TextDim);
            UIRenderer.DrawText("Go to Info tab to unlock special features.", x + width / 2 - 140, centerY + 25, UIColors.TextHint);
        }

        private void DrawItemTypePanel(int x, int y, int width, int height)
        {
            // Panel background
            UIRenderer.DrawRect(x, y, width, height, UIColors.PanelBg);

            // Header
            UIRenderer.DrawRect(x, y, width, 24, UIColors.SectionBg);
            UIRenderer.DrawText("Item Types", x + 8, y + 4, UIColors.TextDim);

            int listY = y + 26;
            int listHeight = height - 26;

            int totalHeight = _filteredGroups.Count * ItemHeight;
            _itemTypeScroll.Begin(x, listY, width, listHeight, totalHeight);

            int startIndex = _itemTypeScroll.GetVisibleStartIndex(ItemHeight);
            int visibleCount = _itemTypeScroll.GetVisibleCount(ItemHeight);
            int yOffset = _itemTypeScroll.GetFirstItemYOffset(ItemHeight);

            UIRenderer.BeginClip(x, listY, width, listHeight);

            for (int i = 0; i < visibleCount && startIndex + i < _filteredGroups.Count; i++)
            {
                int groupIdx = startIndex + i;
                var group = _filteredGroups[groupIdx];
                int itemY = listY + i * ItemHeight + yOffset;

                // Skip if outside visible area
                if (itemY + ItemHeight <= listY || itemY >= listY + listHeight) continue;

                bool isSelected = groupIdx == _selectedGroupIndex;
                bool isHovered = WidgetInput.IsMouseOver(x, listY, width, listHeight) &&
                                 WidgetInput.IsMouseOver(x, itemY, width - 12, ItemHeight - 2);

                DrawItemTypeRow(x, itemY, width - 12, group, isSelected, isHovered);

                if (isHovered && WidgetInput.MouseLeftClick)
                {
                    _selectedGroupIndex = groupIdx;
                    _selectedInstanceIndex = -1;
                    _shimmerAmount = 1;
                    _instanceScroll.ResetScroll();
                    WidgetInput.ConsumeClick();
                }
            }

            UIRenderer.EndClip();
            _itemTypeScroll.End();

            if (_filteredGroups.Count == 0)
            {
                UIRenderer.DrawText("No shimmerable items", x + 10, listY + 10, UIColors.TextHint);
            }
        }

        private void DrawItemTypeRow(int x, int y, int width, ShimmerableItemGroup group, bool isSelected, bool isHovered)
        {
            // Background
            Color4 bgColor;
            if (isSelected)
                bgColor = UIColors.ItemActiveBg;
            else if (isHovered)
                bgColor = UIColors.ItemHoverBg;
            else
                bgColor = UIColors.ItemBg;
            UIRenderer.DrawRect(x, y, width, ItemHeight - 2, bgColor);

            // Item icon (left side) - slightly larger and better centered
            const int iconSize = 36;
            int iconY = y + (ItemHeight - 2 - iconSize) / 2;
            UIRenderer.DrawItem(group.ItemId, x + 4, iconY, iconSize, iconSize);

            // Item name (shifted right for icon, truncated to fit before count)
            int textX = x + iconSize + 12;
            int nameMaxW = width - iconSize - 12 - 60; // reserve space for "x9999" count on right
            UIRenderer.DrawText(TextUtil.Truncate(group.ItemName, nameMaxW), textX, y + 5, UIColors.Text);

            // Shimmer result with icon
            var shimmerType = GetShimmerType(group.ItemId);
            int resultIconSize = 18;
            int resultY = y + 24;

            if (shimmerType == ShimmerType.Locked)
            {
                // Locked items shouldn't normally appear (filtered by CanShimmer)
                // but show status in case they slip through
                UIRenderer.DrawText("LOCKED (defeat boss)", textX, resultY, UIColors.Error);
            }
            else if (shimmerType == ShimmerType.DirectTransform)
            {
                int resultId = GetShimmerResult(group.ItemId);  // Use aliasing-aware method
                if (resultId > 0)
                {
                    UIRenderer.DrawText("->", textX, resultY, UIColors.Info);
                    UIRenderer.DrawItem(resultId, textX + 20, resultY - 2, resultIconSize, resultIconSize);
                    if (isHovered && WidgetInput.IsMouseOver(textX + 20, resultY - 2, resultIconSize, resultIconSize))
                    {
                        _shimmerTooltipText = GetItemName(resultId);
                        _shimmerTooltipX = textX + 20;
                        _shimmerTooltipY = resultY + resultIconSize;
                    }
                }
            }
            else if (shimmerType == ShimmerType.Decraft)
            {
                // Pass createStack as inputStack so decraftCount=1 (not 0 for multi-output recipes)
                var ingredients = GetDecraftIngredients(group.ItemId, GetDecraftInputCount(group.ItemId));
                if (ingredients.Count > 0)
                {
                    var first = ingredients[0];
                    string suffix = ingredients.Count > 1 ? $"+{ingredients.Count - 1}" : "";
                    UIRenderer.DrawText("->", textX, resultY, UIColors.Info);
                    UIRenderer.DrawItem(first.ItemId, textX + 20, resultY - 2, resultIconSize, resultIconSize);
                    if (isHovered && WidgetInput.IsMouseOver(textX + 20, resultY - 2, resultIconSize, resultIconSize))
                    {
                        _shimmerTooltipText = GetItemName(first.ItemId);
                        _shimmerTooltipX = textX + 20;
                        _shimmerTooltipY = resultY + resultIconSize;
                    }
                    if (!string.IsNullOrEmpty(suffix))
                    {
                        UIRenderer.DrawText(suffix, textX + 42, resultY, UIColors.Info);
                    }
                }
            }
            else if (shimmerType == ShimmerType.CoinLuck)
            {
                UIRenderer.DrawText("Coin Luck", textX, resultY, UIColors.Warning);
            }
            else if (shimmerType == ShimmerType.NPCSpawn)
            {
                int shimmerNpcId = GetShimmerNPC(group.ItemId);
                string npcName = GetNPCName((short)shimmerNpcId);
                UIRenderer.DrawText($"-> {npcName}", textX, resultY, UIColors.Success);
            }

            // Total count across all instances (right-aligned, vertically centered)
            int totalStack = 0;
            foreach (var inst in group.Instances)
                totalStack += inst.Stack;
            string totalText = $"x{totalStack}";
            int totalW = TextUtil.MeasureWidth(totalText);
            UIRenderer.DrawText(totalText, x + width - totalW - 4, y + (ItemHeight - 2) / 2 - 6, UIColors.TextDim);
        }

        /// <summary>
        /// Draw the combined details panel (shimmer visualization + instances).
        /// </summary>
        private void DrawDetailsPanel(int x, int y, int width, int height)
        {
            // Panel background
            UIRenderer.DrawRect(x, y, width, height, UIColors.PanelBg);

            // Header
            UIRenderer.DrawRect(x, y, width, 24, UIColors.SectionBg);
            UIRenderer.DrawText("Details", x + 8, y + 4, UIColors.TextDim);

            int contentY = y + 30;

            // No selection state
            if (_selectedGroupIndex < 0 || _selectedGroupIndex >= _filteredGroups.Count)
            {
                UIRenderer.DrawText("Select an item from the list", x + 10, contentY, UIColors.TextHint);
                return;
            }

            var group = _filteredGroups[_selectedGroupIndex];

            // 1. Selected item header (large icon + name + total)
            contentY = DrawSelectedItemHeader(x, contentY, width, group);

            // 2. Shimmer visualization (input -> output)
            contentY = DrawShimmerVisualization(x, contentY, width, group);

            // 3. Instances list (remaining height)
            int instancesHeight = y + height - contentY - 10;
            if (instancesHeight > 50)
            {
                DrawInstancesSection(x, contentY, width, instancesHeight, group);
            }
        }

        /// <summary>
        /// Draw the selected item header with large icon.
        /// </summary>
        private int DrawSelectedItemHeader(int x, int y, int width, ShimmerableItemGroup group)
        {
            const int LargeIconSize = 48;

            // Large item icon
            UIRenderer.DrawItem(group.ItemId, x + 10, y, LargeIconSize, LargeIconSize);
            if (WidgetInput.IsMouseOver(x + 10, y, LargeIconSize, LargeIconSize))
            {
                _shimmerTooltipText = group.ItemName;
                _shimmerTooltipX = x + 10;
                _shimmerTooltipY = y + LargeIconSize + 2;
            }

            // Item name
            UIRenderer.DrawText(group.ItemName, x + 10 + LargeIconSize + 12, y + 6, UIColors.AccentText);

            // Total count
            int totalStack = group.Instances.Sum(i => i.Stack);
            UIRenderer.DrawText($"Total: x{totalStack}", x + 10 + LargeIconSize + 12, y + 26, UIColors.TextDim);

            return y + 58;
        }

        /// <summary>
        /// Draw the shimmer transformation visualization.
        /// </summary>
        private int DrawShimmerVisualization(int x, int y, int width, ShimmerableItemGroup group)
        {
            var shimmerType = GetShimmerType(group.ItemId);

            if (shimmerType == ShimmerType.None)
            {
                UIRenderer.DrawText("No shimmer transform", x + 10, y, UIColors.Error);
                return y + 20;
            }

            // Section header varies by type
            string headerText = shimmerType switch
            {
                ShimmerType.DirectTransform => "TRANSFORMS INTO:",
                ShimmerType.Decraft => "DECRAFTS INTO:",
                ShimmerType.CoinLuck => "SHIMMER EFFECT:",
                ShimmerType.NPCSpawn => "RELEASES NPC:",
                _ => "SHIMMER:"
            };

            // Header color varies by type
            Color4 hdrColor = UIColors.Info;
            if (shimmerType == ShimmerType.CoinLuck) hdrColor = UIColors.Warning;
            if (shimmerType == ShimmerType.NPCSpawn) hdrColor = UIColors.Success;

            UIRenderer.DrawRect(x + 5, y, width - 10, 20, UIColors.PanelBg);
            UIRenderer.DrawText(headerText, x + 12, y + 3, hdrColor);
            y += 24;

            switch (shimmerType)
            {
                case ShimmerType.DirectTransform:
                    int resultId = GetShimmerResult(group.ItemId);
                    y = DrawDirectTransformVisualization(x, y, width, group.ItemId, resultId);
                    break;
                case ShimmerType.Decraft:
                    y = DrawDecraftVisualization(x, y, width, group.ItemId);
                    break;
                case ShimmerType.CoinLuck:
                    y = DrawCoinLuckVisualization(x, y, width, group.ItemId);
                    break;
                case ShimmerType.NPCSpawn:
                    y = DrawNPCSpawnVisualization(x, y, width, group.ItemId);
                    break;
            }

            return y + 8;
        }

        /// <summary>
        /// Draw coin luck visualization showing the luck boost effect.
        /// </summary>
        private int DrawCoinLuckVisualization(int x, int y, int width, int itemId)
        {
            // Coin luck: coins add to a coinLuck accumulator that decays over time
            // Copper=1, Silver=100, Gold=10000, Platinum=1000000 units
            // Luck ranges: 0.025 (low) to 0.2 (max), decays exponentially (0.9999^dayRate per tick)
            string luckInfo = itemId switch
            {
                71 => "Adds 1 coin luck (minimal luck boost)",
                72 => "Adds 100 coin luck (small luck boost)",
                73 => "Adds 10,000 coin luck (moderate luck boost)",
                74 => "Adds 1,000,000 coin luck (max luck boost, up to +0.2)",
                _ => "Adds to coin luck accumulator"
            };

            string description = "Toss coins into shimmer to increase Coin Luck.";
            string note = "Max luck: +0.2. Effect decays over time. More coins = longer duration.";

            UIRenderer.DrawText(luckInfo, x + 15, y, UIColors.Warning);
            y += 20;
            UIRenderer.DrawText(description, x + 15, y, UIColors.TextDim);
            y += 18;
            UIRenderer.DrawText(note, x + 15, y, UIColors.TextHint);
            y += 24;

            return y;
        }

        /// <summary>
        /// Draw NPC spawn visualization showing what creature is released.
        /// </summary>
        private int DrawNPCSpawnVisualization(int x, int y, int width, int itemId)
        {
            short baseNpcId = GetMakeNPC(itemId);
            int shimmerNpcId = GetShimmerNPC(itemId);
            string shimmerName = GetNPCName((short)shimmerNpcId);

            UIRenderer.DrawText($"Releases: {shimmerName}", x + 15, y, UIColors.Success);
            y += 22;

            // Show that it transforms if the shimmer NPC differs from the base
            if (shimmerNpcId != baseNpcId && baseNpcId > 0)
            {
                string baseName = GetNPCName(baseNpcId);
                UIRenderer.DrawText($"(transforms from {baseName})", x + 15, y, UIColors.TextDim);
                y += 20;
            }

            string note = "Toss into shimmer to release the creature.";
            UIRenderer.DrawText(note, x + 15, y, UIColors.TextHint);
            y += 24;

            return y;
        }

        /// <summary>
        /// Get the name of an NPC by ID.
        /// </summary>
        private string GetNPCName(short npcId)
        {
            if (_npcNames.TryGetValue(npcId, out string name))
                return name;

            try
            {
                // Try to get NPC name via reflection
                {
                    var tempNPC = new NPC();
                    // NPC.SetDefaults has 2 params: (int Type, NPCSpawnParams spawnparams = default)
                    // GetMethod with just typeof(int) won't find it — search by name + param count
                    var setDefaultsMethod = typeof(NPC).GetMethods(BindingFlags.Public | BindingFlags.Instance)
                        .FirstOrDefault(m => m.Name == "SetDefaults" &&
                            m.GetParameters().Length >= 1 &&
                            m.GetParameters()[0].ParameterType == typeof(int));
                    if (setDefaultsMethod != null)
                    {
                        var parms = setDefaultsMethod.GetParameters();
                        var args = new object[parms.Length];
                        args[0] = (int)npcId;
                        for (int p = 1; p < parms.Length; p++)
                            args[p] = parms[p].HasDefaultValue ? parms[p].DefaultValue
                                : Activator.CreateInstance(parms[p].ParameterType);
                        setDefaultsMethod.Invoke(tempNPC, args);
                    }

                    var givenNameField = typeof(NPC).GetField("GivenName", BindingFlags.Public | BindingFlags.Instance);
                    var fullNameProp = typeof(NPC).GetProperty("FullName", BindingFlags.Public | BindingFlags.Instance);
                    var typeNameProp = typeof(NPC).GetProperty("TypeName", BindingFlags.Public | BindingFlags.Instance);

                    name = typeNameProp?.GetValue(tempNPC)?.ToString()
                        ?? fullNameProp?.GetValue(tempNPC)?.ToString()
                        ?? givenNameField?.GetValue(tempNPC)?.ToString()
                        ?? $"NPC #{npcId}";
                }
            }
            catch
            {
                name = $"NPC #{npcId}";
            }

            _npcNames[npcId] = name;
            return name;
        }

        /// <summary>
        /// Draw direct transform visualization: [Input Icon] --> [Output Icon]
        /// </summary>
        private int DrawDirectTransformVisualization(int x, int y, int width, int inputId, int resultId)
        {
            const int iconSize = 40;
            string resultName = GetItemName(resultId);

            // Check if result is shimmerable AND we have items to switch to
            bool resultIsShimmerable = CanShimmer(resultId);
            int resultCount = GetStorageCount(resultId);
            bool canSwitch = resultIsShimmerable && resultCount > 0;

            int rowHeight = 55;
            bool rowHovered = canSwitch && WidgetInput.IsMouseOver(x + 5, y, width - 10, rowHeight);

            // Draw highlight if hoverable (only when we can switch)
            if (rowHovered)
            {
                UIRenderer.DrawRect(x + 5, y, width - 10, rowHeight, UIColors.ItemHoverBg);
            }

            // Input icon
            UIRenderer.DrawItem(inputId, x + 15, y + 5, iconSize, iconSize);
            if (WidgetInput.IsMouseOver(x + 15, y + 5, iconSize, iconSize))
            {
                _shimmerTooltipText = GetItemName(inputId);
                _shimmerTooltipX = x + 15;
                _shimmerTooltipY = y + 5 + iconSize + 2;
            }

            // Arrow
            UIRenderer.DrawText("-->", x + 15 + iconSize + 12, y + 18, UIColors.Info);

            // Output icon
            int outputX = x + 15 + iconSize + 50;
            UIRenderer.DrawItem(resultId, outputX, y + 5, iconSize, iconSize);
            if (WidgetInput.IsMouseOver(outputX, y + 5, iconSize, iconSize))
            {
                _shimmerTooltipText = resultName;
                _shimmerTooltipX = outputX;
                _shimmerTooltipY = y + 5 + iconSize + 2;
            }

            // Output name
            UIRenderer.DrawText(resultName, outputX + iconSize + 10, y + 6, UIColors.AccentText);

            // Show storage count of result item
            UIRenderer.DrawText($"You have: x{resultCount}", outputX + iconSize + 10, y + 24,
                resultCount > 0 ? UIColors.Success.WithAlpha(180) : UIColors.TextHint);

            // Click hint - only show if we can actually switch
            if (canSwitch)
            {
                UIRenderer.DrawText("[Click to switch]", outputX + iconSize + 10, y + 40, UIColors.Info);

                if (rowHovered && WidgetInput.MouseLeftClick)
                {
                    SwitchToItem(resultId);
                    WidgetInput.ConsumeClick();
                }
            }

            y += rowHeight + 4;

            // Show chain info if result also shimmers (regardless of whether we have items)
            if (resultIsShimmerable)
            {
                int nextResultId = GetShimmerResult(resultId);
                if (nextResultId > 0)
                {
                    UIRenderer.DrawText("Chain:", x + 15, y, UIColors.TextHint);
                    UIRenderer.DrawText("->", x + 60, y, UIColors.Info);
                    UIRenderer.DrawItem(nextResultId, x + 80, y - 2, 22, 22);
                    if (WidgetInput.IsMouseOver(x + 80, y - 2, 22, 22))
                    {
                        _shimmerTooltipText = GetItemName(nextResultId);
                        _shimmerTooltipX = x + 80;
                        _shimmerTooltipY = y + 22;
                    }
                    y += 26;
                }
            }

            return y;
        }

        /// <summary>
        /// Draw decraft visualization showing all ingredients.
        /// </summary>
        private int DrawDecraftVisualization(int x, int y, int width, int itemId)
        {
            // Pass createStack as inputStack so decraftCount=1 (not 0 for multi-output recipes like Silver Bullet x70)
            var ingredients = GetDecraftIngredients(itemId, GetDecraftInputCount(itemId));

            if (ingredients.Count == 0)
            {
                UIRenderer.DrawText("(no ingredients found)", x + 10, y, UIColors.Error);
                _log.Debug($"[Shimmer] No decraft ingredients for item {itemId} ({GetItemName(itemId)})");
                return y + 20;
            }

            // Show recipe multiplier prominently
            int inputCount = GetDecraftInputCount(itemId);
            if (inputCount > 1)
            {
                UIRenderer.DrawText($"Requires {inputCount} items per decraft", x + 10, y, UIColors.Warning);
                y += 20;
            }

            // Check alchemy recipe - yields are reduced by ~33%
            bool isAlchemy = IsAlchemyRecipe(itemId);
            if (isAlchemy)
            {
                UIRenderer.DrawText("Alchemy recipe: ~33% of each ingredient lost", x + 10, y, UIColors.Warning);
                y += 20;
            }

            const int ingIconSize = 28;
            const int rowHeight = 32;

            // Draw each ingredient
            foreach (var ing in ingredients)
            {
                // Check if this ingredient is shimmerable AND we have items to switch to
                bool ingIsShimmerable = CanShimmer(ing.ItemId);
                int ingCount = GetStorageCount(ing.ItemId);
                bool canSwitch = ingIsShimmerable && ingCount > 0;

                bool ingHovered = canSwitch && WidgetInput.IsMouseOver(x + 5, y, width - 10, rowHeight);

                if (ingHovered)
                {
                    UIRenderer.DrawRect(x + 5, y, width - 10, rowHeight, UIColors.ItemHoverBg);
                }

                // Draw ingredient icon
                UIRenderer.DrawItem(ing.ItemId, x + 15, y + 2, ingIconSize, ingIconSize);
                if (WidgetInput.IsMouseOver(x + 15, y + 2, ingIconSize, ingIconSize))
                {
                    _shimmerTooltipText = GetItemName(ing.ItemId);
                    _shimmerTooltipX = x + 15;
                    _shimmerTooltipY = y + 2 + ingIconSize + 2;
                }

                // Draw ingredient name and yield count
                string ingName = GetItemName(ing.ItemId);
                string yieldText;
                if (isAlchemy && ing.Stack > 0)
                {
                    // Show expected average yield: ~67% of full amount
                    int expectedYield = Math.Max(1, (int)Math.Round(ing.Stack * 2.0 / 3.0));
                    yieldText = ing.Stack > 1 ? $"~x{expectedYield}" : "~x1 (67%)";
                }
                else
                {
                    yieldText = ing.Stack > 1 ? $"x{ing.Stack}" : "";
                }

                // Show storage count on right side (measure first to know name space)
                string haveText = $"have: {ingCount}";
                int haveW = TextUtil.MeasureWidth(haveText);
                int haveX = x + width - haveW - 10;
                UIRenderer.DrawText(haveText, haveX, y + 8,
                    ingCount > 0 ? UIColors.Success.WithAlpha(180) : UIColors.TextHint);

                // Truncate name to fit before count
                int ingNameX = x + 15 + ingIconSize + 8;
                int ingNameMaxW = haveX - ingNameX - 4;
                UIRenderer.DrawText(TextUtil.Truncate(ingName, ingNameMaxW), ingNameX, y + 4, UIColors.AccentText);
                UIRenderer.DrawText(yieldText, ingNameX, y + 18, isAlchemy ? UIColors.Warning : UIColors.TextDim);

                if (canSwitch && ingHovered)
                {
                    // Handle click
                    if (WidgetInput.MouseLeftClick)
                    {
                        SwitchToItem(ing.ItemId);
                        WidgetInput.ConsumeClick();
                    }
                }

                y += rowHeight;
            }

            return y;
        }

        /// <summary>
        /// Draw the instances section showing all stacks/prefixes of the selected item.
        /// </summary>
        private void DrawInstancesSection(int x, int y, int width, int height, ShimmerableItemGroup group)
        {
            // Section header
            UIRenderer.DrawRect(x + 5, y, width - 10, 22, UIColors.PanelBg);
            string instancesHeader = $"Choose Items to Shimmer ({group.Instances.Count})";
            UIRenderer.DrawText(TextUtil.Truncate(instancesHeader, width - 30), x + 12, y + 4, UIColors.TextDim);
            y += 24;
            height -= 24;

            var instances = group.Instances;
            int totalHeight = instances.Count * DetailsInstanceHeight;
            _instanceScroll.Begin(x + 5, y, width - 10, height, totalHeight);

            int startIndex = _instanceScroll.GetVisibleStartIndex(DetailsInstanceHeight);
            int visibleCount = _instanceScroll.GetVisibleCount(DetailsInstanceHeight);
            int yOffset = _instanceScroll.GetFirstItemYOffset(DetailsInstanceHeight);

            UIRenderer.BeginClip(x + 5, y, width - 10, height);

            for (int i = 0; i < visibleCount && startIndex + i < instances.Count; i++)
            {
                int instIdx = startIndex + i;
                var instance = instances[instIdx];
                int itemY = y + i * DetailsInstanceHeight + yOffset;

                // Skip if outside visible area
                if (itemY + DetailsInstanceHeight <= y || itemY >= y + height) continue;

                bool isSelected = instIdx == _selectedInstanceIndex;
                bool isBlocked = IsBlocked(instance);
                bool isHovered = WidgetInput.IsMouseOver(x + 5, y, width - 10, height) &&
                                 WidgetInput.IsMouseOver(x + 5, itemY, width - 22, DetailsInstanceHeight - 2);

                DrawCompactInstanceRow(x + 5, itemY, width - 22, instance, isSelected, isBlocked, isHovered);

                if (isHovered && WidgetInput.MouseLeftClick)
                {
                    _selectedInstanceIndex = instIdx;
                    WidgetInput.ConsumeClick();
                }

                // Right-click to toggle block
                if (isHovered && WidgetInput.MouseRightClick)
                {
                    ToggleBlocked(instance);
                    WidgetInput.ConsumeRightClick();
                }
            }

            UIRenderer.EndClip();
            _instanceScroll.End();
        }

        /// <summary>
        /// Draw a compact instance row (for the details panel).
        /// </summary>
        private void DrawCompactInstanceRow(int x, int y, int width, ItemSnapshot instance, bool isSelected, bool isBlocked, bool isHovered)
        {
            // Background
            Color4 bgColor;
            if (isBlocked)
                bgColor = UIColors.CloseBtn;
            else if (isSelected)
                bgColor = UIColors.ItemActiveBg;
            else if (isHovered)
                bgColor = UIColors.ItemHoverBg;
            else
                bgColor = UIColors.ItemBg;
            UIRenderer.DrawRect(x, y, width, DetailsInstanceHeight - 2, bgColor);

            // Blocked indicator
            int textX = x + 8;
            if (isBlocked)
            {
                UIRenderer.DrawRect(x, y, 4, DetailsInstanceHeight - 2, UIColors.Error);
                UIRenderer.DrawText("[X]", x + 8, y + 8, UIColors.Error);
                textX = x + 35;
            }

            // Stack count and prefix (if any) - more compact layout
            string prefixName = GetPrefixName(instance.Prefix);
            if (!string.IsNullOrEmpty(prefixName))
            {
                UIRenderer.DrawText(prefixName, textX, y + 8, UIColors.Accent);
                int prefixW = TextUtil.MeasureWidth(prefixName);
                UIRenderer.DrawText($"x{instance.Stack}", textX + prefixW + 6, y + 8, UIColors.AccentText);
            }
            else
            {
                UIRenderer.DrawText($"x{instance.Stack}", textX, y + 8, UIColors.AccentText);
            }

            // Source location (right-aligned)
            string sourceName = SourceIndex.GetSourceName(instance.SourceChestIndex);
            int sourceW = TextUtil.MeasureWidth(sourceName);
            UIRenderer.DrawText(sourceName, x + width - sourceW - 4, y + 8, UIColors.TextHint);
        }

        /// <summary>
        /// Switch to shimmering a different item (used for clicking the result).
        /// </summary>
        private void SwitchToItem(int itemId)
        {
            // Find the group with this item ID
            for (int i = 0; i < _filteredGroups.Count; i++)
            {
                if (_filteredGroups[i].ItemId == itemId)
                {
                    _selectedGroupIndex = i;
                    _selectedInstanceIndex = -1;
                    _instanceScroll.ResetScroll();
                    _itemTypeScroll.ScrollToItem(i, ItemHeight);  // Scroll to show selected item
                    _log.Debug($"Switched to shimmer {GetItemName(itemId)}");
                    return;
                }
            }

            // Item not in current list - might need to clear search
            if (!string.IsNullOrEmpty(_searchBar.Text))
            {
                _searchBar.Clear();
                // Consume the HasChanged flag now — we're calling FilterItems ourselves.
                // If we don't, Draw() will see HasChanged=true next frame and reset our selection.
                _ = _searchBar.HasChanged;
                FilterItems();

                // Try again after clearing filter
                for (int i = 0; i < _filteredGroups.Count; i++)
                {
                    if (_filteredGroups[i].ItemId == itemId)
                    {
                        _selectedGroupIndex = i;
                        _selectedInstanceIndex = -1;
                        _instanceScroll.ResetScroll();
                        _itemTypeScroll.ScrollToItem(i, ItemHeight);  // Scroll to show selected item
                        _log.Debug($"Switched to shimmer {GetItemName(itemId)} (cleared search)");
                        return;
                    }
                }
            }

            _log.Warn($"Could not find item {itemId} in shimmerable items");
        }

        private void DrawShimmerControls(int x, int y, int width)
        {
            UIRenderer.DrawRect(x, y, width, 68, UIColors.PanelBg);

            // Get selected instance
            ItemSnapshot? selected = GetSelectedInstance();

            if (selected == null)
            {
                UIRenderer.DrawText("Select a stack to shimmer", x + 10, y + 25, UIColors.TextHint);
                return;
            }

            var item = selected.Value;
            var shimmerType = GetShimmerType(item.ItemId);

            // Coins and NPC spawns can't be "shimmered" via this UI
            if (shimmerType == ShimmerType.CoinLuck)
            {
                UIRenderer.DrawText("Toss coins into shimmer manually for Coin Luck", x + 10, y + 18, UIColors.Warning);
                UIRenderer.DrawText("This UI cannot process coin luck effects", x + 10, y + 38, UIColors.TextHint);
                return;
            }

            if (shimmerType == ShimmerType.NPCSpawn)
            {
                UIRenderer.DrawText("Toss this item into shimmer manually to release the NPC", x + 10, y + 18, UIColors.Success);
                UIRenderer.DrawText("This UI cannot spawn NPCs directly", x + 10, y + 38, UIColors.TextHint);
                return;
            }

            if (shimmerType == ShimmerType.Locked)
            {
                UIRenderer.DrawText("Locked - defeat the required boss first", x + 10, y + 25, UIColors.Warning);
                return;
            }

            if (IsBlocked(item))
            {
                UIRenderer.DrawText("This item is blocked. Right-click to unblock.", x + 10, y + 25, UIColors.Error);
                return;
            }

            // Check minimum required for decrafting
            int minRequired = 1;
            if (shimmerType == ShimmerType.Decraft)
            {
                minRequired = GetDecraftInputCount(item.ItemId);
                if (item.Stack < minRequired)
                {
                    UIRenderer.DrawText($"Need {minRequired} to decraft (have {item.Stack})", x + 10, y + 18, UIColors.Warning);
                    UIRenderer.DrawText("Find more of this item or select a larger stack", x + 10, y + 38, UIColors.TextHint);
                    return;
                }
            }

            // For decrafting, step by minRequired instead of 1
            int stepSmall = shimmerType == ShimmerType.Decraft ? minRequired : 1;
            int stepLarge = shimmerType == ShimmerType.Decraft ? minRequired * 10 : 10;
            int stepHuge = shimmerType == ShimmerType.Decraft ? minRequired * 100 : 100;

            // SHIMMER button (right side, spanning full height)
            int shimmerBtnW = 110;
            int shimmerBtnH = 62;
            int shimmerBtnX = x + width - shimmerBtnW - 5;
            int shimmerBtnY = y + 3;
            bool shimmerHover = WidgetInput.IsMouseOver(shimmerBtnX, shimmerBtnY, shimmerBtnW, shimmerBtnH);
            UIRenderer.DrawRect(shimmerBtnX, shimmerBtnY, shimmerBtnW, shimmerBtnH,
                shimmerHover ? UIColors.Success : UIColors.Success.WithAlpha(180));
            int shimmerTextW = TextUtil.MeasureWidth("SHIMMER");
            UIRenderer.DrawText("SHIMMER", shimmerBtnX + (shimmerBtnW - shimmerTextW) / 2, shimmerBtnY + shimmerBtnH / 2 - 6, UIColors.Text);

            if (shimmerHover && WidgetInput.MouseLeftClick)
            {
                OnShimmer();
                WidgetInput.ConsumeClick();
            }

            // Amount controls (left side, beside SHIMMER button)
            int ctrlWidth = shimmerBtnX - x - 10;

            // Row 1: Amount display
            int row1Y = y + 2;
            int maxAmount = shimmerType == ShimmerType.Decraft
                ? (item.Stack / minRequired) * minRequired
                : item.Stack;
            string amountFull = $"{_shimmerAmount} / {maxAmount}";
            int amountFullW = TextUtil.MeasureWidth(amountFull);

            if (amountFullW <= ctrlWidth)
            {
                string countPart = _shimmerAmount.ToString();
                UIRenderer.DrawText(countPart, x + 5, row1Y, UIColors.AccentText);
                int countW = TextUtil.MeasureWidth(countPart);
                UIRenderer.DrawText($" / {maxAmount}", x + 5 + countW, row1Y, UIColors.Success);
            }
            else
            {
                UIRenderer.DrawText($"{_shimmerAmount}/{maxAmount}", x + 5, row1Y, UIColors.AccentText);
            }

            // Layout: 4 buttons per row, evenly spaced
            int btnH = 20;
            int gap = 2;
            int totalBtnW = ctrlWidth;
            int btnW = (totalBtnW - 3 * gap) / 4;

            // Row 2: +1, +10, +100, Max
            int row2Y = y + 20;
            int cx = x + 5;
            DrawAmountButton(cx, row2Y, btnW, btnH, $"+{stepSmall}", () => _shimmerAmount += stepSmall); cx += btnW + gap;
            DrawAmountButton(cx, row2Y, btnW, btnH, $"+{stepLarge}", () => _shimmerAmount += stepLarge); cx += btnW + gap;
            DrawAmountButton(cx, row2Y, btnW, btnH, $"+{stepHuge}", () => _shimmerAmount += stepHuge); cx += btnW + gap;
            DrawAmountButton(cx, row2Y, btnW, btnH, "Max", () => {
                if (shimmerType == ShimmerType.Decraft)
                    _shimmerAmount = (item.Stack / minRequired) * minRequired;
                else
                    _shimmerAmount = item.Stack;
            });

            // Row 3: -1, -10, -100, Reset
            int row3Y = row2Y + btnH + gap;
            cx = x + 5;
            DrawAmountButton(cx, row3Y, btnW, btnH, $"-{stepSmall}", () => _shimmerAmount = Math.Max(minRequired, _shimmerAmount - stepSmall)); cx += btnW + gap;
            DrawAmountButton(cx, row3Y, btnW, btnH, $"-{stepLarge}", () => _shimmerAmount = Math.Max(minRequired, _shimmerAmount - stepLarge)); cx += btnW + gap;
            DrawAmountButton(cx, row3Y, btnW, btnH, $"-{stepHuge}", () => _shimmerAmount = Math.Max(minRequired, _shimmerAmount - stepHuge)); cx += btnW + gap;
            DrawAmountButton(cx, row3Y, btnW, btnH, "Reset", () => _shimmerAmount = minRequired);

            // Clamp amount
            if (_shimmerAmount > item.Stack)
                _shimmerAmount = item.Stack;
            if (shimmerType == ShimmerType.Decraft)
            {
                _shimmerAmount = (_shimmerAmount / minRequired) * minRequired;
                if (_shimmerAmount < minRequired)
                    _shimmerAmount = minRequired;
            }
            if (_shimmerAmount < 1)
                _shimmerAmount = 1;
        }

        private void DrawAmountButton(int x, int y, int width, int height, string text, Action onClick)
        {
            bool hover = WidgetInput.IsMouseOver(x, y, width, height);
            UIRenderer.DrawRect(x, y, width, height,
                hover ? UIColors.ButtonHover : UIColors.Button);

            int textW = TextUtil.MeasureWidth(text);
            int textX = x + (width - textW) / 2;
            int textY = y + (height - 12) / 2;
            if (textX < x + 2) textX = x + 2;
            UIRenderer.DrawText(text, textX, textY, UIColors.TextDim);

            if (hover && WidgetInput.MouseLeftClick)
            {
                onClick();
                WidgetInput.ConsumeClick();
            }
        }

        private ItemSnapshot? GetSelectedInstance()
        {
            if (_selectedGroupIndex < 0 || _selectedGroupIndex >= _filteredGroups.Count)
                return null;

            var group = _filteredGroups[_selectedGroupIndex];
            if (_selectedInstanceIndex < 0 || _selectedInstanceIndex >= group.Instances.Count)
                return null;

            return group.Instances[_selectedInstanceIndex];
        }

        private void OnShimmer()
        {
            var selected = GetSelectedInstance();
            if (selected == null)
            {
                _log.Debug("[Shimmer] OnShimmer called but no instance selected");
                return;
            }

            var item = selected.Value;
            _log.Info($"[Shimmer] Attempting to shimmer {item.Name} (x{_shimmerAmount}) from {SourceIndex.GetSourceName(item.SourceChestIndex)} slot {item.SourceSlot}");

            if (IsBlocked(item))
            {
                _log.Debug($"[Shimmer] Item {item.Name} is blocked");
                return;
            }

            var shimmerType = GetShimmerType(item.ItemId);
            if (shimmerType != ShimmerType.DirectTransform && shimmerType != ShimmerType.Decraft)
            {
                _log.Warn($"[Shimmer] Item {item.Name} cannot be shimmered (type={shimmerType})");
                return;
            }

            int actualAmount = Math.Min(_shimmerAmount, item.Stack);
            if (actualAmount <= 0)
            {
                _log.Warn($"[Shimmer] Invalid amount: {actualAmount}");
                return;
            }

            // For decrafting, adjust amount to be a multiple of recipe output
            if (shimmerType == ShimmerType.Decraft)
            {
                int inputCount = GetDecraftInputCount(item.ItemId);
                actualAmount = (actualAmount / inputCount) * inputCount;
                if (actualAmount <= 0)
                {
                    _log.Warn($"[Shimmer] Need at least {inputCount}x {item.Name} to decraft");
                    return;
                }
            }

            _log.Info($"[Shimmer] Will {(shimmerType == ShimmerType.Decraft ? "decraft" : "transform")} {actualAmount}x {item.Name}");

            // Dump state BEFORE shimmer
            int debugResultId = shimmerType == ShimmerType.DirectTransform ? GetShimmerResult(item.ItemId) : 0;
            Mod.Dumper?.DumpShimmerOperation("BEFORE", item.ItemId, actualAmount, debugResultId, actualAmount,
                item.SourceChestIndex, item.SourceSlot);

            // Take the item from storage first
            if (!_storage.TakeItem(item.SourceChestIndex, item.SourceSlot, actualAmount, out var taken))
            {
                _log.Error($"Failed to take {actualAmount}x {item.Name} for shimmer");
                Mod.Dumper?.DumpState("SHIMMER_TAKE_FAILED");
                return;
            }

            // Validate the taken item matches what we expected (chest could have changed between snapshot and take)
            if (taken.ItemId != item.ItemId)
            {
                _log.Error($"[Shimmer] Item type mismatch! Expected {item.ItemId} ({item.Name}) but got {taken.ItemId} ({taken.Name}). Returning items.");
                Mod.Dumper?.DumpState("SHIMMER_TYPE_MISMATCH");
                int mismatchDeposited = _storage.DepositItem(taken, out _);
                if (mismatchDeposited < taken.Stack)
                {
                    int mismatchLost = taken.Stack - mismatchDeposited;
                    _log.Error($"[Shimmer] Could not fully return mismatched items to storage ({mismatchDeposited}/{taken.Stack}), spawning {mismatchLost} in inventory");
                    PlaceShimmerResultToInventory(taken.ItemId, mismatchLost);
                }
                return;
            }

            // CRITICAL: Use taken.Stack (actual amount from storage) not actualAmount (from snapshot).
            // Storage could have changed between snapshot and TakeItem, returning fewer items.
            // Using actualAmount here would spawn more output than input consumed (item duplication).
            if (taken.Stack < actualAmount)
            {
                _log.Info($"[Shimmer] Storage changed: requested {actualAmount} but took {taken.Stack}");
                actualAmount = taken.Stack;

                // For decraft, re-align to recipe multiple and return remainder
                if (shimmerType == ShimmerType.Decraft)
                {
                    int inputCount = GetDecraftInputCount(item.ItemId);
                    int aligned = (actualAmount / inputCount) * inputCount;
                    int remainder = actualAmount - aligned;

                    if (aligned <= 0)
                    {
                        // Can't decraft any — return everything to storage
                        _log.Warn($"[Shimmer] Not enough for 1 decraft after take ({taken.Stack} < {inputCount}). Returning.");
                        int cantDecraftDeposited = _storage.DepositItem(taken, out _);
                        if (cantDecraftDeposited < taken.Stack)
                            PlaceShimmerResultToInventory(taken.ItemId, taken.Stack - cantDecraftDeposited);
                        return;
                    }

                    if (remainder > 0)
                    {
                        // Return the non-aligned remainder
                        var remainderSnap = new ItemSnapshot(taken.ItemId, remainder, taken.Prefix,
                            taken.Name, taken.MaxStack, taken.Rarity, -1, -1);
                        int remainderDeposited = _storage.DepositItem(remainderSnap, out _);
                        if (remainderDeposited < remainder)
                            PlaceShimmerResultToInventory(taken.ItemId, remainder - remainderDeposited);
                        _log.Info($"[Shimmer] Returned {remainder} remainder items (not enough for another decraft)");
                    }

                    actualAmount = aligned;
                }
            }

            bool placed;
            if (shimmerType == ShimmerType.DirectTransform)
            {
                int resultItemId = GetShimmerResult(item.ItemId);
                placed = PlaceShimmerResultToInventory(resultItemId, actualAmount);
                if (placed)
                    _log.Info($"Shimmered {actualAmount}x {item.Name} into {GetItemName(resultItemId)}");
            }
            else
            {
                // Decrafting - spawn all ingredients
                // SAFETY: QuickSpawnItem is atomic per-item (items go to inventory or drop on ground)
                // We spawn all ingredients - Terraria handles overflow by dropping items on ground
                // This is safer than trying partial rollback which could cause item loss
                var ingredients = GetDecraftIngredients(item.ItemId, actualAmount);

                // Apply alchemy discount: each ingredient unit has 1/3 chance to vanish (vanilla behavior)
                bool isAlchemy = IsAlchemyRecipe(item.ItemId);
                if (isAlchemy)
                {
                    ingredients = ApplyAlchemyDiscount(ingredients);
                    _log.Info($"[Shimmer] Alchemy discount applied to {item.Name} decraft");
                }

                // If alchemy discount removed ALL ingredients, the shimmer still consumed the input
                // (vanilla behavior: items are lost to shimmer regardless of alchemy outcome)
                if (ingredients.Count == 0)
                {
                    _log.Info($"[Shimmer] Alchemy discount consumed all ingredients for {item.Name} - no output produced (vanilla behavior)");
                    // Fall through to post-shimmer cleanup (reset amount, refresh, etc.)
                }

                placed = true;
                int ingredientsPlaced = 0;
                foreach (var ing in ingredients)
                {
                    if (!PlaceShimmerResultToInventory(ing.ItemId, ing.Stack))
                    {
                        // PlaceShimmerResultToInventory uses QuickSpawnItem which drops on ground if inventory full
                        // This should rarely fail, but log if it does
                        _log.Error($"[Shimmer] Failed to spawn {ing.Stack}x {GetItemName(ing.ItemId)} - this should not happen!");
                        placed = false;
                    }
                    else
                    {
                        ingredientsPlaced++;
                        _log.Debug($"[Shimmer] Spawned {ing.Stack}x {GetItemName(ing.ItemId)}");
                    }
                }
                if (placed)
                {
                    string alchemyNote = isAlchemy ? " (alchemy discount applied)" : "";
                    _log.Info($"Decrafted {actualAmount}x {item.Name} into {ingredients.Count} ingredient types{alchemyNote}");
                }
                else if (ingredientsPlaced > 0)
                {
                    // Partial placement - some ingredients were placed before failure
                    // DO NOT return original items as that would duplicate!
                    // Items that failed to place might still drop on ground via QuickSpawnItem
                    _log.Warn($"[Shimmer] Partial decraft: {ingredientsPlaced}/{ingredients.Count} ingredients placed. NOT recovering original to prevent duplication.");
                    placed = true; // Mark as "handled" to prevent recovery duplication
                }
            }

            if (!placed)
            {
                // Try to return original items to storage via DepositItem
                // ONLY do this if NO outputs were placed (to prevent duplication)
                _log.Error($"Failed to place shimmer result in inventory! Attempting recovery...");
                if (item.SourceChestIndex >= 0)
                    Mod.Dumper?.DumpStateWithChest("SHIMMER_PLACE_FAILED", item.SourceChestIndex);
                else
                    Mod.Dumper?.DumpState("SHIMMER_PLACE_FAILED");
                int recoveryDeposited = _storage.DepositItem(taken, out int depositedTo);
                if (recoveryDeposited >= taken.Stack)
                {
                    _log.Info($"Recovery successful - items returned to chest {depositedTo}");
                    Mod.Dumper?.DumpStateWithChest("SHIMMER_RECOVERY_OK", depositedTo);
                }
                else if (recoveryDeposited > 0)
                {
                    int recoveryLost = taken.Stack - recoveryDeposited;
                    _log.Error($"CRITICAL: Partial recovery - {recoveryDeposited}/{taken.Stack}x {item.Name} returned, {recoveryLost} may be lost!");
                    // Try to spawn remainder in inventory as last resort
                    PlaceShimmerResultToInventory(taken.ItemId, recoveryLost);
                }
                else
                {
                    _log.Error($"CRITICAL: Recovery to storage failed - spawning {taken.Stack}x {item.Name} in inventory as last resort");
                    PlaceShimmerResultToInventory(taken.ItemId, taken.Stack);
                    Mod.Dumper?.DumpState("SHIMMER_RECOVERY_FAILED");
                }
            }

            // Dump state AFTER shimmer
            Mod.Dumper?.DumpShimmerOperation("AFTER", item.ItemId, actualAmount, debugResultId, actualAmount,
                item.SourceChestIndex, item.SourceSlot);

            // Reset amount to 1 after shimmer
            _shimmerAmount = 1;

            // Clear selection before refresh
            _selectedInstanceIndex = -1;
            _selectedGroupIndex = -1;

            // Remember the item ID we were shimmering
            int previousItemId = item.ItemId;

            // Notify parent that storage was modified (for Items tab refresh)
            OnStorageModified?.Invoke();

            // Immediately refresh our data so we can auto-select
            RefreshItems();
            _needsRefresh = false;

            // Find and select the same item group (may have moved in the list)
            for (int i = 0; i < _filteredGroups.Count; i++)
            {
                if (_filteredGroups[i].ItemId == previousItemId)
                {
                    _selectedGroupIndex = i;
                    if (_filteredGroups[i].Instances.Count > 0)
                    {
                        _selectedInstanceIndex = 0;
                        _log.Debug($"[Shimmer] Auto-selected first instance of {_filteredGroups[i].ItemName}");
                    }
                    break;
                }
            }
        }

        /// <summary>
        /// Place the shimmer result into player inventory using QuickSpawnItem.
        /// Uses the Item-based overload to avoid random prefix (vanilla decraft has no prefix).
        /// </summary>
        private bool PlaceShimmerResultToInventory(int itemId, int stack)
        {
            try
            {
                var player = ReflectionCache.GetLocalPlayer();
                if (player == null)
                {
                    _log.Error("[Shimmer] Could not get local player");
                    return false;
                }

                var playerType = player.GetType();

                // Prefer QuickSpawnItem(IEntitySource, Item) to avoid Prefix(-1) randomization.
                // The (IEntitySource, int, int) overload calls Prefix(-1) which randomizes prefix on
                // weapons/accessories. Vanilla decraft spawns with no prefix (pfix=0).
                var quickSpawnItemMethod = playerType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(m => m.Name == "QuickSpawnItem" &&
                        m.GetParameters().Length == 2 &&
                        m.GetParameters()[1].ParameterType == typeof(Item));

                if (quickSpawnItemMethod != null)
                {
                    object source = CreateEntitySource(player);
                    if (source != null)
                    {
                        // Create item with SetDefaults (no prefix) and set stack
                        var resultItem = new Item();
                        resultItem.SetDefaults(itemId);
                        resultItem.stack = stack;
                        quickSpawnItemMethod.Invoke(player, new object[] { source, resultItem });
                        _log.Debug($"[Shimmer] QuickSpawnItem(Item) succeeded for {stack}x item {itemId}");
                        return true;
                    }
                }

                // Fallback: try the (IEntitySource, int, int) overload (applies random prefix but better than nothing)
                var quickSpawnIntMethod = playerType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(m => m.Name == "QuickSpawnItem" && m.GetParameters().Length >= 3);

                if (quickSpawnIntMethod != null)
                {
                    var parms = quickSpawnIntMethod.GetParameters();
                    object source = null;
                    if (parms.Length > 0 && parms[0].ParameterType.Name.Contains("IEntitySource"))
                        source = CreateEntitySource(player);

                    if (source != null || parms.Length == 2)
                    {
                        object[] args = source != null
                            ? new object[] { source, itemId, stack }
                            : new object[] { itemId, stack };
                        quickSpawnIntMethod.Invoke(player, args);
                        _log.Debug($"[Shimmer] QuickSpawnItem(int) fallback for {stack}x item {itemId}");
                        return true;
                    }
                }

                // Last resort: direct inventory placement
                _log.Debug("[Shimmer] Trying fallback inventory placement");
                return PlaceItemDirectly(player, itemId, stack);
            }
            catch (Exception ex)
            {
                _log.Error($"[Shimmer] PlaceShimmerResultToInventory failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Create an EntitySource for QuickSpawnItem.
        /// </summary>
        private object CreateEntitySource(object player)
        {
            try
            {
                // Try creating EntitySource_Parent(player) which is most generic
                var entitySourceParentType = typeof(Terraria.DataStructures.EntitySource_Parent);
                {
                    // Constructor takes IEntitySourceTarget (interface), not Entity (class).
                    // Player extends Entity which implements IEntitySourceTarget, so passing player works.
                    var iEntitySourceTarget = typeof(Terraria.IEntitySourceTarget);
                    var ctor = entitySourceParentType.GetConstructor(new[] { iEntitySourceTarget });
                    if (ctor != null)
                    {
                        return ctor.Invoke(new[] { player });
                    }
                }

                // Try EntitySource_ByItemSourceId
                var entitySourceByItemType = typeof(Terraria.DataStructures.EntitySource_ByItemSourceId);

                if (entitySourceByItemType != null)
                {
                    var ctor = entitySourceByItemType.GetConstructors().FirstOrDefault();
                    if (ctor != null)
                    {
                        var ctorParams = ctor.GetParameters();
                        if (ctorParams.Length == 2)
                        {
                            // (Entity entity, int itemSourceId)
                            return ctor.Invoke(new object[] { player, 0 });
                        }
                    }
                }

                return null;
            }
            catch (Exception ex)
            {
                _log.Debug($"[Shimmer] CreateEntitySource failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Fallback: Place item directly into inventory slot.
        /// </summary>
        private bool PlaceItemDirectly(Player player, int itemId, int stack)
        {
            try
            {
                var inventory = player.inventory;
                if (inventory == null) return false;

                int remaining = stack;

                // First pass: try to stack with existing items of same type
                for (int i = 0; i < 50 && remaining > 0; i++)
                {
                    var invItem = inventory[i];
                    if (invItem == null || invItem.type != itemId) continue;

                    // Don't stack items with different prefixes (modifiers)
                    if (invItem.prefix != 0) continue; // Shimmer results are always unprefixed

                    int maxStack = ReflectionCache.GetItemMaxStack(invItem);
                    if (invItem.stack >= maxStack) continue;

                    int canAdd = Math.Min(remaining, maxStack - invItem.stack);
                    invItem.stack += canAdd;
                    remaining -= canAdd;
                }

                // Second pass: find empty slots
                for (int i = 0; i < 50 && remaining > 0; i++)
                {
                    var invItem = inventory[i];
                    if (invItem != null && invItem.type != 0) continue;

                    if (invItem == null)
                    {
                        inventory[i] = new Item();
                        invItem = inventory[i];
                    }

                    invItem.SetDefaults(itemId);
                    int maxStack = ReflectionCache.GetItemMaxStack(invItem);
                    int toPlace = Math.Min(remaining, maxStack);
                    invItem.stack = toPlace;
                    remaining -= toPlace;
                }

                if (remaining > 0)
                {
                    _log.Warn($"[Shimmer] Inventory full - {remaining}x items could not be placed");
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                _log.Error($"[Shimmer] Direct placement failed: {ex.Message}");
                return false;
            }
        }

        // GetLocalPlayer() is provided by ReflectionCache.GetLocalPlayer()

        /// <summary>
        /// Get item name by ID (uses cache).
        /// Returns empty string if name cannot be resolved (icon will show instead).
        /// </summary>
        private string GetItemName(int itemId)
        {
            if (_itemNames.TryGetValue(itemId, out string name))
                return name;

            try
            {
                // Create temporary item via ReflectionCache and get its name
                var tempItem = ReflectionCache.CreateItem(itemId);
                name = tempItem != null ? ReflectionCache.GetItemName(tempItem) : "";

                _itemNames[itemId] = name;
                return name;
            }
            catch (Exception ex)
            {
                _log.Debug($"[Shimmer] GetItemName({itemId}) failed: {ex.Message}");
                name = "";
                _itemNames[itemId] = name;
                return name;
            }
        }

        /// <summary>
        /// Get total count of an item type in storage.
        /// Uses a per-frame cache to avoid calling GetAllItems() for every item.
        /// </summary>
        private int GetStorageCount(int itemId)
        {
            if (!_storageCountCacheValid)
            {
                // Build cache from all items once per frame
                if (_storageCountCache == null)
                    _storageCountCache = new Dictionary<int, int>();
                else
                    _storageCountCache.Clear();

                var allItems = _rangeCalc != null
                    ? _rangeCalc.FilterItemsByRange(_storage.GetAllItems())
                    : _storage.GetAllItems();
                foreach (var item in allItems)
                {
                    if (item.IsEmpty) continue;
                    if (_storageCountCache.ContainsKey(item.ItemId))
                        _storageCountCache[item.ItemId] += item.Stack;
                    else
                        _storageCountCache[item.ItemId] = item.Stack;
                }
                _storageCountCacheValid = true;
            }

            return _storageCountCache.TryGetValue(itemId, out int count) ? count : 0;
        }

        private bool IsBlocked(ItemSnapshot item)
        {
            long key = ((long)item.ItemId << 16) | (long)(item.Prefix & 0xFFFF);
            return _blockedItems.Contains(key);
        }

        private void ToggleBlocked(ItemSnapshot item)
        {
            long key = ((long)item.ItemId << 16) | (long)(item.Prefix & 0xFFFF);
            if (_blockedItems.Contains(key))
            {
                _blockedItems.Remove(key);
                _log.Debug($"Unblocked {item.Name} (prefix {item.Prefix}) from shimmer");
            }
            else
            {
                _blockedItems.Add(key);
                _log.Debug($"Blocked {item.Name} (prefix {item.Prefix}) from shimmer");
            }
        }

        private void RefreshItems()
        {
            _shimmerableGroups.Clear();

            // Get items in range from storage
            var allItems = _rangeCalc != null
                ? _rangeCalc.FilterItemsByRange(_storage.GetAllItems())
                : _storage.GetAllItems();
            _log.Debug($"[Shimmer] RefreshItems: {allItems.Count} in-range items from storage");

            // Group items by ItemId
            var groupDict = new Dictionary<int, ShimmerableItemGroup>();

            foreach (var item in allItems)
            {
                if (item.IsEmpty) continue;
                if (!CanShimmer(item.ItemId)) continue;

                if (!groupDict.TryGetValue(item.ItemId, out var group))
                {
                    group = new ShimmerableItemGroup
                    {
                        ItemId = item.ItemId,
                        ItemName = item.Name,
                        Instances = new List<ItemSnapshot>()
                    };
                    groupDict[item.ItemId] = group;
                }
                group.Instances.Add(item);
            }

            // Convert to list and sort by name
            foreach (var group in groupDict.Values)
            {
                // Sort instances by stack size (descending)
                group.Instances.Sort((a, b) => b.Stack.CompareTo(a.Stack));
                _shimmerableGroups.Add(group);
            }

            // Sort groups by name
            _shimmerableGroups.Sort((a, b) => string.Compare(a.ItemName, b.ItemName, StringComparison.OrdinalIgnoreCase));

            _log.Debug($"[Shimmer] Found {_shimmerableGroups.Count} shimmerable item types");
            FilterItems();
        }

        private void FilterItems()
        {
            string search = _searchBar.Text.ToLower();

            if (string.IsNullOrEmpty(search))
            {
                _filteredGroups = new List<ShimmerableItemGroup>(_shimmerableGroups);
            }
            else
            {
                _filteredGroups = new List<ShimmerableItemGroup>();
                foreach (var group in _shimmerableGroups)
                {
                    if (group.ItemName.ToLower().Contains(search))
                    {
                        _filteredGroups.Add(group);
                    }
                }
            }

            // Reset selection if out of bounds
            if (_selectedGroupIndex >= _filteredGroups.Count)
            {
                _selectedGroupIndex = -1;
                _selectedInstanceIndex = -1;
            }
        }

        /// <summary>
        /// Helper class to group items by type.
        /// </summary>
        private class ShimmerableItemGroup
        {
            public int ItemId;
            public string ItemName;
            public List<ItemSnapshot> Instances;
        }
    }
}
