# TerrariaModder Core

A lightweight runtime mod framework for Terraria 1.4.5.8. It loads .NET Framework mods through TerrariaInjector and provides shared configuration, input, UI, assets, save support, networking, and server administration.

## Features

- Loads mods from <code>TerrariaModder/mods/</code> using each mod's <code>manifest.json</code>
- Harmony-based runtime patching without replacing Terraria.exe
- F6 Mod Menu with typed settings, validation, editable text, runtime option lists, keybind rebinding, and accessible themes
- Optional <code>ModBase</code> metadata binding while retaining binary compatibility with existing <code>IMod</code> mods
- Content-ready and world lifecycle callbacks through <code>IModLifecycle</code>
- Custom item registration with stable identities, texture injection, recipes, shops, drops, shimmer mappings, and missing-mod recovery
- Save journaling and atomic sidecar writes for character, equipment, bank, world, and custom-item data
- Host & Play and dedicated-server support with native Invite Friends flow, permissions, scoped configuration, and server-authoritative operations
- Reusable UI widgets, command registration, events, logging, and networking helpers

## Installation

1. Back up characters and worlds you care about.
2. Move an existing <code>TerrariaModder/core/</code> folder out of the Terraria directory.
3. Extract the new Core package into the Terraria folder.
4. Launch <code>TerrariaInjector.exe</code> for modded play. Launching <code>Terraria.exe</code> directly remains the vanilla path.
5. Install each mod by replacing its complete folder under <code>TerrariaModder/mods/</code>.

## Layout

~~~text
Terraria/
|-- Terraria.exe
|-- TerrariaInjector.exe
`-- TerrariaModder/
    |-- core/
    |   |-- TerrariaModder.Core.dll
    |   |-- config.json
    |   |-- deps/
    |   |-- logs/
    |   `-- Docs/
    `-- mods/
        `-- example-mod/
            |-- manifest.json
            |-- ExampleMod.dll
            `-- README.md
~~~

## Session logs and console

Each client writes a separate file named <code>terrariamodder.client.session-&lt;UTC time&gt;-&lt;PID&gt;-&lt;id&gt;.log</code>. Dedicated servers use the corresponding <code>terrariamodder.server.session-...</code> name. Core keeps the latest 20 session files per role. Combined <code>terrariamodder.log</code> and <code>terrariamodder.server.log</code> files remain for compatibility with existing tools.

Set <code>hideConsole</code> to <code>true</code> in <code>TerrariaModder/core/config.json</code> to hide the extra Windows client console after the game becomes ready. Restart to apply. Dedicated-server consoles and shared terminals are not hidden.

## Multiplayer

Core preserves Terraria's normal Host & Play flow, including Invite Friends. Dedicated servers expose administrator, permission, configuration, and storage services needed by compatible mods. A mod's manifest and README state whether it is client-only, optional, or required on every peer.

## For mod authors

Source, the starter template, API reference, and walkthroughs are available at https://github.com/Inidar1/terraria-modder and https://inidar1.github.io/terraria-modder/.

New mods can inherit <code>ModBase</code> so identity comes from <code>manifest.json</code>. Implement <code>IModLifecycle</code> when the mod needs content-ready or world load/unload callbacks. Existing direct <code>IMod</code> implementations remain supported.

## License and credits

TerrariaModder Core is MIT licensed. It uses Harmony and Mono.Cecil. TerrariaInjector is maintained by ConfuzzedCat and distributed under GPL-3.0; see <code>THIRD-PARTY-NOTICES.md</code> when redistributing the combined package.