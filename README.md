# TerrariaModder

[![Discord](https://img.shields.io/discord/1467363973526716572?color=5865F2&logo=discord&logoColor=white&label=Discord)](https://discord.gg/VvVD5EeYsK)

A lightweight modding framework for Terraria 1.4.5.8. Launch through TerrariaInjector for modded play or launch Terraria normally for vanilla.

## Download

Install Core and mods with [TerrariaModder Vault](https://www.nexusmods.com/terraria/mods/159), or download Core and individual mods from [Nexus Mods](https://www.nexusmods.com/profile/Inidar/mods).

Back up characters and worlds you care about before testing an update. For manual installation, replace the complete Core or mod folder instead of merging an old and new package.

## Released mods

| Mod | Description | Default input | Download |
| --- | --- | --- | --- |
| Mod Menu | Configure mods and rebind keys | F6 | Included in [Core](https://www.nexusmods.com/terraria/mods/135) |
| Skip Intro | Waits for loading, then skips the ReLogic splash | Automatic | [Nexus](https://www.nexusmods.com/terraria/mods/140) |
| Quick Keys | Torch, recall, quick stack, ruler, and optional slots 11-20 | Tilde, Home, End, K | [Nexus](https://www.nexusmods.com/terraria/mods/143) |
| Item Spawner | Search and spawn vanilla or registered modded items | Insert | [Nexus](https://www.nexusmods.com/terraria/mods/141) |
| Auto Furniture Buffs | Applies buffs from nearby furniture, including the Dead Cells station | Automatic | [Nexus](https://www.nexusmods.com/terraria/mods/138) |
| Pet Chests | Use cosmetic pets as portable piggy banks | Right-click pet | [Nexus](https://www.nexusmods.com/terraria/mods/142) |
| Storage Hub | Registered storage, crafting, shimmer, progression, relays, and Mysterious Chest | F5 | [Nexus](https://www.nexusmods.com/terraria/mods/136) |
| Admin Panel | God mode, movement, time, teleport, respawn, and NPC controls | Backslash, F9 | [Nexus](https://www.nexusmods.com/terraria/mods/137) |
| Whip Stacking | Enables Terraria's native five-effect whip-tag capacity | Automatic | [Nexus](https://www.nexusmods.com/terraria/mods/139) |
| Seed Lab | Mix secret-seed world-generation and singleplayer runtime features | F10 | [Nexus](https://www.nexusmods.com/terraria/mods/144) |
| FPS Unlocked | High-rate rendering with 60 Hz game simulation and interpolation | Automatic | [Nexus](https://www.nexusmods.com/terraria/mods/145) |
| Biome Spread Control | Prevent corruption, crimson, and Hallow spread | Automatic | [Nexus](https://www.nexusmods.com/terraria/mods/146) |
| Randomizer | Deterministic modules for loot, drops, recipes, shops, spawns, and more | Numpad / | [Nexus](https://www.nexusmods.com/terraria/mods/147) |

Public Debug Tools 2.0.0 is available from the Core optional files for mod development and advanced diagnostics. Its stable feature set includes an in-game console, localhost HTTP API, virtual input, state inspection, and window controls.

Each mod's README states whether it is client-only, optional, singleplayer-only, or required on every peer.

## Create a mod

- [Wiki and guides](https://inidar1.github.io/terraria-modder/)
- [Starter template](templates/ModTemplate)
- [Core API reference](docs/core-api-reference.md)
- [Harmony guide](docs/harmony-basics.md)
- [Current released mod source](src/)

New mods can inherit <code>ModBase</code> so identity comes from <code>manifest.json</code>. Implement <code>IModLifecycle</code> for content-ready and world load/unload callbacks. Core provides typed configuration, dynamic option lists, keybinds, UI widgets, commands, events, custom items, save support, and multiplayer services.

## Build from source

Requirements:

- Windows 10 or 11
- Terraria 1.4.5.8 from Steam
- .NET SDK 6 or later
- .NET Framework 4.8 Developer Pack (recommended for offline builds; the .NET SDK can restore reference assemblies when package restore is available)

Run <code>setup.bat</code> once to link the local Terraria installation and test Core, then use <code>build.bat</code>. Build output is written under <code>build/core/</code> and <code>build/plugins/</code>. <code>deploy.bat</code> copies those local builds into the linked development install while preserving existing mod data files.

See [CONTRIBUTING.md](CONTRIBUTING.md) for the full workflow.

## Documentation

- [Installation](docs/installation.md)
- [Available mods](docs/finding-mods.md)
- [Making your first mod](docs/making-your-first-mod.md)
- [Core API](docs/core-api-reference.md)
- [Troubleshooting](docs/troubleshooting.md)

## Credits

TerrariaInjector is made by [ConfuzzedCat](https://github.com/ConfuzzedCat/TerrariaInjector) and included in releases with permission. TerrariaModder uses [Harmony](https://github.com/pardeike/Harmony) and [Mono.Cecil](https://github.com/jbevain/cecil).

## License

MIT License