---
title: Harmony Patching Guide for Terraria 1.4.5
description: Learn Harmony runtime patching for Terraria 1.4.5 modding. Covers prefix, postfix, and transpiler patches with real examples from working mods.
nav_order: 6
---

# Harmony Basics

This guide covers the essential Harmony concepts for TerrariaModder development.

## What is Harmony?

[Harmony](https://github.com/pardeike/Harmony) is a library for patching .NET methods at runtime. It lets you:
- Run code **before** a method executes (prefix)
- Run code **after** a method executes (postfix)
- Replace a method entirely (transpiler)

TerrariaModder uses Harmony to hook into Terraria's game loop, player updates, and other systems without modifying game files.

## Why Harmony?

Without Harmony, modifying game behavior would require:
- Decompiling and recompiling Terraria (breaks with updates)
- IL weaving at build time (complex, fragile)
- Memory patching (platform-specific, dangerous)

Harmony provides clean, maintainable runtime patching that survives game updates.

## Prefix vs Postfix Patches

### Postfix Patches (Most Common)

A **postfix** runs after the original method completes. Use it to:
- React to game state changes
- Add behavior without changing original logic
- Read values computed by the original method

```csharp
[HarmonyPatch(typeof(Terraria.Main), "DoUpdate")]
public static class DoUpdatePatch
{
    [HarmonyPostfix]
    public static void Postfix()
    {
        // Runs after every Main.DoUpdate() call
        // Game state is already updated
        MyMod.OnGameUpdate();
    }
}
```

**Example from SkipIntro:** Patches `Main.DoUpdate` to check if assets are loaded and skip the splash screen.

### Prefix Patches

A **prefix** runs before the original method. Use it to:
- Conditionally skip the original method
- Modify input parameters
- Set up state before the method runs

```csharp
[HarmonyPatch(typeof(Terraria.Player), "Update")]
public static class PlayerUpdatePatch
{
    [HarmonyPrefix]
    public static bool Prefix(Player __instance)
    {
        // Return true  = run original method
        // Return false = skip original method

        if (ShouldSkipUpdate(__instance))
            return false;

        return true;
    }
}
```

**Example from PetChests:** Uses a prefix on `TryGetContainerIndex` to return a piggy bank container for cosmetic pets, skipping the original method.

## Patch Method Signatures

Harmony uses special parameter names (prefixed with `__`) to inject values:

### `__instance`

The object instance the method is called on (for instance methods):

```csharp
public static void Postfix(Player __instance)
{
    // __instance is the Player being updated
    int health = __instance.statLife;
}
```

### `__result`

The return value of the method (postfix only):

```csharp
[HarmonyPatch(typeof(Projectile), "IsInteractable")]
public static class IsInteractablePatch
{
    [HarmonyPostfix]
    public static void Postfix(Projectile __instance, ref bool __result)
    {
        // Modify the return value
        if (IsPetWithStorage(__instance))
            __result = true;
    }
}
```

### `ref` Parameters

Use `ref` to modify parameters or return values:

```csharp
public static void Postfix(ref bool __result)
{
    __result = true;  // Changes what the method "returns"
}
```

### Original Parameters

Access original method parameters by name:

```csharp
[HarmonyPatch(typeof(Player), "AddBuff")]
public static class AddBuffPatch
{
    // Original: void AddBuff(int type, int time, bool quiet)
    [HarmonyPostfix]
    public static void Postfix(Player __instance, int type, int time)
    {
        _log.Debug($"Player got buff {type} for {time} ticks");
    }
}
```

## How Patches Are Applied

TerrariaModder uses two complementary systems for applying Harmony patches. Understanding both will help you choose the right approach.

### Automatic Patching (Attribute-Based)

The injector automatically applies all `[HarmonyPatch]` attribute patches from your mod assembly at startup. You don't need to call `PatchAll()` yourself for attribute-based patches:

```csharp
// This patch is applied AUTOMATICALLY by the injector at startup.
// No PatchAll() call needed in your mod code.
[HarmonyPatch(typeof(Terraria.Player), "Update")]
public static class PlayerUpdatePatch
{
    [HarmonyPostfix]
    public static void Postfix(Player __instance)
    {
        MyMod.OnPlayerUpdate(__instance);
    }
}
```

The injector loads all mod assemblies, then calls `harmony.PatchAll()` for each one before the game starts. Terraria types are already loaded at this point, so attribute-based patches work immediately.

### Lifecycle Hooks (for Manual Patches)

For patches that need to be applied at specific points in the game's startup, or that require setup before patching, implement **lifecycle hook** methods. The injector discovers these static methods on your mod's types and calls them at the right time:

```csharp
public class Mod : IMod
{
    private static Harmony _harmony;
    private static ILogger _log;

    public void Initialize(ModContext context)
    {
        _log = context.Logger;
        // Don't patch here -- use OnGameReady instead
    }

    /// <summary>
    /// Called by injector when Main.Initialize() completes.
    /// GraphicsDevice, Window.Handle, and Main.instance are ready.
    /// Safe to apply manual Harmony patches here.
    /// </summary>
    public static void OnGameReady()
    {
        _harmony = new Harmony("com.yourname.yourmod");

        try
        {
            var mainType = typeof(Terraria.Main);
            var method = mainType.GetMethod("DoUpdate",
                BindingFlags.NonPublic | BindingFlags.Instance);

            if (method != null)
            {
                _harmony.Patch(method,
                    postfix: new HarmonyMethod(typeof(Mod), nameof(DoUpdate_Postfix)));
                _log?.Info("Manual patch applied in OnGameReady");
            }
        }
        catch (Exception ex)
        {
            _log?.Error($"Patch failed: {ex.Message}");
        }
    }

    public static void DoUpdate_Postfix(Main __instance)
    {
        // Runs after Main.DoUpdate
    }

    public void Unload()
    {
        _harmony?.UnpatchAll("com.yourname.yourmod");
    }
}
```

### Available Lifecycle Hooks

The injector discovers these as **public static void** methods on any type in your mod assembly:

| Hook | When It Fires | Use Cases |
|------|---------------|-----------|
| `OnGameReady()` | After `Main.Initialize()` completes | Manual Harmony patches, accessing GraphicsDevice, Window.Handle |
| `OnContentLoaded()` | After `Main.LoadContent()` completes | Custom textures, fonts (see timing note below) |
| `OnFirstUpdate()` | On the first `Main.Update()` frame | One-time setup that needs the full game loop running |
| `OnShutdown()` | When the game exits (`Main_Exiting`) | Save state, cleanup before Terraria disposes systems |

### Timing Gotchas

**OnContentLoaded fires BEFORE OnGameReady.** XNA calls `LoadContent()` from within `Initialize()`, so the order is:

1. `Initialize()` starts
2. `LoadContent()` called internally by XNA → **OnContentLoaded fires**
3. `Initialize()` finishes → **OnGameReady fires**
4. First `Update()` → **OnFirstUpdate fires**

If your code needs both content and patches ready, put it in `OnGameReady` since by that point `LoadContent` has already completed. The Core framework handles this: `AssetSystem.OnContentLoaded()` is a no-op if patches aren't applied yet, and runs the real logic from `OnGameReady` instead.

### When to Use What

| Scenario | Approach |
|----------|----------|
| Standard game method patches (Player.Update, Main.DoUpdate, etc.) | **Attribute-based**, auto-applied by injector |
| Manual patches needing reflection to find methods | **OnGameReady** lifecycle hook |
| Custom texture loading | **OnGameReady** (content + GraphicsDevice ready) |
| One-time setup needing full game loop | **OnFirstUpdate** |
| Saving state on exit | **OnShutdown** |

## Harmony IDs and Cleanup

### Unique Patch IDs

Every Harmony instance needs a unique ID. Use reverse domain notation:

```csharp
var harmony = new Harmony("com.yourname.yourmod");
```

This ID is used for:
- Identifying your patches
- Unpatching only your patches (not other mods')
- Debugging patch conflicts

### Cleanup on Unload

**Always unpatch in your `Unload()` method:**

```csharp
public void Unload()
{
    _harmony?.UnpatchAll("com.yourname.yourmod");
}
```

This ensures:
- Your patches don't persist after mod is disabled
- No memory leaks from dangling patch references
- Clean state for mod reload

**Note:** Attribute-based patches auto-applied by the injector are unpatched automatically when the game closes. If you also apply manual patches in `OnGameReady()`, create your own Harmony instance there and call `UnpatchAll()` in `Unload()` with your unique ID to clean up just your manual patches.

## Common Pitfalls

### 1. Patching Non-Existent Methods

Terraria's internal methods can change between versions. Always check:

```csharp
var method = typeof(Main).GetMethod("DoUpdate",
    BindingFlags.NonPublic | BindingFlags.Instance);

if (method == null)
{
    _log.Warn("DoUpdate method not found - patch skipped");
    return;
}

_harmony.Patch(method, postfix: new HarmonyMethod(typeof(Mod), "DoUpdate_Postfix"));
```

### 2. Wrong BindingFlags

Most Terraria methods are non-public:

```csharp
// Common flags combinations
BindingFlags.Public | BindingFlags.Static          // public static
BindingFlags.Public | BindingFlags.Instance        // public instance
BindingFlags.NonPublic | BindingFlags.Static       // private/internal static
BindingFlags.NonPublic | BindingFlags.Instance     // private/internal instance
```

### 3. Static State in Patches

Patch methods must be `public static`, but be careful with static state:

```csharp
private static bool _hasRun = false;

public static void Postfix()
{
    if (_hasRun) return;  // Guard against running multiple times

    DoOnce();
    _hasRun = true;
}

// Reset in Unload!
public void Unload()
{
    _hasRun = false;  // Reset for next session
    _harmony?.UnpatchAll("my.mod.id");
}
```

### 4. Exceptions in Patches

Exceptions in patches can crash the game. Always use try/catch:

```csharp
public static void Postfix(Player __instance)
{
    try
    {
        // Your code here
    }
    catch (Exception ex)
    {
        // Log but don't crash
        _staticLog?.Error($"Patch error: {ex.Message}");
    }
}
```

### 5. Performance in Per-Frame Patches

Patches on `DoUpdate` or `Player.Update` run 60 times per second:

```csharp
private static int _counter = 0;

public static void Postfix()
{
    // Throttle expensive operations
    if (++_counter < 60) return;  // Run once per second
    _counter = 0;

    DoExpensiveOperation();
}
```

## Debugging Patches

### Log Everything

```csharp
public static void OnGameReady()
{
    _log.Debug("Starting patch process...");

    var mainType = typeof(Terraria.Main);
    _log.Debug($"Found Main type: {mainType != null}");

    var method = mainType.GetMethod("DoUpdate",
        BindingFlags.NonPublic | BindingFlags.Instance);
    _log.Debug($"Found DoUpdate: {method != null}");

    if (method != null)
    {
        _harmony.Patch(method, postfix: new HarmonyMethod(typeof(Mod), "MyPostfix"));
        _log.Info("Patch applied successfully");
    }
}
```

### Verify Patch is Running

```csharp
private static bool _loggedOnce = false;

public static void Postfix()
{
    if (!_loggedOnce)
    {
        _staticLog?.Info("Postfix is running!");
        _loggedOnce = true;
    }
}
```

## Manual Patching vs Attributes

### Attribute-Based (Simpler)

```csharp
// Auto-applied by the injector at startup. No PatchAll() call needed.
[HarmonyPatch(typeof(Terraria.Main), "DoUpdate")]
public static class DoUpdatePatch
{
    [HarmonyPostfix]
    public static void Postfix() { }
}
```

### Manual Patching (More Control)

```csharp
var method = typeof(Main).GetMethod("DoUpdate",
    BindingFlags.NonPublic | BindingFlags.Instance);

var postfix = typeof(Mod).GetMethod("DoUpdate_Postfix",
    BindingFlags.Public | BindingFlags.Static);

_harmony.Patch(method, postfix: new HarmonyMethod(postfix));
```

Manual patching is useful when:
- Method names vary between versions
- You need conditional patching
- You want detailed logging of what was patched

## Further Reading

- [Harmony Documentation](https://harmony.pardeike.net/articles/intro.html)
- [SkipIntro Walkthrough](walkthroughs/skip-intro) - Simple postfix example
- [AutoBuffs Walkthrough](walkthroughs/auto-buffs) - Player.Update patching
- [PetChests Walkthrough](walkthroughs/pet-chests) - Multiple coordinated patches
