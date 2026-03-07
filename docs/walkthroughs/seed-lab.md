---
title: SeedLab Mod - Mix Secret Seed Features in Terraria 1.4.5
description: Walkthrough of the SeedLab mod for Terraria 1.4.5. Toggle and mix secret seed features at runtime using world-gen patching and flag overrides.
parent: Walkthroughs
nav_order: 9
---

# SeedLab Walkthrough

**Difficulty:** Advanced
**Concepts:** World generation patching, runtime flag overrides, dual-panel UI, preset system, hierarchical feature model

SeedLab lets players mix and match individual features from Terraria's secret and special seeds, both at runtime and during world generation.

## What It Does

- **In-Game Panel** (F10 in-world): Toggle seed features while playing. Enemy stats, spawn rates, and boss AI change immediately.
- **World-Gen Panel** (F10 at menu): Configure which seed features to force-enable before creating a new world.
- **15 Seeds**: 9 special seeds (For the Worthy, Drunk World, Don't Starve, Not the Bees, Remix, Zenith, Celebration, No Traps, Skyblock) + 6 secret seeds (Vampire, Infected, Team Spawns, Dual Dungeons, Halloween Forever, Christmas Forever)
- **120+ Individual Features**: Each seed is decomposed into granular toggleable effects
- **Presets**: Save and load custom seed configurations

## Architecture

SeedLab uses a **hierarchical feature model** with three tiers:

```
Seed (e.g., "For the Worthy")
├── Group (e.g., "Enemy Scaling")
│   ├── Feature (e.g., "Enemy Stat Multipliers")
│   └── Feature (e.g., "Boss Life Scaling")
└── Group (e.g., "Gameplay Changes")
    ├── Feature (e.g., "Bunnies Are Hostile")
    └── Feature (e.g., "Demon Eyes Shoot Lasers")
```

This hierarchy enables three granularity levels: toggle an entire seed, a group of related features, or individual effects.

## Key Concepts

### 1. Dual-Mode Operation

SeedLab has two completely independent systems:

**Runtime mode** modifies the current world's gameplay by patching methods like `NPC.SetDefaults`, `NPC.AI`, and `Spawner.GetSpawnRate`. Changes take effect immediately.

**World-gen mode** modifies Terraria's seed flags during world creation by patching `WorldGen.Reset()` and `GenPass.Apply()`. Features must be configured before creating the world.

Each mode has its own state file:
- `state.json`: Runtime feature toggles (per-world)
- `state-worldgen.json`: World-gen feature overrides

### 2. Global vs. Mixed Flag Overrides

The runtime patching system uses an optimization to minimize overhead:

```
If ALL features of a seed are enabled  → Set Main.getGoodWorld = true globally
If ALL features of a seed are disabled → Set Main.getGoodWorld = false globally
If MIXED (some on, some off)           → Keep global flag + use per-method prefixes
```

When a seed is fully enabled or disabled, the global flag handles everything and per-method patches skip their checks entirely. Only when features are mixed does the mod need per-method prefix/postfix pairs:

```csharp
// Prefix: save original flag, apply override
static void NPC_SetDefaults_Prefix(ref bool __state)
{
    __state = Main.getGoodWorld;  // save original
    if (FeatureManager.IsFeatureEnabled("ftw_enemy_scaling"))
        Main.getGoodWorld = true;
}

// Postfix: restore original flag
static void NPC_SetDefaults_Postfix(bool __state)
{
    Main.getGoodWorld = __state;  // restore
}
```

This save/restore pattern ensures one feature's override doesn't leak into unrelated code.

### 3. Harmony Patch Targets

Runtime features patch approximately 7 key methods:

| Target | Purpose |
|--------|---------|
| `NPC.SetDefaults` | Enemy stat scaling (FTW, Zenith, Celebration) |
| `NPC.AI` | Boss behavior changes (FTW, Not the Bees, No Traps) |
| `NPC.ScaleStats_ByDifficulty_Tweaks` | Boss life/damage multipliers |
| `Spawner.GetSpawnRate` | Spawn rate adjustments |
| `Spawner.SpawnNPC` | Spawn pool and depth changes |
| `Player.UpdateBuffs` | Hunger debuff (Don't Starve) |
| Global flag toggles | Binary seed flags (Vampire, Infected, etc.) |

### 4. Two UI Panels

**In-Game Panel** (`InGamePanel`) uses the Core UI framework:
- Collapsible seed sections with group toggles
- Normal mode (grouped) vs Advanced mode (individual features)
- Scrollable content with mouse wheel support
- Preset save/load buttons

**World-Gen Panel** (`WorldGenPanel`) uses DraggablePanel from the Widget Library:
- Three tabs: By Seed, By Category, Presets
- Two-state checkboxes (unchecked = vanilla, checked = force on)
- Built-in presets for each full seed configuration
- Active count display in panel header

F10 opens whichever panel is appropriate: in-game panel when in a world, world-gen panel when at the menu.

### 5. Command Registration

SeedLab registers debug commands for remote/scripted control:

```csharp
context.RegisterCommand("status", "Show current seed feature states", CmdStatus);
context.RegisterCommand("toggle", "Toggle a seed/group: seed-lab.toggle ftw", CmdToggle);
context.RegisterCommand("preset", "Manage presets: seed-lab.preset list", CmdPreset);
context.RegisterCommand("reset", "Reset all features to match world seed flags", CmdReset);
```

Commands are auto-namespaced with the mod ID, so `toggle` becomes `seed-lab.toggle`.

### 6. State Persistence

Feature states are saved per-world using the mod's config folder:

```csharp
public void SaveState()
{
    var data = new Dictionary<string, bool>();
    foreach (var feature in _features)
        data[feature.Id] = feature.Enabled;

    File.WriteAllText(_statePath, JsonConvert.SerializeObject(data));
}
```

On world load, the mod reads the world's original seed flags and then applies any saved overrides:

```csharp
public void OnWorldLoad()
{
    InitFromWorldFlags();  // Read Main.getGoodWorld, Main.drunkWorld, etc.
    LoadState();           // Apply saved overrides on top
    RecalculateGlobalFlags();
}
```

## Lessons Learned

1. **Save/restore flag pattern**: When overriding global state for a single method call, always save the original in a prefix and restore in a postfix
2. **Global optimization**: Skip per-method patching when the entire seed is uniformly enabled or disabled
3. **Dual-panel UI**: Same keybind can open different panels depending on game state (in-world vs menu)
4. **Hierarchical features**: Grouping features into seeds and groups gives users three levels of control granularity
5. **State independence**: Runtime and world-gen states are completely separate, preventing confusing interactions

For more on Harmony patching patterns, see [Harmony Basics](../harmony-basics.md).
