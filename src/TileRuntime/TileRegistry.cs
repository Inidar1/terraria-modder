using System;
using System.Collections.Generic;
using System.Linq;
using TerrariaModder.Core.Logging;

namespace TerrariaModder.TileRuntime
{
    /// <summary>
    /// Shared process-wide registry for custom tile definitions and deterministic runtime IDs.
    /// </summary>
    internal static class TileRegistry
    {
        private static ILogger _log;

        private static readonly Dictionary<string, TileDefinition> _definitions =
            new Dictionary<string, TileDefinition>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, List<string>> _modTiles =
            new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, string> _modFolders =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, int> _idToType =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<int, string> _typeToId =
            new Dictionary<int, string>();
        private static readonly Dictionary<int, TileDefinition> _typeToDefinition =
            new Dictionary<int, TileDefinition>();

        private static Dictionary<string, int> _vanillaNameCache;

        public static int VanillaTileCount { get; private set; }
        public static bool TypesAssigned { get; private set; }
        public static int Count => _definitions.Count;
        public static IEnumerable<string> AllIds => _definitions.Keys;

        public static void Initialize(ILogger logger)
        {
            _log = logger;
            VanillaTileCount = ReadVanillaTileCount();
        }

        public static bool Register(string modId, string tileName, TileDefinition definition)
        {
            if (string.IsNullOrWhiteSpace(modId) || string.IsNullOrWhiteSpace(tileName) || definition == null)
            {
                _log?.Warn("[TileRuntime.TileRegistry] Invalid registration");
                return false;
            }

            string error = definition.Validate();
            if (error != null)
            {
                _log?.Warn($"[TileRuntime.TileRegistry] Validation failed for {modId}:{tileName}: {error}");
                return false;
            }

            if (TypesAssigned)
            {
                _log?.Warn($"[TileRuntime.TileRegistry] Cannot register {modId}:{tileName}: types already assigned");
                return false;
            }

            string fullId = BuildFullId(modId, tileName);
            if (_definitions.ContainsKey(fullId))
            {
                _log?.Warn($"[TileRuntime.TileRegistry] Duplicate tile registration: {fullId}");
                return false;
            }

            _definitions[fullId] = definition;

            if (!_modTiles.TryGetValue(modId, out var list))
            {
                list = new List<string>();
                _modTiles[modId] = list;
            }

            list.Add(tileName);
            _log?.Info($"[TileRuntime.TileRegistry] Registered: {fullId}");
            return true;
        }

        public static void RegisterModFolder(string modId, string folderPath)
        {
            if (string.IsNullOrWhiteSpace(modId) || string.IsNullOrWhiteSpace(folderPath))
                return;

            _modFolders[modId] = folderPath;
        }

        public static void AssignRuntimeTypes()
        {
            if (TypesAssigned)
                return;

            _idToType.Clear();
            _typeToId.Clear();
            _typeToDefinition.Clear();

            if (_definitions.Count == 0)
            {
                TypesAssigned = true;
                _log?.Info("[TileRuntime.TileRegistry] No custom tiles registered");
                return;
            }

            var sortedIds = _definitions.Keys.OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToList();
            int maxType = VanillaTileCount + sortedIds.Count - 1;
            if (maxType > ushort.MaxValue)
            {
                _log?.Error($"[TileRuntime.TileRegistry] Custom tile range exceeds ushort max ({maxType} > {ushort.MaxValue})");
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
            _log?.Info($"[TileRuntime.TileRegistry] Assigned {sortedIds.Count} runtime tile IDs ({VanillaTileCount} - {maxType})");
        }

        public static int ResolveTileType(string tileRef)
        {
            if (string.IsNullOrWhiteSpace(tileRef))
                return -1;

            int customType = GetRuntimeType(tileRef);
            if (customType >= 0)
                return customType;

            if (int.TryParse(tileRef, out int directId) && directId >= 0)
                return directId;

            return ResolveVanillaTileName(tileRef);
        }

        public static int GetRuntimeType(string fullId)
        {
            return _idToType.TryGetValue(fullId, out int tileType) ? tileType : -1;
        }

        public static bool TryGetRuntimeType(string fullId, out int tileType)
        {
            return _idToType.TryGetValue(fullId, out tileType);
        }

        public static string GetFullId(int runtimeType)
        {
            return _typeToId.TryGetValue(runtimeType, out var fullId) ? fullId : null;
        }

        public static TileDefinition GetDefinition(int runtimeType)
        {
            return _typeToDefinition.TryGetValue(runtimeType, out var definition) ? definition : null;
        }

        public static TileDefinition GetDefinitionById(string fullId)
        {
            return _definitions.TryGetValue(fullId, out var definition) ? definition : null;
        }

        public static string GetModFolder(string modId)
        {
            return _modFolders.TryGetValue(modId, out var folderPath) ? folderPath : null;
        }

        public static bool IsCustomTile(int tileType)
        {
            return tileType >= VanillaTileCount && _typeToId.ContainsKey(tileType);
        }

        public static IReadOnlyList<string> GetTilesForMod(string modId)
        {
            return _modTiles.TryGetValue(modId, out var list)
                ? list.ToList()
                : Array.Empty<string>();
        }

        public static void Reset()
        {
            _definitions.Clear();
            _modTiles.Clear();
            _modFolders.Clear();
            _idToType.Clear();
            _typeToId.Clear();
            _typeToDefinition.Clear();
            _vanillaNameCache = null;
            TypesAssigned = false;
        }

        private static string BuildFullId(string modId, string tileName)
        {
            return modId + ":" + tileName;
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
                            ushort value = (ushort)field.GetValue(null);
                            _vanillaNameCache[field.Name] = value;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _log?.Warn($"[TileRuntime.TileRegistry] Failed to build vanilla tile cache: {ex.Message}");
                }
            }

            if (_vanillaNameCache.TryGetValue(name, out int id))
                return id;

            string noSpaces = name.Replace(" ", "");
            return _vanillaNameCache.TryGetValue(noSpaces, out id) ? id : -1;
        }

        private static int ReadVanillaTileCount()
        {
            try
            {
                var countField = typeof(Terraria.ID.TileID).GetField("Count", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                if (countField != null)
                {
                    object value = countField.GetValue(null);
                    if (value is short shortValue) return shortValue;
                    if (value is int intValue) return intValue;
                    if (value is ushort ushortValue) return ushortValue;
                }
            }
            catch (Exception ex)
            {
                _log?.Warn($"[TileRuntime.TileRegistry] Failed to read TileID.Count: {ex.Message}");
            }

            return 692;
        }
    }
}
