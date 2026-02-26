using System;
using System.Collections.Generic;
using System.Reflection;
using TerrariaModder.Core.Assets;
using TerrariaModder.Core.Logging;

namespace StorageHub.DedicatedBlocks
{
    internal readonly struct StorageNetworkResult
    {
        public static readonly StorageNetworkResult Empty = new StorageNetworkResult(-1, -1, Array.Empty<(int x, int y)>());

        public int HeartX { get; }
        public int HeartY { get; }
        public IReadOnlyCollection<(int x, int y)> UnitPositions { get; }

        public bool HasHeart => HeartX >= 0 && HeartY >= 0;
        public int UnitCount => UnitPositions?.Count ?? 0;

        public StorageNetworkResult(int heartX, int heartY, IReadOnlyCollection<(int x, int y)> unitPositions)
        {
            HeartX = heartX;
            HeartY = heartY;
            UnitPositions = unitPositions ?? Array.Empty<(int x, int y)>();
        }
    }

    /// <summary>
    /// Resolves connected Storage Hub networks from a clicked heart/access/component tile.
    /// </summary>
    internal sealed class StorageNetworkResolver
    {
        private readonly ILogger _log;
        private readonly int _heartTileType;
        private readonly int _unitTileType;
        private readonly int _connectorTileType;
        private readonly HashSet<int> _componentTileTypes = new HashSet<int>();

        private static readonly (int x, int y)[] ComponentNeighborOffsets =
        {
            (-1, 0), (0, -1), (1, -1), (2, 0),
            (2, 1), (1, 2), (0, 2), (-1, 1)
        };

        private static readonly (int x, int y)[] ConnectorNeighborOffsets =
        {
            (-1, 0), (1, 0), (0, -1), (0, 1)
        };

        // Reflection cache
        private static bool _reflectionInitialized;
        private static Type _mainType;
        private static Type _tileType;
        private static FieldInfo _tileArrayField;
        private static FieldInfo _maxTilesXField;
        private static FieldInfo _maxTilesYField;
        private static FieldInfo _tileTypeField;
        private static MethodInfo _tileActiveMethod;
        private static PropertyInfo _tileHasTileProperty;

        private readonly struct NodeKey
        {
            public readonly int X;
            public readonly int Y;
            public readonly bool IsConnector;

            public NodeKey(int x, int y, bool isConnector)
            {
                X = x;
                Y = y;
                IsConnector = isConnector;
            }
        }

        public StorageNetworkResolver(
            ILogger log,
            int heartTileType,
            int unitTileType,
            int componentTileType,
            int connectorTileType,
            int accessTileType,
            int craftingAccessTileType)
        {
            _log = log;
            _heartTileType = heartTileType;
            _unitTileType = unitTileType;
            _connectorTileType = connectorTileType;

            if (heartTileType >= 0) _componentTileTypes.Add(heartTileType);
            if (unitTileType >= 0) _componentTileTypes.Add(unitTileType);
            if (componentTileType >= 0) _componentTileTypes.Add(componentTileType);
            if (accessTileType >= 0) _componentTileTypes.Add(accessTileType);
            if (craftingAccessTileType >= 0) _componentTileTypes.Add(craftingAccessTileType);

            if (!_reflectionInitialized)
            {
                InitializeReflection();
            }
        }

        public bool TryResolveNetwork(int tileX, int tileY, out StorageNetworkResult result)
        {
            result = StorageNetworkResult.Empty;

            try
            {
                if (!TryGetWorldContext(out var tiles, out int maxX, out int maxY))
                    return false;

                if (!TryGetTileTypeAt(tiles, maxX, maxY, tileX, tileY, out int clickedTileType))
                    return false;

                if (!TryCreateNodeFromTile(tileX, tileY, clickedTileType, out NodeKey start, out int startType))
                    return false;

                var exploredFromStart = ExploreConnectedNodes(tiles, maxX, maxY, start, startType);
                if (exploredFromStart.Count == 0)
                    return false;

                if (!TryPickHeart(exploredFromStart, start, tileX, tileY, out NodeKey heartNode, out int heartType))
                    return false;

                var exploredFromHeart = ExploreConnectedNodes(tiles, maxX, maxY, heartNode, heartType);
                var units = new List<(int x, int y)>();
                foreach (var pair in exploredFromHeart)
                {
                    NodeKey node = pair.Key;
                    if (node.IsConnector) continue;
                    if (pair.Value != _unitTileType) continue;
                    units.Add((node.X, node.Y));
                }

                result = new StorageNetworkResult(heartNode.X, heartNode.Y, units);
                return true;
            }
            catch (Exception ex)
            {
                _log?.Error($"StorageNetworkResolver failed: {ex.Message}");
                return false;
            }
        }

        private Dictionary<NodeKey, int> ExploreConnectedNodes(Array tiles, int maxX, int maxY, NodeKey start, int startType)
        {
            var visited = new Dictionary<NodeKey, int>();
            var queue = new Queue<NodeKey>();

            visited[start] = startType;
            queue.Enqueue(start);

            while (queue.Count > 0)
            {
                NodeKey current = queue.Dequeue();
                foreach (var adjacent in GetAdjacentTiles(current))
                {
                    int checkX = adjacent.x;
                    int checkY = adjacent.y;
                    if (!TryGetTileTypeAt(tiles, maxX, maxY, checkX, checkY, out int adjacentType))
                        continue;

                    if (!TryCreateNodeFromTile(checkX, checkY, adjacentType, out NodeKey nextNode, out int nextType))
                        continue;

                    if (visited.ContainsKey(nextNode))
                        continue;

                    visited[nextNode] = nextType;
                    queue.Enqueue(nextNode);
                }
            }

            return visited;
        }

        private IEnumerable<(int x, int y)> GetAdjacentTiles(NodeKey node)
        {
            var offsets = node.IsConnector ? ConnectorNeighborOffsets : ComponentNeighborOffsets;
            for (int i = 0; i < offsets.Length; i++)
            {
                yield return (node.X + offsets[i].x, node.Y + offsets[i].y);
            }
        }

        private bool TryPickHeart(
            Dictionary<NodeKey, int> visited,
            NodeKey start,
            int clickX,
            int clickY,
            out NodeKey heartNode,
            out int heartType)
        {
            heartNode = default;
            heartType = -1;

            if (visited.TryGetValue(start, out int startType) && !start.IsConnector && startType == _heartTileType)
            {
                heartNode = start;
                heartType = startType;
                return true;
            }

            bool foundHeart = false;
            long closestDistance = long.MaxValue;

            foreach (var pair in visited)
            {
                NodeKey node = pair.Key;
                int tileType = pair.Value;
                if (node.IsConnector) continue;
                if (tileType != _heartTileType) continue;

                long dx = node.X - clickX;
                long dy = node.Y - clickY;
                long distanceSq = dx * dx + dy * dy;

                if (!foundHeart || distanceSq < closestDistance)
                {
                    foundHeart = true;
                    closestDistance = distanceSq;
                    heartNode = node;
                    heartType = tileType;
                }
            }

            return foundHeart;
        }

        private bool TryCreateNodeFromTile(int tileX, int tileY, int tileType, out NodeKey node, out int normalizedType)
        {
            node = default;
            normalizedType = tileType;

            if (tileType == _connectorTileType)
            {
                node = new NodeKey(tileX, tileY, isConnector: true);
                return true;
            }

            if (!_componentTileTypes.Contains(tileType))
                return false;

            int topX = tileX;
            int topY = tileY;
            var definition = AssetSystem.GetTileDefinition(tileType);
            if (definition != null &&
                CustomTileContainers.TryGetTopLeft(tileX, tileY, definition, out int resolvedTopX, out int resolvedTopY))
            {
                topX = resolvedTopX;
                topY = resolvedTopY;
            }

            node = new NodeKey(topX, topY, isConnector: false);
            return true;
        }

        private static void InitializeReflection()
        {
            try
            {
                _mainType = Type.GetType("Terraria.Main, Terraria")
                    ?? Assembly.Load("Terraria").GetType("Terraria.Main");
                _tileType = Type.GetType("Terraria.Tile, Terraria")
                    ?? Assembly.Load("Terraria").GetType("Terraria.Tile");

                if (_mainType != null)
                {
                    _tileArrayField = _mainType.GetField("tile", BindingFlags.Public | BindingFlags.Static);
                    _maxTilesXField = _mainType.GetField("maxTilesX", BindingFlags.Public | BindingFlags.Static);
                    _maxTilesYField = _mainType.GetField("maxTilesY", BindingFlags.Public | BindingFlags.Static);
                }

                if (_tileType != null)
                {
                    _tileTypeField = _tileType.GetField("type", BindingFlags.Public | BindingFlags.Instance);
                    _tileActiveMethod = _tileType.GetMethod("active", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                    _tileHasTileProperty = _tileType.GetProperty("HasTile", BindingFlags.Public | BindingFlags.Instance);
                }
            }
            catch
            {
                // Reflection failures are handled by null checks in calls.
            }
            finally
            {
                _reflectionInitialized = true;
            }
        }

        private bool TryGetWorldContext(out Array tiles, out int maxX, out int maxY)
        {
            tiles = null;
            maxX = 0;
            maxY = 0;

            try
            {
                tiles = _tileArrayField?.GetValue(null) as Array;
                if (tiles == null)
                    return false;

                object maxXObj = _maxTilesXField?.GetValue(null);
                object maxYObj = _maxTilesYField?.GetValue(null);
                if (maxXObj == null || maxYObj == null)
                    return false;

                maxX = Convert.ToInt32(maxXObj);
                maxY = Convert.ToInt32(maxYObj);
                return maxX > 0 && maxY > 0;
            }
            catch
            {
                return false;
            }
        }

        private bool TryGetTileTypeAt(Array tiles, int maxX, int maxY, int x, int y, out int tileType)
        {
            tileType = -1;

            if (tiles == null || x < 0 || y < 0 || x >= maxX || y >= maxY)
                return false;

            try
            {
                object tile = tiles.GetValue(x, y);
                if (tile == null || !IsTileActive(tile))
                    return false;

                object typeVal = _tileTypeField?.GetValue(tile);
                if (typeVal == null)
                    return false;

                tileType = Convert.ToInt32(typeVal);
                return tileType > 0;
            }
            catch
            {
                return false;
            }
        }

        private bool IsTileActive(object tile)
        {
            try
            {
                if (_tileActiveMethod != null)
                {
                    object active = _tileActiveMethod.Invoke(tile, null);
                    if (active is bool activeBool)
                        return activeBool;
                }

                if (_tileHasTileProperty != null)
                {
                    object hasTile = _tileHasTileProperty.GetValue(tile, null);
                    if (hasTile is bool hasTileBool)
                        return hasTileBool;
                }

                object typeVal = _tileTypeField?.GetValue(tile);
                return typeVal != null && Convert.ToInt32(typeVal) > 0;
            }
            catch
            {
                return false;
            }
        }
    }
}

