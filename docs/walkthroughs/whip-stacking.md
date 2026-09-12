---
title: WhipStacking Mod - Five Whip Tag Effects in Terraria 1.4.5.8
description: Walkthrough of WhipStacking, which enables Terraria's native capacity of five simultaneous whip tag effects.
---

# WhipStacking Walkthrough

WhipStacking enables Terraria 1.4.5.8's native maximum of five simultaneous whip tag effects. Terraria still owns tag duration, hit processing, replacement order, special effects, and cleanup.

## How it works

Terraria 1.4.5 introduced a native tag-effect stack. The game normally starts each player with capacity for one effect, and accessories can raise that capacity. The stack itself supports up to five effects.

After Terraria applies equipment effects, WhipStacking sets the local player's capacity to the native maximum. At capacity, applying another distinct tag replaces the oldest active effect using Terraria's normal behavior.

## Implementation

The mod installs a Harmony postfix on <code>Player.UpdateEquips(int)</code> after game content is ready. The postfix changes only <code>Player.maxTagEffects</code> and uses <code>TagEffectStack.MaxEffects</code> as the limit.

This keeps the patch small and avoids reproducing Terraria's whip-hit pipeline. Capacity accessories retain their other bonuses.

## Configuration

The mod is enabled by default. Open the Mod Menu with F6 to turn it on or off; the setting takes effect immediately.

## Multiplayer

WhipStacking is an optional client mod. Each player who wants the five-effect capacity installs it on their client.

## Source

See <code>src/WhipStacking/</code> in the TerrariaModder repository.