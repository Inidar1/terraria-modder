---
title: FPS Unlocked - Terraria 1.4.5.8 Mod Walkthrough
description: Unlock Terraria rendering beyond 60 FPS while preserving its fixed-rate simulation.
---

# FPS Unlocked Walkthrough

FPS Unlocked separates rendering from Terraria's 60 Hz update loop. It draws intermediate frames at the selected rate and interpolates visible game state between completed updates.

## Modes

| Mode | Behavior |
| --- | --- |
| VSync (Vanilla) | Restores Terraria's native frame pacing and VSync |
| Capped | Uses the configured frame-rate limit |
| Uncapped | Draws as quickly as the system permits |

Interpolation can be disabled independently while keeping rendering unlocked. Mouse polling on render frames is also configurable and applies only while the game is focused.

## What is interpolated

The mod interpolates players, NPCs, projectiles, camera motion, held items, rotations, graphical offsets, and relevant entity-linked effects. Teleports and large camera transitions reset interpolation so old positions do not smear across the screen.

Lighting and game simulation continue advancing on Terraria updates. Partial draw frames reuse the current simulation state.

## Render safety

Terraria can replace render targets or enter and leave SpriteBatch drawing across menus, display changes, world transitions, and shutdown. FPS Unlocked rechecks those transitions and always restores temporarily interpolated state, including when drawing throws.

## Configuration

Open F6 and select FPS Unlocked. Mode, cap, interpolation, mouse polling, and VSync changes apply without restarting the game.

Disabled and VSync (Vanilla) modes remove the mod's rendering and timing patches after restoring native graphics state. Terraria's own Frame Skip setting still controls vanilla pacing; with Frame Skip Off, VSync can draw at the display refresh rate rather than 60 FPS.

## Multiplayer

FPS Unlocked is client-only and changes local rendering only.

## Source

See <code>src/FpsUnlocked/</code> in the TerrariaModder repository.
