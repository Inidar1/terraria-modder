# Pet Chests

Right-click a summoned cosmetic pet to use it as a portable piggy bank.

## Features

- Works with cosmetic pet projectiles; light pets, Chester, and the Flying Piggy Bank keep their normal behavior
- Tracks the exact pet projectile so the correct moving pet keeps the bank open
- Preserves Terraria's normal weapon, tool, inventory, and partial-stack input while the bank is open
- Closes when the pet disappears, moves beyond the configured range, the inventory closes, or the mod is disabled
- Supports switching between Terraria containers without leaving stale pet state

## Configuration

| Setting | Default | Description |
| --- | --- | --- |
| Enabled | On | Enables pet interaction |
| Interaction Range | 200 pixels | Maximum player-to-pet distance |
| Shown Hint | Off until shown | Controls the first-use hint |

Open the F6 Mod Menu to change these settings.

## Multiplayer

Pet Chests is an optional client mod and uses the local player's native piggy-bank container.

## Installation

Requires TerrariaModder Core. Replace the existing <code>TerrariaModder/mods/pet-chests/</code> folder with the downloaded mod folder, then launch through <code>TerrariaInjector.exe</code>.