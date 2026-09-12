---
title: Install Terraria 1.4.5.8 Mods - TerrariaModder Setup Guide
description: Step-by-step guide to install TerrariaModder and mods on Terraria 1.4.5.8. Download, extract, and launch with TerrariaInjector in minutes.
nav_order: 2
---

# Installation

This guide covers installing TerrariaModder for players who want to use mods.

## Requirements

- **Terraria 1.4.5.8** (Steam version)
- **Windows** (10 or 11)

## Option A: Install via the Vault (Recommended)

The **[TerrariaModder Vault](https://www.nexusmods.com/terraria/mods/159)** is the official mod manager. It handles Core and mods in one place and keeps installed configuration during ordinary updates.

1. Download and run the Vault from [Nexus Mods](https://www.nexusmods.com/terraria/mods/159)
2. Select your Terraria installation
3. Sign in to your own Nexus account. Vault stores the user credential in the operating system's protected credential store; no developer API key is embedded in the app.
4. Install Core, then choose the mods you want from Browse
5. Nexus Premium downloads start directly. With a free account, follow **Manual Download** and **Slow Download** in the embedded Nexus page.
6. Click **Launch Modded** to launch

That's it. The Vault keeps everything up to date and lets you enable/disable mods without touching files. **Most players should use this.**

---

## Option B: Manual Installation

If you prefer to manage files yourself:

### Step 1: Download

Download **TerrariaModder Core** from [Nexus Mods](https://www.nexusmods.com/profile/Inidar/mods). This is the framework that all mods require.

Then download any mods you want. Each mod is a separate download on the same Nexus page.

### Step 2: Find Your Terraria Folder

The default Steam location is:
```
C:\Program Files (x86)\Steam\steamapps\common\Terraria
```

To find it in Steam:
1. Right-click Terraria in your library
2. Click "Properties"
3. Go to "Local Files" tab
4. Click "Browse Local Files"

### Step 3: Install Core

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

### Step 4: Install Mods

Move an older copy of that mod folder aside, then extract the complete new mod folder under `TerrariaModder/mods/`:

```
TerrariaModder/
└── mods/
    ├── skip-intro/
    ├── quick-keys/
    ├── storage-hub/
    └── (etc.)
```

### Step 5: Launch

**Important:** Run `TerrariaInjector.exe` instead of `Terraria.exe`.

You can:
- Double-click `TerrariaInjector.exe` directly
- Create a shortcut to it on your desktop
- Add it as a non-Steam game in Steam

The game will launch normally with mods active.

### Step 6: Configure (Optional)

Press **F6** in-game to open the **ModMenu** where you can:
- Enable/disable mods
- Change mod settings
- Rebind keybinds

ModMenu is built into TerrariaModder Core - no separate installation needed. Your configuration changes are saved automatically, and keybind changes persist across game restarts.

## Verifying Installation

If mods are working, you'll see:
1. The ReLogic splash screen is skipped (if SkipIntro mod is installed)
2. The latest client session log lists Core and each loaded mod without errors
3. F6 opens the mod menu

## Adding More Mods

**With the Vault:** Browse the requirement-based catalog and click **Install**. Free Nexus accounts complete the Manual Download / Slow Download step in the embedded page; Premium accounts download directly.

**Manually:**

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

**With the Vault:** It detects new versions automatically. Click to update.

**Manually:**

1. Back up characters, worlds, and the existing `TerrariaModder/` folder
2. Move the current `TerrariaModder/core/` folder aside
3. Install the complete new Core folder rather than merging files into the old one
4. Replace each updated mod folder the same way and keep the backup until the new build is confirmed

## Uninstalling

To completely remove TerrariaModder:

1. Delete `TerrariaInjector.exe`
2. Delete the `TerrariaModder/` folder
3. Launch Terraria normally via Steam

## Troubleshooting

### Game doesn't launch

Check `TerrariaModder/core/logs/` for errors.

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
3. Verify Terraria version is 1.4.5.8

See [Troubleshooting](troubleshooting.md) for more solutions.
