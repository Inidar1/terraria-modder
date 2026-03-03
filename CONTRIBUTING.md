# Contributing to TerrariaModder

Thanks for your interest in contributing! This guide will get you set up and explain how we work.

## Prerequisites

- **Windows 10/11** (Terraria modding is Windows-only)
- **[.NET SDK](https://dotnet.microsoft.com/download)** (6.0 or later)
- **[.NET Framework 4.8 Developer Pack](https://dotnet.microsoft.com/download/dotnet-framework/net48)** (or Visual Studio 2022, which includes both)
- **Terraria 1.4.5** installed via Steam

## Getting Started

### 1. Fork and clone

Fork this repo on GitHub, then clone your fork:

```bash
git clone https://github.com/YOUR-USERNAME/terraria-modder.git
cd terraria-modder
```

### 2. Run setup

```bash
setup.bat
```

This will:
- Verify your .NET installation
- Find your Terraria install (auto-detects Steam, or asks you)
- Link it into the repo so builds can reference `Terraria.exe`
- Run a test build to make sure everything works

### 3. Build

```bash
build.bat
```

Builds the Core framework and all mods. Output goes to `build/`.

### 4. Deploy and test

```bash
deploy.bat
```

Copies the built DLLs into your Terraria's mod folder. Then launch with:

```bash
Terraria\TerrariaInjector.exe
```

## Project Structure

```
src/
├── Core/           # The framework. Mod loading, config, UI, events, input.
│                   # Don't modify unless necessary — changes here affect all mods.
├── AdminPanel/     # God mode, teleports, time control
├── AutoBuffs/      # Auto-apply furniture buffs
├── DebugTools/     # HTTP debug server, in-game console
├── FpsUnlocked/    # Framerate unlock with interpolation
├── ItemSpawner/    # In-game item spawner UI
├── PetChests/      # Pets as portable piggy banks
├── QuickKeys/      # Auto-torch, recall, quick-stack hotkeys
├── SeedLab/        # Secret seed feature toggling
├── SkipIntro/      # Skip ReLogic splash
├── StorageHub/     # Unified storage + crafting UI
└── WhipStacking/   # Pre-1.4.5 whip tag stacking
```

Each mod folder contains:
- `{ModName}.csproj` — Build project
- `manifest.json` — Mod metadata (id, name, version, config, keybinds)
- `Mod.cs` — Entry point implementing `IMod`
- Additional classes as needed

### How mods work

Mods are .NET Framework 4.8 class libraries that implement `IMod`. The Core framework loads them at runtime. Most mods use [Harmony](https://harmony.pardeike.net/) to patch Terraria's methods without modifying the game files.

See the [Wiki](https://inidar1.github.io/terraria-modder/) for the full API reference and modding tutorials.

## Making Changes

### Pick what to work on

Check [Issues](https://github.com/Inidar1/terraria-modder/issues) for open tasks. Issues labeled `good first issue` are a great starting point. If you want to work on something, comment on the issue so others know.

For new ideas, open an issue first to discuss before writing code.

### Branch and develop

```bash
git checkout -b my-feature
# make changes
build.bat
deploy.bat
# test in-game
```

### What to test

- **Build passes**: `build.bat` completes without errors
- **In-game**: Launch with `TerrariaInjector.exe`, verify your changes work
- **No regressions**: Other mods still work (especially if you touched Core)
- **Check logs**: `Terraria\TerrariaModder\core\logs\terrariamodder.log` for errors

### Submit a PR

Push your branch and open a pull request. The PR template will guide you through what to include.

Keep PRs focused — one feature or fix per PR. Small PRs get reviewed faster.

## Code Guidelines

- **Follow existing patterns** — Look at how similar mods do things before inventing new approaches
- **Use the Core API** — Config, keybinds, events, UI components are all provided. See the [API Reference](https://inidar1.github.io/terraria-modder/core-api-reference/)
- **Don't modify Core without discussion** — Core changes affect every mod. Open an issue first
- **Use Harmony responsibly** — Prefix/postfix patches only. Avoid transpilers unless absolutely necessary
- **Test in-game** — Mods interact with a live game. Unit tests can't catch everything

## Code Ownership

See [CODEOWNERS](.github/CODEOWNERS) for who reviews what. PRs are auto-assigned to the right reviewer based on which files you change.

## Getting Help

- **[Discord](https://discord.gg/VvVD5EeYsK)** — Fastest way to get answers
- **[Wiki](https://inidar1.github.io/terraria-modder/)** — Guides, API docs, mod walkthroughs
- **[Starter Template](templates/ModTemplate)** — If you're creating a new mod from scratch
