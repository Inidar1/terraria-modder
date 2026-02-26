# Progression and Disks

## Network Tier Progression

Storage Hub uses tier-based range progression.

| Tier | Unlock Cost | Range | Notes |
|---|---|---|---|
| 0 | Start | 50 tiles | Base range |
| 1 | 5 Shadow Scale or Tissue Sample | 100 tiles | Early expansion |
| 2 | 10 Hellstone Bar | 500 tiles | Mid-game expansion |
| 3 | 10 Hallowed Bar | 1000 tiles | Station memory tier |
| 4 | 10 Luminite Bar | Global | `int.MaxValue` range |

Station memory availability:

- Tier `< 3`: not available.
- Tier `>= 3`: available.

## Special Unlocks

These unlock non-tile crafting conditions.

| Unlock | Cost | Effect |
|---|---|---|
| Water Crafting | 20 Bottled Water | Enables water-required recipes |
| Honey Crafting | 20 Bottled Honey | Enables honey-required recipes |
| Lava Crafting | 20 Obsidian | Enables lava-required recipes |
| Shimmer | 10 Aether Block | Enables shimmer recipes and decrafting tab |
| Snow Biome | 50 Ice Block | Enables snow biome requirements |
| Graveyard / Ecto Mist | 10 Tombstones (any accepted type) | Enables graveyard requirements |
| Demon Altar | 10 Shadow Scale | Enables Demon Altar requirement |
| Crimson Altar | 10 Tissue Sample | Enables Crimson Altar requirement |

## Relay Limits

- Relay radius: `200` tiles from relay position.
- Maximum relays: `10`.

## Disk Upgrade Costs

Upgrades are done through the Disk Upgrader UI.

| From | To | Required Materials |
|---|---|---|
| Basic Disk | Improved Disk | 1 Ruby, 2 Gold Bar |
| Improved Disk | Advanced Disk | 1 Diamond, 4 Gold Bar |
| Advanced Disk | Quantum Disk | 2 Diamond, 8 Gold Bar |

## Technical Disk Identity Note

Current implementation stores disk identity on the disk item's Terraria `prefix` byte:

- UID range: `1..255`
- Scope: per disk item type, per world

This is a known limitation and is tracked for migration to dedicated metadata/UUID storage.

