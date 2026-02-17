---
title: Home
---

# TerrariaModder

A lightweight modding framework for Terraria 1.4.5 that works alongside vanilla Terraria.

## What is TerrariaModder?

TerrariaModder lets you run mods on Terraria 1.4.5 without waiting for tModLoader. It's designed for quality-of-life mods, utilities, automation, and custom content (new items with custom textures, recipes, shops, and drops via the Custom Assets system).

**Key Features:**
- In-game mod menu (F6) for configuration and keybind rebinding
- Widget Library for building mod UIs (panels, buttons, sliders, scroll, text input)
- Lifecycle hooks for deterministic mod initialization (OnGameReady, OnContentLoaded, etc.)
- Automatic Harmony patch application: attribute patches work without boilerplate
- Hot reload support for config changes (no restart needed)
- Keybind persistence across game restarts
- Per-mod configuration with JSON schemas
- Colorblind-friendly theme support (normal, red-green, blue-yellow, high-contrast)

## For Players

**Want to install and use mods?**

1. [Installation Guide](installation) - Get up and running
2. [Troubleshooting](troubleshooting) - Fix common issues
3. [Available Mods](available-mods) - What's available

### Available Mods

Download Core and any mods you want from [Nexus Mods](https://www.nexusmods.com/profile/Inidar/mods). Each mod is a separate download.

| Mod | Description | Keybind | Download |
|-----|-------------|---------|----------|
| **ModMenu** | In-game configuration UI for all mods (built into Core) | F6 | Included in [Core](https://www.nexusmods.com/terraria/mods/135) |
| **SkipIntro** | Skips the ReLogic splash screen on startup | Automatic | [Nexus](https://www.nexusmods.com/terraria/mods/140) |
| **QuickKeys** | Auto-torch, recall hotkey, quick-stack, ruler, extended hotbar (opt-in) | Tilde, Home, End, K | [Nexus](https://www.nexusmods.com/terraria/mods/143) |
| **AutoBuffs** | Automatically applies nearby furniture buffs | Automatic | [Nexus](https://www.nexusmods.com/terraria/mods/138) |
| **PetChests** | Right-click any cosmetic pet to access piggy bank | Right-click | [Nexus](https://www.nexusmods.com/terraria/mods/142) |
| **ItemSpawner** | Spawn any item (singleplayer only) | Insert | [Nexus](https://www.nexusmods.com/terraria/mods/141) |
| **StorageHub** | Unified storage with crafting, recipes, shimmer decraft, painting chest, relay network | F5 | [Nexus](https://www.nexusmods.com/terraria/mods/136) |
| **AdminPanel** | God mode, movement speed, teleports, time controls, respawn settings | Backslash, F9 | [Nexus](https://www.nexusmods.com/terraria/mods/137) |
| **WhipStacking** | Restores pre-1.4.5 whip tag stacking | Automatic | [Nexus](https://www.nexusmods.com/terraria/mods/139) |
| **SeedLab** | Toggle secret seed features for world gen (WIP) | F10 | [Nexus](https://www.nexusmods.com/terraria/mods/144) |
| **DebugTools** | Debug HTTP server, in-game console, virtual input, window management | Ctrl+` | — |

Press **F6** in-game to configure mods and rebind keys. Changes are saved automatically and keybinds persist across game restarts.

## For Modders

**Want to create mods?**

1. [Making Your First Mod](making-your-first-mod) - Step-by-step tutorial
2. [Harmony Basics](harmony-basics) - Runtime patching guide
3. [Tested Patterns](tested-patterns) - Proven techniques from real mods
4. [Core API Reference](core-api-reference) - Framework APIs
5. [Publishing Your Mod](publishing-your-mod) - Distribution guide

### Mod Walkthroughs

Learn by studying real, working mods:

- [SkipIntro](walkthroughs/skip-intro) - Harmony patch with lifecycle hooks
- [AutoBuffs](walkthroughs/auto-buffs) - Tile scanning and buff application
- [QuickKeys](walkthroughs/quick-keys) - Complex input handling and reflection
- [PetChests](walkthroughs/pet-chests) - Projectile interaction
- [ItemSpawner](walkthroughs/item-spawner) - Full UI implementation
- [StorageHub](walkthroughs/storage-hub) - Multi-tab storage, crafting, shimmer, painting chest, relay network
- [AdminPanel](walkthroughs/admin-panel) - UI sliders, Harmony patches, boss detection
- [WhipStacking](walkthroughs/whip-stacking) - Harmony prefixes, restoring removed mechanics
- [DebugTools](walkthroughs/debug-tools) - HTTP server, console, virtual input, window management
- [SeedLab](walkthroughs/seed-lab) - World-gen patching, runtime seed feature toggling

## Requirements

- Terraria 1.4.5 (Steam version)
- Windows

## Quick Links

- [Nexus Mods](https://www.nexusmods.com/profile/Inidar/mods)
- [GitHub Repository](https://github.com/Inidar1/terraria-modder) (source code)
- [Report Issues](https://github.com/Inidar1/terraria-modder/issues)

## Credits

**Author:** Inidar

Built on [TerrariaInjector](https://github.com/ConfuzzedCat/TerrariaInjector) by ConfuzzedCat. Uses [Harmony](https://github.com/pardeike/Harmony) by pardeike and [Mono.Cecil](https://github.com/jbevain/cecil) by jbevain.
