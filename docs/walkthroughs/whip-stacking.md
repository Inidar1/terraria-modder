---
title: WhipStacking Mod - Restore Whip Tag Stacking in Terraria 1.4.5
description: Walkthrough of the WhipStacking mod for Terraria 1.4.5. Restores pre-1.4.5 whip tag stacking using Harmony prefixes and multi-entry state tracking.
parent: Walkthroughs
nav_order: 7
---

# WhipStacking Walkthrough

**Difficulty:** Intermediate
**Concepts:** Harmony prefixes, restoring removed game mechanics, multi-entry state tracking, private-field reflection

WhipStacking restores pre-1.4.5 whip tag stacking behavior, allowing multiple whip tags to be active on the same NPC simultaneously.

## What It Does

In Terraria 1.4.5, only one whip tag can be active on an NPC at a time. This mod restores the ability for multiple whip tags to stack, enabling hybrid whip builds where tags from different whips all apply their effects.

## Key Concepts

### 1. Replacing Vanilla State with Multi-Entry Tracking

The core technique is replacing Terraria's single-effect whip model with a per-player dictionary:

```csharp
// Vanilla: TagEffectState._effect = single UniqueTagEffect
// Mod: Dictionary<whipType, WhipTagEntry> per player

public class WhipTagEntry
{
    public int WhipType;                    // Item ID of this whip
    public UniqueTagEffect Effect;          // Direct reference — mods reference Terraria.exe
    public int[] TimeLeftOnNPC;             // Timer per NPC (max 200)
    public int[] ProcTimeLeftOnNPC;         // Proc window per NPC

    public bool HasAnyActiveTags()
    {
        for (int i = 0; i < TimeLeftOnNPC.Length; i++)
            if (TimeLeftOnNPC[i] > 0) return true;
        return false;
    }
}
```

Each player has their own dictionary tracking all active whips. Each whip maintains separate NPC timer arrays.

### 2. Harmony Prefix Pattern (Replace Vanilla)

All 10 patches use the same pattern: prefix that returns `false` to skip vanilla when enabled, `true` to run vanilla when disabled:

```csharp
[HarmonyPatch]
public static class TagPatches
{
    public static bool TrySetActiveEffect_Prefix(TagEffectState __instance, int type)
    {
        if (!Enabled) return true;  // Run vanilla when disabled
        try
        {
            SetActiveEffect(__instance, type);
        }
        catch (Exception ex)
        {
            _log?.Error($"TrySetActiveEffect error: {ex.Message}");
            return true;  // Fall back to vanilla on error
        }
        return false;  // Skip vanilla
    }
}
```

The try-catch with vanilla fallback is critical: if anything goes wrong, the mod gracefully degrades to vanilla behavior instead of crashing.

### 3. Multi-Whip Hit Effects

When calculating hit damage, iterate all active whips instead of checking just one:

```csharp
// In ModifyHit_Prefix
foreach (var entry in dict.Values)
{
    if (entry.TimeLeftOnNPC[npcIdx] <= 0 || entry.Effect == null) continue;

    // entry.Effect is UniqueTagEffect — direct method calls, no reflection
    if (!entry.Effect.CanRunHitEffects(owner, projectile, npc)) continue;

    entry.Effect.ModifyTaggedHit(owner, projectile, npc, ref damage, ref crit);
}
```

### 4. Timer Management

Each frame, decrement all whip timers and clean up expired entries:

```csharp
private static List<int> _toRemove = new List<int>();

// In Update_Prefix
_toRemove.Clear();
foreach (var kvp in dict)
{
    var entry = kvp.Value;
    for (int i = 0; i < entry.TimeLeftOnNPC.Length; i++)
        if (entry.TimeLeftOnNPC[i] > 0) entry.TimeLeftOnNPC[i]--;

    if (!entry.HasAnyActiveTags())
        _toRemove.Add(kvp.Key);
}

for (int i = 0; i < _toRemove.Count; i++)
    dict.Remove(_toRemove[i]);
```

The reusable `_toRemove` list avoids allocations per frame.

### 5. Maintaining Vanilla Compatibility

When setting a new active whip, update vanilla's internal fields so unpatched code doesn't crash:

```csharp
private static void SetActiveEffect(TagEffectState tagState, int type)
{
    var effect = GetEffect(type);
    _effectField.SetValue(tagState, effect);        // Keep vanilla _effect updated (private field)
    _typeSetter.Invoke(tagState, new object[] { type }); // Keep vanilla Type updated (private setter)

    // Also track in our multi-tag dictionary
    MultiTagState.AddOrUpdateEntry(playerIndex, type, effect);
}
```

## Patches Applied

| Method | Purpose |
|--------|---------|
| `TrySetActiveEffect` | Add whip to multi-tag dict instead of replacing |
| `TryApplyTagToNPC` | Tag NPC in specific whip's entry |
| `ModifyHit` | Apply damage from ALL active whips |
| `OnHit` | Fire hit effects from ALL active whips |
| `Update` | Tick down all whip timers, remove expired |
| `IsNPCTagged` | True if ANY whip has active tag on NPC |
| `CanProcOnNPC` | True if ANY whip has active proc on NPC |
| `TryEnableProcOnNPC` | Enable proc for matching whip |
| `ClearProcOnNPC` | Clear proc for all whips on NPC |
| `ResetNPCSlotData` | Clear all whip data for NPC |

## Lessons Learned

1. **Prefix + return false replaces vanilla** - Cleaner than transpilers when you need to replace entire method logic
2. **Always fall back to vanilla on error** - Return true in catch blocks so crashes degrade gracefully
3. **Reuse collections to avoid GC pressure** - Static lists for per-frame operations
4. **Keep vanilla fields updated** - Other code (including other mods) may read vanilla state
5. **Reset state on world load** - NPC indices get reused, so clear stale timer data

For more on Harmony patching patterns, see [Harmony Basics](../harmony-basics).
