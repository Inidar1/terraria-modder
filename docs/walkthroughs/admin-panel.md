---
title: Admin Panel - Terraria 1.4.5.8 Mod Walkthrough
description: UI, respawn, teleport, NPC spawning, and server-authorized administration through current Terraria APIs.
---

# Admin Panel Walkthrough

Admin Panel combines local player controls with operations that require server authority.

## Controls

The Backslash key opens a draggable panel. F9 toggles god mode directly. The panel provides health/mana restoration, movement speed, time presets and speed, teleport destinations, separate normal/boss respawn presets, and searchable boss/NPC spawn catalogs.

## Respawn timing

Terraria's normal and boss deaths have different base timers. The mod stores indices into explicit valid preset lists, validates those indices when configuration is read, and adjusts the live timer without allowing negative or out-of-range values.

## Patch ownership

Content-ready Harmony patches handle player effect reset, death updates, time rate, and horizontal movement. UI actions are queued to the update path so drawing does not mutate game state. Teleport and NPC/time operations use Core's server command path when multiplayer authority is required.

## Multiplayer

Works in singleplayer, Host & Play, and dedicated servers. Multiplayer state-changing actions require administrator permission. The Host & Play host is an administrator automatically.

## Source

See <code>src/AdminPanel/</code> in the TerrariaModder repository.