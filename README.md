# TerrariaModder

A lightweight modding framework for Terraria 1.4.5. No tModLoader required.

## Download

Get the latest release from [Nexus Mods](https://www.nexusmods.com/profile/Inidar/mods), or follow build instructions below. Extract to your Terraria folder and run `TerrariaInjector.exe`.

## Create Your Own Mods

TerrariaModder is built to be modder-friendly. Everything you need to get started:

- **[Wiki & Guides](https://inidar1.github.io/terraria-modder/)** — Installation, first mod tutorial, Harmony basics, API reference, and walkthroughs of every included mod
- **[Starter Template](templates/ModTemplate)** — Ready-to-build mod template so you can have a working mod in minutes
- **Example Mods** — Every mod in this repo is open source with full source code. Use them as reference for real-world patterns
- **Built-in UI Library** — Panels, buttons, sliders, text fields, scrollable lists, tabs, and automatic colorblind support
- **Automatic Config UI** — Define config fields and the mod menu generates the settings UI for you

A GUI mod manager app is coming soon.

## Included Mods

| Mod | Description | Default Key |
|-----|-------------|-------------|
| ModMenu | In-game mod configuration | F6 |
| SkipIntro | Skips the ReLogic splash screen | (auto) |
| QuickKeys | Auto-torch, recall, quick-stack, ruler, extended hotbar (opt-in) | Tilde, Home, End, K |
| ItemSpawner | In-game item spawner | Insert |
| AutoBuffs | Automatically applies furniture buffs | (auto) |
| PetChests | Use pets as portable piggy banks | (right-click) |
| StorageHub | Unified storage with crafting, recipes, shimmer decraft, painting chest, relay network | F5 |
| AdminPanel | God mode, movement speed, teleports, time controls, respawn | Backslash, F9 |
| WhipStacking | Restores pre-1.4.5 whip tag stacking | (auto) |
| SeedLab | Toggle secret seed features for world gen (WIP) | F10 |

**Optional (separate download):**

| Mod | Description | Default Key |
|-----|-------------|-------------|
| DebugTools | Debug HTTP server, in-game console, virtual input, window management | Ctrl+` |

## Multiplayer

TerrariaModder is client-side only. Mods that only affect your own player (like keybinds, UI, and buffs) work fine in multiplayer. Mods that modify shared game state (chests, time, NPCs) are singleplayer only for now. Each mod page notes its multiplayer status. Full multiplayer support is planned for a future update.

## Building from Source

Requires [.NET SDK](https://dotnet.microsoft.com/download) and the [.NET Framework 4.8 Developer Pack](https://dotnet.microsoft.com/download/dotnet-framework/net48). Both are included with Visual Studio 2022.

```bash
# Build all mods
dotnet build src/Core/TerrariaModder.Core.csproj -c Release
dotnet build src/SkipIntro/SkipIntro.csproj -c Release
# ... etc for each mod in src/
```

Each mod builds to a single DLL. Place them in `Terraria/TerrariaModder/mods/<mod-id>/` alongside their `manifest.json`.

## Documentation

See the [Wiki](https://inidar1.github.io/terraria-modder/) for guides and reference, or browse the docs directly:

- [Installation Guide](docs/installation.md)
- [Making Your First Mod](docs/making-your-first-mod.md)
- [Core API Reference](docs/core-api-reference.md)
- [Harmony Basics](docs/harmony-basics.md)
- [Tested Patterns](docs/tested-patterns.md)
- [Troubleshooting](docs/troubleshooting.md)

## Credits

Special thanks to [ConfuzzedCat](https://github.com/ConfuzzedCat) for [TerrariaInjector](https://github.com/ConfuzzedCat/TerrariaInjector), the injector that makes this entire project possible. Included in releases with their permission; check out the source at their repo.

Built with [Harmony](https://github.com/pardeike/Harmony) (runtime patching) and [Mono.Cecil](https://github.com/jbevain/cecil) (assembly inspection), both MIT licensed.

## License

MIT License
