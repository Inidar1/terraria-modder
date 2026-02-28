# Tile Runtime Migration

## Goal

Move a tile mod off forked Core tile patches and onto the shared `tile-runtime` dependency mod.

## Required changes

1. Add `"tile-runtime"` to the mod manifest dependencies.
2. Reference `TerrariaModder.TileRuntime` from the mod project.
3. Replace `context.RegisterTile(...)` with `context.UseTileRuntime().RegisterTile(...)`.
4. Replace Core tile-type lookups with `TileRuntimeApi.ResolveTile(...)`.
5. Remove any use of fork-only Core APIs such as tile bridge helpers or `CreateTileId`-style item shortcuts.

## Minimal example

```csharp
using TerrariaModder.Core;
using TerrariaModder.TileRuntime;

public void Initialize(ModContext context)
{
    var tiles = context.UseTileRuntime();

    tiles.RegisterTile("example-tile", new TileDefinition
    {
        DisplayName = "Example Tile",
        TexturePath = @"Assets\example-tile.png",
        Width = 1,
        Height = 1,
        Solid = true,
        FrameImportant = true
    });
}
```

## Placement items

If a placeable item targets a runtime tile, resolve the tile type from the runtime instead of relying on forked Core item helpers:

```csharp
item.CreateTile = TileRuntimeApi.ResolveTile("example-mod:example-tile");
```

If those items may already exist in player inventory before world load finishes, refresh the live item instances after the runtime tile IDs are resolved.

## Packaging

- Ship the mod with a dependency on `tile-runtime`.
- Keep `tile-runtime` installed alongside the mod in `TerrariaModder/mods/tile-runtime/`.
- Do not bundle private copies of `TerrariaModder.TileRuntime.dll` into multiple unrelated mod folders.

## Verification checklist

1. Launch with a fresh upstream Core checkout.
2. Confirm the mod registers tiles through `tile-runtime`.
3. Confirm the log shows runtime tile ID assignment and tile texture injection.
4. Place, break, save, and reload each custom tile.
