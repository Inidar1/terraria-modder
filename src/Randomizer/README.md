# Randomizer

A deterministic modular randomizer for reproducible Terraria singleplayer runs.

Press Numpad / on the title screen or in a world to open the Randomizer panel. The key can be changed in the F6 Mod Menu.

## Modules

- Chest loot
- Enemy drops
- Recipe outputs
- NPC shops
- Fishing catches
- Tile drops
- Eligible enemy spawns
- Item statistics
- Starting inventory
- Gravity
- Weather

Every module can be enabled independently. Gameplay modules remain inactive in multiplayer.

## Seed system

A nonzero seed produces the same results across game restarts and computers. Each module receives a stable derived sub-seed, so changing one module does not silently change another module's random sequence. Seed 0 chooses a fresh seed.

Recipe randomization preserves an unmodified output baseline. Disabling the recipe module restores that baseline; enabling it again randomizes from the baseline rather than from an already-randomized recipe table.

## Multiplayer

Randomizer is singleplayer-only to prevent client/server state divergence.

## Installation

Requires TerrariaModder Core. Replace the existing <code>TerrariaModder/mods/randomizer/</code> folder with the downloaded mod folder, then launch through <code>TerrariaInjector.exe</code>.