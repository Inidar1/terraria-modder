# TerrariaModder.Core

A modding framework for Terraria 1.4.5 that enables runtime patching and mod loading.

## Features

- Hot-loads mods from the `TerrariaModder/mods/` folder
- Harmony-based runtime patching
- Configuration system with in-game UI (F6)
- Keybind system with rebinding support and persistence across restarts
- Config baseline tracking (detects changes for "Restart Required" display)
- Event system for game hooks (GameEvents, PlayerEvents, ItemEvents, NPCEvents, FrameEvents)
- Custom Assets system (custom items with real type IDs, save interception, texture injection)
- UI Renderer and widget library (panels, buttons, sliders, text input, scroll views)
- Reflection utilities for safe Terraria type access
- Debug command registry (CommandRegistry, the public API for mods to register commands)
- Consolidated logging (single file, labeled entries)

## Installation

1. Extract the Core zip into your Terraria folder
   (e.g. `C:\Program Files (x86)\Steam\steamapps\common\Terraria`).
2. Launch via `TerrariaInjector.exe` instead of `Terraria.exe`.
3. Add mods by extracting their zips into the same Terraria folder.
4. Press F6 in-game to configure mods.

## Folder Structure

```
Terraria/
├── TerrariaInjector.exe           <- Run this
├── Terraria.exe
└── TerrariaModder/
    ├── core/
    │   ├── TerrariaModder.Core.dll
    │   ├── config.json            <- Folder structure configuration
    │   ├── deps/
    │   │   ├── 0Harmony.dll
    │   │   └── Mono.Cecil.dll
    │   ├── logs/
    │   │   └── terrariamodder.log <- All mods log here
    │   └── Docs/
    │       ├── README.md
    │       └── THIRD-PARTY-NOTICES.md
    └── mods/                       <- Mods go here
        ├── quick-keys/
        │   ├── manifest.json
        │   ├── QuickKeys.dll
        │   └── README.md
        └── other-mod/
            ├── manifest.json
            └── OtherMod.dll
```

## For Mod Authors

See the main repository for documentation on creating mods:
https://github.com/Inidar1/terraria-modder

## Multiplayer

TerrariaModder is client-side only. Mods that only affect your own player (like keybinds, UI, and buffs) work fine in multiplayer. Mods that modify shared game state (chests, time, NPCs) are singleplayer only for now. Each mod's README notes its multiplayer status. Full multiplayer support is planned for a future update.

## License

TerrariaModder.Core is distributed under the MIT license.

Note: When distributing with TerrariaInjector (GPL-3.0), see THIRD-PARTY-NOTICES.md.

## Credits

**Author**: Inidar

**Dependencies**:
- [TerrariaInjector](https://github.com/ConfuzzedCat/TerrariaInjector) by ConfuzzedCat (GPL-3.0)
- [Harmony](https://github.com/pardeike/Harmony) by pardeike (MIT)
- [Mono.Cecil](https://github.com/jbevain/cecil) by Jb Evain (MIT)
