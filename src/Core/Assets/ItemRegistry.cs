using System;
using System.Collections.Generic;
using System.Linq;
using TerrariaModder.Core.Logging;

namespace TerrariaModder.Core.Assets
{
    /// <summary>
    /// Central registry mapping string item IDs ("modid:itemname") to runtime type IDs (6145+).
    /// Runtime types are hash-derived within a range based on the native item count,
    /// with collision probing. Persist string identities rather than these runtime numbers.
    /// </summary>
    public static class ItemRegistry
    {
        private static ILogger _log;

        // Registered definitions: fullId ("modid:itemname") → definition
        private static readonly Dictionary<string, ItemDefinition> _definitions
            = new Dictionary<string, ItemDefinition>(StringComparer.OrdinalIgnoreCase);

        // Mod folder paths for texture loading
        private static readonly Dictionary<string, string> _modFolders
            = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Per-mod item lists for enumeration/unload
        private static readonly Dictionary<string, List<string>> _modItems
            = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        // Runtime type mappings (populated by AssignRuntimeTypes)
        private static readonly Dictionary<string, int> _idToType = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<int, string> _typeToId = new Dictionary<int, string>();
        private static readonly Dictionary<int, ItemDefinition> _typeToDefinition = new Dictionary<int, ItemDefinition>();

        // Alias → current fullId (built from ItemDefinition.Aliases in AssignRuntimeTypes)
        private static readonly Dictionary<string, string> _aliasToCurrentId
            = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // KnownUnknown types: type IDs for Optional mods the client doesn't have locally.
        // Populated from TypeIdManifest packet on join, cleared on disconnect.
        private static readonly Dictionary<int, string> _knownUnknowns = new Dictionary<int, string>();
        // Reverse lookup: fullId → typeId, for resolving server-sent item IDs to KnownUnknown types.
        private static readonly Dictionary<string, int> _knownUnknownsByFullId
            = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        /// <summary>First vanilla item count (before extension). Custom types start here.</summary>
        public static int VanillaItemCount { get; private set; }

        /// <summary>Whether runtime types have been assigned.</summary>
        public static bool TypesAssigned { get; private set; }

        /// <summary>Number of registered custom items.</summary>
        public static int Count => _definitions.Count;

        /// <summary>All registered full IDs.</summary>
        public static IEnumerable<string> AllIds => _definitions.Keys;

        public static void Initialize(ILogger logger, int vanillaItemCount)
        {
            _log = logger;
            VanillaItemCount = vanillaItemCount;
        }

        /// <summary>
        /// Register a custom item definition.
        /// Called by mods during Initialize() via ModContext.RegisterItem().
        /// </summary>
        public static bool Register(string modId, string itemName, ItemDefinition definition)
        {
            if (string.IsNullOrEmpty(modId) || string.IsNullOrEmpty(itemName) || definition == null)
            {
                _log?.Warn("[ItemRegistry] Invalid registration: null modId, itemName, or definition");
                return false;
            }

            string error = definition.Validate();
            if (error != null)
            {
                _log?.Warn($"[ItemRegistry] Validation failed for {modId}:{itemName}: {error}");
                return false;
            }

            string fullId = $"{modId}:{itemName}";

            if (_definitions.ContainsKey(fullId))
            {
                _log?.Warn($"[ItemRegistry] Duplicate item: {fullId}");
                return false;
            }

            if (TypesAssigned)
            {
                _log?.Warn($"[ItemRegistry] Cannot register {fullId}: types already assigned");
                return false;
            }

            _definitions[fullId] = definition;

            if (!_modItems.TryGetValue(modId, out var list))
            {
                list = new List<string>();
                _modItems[modId] = list;
            }
            list.Add(itemName);

            _log?.Debug($"[ItemRegistry] Registered: {fullId} ({definition.DisplayName})");
            return true;
        }

        /// <summary>
        /// Register a mod's folder path (for texture loading).
        /// </summary>
        public static void RegisterModFolder(string modId, string folderPath)
        {
            _modFolders[modId] = folderPath;
        }

        /// <summary>
        /// Assign deterministic runtime type IDs to all registered items.
        /// Called once after all mods have loaded.
        ///
        /// IDs are hash-derived (FNV-1a of the full item key) rather than sequential.
        /// This makes each item's ID independent of what other items are registered, so
        /// host and client agree on type numbers even when they have different mod subsets.
        ///
        /// Valid range: [VanillaItemCount, 32767) — arrays were extended to 32767 by TypeExtension.
        /// Collisions are extremely unlikely but handled via linear probing with a warning log.
        /// </summary>
        public static void AssignRuntimeTypes()
        {
            if (TypesAssigned) return;
            if (_definitions.Count == 0)
            {
                _log?.Info("[ItemRegistry] No custom items registered");
                TypesAssigned = true;
                return;
            }

            _idToType.Clear();
            _typeToId.Clear();
            _typeToDefinition.Clear();

            // Extended array upper bound (exclusive). TypeExtension defaults to 32767.
            // Valid indices: 0..32766, so custom items live in [VanillaItemCount, 32767).
            const int MaxType = 32767;
            int assignedCount = 0;
            int probeSteps = 0;
            int collidedItems = 0;

            foreach (string fullId in _definitions.Keys)
            {
                uint rawHash = FNV1a.Hash(fullId);
                int intended = FNV1a.ToRange(rawHash, VanillaItemCount, MaxType);
                int candidate = intended;

                // Resolve collisions: linear probe until a free slot is found
                while (_typeToId.ContainsKey(candidate))
                {
                    candidate++;
                    if (candidate >= MaxType) candidate = VanillaItemCount; // wrap
                    if (candidate == intended)
                    {
                        _log?.Error($"[ItemRegistry] No free type slot for {fullId} — custom item range exhausted");
                        candidate = -1;
                        break;
                    }
                    probeSteps++;
                }

                if (candidate < 0) continue;

                if (candidate != intended)
                {
                    collidedItems++;
                    // Name both conflicting items so the mod author knows what to rename
                    string incumbent = _typeToId.TryGetValue(intended, out var inc) ? inc : "(unknown)";
                    _log?.Warn($"[ItemRegistry] Hash collision: \"{incumbent}\" and \"{fullId}\" both targeted slot {intended} — \"{fullId}\" probe-resolved to {candidate}. Rename one item key to eliminate this.");
                }

                _idToType[fullId] = candidate;
                _typeToId[candidate] = fullId;
                _typeToDefinition[candidate] = _definitions[fullId];
                assignedCount++;
            }

            TypesAssigned = true;
            _log?.Info($"[ItemRegistry] Assigned {assignedCount} runtime types via FNV-1a hash in [{VanillaItemCount}, {MaxType})");
            if (collidedItems > 0)
                _log?.Warn($"[ItemRegistry] {collidedItems} hash collision(s) detected ({probeSteps} probe step(s)) — see above for details. Rename conflicting item keys to eliminate.");

            foreach (var kvp in _idToType)
                _log?.Debug($"[ItemRegistry]   {kvp.Key} → type {kvp.Value}");

            // Build alias reverse map: old fullId → current fullId
            _aliasToCurrentId.Clear();
            foreach (var kvp in _definitions)
            {
                var def = kvp.Value;
                if (def.Aliases == null || def.Aliases.Count == 0) continue;
                foreach (string alias in def.Aliases)
                {
                    if (string.IsNullOrEmpty(alias)) continue;
                    if (_aliasToCurrentId.ContainsKey(alias))
                        _log?.Warn($"[ItemRegistry] Duplicate alias \"{alias}\" — already mapped to \"{_aliasToCurrentId[alias]}\", ignoring second registration for \"{kvp.Key}\"");
                    else
                        _aliasToCurrentId[alias] = kvp.Key;
                }
            }
            if (_aliasToCurrentId.Count > 0)
                _log?.Info($"[ItemRegistry] Built alias map: {_aliasToCurrentId.Count} alias(es)");
        }

        // ── Lookups ──

        /// <summary>
        /// Get runtime type for a full item ID. Returns -1 if not found.
        /// If the canonical name fails, automatically checks the alias map (handles renamed items).
        /// </summary>
        public static int GetRuntimeType(string fullId)
        {
            if (_idToType.TryGetValue(fullId, out int type)) return type;
            // Check alias map — item may have been renamed
            if (_aliasToCurrentId.TryGetValue(fullId, out string currentId))
                return _idToType.TryGetValue(currentId, out type) ? type : -1;
            return -1;
        }

        /// <summary>Get full item ID for a runtime type. Returns null if not found.</summary>
        public static string GetFullId(int runtimeType)
        {
            return _typeToId.TryGetValue(runtimeType, out string id) ? id : null;
        }

        /// <summary>Persistence identity: existing vanilla:type convention or registered custom ID.</summary>
        public static string GetPersistentId(int runtimeType)
        {
            if (runtimeType > 0 && runtimeType < VanillaItemCount) return "vanilla:" + runtimeType.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return GetFullId(runtimeType) ?? (IsKnownUnknown(runtimeType, out string id) ? id : null);
        }

        /// <summary>Resolve a saved identity without accepting invalid or runtime-only numeric types.</summary>
        public static int ResolvePersistentId(string itemId)
        {
            if (string.IsNullOrEmpty(itemId)) return -1;
            if (itemId.StartsWith("vanilla:", StringComparison.Ordinal))
                return int.TryParse(itemId.Substring(8), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int type)
                    && type > 0 && type < VanillaItemCount ? type : -1;
            return GetRuntimeType(itemId);
        }

        /// <summary>Get definition for a runtime type. Returns null if not found.</summary>
        public static ItemDefinition GetDefinition(int runtimeType)
        {
            return _typeToDefinition.TryGetValue(runtimeType, out var def) ? def : null;
        }

        /// <summary>Get definition by full ID. Returns null if not found.</summary>
        public static ItemDefinition GetDefinitionById(string fullId)
        {
            return _definitions.TryGetValue(fullId, out var def) ? def : null;
        }

        /// <summary>Check if a runtime type is a custom item.</summary>
        public static bool IsCustomItem(int type) => type >= VanillaItemCount && _typeToId.ContainsKey(type);

        /// <summary>Returns true if the given mod has registered any custom items.</summary>
        public static bool HasCustomItems(string modId) =>
            !string.IsNullOrEmpty(modId) && _modItems.ContainsKey(modId);

        /// <summary>Get mod folder path for texture loading.</summary>
        public static string GetModFolder(string modId)
        {
            return _modFolders.TryGetValue(modId, out var path) ? path : null;
        }

        /// <summary>Get all item names registered by a specific mod.</summary>
        public static IEnumerable<string> GetItemsForMod(string modId)
        {
            return _modItems.TryGetValue(modId, out var list)
                ? (IEnumerable<string>)list
                : Array.Empty<string>();
        }

        /// <summary>
        /// Resolve an item reference to a runtime type.
        /// Accepts "modid:itemname" (custom) or vanilla item type ID (int).
        /// Returns -1 if unresolvable.
        /// </summary>
        public static int ResolveItemType(string itemRef)
        {
            if (string.IsNullOrEmpty(itemRef)) return -1;

            // Try as custom item first
            int customType = GetRuntimeType(itemRef);
            if (customType >= 0) return customType;

            // Try as vanilla int type
            if (int.TryParse(itemRef, out int vanillaType) && vanillaType > 0 && vanillaType < VanillaItemCount)
                return vanillaType;

            // Try resolving vanilla item name via reflection
            return ResolveVanillaItemName(itemRef);
        }

        private static Dictionary<string, int> _vanillaNameCache;

        private static int ResolveVanillaItemName(string name)
        {
            if (_vanillaNameCache == null)
            {
                _vanillaNameCache = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    var itemIdType = typeof(Terraria.ID.ItemID);
                    foreach (var field in itemIdType.GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
                    {
                        if (field.FieldType == typeof(short))
                        {
                            short val = (short)field.GetValue(null);
                            if (val > 0 && val < VanillaItemCount)
                                _vanillaNameCache[field.Name] = val;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _log?.Warn($"[ItemRegistry] Failed to build vanilla name cache: {ex.Message}");
                }
            }

            // Try exact match and common variations
            if (_vanillaNameCache.TryGetValue(name, out int id)) return id;

            // Try with spaces removed
            string noSpaces = name.Replace(" ", "");
            if (_vanillaNameCache.TryGetValue(noSpaces, out id)) return id;

            return -1;
        }

        /// <summary>Clear all registrations (for testing/reload).</summary>
        public static void Clear()
        {
            _definitions.Clear();
            _modItems.Clear();
            _idToType.Clear();
            _typeToId.Clear();
            _typeToDefinition.Clear();
            _aliasToCurrentId.Clear();
            _knownUnknowns.Clear();
            _knownUnknownsByFullId.Clear();
            TypesAssigned = false;
            _vanillaNameCache = null;
        }

        // ── KnownUnknowns (Phase I: heterogeneous mod sets) ──

        /// <summary>
        /// Register a type ID known to the server but not locally installed.
        /// Called when receiving TypeIdManifest packet from server.
        /// </summary>
        public static void AddKnownUnknown(int typeId, string fullId)
        {
            if (typeId < VanillaItemCount) return; // sanity: never replace vanilla
            if (IsCustomItem(typeId)) return;       // already known locally, skip
            _knownUnknowns[typeId] = fullId;
            if (!string.IsNullOrEmpty(fullId))
                _knownUnknownsByFullId[fullId] = typeId;
        }

        /// <summary>
        /// Reverse lookup: returns the KnownUnknown type ID for a given full item ID, or -1 if not found.
        /// Used to resolve server-sent item IDs (e.g. in CustomItemSync) to KnownUnknown type IDs.
        /// </summary>
        public static int GetKnownUnknownType(string fullId)
        {
            if (string.IsNullOrEmpty(fullId)) return -1;
            return _knownUnknownsByFullId.TryGetValue(fullId, out int typeId) ? typeId : -1;
        }

        /// <summary>
        /// Returns true if the type is a server-known Optional mod item the client doesn't have.
        /// </summary>
        public static bool IsKnownUnknown(int type, out string fullId)
        {
            return _knownUnknowns.TryGetValue(type, out fullId);
        }

        /// <summary>
        /// Clear all KnownUnknown entries. Called on disconnect.
        /// </summary>
        public static void ClearKnownUnknowns()
        {
            int count = _knownUnknowns.Count;
            _knownUnknowns.Clear();
            _knownUnknownsByFullId.Clear();
            if (count > 0)
                _log?.Info($"[ItemRegistry] Cleared {count} KnownUnknown type(s) on disconnect");
        }

        // ── IContentRegistry adapter ──

        /// <summary>
        /// IContentRegistry adapter wrapping the static ItemRegistry.
        /// Registered with ContentRegistry so the coordinator can call AssignRuntimeTypes()
        /// without knowing about ItemRegistry specifically.
        /// </summary>
        internal sealed class Adapter : IContentRegistry
        {
            void IContentRegistry.AssignRuntimeTypes() => ItemRegistry.AssignRuntimeTypes();
            int IContentRegistry.Count => ItemRegistry.Count;
            bool IContentRegistry.HasCustomContent(string modId) => ItemRegistry.HasCustomItems(modId);
        }
    }
}
