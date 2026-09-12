---
title: Pet Chests - Terraria 1.4.5.8 Mod Walkthrough
description: Bind Terraria's piggy-bank UI to a cosmetic pet while preserving normal inventory and item input.
---

# Pet Chests Walkthrough

Pet Chests makes cosmetic vanity pets interactable as portable piggy banks. Light pets, Chester, and the Flying Piggy Bank keep their normal behavior.

## Identifying the pet

The mod accepts active projectiles marked by Terraria's <code>Main.projPet</code> table, rejects entries in <code>ProjectileID.Sets.LightPet</code>, and ignores the two native portable-bank projectiles. The click must land inside the pet hitbox and remain within the configured range.

When the bank opens, the mod stores the projectile slot, identity, and type. Checking all three prevents a reused projectile slot from binding the bank to a different projectile.

## Preserving vanilla input

The open container uses Terraria's native piggy-bank index and projectile tracker. The mod consumes only the click that closes the bound pet bank. Weapon, tool, inventory, and right-click partial-stack behavior remain with Terraria.

The binding is released when the pet disappears, changes identity, leaves range, the inventory closes, another container opens, the mod is disabled, or the world unloads.

## Multiplayer

Pet Chests is an optional client mod and uses the local player's native personal bank.

## Source

See <code>src/PetChests/</code> in the TerrariaModder repository.