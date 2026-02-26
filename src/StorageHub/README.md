# Storage Hub

A unified storage management system for Terraria 1.4.5 that connects all your chests into one searchable interface with crafting, shimmering, and progression features.

## Fork Context

This Storage Hub version is part of a custom fork and includes behavior changes to more closely match Magic Storage-style workflows (heart/unit network usage, access points, and quick stack behavior).  
This is an unofficial adaptation.

## Attribution

Design inspiration comes from [Magic Storage](https://github.com/blushiemagic/MagicStorage), created by `blushiemagic`.

## Features

### Unified Storage Access
- **One UI for all chests** - Press F5 to access items from all registered chests
- **Smart chest registration** - Chests are registered when you open them (prevents remote looting of unexplored areas)
- **Search & filter** - Find items instantly with text search and category filters
- **Sort options** - Sort by name, stack size, rarity, type, or most recent

### One-Click Crafting
- **See what you can craft** - Shows all craftable recipes based on your connected storage
- **Batch crafting** - Craft 1, 10, 100, or max at once
- **Material tracking** - See exactly what you have vs what you need
- **Station memory** - At Tier 3+, remembered crafting stations work from anywhere

### Shimmer Decrafting
- **Bulk shimmer** - Shimmer items directly from storage (1, 10, 100, or all)
- **Block protection** - Right-click to protect specific prefixed items from shimmering
- **Unlock required** - Consume 10 Aether Block to enable shimmer features

### Progression System
| Tier | Unlock Cost | Range | Features |
|------|-------------|-------|----------|
| 0 | (Start) | 50 tiles | Basic storage access |
| 1 | 5 Shadow Scale/Tissue Sample | 100 tiles | Extended range |
| 2 | 10 Hellstone Bar | 500 tiles | Larger range |
| 3 | 10 Hallowed Bar | 1000 tiles | Station memory |
| 4 | 10 Luminite Bar | Entire world | Global access |

### Relays
Extend your range to distant areas without upgrading tier:
- Place relay points to create coverage networks
- Each relay adds additional range around its position
- Perfect for connecting multiple bases

### Dedicated Storage Blocks
Storage Hub now supports dedicated custom blocks:
- **Storage Core** opens the Storage Hub UI on right-click
- **Storage Unit** is a true custom container tile for network storage
- Optional mode `dedicatedBlocksOnly=true` restricts network registration to Storage Units only

### TODO / Known Limitation
- **Disk identity storage** currently uses the disk item's prefix field as a UID (`1..255`), so each disk item type is limited to 255 unique disk identities per world.
- **TODO:** Replace prefix-backed UID with dedicated persistent disk metadata/UUID storage.

## Controls

| Key | Action |
|-----|--------|
| F5 | Toggle Storage Hub UI |
| Left-click item | Take full stack to cursor |
| Right-click item | Take 1 to cursor (hold for rapid pickup) |
| Shift+click item | Move to inventory |
| Middle-click item | Toggle favorite |

## Tabs

### Items Tab
Browse all items in your connected storage network. Use the search bar to filter by name, or click the Sort/Filter buttons to organize by category.

Search also supports tag filters (MagicStorage-style). Examples:
- `#weapon` (only weapons)
- `#potion` (only potion-like consumables)
- `#material -#favorite` (materials excluding favorited items)
- `shark #ammo` (name + tag combined)

### Crafting Tab
Shows recipes you can craft with available materials. Select a recipe to see ingredients and craft amounts. Toggle "All" to see partially-craftable recipes.

The crafting search bar also supports the same `#tag` syntax for recipe outputs.

### Recipes Tab
Look up any recipe by output item. Useful for planning what materials to gather.

The recipes search bar supports the same `#tag` syntax for item filtering.

### Shimmer Tab
Decraft items using shimmer. Requires the Shimmer unlock (10 Aether Block). Right-click items to protect specific prefixes from being shimmered.

Shimmer types handled:
- **Direct transforms** - Item converts to another item (e.g. torch -> aether torch)
- **Decrafting** - Crafted items break down into recipe ingredients
- **Alchemy recipes** - Potions/alchemy table recipes lose ~33% of ingredients (each unit has 1/3 chance to vanish, matching vanilla behavior)
- **Critter items** - Shows the shimmer-transformed creature (not the base creature)
- **Coin Luck** - Informational display only (must toss manually)

The tab also handles vanilla edge cases: custom shimmer results (5 recipes return different items than their normal ingredients), world-evil recipe variants, boss progression locks, and item aliasing.

### Unlocks Tab
- Upgrade tier (consume materials to increase chest range)
- Unlock special features (water/honey/lava crafting, snow biome, graveyard, shimmer, altars)
- View current tier and range

### Network Tab
- See registered chests and their status
- View remembered crafting stations
- Manage relay positions

## Special Unlocks

Consume specific items to unlock additional features:

| Unlock | Cost | Effect |
|--------|------|--------|
| Water Crafting | 20 Bottled Water | Craft water recipes anywhere |
| Honey Crafting | 20 Bottled Honey | Craft honey recipes anywhere |
| Lava Crafting | 20 Obsidian | Craft lava recipes anywhere |
| Snow Biome | 50 Ice Block | Craft snow biome recipes anywhere |
| Graveyard | 10 Tombstone (any type) | Craft graveyard recipes anywhere |
| Shimmer | 10 Aether Block | Enable shimmer crafting and decrafting |
| Demon Altar | 10 Shadow Scale | Craft at Demon Altar anywhere |
| Crimson Altar | 10 Tissue Sample | Craft at Crimson Altar anywhere |

## Design Philosophy

### Safety First
- **Snapshot-based** - UI works with copies of items, never modifies storage directly during browsing
- **Two-phase commit** - Crafting validates all materials before consuming, with rollback on failure
- **Manual registration** - Must physically open chests to register them (no remote looting world-gen chests)

### Multiplayer Ready
- Built with `IStorageProvider` abstraction from day one
- Singleplayer uses direct access; multiplayer will use network packets
- UI code doesn't know or care which provider is active

### Performance
- Virtual scrolling for large item lists
- Lazy refresh (only when needed, not every frame)
- Pre-indexed recipe lookups for instant craftability checks

## Configuration

### Mod Settings (via F6 Menu)

| Setting | Default | Description |
|---------|---------|-------------|
| Enabled | true | Master toggle for the Storage Hub mod |
| Protect Hotbar | false | Prevent hotbar items from being consumed by crafting |
| Recursive Crafting | true | Auto-craft missing intermediate materials |
| Recursive Depth | 0 | Max sub-crafting depth (0 = unlimited) |
| Dedicated Blocks Only | true | Restrict network registration to Storage Unit tiles |

### Per-World Data

Progress is saved per-world and per-character in:
```
Terraria/TerrariaModder/mods/storage-hub/config/[world]_[character].json
```

Includes:
- Current tier
- Registered chest positions
- Remembered crafting stations
- Special unlocks
- Favorite items
- Relay positions

## Keybind Customization

The toggle key (default F5) can be rebound via the Mod Menu (F6). All keybinds are stored centrally in:
```
Terraria/TerrariaModder/core/keybinds.json
```

## Troubleshooting

### Items not showing up
- Make sure you've opened the chest at least once to register it
- Check if the chest is within your tier's range (see Unlocks tab)
- Inventory items are excluded from the Items tab (it shows chest storage only)

### Crafting says "Failed"
- Check the log file for details: `TerrariaModder/core/logs/terrariamodder.log`
- Ensure you have inventory space for the crafted item
- Verify materials weren't consumed by another action between checking and crafting

### UI won't open
- Verify the mod is enabled in config
- Check that you're in a world (UI only works after world load)
- Look for errors in the log file

## Reporting Bugs

If you encounter an issue, please include these files when reporting:

1. **Session log** - `Terraria/TerrariaModder/mods/storage-hub/debug/session.log`
   - Contains timestamped events from your play session
   - Automatically captures errors with context
   - Overwrites each game launch (no buildup)

2. **Main log** - `Terraria/TerrariaModder/core/logs/terrariamodder.log`
   - Full framework log with all mod activity

The session log is especially useful for item-related bugs (shimmer failures, crafting issues, missing items) as it tracks operations with before/after state.

## Version History

### 1.0.0
- Initial release
- 6 tabs: Items, Crafting, Recipes, Shimmer, Unlocks, Network
- 5-tier progression system (Tier 0-4)
- Station memory at Tier 3+
- Shimmer decrafting with protection
- Relay system for range extension
- Dedicated custom Storage Core + Storage Unit blocks
- Recursive crafting with configurable depth

## Multiplayer

Singleplayer only.

## Installation

Requires TerrariaModder Core.

Extract this zip into your Terraria folder. The mod goes into
`TerrariaModder/mods/storage-hub/`.

## Dedicated Docs Site

Storage Hub now has a standalone MkDocs config under:

- `src/StorageHub/mkdocs.yml`

Serve locally from repo root:

```powershell
mkdocs serve -f src/StorageHub/mkdocs.yml
```
