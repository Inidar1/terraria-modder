using System;
using System.Collections.Generic;
using TerrariaModder.Core.Logging;

namespace TerrariaModder.TileRuntime
{
    /// <summary>
    /// Shared tile runtime entry point.
    /// This is currently a skeleton for proving the bootstrap and bridge flow.
    /// </summary>
    public static class TileRuntimeApi
    {
        private static ILogger _log;
        private static bool _initialized;
        private static bool _registrationsFrozen;
        private static bool _patchesApplied;
        private static bool _texturesLoaded;
        private static bool _contentLoaded;
        private static int _textureRetryCount;
        private const int MaxTextureRetries = 300;

        public static bool IsInitialized => _initialized;
        public static bool RegistrationsFrozen => _registrationsFrozen;
        public static int ExtendedTileCount => TileTypeExtension.ExtendedCount;

        public static void Initialize(ILogger logger)
        {
            if (_initialized)
                return;

            _log = logger;
            TileRegistry.Initialize(logger);
            TileObjectRegistrar.Initialize(logger);
            TileTextureLoader.Initialize(logger);
            TileBehaviorPatches.Initialize(logger);
            PlayerAdjTileSafetyPatches.Initialize(logger);
            TileSavePatches.Initialize(logger);
            _initialized = true;
            _log?.Info("[TileRuntime] Initialized shared runtime skeleton");
        }

        public static bool RegisterTile(string modId, string tileName, TileDefinition definition)
        {
            if (!_initialized)
                throw new InvalidOperationException("TileRuntimeApi.Initialize must be called before registration.");

            if (_registrationsFrozen)
            {
                _log?.Warn($"[TileRuntime] Rejecting late tile registration {modId}:{tileName}");
                return false;
            }

            return TileRegistry.Register(modId, tileName, definition);
        }

        public static void RegisterModFolder(string modId, string modFolder)
        {
            if (!_initialized)
                throw new InvalidOperationException("TileRuntimeApi.Initialize must be called before registration.");

            TileRegistry.RegisterModFolder(modId, modFolder);
        }

        public static int ResolveTile(string tileRef)
        {
            return TileRegistry.ResolveTileType(tileRef);
        }

        public static bool TryGetTileType(string fullId, out int tileType)
        {
            return TileRegistry.TryGetRuntimeType(fullId, out tileType);
        }

        public static IReadOnlyList<string> GetTilesForMod(string modId)
        {
            return TileRegistry.GetTilesForMod(modId);
        }

        public static void OnGameReady()
        {
            if (!_initialized)
                return;

            if (_patchesApplied)
                return;

            _registrationsFrozen = true;
            TileRegistry.AssignRuntimeTypes();
            if (TileRegistry.Count > 0)
            {
                int newTileCount = TileRegistry.VanillaTileCount + TileRegistry.Count + 256;
                int result = TileTypeExtension.Apply(_log, newTileCount, failFast: true);
                if (result < 0)
                    throw new InvalidOperationException("TileRuntime TileTypeExtension failed");

                TileTextureLoader.ApplyPatches();
                TileObjectRegistrar.ApplyDefinitions();
                TileBehaviorPatches.ApplyPatches();
                PlayerAdjTileSafetyPatches.ApplyPatches();
                TileSavePatches.ApplyPatches();

                int injected = TileTextureLoader.InjectAllTextures();
                if (injected > 0)
                    _texturesLoaded = true;
            }
            _patchesApplied = true;

            if (_contentLoaded)
                OnContentLoaded();

            _log?.Info($"[TileRuntime] OnGameReady complete (tiles={TileRegistry.Count})");
        }

        public static void OnContentLoaded()
        {
            _contentLoaded = true;

            if (!_initialized || !_patchesApplied || _texturesLoaded)
                return;

            if (TileRegistry.Count == 0)
            {
                _texturesLoaded = true;
                return;
            }

            int injected = TileTextureLoader.InjectAllTextures();
            if (injected > 0)
                _texturesLoaded = true;
        }

        public static void OnUpdate()
        {
            if (!_initialized || !_patchesApplied)
                return;

            if (TileRegistry.Count > 0)
                TileObjectRegistrar.ApplyDefinitions();

            if (_texturesLoaded)
                return;

            _textureRetryCount++;
            if (_textureRetryCount % 10 == 0)
            {
                int injected = TileTextureLoader.InjectAllTextures();
                if (injected > 0)
                {
                    _texturesLoaded = true;
                    return;
                }

                TileTextureLoader.ReinjectCachedTextures();
            }

            if (_textureRetryCount >= MaxTextureRetries)
            {
                int injected = TileTextureLoader.InjectAllTextures();
                TileTextureLoader.ReinjectCachedTextures();
                _texturesLoaded = injected > 0;
            }
        }

        public static void ResetForTesting()
        {
            TileRegistry.Reset();
            _registrationsFrozen = false;
            _patchesApplied = false;
            _texturesLoaded = false;
            _contentLoaded = false;
            _textureRetryCount = 0;
        }
    }
}
