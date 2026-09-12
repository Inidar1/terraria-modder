---
title: Auto Furniture Buffs - Terraria 1.4.5.8 Mod Walkthrough
description: Scan nearby furniture efficiently and apply its current Terraria buffs to the local player.
---

# Auto Furniture Buffs Walkthrough

Auto Furniture Buffs scans a configurable radius around the local player and applies enabled furniture buffs automatically.

## Supported furniture

| Furniture | Effect |
| --- | --- |
| Crystal Ball | Clairvoyance |
| Ammo Box | Ammo Box |
| Bewitching Table | Bewitched |
| Sharpening Station | Sharpened |
| War Table | War Table |
| Slice of Cake | Sugar Rush |
| Dead Cells Potion Station | Extended potion duration |

The tile and buff identifiers are verified against Terraria 1.4.5.8.

## Efficient scanning

A postfix on <code>Player.Update(int)</code> runs only for the local player after content is ready. It waits briefly after world entry, scans every ten updates, skips disabled furniture, and avoids searching for buffs that are already active. Reused sets avoid per-scan allocations.

## Configuration

Every furniture type has an independent F6 toggle. The radius defaults to 40 tiles and can be set from 5 to 100. Larger radii examine more tiles per scan.

## Multiplayer

The mod applies buffs to the local player and can be installed as an optional client mod.

## Source

See <code>src/AutoBuffs/</code> in the TerrariaModder repository.