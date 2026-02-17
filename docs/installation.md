---
title: Installation
nav_order: 2
---

# Installation

This guide covers installing TerrariaModder for players who want to use mods.

## Requirements

- **Terraria 1.4.5** (Steam version)
- **Windows** (10 or 11)

## Step 1: Download

Download **TerrariaModder Core** from [Nexus Mods](https://www.nexusmods.com/profile/Inidar/mods). This is the framework that all mods require.

Then download any mods you want. Each mod is a separate download on the same Nexus page.

## Step 2: Find Your Terraria Folder

The default Steam location is:
```
C:\Program Files (x86)\Steam\steamapps\common\Terraria
```

To find it in Steam:
1. Right-click Terraria in your library
2. Click "Properties"
3. Go to "Local Files" tab
4. Click "Browse Local Files"

## Step 3: Install Core

Extract the Core zip into your Terraria folder. After extraction, you should have:

```
Terraria/
├── Terraria.exe              (existing)
├── TerrariaInjector.exe      (new)
└── TerrariaModder/           (new)
    ├── core/
    │   ├── TerrariaModder.Core.dll
    │   ├── config.json
    │   ├── deps/
    │   │   ├── 0Harmony.dll
    │   │   └── Mono.Cecil.dll
    │   ├── logs/
    │   └── Docs/
    │       ├── README.md
    │       └── THIRD-PARTY-NOTICES.md
    └── mods/
```

## Step 4: Install Mods

Extract each mod zip into your Terraria folder. Each mod adds a folder under `TerrariaModder/mods/`:

```
TerrariaModder/
└── mods/
    ├── skip-intro/
    ├── quick-keys/
    ├── storage-hub/
    └── (etc.)
```

## Step 5: Launch

**Important:** Run `TerrariaInjector.exe` instead of `Terraria.exe`.

You can:
- Double-click `TerrariaInjector.exe` directly
- Create a shortcut to it on your desktop
- Add it as a non-Steam game in Steam

The game will launch normally with mods active.

## Step 6: Configure (Optional)

Press **F6** in-game to open the **ModMenu** where you can:
- Enable/disable mods
- Change mod settings
- Rebind keybinds

ModMenu is built into TerrariaModder Core - no separate installation needed. Your configuration changes are saved automatically, and keybind changes persist across game restarts.

## Verifying Installation

If mods are working, you'll see:
1. The ReLogic splash screen is skipped (if SkipIntro mod is installed)
2. A small overlay in the top-left corner on the title screen showing "TerrariaModder v0.1.0 - X mods loaded"
3. F6 opens the mod menu

## Adding More Mods

To install additional mods:

1. Download the mod zip
2. Extract it into your Terraria folder (contents go into `TerrariaModder/mods/`)
3. Restart the game

Each mod should be in its own folder:
```
mods/
├── existing-mod/
└── new-mod/
    ├── manifest.json
    └── NewMod.dll
```

## Removing Mods

To remove a mod, simply delete its folder from `TerrariaModder/mods/`.

## Updating TerrariaModder

1. Back up your `TerrariaModder/mods/` folder (contains your mod configs)
2. Download the new Core version
3. Extract over your existing installation
4. Your mod configs will be preserved

## Uninstalling

To completely remove TerrariaModder:

1. Delete `TerrariaInjector.exe`
2. Delete the `TerrariaModder/` folder
3. Launch Terraria normally via Steam

## Troubleshooting

### Game doesn't launch

Check `TerrariaModder/core/logs/terrariamodder.log` for errors.

### Mods not loading

1. Verify you're running `TerrariaInjector.exe`, not `Terraria.exe`
2. Check that mod folders have both a `.dll` file and `manifest.json`
3. Check the log file for error messages

### F6 doesn't open menu

1. Make sure Core loaded (check title screen overlay)
2. Try a different key if F6 conflicts with something
3. Check the log file for keybind registration

### Game crashes on startup

1. Check `TerrariaInjector.log` in the Terraria folder
2. Try removing recently added mods
3. Verify Terraria version is 1.4.5

See [Troubleshooting](troubleshooting) for more solutions.
