using System.Collections.Generic;
using TerrariaModder.Core;

namespace TerrariaModder.TileRuntime
{
    /// <summary>
    /// Mod-facing helper that binds tile registration to a specific mod ID.
    /// This avoids relying on Core owning tile-specific helpers.
    /// </summary>
    public sealed class TileRuntimeModContext
    {
        private readonly string _modId;
        private readonly string _modFolder;

        internal TileRuntimeModContext(string modId, string modFolder)
        {
            _modId = modId;
            _modFolder = modFolder;
        }

        public bool RegisterTile(string tileName, TileDefinition definition)
        {
            TileRuntimeApi.RegisterModFolder(_modId, _modFolder);
            return TileRuntimeApi.RegisterTile(_modId, tileName, definition);
        }

        public IReadOnlyList<string> GetTiles()
        {
            return TileRuntimeApi.GetTilesForMod(_modId);
        }
    }
}
