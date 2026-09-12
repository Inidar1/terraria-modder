---
title: Publishing a Terraria 1.4.5.8 Mod
description: How to package, publish, and distribute your Terraria 1.4.5.8 mod. Covers manifest.json, Nexus Mods uploads, GitHub releases, and versioning.
nav_order: 9
---

# Publishing Your Mod

Once your mod is working locally, here's how to share it with other players.

## Preparing for Release

### 1. Test Thoroughly

Before releasing:
- Test all features multiple times
- Test with other mods enabled
- Test after game restarts
- Check logs for errors

### 2. Clean Up Your Code

- Remove debug logging (or keep minimal)
- Remove commented-out code
- Ensure meaningful variable names

### 3. Complete Your manifest.json

```json
{
  "id": "your-mod-id",
  "name": "Your Mod Name",
  "version": "1.0.0",
  "description": "Clear description of what your mod does",
  "author": "Your Name",
  "entry_dll": "YourMod.dll",
  "framework_version": ">=0.4.0",
  "multiplayer": "client-only",
  "icon": "icon.png",
  "keybinds": []
}
```

Required fields:
- `id` - Unique identifier (lowercase, hyphens only)
- `name` - Display name
- `version` - Semantic version (major.minor.patch)
- `author` - Your name
- `description` - What the mod does
- `entry_dll` - Your compiled DLL filename (if omitted, inferred from id)

Optional but recommended:
- `multiplayer` - `"required"` (all players need it), `"optional"` (server has it, clients can join without), or `"client-only"` (no server impact). Mods with custom items are automatically `required`.
- `framework_version` - Minimum Core version (e.g., `">=0.4.0"`)
- `icon` - Path to icon.png (displayed in mod menu)

### 4. Choose a License

Add a LICENSE file to your mod folder. Common choices:
- MIT - Permissive, allows anything
- GPL - Requires derivative works to be open source
- Unlicense - Public domain

## Package Structure

Your release should be a zip file containing:

```
YourModName/
├── manifest.json       <- Required
├── YourMod.dll        <- Your compiled mod
├── icon.png           <- Optional: mod icon (shown in UI headers)
├── README.md          <- Optional but recommended
└── LICENSE            <- Optional but recommended
```

### Creating the Package

1. Build your mod in Release mode
2. Create a folder with your mod ID
3. Copy the required files into it
4. Zip the folder

```batch
@echo off
set MOD_NAME=your-mod

mkdir %MOD_NAME%
copy bin\Release\YourMod.dll %MOD_NAME%\
copy manifest.json %MOD_NAME%\
copy README.md %MOD_NAME%\
copy LICENSE %MOD_NAME%\

powershell Compress-Archive -Path %MOD_NAME% -DestinationPath %MOD_NAME%.zip
rmdir /s /q %MOD_NAME%
```

## Where to Publish

### Nexus Mods (Recommended)

Publish on [Nexus Mods](https://www.nexusmods.com/terraria) to reach the most players. The **[TerrariaModder Vault](https://www.nexusmods.com/terraria/mods/159)** discovers mods through Nexus's declared requirements rather than a name or description search.

1. Create a mod page at [nexusmods.com/terraria](https://www.nexusmods.com/terraria)
2. Add **TerrariaModder Core** ([mod 135](https://www.nexusmods.com/terraria/mods/135)) in the page's **Requirements**
3. Upload your mod zip as an active **main file** on the Files tab
4. The current active main release can now appear in Vault Browse

See [The Vault](the-vault.md) for packaging requirements (zip structure, manifest.json fields, versioning) that ensure your mod installs cleanly through the Vault.

### GitHub Releases

Also recommended — keeps your source available and provides a version history:

1. Create a GitHub repository for your mod
2. Push your source code
3. Go to Releases > Create new release
4. Tag with version number (v1.0.0)
5. Upload your zip file
6. Write release notes

### Terraria Forums

Post in the Terraria Community Forums:
- Include clear description
- Screenshots if applicable
- Link to download
- Installation instructions

### Discord

Share in Terraria modding Discord servers:
- Provide direct download link
- Explain what it does
- Note any requirements

## Installation Instructions for Users

If your mod declares TerrariaModder Core in Nexus Requirements, players can install it through the [TerrariaModder Vault](https://www.nexusmods.com/terraria/mods/159). Premium accounts download directly; free accounts use Nexus's Manual Download / Slow Download controls in the embedded page. For fully manual installs, include this in your README:

```markdown
## Installation

### Option A: TerrariaModder Vault (Recommended)
Install from the [TerrariaModder Vault](https://www.nexusmods.com/terraria/mods/159). Free Nexus accounts complete the Manual Download / Slow Download step; Premium accounts download directly.

### Option B: Manual
1. Install TerrariaModder framework (if not already installed)
2. Download `your-mod.zip`
3. Extract to `Terraria/TerrariaModder/mods/`
4. Launch game with TerrariaInjector.exe

Your folder structure should look like:
Terraria/TerrariaModder/mods/your-mod/
├── manifest.json
└── YourMod.dll
```

## Versioning

Use semantic versioning:
- **Major** (1.0.0 -> 2.0.0): Breaking changes
- **Minor** (1.0.0 -> 1.1.0): New features, backward compatible
- **Patch** (1.0.0 -> 1.0.1): Bug fixes

Update `version` in manifest.json with each release.

## Updating Your Mod

When releasing updates:

1. Increment version number appropriately
2. Write changelog describing changes
3. Test the update process (install over old version)

## Common Issues

### "Mod not loading"

Users should check:
- manifest.json is valid JSON
- DLL name matches manifest id
- Folder is in mods/ directory
- Using correct Terraria version

### "Feature X doesn't work"

- Check if feature requires specific game state (world loaded, etc.)
- Verify keybinds aren't conflicting
- Look for errors in logs

## Tips for Success

1. **Declare the Core requirement on Nexus Mods** - Makes the current active main file discoverable through the [TerrariaModder Vault](the-vault.md)
2. **Clear naming** - Make it obvious what your mod does
3. **Good documentation** - README with features, installation, keybinds
4. **Responsive** - Answer questions, fix reported bugs
5. **Updates** - Keep compatible with new Terraria versions
6. **Source available** - Helps others learn and contribute
