# Biome Spread Control

Prevents corruption, crimson, and Hallow from spreading to new tiles in singleplayer worlds.

## Behavior

When Disable Biome Spread is enabled:

- Terraria's infection-spread update is suppressed.
- Evil and Hallow grass cannot grow naturally onto bare dirt or mud.
- Crystal shard and chlorophyte growth continue normally.
- World generation and explicit recursive grass operations retain their normal behavior.

The option is enabled by default. Turning it off restores Terraria's normal spread flag immediately without restarting.

## Configuration

| Setting | Default | Description |
| --- | --- | --- |
| Disable Biome Spread | On | Prevents corruption, crimson, and Hallow spread |

Open the F6 Mod Menu to change this setting.

## Multiplayer

This mod is for singleplayer worlds. Multiplayer world spread is server-owned.

## Installation

Requires TerrariaModder Core. Replace the existing <code>TerrariaModder/mods/biome-spread/</code> folder with the downloaded mod folder, then launch through <code>TerrariaInjector.exe</code>.