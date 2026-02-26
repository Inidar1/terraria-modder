# Repository Guidelines

## Project Structure & Module Organization
Core framework code lives in `src/Core` (`TerrariaModder.Core.csproj`). Each mod is a separate project under `src/<ModName>/` with:
- `<ModName>.csproj`
- `Mod.cs`
- `manifest.json`
- optional supporting classes/assets

Use `templates/ModTemplate/` when creating a new mod. Documentation source is in `docs/`, built via `mkdocs.yml`. Build outputs go to `build/core/` (core DLL) and `build/plugins/` (mod DLLs).

## Build, Test, and Development Commands
Run from repo root:

```powershell
# Build core
dotnet build src/Core/TerrariaModder.Core.csproj -c Release

# Build one mod
dotnet build src/StorageHub/StorageHub.csproj -c Release

# Build all projects under src/
Get-ChildItem src -Recurse -Filter *.csproj | ForEach-Object { dotnet build $_.FullName -c Release }
```

Docs commands:

```powershell
pip install mkdocs-material
mkdocs serve   # local docs preview
mkdocs build   # CI-equivalent static build
```

## Coding Style & Naming Conventions
This repo is C# (`net48`, `LangVersion=latest`) with Allman braces and 4-space indentation. Follow existing naming patterns:
- `PascalCase` for types/methods/properties
- `_camelCase` for private fields
- descriptive file names matching primary types

For manifests, keep `id` lowercase kebab-case (example: `quick-keys`) and align `entry_dll` with the assembly name.

## Testing Guidelines
There is currently no dedicated automated test project. Validate changes with manual smoke tests in Terraria 1.4.5:
1. Build the affected project(s).
2. Copy DLL + `manifest.json` to `Terraria/TerrariaModder/mods/<mod-id>/`.
3. Launch via `TerrariaInjector.exe`.
4. Verify feature behavior and check logs for errors.

When adding pure logic components, prefer introducing small unit-testable classes and (if feasible) a `tests/` project.

## Commit & Pull Request Guidelines
Recent history is mostly release-style commits (for example, `v0.1.0`). For normal development commits, use clear, scoped messages (example: `StorageHub: fix recursive craft refresh`).

PRs should include:
- concise summary of behavior changes
- affected mod(s)/paths
- manual test steps performed
- screenshots/GIFs for UI changes
- docs updates when APIs, manifests, or user workflows change
