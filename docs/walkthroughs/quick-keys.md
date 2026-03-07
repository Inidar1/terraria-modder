---
title: QuickKeys Mod - Auto-Torch and Hotkeys for Terraria 1.4.5
description: Walkthrough of the QuickKeys mod for Terraria 1.4.5. Auto-torch, recall, quick-stack, ruler overlay, and extended hotbar using input handling and reflection.
parent: Walkthroughs
nav_order: 3
---

# QuickKeys Walkthrough

**Difficulty:** Advanced
**Concepts:** Complex input handling, inventory manipulation, reflection, multiple features

> **Attribution:** QuickKeys is inspired by the [Helpful Hotkeys](https://steamcommunity.com/sharedfiles/filedetails/?id=2561598917) mod for tModLoader by direwolf420. This implementation provides similar quality-of-life features for vanilla Terraria 1.4.5.

QuickKeys is the most complex bundled mod. It provides auto-torch placement, recall hotkey, quick-stack, ruler overlay, and an extended hotbar (slots 11-20 via NumPad).

## What It Does

- **Auto-Torch (Tilde):** Places torches at cursor position
- **Auto-Recall (Home):** Uses recall potions/magic mirror
- **Quick-Stack (End):** Quick-stacks to nearby chests
- **Ruler (K):** Toggles a ruler distance overlay (sets `player.rulerLine` each frame, overriding vanilla's per-frame reset)
- **Extended Hotbar (NumPad 1-9, 0):** Quick switch to hotbar slots 11-20 (disabled by default, enable via F6 mod menu)

## Key Concepts

### 1. Direct Field and Method Access

QuickKeys accesses game internals directly via the Terraria.exe reference:

```csharp
using Terraria;
using Terraria.ID;
using Microsoft.Xna.Framework;

// Inventory: direct field access
Item[] inventory = player.inventory;
int selectedSlot = player.selectedItem;

// Item check: direct method call
player.ItemCheck();

// selectedItemState.Select() to change active item
player.selectedItemState.Select(slotIndex);

// Tile set (for torch detection): direct
bool[] torchSet = TileID.Sets.Torches;
bool isTorch = torchSet[item.createTile];
```

No reflection cache needed — all these are public members.

### 2. Item Slot Swapping

Since `selectedItem` is often read-only, swap inventory slots:

```csharp
private int _savedSlot = -1;
private int _targetSlot = -1;
private bool _needsRestore = false;

private void UseItemInSlot(Player player, int slot)
{
    _savedSlot = player.selectedItem;
    _targetSlot = slot;

    // Physically swap the items
    Item temp = player.inventory[_savedSlot];
    player.inventory[_savedSlot] = player.inventory[slot];
    player.inventory[slot] = temp;

    // Trigger item use
    player.controlUseItem = true;
    _needsRestore = true;
}

// Call every frame
private void RestoreIfNeeded(Player player)
{
    if (!_needsRestore) return;
    if (player.itemAnimation > 0) return; // Still using item

    // Swap back
    Item temp = player.inventory[_savedSlot];
    player.inventory[_savedSlot] = player.inventory[_targetSlot];
    player.inventory[_targetSlot] = temp;

    _needsRestore = false;
}
```

### 3. Finding Items by Type

Search inventory slots (indices 0-58, 59 slots total):

```csharp
private int FindItemOfTypes(Player player, int[] types)
{
    for (int i = 0; i < player.inventory.Length; i++)
    {
        Item item = player.inventory[i];
        if (item != null && item.stack > 0)
        {
            if (Array.IndexOf(types, item.type) >= 0)
                return i;
        }
    }
    return -1;
}

// Recall items - priority order (infinite-use first, then consumables)
int[] recallItems = {
    3124,  // Cell Phone (highest priority - infinite use, most features)
    5437,  // Shellphone (base)
    5358,  // Shellphone (spawn)
    5359,  // Shellphone (ocean)
    5360,  // Shellphone (underworld)
    5361,  // Shellphone (home)
    50,    // Magic Mirror
    3199,  // Ice Mirror
    2350   // Recall Potion (lowest priority - consumable)
};
```

### 4. Torch Placement

Find a torch in inventory and place it at the cursor position. Key points:
- Use `item.createTile` for the tile type (supports all torch variants)
- Use `item.placeStyle` for colored torches (blue, green, etc.)
- Verify `stack > 0` before placing
- Call `TurnToAir()` when stack reaches 0

```csharp
private void PlaceTorch()
{
    // Find first torch with stack > 0
    Item torchItem = null;
    for (int i = 0; i < player.inventory.Length; i++)
    {
        Item item = player.inventory[i];
        if (item.stack > 0 && TileID.Sets.Torches[item.createTile])
        {
            torchItem = item;
            break;
        }
    }
    if (torchItem == null) return; // No torches

    // Get torch properties
    int tileType = torchItem.createTile;   // Tile type (4 for torches)
    int placeStyle = torchItem.placeStyle; // Style for colored variants

    // Convert mouse to tile coordinates
    int tileX = (int)(Main.MouseWorld.X / 16f);
    int tileY = (int)(Main.MouseWorld.Y / 16f);

    // Place using correct type and style
    bool placed = WorldGen.PlaceTile(tileX, tileY, tileType,
        mute: false, forced: false, plr: Main.myPlayer, style: placeStyle);

    if (placed)
    {
        // Consume torch properly
        torchItem.stack--;
        if (torchItem.stack <= 0)
            torchItem.TurnToAir(); // Remove empty item from inventory
    }
}
```

### 5. Multiple Keybinds

Register multiple actions:

```csharp
public void Initialize(ModContext context)
{
    context.RegisterKeybind("auto-torch", "Auto Torch",
        "Place a torch near cursor", "OemTilde", OnAutoTorch);

    context.RegisterKeybind("auto-recall", "Auto Recall",
        "Use recall item", "Home", UseRecall);

    context.RegisterKeybind("quick-stack", "Quick Stack",
        "Quick stack to chests", "End", QuickStack);

    // Extended hotbar: NumPad1-9 for slots 11-19, NumPad0 for slot 20
    for (int i = 1; i <= 9; i++)
    {
        int slot = i + 10; // NumPad1 = slot 11, etc.
        context.RegisterKeybind($"hotbar-{slot}", $"Hotbar Slot {slot}",
            $"Switch to hotbar slot {slot}", $"NumPad{i}", () => SwitchToSlot(slot));
    }
    context.RegisterKeybind("hotbar-20", "Hotbar Slot 20",
        "Switch to hotbar slot 20", "NumPad0", () => SwitchToSlot(20));
}
```

## Code Structure Overview

```csharp
public class Mod : IMod
{
    // State for item use restoration
    private int _savedSlot = -1;
    private bool _needsRestore = false;

    // Ruler state
    private static bool _rulerActive = false;

    public void Initialize(ModContext context)
    {
        RegisterKeybinds(context);
        FrameEvents.OnPostUpdate += OnPostUpdate;
    }

    // No Harmony patches needed. QuickKeys uses keybind callbacks
    // and FrameEvents.OnPostUpdate for all its features.

    // Keybind handlers
    private void PlaceTorch() { /* ... */ }
    private void UseRecall() { /* ... */ }
    private void QuickStack() { /* ... */ }
    private void OnRulerToggle() { _rulerActive = !_rulerActive; }
    private void SwitchToSlot(int slot) { /* ... */ }

    // Frame event to restore inventory and maintain ruler state
    // QuickKeys uses FrameEvents.OnPostUpdate, not Player.Update patching
    private void OnPostUpdate()
    {
        // Ruler: re-apply rulerLine each frame (vanilla ResetEffects clears it)
        if (_rulerActive)
        {
            player.rulerLine = true;
            player.builderAccStatus[0] = 0;
        }

        // Item restoration after quick-use
        if (_needsRestore && player.itemAnimation <= 0)
        {
            RestoreInventory(player);
        }
    }
}
```

## Lessons Learned

1. **Access public members directly** - `player.inventory`, `player.ItemCheck()`, `TileID.Sets.Torches` — no reflection needed
2. **Use `selectedItemState.Select()`** - Cleaner than swapping; falls back to swap if Select fails
3. **Search full inventory** - Not just hotbar (10 slots)
4. **Bounds check everything** - Tile access, array access
5. **Multiplayer sync** - Send tile updates in multiplayer
6. **Restore state** - Always clean up after temporary changes
7. **Multiple approaches** - Have fallbacks when the primary method fails

For more on Harmony patching patterns, see [Harmony Basics](../harmony-basics).
