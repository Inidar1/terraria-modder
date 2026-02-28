# Shared Tile Runtime Usage

## Goal

Use `TerrariaModder.TileRuntime` directly from a mod so custom tile registration does not depend on any Core tile-specific patches or API additions.

This is the path intended for running against a fresh upstream `TerrariaModder.Core`.

For a short migration checklist, see [tile-runtime-migration.md](tile-runtime-migration.md).

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

This runtime API is the supported path for running custom tiles on a fresh upstream Core checkout.
