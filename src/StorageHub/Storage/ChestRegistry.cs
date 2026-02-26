using System;
using System.Collections.Generic;
using StorageHub.Config;
using TerrariaModder.Core.Logging;

namespace StorageHub.Storage
{
    /// <summary>
    /// Tracks which chests have been registered (opened) by the player.
    ///
    /// DESIGN RATIONALE - Why manual chest open is required:
    ///
    /// Without this, players could remotely access world-gen loot chests they've
    /// never found (shadow chests, dungeon chests, etc.). That would be cheating.
    ///
    /// Solution: First manual open = registered. Now it's in your network.
    ///
    /// Why this works:
    /// - Prevents cheating (can't remote-loot shadow chests without finding them)
    /// - Natural gameplay (you're registering chests you already use)
    /// - No extra work for player (just play normally)
    ///
    /// Exclusions:
    /// - Locked chests (dungeon/biome) - requires key, player hasn't "earned" access
    /// - Trapped chests/mimics - not real storage
    ///
    /// Persistence:
    /// - Stored in StorageHubConfig per character per world
    /// - Survives game restart
    /// - Chest removal (breaking) unregisters it
    /// </summary>
    public class ChestRegistry
    {
        private readonly ILogger _log;
        private readonly StorageHubConfig _config;

        // Fast lookup by position
        private HashSet<(int x, int y)> _registeredPositions = new HashSet<(int x, int y)>();
        private HashSet<(int x, int y)> _temporaryOverridePositions;

        // Singleton for access from Harmony patches
        private static ChestRegistry _instance;
        public static ChestRegistry Instance => _instance;

        /// <summary>
        /// Clear static singleton reference on world unload to prevent stale references.
        /// </summary>
        public static void ClearInstance() => _instance = null;

        public ChestRegistry(ILogger log, StorageHubConfig config)
        {
            _log = log;
            _config = config;
            _instance = this;
        }

        /// <summary>
        /// Load registered chests from config.
        /// Called when world loads.
        /// </summary>
        public void LoadFromConfig()
        {
            _registeredPositions.Clear();
            int configCount = _config.RegisteredChests.Count;
            foreach (var chest in _config.RegisteredChests)
            {
                _registeredPositions.Add((chest.X, chest.Y));
                _log.Debug($"[Registry] Loaded chest at ({chest.X}, {chest.Y})");
            }
            _log.Debug($"[Registry] LoadFromConfig: config had {configCount} chests, loaded {_registeredPositions.Count}");
        }

        /// <summary>
        /// Save registered chests to config.
        /// Called when world unloads.
        /// </summary>
        public void SaveToConfig()
        {
            int beforeCount = _config.RegisteredChests.Count;
            _config.RegisteredChests.Clear();
            foreach (var pos in _registeredPositions)
            {
                _config.RegisteredChests.Add(new ChestPosition(pos.x, pos.y));
            }
            _log.Debug($"[Registry] SaveToConfig: {_registeredPositions.Count} positions -> config (was {beforeCount})");
        }

        /// <summary>
        /// Callback to save config when registrations change.
        /// Set this to persist changes immediately (survives force quit).
        /// </summary>
        public Action OnRegistrationChanged { get; set; }

        /// <summary>
        /// Register a chest by its position.
        /// Called from Harmony patch when player opens a chest.
        /// </summary>
        /// <param name="x">Chest X position in tiles.</param>
        /// <param name="y">Chest Y position in tiles.</param>
        /// <returns>True if newly registered, false if already registered.</returns>
        public bool RegisterChest(int x, int y)
        {
            if (_registeredPositions.Add((x, y)))
            {
                _log.Debug($"[Registry] NEW chest registered at ({x}, {y}), total: {_registeredPositions.Count}");
                // Save immediately so registration survives force quit
                SaveToConfig();
                OnRegistrationChanged?.Invoke();
                return true;
            }
            _log.Debug($"[Registry] Chest at ({x}, {y}) already registered");
            return false;
        }

        /// <summary>
        /// Unregister a chest by its position.
        /// Called when a chest is broken.
        /// </summary>
        public bool UnregisterChest(int x, int y)
        {
            if (_registeredPositions.Remove((x, y)))
            {
                _log.Debug($"Unregistered chest at ({x}, {y})");
                // Save immediately so unregistration survives force quit
                SaveToConfig();
                OnRegistrationChanged?.Invoke();
                return true;
            }
            return false;
        }

        /// <summary>
        /// Check if a chest at the given position is registered.
        /// </summary>
        public bool IsRegistered(int x, int y)
        {
            var active = _temporaryOverridePositions ?? _registeredPositions;
            return active.Contains((x, y));
        }

        /// <summary>
        /// Get all registered positions.
        /// </summary>
        public IEnumerable<(int x, int y)> GetRegisteredPositions()
        {
            return _temporaryOverridePositions ?? _registeredPositions;
        }

        /// <summary>
        /// Temporarily override the active storage positions used by readers.
        /// Does not mutate persistent registrations.
        /// </summary>
        public IDisposable UseTemporaryPositions(IEnumerable<(int x, int y)> positions)
        {
            var previous = _temporaryOverridePositions;
            _temporaryOverridePositions = positions != null
                ? new HashSet<(int x, int y)>(positions)
                : null;

            return new DisposableScope(() => _temporaryOverridePositions = previous);
        }

        /// <summary>
        /// Get the count of registered chests.
        /// </summary>
        public int Count => (_temporaryOverridePositions ?? _registeredPositions).Count;

        /// <summary>
        /// Replace all registrations with a new set of positions.
        /// Used by dedicated Storage Hub networks to mirror currently connected units.
        /// </summary>
        public bool ReplaceAll(IEnumerable<(int x, int y)> positions)
        {
            var next = new HashSet<(int x, int y)>();
            if (positions != null)
            {
                foreach (var pos in positions)
                    next.Add(pos);
            }

            if (_registeredPositions.SetEquals(next))
                return false;

            _registeredPositions = next;
            _log.Debug($"[Registry] Replaced registrations, total: {_registeredPositions.Count}");
            SaveToConfig();
            OnRegistrationChanged?.Invoke();
            return true;
        }

        /// <summary>
        /// Clear all registrations.
        /// Used when resetting or changing worlds.
        /// </summary>
        public void Clear()
        {
            _registeredPositions.Clear();
        }

        /// <summary>
        /// Validate registrations against current world chests.
        /// Removes registrations for chests that no longer exist.
        /// Called after world load to clean up stale entries.
        /// </summary>
        /// <param name="chestPositionValidator">Function that returns true if a chest exists at the given position.</param>
        public void ValidateRegistrations(System.Func<int, int, bool> chestPositionValidator)
        {
            var toRemove = new List<(int x, int y)>();

            foreach (var pos in _registeredPositions)
            {
                if (!chestPositionValidator(pos.x, pos.y))
                {
                    toRemove.Add(pos);
                }
            }

            foreach (var pos in toRemove)
            {
                _registeredPositions.Remove(pos);
                _log.Debug($"Removed stale registration at ({pos.x}, {pos.y})");
            }

            if (toRemove.Count > 0)
            {
                _log.Info($"Cleaned up {toRemove.Count} stale chest registrations");
            }
        }

        private sealed class DisposableScope : IDisposable
        {
            private readonly Action _onDispose;
            private bool _disposed;

            public DisposableScope(Action onDispose)
            {
                _onDispose = onDispose;
            }

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                _onDispose?.Invoke();
            }
        }
    }
}
