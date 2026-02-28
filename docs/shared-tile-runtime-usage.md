# Shared Tile Runtime Usage

## Goal

Use `TerrariaModder.TileRuntime` directly from a mod so custom tile registration does not depend on any Core tile-specific patches or API additions.

This is the path intended for running against a fresh upstream `TerrariaModder.Core`.

## Runtime dependency

Your tile mod should depend on the runtime mod:

```json
{
  "dependencies": ["tile-runtime"]
}
```

## Project reference

Reference `TerrariaModder.TileRuntime` from your mod project in addition to `TerrariaModder.Core`.

## Registration pattern

Use the runtime helper from `Initialize(ModContext context)`:

```csharp
using TerrariaModder.Core;
using TerrariaModder.TileRuntime;

public class Mod : IMod
{
    public string Id => "example-mod";
    public string Name => "Example Mod";
    public string Version => "1.0.0";

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

    public void OnWorldLoad() { }
    public void OnWorldUnload() { }
    public void Unload() { }
}
```

## Notes

- `TexturePath` is resolved relative to the mod folder.
- The runtime bootstrap mod owns patch timing and tile ID assignment.
- Tile registration must happen during mod initialization, before `OnGameReady`.

## Zero-Core target

Once mods use this API directly, the temporary Core bridge added on this branch is no longer required.

The intended cleanup path is:

1. update tile mods to register through `TerrariaModder.TileRuntime`
2. switch back to a fresh upstream Core checkout
3. keep `tile-runtime` installed as a dependency mod
