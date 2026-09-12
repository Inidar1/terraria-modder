---
title: Biome Spread Control - Terraria 1.4.5.8 Mod Walkthrough
description: Control corruption, crimson, and Hallow spread while leaving unrelated world updates intact.
---

# Biome Spread Control Walkthrough

Biome Spread Control prevents corruption, crimson, and Hallow from converting new tiles. It is enabled by default and can be changed immediately from the Mod Menu.

## Behavior

When Disable Evil Spread is on:

- Terraria's infection-spread pass is suppressed.
- Evil and Hallow grass cannot grow naturally onto bare dirt or mud.
- World generation and explicit recursive grass operations retain their normal behavior.
- Crystal shard and chlorophyte growth are unaffected.

Turning the option off restores Terraria's normal spread flag immediately.

## Implementation

The mod patches <code>WorldGen.hardUpdateWorld</code> before each update and disables infection spread while the option is active. A second prefix on <code>WorldGen.SpreadGrass</code> blocks natural evil or Hallow grass propagation without blocking world generation.

The tile identifiers come from Terraria's current <code>TileID</code> definitions.

## Configuration

Open F6 and select Biome Spread Control.

| Setting | Default | Effect |
| --- | --- | --- |
| Enabled | On | Enables the mod |
| Disable Evil Spread | On | Prevents corruption, crimson, and Hallow spread |

## Multiplayer

This mod is for singleplayer worlds. Multiplayer world spread is server-owned.

## Source

See <code>src/BiomeSpread/</code> in the TerrariaModder repository.