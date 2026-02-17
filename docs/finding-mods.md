---
title: Available Mods
nav_order: 4
---

# Available Mods

All official TerrariaModder mods are available on [Nexus Mods](https://www.nexusmods.com/profile/Inidar/mods). Each mod is a separate download, so install only what you want.

## Official Mods

All mods require TerrariaModder Core to be installed first.

| Mod | Description | Keybind | Download |
|-----|-------------|---------|----------|
| **SkipIntro** | Skips the ReLogic splash screen on startup | Automatic | [Nexus](https://www.nexusmods.com/terraria/mods/140) |
| **QuickKeys** | Auto-torch, recall hotkey, quick-stack, ruler, extended hotbar | Tilde, Home, End, K | [Nexus](https://www.nexusmods.com/terraria/mods/143) |
| **AutoBuffs** | Automatically applies nearby furniture buffs | Automatic | [Nexus](https://www.nexusmods.com/terraria/mods/138) |
| **PetChests** | Right-click any cosmetic pet to access piggy bank | Right-click | [Nexus](https://www.nexusmods.com/terraria/mods/142) |
| **ItemSpawner** | In-game item spawner UI (singleplayer) | Insert | [Nexus](https://www.nexusmods.com/terraria/mods/141) |
| **StorageHub** | Unified storage with crafting, recipes, shimmer decraft, painting chest, relay network | F5 | [Nexus](https://www.nexusmods.com/terraria/mods/136) |
| **AdminPanel** | God mode, movement speed, teleports, time controls, respawn settings | Backslash, F9 | [Nexus](https://www.nexusmods.com/terraria/mods/137) |
| **WhipStacking** | Restores pre-1.4.5 whip tag stacking | Automatic | [Nexus](https://www.nexusmods.com/terraria/mods/139) |
| **SeedLab** | Toggle secret seed features for world gen (WIP) | F10 | [Nexus](https://www.nexusmods.com/terraria/mods/144) |
| **DebugTools** | Debug HTTP server, in-game console, virtual input, window management | Ctrl+` | — |

**ModMenu** (F6) is built into Core, no separate download needed.

## Installing a Mod

1. Download the mod zip from Nexus
2. Extract it into your Terraria folder
3. The mod's folder will appear under `TerrariaModder/mods/`
4. Launch game with TerrariaInjector.exe

Each mod zip extracts to its own folder:
```
TerrariaModder/
└── mods/
    └── mod-name/
        ├── manifest.json
        └── ModName.dll
```

## Finding Community Mods

### GitHub

Search GitHub for TerrariaModder mods:
- Search: `TerrariaModder mod`
- Look for repositories with manifest.json files
- Check releases for download packages

### Terraria Forums

Check the Terraria Community Forums:
- Client/Server Mods section
- Search for "TerrariaModder"

### Discord

Terraria modding Discord servers often have:
- Mod showcase channels
- Work-in-progress mods
- Direct links to downloads

## Evaluating Mods

Before installing a mod, consider:

### 1. Source Code Available?

Prefer mods with source code:
- You can verify what it does
- Community can audit for safety
- Easier to report/fix issues

### 2. Recent Updates?

Check when the mod was last updated:
- Works with current Terraria version?
- Active maintenance?

### 3. Documentation

Good mods have:
- Clear description of features
- Installation instructions
- Keybind documentation
- Known issues listed

### 4. Community Feedback

Look for:
- Download counts
- User comments
- Issue reports and responses

## Mod Compatibility

### With TerrariaModder Core

Some mods specify a `framework_version` in manifest.json. If present:
- Must match or be lower than your installed Core version
- Higher version means you need to update Core
- Most mods omit this field and work with any Core version

### With Other Mods

Most mods are compatible with each other. Issues can arise if:
- Two mods patch the same method differently
- Keybind conflicts (configure in ModMenu)
- Both try to modify the same game data

### With Terraria Versions

Mods are built for specific Terraria versions:
- TerrariaModder targets Terraria 1.4.5
- Older/newer Terraria versions may have issues
- Check mod documentation for supported versions

## Troubleshooting

### Mod Not Loading

1. Check folder structure is correct
2. Verify manifest.json is valid
3. Check logs in `TerrariaModder/core/logs/terrariamodder.log`

### Mod Crashes Game

1. Disable the mod (delete or rename the mod folder temporarily)
2. Check logs for error messages
3. Report issue to mod author

### Keybind Conflicts

1. Open ModMenu (F6)
2. Go to the conflicting mod
3. Change keybind to unused key

## Requesting Mods

Want a mod that doesn't exist?

1. Check if similar mod exists
2. Post request on forums/Discord
3. Consider learning to make it yourself!

See [Making Your First Mod](making-your-first-mod) to get started.

## Contributing

Found a useful mod? Help the community:
- Share links (with credit to author)
- Write reviews
- Report bugs constructively
- Contribute fixes if source is available
