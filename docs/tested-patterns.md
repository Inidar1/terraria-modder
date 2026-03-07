---
title: Tested Modding Patterns for Terraria 1.4.5
description: Proven code patterns for Terraria 1.4.5 modding. Harmony patches, reflection, tile scanning, inventory manipulation, and UI drawing techniques that work.
nav_order: 7
---

# Tested Patterns

These patterns are extracted from the framework's bundled mods and are proven to work with Terraria 1.4.5.

For comprehensive Harmony documentation, see [Harmony Basics](harmony-basics).

## Harmony Patching

### Basic Postfix Patch

Run code after a method executes:

```csharp
using HarmonyLib;

[HarmonyPatch(typeof(Terraria.Main), "DoUpdate")]
public static class DoUpdatePatch
{
    [HarmonyPostfix]
    public static void Postfix()
    {
        // Runs after every Main.DoUpdate() call
        MyMod.OnUpdate();
    }
}
```

### Basic Prefix Patch

Run code before a method, optionally skip the original:

```csharp
[HarmonyPatch(typeof(Terraria.Player), "Update")]
public static class PlayerUpdatePatch
{
    [HarmonyPrefix]
    public static bool Prefix(Player __instance)
    {
        // __instance is the Player being updated

        // Return true to run original method
        // Return false to skip it
        return true;
    }
}
```

### Applying Patches

**Attribute-based patches** are auto-applied by the injector, no `PatchAll()` needed:

```csharp
// Just define the patch class. The injector applies it automatically.
[HarmonyPatch(typeof(Terraria.Player), "Update")]
public static class PlayerUpdatePatch
{
    [HarmonyPostfix]
    public static void Postfix(Player __instance)
    {
        MyMod.OnUpdate(__instance);
    }
}
```

**Manual patches** use the `OnGameReady` lifecycle hook. The injector calls this when `Main.Initialize()` completes:

```csharp
private static Harmony _harmony;
private static ILogger _log;

public void Initialize(ModContext context)
{
    _log = context.Logger;
}

public static void OnGameReady()
{
    _harmony = new Harmony("com.yourname.yourmod");
    try
    {
        var method = typeof(Terraria.Main).GetMethod("DoUpdate",
            BindingFlags.NonPublic | BindingFlags.Instance);
        _harmony.Patch(method, postfix: new HarmonyMethod(typeof(Mod), "MyPostfix"));
        _log?.Info("Patches applied");
    }
    catch (Exception ex)
    {
        _log?.Error($"Patch failed: {ex.Message}");
    }
}

public void Unload()
{
    _harmony?.UnpatchAll("com.yourname.yourmod");
}
```

## Input Handling

### Detecting Key Press (Not Held)

Track previous state to detect the moment a key is pressed:

> **Note:** This pattern uses XNA's `Keys` enum directly via `Main.keyState`.
> For keybind registration, use string key names instead (see [Core API Reference](core-api-reference#keybinds)).

```csharp
private bool _wasKeyDown = false;

public void Update()
{
    bool isKeyDown = Main.keyState.IsKeyDown(Keys.F);

    if (isKeyDown && !_wasKeyDown)
    {
        // Key was just pressed this frame
        OnKeyPressed();
    }

    _wasKeyDown = isKeyDown;
}
```

### Checking Modifier Keys

```csharp
bool ctrl = Main.keyState.IsKeyDown(Keys.LeftControl) ||
            Main.keyState.IsKeyDown(Keys.RightControl);
bool shift = Main.keyState.IsKeyDown(Keys.LeftShift) ||
             Main.keyState.IsKeyDown(Keys.RightShift);
bool alt = Main.keyState.IsKeyDown(Keys.LeftAlt) ||
           Main.keyState.IsKeyDown(Keys.RightAlt);

if (ctrl && isKeyDown && !_wasKeyDown)
{
    // Ctrl+Key pressed
}
```

### Keybind Persistence

User keybinds are saved to `TerrariaModder/core/keybinds.json`:

```json
{
  "quick-keys.auto-torch": "NumPad1",
  "item-spawner.toggle": "F7"
}
```

Format: `"modId.keybindId": "KeyCombo"`

Keybinds persist automatically - no manual save needed.

### Config/Keybind Baseline Checking

Check if settings changed since startup (used by ModMenu for "Restart Required"):

```csharp
// Check if config values changed from what they were at startup
bool configChanged = _context.Config.HasChangesFromBaseline();

// Check if keybinds changed for a specific mod
bool keybindsChanged = KeybindManager.HasKeybindChangesFromBaseline(modId);
```

This allows detecting whether a restart is needed for mods that don't support hot reload.

## Player and Inventory

### Getting Local Player

```csharp
Player player = Main.player[Main.myPlayer];
```

### Searching Inventory

Search all 59 slots (indices 0-58), not just hotbar (10 slots):

```csharp
private int FindItemInInventory(Player player, int[] validTypes)
{
    for (int i = 0; i < player.inventory.Length; i++)
    {
        Item item = player.inventory[i];
        if (item != null && item.type != 0 &&
            Array.IndexOf(validTypes, item.type) >= 0)
        {
            return i;
        }
    }
    return -1;
}

// Usage: find recall potions, magic mirrors, etc.
int[] recallItems = { 50, 3124, 3199, 5437, 2350 };
int slot = FindItemInInventory(player, recallItems);
```

### Using an Item Programmatically

When `selectedItem` is read-only, swap inventory slots:

```csharp
private int _originalSlot = -1;
private int _itemSlot = -1;
private bool _needsRestore = false;

public void UseItemInSlot(Player player, int slot)
{
    _originalSlot = player.selectedItem;
    _itemSlot = slot;

    // Swap items
    Item temp = player.inventory[_originalSlot];
    player.inventory[_originalSlot] = player.inventory[slot];
    player.inventory[slot] = temp;

    // Trigger use
    player.controlUseItem = true;
    _needsRestore = true;
}

// Call every frame to restore when done
public void UpdateRestore(Player player)
{
    if (!_needsRestore) return;
    if (player.itemAnimation > 0) return; // Still animating

    // Swap back
    Item temp = player.inventory[_originalSlot];
    player.inventory[_originalSlot] = player.inventory[_itemSlot];
    player.inventory[_itemSlot] = temp;
    _needsRestore = false;
}
```

## World and Tiles

### World Position from Mouse

```csharp
// Preferred: use Main.MouseWorld (handles zoom/coordinate transforms correctly)
int tileX = (int)(Main.MouseWorld.X / 16f);
int tileY = (int)(Main.MouseWorld.Y / 16f);

// Manual approach (only correct during Draw phase):
// float worldX = Main.screenPosition.X + Main.mouseX;
// float worldY = Main.screenPosition.Y + Main.mouseY;
// Note: Main.mouseX may be world-space during Update (after SetZoom_World).
// Use Main.MouseWorld or PlayerInput.MouseX for reliable results.
```

### Checking Tile State

```csharp
Tile tile = Main.tile[tileX, tileY];

// Check if solid
bool isSolid = tile.active() && Main.tileSolid[tile.type];

// Check if empty air
bool isEmpty = !tile.active();

// Check tile type (tile.type is ushort)
if (tile.active() && tile.type == TileID.Torches)
{
    // It's a torch
}
```

### Placing Tiles

```csharp
// For items from inventory, use item.createTile and item.placeStyle
int tileType = item.createTile;   // e.g., TileID.Torches (4)
int style = item.placeStyle;      // e.g., 0=orange, 1=blue, 8=bone, etc.

bool placed = WorldGen.PlaceTile(tileX, tileY, tileType,
    mute: false,      // Play sound
    forced: false,    // Don't force placement
    plr: Main.myPlayer,
    style: style);    // Item's place style for variants

if (placed)
{
    // Consume item properly
    item.stack--;
    if (item.stack <= 0)
        item.TurnToAir(); // Remove empty slot

    // Sync in multiplayer
    if (Main.netMode == NetmodeID.MultiplayerClient)
        NetMessage.SendTileSquare(-1, tileX, tileY);
}
```

### Scanning Nearby Tiles

```csharp
private List<Point> FindNearbyFurniture(Player player, int tileType, int range)
{
    var found = new List<Point>();

    int centerX = (int)(player.Center.X / 16f);
    int centerY = (int)(player.Center.Y / 16f);

    for (int x = centerX - range; x <= centerX + range; x++)
    {
        for (int y = centerY - range; y <= centerY + range; y++)
        {
            if (x < 0 || x >= Main.maxTilesX ||
                y < 0 || y >= Main.maxTilesY)
                continue;

            Tile tile = Main.tile[x, y];
            if (tile.active() && tile.type == tileType)
            {
                found.Add(new Point(x, y));
            }
        }
    }

    return found;
}
```

## Buffs

### Applying a Buff

```csharp
// Apply buff to player
// buffType: the buff ID
// duration: in frames (60 = 1 second)
player.AddBuff(buffType, duration);
```

### Checking for Buff

```csharp
bool hasBuff = false;
for (int i = 0; i < Player.MaxBuffs; i++)
{
    if (player.buffType[i] == buffType && player.buffTime[i] > 0)
    {
        hasBuff = true;
        break;
    }
}
```

## Chat Messages

### Show Message in Chat

```csharp
// Basic white text
Main.NewText("Hello!");

// Colored text (RGB)
Main.NewText("Warning!", 255, 200, 100); // Orange

// Common colors
Main.NewText("Success", 100, 255, 100);  // Green
Main.NewText("Error", 255, 100, 100);    // Red
Main.NewText("Info", 100, 200, 255);     // Blue
```

## UI Rendering

### Using the Widget Library (Recommended)

The Widget Library provides pre-built UI components. Use `DraggablePanel` + `StackLayout` for most UIs:

```csharp
using TerrariaModder.Core.UI;
using TerrariaModder.Core.UI.Widgets;

private DraggablePanel _panel;
private bool _featureEnabled;

public void Initialize(ModContext context)
{
    _panel = new DraggablePanel("my-mod-panel", "Settings", 350, 250);
    _panel.RegisterDrawCallback(DrawPanel);
    context.RegisterKeybind("toggle", "Toggle UI", "Open settings", "F5", () => _panel.Toggle());
}

private void DrawPanel()
{
    if (!_panel.BeginDraw()) return;

    var layout = new StackLayout(_panel.ContentX, _panel.ContentY, _panel.ContentWidth, spacing: 6);
    layout.SectionHeader("Options");
    if (layout.Toggle("Feature", _featureEnabled)) _featureEnabled = !_featureEnabled;
    if (layout.Button("Do Something")) { /* action */ }

    _panel.EndDraw();
}

public void Unload()
{
    _panel.UnregisterDrawCallback();
}
```

See [Core API Reference - Widget Library](core-api-reference#widget-library) for all available widgets.

### Drawing with UIRenderer (Low-Level)

For custom drawing beyond what widgets provide:

```csharp
using TerrariaModder.Core.UI;

void OnDraw()
{
    // Filled rectangle (Color4 or RGBA)
    UIRenderer.DrawRect(x, y, width, height, UIColors.PanelBg);
    UIRenderer.DrawRect(x, y, width, height, r, g, b, alpha);

    // Text
    UIRenderer.DrawText("Hello", x, y, UIColors.Text);

    // Mouse state
    bool hovering = UIRenderer.IsMouseOver(x, y, width, height);
    bool clicked = UIRenderer.MouseLeftClick;
    int scroll = UIRenderer.ScrollWheel;
}
```

### Subscribing to Draw Events

```csharp
// Option 1: Widget Library (recommended) - register panel draw callback
_panel.RegisterDrawCallback(DrawPanel);

// Option 2: Manual event subscription (for HUD overlays, etc.)
public void Initialize(ModContext context)
{
    FrameEvents.OnUIOverlay += OnDraw;
}

public void Unload()
{
    FrameEvents.OnUIOverlay -= OnDraw;
}
```

## Debug Logging

### Throttled Logging

Avoid spamming logs every frame:

```csharp
private int _logCounter = 0;

void Update()
{
    _logCounter++;
    if (_logCounter >= 300) // Every 5 seconds at 60fps
    {
        _log.Debug($"Status: {_processedCount} items processed");
        _logCounter = 0;
    }
}
```

### Dump Inventory for Discovery

```csharp
_log.Debug("=== Inventory Dump ===");
for (int i = 0; i < player.inventory.Length; i++)
{
    Item item = player.inventory[i];
    if (item != null && item.type != 0)
    {
        _log.Debug($"[{i}] type={item.type} name={item.Name} stack={item.stack}");
    }
}
```

## Reflection Patterns

Mods reference Terraria.exe and XNA directly — use types without reflection for all public members. Only use `BindingFlags`-based reflection for **private** members. See [Core API Reference](core-api-reference) for the `Game` class helpers.

### Direct Type Access

Mods have compile-time references to Terraria and XNA:

```csharp
using Terraria;
using Terraria.ID;
using Microsoft.Xna.Framework;

// Use typeof() directly — no Type.GetType() needed
var updateMethod = typeof(Player).GetMethod("Update", new Type[] { typeof(int) });
var privateField = typeof(Main).GetField("_isAsyncLoadComplete",
    BindingFlags.NonPublic | BindingFlags.Static);

// Public fields and methods: access directly
Player local = Main.LocalPlayer;
int health = local.statLife;
local.AddBuff(BuffID.Ironskin, 3600);
Vector2 mouseWorld = Main.MouseWorld;
```

### Reflection for Private Members

Only use reflection when a member is private or internal:

```csharp
// Private instance field
var ownerField = typeof(TagEffectState).GetField("_owner",
    BindingFlags.NonPublic | BindingFlags.Instance);
int owner = (int)ownerField.GetValue(instance);

// Private static field
var loadCompleteField = typeof(Main).GetField("_isAsyncLoadComplete",
    BindingFlags.NonPublic | BindingFlags.Static);
bool ready = (bool)loadCompleteField.GetValue(null);
```

### Using the Game Class

`TerrariaModder.Core.Reflection.Game` provides convenience accessors for common state:

```csharp
using TerrariaModder.Core.Reflection;

// State checks
if (Game.InWorld && !Game.InMenu)
{
    Player player = Game.LocalPlayer;
    Vector2 mouse = Game.MouseWorld;
    bool dayTime = Game.IsDayTime;
}

// Tile access (bounds-checked)
Tile tile = Game.GetTile(tileX, tileY);
Vector2 worldPos = Game.TileToWorld(tileX, tileY);

// UI helpers
Game.BlockMouse();
Game.ShowMessage("Hello!", 255, 200, 100);
```

### Input Blocking (Widget Library vs Manual)

**Recommended:** Use `DraggablePanel` from the Widget Library. It handles all input blocking, z-order, and click-through prevention automatically:

```csharp
var panel = new DraggablePanel("my-panel", "Settings", 400, 300);
panel.RegisterDrawCallback(DrawPanel);

// That's it - input blocking, z-order, click-through prevention all handled.
```

**Manual approach** (for custom UIs without widgets):

```csharp
private bool _modalOpen = false;
private const string PanelId = "my-mod-panel";

void OnDraw()
{
    if (!_modalOpen) return;

    int x = 100, y = 100, width = 400, height = 300;

    // Register bounds every frame (handles position changes)
    UIRenderer.RegisterPanelBounds(PanelId, x, y, width, height);

    // Draw your panel...
    UIRenderer.DrawPanel(x, y, width, height, UIColors.PanelBg);

    // Check priority for multi-panel scenarios
    if (UIRenderer.ShouldBlockForHigherPriorityPanel(PanelId))
        return;

    // Handle scroll - read then consume
    int scroll = UIRenderer.ScrollWheel;
    if (scroll != 0)
    {
        UIRenderer.ConsumeScroll();  // Prevent hotbar scrolling
    }

    // Handle clicks - consume to prevent propagation
    if (UIRenderer.MouseLeftClick)
    {
        UIRenderer.ConsumeClick();
    }
}

void CloseModal()
{
    _modalOpen = false;
    UIRenderer.UnregisterPanelBounds(PanelId);
}
```

The panel registration system:
- Automatically prevents inventory click-through via `ItemSlot.Handle` patch
- Blocks HUD buttons (Quick Stack, Bestiary, Sort, Smart Stack) via `PlayerInput.IgnoreMouseInterface` patch
- Sets `Main.blockMouse` and `Player.mouseInterface`
- Tracks multiple panels with dynamic z-order (click-to-focus)
- Clears player controls when modal is open

For private-member reflection patterns, see the [SkipIntro Walkthrough](walkthroughs/skip-intro) (private static fields) or [WhipStacking Walkthrough](walkthroughs/whip-stacking) (private instance fields on internal types).
