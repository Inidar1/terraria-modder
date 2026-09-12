---
title: The TerrariaModder Vault - Mod Manager & Launcher
description: What the TerrariaModder Vault is, how Nexus discovery works, and how to package a mod for reliable installs and updates.
nav_order: 8.5
---

# The TerrariaModder Vault

The [TerrariaModder Vault](https://www.nexusmods.com/terraria/mods/159) is the official mod manager and launcher for TerrariaModder. It finds compatible Nexus releases, manages local Core and mod installations, and launches either modded or vanilla Terraria.

As a modder, understanding the Vault matters because **it's the primary way players will find and install your mod.** A Vault-compatible mod installs cleanly, shows the right version, and surfaces its settings in the Mod Menu automatically.

## What the Vault Does (Player Side)

Players use the Vault to:
- Browse Nexus mods that declare TerrariaModder Core as a requirement
- Install directly with Nexus Premium, or complete Nexus's Manual Download / Slow Download flow with a free account
- Keep mods up to date
- Launch Terraria with mods active via the **Launch Modded** button
- Enable and disable individual mods without touching files
- Install local mod archives, including mods that are not listed on Nexus

When a player installs your mod through the Vault, it downloads the selected release through the account-appropriate Nexus flow, validates and stages the archive, and then replaces the installed mod transactionally. Existing configuration is retained during ordinary updates. On next launch, Core loads the installed folder under `TerrariaModder/mods/`.

## Making Your Mod Vault-Compatible

### 1. Publish on Nexus Mods

The Vault installs mods from Nexus Mods. To be available through it:
- Create a mod page at [nexusmods.com/terraria](https://www.nexusmods.com/terraria)
- Add **TerrariaModder Core** ([Nexus mod 135](https://www.nexusmods.com/terraria/mods/135)) to the mod's Nexus **Requirements**
- Upload your mod zip as the **main file** on the Files tab

The Nexus requirement is the canonical discovery signal. A title, description, or tag containing "TerrariaModder" does not by itself put a mod in the Vault catalog. The Vault selects the current active main file and excludes archived or unavailable releases.

See [Publishing Your Mod](publishing-your-mod.md) for the full packaging guide.

### 2. Correct Zip Structure

The recommended layout is a single top-level folder named after your mod ID:

```
your-mod-id.zip
└── your-mod-id/
    ├── manifest.json    ← required
    ├── YourMod.dll      ← required
    ├── icon.png         ← optional but recommended
    └── README.md        ← optional but recommended
```

**The folder name should match the `id` field in your manifest.json.** The Vault can normalize a mismatched wrapper folder from a valid package, but matching names keep manual installs and third-party tooling predictable.

### 3. Complete manifest.json

All required fields must be present and valid:

```json
{
  "id": "your-mod-id",
  "name": "Your Mod Name",
  "version": "1.0.0",
  "author": "Your Name",
  "description": "Clear, one-line description of what the mod does.",
  "entry_dll": "YourMod.dll",
  "framework_version": ">=0.2.0"
}
```

| Field | Required | Notes |
|-------|----------|-------|
| `id` | Yes | Lowercase, hyphens only. Must match the top-level folder in your zip. |
| `name` | Yes | Display name shown in the Mod Menu and Vault |
| `version` | Yes | Semantic version: `major.minor.patch` |
| `author` | Yes | Your name or handle |
| `description` | Yes | Shown in the mod list |
| `entry_dll` | No | Inferred from `id` if omitted (`your-mod-id` → `YourModId.dll`) |
| `framework_version` | Recommended | Minimum Core version required. Use `>=0.X.X` format. |

### 4. Semantic Versioning

Use `major.minor.patch` versioning. The Vault uses this to detect available updates:

- **Patch** (`1.0.0 → 1.0.1`) — Bug fixes only
- **Minor** (`1.0.0 → 1.1.0`) — New features, backward compatible
- **Major** (`1.0.0 → 2.0.0`) — Breaking changes or major overhaul

Always increment the version when uploading a new file to Nexus. Never reuse a version number — the Vault considers the same version already installed and won't re-download it.

### 5. Declare Your Framework Version

If your mod uses APIs introduced in a specific Core version, declare it:

```json
"framework_version": ">=0.2.0"
```

The Vault uses this to warn when the installed Core may be incompatible. The player can continue after acknowledging the warning because some mods work across a wider range than their metadata states. Structurally unsafe or invalid packages are still rejected. Use the Core version you built and tested against. When in doubt, check the `<Version>` in Core's `.csproj` or the overlay on the Terraria title screen.

## Config and Settings Conventions

### Define Your Config Schema

Config defined in manifest.json is automatically surfaced in the Mod Menu (F6) and respected by the Vault. Always prefer this over hardcoded values so users can maintain configs between updates:

```json
"config_schema": {
  "enabled": {
    "type": "bool",
    "default": true,
    "label": "Enable Mod",
    "description": "Master on/off toggle."
  },
  "myFeature": {
    "type": "bool",
    "default": false,
    "label": "My Feature",
    "description": "What this does."
  }
}
```

Supported types: `bool`, `int`, `float`, `string`.

**Always provide sensible defaults.** The Vault installs mods into a clean state — no existing config file. Your defaults are what every new player gets on first launch.

Config is stored at `TerrariaModder/mods/{mod-id}/config.json` and hot-reloads without a game restart.

### Keybind Conventions

Declare keybinds in manifest.json so they appear in the Mod Menu and are rebindable:

```json
"keybinds": [
  {
    "id": "toggle",
    "label": "Toggle My Mod",
    "description": "Opens or closes the mod UI.",
    "default": "F7"
  }
]
```

Avoid conflicts with keys already used by bundled mods:

| Key | Used by |
|-----|---------|
| F5 | StorageHub |
| F6 | ModMenu (Core) |
| F9 | AdminPanel god mode |
| F10 | SeedLab |
| Insert | ItemSpawner |
| Backslash | AdminPanel toggle |
| Ctrl+` | DebugTools |

`MouseLeft` cannot be used as a keybind (reserved for UI interaction). `MouseRight` and `MouseMiddle` are fine.

## Pre-Release Checklist

Before uploading to Nexus:

- [ ] Zip has a **single** top-level folder matching your `id`
- [ ] Nexus **Requirements** includes TerrariaModder Core (mod 135)
- [ ] `manifest.json` has all required fields
- [ ] `version` is higher than any previously uploaded version
- [ ] `framework_version` declared if you use recent Core APIs
- [ ] All config fields have sensible `default` values
- [ ] Keybinds don't conflict with Core or bundled mods
- [ ] Tested with a **clean install** (delete `mods/{your-mod-id}/` before testing)

## Testing the Full Install Path

Before announcing your mod, verify it works end-to-end through the Vault:

1. Download the Vault from [Nexus](https://www.nexusmods.com/terraria/mods/159)
2. Install your mod through the Vault (not manually)
3. Launch with **Launch Modded**
4. Verify your mod appears in the Mod Menu (F6) with the correct name and version
5. Verify config defaults are applied (no leftover settings from your dev environment)
6. Test all features from the clean default state
