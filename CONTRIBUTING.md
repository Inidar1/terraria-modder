# Contributing to TerrariaModder

TerrariaModder targets Terraria 1.4.5.8 on Windows. Contributions to Core or a mod should be built against the current Steam executable and tested through TerrariaInjector.

## Prerequisites

- Windows 10 or 11
- Terraria 1.4.5.8 from Steam
- .NET SDK 6 or later
- .NET Framework 4.8 Developer Pack (recommended for offline builds; the .NET SDK can restore reference assemblies when package restore is available)
- Git

## Setup

1. Fork and clone the repository.
2. Run `setup.bat` from the repository root.
3. Confirm the script finds Terraria and completes the Core test build.
4. Run `build.bat` to build Core and every public mod project.
5. Run `deploy.bat` to copy local build outputs into the linked development install.
6. Launch `Terraria/TerrariaInjector.exe`.

The scripts can be called from another working directory; they resolve paths from their own repository root. Output is written under `build/core/` and `build/plugins/`.

## Project structure

- `src/Core/`: loader, typed config, input, UI, assets, save support, networking, and server administration
- `src/<Mod>/`: released mod source, manifest, README, and assets
- `src/DebugTools/`: Debug Tools with a stable feature set
- `templates/ModTemplate/`: starter project for new mods
- `docs/`: MkDocs wiki source

## Starting a mod

Copy `templates/ModTemplate/` to `src/YourModName/`, then update the project, namespace, and `manifest.json`. The template inherits `ModBase`, so manifest metadata is authoritative, and implements `IModLifecycle` so its content/world callbacks run.

Use direct Terraria and XNA references for accessible APIs. Use reflection for inaccessible members or client/server assembly boundaries. Verify every private member name, overload, parameter order, and numeric Terraria ID against the executable version you are targeting.

Apply manual Harmony patches from `IModLifecycle.OnContentReady` and remove them by their unique Harmony ID from `Unload`.

## Configuration and manifests

Define user settings in a `ModConfig` subclass. Use `Client` and `Server` scopes plus `Range`, `Options`, `OptionProvider`, `RestartRequired`, and migration attributes as appropriate. Implement a public parameterless `OnConfigChanged` method when changes can apply immediately.

Each `manifest.json` must have a unique lowercase id, display name, semantic version, author, description, entry DLL, and accurate multiplayer category. Add download/homepage metadata when available.

## Validation

Before opening a pull request:

1. Run `build.bat` and resolve every error.
2. Deploy and drive the actual changed behavior in Terraria 1.4.5.8.
3. Check the newest `TerrariaModder/core/logs/terrariamodder.client.session-*.log` or server session log for exceptions.
4. Test save/reopen and full-inventory/storage boundaries for changes involving items, equipment, banks, chests, characters, worlds, configs, or sidecars.
5. Test Host & Play, a remote client, or a dedicated server when the changed behavior crosses that boundary.
6. Update the mod README and relevant wiki page when behavior or public API changes.

Keep changes focused and do not include generated `build/`, `bin/`, `obj/`, local Terraria files, saves, logs, or credentials.

## Pull requests

Explain the concrete trigger, previous behavior, resulting behavior, and validation performed. Do not include unrelated version bumps. Core changes affect every mod, so describe the compatibility impact and update the starter template or API docs when an author-facing contract changes.

Use the issue tracker or Discord for questions:

- https://github.com/Inidar1/terraria-modder/issues
- https://discord.gg/VvVD5EeYsK