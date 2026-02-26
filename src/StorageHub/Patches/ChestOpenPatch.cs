using System;
using System.Reflection;
using TerrariaModder.Core.Logging;
using StorageHub.Storage;
using StorageHub.Utils;

namespace StorageHub.Patches
{
    /// <summary>
    /// Detects when the player opens a chest and registers it.
    ///
    /// APPROACH: Instead of Harmony patching (which requires finding exact methods),
    /// we poll the player.chest field each frame and detect changes.
    /// This is simpler and more robust to Terraria version changes.
    ///
    /// When player.chest changes to a positive value (chest index), we register that chest.
    ///
    /// Why manual open is required:
    /// - Prevents accessing world-gen loot chests remotely (cheating)
    /// - Player must physically find and open the chest first
    /// - Natural gameplay - registering chests you already use
    /// </summary>
    public class ChestOpenDetector
    {
        private readonly ILogger _log;
        private readonly ChestRegistry _registry;

        // Reflection cache
        private static Type _mainType;
        private static Type _chestType;
        private static FieldInfo _playerArrayField;
        private static FieldInfo _myPlayerField;
        private static FieldInfo _chestArrayField;
        private static FieldInfo _tileArrayField;
        private static FieldInfo _playerInventoryField;
        private static FieldInfo _playerChestField;
        private static FieldInfo _chestXField;
        private static FieldInfo _chestYField;
        private static FieldInfo _tileTypeField;
        private static MethodInfo _tileActiveMethod;

        // State tracking
        private int _lastChestIndex = -1;
        private bool _initialized = false;
        private bool _dedicatedBlocksOnly;
        private int _requiredTileType = -1;

        public ChestOpenDetector(ILogger log, ChestRegistry registry)
        {
            _log = log;
            _registry = registry;
        }

        public void Initialize()
        {
            try
            {
                _mainType = Type.GetType("Terraria.Main, Terraria")
                    ?? Assembly.Load("Terraria").GetType("Terraria.Main");
                _chestType = Type.GetType("Terraria.Chest, Terraria")
                    ?? Assembly.Load("Terraria").GetType("Terraria.Chest");

                if (_mainType != null)
                {
                    _playerArrayField = _mainType.GetField("player", BindingFlags.Public | BindingFlags.Static);
                    _myPlayerField = _mainType.GetField("myPlayer", BindingFlags.Public | BindingFlags.Static);
                    _chestArrayField = _mainType.GetField("chest", BindingFlags.Public | BindingFlags.Static);
                    _tileArrayField = _mainType.GetField("tile", BindingFlags.Public | BindingFlags.Static);
                    _playerInventoryField = _mainType.GetField("playerInventory", BindingFlags.Public | BindingFlags.Static);
                }

                var playerType = Type.GetType("Terraria.Player, Terraria")
                    ?? Assembly.Load("Terraria").GetType("Terraria.Player");

                if (playerType != null)
                {
                    _playerChestField = playerType.GetField("chest", BindingFlags.Public | BindingFlags.Instance);
                }

                if (_chestType != null)
                {
                    _chestXField = _chestType.GetField("x", BindingFlags.Public | BindingFlags.Instance);
                    _chestYField = _chestType.GetField("y", BindingFlags.Public | BindingFlags.Instance);
                }

                var tileType = Type.GetType("Terraria.Tile, Terraria")
                    ?? Assembly.Load("Terraria").GetType("Terraria.Tile");
                if (tileType != null)
                {
                    _tileTypeField = tileType.GetField("type", BindingFlags.Public | BindingFlags.Instance);
                    _tileActiveMethod = tileType.GetMethod("active", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                }

                _initialized = _playerArrayField != null && _myPlayerField != null &&
                               _playerChestField != null && _chestArrayField != null &&
                               _chestXField != null && _chestYField != null;

                if (_initialized)
                {
                    _log.Debug("ChestOpenDetector initialized");
                }
                else
                {
                    _log.Warn("ChestOpenDetector: some reflection fields not found");
                }
            }
            catch (Exception ex)
            {
                _log.Error($"ChestOpenDetector init failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Called each frame to detect chest opens.
        /// Should be called from FrameEvents.OnPreUpdate.
        /// </summary>
        public void Update()
        {
            if (!_initialized) return;

            try
            {
                int currentChest = GetPlayerChestIndex();

                // Detect change from closed/other to a chest
                if (currentChest != _lastChestIndex)
                {
                    if (currentChest >= 0)
                    {
                        // Player opened a chest (not a bank)
                        OnChestOpened(currentChest);
                    }
                    _lastChestIndex = currentChest;
                }
            }
            catch (Exception ex)
            {
                _log.Error($"ChestOpenDetector update failed: {ex.Message}");
            }
        }

        private int GetPlayerChestIndex()
        {
            try
            {
                if (_myPlayerField == null || _playerArrayField == null || _playerChestField == null)
                    return -1;

                var myPlayerVal = _myPlayerField.GetValue(null);
                if (myPlayerVal == null) return -1;

                int myPlayer = (int)myPlayerVal;
                var players = _playerArrayField.GetValue(null) as Array;
                if (players == null) return -1;

                // Bounds check before array access
                if (myPlayer < 0 || myPlayer >= players.Length)
                    return -1;

                var player = players.GetValue(myPlayer);
                if (player == null) return -1;

                var chestVal = _playerChestField.GetValue(player);
                if (chestVal == null) return -1;

                return (int)chestVal;
            }
            catch
            {
                return -1;
            }
        }

        private void OnChestOpened(int chestIndex)
        {
            try
            {
                if (_chestArrayField == null || _chestXField == null || _chestYField == null)
                    return;

                var chests = _chestArrayField.GetValue(null) as Array;
                if (chests == null || chestIndex < 0 || chestIndex >= chests.Length)
                    return;

                var chest = chests.GetValue(chestIndex);
                if (chest == null) return;

                var xVal = _chestXField.GetValue(chest);
                var yVal = _chestYField.GetValue(chest);
                if (xVal == null || yVal == null) return;

                int x = (int)xVal;
                int y = (int)yVal;

                // Check if it's a valid chest position
                if (x < 0 || y < 0) return;

                // Check if locked (we shouldn't register locked chests)
                // For now, assume if player can open it, it's not locked
                // Terraria prevents opening locked chests anyway

                // Register the chest
                if (_dedicatedBlocksOnly)
                {
                    int tileType = GetTileTypeAt(x, y);
                    if (_requiredTileType >= 0 && tileType == _requiredTileType)
                    {
                        CloseCurrentChest();
                        GameText.Show("Storage Units cannot be opened directly. Use Storage Core or Storage Access.");
                        _log.Debug($"Blocked direct Storage Unit open at ({x}, {y})");
                    }

                    // Dedicated block mode uses network scans from heart/access interactions,
                    // not manual chest registration.
                    return;
                }

                if (_registry.RegisterChest(x, y))
                {
                    _log.Debug($"Registered chest at ({x}, {y}) - index {chestIndex}");
                }
            }
            catch (Exception ex)
            {
                _log.Error($"OnChestOpened failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Reset state when world unloads.
        /// </summary>
        public void Reset()
        {
            _lastChestIndex = -1;
        }

        public void SetDedicatedMode(bool dedicatedBlocksOnly, int requiredTileType)
        {
            _dedicatedBlocksOnly = dedicatedBlocksOnly;
            _requiredTileType = requiredTileType;
            _log.Debug($"ChestOpenDetector dedicated mode: enabled={_dedicatedBlocksOnly}, tileType={_requiredTileType}");
        }

        private int GetTileTypeAt(int x, int y)
        {
            try
            {
                if (_tileArrayField == null || _tileTypeField == null)
                    return -1;

                var tiles = _tileArrayField.GetValue(null) as Array;
                if (tiles == null) return -1;

                object tile = tiles.GetValue(x, y);
                if (tile == null) return -1;

                if (_tileActiveMethod != null)
                {
                    object activeObj = _tileActiveMethod.Invoke(tile, null);
                    if (activeObj is bool active && !active)
                        return -1;
                }

                var typeVal = _tileTypeField.GetValue(tile);
                if (typeVal == null) return -1;

                return Convert.ToInt32(typeVal);
            }
            catch
            {
                return -1;
            }
        }

        private void CloseCurrentChest()
        {
            try
            {
                if (_myPlayerField == null || _playerArrayField == null || _playerChestField == null)
                    return;

                var myPlayerVal = _myPlayerField.GetValue(null);
                if (myPlayerVal == null) return;

                int myPlayer = (int)myPlayerVal;
                var players = _playerArrayField.GetValue(null) as Array;
                if (players == null || myPlayer < 0 || myPlayer >= players.Length)
                    return;

                var player = players.GetValue(myPlayer);
                if (player == null) return;

                _playerChestField.SetValue(player, -1);
                _playerInventoryField?.SetValue(null, false);
                _lastChestIndex = -1;
            }
            catch
            {
                // Best effort close.
            }
        }
    }
}
