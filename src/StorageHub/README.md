# Storage Hub

A unified storage interface for Terraria 1.4.5.8 with chest registration, search, crafting, recipes, shimmer decrafting, progression, relays, and the Mysterious Chest.

## Storage and crafting

- Open a chest once to register it, then browse eligible registered chests from one panel.
- Search and sort stored items without scanning every chest on every frame.
- Craft one, ten, one hundred, or the maximum available amount.
- Recursive crafting can make missing intermediate ingredients.
- Crafting validates material plans, delivery space, and rollback before committing item changes.
- Recipe, station, liquid, biome, and altar requirements follow the unlocked network state.

## Progression

| Tier | Cost | Range | Added capability |
| --- | --- | --- | --- |
| 0 | Start | 50 tiles | Basic storage access |
| 1 | 5 Shadow Scale or Tissue Sample | 100 tiles | Extended range |
| 2 | 10 Hellstone Bars | 500 tiles | Larger range |
| 3 | 10 Hallowed Bars | 1000 tiles | Station memory |
| 4 | 10 Luminite Bars | Whole world | Global access |

Relays extend coverage to other bases. Special unlocks add remote water, honey, lava, snow, graveyard, shimmer, and altar recipe conditions.

## Mysterious Chest

The Merchant sells a special chest whose capacity can be upgraded from 40 to 80, 200, 1,000, and 5,000 slots. Capacity and contents persist across rename, save/reload, placement, pickup, and replacement. Large capacities use extended storage rather than Terraria's fixed chest array.

## Controls

| Control | Action |
| --- | --- |
| F5 | Open or close Storage Hub |
| Left-click | Take a stack |
| Right-click | Take items individually |
| Shift-click | Move to inventory |
| Middle-click | Toggle favorite protection |

## Configuration

The F6 Mod Menu controls hotbar protection, recursive crafting and depth, Mysterious Chest availability, and whether a multiplayer server trusts all players. Progression and registered storage are saved for the selected world and character.

## Multiplayer

Storage Hub supports singleplayer, Host & Play, and dedicated servers. All peers need the mod because it adds a custom item and storage protocol. The server validates storage requests and synchronizes chest mutations.

## Troubleshooting

Current sessions are logged under <code>TerrariaModder/core/logs/</code>. Include the latest client or server session log when reporting a storage or crafting problem.

## Installation

Requires TerrariaModder Core. Replace the existing <code>TerrariaModder/mods/storage-hub/</code> folder with the downloaded mod folder, then launch through <code>TerrariaInjector.exe</code>.