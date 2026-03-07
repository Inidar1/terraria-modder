---
title: AutoBuffs Mod - Automatic Furniture Buffs for Terraria 1.4.5
description: Walkthrough of the AutoBuffs mod for Terraria 1.4.5. Automatically applies nearby furniture buffs using tile scanning and Player.Update patching.
parent: Walkthroughs
nav_order: 2
---

# AutoBuffs Walkthrough

**Difficulty:** Intermediate
**Concepts:** Tile scanning, buff application, Player.Update patch

AutoBuffs automatically applies buffs from nearby furniture like crystal balls, ammo boxes, and sharpening stations.

## What It Does

Every 10 game ticks (~6 times per second), the mod scans tiles around the player for buff-providing furniture and applies the corresponding buffs automatically.

## Supported Furniture

The mod supports 6 furniture types:

| Furniture | Tile ID | Buff | Buff ID |
|-----------|---------|------|---------|
| Crystal Ball | 125 | Clairvoyance | 29 |
| Ammo Box | 287 | Ammo Box | 93 |
| Bewitching Table | 354 | Bewitched | 150 |
| Sharpening Station | 377 | Sharpened | 159 |
| War Table | 464 | War Table | 348 |
| Slice of Cake | 621 | Sugar Rush | 192 |

Each buff type can be individually enabled/disabled in the mod config.

## Key Concepts

### 1. Patching Player.Update

We patch the player update loop to run our scan:

```csharp
var playerType = typeof(Terraria.Player);
var updateMethod = playerType.GetMethod("Update",
    BindingFlags.Public | BindingFlags.Instance);

_harmony.Patch(updateMethod,
    postfix: new HarmonyMethod(typeof(Mod), "PlayerUpdatePostfix"));
```

### 2. Throttled Scanning

Don't scan every frame - that's 60 operations per second. The actual code uses 10-tick intervals:

```csharp
private const int SCAN_CADENCE_TICKS = 10;
private static int _tickCounter = 0;

public static void PlayerUpdatePostfix(Player __instance, int i)
{
    if (i != Main.myPlayer) return;

    if (++_tickCounter < SCAN_CADENCE_TICKS) return;
    _tickCounter = 0;

    ScanAndApplyBuffs(__instance);
}
```

This means buffs are checked approximately 6 times per second, balancing responsiveness with performance.

### 3. Tile Scanning with Radius

Scan a circular area around the player:

```csharp
private static void ScanForFurniture(Player player, int scanRadius)
{
    int playerTileX = (int)(player.position.X / 16f);
    int playerTileY = (int)(player.position.Y / 16f);
    int radiusSq = scanRadius * scanRadius;

    int minX = Math.Max(0, playerTileX - scanRadius);
    int maxX = Math.Min(Main.maxTilesX - 1, playerTileX + scanRadius);
    int minY = Math.Max(0, playerTileY - scanRadius);
    int maxY = Math.Min(Main.maxTilesY - 1, playerTileY + scanRadius);

    for (int x = minX; x <= maxX; x++)
    {
        int dx = x - playerTileX;
        int dxSq = dx * dx;

        // Skip entire column if too far
        if (dxSq > radiusSq) continue;

        for (int y = minY; y <= maxY; y++)
        {
            int dy = y - playerTileY;

            // Check circular radius
            if (dxSq + dy * dy > radiusSq) continue;

            Tile tile = Main.tile[x, y];
            if (!tile.active()) continue;

            CheckTileForBuff(player, tile.type);
        }
    }
}
```

The circular radius check (`dxSq + dy * dy > radiusSq`) ensures furniture at the corners of the scan area aren't included if they're outside the actual radius.

### 4. Buff Mapping

Map tile types to buff information:

```csharp
private struct BuffInfo
{
    public int BuffId;
    public bool IsSugarRush;  // Special handling for limited-duration buff
    public Func<bool> IsEnabled;  // Config check
}

private static readonly Dictionary<ushort, BuffInfo> _tileToBuffMap = new Dictionary<ushort, BuffInfo>
{
    { 125, new BuffInfo { BuffId = 29, IsSugarRush = false } },   // Crystal Ball
    { 287, new BuffInfo { BuffId = 93, IsSugarRush = false } },   // Ammo Box
    { 354, new BuffInfo { BuffId = 150, IsSugarRush = false } },  // Bewitching Table
    { 377, new BuffInfo { BuffId = 159, IsSugarRush = false } },  // Sharpening Station
    { 464, new BuffInfo { BuffId = 348, IsSugarRush = false } },  // War Table
    { 621, new BuffInfo { BuffId = 192, IsSugarRush = true } },   // Slice of Cake
};
```

### 5. Avoiding Duplicate Work

Track which tiles we've already found this scan:

```csharp
private static HashSet<ushort> _foundTiles = new HashSet<ushort>();

private static void ScanAndApplyBuffs(Player player)
{
    // Only scan for buffs we don't already have
    var neededTiles = new HashSet<ushort>();
    foreach (var kvp in _tileToBuffMap)
    {
        if (!kvp.Value.IsEnabled()) continue;

        int buffIndex = player.FindBuffIndex(kvp.Value.BuffId);
        if (buffIndex < 0)  // Don't have this buff
        {
            neededTiles.Add(kvp.Key);
        }
    }

    // Early exit if all buffs already active
    if (neededTiles.Count == 0) return;

    _foundTiles.Clear();
    ScanForFurniture(player, scanRadius);

    // Apply buffs for found tiles
    foreach (ushort tileType in _foundTiles)
    {
        if (_tileToBuffMap.TryGetValue(tileType, out BuffInfo info))
        {
            player.AddBuff(info.BuffId, duration, quiet: true);
        }
    }
}
```

### 6. Special Handling: Sugar Rush

The Slice of Cake buff (Sugar Rush) has special handling because it's a limited-duration buff rather than an "effectively infinite" buff like the others:

```csharp
private const int EFFECTIVELY_INFINITE_BUFF_TICKS = 60 * 60 * 24;  // 24 hours
private const int SUGAR_RUSH_DURATION_TICKS = 60 * 60 * 2;         // 2 hours
private const int SUGAR_RUSH_REAPPLY_THRESHOLD = 60 * 2;           // 2 seconds

private static void ApplyBuff(Player player, BuffInfo info)
{
    int duration = info.IsSugarRush
        ? SUGAR_RUSH_DURATION_TICKS
        : EFFECTIVELY_INFINITE_BUFF_TICKS;

    player.AddBuff(info.BuffId, duration, quiet: true);
}
```

Sugar Rush is reapplied when its duration falls below 2 seconds, ensuring it stays active while near the Slice of Cake.

## Simplified Code Structure

> **Note:** This is simplified pseudocode for illustration. The actual implementation
> uses a `BuffScanner` helper class with additional throttling logic. Method signatures
> and patterns differ from production code.

```csharp
public class Mod : IMod
{
    private Harmony _harmony;
    private static int _tickCounter = 0;

    public void Initialize(ModContext context)
    {
        // Harmony setup happens in OnGameReady (see below)
    }

    public static void OnGameReady()
    {
        // Apply manual patches that need reflection to find methods.
        // Attribute-based [HarmonyPatch] patches are auto-applied by the
        // injector -- don't call PatchAll() for those or they'll double-apply.
        _harmony = new Harmony("com.terrariamodder.autobuffs");
        var method = typeof(Terraria.Player).GetMethod("Update",
            BindingFlags.Public | BindingFlags.Instance);
        _harmony.Patch(method,
            postfix: new HarmonyMethod(typeof(Mod), nameof(PlayerUpdatePostfix)));
    }

    public static void PlayerUpdatePostfix(Player __instance, int i)
    {
        if (i != Main.myPlayer) return;

        if (++_tickCounter < 10) return;  // Every 10 ticks
        _tickCounter = 0;

        ScanAndApplyBuffs(__instance);
    }

    private static void ScanAndApplyBuffs(Player player)
    {
        int cx = (int)(player.position.X / 16f);
        int cy = (int)(player.position.Y / 16f);
        int radius = 40;  // Configurable scan radius (default 40, range 5-100)

        for (int x = cx - radius; x <= cx + radius; x++)
        {
            for (int y = cy - radius; y <= cy + radius; y++)
            {
                if (x < 0 || x >= Main.maxTilesX) continue;
                if (y < 0 || y >= Main.maxTilesY) continue;

                Tile tile = Main.tile[x, y];
                if (tile.active() && IsFurnitureWeTrack(tile.type))
                {
                    ApplyBuff(player, tile.type);
                }
            }
        }
    }
}
```

## Lessons Learned

1. **Throttle expensive operations** - Scan every 10 ticks, not every frame
2. **Only process local player** - Check `whoAmI == Main.myPlayer`
3. **Skip unnecessary work** - Don't scan if all buffs already active
4. **Bounds check tile access** - Arrays can be out of bounds at world edges
5. **Use lookup tables** - Dictionary for tile-to-buff mapping
6. **Support per-buff config** - Let users enable/disable individual buffs
7. **Handle special cases** - Sugar Rush needs different duration handling

For more on Harmony patching patterns, see [Harmony Basics](../harmony-basics).
