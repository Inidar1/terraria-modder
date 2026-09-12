# Seed Lab

Mix individual features from Terraria's secret seeds for custom world generation and singleplayer gameplay.

## Features

- Press F10 in world-selection and creation menus to configure the next generated world.
- Press F10 in a singleplayer world to configure supported runtime effects.
- Mix For the Worthy, Drunk World, Don't Starve, Not the Bees, Remix, Zenith, Celebration, No Traps, Skyblock, and additional secret-seed groups.
- Save and load presets.
- Keep world-generation settings separate from in-world runtime settings.

Zenith generation keeps its combined seed flags active throughout all relevant generation passes. World-generation settings and presets are saved atomically so an interrupted write does not replace a valid file with partial JSON.

## Runtime behavior

Supported toggles cover enemy scaling, spawn behavior, boss behavior, lighting, death sounds, holidays, hunger/darkness, recall/respawn behavior, and other seed-specific rules. Temporary vampire and seed state is cleared when its feature or world is left.

## Multiplayer

Seed Lab runtime overrides are singleplayer-only. In multiplayer, its runtime patches remain inactive.

## Installation

Requires TerrariaModder Core. Replace the existing <code>TerrariaModder/mods/seed-lab/</code> folder with the downloaded mod folder, then launch through <code>TerrariaInjector.exe</code>.