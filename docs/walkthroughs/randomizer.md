---
title: Randomizer - Terraria 1.4.5.8 Mod Walkthrough
description: Configure deterministic Terraria randomizer modules for reproducible singleplayer runs.
---

# Randomizer Walkthrough

Randomizer provides independent modules for chest loot, enemy drops, recipes, shops, fishing, tile drops, spawns, item stats, starting items, gravity, and weather. Press Numpad / on the title screen or in a world to open its panel.

## Deterministic seeds

A nonzero seed produces the same module results across game restarts and computers. Each module derives its own stable sub-seed, so enabling or disabling another module does not silently change unrelated randomization.

Seed 0 selects a fresh seed. World-owned changes retain their chosen seed for that world.

## Modules

| Module | Effect |
| --- | --- |
| Chest Loot | Shuffles generated chest contents |
| Enemy Drops | Changes enemy drop mappings |
| Recipes | Changes recipe outputs |
| Shops | Changes NPC shop inventory |
| Fishing | Changes catches |
| Tile Drops | Changes mined tile drops |
| Spawns | Changes eligible enemy spawns |
| Item Stats | Scrambles supported item statistics |
| Starting Items | Changes the initial inventory |
| Gravity | Changes player gravity over time |
| Weather | Changes weather over time |

Modules can be toggled independently. Turning Recipe Shuffle off restores the unmodified recipe outputs; turning it on again derives a fresh randomized mapping from the configured seed without treating already-randomized outputs as the baseline.

## Configuration

Use the Randomizer panel or the F6 Mod Menu. The panel key is handled through TerrariaModder's keybind system and can be rebound.

## Multiplayer

Randomizer is a singleplayer mod. Its gameplay modules stay inactive in multiplayer to avoid client/server state divergence.

## Source

See <code>src/Randomizer/</code> in the TerrariaModder repository.