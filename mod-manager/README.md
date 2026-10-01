# TerrariaModder Vault

A mod manager for [TerrariaModder](https://www.nexusmods.com/terraria/mods/135) mods on Nexus Mods.

## Features

- **Browse and install** — Browse published Nexus mods with a TerrariaModder tag or an active Core requirement, then install through the account-appropriate Nexus flow.
- **Update tracking** — Check for mod updates and update one mod or a selected set.
- **Mod management** — Enable, disable, or uninstall mods while preserving settings when requested.
- **Core framework** — Install and update the TerrariaModder Core framework.
- **Nexus integration** — Nexus SSO login, `nxm://` protocol handling, premium API downloads, and the embedded free-account Manual Download flow.
- **Compatibility guidance** — Current Terraria/Core requirements and declared dependencies produce warnings; users can continue when a mod may still work.

## Requirements

- Windows 10/11 for the supported runtime release.
- .NET 8 runtime (bundled in published builds).
- [WebView2 Runtime](https://developer.microsoft.com/en-us/microsoft-edge/webview2/) (normally present on Windows 10/11).
- Terraria from Steam or GOG.

Linux source and builds are kept aligned, but Linux runtime behavior is not currently tested.

## Getting started

1. Download `TerrariaModderVault.exe` from the latest release.
2. Run it and select your Terraria installation; Steam/GOG installs can be detected automatically.
3. Sign in with your Nexus Mods account in Settings.
4. Browse and install mods from Browse. Premium Nexus accounts download directly; free accounts complete Nexus's Manual Download / Slow Download step inside Vault's embedded browser.

Browse works before login with public tags and active mod-level requirements. Sign in with your own Nexus account for the complete catalog, including file-to-file requirements. Nexus file downloads and account operations also use that credential. No project or developer API key is embedded in the application.

For mod authors, either the **TerrariaModder** Nexus tag or an active requirement on [TerrariaModder Core](https://www.nexusmods.com/terraria/mods/135) qualifies a published mod for Browse. Core requirements may be set at the mod or file level; using both a tag and a requirement makes the page clear to players. A title or description mention alone is not a catalog signal. Upload the current release as an active MAIN file so Vault can select, compare, and install the intended version; a tagged page is not held out of Browse while its files are being prepared.

## Building from source

```bash
# Local debug build
dotnet build src/TerrariaModManager/TerrariaModManager.csproj

# Explicit platform publishes
dotnet publish src/TerrariaModManager/TerrariaModManager.csproj -c Release -r win-x64 --self-contained true -o publish/win-x64
dotnet publish src/TerrariaModManager/TerrariaModManager.csproj -c Release -r linux-x64 --self-contained true -o publish/linux-x64
```

`build.sh` replaces local publish outputs and creates distribution ZIPs; use it only when those artifacts are intended.

## Architecture and behavior

- Avalonia desktop UI with WebView2 for the Windows in-app Nexus download flow.
- Nexus GraphQL pages published Terraria mods, tags, active mod-level Core requirements, and current file versions. The signed-in user's key resolves effective file-to-file Core requirements in one paged API batch. The union is deduplicated and sorted deterministically; a dated cache and visible partial/failure status protect against evolving provider behavior. Current MAIN-file versions drive update labels rather than stale mod-page versions.
- Nexus Mods API v1 for authenticated metadata, update checks, and premium downloads.
- Nexus SSO protocol 2 WebSocket with connection-token reconnect.
- SharpCompress archive support for ZIP, 7z, and RAR.

Installs are staged and validated before replacing an enabled or disabled mod. Commit failures restore the previous folder and configuration. User configuration and save-like files survive updates without restoring stale packaged payload over the new version. Archive checks are limited to filesystem/data-safety boundaries; Vault does not impose a content allowlist on user-selected mods.

## License

[MIT License](LICENSE)
