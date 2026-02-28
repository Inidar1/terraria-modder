using System;
using TerrariaModder.Core;

namespace TerrariaModder.TileRuntime
{
    /// <summary>
    /// Extension helpers for mods using the shared tile runtime.
    /// </summary>
    public static class TileRuntimeExtensions
    {
        public static TileRuntimeModContext UseTileRuntime(this ModContext context)
        {
            if (context == null)
                throw new ArgumentNullException(nameof(context));

            return new TileRuntimeModContext(context.Manifest.Id, context.ModFolder);
        }
    }
}
