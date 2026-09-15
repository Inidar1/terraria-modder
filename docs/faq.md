---
title: FAQ - Terraria 1.4.5.8 Modding Questions
description: Frequently asked questions about TerrariaModder and modding Terraria 1.4.5.8. Covers compatibility, safety, multiplayer, tModLoader differences, and more.
nav_order: 3.5
---

# Frequently Asked Questions

Common questions about TerrariaModder and modding Terraria 1.4.5.8.

## General

### What is TerrariaModder?

TerrariaModder is a lightweight modding framework for Terraria 1.4.5.8 on Windows. It lets you install quality-of-life mods like auto-buffs, quick-stack hotkeys, storage management, and more. It also provides a framework for creating your own mods using C# and Harmony runtime patching.

### How is TerrariaModder different from tModLoader?

TerrariaModder and tModLoader are separate modding frameworks:

| | TerrariaModder | tModLoader |
|---|---|---|
| **Target version** | Terraria 1.4.5.8 | Uses its own supported Terraria branch |
| **Approach** | Runtime injection via Harmony patches | Full game modification |
| **Mod scope** | QoL mods, utilities, automation, custom items | Total conversion, new content, biomes, bosses |
| **Game files** | Does not modify Terraria.exe | Replaces game executable |
| **Mod count** | Growing collection of focused mods | Thousands of community mods |
| **Steam Workshop** | No (Nexus Mods + GitHub) | Yes |

Use TerrariaModder for this framework's focused vanilla-compatible mods and current 1.4.5.8 APIs. Use tModLoader for its separate mod ecosystem.

### Can I use TerrariaModder and tModLoader at the same time?

Do not load both frameworks into the same game process. They use separate launch paths, so you can keep both installed and choose which one to run.

### Does TerrariaModder work with Terraria 1.4.4 or earlier?

TerrariaModder is built specifically for Terraria 1.4.5.8. It may partially work on nearby versions, but method signatures and game internals can change between updates. Only 1.4.5.8 is officially supported by this release.

## Safety & Compatibility

### Is TerrariaModder safe to use?

Yes. TerrariaModder does not modify any game files. It works by injecting code at runtime through TerrariaInjector.exe, which loads mods alongside the game process. Your Terraria installation remains completely vanilla. To verify: all source code is open on [GitHub](https://github.com/Inidar1/terraria-modder).

### Will mods corrupt my save files?

Mods can change characters and worlds during play. Core stores registered custom-item data in sidecar files and journals save operations so missing or temporarily disabled mods do not silently destroy their item data. Back up characters and worlds you care about before testing an update.

### Does TerrariaModder work with Steam achievements?

Yes. Since TerrariaModder launches through TerrariaInjector.exe alongside the real Terraria process, Steam achievements still work normally.

## Multiplayer

### Does TerrariaModder work in multiplayer?

Yes. TerrariaModder has full multiplayer support for both **Host & Play** and **Dedicated Server** modes. Features include an admin system, per-mod permission grants, server console commands, and config scoping ([Server] vs [Client] properties).

Each mod declares its multiplayer compatibility in its manifest:
- **required** — all connected players must have the mod installed
- **optional** — server has it, clients can join without it (features degrade)
- **client-only** — only affects your own game (e.g., FpsUnlocked, SkipIntro)

### Do other players need TerrariaModder installed?

For **client-only** mods (SkipIntro, QuickKeys, and FPS Unlocked), no—other players do not need them. Optional mods can be installed only where their feature is needed, subject to the multiplayer notes on that mod's page. For **required** mods, especially Storage Hub with its custom items, every peer needs a compatible version. A client missing a required mod receives a popup with the mod name and download link.

### How do I become admin on a server?

Three ways:
1. **Host & Play** — the host is automatically admin
2. **Localhost** — connecting from 127.0.0.1 grants auto-admin
3. **Reqop key** — type `/reqop <key>` in chat (key is printed at server startup)

Admins can promote others with `/op PlayerName` in the server console or the F6 Players tab.

## Installation

### Where do I download TerrariaModder?

The easiest way is the **[TerrariaModder Vault](https://www.nexusmods.com/terraria/mods/159)** — the official mod manager. It handles Core plus compatible Nexus and local mod packages. Its Browse list comes from mods that declare TerrariaModder Core in Nexus Requirements.

If you prefer manual installs: download TerrariaModder Core and individual mods from [Nexus Mods](https://www.nexusmods.com/profile/Inidar/mods). Source code is on [GitHub](https://github.com/Inidar1/terraria-modder). See the [Installation Guide](installation.md) for step-by-step instructions.

### How do I update TerrariaModder?

**With the Vault:** It detects current active main-file versions and offers updates. Premium Nexus accounts download directly; free accounts complete Nexus's Manual Download / Slow Download flow in the embedded page.

**Manually:** Move the current Core folder aside, then replace it with the new Core folder. Replace a mod by moving its old folder aside and installing the complete new folder. Keep the backup until the new build has loaded your characters and worlds correctly. See [Installation - Updating](installation.md#updating-terrariamodder) for details.

### How do I uninstall TerrariaModder?

Delete `TerrariaInjector.exe` and the `TerrariaModder/` folder from your Terraria directory. Your game returns to vanilla. See [Installation - Uninstalling](installation.md#uninstalling) for details.

### My antivirus flags TerrariaInjector.exe

TerrariaInjector uses DLL injection to load mods, which is a technique that antivirus software sometimes flags. This is a false positive. You can verify the source code on GitHub, or add an exception in your antivirus. See [Troubleshooting](troubleshooting.md) for more help.

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

Mods are written in C# targeting .NET Framework 4.8. Use direct Terraria/XNA references for accessible game APIs and Harmony for runtime patches; reflection is still needed for inaccessible members and assembly boundaries. See [Making Your First Mod](making-your-first-mod.md) to get started.

### Do I need the Terraria source code to make mods?

No source license is required. Public Terraria types can be referenced from your installed assembly at compile time, while a decompiler such as ILSpy helps verify private members and exact method signatures before patching.

### How do I debug my mod?

TerrariaModder logs to `TerrariaModder/core/logs/`. Use `_log.Info()` calls in your mod code. Debug Tools 2.0.1 also provides an in-game console (Ctrl+`) and localhost HTTP API for advanced debugging.

### Can I distribute mods I create?

Yes. To appear in [TerrariaModder Vault](https://www.nexusmods.com/terraria/mods/159) Browse, publish on Nexus Mods, declare TerrariaModder Core (mod 135) in Nexus Requirements, and provide a current active main file. Mods distributed elsewhere can still be installed as local archives. See [The Vault](the-vault.md) for packaging requirements and the [Publishing Guide](publishing-your-mod.md) for the full distribution guide.
