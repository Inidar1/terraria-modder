---
title: Skip Intro - Terraria 1.4.5.8 Mod Walkthrough
description: Wait for Terraria to finish loading, then skip the ReLogic splash through a small lifecycle-owned Harmony patch.
---

# Skip Intro Walkthrough

Skip Intro is a small example of a mod that waits for the correct game lifecycle point before applying a Harmony patch.

## Behavior

Terraria uses the ReLogic splash as part of startup. The mod does not guess how long loading will take. It watches Terraria's asynchronous-load completion field from a postfix on <code>Main.DoUpdate</code>, then advances the splash counter once loading is complete.

If the splash has already ended, the mod marks its work complete. If the private splash counter cannot be found, it falls back to Terraria's public splash flag.

## Lifecycle

The mod implements <code>IModLifecycle</code>. <code>Initialize</code> reads configuration and creates the Harmony owner. <code>OnContentReady</code> resolves the current Terraria members and installs the patch. <code>Unload</code> removes the patch and clears static state for a later reload.

This keeps the patch out of constructors and avoids running before Terraria content is ready.

## Configuration

Enabled is on by default and is marked Restart Required because the splash occurs before an in-game setting can be useful.

## Multiplayer

Skip Intro is client-only and does not change world or server state.

## Source

See <code>src/SkipIntro/</code> in the TerrariaModder repository.