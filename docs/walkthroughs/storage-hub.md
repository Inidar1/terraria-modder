---
title: StorageHub Mod - Unified Storage and Crafting for Terraria 1.4.5
description: Walkthrough of the StorageHub mod for Terraria 1.4.5. Multi-tab storage UI with crafting, shimmer decraft, painting chest, and relay network.
parent: Walkthroughs
nav_order: 5.5
---

# StorageHub Walkthrough

**Difficulty:** Advanced
**Concepts:** Multi-tab UI, crafting system, item snapshots, data persistence, relay network, reflection

StorageHub is a unified storage interface that lets you browse, search, craft, and decraft items across all your registered chests from a single panel.

## What It Does

Press F5 to open a 6-tab panel:
- **Items** - Browse all registered chest contents with search, sort, and category filters
- **Crafting** - One-click crafting with recursive intermediate crafting support
- **Recipes** - Full recipe browser showing what creates and uses each item
- **Shimmer** - Decraft items back into ingredients using shimmer
- **Unlocks** - Tier progression, station memory, relay management, and painting chest upgrades
- **Network** - View registered chests and crafting station availability

## Key Concepts

### 1. Chest Registration (No Harmony Patches)

StorageHub detects chest opens by polling `player.chest` each frame rather than patching:

```csharp
private int _lastChest = -1;

void OnPostUpdate()
{
    var player = Main.player[Main.myPlayer];
    int currentChest = player.chest;

    if (currentChest >= 0 && _lastChest < 0)
    {
        // Player just opened a chest - register it
        OnChestOpened(currentChest);
    }

    _lastChest = currentChest;
}
```

Chests are stored per-character per-world in the config. Locked chests, trapped chests, and mimics are excluded.

### 2. Snapshot-Based Item Access

All item access uses immutable snapshots rather than direct references. This prevents accidental mutation of game state:

```csharp
public class ItemSnapshot
{
    public int Type;
    public int Stack;
    public string Name;
    public int Rarity;
    // ... other fields

    // Source tracking for operations
    public int SourceChestIndex;
    public int SourceSlotIndex;
}

// Get all items from registered chests
List<ItemSnapshot> items = _storageProvider.GetAllItems();

// Take an item (moves from chest to cursor)
_storageProvider.TakeItemToCursor(snapshot);
```

### 3. Tiered Progression System

Storage access range expands through 4 tiers, each unlocked by consuming items:

```csharp
// Tier requirements (item types consumed to upgrade)
Tier 0 → 1: Shadow Scale (86) or Tissue Sample (1329)
Tier 1 → 2: Hellstone Bar (175)
Tier 2 → 3: Hallowed Bar (1225)
Tier 3 → 4: Luminite Bar (3467)

// Range per tier (in tiles)
Tier 0: 50 tiles (800 pixels)
Tier 1: 100 tiles (1600 pixels)
Tier 2: 500 tiles (8000 pixels)
Tier 3: 1000 tiles (16000 pixels) + station memory unlock
Tier 4: Unlimited range
```

Tier 3+ unlocks **station memory**: the mod remembers crafting stations you've visited, so you don't need to be near them every time.

### 4. Relay Network (BFS Range Extension)

Players can place up to 10 relays to extend their storage range via a breadth-first search. Each relay extends range by 200 tiles from its position:

```csharp
// Range calculation: BFS from player through relay network
HashSet<(int, int)> reachable = new HashSet<(int, int)>();
Queue<(int x, int y, int range)> queue = new Queue<>();

// Start from player position
queue.Enqueue((playerTileX, playerTileY, baseRange));

while (queue.Count > 0)
{
    var (x, y, remaining) = queue.Dequeue();

    // Check for relays within remaining range
    foreach (var relay in relays)
    {
        int dist = ManhattanDistance(x, y, relay.X, relay.Y);
        if (dist <= remaining)
        {
            // Relay extends range from its position
            queue.Enqueue((relay.X, relay.Y, relayRange));
        }
    }
}
```

### 5. Recipe Indexing for O(1) Lookup

The crafting system pre-indexes all recipes on first use for fast lookup:

```csharp
// Pre-built indexes
Dictionary<int, List<Recipe>> _recipesByResult;    // item type → recipes that make it
Dictionary<int, List<Recipe>> _recipesByIngredient; // item type → recipes that use it
Dictionary<int, List<Recipe>> _recipesByStation;    // tile type → recipes at that station

// Craftability check
bool CanCraft(Recipe recipe)
{
    // Check all ingredients available in storage + inventory
    foreach (var ingredient in recipe.requiredItem)
    {
        int available = CountInStorage(ingredient.type) + CountInInventory(ingredient.type);
        if (available < ingredient.stack) return false;
    }

    // Check crafting station (or station memory)
    return HasStation(recipe) || _stationMemory.Contains(recipe.requiredTile);
}
```

### 6. Recursive Crafting

When partial materials are available, the system can auto-craft intermediates:

```csharp
// Example: Crafting a Nights Edge when you have ore instead of bars
// 1. Detect missing: need 10 Hellstone Bars, have 0
// 2. Find recipe: Hellstone Bar = 3 Hellstone + 1 Obsidian (at Hellforge)
// 3. Check: have 30 Hellstone + 10 Obsidian? Yes
// 4. Auto-craft: 10 Hellstone Bars first, then craft Nights Edge
```

The craftability UI shows three states:
- **Green**: All materials available, can craft now
- **Yellow**: Partial materials, recursive crafting can fill the gap
- **Gray**: Not enough materials even with recursive crafting

### 7. Shimmer Decrafting

Reverses crafting recipes, converting items back to ingredients:

```csharp
// Shimmer decraft: reverse a recipe
void DecraftItem(ItemSnapshot item, Recipe recipe)
{
    // Remove the item from storage
    ConsumeItem(item, recipe.createItem.stack);

    // Spawn each ingredient
    foreach (var ingredient in recipe.requiredItem)
    {
        SpawnItem(ingredient.type, ingredient.stack);
    }
}
```

The shimmer system respects:
- **Boss progression locks**: Some items only decraft after defeating specific bosses
- **Biome variants**: Corruption vs Crimson recipes differ
- **Special unlock**: Requires 10 Aether Blocks to activate

### 8. Station Detection

Crafting stations are detected by scanning nearby tiles:

```csharp
// 34 tile-based stations + 5 environment conditions
int[] StationTileIDs = {
    TileID.WorkBenches, TileID.Anvils, TileID.Furnaces,
    TileID.Loom, TileID.Kegs, /* ... */
};

bool[] EnvironmentConditions = {
    IsNearWater(),    // player.adjWater
    IsNearHoney(),    // player.adjHoney
    IsNearLava(),     // player.adjLava
    IsInSnowBiome(),  // player.ZoneSnow
    IsInGraveyard(),  // player.ZoneGraveyard
};
```

At Tier 3+, visited stations are remembered in **station memory**, persisted per-world so you don't lose progress.

### 9. Multi-Tab UI Architecture

The UI uses a coordinator pattern with independent tabs:

```csharp
class StorageHubUI
{
    private DraggablePanel _panel;
    private ITab[] _tabs;
    private int _activeTab;

    void DrawPanel()
    {
        if (!_panel.BeginDraw()) return;

        // Tab bar at top
        _activeTab = TabBar.Draw(x, y, width, tabNames, _activeTab);

        // Active tab draws its content
        _tabs[_activeTab].Draw(contentX, contentY, contentWidth, contentHeight);

        _panel.EndDraw();
    }
}

interface ITab
{
    void Draw(int x, int y, int width, int height);
    void MarkDirty();  // Signal that data needs refresh
}
```

Each tab maintains its own scroll state, filter state, and refresh timing. Tabs are marked dirty when storage changes occur (chest opened, item crafted, etc.).

### 10. Per-World Data Persistence

StorageHub saves progression data per-character per-world:

```csharp
// File: mods/storage-hub/worlds/{world-name}/{character-name}.json
{
    "tier": 2,
    "registeredChests": [[100, 200], [150, 200]],
    "relays": [[120, 195]],
    "stationMemory": [18, 16, 77],
    "stationMemoryEnabled": true,
    "favorites": [3506, 757, 1326],
    "sortMode": "name",
    "categoryFilter": "all"
}

// Character-level unlocks survive across worlds
// File: mods/storage-hub/characters/{character-name}.json
{
    "shimmerUnlocked": true
}
```

For disk-drive persistence internals (including the current prefix-byte UID model), see [StorageHub Disk Storage Format](storage-hub-disk-storage.md).

## UI Components

StorageHub uses its own custom UI components (it predates the Widget Library), plus `TextUtil` from the Widget Library:

| Component | Library | Purpose |
|-----------|---------|---------|
| Panel (custom) | Custom | Manual drag, z-order via `RegisterPanelBounds`/`RegisterPanelDraw` |
| `TabBar` | Custom | Tab navigation with active indicators |
| `SearchBar` | Custom | Text input with real-time filtering |
| `ScrollPanel` | Custom | Virtual scrolling for item lists |
| `ItemSlotGrid` | Custom | Grid layout with 44px item slots |
| `TextUtil` | Widget Library | Text measurement and truncation |

StorageHub implements its own draggable panel with manual drag tracking rather than using the Widget Library's `DraggablePanel`. New mods should use the Widget Library instead; see [Core API Reference](../core-api-reference#widget-library).

## Configuration

StorageHub has several config options beyond the standard `enabled`:

| Config Key | Type | Default | Description |
|------------|------|---------|-------------|
| `blockHotbarFromCrafting` | bool | false | Protect hotbar slots 1-10 from crafting consumption |
| `recursiveCrafting` | bool | true | Enable auto-crafting of intermediate ingredients |
| `recursiveCraftingDepth` | int | 0 | Maximum recursive depth (0 = unlimited) |
| `paintingChest` | bool | true | Enable the Mysterious Painting feature |

## Lessons Learned

1. **Polling beats patching**: Checking `player.chest` each frame is simpler and more robust than Harmony-patching chest open methods
2. **Snapshots prevent bugs**: Never hand out references to real game items; return copies
3. **Pre-index for performance**: Building recipe lookups once is far better than searching every frame
4. **Per-world persistence**: Storage registrations and progression must be scoped to character+world pairs
5. **Lazy refresh**: Only recalculate when data changes (dirty flag), not every frame
6. **Tiered progression**: Gates encourage exploration while still being useful early
7. **Station memory at high tiers**: Rewards invested players without making early game trivial
8. **Relay BFS**: Elegant way to extend range without unlimited access

For more on building mod UIs, see [Core API Reference - Widget Library](../core-api-reference#widget-library).
For crafting station tile IDs, always verify against the decomp, never guess.
