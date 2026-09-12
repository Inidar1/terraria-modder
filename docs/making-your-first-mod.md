---
title: How to Make a Terraria 1.4.5.8 Mod - Beginner Tutorial
description: Learn how to create a Terraria 1.4.5.8 mod from scratch using TerrariaModder and Harmony. Step-by-step C# tutorial with project setup, config, keybinds, and UI.
nav_order: 5
---

# Making Your First Mod

This tutorial walks you through creating a simple TerrariaModder mod from scratch.

## Prerequisites

- **.NET Framework 4.8 Developer Pack** - [Download](https://dotnet.microsoft.com/download/dotnet-framework/net48) (recommended for offline builds; the .NET SDK can restore reference assemblies when package restore is available)
- **Visual Studio 2019+** or **VS Code** with C# extension
- **TerrariaModder** installed in your Terraria folder

## What We're Building

A simple mod that:
- Shows a message when you enter a world
- Has a configurable greeting message
- Has a keybind to show the message on demand

## Step 1: Create the Project Structure

Create a new folder for your mod:

```
src/MyFirstMod/
├── MyFirstMod.csproj
├── manifest.json
├── icon.png              <- Optional: mod icon (shown in UI headers)
└── Mod.cs
```

## Step 2: Create the Project File

Create `MyFirstMod.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net48</TargetFramework>
    <AssemblyName>MyFirstMod</AssemblyName>
    <RootNamespace>MyFirstMod</RootNamespace>
    <OutputType>Library</OutputType>
    <LangVersion>latest</LangVersion>
    <OutputPath>..\..\build\plugins\</OutputPath>
    <AppendTargetFrameworkToOutputPath>false</AppendTargetFrameworkToOutputPath>
    <AppendRuntimeIdentifierToOutputPath>false</AppendRuntimeIdentifierToOutputPath>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\Core\TerrariaModder.Core.csproj">
      <Private>false</Private>
    </ProjectReference>
  </ItemGroup>

  <ItemGroup>
    <Reference Include="Terraria">
      <HintPath>..\..\Terraria\Terraria.exe</HintPath>
      <Private>false</Private>
    </Reference>
    <Reference Include="0Harmony">
      <HintPath>..\..\Terraria\TerrariaModder\core\deps\0Harmony.dll</HintPath>
      <Private>false</Private>
    </Reference>
    <Reference Include="Microsoft.Xna.Framework.Game, Version=4.0.0.0, Culture=neutral, PublicKeyToken=842cf8be1de50553">
      <HintPath>C:\Windows\Microsoft.NET\assembly\GAC_32\Microsoft.Xna.Framework.Game\v4.0_4.0.0.0__842cf8be1de50553\Microsoft.Xna.Framework.Game.dll</HintPath>
      <Private>false</Private>
    </Reference>
  </ItemGroup>
</Project>
```

Key settings:
- `TargetFramework`: Must be `net48`
- `Private`: Set to `false` to avoid copying framework DLLs into your output
- `Terraria` reference: Allows using Terraria types directly (e.g., `Main.NewText()`). Optional; advanced mods can use reflection instead (see [Tested Patterns](tested-patterns.md#reflection-patterns))
- `0Harmony` reference: Required for Harmony patching

**Note:** HintPaths assume your mod is in `src/YourMod/` with `Terraria/` alongside `src/`. Adjust paths if your layout differs.

## Step 3: Create the Manifest

Create `manifest.json`:

```json
{
  "id": "my-first-mod",
  "name": "My First Mod",
  "version": "1.0.0",
  "author": "Your Name",
  "description": "A simple greeting mod",
  "entry_dll": "MyFirstMod.dll",
  "icon": "icon.png",

  "keybinds": [
    {
      "id": "show-greeting",
      "label": "Show Greeting",
      "description": "Display the greeting message",
      "default": "G"
    }
  ]
}
```

### Manifest Fields

| Field | Required | Description |
|-------|----------|-------------|
| `id` | Yes | Unique identifier (lowercase, hyphens only) |
| `name` | Yes | Display name |
| `version` | Yes | Semantic version (x.y.z) |
| `author` | Yes | Your name |
| `entry_dll` | No | Main DLL filename (defaults to `{id}.dll` with hyphens removed) |
| `description` | Yes | What the mod does |
| `dependencies` | No | Required mod IDs (loaded first) |
| `optional_dependencies` | No | Optional mod IDs (loaded first if present) |
| `incompatible_with` | No | Mod IDs that conflict with this mod |
| `keybinds` | No | Rebindable hotkeys |
| `framework_version` | No | Minimum required TerrariaModder version |
| `terraria_version` | No | Minimum Terraria version |
| `homepage` | No | Mod homepage URL |
| `icon` | No | Path to a PNG icon file (relative to mod folder, defaults to `icon.png`). Shown in panel headers and mod list. |
| `tags` | No | Mod tags for categorization |

**Note on `entry_dll`:** Optional. If omitted, defaults to the mod ID with hyphens removed plus `.dll` (e.g., `my-first-mod` → `myfirstmod.dll`). Specify explicitly if your DLL name differs from this pattern.

## Step 4: Create the Mod Class

Create `Mod.cs`:

```csharp
using Terraria;
using TerrariaModder.Core;
using TerrariaModder.Core.Config;
using TerrariaModder.Core.Logging;

namespace MyFirstMod
{
    // Config class — properties supply F6 menu metadata and save automatically
    public class MyFirstModConfig : ModConfig
    {
        public override int Version => 1;

        [Client] public bool ShowOnEnter { get; set; } = true;
        [Client] public string GreetingMessage { get; set; } = "Hello, Terraria!";
    }

    // IMod = required. IModLifecycle = optional (world load/unload hooks).
    public class Mod : ModBase, IModLifecycle
    {
        // These must match manifest.json

        private ILogger _log;
        private MyFirstModConfig _config;

        public override void Initialize(ModContext context)
        {
            _log = context.Logger;
            _config = context.GetConfig<MyFirstModConfig>();

            // Register our keybind
            context.RegisterKeybind("show-greeting", "Show Greeting",
                "Display the greeting message", "G", ShowGreeting);

            _log.Info("My First Mod initialized!");
        }

        public void OnContentReady(ModContext context)
        {
            // Called after all mods are initialized and content IDs are assigned.
            // Use this for cross-mod lookups or anything that depends on other mods being loaded.
        }

        public void OnWorldLoad()
        {
            if (_config.ShowOnEnter)
                ShowGreeting();
        }

        public void OnWorldUnload()
        {
            _log.Debug("World unloading");
        }

        public override void Unload()
        {
            _log.Info("My First Mod unloading");
        }

        // Optional: Implement this to receive config changes without restart
        public void OnConfigChanged()
        {
            _log.Info("Config changed - reloading settings");
            // Re-read any cached config values here
        }

        private void ShowGreeting()
        {
            // Read the typed config instance, including live edits.
            string greeting = _config.GreetingMessage;

            // Show it in the game chat
            Main.NewText(greeting, 255, 255, 100); // Yellow text

            _log.Info($"Showed greeting: {greeting}");
        }
    }
}
```

## Step 5: Build

### Command Line

```batch
dotnet build src/MyFirstMod/MyFirstMod.csproj -c Release
```

### Visual Studio

1. Add project to the solution
2. Build (Ctrl+Shift+B)

## Step 6: Deploy

Copy the files to your Terraria mods folder:

```
Terraria/TerrariaModder/mods/my-first-mod/
├── manifest.json    (copy from src/MyFirstMod/)
└── MyFirstMod.dll   (copy from build/plugins/)
```

Or add to `deploy.bat`:
```batch
if not exist "%TERRARIA_PATH%\TerrariaModder\mods\my-first-mod" mkdir "%TERRARIA_PATH%\TerrariaModder\mods\my-first-mod"
copy /Y "build\plugins\MyFirstMod.dll" "%TERRARIA_PATH%\TerrariaModder\mods\my-first-mod\" >nul
copy /Y "src\MyFirstMod\manifest.json" "%TERRARIA_PATH%\TerrariaModder\mods\my-first-mod\" >nul
```

## Step 7: Test

1. Run `TerrariaInjector.exe`
2. Load or create a world
3. You should see your greeting message
4. Press G to show it again
5. Press F6 to open mod menu and change the greeting

## Step 8: Check Logs

If something doesn't work, open `Terraria/TerrariaModder/core/logs/` and use the newest `terrariamodder.client.session-*.log` file for the failed run. The combined `terrariamodder.log` remains available for compatibility.

Look for `[my-first-mod]` entries to see your mod's log messages.

## Notes for Existing Modders

If you're updating a mod from an earlier Core version:

- **Recompile against new Core** — mods must be recompiled against Core 0.4.0+. Old DLLs that only use basic `IMod` methods will still load, but mods using changed APIs (like the old `IModConfig` interface) will fail gracefully and need a recompile.
- **Config migration is automatic** — if your mod used the old `config_schema` / `config.json` system, user settings migrate automatically to the new `core/configs/` location. We encourage adopting the new `ModConfig` class for free F6 UI, multiplayer scoping, and validation. Existing settings continue to migrate through the compatibility path.
- **IModLifecycle is optional** — `OnContentReady`, `OnWorldLoad`, and `OnWorldUnload` moved from `IMod` to the optional `IModLifecycle` interface. Add `IModLifecycle` to your class declaration to use them.
- **Multiplayer** — add `"multiplayer": "client-only"` (or `"required"` / `"optional"`) to your manifest.json. See [Publishing](publishing-your-mod.md) for details.

## Next Steps

Now that you have a working mod:

1. **Add a UI panel** - Use `DraggablePanel` + `StackLayout` from the [Widget Library](core-api-reference.md#widget-library) for instant drag, close, z-order
2. **Add Harmony patches** - See [Harmony Basics](harmony-basics.md) for patching game behavior. Apply patches manually with `_harmony.Patch()` in `IModLifecycle.OnContentReady` after Terraria and other mod content are initialized
3. **Add more features** - See [Tested Patterns](tested-patterns.md) for common techniques
4. **Study real mods** - Read the [Mod Walkthroughs](walkthroughs.md)
5. **Publish** - See [Publishing Your Mod](publishing-your-mod.md)

## Common Issues

### "IMod not found"

Make sure you're referencing `TerrariaModder.Core.csproj` correctly.

### "Id mismatch" warning

The `Id` property in your Mod class must exactly match the `id` in manifest.json.

### Config not working

1. Make sure your `ModConfig` subclass properties have `[Client]` or `[Server]` attributes
2. Delete the config file in `TerrariaModder/core/configs/` to reset to defaults
3. Check logs for config parsing errors

### Keybind not working

1. Check the keybind is registered (look for log message)
2. Try a different key if there's a conflict
3. Make sure you're in-game, not on the menu

New mods can inherit `ModBase` to read `Id`, `Name`, and `Version` from `manifest.json`. The loader binds these before `Initialize`; do not read them in the constructor. Existing `IMod` implementations remain supported.
