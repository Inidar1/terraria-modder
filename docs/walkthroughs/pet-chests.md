---
title: PetChests Mod - Use Pets as Piggy Banks in Terraria 1.4.5
description: Walkthrough of the PetChests mod for Terraria 1.4.5. Right-click cosmetic pets to open piggy bank storage using projectile interaction and Harmony patches.
parent: Walkthroughs
nav_order: 4
---

# PetChests Walkthrough

**Difficulty:** Intermediate
**Concepts:** Projectile interaction, multiple patches, state management

PetChests allows right-clicking on any cosmetic pet projectile to open piggy bank storage.

## What It Does

When you right-click on any summoned cosmetic pet, it opens your piggy bank storage. This turns every cosmetic pet into a portable piggy bank - no consumables or special items needed.

## Key Concepts

### 1. Multiple Harmony Patches

This mod patches several methods to intercept projectile interaction:

> **Note:** The code examples below are simplified for clarity. The actual
> implementation uses a `PetInteraction` helper class with more complex state
> management. The `PatchMethod()` call shown is pseudocode - see the source code
> or [Harmony Basics](../harmony-basics) for actual patching patterns.

```csharp
private void ApplyPatches()
{
    var projectileType = typeof(Projectile);
    var playerType = typeof(Player);

    // Pseudocode - actual implementation uses Harmony.Patch() directly
    // Is the projectile interactable?
    PatchMethod(projectileType, "IsInteractable");

    // Can we get a container from it?
    PatchMethod(projectileType, "TryGetContainerIndex");

    // Player proximity check
    PatchMethod(playerType, "HandleBeingInChestRange");

    // Sound effect override
    PatchMethod(typeof(Main), "PlayInteractiveProjectileOpenCloseSound");
}
```

### 2. Identifying Cosmetic Pet Projectiles

Check if a projectile is a cosmetic pet (not a light pet like fairies):

```csharp
private static bool IsCosmeticPet(Projectile proj)
{
    // Check if it's owned by local player
    if (proj.owner != Main.myPlayer) return false;

    int type = proj.type;

    // Use Main.projPet[] array to identify pets
    if (!Main.projPet[type]) return false;

    // Filter out light pets (fairies, etc.)
    if (ProjectileID.Sets.LightPet[type]) return false;

    // Skip Chester (960) and Flying Piggy Bank (525) - vanilla handles these
    if (type == 960 || type == 525) return false;

    return true;
}
```

This makes ALL cosmetic pets work as piggy banks, not just specific ones like Chester.

### 3. Intercepting Interaction

Make cosmetic pets appear interactable:

```csharp
[HarmonyPatch(typeof(Projectile), "IsInteractable")]
public static class IsInteractiblePatch
{
    [HarmonyPostfix]
    public static void Postfix(Projectile __instance, ref bool __result)
    {
        // If original returned false, check if it's a cosmetic pet
        if (!__result && IsCosmeticPet(__instance))
        {
            __result = true;
        }
    }
}
```

### 4. Opening Piggy Bank

When interaction happens, open piggy bank:

```csharp
private static void OpenPiggyBank(Player player)
{
    // Set chest to piggy bank mode (-2)
    player.chest = -2;

    // Trigger UI update
    Main.playerInventory = true;

    // Play sound
    SoundEngine.PlaySound(SoundID.MenuOpen);

    _log.Info("Opened piggy bank via pet");
}
```

### 5. Managing State

Track when piggy bank is open via pet:

```csharp
private static bool _openedViaPet = false;
private static int _closeCooldown = 0;

private static void OnPetInteract(Projectile pet)
{
    Player player = Main.LocalPlayer;

    if (player.chest == -2) // Already open
    {
        ClosePiggyBank(player);
    }
    else
    {
        OpenPiggyBank(player);
        _openedViaPet = true;
    }
}

// Close when inventory closes
public static void PlayerUpdate_Postfix(Player __instance, int i)
{
    if (i != Main.myPlayer) return;

    if (_openedViaPet && !Main.playerInventory)
    {
        ClosePiggyBank(__instance);
        _openedViaPet = false;
    }

    // Cooldown to prevent immediate reopen
    if (_closeCooldown > 0)
        _closeCooldown--;
}
```

### 6. Handling Edge Cases

```csharp
// Prevent issues with cooldown
private static void ClosePiggyBank(Player player)
{
    player.chest = -1;
    _closeCooldown = 10; // Frames of cooldown
    _log.Info("Closed piggy bank");
}

// Check cooldown before opening
private static bool CanOpen()
{
    return _closeCooldown <= 0;
}
```

## Simplified Code Structure

```csharp
public class Mod : IMod
{
    private Harmony _harmony;
    private static bool _openedViaPet = false;
    private static int _cooldown = 0;

    public void Initialize(ModContext context)
    {
        // Attribute-based patches are auto-applied by injector
        // Manual patches go in OnGameReady() if needed
    }

    private static bool IsCosmeticPet(Projectile proj)
    {
        return proj.owner == Main.myPlayer &&
               Main.projPet[proj.type] &&
               !ProjectileID.Sets.LightPet[proj.type] &&
               proj.type != 960 && proj.type != 525;
    }

    // Patch: Make cosmetic pets appear interactable
    public static void IsInteractible_Postfix(Projectile __instance, ref bool __result)
    {
        if (!__result && IsCosmeticPet(__instance))
            __result = true;
    }

    // Patch (Prefix): Return piggy bank container index for cosmetic pets
    // Prefix returns false to skip vanilla (which would check for Chester/Piggy Bank types)
    public static bool TryGetContainerIndex_Prefix(Projectile __instance,
        ref int containerIndex, ref bool __result)
    {
        if (IsCosmeticPet(__instance))
        {
            containerIndex = -2;  // Piggy bank
            __result = true;
            return false;  // Skip vanilla
        }
        return true;  // Run vanilla for non-pets
    }

    // Patch: Track state each frame
    public static void PlayerUpdate_Postfix(Player __instance, int i)
    {
        if (i != Main.myPlayer) return;
        if (_cooldown > 0) _cooldown--;

        if (_openedViaPet && !Main.playerInventory)
        {
            _openedViaPet = false;
            __instance.chest = -1;
        }
    }

    private static void OpenPiggyBank(Player player)
    {
        player.chest = -2;
        Main.playerInventory = true;
    }
}
```

## Configuration

| Config Key | Type | Default | Description |
|------------|------|---------|-------------|
| `interactionRange` | int | 200 | Max distance in pixels to interact with a pet (range: 100-500) |

## Lessons Learned

1. **Multiple patches work together** - Intercept at different points
2. **Check ownership** - Only affect local player's pets
3. **Use cooldowns** - Prevent rapid open/close flickering
4. **Track your state** - Know when you opened something
5. **Clean up on inventory close** - Watch for UI state changes
6. **Pet array identification** - Use `Main.projPet[]` and `ProjectileID.Sets.LightPet` to identify pet projectiles

For more on coordinating multiple Harmony patches, see [Harmony Basics](../harmony-basics).
