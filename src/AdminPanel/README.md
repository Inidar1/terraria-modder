# Admin Panel

A quick-access panel for administrative and testing controls in singleplayer and multiplayer.

## Features

- God mode, full health, and full mana
- Movement speed from 1x to 10x
- Dawn, noon, dusk, and night presets with time speed from 1x to 60x
- Teleport to spawn, dungeon, hell, beach, bed, or a random valid location
- Separate valid respawn-time presets for normal and boss deaths
- Boss and NPC catalogs with search, favorites, and spawn controls

## Keybinds

| Default | Action |
| --- | --- |
| Backslash | Open or close the panel |
| F9 | Toggle god mode |

Both keybinds can be changed in the F6 Mod Menu.

## Respawn presets

Normal deaths: 1, 2, 3, 5, 10, 15, 20, 30, or 45 seconds.

Boss deaths: 2, 5, 7, 10, 20, 30, 45, 60, or 90 seconds.

Saved indices are validated before use so an invalid configuration cannot create a negative or out-of-range respawn value.

## Multiplayer

Works in singleplayer, Host & Play, and on dedicated servers. Multiplayer actions that change server state require administrator permission; a Host & Play host is an administrator automatically.

## Installation

Requires TerrariaModder Core. Replace the existing <code>TerrariaModder/mods/admin-panel/</code> folder with the downloaded mod folder, then launch through <code>TerrariaInjector.exe</code>.