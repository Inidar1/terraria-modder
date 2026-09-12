---
title: Quick Keys - Terraria 1.4.5.8 Mod Walkthrough
description: Rebindable shortcuts for torch placement, recall, quick stack, ruler display, and one-shot extended-hotbar use.
---

# Quick Keys Walkthrough

Quick Keys combines several quality-of-life actions under Core's keybind system.

## Actions

- **Auto Torch** finds a torch in inventory, checks Terraria placement rules near the cursor, places it, and sends tile and inventory synchronization in multiplayer.
- **Auto Recall** selects the best available Recall Potion, mirror, Cell Phone, or active Shellphone destination and uses Terraria's normal recall path.
- **Quick Stack** sends eligible inventory items to nearby chests.
- **Ruler** maintains Terraria's ruler-line flag while enabled.
- **Extended Hotbar** temporarily selects slots 11-20, activates the item once, releases held-use state, and returns to the original hotbar slot.

## Input ownership

Each action is registered with <code>ModContext.RegisterKeybind</code>. Core handles rebinding and persistence. Extended-hotbar use records the original slot and always restores it after the one-shot action, including cancellation and world transitions.

## Configuration

The F6 Mod Menu controls the master toggle, chat messages, extended hotbar, and diagnostic logging. Extended hotbar is off by default.

## Multiplayer

Quick Keys is client-only. Auto Torch emits Terraria's normal tile and inventory packets; the other shortcuts use the local player's standard game actions.

## Source

See <code>src/QuickKeys/</code> in the TerrariaModder repository.