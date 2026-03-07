---
title: FAQ - Terraria 1.4.5 Modding Questions
description: Frequently asked questions about TerrariaModder and modding Terraria 1.4.5. Covers compatibility, safety, multiplayer, tModLoader differences, and more.
nav_order: 3.5
---

# Frequently Asked Questions

Common questions about TerrariaModder and modding Terraria 1.4.5.

## General

### What is TerrariaModder?

TerrariaModder is a lightweight modding framework for Terraria 1.4.5 on Windows. It lets you install quality-of-life mods like auto-buffs, quick-stack hotkeys, storage management, and more. It also provides a framework for creating your own mods using C# and Harmony runtime patching.

### How is TerrariaModder different from tModLoader?

TerrariaModder and tModLoader are separate modding frameworks:

| | TerrariaModder | tModLoader |
|---|---|---|
| **Target version** | Terraria 1.4.5 (latest vanilla) | Terraria 1.4.4.9 (older version) |
| **Approach** | Runtime injection via Harmony patches | Full game modification |
| **Mod scope** | QoL mods, utilities, automation, custom items | Total conversion, new content, biomes, bosses |
| **Game files** | Does not modify Terraria.exe | Replaces game executable |
| **Mod count** | Growing collection of focused mods | Thousands of community mods |
| **Steam Workshop** | No (Nexus Mods + GitHub) | Yes |

Use TerrariaModder if you want mods on the latest Terraria version without downgrading. Use tModLoader if you want access to the massive existing mod ecosystem.

### Can I use TerrariaModder and tModLoader at the same time?

No. tModLoader replaces Terraria.exe and targets a different game version. You need to choose one or the other. You can switch between them by verifying game files in Steam to restore vanilla Terraria.

### Does TerrariaModder work with Terraria 1.4.4 or earlier?

TerrariaModder is built specifically for Terraria 1.4.5. It may partially work on nearby versions, but method signatures and game internals can change between updates. Only 1.4.5 is officially supported.

## Safety & Compatibility

### Is TerrariaModder safe to use?

Yes. TerrariaModder does not modify any game files. It works by injecting code at runtime through TerrariaInjector.exe, which loads mods alongside the game process. Your Terraria installation remains completely vanilla. To verify: all source code is open on [GitHub](https://github.com/Inidar1/terraria-modder).

### Will mods corrupt my save files?

No. TerrariaModder mods do not modify your player or world save files. Mods that add custom items use a separate sidecar file system to store custom data. If you uninstall TerrariaModder, your saves work normally in vanilla Terraria (custom items revert to empty slots).

### Does TerrariaModder work with Steam achievements?

Yes. Since TerrariaModder launches through TerrariaInjector.exe alongside the real Terraria process, Steam achievements still work normally.

## Multiplayer

### Does TerrariaModder work in multiplayer?

TerrariaModder is designed primarily for singleplayer. Some mods work in multiplayer (client-side QoL mods like AutoBuffs, QuickKeys), but mods that modify game state (AdminPanel god mode, ItemSpawner) are singleplayer only. Each mod's documentation specifies multiplayer compatibility.

### Do other players need TerrariaModder installed?

No. Client-side mods only affect your own game. Other players don't need anything installed and won't see your mods. The server sees standard Terraria network traffic.

## Installation

### Where do I download TerrariaModder?

The easiest way is the **[TerrariaModder Vault](https://www.nexusmods.com/terraria/mods/159)** — the official mod manager. Install it once and it handles Core and all mods for you.

If you prefer manual installs: download TerrariaModder Core and individual mods from [Nexus Mods](https://www.nexusmods.com/profile/Inidar/mods). Source code is on [GitHub](https://github.com/Inidar1/terraria-modder). See the [Installation Guide](installation) for step-by-step instructions.

### How do I update TerrariaModder?

**With the Vault:** It detects new versions and lets you update with one click.

**Manually:** Download the new Core version and extract it over your existing installation. Your mod configs and keybinds are preserved. See [Installation - Updating](installation#updating-terrariamodder) for details.

### How do I uninstall TerrariaModder?

Delete `TerrariaInjector.exe` and the `TerrariaModder/` folder from your Terraria directory. Your game returns to vanilla. See [Installation - Uninstalling](installation#uninstalling) for details.

### My antivirus flags TerrariaInjector.exe

TerrariaInjector uses DLL injection to load mods, which is a technique that antivirus software sometimes flags. This is a false positive. You can verify the source code on GitHub, or add an exception in your antivirus. See [Troubleshooting](troubleshooting) for more help.

## Modding

### What can I mod with TerrariaModder?

Anything you can patch with Harmony. Common examples:

- **Quality of life**: Auto-buffs, quick-stack, torch placement, recall hotkeys
- **UI additions**: Item spawners, storage management, admin panels
- **Gameplay changes**: Whip stacking, respawn timers, movement speed
- **Custom content**: New items with custom textures, recipes, shop entries, and drops
- **World generation**: Modify seed features, toggle secret seeds
- **Utilities**: Debug tools, automation, HTTP APIs

### What programming language do mods use?

Mods are written in C# targeting .NET Framework 4.8. You'll use [Harmony](https://github.com/pardeike/Harmony) for runtime patching and reflection to access Terraria's internal types. See [Making Your First Mod](making-your-first-mod) to get started.

### Do I need the Terraria source code to make mods?

No. TerrariaModder uses reflection and Harmony, so you never directly reference Terraria's assemblies at compile time. You do need to understand Terraria's internal structure, which you can explore using tools like ILSpy or dnSpy to decompile Terraria.exe.

### How do I debug my mod?

TerrariaModder logs to `TerrariaModder/core/logs/terrariamodder.log`. Use `_log.Info()` calls in your mod code. The DebugTools mod also provides an in-game console (Ctrl+`) and HTTP debug server for advanced debugging.

### Can I distribute mods I create?

Yes. Publishing on [Nexus Mods](https://www.nexusmods.com/terraria) makes your mod automatically installable through the [TerrariaModder Vault](https://www.nexusmods.com/terraria/mods/159), which is how most players install mods. See [The Vault](the-vault) for packaging requirements and the [Publishing Guide](publishing-your-mod) for the full distribution guide.
