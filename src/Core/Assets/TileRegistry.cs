using System;
using System.Collections.Generic;
using System.Linq;
using TerrariaModder.Core.Logging;

namespace TerrariaModder.Core.Assets
{
    /// <summary>
    /// Registry for custom tile definitions and deterministic runtime tile IDs.
    /// </summary>
    public static class TileRegistry
    {
        private static ILogger _log;

        // fullId ("modid:tile-name") -> definition
        private static readonly Dictionary<string, TileDefinition> _definitions
            = new Dictionary<string, TileDefinition>(StringComparer.OrdinalIgnoreCase);

        // per-mod listing
        private static readonly Dictionary<string, List<string>> _modTiles
            = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        // mod folder lookup for texture loading
        private static readonly Dictionary<string, string> _modFolders
            = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // runtime mappings
        private static readonly Dictionary<string, int> _idToType
            = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<int, string> _typeToId = new Dictionary<int, string>();
        private static readonly Dictionary<int, TileDefinition> _typeToDefinition = new Dictionary<int, TileDefinition>();

        private static Dictionary<string, int> _vanillaNameCache;

        public static int VanillaTileCount { get; private set; }
        public static bool TypesAssigned { get; private set; }
        public static int Count => _definitions.Count;
        public static IEnumerable<string> AllIds => _definitions.Keys;

        public static void Initialize(ILogger logger, int vanillaTileCount)
        {
            _log = logger;
            VanillaTileCount = vanillaTileCount;
        }

        public static bool Register(string modId, string tileName, TileDefinition definition)
        {
            if (string.IsNullOrWhiteSpace(modId) || string.IsNullOrWhiteSpace(tileName) || definition == null)
            {
                _log?.Warn("[TileRegistry] Invalid registration: null modId, tileName, or definition");
                return false;
            }

            string error = definition.Validate();
            if (error != null)
            {
                _log?.Warn($"[TileRegistry] Validation failed for {modId}:{tileName}: {error}");
                return false;
            }

            string fullId = $"{modId}:{tileName}";
            if (_definitions.ContainsKey(fullId))
            {
                _log?.Warn($"[TileRegistry] Duplicate tile: {fullId}");
                return false;
            }

            if (TypesAssigned)
            {
                _log?.Warn($"[TileRegistry] Cannot register {fullId}: types already assigned");
                return false;
            }

            _definitions[fullId] = definition;

            if (!_modTiles.TryGetValue(modId, out var list))
            {
                list = new List<string>();
                _modTiles[modId] = list;
            }
            list.Add(tileName);

            _log?.Debug($"[TileRegistry] Registered: {fullId} ({definition.DisplayName})");
            return true;
        }

        public static void RegisterModFolder(string modId, string folderPath)
        {
            _modFolders[modId] = folderPath;
        }

        public static string GetModFolder(string modId)
        {
            return _modFolders.TryGetValue(modId, out var path) ? path : null;
        }

        public static void AssignRuntimeTypes()
        {
            if (TypesAssigned) return;
            if (_definitions.Count == 0)
            {
                TypesAssigned = true;
                _log?.Info("[TileRegistry] No custom tiles registered");
                return;
            }

            _idToType.Clear();
            _typeToId.Clear();
            _typeToDefinition.Clear();

            var sortedIds = _definitions.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();
            int maxType = VanillaTileCount + sortedIds.Count - 1;
            if (maxType > ushort.MaxValue)
            {
                _log?.Error($"[TileRegistry] Custom tile range exceeds ushort max ({maxType} > {ushort.MaxValue})");
                TypesAssigned = true;
                return;
            }

            for (int i = 0; i < sortedIds.Count; i++)
            {
                int runtimeType = VanillaTileCount + i;
                string fullId = sortedIds[i];

                _idToType[fullId] = runtimeType;
                _typeToId[runtimeType] = fullId;
                _typeToDefinition[runtimeType] = _definitions[fullId];
            }

            TypesAssigned = true;
            _log?.Info($"[TileRegistry] Assigned {sortedIds.Count} runtime tile IDs ({VanillaTileCount} - {maxType})");
            foreach (var id in sortedIds)
                _log?.Debug($"[TileRegistry]   {id} -> tile {_idToType[id]}");
        }

        public static int GetRuntimeType(string fullId)
        {
            return _idToType.TryGetValue(fullId, out int type) ? type : -1;
        }

        public static string GetFullId(int runtimeType)
        {
            return _typeToId.TryGetValue(runtimeType, out string id) ? id : null;
        }

        public static TileDefinition GetDefinition(int runtimeType)
        {
            return _typeToDefinition.TryGetValue(runtimeType, out var def) ? def : null;
        }

        public static TileDefinition GetDefinitionById(string fullId)
        {
            return _definitions.TryGetValue(fullId, out var def) ? def : null;
        }

        public static bool IsCustomTile(int type)
        {
            return type >= VanillaTileCount && _typeToId.ContainsKey(type);
        }

        public static IEnumerable<string> GetTilesForMod(string modId)
        {
            return _modTiles.TryGetValue(modId, out var list)
                ? (IEnumerable<string>)list
                : Array.Empty<string>();
        }

        /// <summary>
        /// Resolve a tile reference string to a runtime tile ID.
        /// Supports:
        /// - "modid:tile-name" custom IDs
        /// - numeric IDs
        /// - vanilla TileID field names ("WorkBenches", "Containers", etc)
        /// </summary>
        public static int ResolveTileType(string tileRef)
        {
            if (string.IsNullOrWhiteSpace(tileRef))
                return -1;

            int customType = GetRuntimeType(tileRef);
            if (customType >= 0) return customType;

            if (int.TryParse(tileRef, out int directId) && directId >= 0)
                return directId;

            return ResolveVanillaTileName(tileRef);
        }

        private static int ResolveVanillaTileName(string name)
        {
            if (_vanillaNameCache == null)
            {
                _vanillaNameCache = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    var tileIdType = typeof(Terraria.ID.TileID);
                    foreach (var field in tileIdType.GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
                    {
                        if (field.FieldType == typeof(ushort))
                        {
                            ushort val = (ushort)field.GetValue(null);
                            _vanillaNameCache[field.Name] = val;
                        }
                    }
                    _log?.Debug($"[TileRegistry] Built vanilla tile cache: {_vanillaNameCache.Count} entries");
                }
                catch (Exception ex)
                {
                    _log?.Warn($"[TileRegistry] Failed to build vanilla tile cache: {ex.Message}");
                }
            }

            if (_vanillaNameCache.TryGetValue(name, out int id)) return id;

            string noSpaces = name.Replace(" ", "");
            if (_vanillaNameCache.TryGetValue(noSpaces, out id)) return id;

            return -1;
        }

        public static void Clear()
        {
            _definitions.Clear();
            _modTiles.Clear();
            _idToType.Clear();
            _typeToId.Clear();
            _typeToDefinition.Clear();
            _vanillaNameCache = null;
            TypesAssigned = false;
        }
    }
}
