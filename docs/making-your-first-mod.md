---
title: Making Your First Mod
nav_order: 5
---

# Making Your First Mod

This tutorial walks you through creating a simple TerrariaModder mod from scratch.

## Prerequisites

- **.NET Framework 4.8 SDK** - [Download](https://dotnet.microsoft.com/download/dotnet-framework/net48)
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
      <HintPath>..\..\Terraria\Mods\Libs\0Harmony.dll</HintPath>
      <Private>false</Private>
    </Reference>
  </ItemGroup>
</Project>
```

Key settings:
- `TargetFramework`: Must be `net48`
- `Private`: Set to `false` to avoid copying framework DLLs into your output
- `Terraria` reference: Allows using Terraria types directly (e.g., `Main.NewText()`). Optional; advanced mods can use reflection instead (see [Tested Patterns](tested-patterns#reflection-patterns))
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

  "config_schema": {
    "greeting": {
      "type": "string",
      "default": "Hello, Terraria!",
      "label": "Greeting Message",
      "description": "Message shown when entering a world"
    },
    "show_on_enter": {
      "type": "bool",
      "default": true,
      "label": "Show on World Enter",
      "description": "Show greeting when entering a world"
    }
  },

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
| `config_schema` | No | User-configurable settings |
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
using TerrariaModder.Core.Logging;

namespace MyFirstMod
{
    public class Mod : IMod
    {
        // These must match manifest.json
        public string Id => "my-first-mod";
        public string Name => "My First Mod";
        public string Version => "1.0.0";

        private ILogger _log;
        private ModContext _context;

        public void Initialize(ModContext context)
        {
            _log = context.Logger;
            _context = context;

            // Register our keybind
            context.RegisterKeybind("show-greeting", "Show Greeting",
                "Display the greeting message", "G", ShowGreeting);

            _log.Info("My First Mod initialized!");
        }

        public void OnWorldLoad()
        {
            // Check if we should show greeting on world enter
            bool showOnEnter = _context.Config?.Get<bool>("show_on_enter") ?? true;

            if (showOnEnter)
            {
                ShowGreeting();
            }
        }

        public void OnWorldUnload()
        {
            _log.Debug("World unloading");
        }

        public void Unload()
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
            // Get the greeting from config, or use default
            string greeting = _context.Config?.Get<string>("greeting") ?? "Hello, Terraria!";

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

If something doesn't work, check:
```
Terraria/TerrariaModder/core/logs/terrariamodder.log
```

Look for `[my-first-mod]` entries to see your mod's log messages.

## Next Steps

Now that you have a working mod:

1. **Add a UI panel** - Use `DraggablePanel` + `StackLayout` from the [Widget Library](core-api-reference#widget-library) for instant drag, close, z-order
2. **Add Harmony patches** - See [Harmony Basics](harmony-basics) for patching game behavior. Attribute-based patches are auto-applied; manual patches go in `OnGameReady()` [lifecycle hooks](core-api-reference#injector-lifecycle-hooks)
3. **Add more features** - See [Tested Patterns](tested-patterns) for common techniques
4. **Study real mods** - Read the [Mod Walkthroughs](walkthroughs)
5. **Publish** - See [Publishing Your Mod](publishing-your-mod)

## Common Issues

### "IMod not found"

Make sure you're referencing `TerrariaModder.Core.csproj` correctly.

### "Id mismatch" warning

The `Id` property in your Mod class must exactly match the `id` in manifest.json.

### Config not working

1. Make sure config key names match between manifest.json and your code
2. Delete `config.json` to reset to defaults
3. Check logs for config parsing errors

### Keybind not working

1. Check the keybind is registered (look for log message)
2. Try a different key if there's a conflict
3. Make sure you're in-game, not on the menu
