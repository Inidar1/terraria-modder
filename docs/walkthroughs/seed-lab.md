---
title: Seed Lab - Terraria 1.4.5.8 Mod Walkthrough
description: Compose secret-seed world-generation and runtime features with scoped patches, presets, and safe state files.
---

# Seed Lab Walkthrough

Seed Lab lets players combine individual features from Terraria's special seeds. Its title-screen panel configures new world generation; its in-world panel controls supported singleplayer runtime effects.

## Feature catalog

Features are grouped by seed and category so broad choices can expand into the exact underlying flags and patches. Presets store selected combinations. Runtime and world-generation selections use separate files so preparing a world does not silently change the current play session.

## World generation

Seed Lab applies requested flags before Terraria's generation passes and resets them at the correct boundaries. Zenith is a combined seed, so its component effects remain active across every relevant pass instead of being restored after only the first pass.

World-generation state and presets use safe replacement writes. A failed or interrupted write leaves the previous valid file available.

## Runtime patches

Supported singleplayer features cover enemy scaling and behavior, spawns, lighting, death sounds, holidays, hunger/darkness, recall, respawn, vampire cleanup, and seed-specific rules. Disabling a feature or leaving a world clears its temporary state.

## Multiplayer

Runtime overrides remain inactive in multiplayer. Seed Lab is intended for singleplayer gameplay and local world creation.

## Source

See <code>src/SeedLab/</code> in the TerrariaModder repository.