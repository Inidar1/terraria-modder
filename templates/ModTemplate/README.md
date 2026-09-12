# Mod Template

This is a template for creating new TerrariaModder mods.

## Quick Start

1. **Copy this folder** to `src/YourModName/`

2. **Rename files and update references:**
   - Rename `ModTemplate.csproj` to `YourModName.csproj`
   - Update `<AssemblyName>` and `<RootNamespace>` in the .csproj

3. **Update manifest.json:**
   - Change `id` to a unique lowercase identifier (e.g., "my-awesome-mod")
   - Change `name` to your mod's display name
   - Change `author` to your name
   - Update `description`
   - Change `entry_dll` to match your assembly name (e.g., "YourModName.dll")

4. **Update Mod.cs:**
   - Update the namespace to match your mod name
   - Implement your mod's functionality
   - Keep `ModBase, IModLifecycle` if you use the included content/world lifecycle methods

5. **Add to solution:**
   ```bash
   dotnet sln add src/YourModName/YourModName.csproj
   ```

6. **Build and test:**
   ```bash
   dotnet build src/YourModName/YourModName.csproj -c Release
   ```

## Features Included

### Configuration
The template includes a basic config schema with:
- `enabled` (bool) - Toggle mod on/off
- `exampleNumber` (int) - Example number setting
- `exampleFloat` (float) - Example decimal setting

Edit the `MyModConfig` properties and attributes in `Mod.cs` to add, remove, or modify config options. Config is accessible in-game through the Mod Menu (F6).

### Keybinds
One keybind is pre-configured:
- `toggle` (F7) - Toggle the feature on/off

Add more in `manifest.json` under `keybinds` and register them in `Initialize()`.

### Events
Common event subscriptions are included but commented out. Uncomment what you need:
- `GameEvents` - World load/unload, day/night
- `FrameEvents` - Per-frame updates
- `PlayerEvents` - Spawn, death, buffs
- `NPCEvents` - NPC/boss spawn and death

### Harmony Patching
The project includes a reference to 0Harmony for runtime method patching. See the bundled mods (SkipIntro, WhipStacking) for examples of Harmony prefix/postfix patterns.

### Hot Reload
The template implements `OnConfigChanged()` so config changes from the mod menu take effect immediately without restart. This method is optional — remove it if your mod requires a restart for config changes.

## File Structure

```
YourModName/
├── YourModName.csproj  # Project file
├── manifest.json       # Mod manifest (id, name, config, keybinds)
├── Mod.cs              # Main mod class
├── icon.png            # Optional: mod icon (shown in panel headers)
└── README.md           # This file (can delete)
```

## Tips

- **Logging**: Use `_log.Info()`, `_log.Debug()`, `_log.Warn()`, `_log.Error()`
- **Config**: Define a `ModConfig` subclass and access it with `_context.GetConfig<T>()`
- **Terraria APIs**: Add direct Terraria/XNA references when your mod needs public game types; use reflection only for inaccessible members or assembly boundaries
- **Performance**: Avoid heavy work in `OnPostUpdate` - it runs every frame
- **Cleanup**: Always unsubscribe from events in `Unload()` to prevent memory leaks

## API Reference

See the main documentation for full API details:
- `ModBase` and `IModLifecycle`
- `ModContext` services
- Event system
- Config system
- Keybind system
- Harmony patching
