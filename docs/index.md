---
title: TerrariaModder - Modding Framework for Terraria 1.4.5.8
description: A lightweight modding framework for Terraria 1.4.5.8. Install QoL mods or create your own using Harmony runtime patches, without waiting for tModLoader.
---

# TerrariaModder

[![Discord](https://img.shields.io/discord/1467363973526716572?color=5865F2&logo=discord&logoColor=white&label=Discord)](https://discord.gg/VvVD5EeYsK)

A lightweight modding framework for Terraria 1.4.5.8 that works alongside vanilla Terraria.

## What is TerrariaModder?

TerrariaModder lets you run mods on Terraria 1.4.5.8 without waiting for tModLoader. It's designed for quality-of-life mods, utilities, automation, and custom content (new items with custom textures, recipes, shops, and drops via the Custom Assets system).

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

The easiest way is the **[TerrariaModder Vault](https://www.nexusmods.com/terraria/mods/159)** — the official mod manager. Its Browse catalog uses Nexus's declared TerrariaModder Core requirement, and it supports both free and Premium Nexus account download flows. Click **Launch Modded** when you're ready to play.

1. [Installation Guide](installation.md) - Get up and running (Vault or manual)
2. [Troubleshooting](troubleshooting.md) - Fix common issues
3. [Available Mods](finding-mods.md) - What's available

### Available Mods

Download Core and any mods you want from [Nexus Mods](https://www.nexusmods.com/profile/Inidar/mods), or install them all through the [Vault](https://www.nexusmods.com/terraria/mods/159) in one place. Each mod is a separate download.

| Mod | Description | Keybind | Multiplayer | Download |
|-----|-------------|---------|-------------|----------|
| **ModMenu** | In-game configuration UI for all mods (built into Core) | F6 | All modes | Included in [Core](https://www.nexusmods.com/terraria/mods/135) |
| **SkipIntro** | Skips the ReLogic splash screen on startup | Automatic | Client-only | [Nexus](https://www.nexusmods.com/terraria/mods/140) |
| **QuickKeys** | Auto-torch, recall hotkey, quick-stack, ruler, extended hotbar (opt-in) | Tilde, Home, End, K | Client-only | [Nexus](https://www.nexusmods.com/terraria/mods/143) |
| **AutoBuffs** | Automatically applies nearby furniture buffs | Automatic | Client-only | [Nexus](https://www.nexusmods.com/terraria/mods/138) |
| **PetChests** | Right-click any cosmetic pet to access piggy bank | Right-click | Client-only | [Nexus](https://www.nexusmods.com/terraria/mods/142) |
| **ItemSpawner** | Spawn any item (admin or granted players in MP) | Insert | Optional | [Nexus](https://www.nexusmods.com/terraria/mods/141) |
| **StorageHub** | Unified storage with crafting, recipes, shimmer, mysterious chest, relay network | F5 | Required | [Nexus](https://www.nexusmods.com/terraria/mods/136) |
| **AdminPanel** | God mode, movement speed, teleports, time controls, respawn settings | Backslash, F9 | Optional | [Nexus](https://www.nexusmods.com/terraria/mods/137) |
| **WhipStacking** | Enables Terraria's native maximum of five simultaneous whip tag effects | Automatic | Client-only | [Nexus](https://www.nexusmods.com/terraria/mods/139) |
| **SeedLab** | Toggle secret seed features for world gen | F10 | Optional | [Nexus](https://www.nexusmods.com/terraria/mods/144) |
| **FpsUnlocked** | Unlock frame rate with smooth interpolation (60 Hz logic + high-FPS rendering) | Automatic | Client-only | [Nexus](https://www.nexusmods.com/terraria/mods/145) |
| **BiomeSpread** | Prevent corruption, crimson, and Hallow spread | Automatic | Singleplayer | [Nexus](https://www.nexusmods.com/terraria/mods/146) |
| **Randomizer** | Deterministic modular randomizer for chests, drops, recipes, shops, and more | Numpad / | Singleplayer | [Nexus](https://www.nexusmods.com/terraria/mods/147) |
| **Debug Tools** | HTTP debug server, in-game console, virtual input, runtime introspection | Ctrl+` | Client-only | [Core optional files](https://www.nexusmods.com/terraria/mods/135) |

Press **F6** in-game to configure mods and rebind keys. Changes are saved automatically and keybinds persist across game restarts.

## For Modders

**Want to create mods?**

1. [Making Your First Mod](making-your-first-mod.md) - Step-by-step tutorial
2. [Harmony Basics](harmony-basics.md) - Runtime patching guide
3. [Tested Patterns](tested-patterns.md) - Proven techniques from real mods
4. [Core API Reference](core-api-reference.md) - Framework APIs
5. [The Vault](the-vault.md) - Making your mod installable via the official mod manager
6. [Publishing Your Mod](publishing-your-mod.md) - Distribution guide

### Mod Walkthroughs

Learn by studying real, working mods:

- [SkipIntro](walkthroughs/skip-intro.md) - Harmony patch with lifecycle hooks
- [AutoBuffs](walkthroughs/auto-buffs.md) - Tile scanning and buff application
- [QuickKeys](walkthroughs/quick-keys.md) - Complex input handling and reflection
- [PetChests](walkthroughs/pet-chests.md) - Projectile interaction
- [ItemSpawner](walkthroughs/item-spawner.md) - Full UI implementation
- [StorageHub](walkthroughs/storage-hub.md) - Multi-tab storage, crafting, shimmer, mysterious chest, relay network
- [AdminPanel](walkthroughs/admin-panel.md) - UI sliders, Harmony patches, boss detection
- [WhipStacking](walkthroughs/whip-stacking.md) - Terraria's native five-effect whip tag capacity
- [SeedLab](walkthroughs/seed-lab.md) - World-gen patching, runtime seed feature toggling
- [FPS Unlocked](walkthroughs/fps-unlocked.md) - Fixed-step simulation with interpolated high-rate rendering
- [Biome Spread Control](walkthroughs/biome-spread.md) - Scoped world-spread patches
- [Randomizer](walkthroughs/randomizer.md) - Deterministic module seeds and reversible recipe state
- [Debug Tools](walkthroughs/debug-tools.md) - HTTP server, console, virtual input, window management

## Requirements

- Terraria 1.4.5.8 (Steam version)
- Windows

## Quick Links

- [Nexus Mods](https://www.nexusmods.com/profile/Inidar/mods)
- [GitHub Repository](https://github.com/Inidar1/terraria-modder) (source code)
- [Report Issues](https://github.com/Inidar1/terraria-modder/issues)

## Credits

**Author:** Inidar

Built on [TerrariaInjector](https://github.com/ConfuzzedCat/TerrariaInjector) by ConfuzzedCat. Uses [Harmony](https://github.com/pardeike/Harmony) by pardeike and [Mono.Cecil](https://github.com/jbevain/cecil) by jbevain.
