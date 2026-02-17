---
title: Core API Reference
nav_order: 8
---

# Core API Reference

This documents the validated APIs in TerrariaModder.Core that are proven to work.

## IMod Interface

Every mod must implement `IMod`:

```csharp
public interface IMod
{
    string Id { get; }       // Must match manifest.json
    string Name { get; }
    string Version { get; }

    void Initialize(ModContext context);
    void OnWorldLoad();
    void OnWorldUnload();
    void Unload();
}
```

### IMod Lifecycle

1. `Initialize()` - Called when mod loads. Set up config, keybinds, UI, events.
2. `OnWorldLoad()` - Called when entering a world.
3. `OnWorldUnload()` - Called when leaving a world.
4. `Unload()` - Called when game closes. Clean up resources.

### Injector Lifecycle Hooks

The injector discovers **public static void** methods on any type in your mod assembly and calls them at specific points during game startup. These are separate from the IMod interface; they fire for the assembly, not per-mod-instance.

```csharp
public class Mod : IMod
{
    // IMod methods (Initialize, OnWorldLoad, etc.) ...

    /// Called after Main.Initialize() completes.
    /// GraphicsDevice, Window.Handle, Main.instance all ready.
    /// Use for manual Harmony patches that need game types resolved.
    public static void OnGameReady()
    {
        // Safe to patch, access GraphicsDevice, etc.
    }

    /// Called after Main.LoadContent() completes.
    /// NOTE: Fires BEFORE OnGameReady (XNA calls LoadContent from within Initialize).
    public static void OnContentLoaded()
    {
        // Textures and fonts loaded, but patches may not be applied yet.
    }

    /// Called on the first Main.Update() frame.
    /// Full game loop is running.
    public static void OnFirstUpdate()
    {
        // One-time setup needing the game loop active.
    }

    /// Called when game is exiting (before Terraria disposes systems).
    public static void OnShutdown()
    {
        // Save state, final cleanup.
    }
}
```

| Hook | When | Use For |
|------|------|---------|
| `OnGameReady` | After `Main.Initialize()` | Manual Harmony patches, GraphicsDevice access |
| `OnContentLoaded` | After `Main.LoadContent()` | Custom texture loading (but see timing note) |
| `OnFirstUpdate` | First `Main.Update()` | Setup requiring full game loop |
| `OnShutdown` | Game exiting | Save state, cleanup |

**Timing:** `OnContentLoaded` fires BEFORE `OnGameReady` because XNA calls `LoadContent()` from within `Initialize()`. If you need both content and patches ready, use `OnGameReady` since `LoadContent` has already completed by then.

See [Harmony Basics](harmony-basics#how-patches-are-applied) for details on when to use lifecycle hooks vs attribute patches.

## ModContext

Provided to `Initialize()`, gives access to framework services:

```csharp
public void Initialize(ModContext context)
{
    // Logging
    ILogger log = context.Logger;

    // Configuration (null if mod has no config_schema)
    IModConfig config = context.Config;

    // Mod folder path
    string path = context.ModFolder;

    // Manifest data
    ModManifest manifest = context.Manifest;

    // Register keybinds
    context.RegisterKeybind("id", "Name", "Description", "Key", callback);
}
```

### ModContext Members

| Member | Type | Description |
|--------|------|-------------|
| `Logger` | `ILogger` | Per-mod logger instance |
| `Config` | `IModConfig` | Mod configuration (null if no config_schema) |
| `ModFolder` | `string` | Path to the mod's folder |
| `Manifest` | `ModManifest` | Parsed manifest.json data |

### Custom Item Registration

```csharp
// Register a custom item
bool success = context.RegisterItem("fire-sword", new ItemDefinition
{
    DisplayName = "Flame Blade",
    Tooltip = new[] { "Shoots fireballs", "Burns enemies on contact" },
    Damage = 50,
    Melee = true,
    UseStyle = 1,
    Rarity = 5,
    Value = 50000
});

// Get all items registered by this mod
IEnumerable<string> items = context.GetItems();
```

See [Custom Assets](#custom-assets-system) for full ItemDefinition, RecipeDefinition, ShopDefinition, and DropDefinition documentation.

### Debug Command Registration

```csharp
// Register a debug command (namespaced as "modid.name")
context.RegisterCommand("status", "Show mod status", args =>
{
    CommandRegistry.Write("Status: OK");
});

// Get all commands registered by this mod
IReadOnlyList<CommandInfo> cmds = context.GetCommands();
```

Commands are executed via the Debug Console (Ctrl+`) or programmatically via `CommandRegistry.Execute()`.

### Keybind Registration

```csharp
// Register with string key
Keybind kb = context.RegisterKeybind(
    keybindId: "toggle",
    label: "Toggle Feature",
    description: "Turn feature on/off",
    defaultKey: "F5",
    callback: OnToggle
);

// Register with modifier keys using string format
Keybind kb2 = context.RegisterKeybind(
    keybindId: "action",
    label: "Action",
    description: "Do something",
    defaultKey: "Ctrl+G",  // String format with modifiers
    callback: OnAction
);

// Retrieve registered keybinds
Keybind myKeybind = context.GetKeybind("toggle");
IEnumerable<Keybind> allMyKeybinds = context.GetKeybinds();
```

## ILogger

Per-mod logger that writes to the shared log file:

```csharp
private ILogger _log;

_log.Debug("Verbose info");     // For troubleshooting
_log.Info("Normal message");    // Standard logging
_log.Warn("Potential issue");   // Warnings
_log.Error("Something failed"); // Errors
_log.Error("Failed", exception); // Error with exception details
```

**Properties:**

| Property | Type | Description |
|----------|------|-------------|
| `MinLevel` | `LogLevel` | Minimum level to output (messages below are ignored) |
| `ModId` | `string` | The mod ID this logger is for |

Log output format:
```
[2024-01-30 12:34:56] [INFO ] [my-mod] Normal message
```

All logs go to `TerrariaModder/core/logs/terrariamodder.log`.

## IModConfig

Access and manage user configuration defined in manifest.json:

```csharp
// Get typed value
bool enabled = config.Get<bool>("enabled");
int count = config.Get<int>("item_count");
string name = config.Get<string>("player_name");
float speed = config.Get<float>("move_speed");

// With default fallback
int value = config.Get<int>("count", 10);

// Try pattern (safe retrieval)
if (config.TryGet<bool>("flag", out bool flag))
{
    // flag was found and retrieved
}

// Check if key exists
if (config.HasKey("optional_setting"))
{
    // Key exists in config
}

// Modify config values
config.Set<int>("count", 25);
config.Set<bool>("enabled", false);

// Persistence
config.Save();           // Save changes to disk
config.Reload();         // Reload from disk (discards unsaved changes)
config.ResetToDefaults(); // Reset all values to manifest defaults

// Properties
bool dirty = config.HasUnsavedChanges;  // True if Set() called since last Save()
string path = config.FilePath;          // Path to config.json file
IReadOnlyDictionary<string, ConfigField> schema = config.Schema;  // Schema definition

// Events
config.OnValueChanged += (string key) =>
{
    _log.Info($"Config '{key}' was changed");
};

config.OnConfigReloaded += () =>
{
    _log.Info("Config was reloaded from disk");
};
```

### IModConfig Members

| Member | Description |
|--------|-------------|
| `Get<T>(key)` | Get config value, throws if not found |
| `Get<T>(key, default)` | Get config value with fallback |
| `TryGet<T>(key, out value)` | Try to get value, returns false if not found |
| `Set<T>(key, value)` | Set a config value |
| `HasKey(key)` | Check if key exists |
| `Save()` | Persist changes to disk |
| `Reload()` | Reload from disk |
| `ResetToDefaults()` | Reset all values to defaults |
| `HasUnsavedChanges` | True if unsaved changes exist |
| `FilePath` | Path to config file |
| `Schema` | Config field definitions |
| `OnValueChanged` | Event fired when value changes |
| `OnConfigReloaded` | Event fired on reload |

**ModConfig-only methods** (cast from IModConfig if needed):

| Member | Description |
|--------|-------------|
| `HasChangesFromBaseline()` | Check if values differ from startup (for restart detection) |
| `Dispose()` | Clean up file watcher (called automatically by framework) |

### Hot Reload Support

Implement `OnConfigChanged()` in your mod class to handle live config updates from the mod menu.

*Note: This method is discovered via reflection - it's not part of the IMod interface. Just add it as a public void method on your mod class.*

```csharp
public class Mod : IMod
{
    private bool _enabled;
    private int _maxItems;
    private ModContext _context;

    public void Initialize(ModContext context)
    {
        _context = context;
        LoadConfig();
    }

    // Called by mod menu automatically when a config value changes
    public void OnConfigChanged()
    {
        LoadConfig();
        _context.Logger.Info("Config reloaded!");
    }

    private void LoadConfig()
    {
        var config = _context.Config;
        _enabled = config.Get<bool>("enabled");
        _maxItems = config.Get<int>("maxItems");
    }
}
```

All config changes are saved to disk immediately. If you implement `OnConfigChanged()`, the mod is also notified in real-time. Without it, the mod menu shows "Game Restart Required" since changes only take effect after restart.

### Config Types

Defined in `manifest.json` under `config_schema`:

```json
{
  "config_schema": {
    "enabled": {
      "type": "bool",
      "default": true,
      "label": "Enabled"
    },
    "count": {
      "type": "int",
      "default": 10,
      "min": 1,
      "max": 100,
      "label": "Count"
    },
    "speed": {
      "type": "float",
      "default": 1.5,
      "min": 0.1,
      "max": 10.0,
      "label": "Speed"
    },
    "name": {
      "type": "string",
      "default": "Player",
      "label": "Name"
    },
    "mode": {
      "type": "enum",
      "options": ["easy", "normal", "hard"],
      "default": "normal",
      "label": "Mode"
    }
  }
}
```

## Keybinds

### Registering in manifest.json

```json
{
  "keybinds": [
    {
      "id": "toggle",
      "label": "Toggle Feature",
      "description": "Turn feature on/off",
      "default": "F5"
    }
  ]
}
```

**Note:** Use `"default"` not `"default_key"` for the default key binding.

### Registering in Code

```csharp
context.RegisterKeybind(
    id: "toggle",
    label: "Toggle Feature",
    description: "Turn feature on/off",
    defaultKey: "F5",
    callback: OnToggle
);

private void OnToggle()
{
    _enabled = !_enabled;
    _log.Info($"Feature is now {(_enabled ? "ON" : "OFF")}");
}
```

### Keybind Persistence

User keybinds are automatically saved to `TerrariaModder/core/keybinds.json` and persist across game restarts:

```json
{
  "quick-keys.auto-torch": "NumPad1",
  "item-spawner.toggle": "F7"
}
```

When a mod loads, any saved bindings are automatically restored.

### Restart Detection

```csharp
// Check if keybinds changed since startup
bool keybindsChanged = KeybindManager.HasKeybindChangesFromBaseline(modId);

// Check if config changed since startup
bool configChanged = config.HasChangesFromBaseline();
```

Used by ModMenu to show "Game Restart Required" for mods without `OnConfigChanged()`.

### KeybindManager (Static)

Central registry for all keybinds.

**Properties:**

| Property | Type | Description |
|----------|------|-------------|
| `Keybinds` | `IReadOnlyList<Keybind>` | All registered keybinds |
| `Enabled` | `bool` | Enable/disable all keybind processing |

**Methods:**

```csharp
// Get all keybinds
IReadOnlyList<Keybind> all = KeybindManager.GetAllKeybinds();

// Get keybind by full ID (modId.keybindId)
Keybind kb = KeybindManager.GetKeybind("quick-keys.auto-torch");

// Get all keybinds for a mod
IEnumerable<Keybind> modKeybinds = KeybindManager.GetKeybindsForMod("quick-keys");

// Change a keybind
KeybindManager.SetBinding("quick-keys.auto-torch", KeyCombo.Parse("NumPad1"));

// Reset to default
KeybindManager.ResetToDefault("quick-keys.auto-torch");

// Get conflicts between mods
List<KeybindConflict> conflicts = KeybindManager.GetConflicts();
```

### Keybind Class

Represents a single registered keybind.

**Properties:**

| Property | Type | Description |
|----------|------|-------------|
| `Id` | `string` | Full ID (modId.keybindId format) |
| `ModId` | `string` | Mod that owns this keybind |
| `Label` | `string` | Display label |
| `Description` | `string` | Tooltip text |
| `DefaultKey` | `KeyCombo` | Default key combination |
| `CurrentKey` | `KeyCombo` | Current key combination (settable) |
| `Callback` | `Action` | Callback invoked when pressed |
| `Enabled` | `bool` | Whether this keybind is enabled |

**Methods:**

```csharp
keybind.ResetToDefault();  // Reset to default key
```

### KeyCombo Class

Represents a key combination (key + modifiers).

**Properties:**

| Property | Type | Description |
|----------|------|-------------|
| `Key` | `int` | Key code (from KeyCode constants) |
| `Ctrl` | `bool` | Ctrl modifier |
| `Shift` | `bool` | Shift modifier |
| `Alt` | `bool` | Alt modifier |

**Methods:**

```csharp
// Parse from string
KeyCombo combo = KeyCombo.Parse("Ctrl+Shift+F5");

// Check state
bool pressed = combo.IsPressed();       // Currently held
bool just = combo.JustPressed();        // Just pressed this frame

// Other
string str = combo.ToString();          // "Ctrl+Shift+F5"
KeyCombo copy = combo.Clone();
```

### InputState (Static)

Low-level keyboard and mouse state tracking.

**Key State Methods:**

```csharp
bool down = InputState.IsKeyDown(keyCode);           // Key currently held
bool justPressed = InputState.IsKeyJustPressed(keyCode);  // Just pressed this frame
bool justReleased = InputState.IsKeyJustReleased(keyCode); // Just released this frame
```

**Modifier Methods:**

```csharp
bool ctrl = InputState.IsCtrlDown();
bool shift = InputState.IsShiftDown();
bool alt = InputState.IsAltDown();
```

**Properties:**

| Property | Type | Description |
|----------|------|-------------|
| `ScrollWheelDelta` | `int` | Scroll wheel change since last frame |

**Utility:**

```csharp
// Check if input should be blocked (chat open, menu, etc.)
if (InputState.ShouldBlockInput()) return;
```

Note: `InputState.Update()` is called automatically by the framework each frame.

### Key Names

Key strings are parsed by `KeyCombo.Parse()`. Modifier combos use `+` separator (e.g., `"Ctrl+Shift+F1"`).

Common key names:
- Letters: `A` through `Z`
- Numbers: `D0` through `D9`
- Function keys: `F1` through `F12`
- Numpad: `NumPad0` through `NumPad9`
- Numpad operators: `Multiply`, `Add`, `Subtract`, `Decimal`, `Divide`
- Special: `Space`, `Enter`, `Tab`, `Escape`
- Modifiers: `LeftControl`, `RightControl`, `LeftShift`, `RightShift`, `LeftAlt`, `RightAlt`
- Mouse: `MouseRight` or `RMB`, `MouseMiddle` or `MMB`
- Others: `OemTilde` (tilde key), `Home`, `End`, `Insert`, `Delete`, `PageUp`, `PageDown`

**Note:** `MouseLeft` cannot be used for keybinds - it's reserved for UI interaction (clicking the rebind button itself).

## FrameEvents

Subscribe to per-frame game events:

```csharp
using TerrariaModder.Core.Events;

public void Initialize(ModContext context)
{
    FrameEvents.OnPreUpdate += OnPreUpdate;
    FrameEvents.OnPostUpdate += OnPostUpdate;
    FrameEvents.OnPreDraw += OnPreDraw;
    FrameEvents.OnPostDraw += OnPostDraw;
    FrameEvents.OnUIOverlay += OnDraw;
}

public void Unload()
{
    FrameEvents.OnPreUpdate -= OnPreUpdate;
    FrameEvents.OnPostUpdate -= OnPostUpdate;
    FrameEvents.OnPreDraw -= OnPreDraw;
    FrameEvents.OnPostDraw -= OnPostDraw;
    FrameEvents.OnUIOverlay -= OnDraw;
}
```

### FrameEvents Members

| Event | Description |
|-------|-------------|
| `OnPreUpdate` | Before game updates each frame. Use for input processing. |
| `OnPostUpdate` | After game updates each frame. Use for state reactions. |
| `OnPreDraw` | Before game draws each frame. Use for preparing draw data. |
| `OnPostDraw` | After game draws each frame. Use for custom rendering. |
| `OnUIOverlay` | Just before cursor is drawn. **Use this for custom UI panels.** |

**Important:** Always unsubscribe from events in `Unload()` to prevent memory leaks.

## GameEvents

World and state events:

```csharp
GameEvents.OnWorldLoad += () => { };
GameEvents.OnWorldUnload += () => { };
GameEvents.OnDayStart += () => { };
GameEvents.OnNightStart += () => { };
GameEvents.OnWorldSave += () => { };      // Before save
GameEvents.OnReturnToMenu += () => { };   // Exiting to menu
```

## PlayerEvents

```csharp
PlayerEvents.OnPlayerSpawn += (args) => { };   // PlayerSpawnEventArgs
PlayerEvents.OnPlayerDeath += (args) => { };   // PlayerDeathEventArgs
PlayerEvents.OnPlayerHurt += (args) => { };    // PlayerHurtEventArgs (Cancellable)
PlayerEvents.OnBuffApplied += (args) => { };   // BuffEventArgs
PlayerEvents.OnPlayerUpdate += (args) => { };  // Per-frame
```

**Event Args Properties:**

| Event Args | Extends | Properties |
|------------|---------|------------|
| `PlayerSpawnEventArgs` | `PlayerEventArgs` | `PlayerIndex`, `Player`, `SpawnX`, `SpawnY` |
| `PlayerDeathEventArgs` | `PlayerEventArgs` | `PlayerIndex`, `Player`, `Damage`, `DeathReason` |
| `PlayerHurtEventArgs` | `CancellableEventArgs` | `PlayerIndex`, `Player`, `Damage`, `HitDirection`, `PvP`, `Cancelled` |
| `BuffEventArgs` | `PlayerEventArgs` | `PlayerIndex`, `Player`, `BuffType`, `Duration` |

*Note: PlayerHurtEventArgs extends CancellableEventArgs (not PlayerEventArgs) and defines its own PlayerIndex/Player properties.*

## ItemEvents

```csharp
ItemEvents.OnItemPickup += (args) => { };  // ItemPickupEventArgs (Cancellable)
ItemEvents.OnItemUse += (args) => { };     // ItemUseEventArgs (Cancellable)
ItemEvents.OnItemDrop += (args) => { };    // ItemDropEventArgs (Cancellable)
```

**Event Args Properties:**

| Event Args | Properties |
|------------|------------|
| `ItemPickupEventArgs` | `PlayerIndex`, `Player`, `ItemType`, `Item`, `Stack`, `Cancelled` |
| `ItemUseEventArgs` | `PlayerIndex`, `Player`, `ItemType`, `Item`, `Cancelled` |
| `ItemDropEventArgs` | `ItemType`, `Stack`, `X`, `Y`, `Cancelled` |

## NPCEvents

```csharp
NPCEvents.OnNPCSpawn += (args) => { };   // NPCSpawnEventArgs
NPCEvents.OnBossSpawn += (args) => { };  // BossSpawnEventArgs
NPCEvents.OnNPCDeath += (args) => { };   // NPCDeathEventArgs
NPCEvents.OnNPCHit += (args) => { };     // NPCHitEventArgs (Cancellable)
```

**Event Args Properties:**

| Event Args | Extends | Properties |
|------------|---------|------------|
| `NPCSpawnEventArgs` | `NPCEventArgs` | `NPCIndex`, `NPC`, `NPCType`, `SpawnX` (float), `SpawnY` (float) |
| `BossSpawnEventArgs` | `NPCEventArgs` | `NPCIndex`, `NPC`, `NPCType`, `BossName` |
| `NPCDeathEventArgs` | `NPCEventArgs` | `NPCIndex`, `NPC`, `NPCType`, `LastDamage`, `KillerPlayerIndex` |
| `NPCHitEventArgs` | `CancellableEventArgs` | `NPCIndex`, `NPC`, `Damage`, `Knockback`, `HitDirection`, `Crit`, `Cancelled` |

*Note: NPCHitEventArgs extends CancellableEventArgs (not NPCEventArgs) and defines its own NPCIndex/NPC properties.*

## WorldEvents

*Currently untested. These event args are defined but no events currently fire them. Reserved for future use.*

```csharp
// WorldEventArgs: WorldName, WorldId, IsNewWorld
// TimeEventArgs: Time, IsDay
```

### Event Args Base Classes

**ModEventArgs** - Base class for all mod events:

| Property | Type | Description |
|----------|------|-------------|
| `Timestamp` | `DateTime` | When the event was created (set to DateTime.Now) |

**CancellableEventArgs** - Extends ModEventArgs for cancellable events:

| Member | Type | Description |
|--------|------|-------------|
| `Cancelled` | `bool` | Set to true to cancel the event |
| `Cancel()` | `void` | Convenience method - sets Cancelled = true |

### Cancellable Events

Set `args.Cancelled = true` or call `args.Cancel()` to prevent the action:

```csharp
PlayerEvents.OnPlayerHurt += (args) => {
    if (ShouldBlockDamage(args))
        args.Cancelled = true;  // Or: args.Cancel();
};
```

## UIRenderer

Static class for drawing custom UI. Use in `FrameEvents.OnUIOverlay` handler.

### Drawing Methods

```csharp
using TerrariaModder.Core.UI;

void OnDraw()
{
    // Filled rectangle (RGBA or Color4)
    UIRenderer.DrawRect(x, y, width, height, r, g, b, alpha);
    UIRenderer.DrawRect(x, y, width, height, UIColors.PanelBg);

    // Rectangle outline
    UIRenderer.DrawRectOutline(x, y, width, height, r, g, b, alpha, thickness);
    UIRenderer.DrawRectOutline(x, y, width, height, UIColors.Border, thickness);

    // Panel (background + border)
    UIRenderer.DrawPanel(x, y, width, height, bgR, bgG, bgB, bgA);
    UIRenderer.DrawPanel(x, y, width, height, UIColors.PanelBg);

    // Text (with border for readability)
    UIRenderer.DrawText("text", x, y, r, g, b, alpha);
    UIRenderer.DrawText("text", x, y, UIColors.Text);

    // Text with shadow (same as DrawText, uses Utils.DrawBorderString)
    UIRenderer.DrawTextShadow("text", x, y, r, g, b, alpha);
    UIRenderer.DrawTextShadow("text", x, y, UIColors.Text);

    // Text at smaller scale (0.75x)
    UIRenderer.DrawTextSmall("small text", x, y, r, g, b, alpha);
    UIRenderer.DrawTextSmall("small text", x, y, UIColors.TextDim);

    // Text with custom scale
    UIRenderer.DrawTextScaled("scaled", x, y, r, g, b, alpha, 1.5f);

    // Draw item icon (for item spawners, inventories, etc.)
    UIRenderer.DrawItem(itemType, x, y, width, height, alpha);

    // Load and draw custom PNG textures (cached, aspect-ratio preserved)
    object tex = UIRenderer.LoadTexture("path/to/image.png");
    UIRenderer.DrawTexture(tex, x, y, width, height, alpha);
}
```

Most drawing methods support both `(r, g, b, a)` byte parameters and `Color4` struct overloads. The `Color4` overloads work with the `UIColors` palette for consistent theming. Note: `DrawTextScaled` only has the RGBA overload; use `DrawText` with `Color4` for themed text.

### Text Measurement

```csharp
// Measure text width using the game font (returns pixel width)
int width = UIRenderer.MeasureText("Hello world");

// For truncation and layout, prefer TextUtil from the Widget Library:
int width2 = TextUtil.MeasureWidth("Hello world");
string truncated = TextUtil.Truncate("Very long text", 200);
```

### Scissor Clipping (for scrollable regions)

```csharp
// Clip drawing to a rectangular region
UIRenderer.BeginClip(x, y, width, height);
// ... draw content that may extend outside bounds ...
UIRenderer.EndClip();

// Check if currently clipping
bool clipping = UIRenderer.IsClipping;
```

### Mouse Input

```csharp
// Mouse position
int mx = UIRenderer.MouseX;
int my = UIRenderer.MouseY;

// Mouse buttons
bool leftDown = UIRenderer.MouseLeft;        // Left button held
bool leftClick = UIRenderer.MouseLeftClick;  // Left button just clicked (one frame)
bool rightDown = UIRenderer.MouseRight;      // Right button held
bool rightClick = UIRenderer.MouseRightClick; // Right button just clicked

// Scroll wheel
int scroll = UIRenderer.ScrollWheel;         // Scroll delta

// Hit testing
bool hovering = UIRenderer.IsMouseOver(x, y, width, height);
```

### Screen Dimensions

```csharp
int screenW = UIRenderer.ScreenWidth;
int screenH = UIRenderer.ScreenHeight;
```

### Input Blocking

```csharp
// Block mouse input from reaching the game (for custom UI)
UIRenderer.BlockInput(true);

// Clear click state (prevent click from propagating)
UIRenderer.ConsumeClick();

// Consume scroll wheel (prevent hotbar scrolling)
UIRenderer.ConsumeScroll();

// Block keyboard when waiting for key input (e.g., keybind rebinding)
// This prevents pressed keys from activating hotbar slots
UIRenderer.IsWaitingForKeyInput = true;
```

**Input Blocking Details:**
- `BlockInput(true)` sets `Main.blockMouse`, `Player.mouseInterface`, `PlayerInput.WritingText`, and clears player control flags
- `ConsumeScroll()` clears both `ScrollWheelDelta` (hotbar) and `ScrollWheelDeltaForUI` (UI)
- `IsWaitingForKeyInput` is specifically for scenarios like keybind rebinding where you need to capture a key press without it activating game actions. Set this immediately when entering key capture mode.

### Panel Registration

There are two approaches to panel management:

**Recommended: Use the Widget Library's `DraggablePanel`** (see [Widget Library](#widget-library)). It handles panel bounds, z-order, input blocking, and draw registration automatically. This is the preferred approach for new mods.

**Manual approach:** Register panel bounds directly for custom UI without the widget library:

```csharp
// Register panel bounds (call every frame while UI is visible)
UIRenderer.RegisterPanelBounds("my-panel-id", x, y, width, height);

// When UI closes
UIRenderer.UnregisterPanelBounds("my-panel-id");

// Check if mouse is over any registered panel
bool overPanel = UIRenderer.IsMouseOverAnyPanel();
bool overMyPanel = UIRenderer.IsMouseOverPanel("my-panel-id");

// Check if blocking is active (any panels registered)
bool blocking = UIRenderer.IsBlocking;
```

**Panel draw registration** - Register a draw callback for z-order management:

```csharp
// Register (panel draws in correct z-order, click-to-focus works)
UIRenderer.RegisterPanelDraw("my-panel-id", MyDrawCallback);

// Bring to front (drawn last, gets clicks first)
UIRenderer.BringToFront("my-panel-id");

// Unregister when closing
UIRenderer.UnregisterPanelDraw("my-panel-id");
```

**Panel z-order** - When multiple panels are open, use z-order checking:

```csharp
// Returns true if a higher-z-order panel should handle input instead
if (UIRenderer.ShouldBlockForHigherPriorityPanel("my-panel-id")) return;
```

Panels use dynamic z-order with click-to-focus. Clicking on a panel brings it to the front. The most recently focused panel has the highest priority. ModMenu always stays on top when open.

**Click-through prevention:**
When panels are registered and the mouse is over them, the framework automatically prevents clicks from passing through to:
- **Inventory slots** - via `ItemSlot.Handle` patch
- **HUD buttons** (Quick Stack, Bestiary, Sort, Smart Stack) - via `PlayerInput.IgnoreMouseInterface` patch

**Best practice for modal UIs:**
1. Use `DraggablePanel` from the Widget Library (handles everything automatically)
2. Or manually call `RegisterPanelBounds()` every frame while visible
3. Call `UnregisterPanelBounds()` when closing
4. Set `IsWaitingForKeyInput = true` only when actively capturing keyboard (search fields, keybind rebinding)

### Text Input (Advanced)

**Recommended:** Use the `TextInput` widget from the [Widget Library](#widget-library) instead of these low-level methods.

For mods that need direct keyboard text input (like search boxes):

```csharp
// Enable text input mode (call in both Update and Draw)
UIRenderer.EnableTextInput();

// Handle IME input
UIRenderer.HandleIME();

// Clear input state when starting
UIRenderer.ClearInput();

// Get input text (call in Draw phase)
string newText = UIRenderer.GetInputText(currentText);

// Check if Escape was pressed
if (UIRenderer.CheckInputEscape())
{
    // User pressed Escape, close text input
}

// Disable when done
UIRenderer.DisableTextInput();

// Block keyboard from reaching the game while typing
UIRenderer.RegisterKeyInputBlock("my-search-bar");
// ... when done:
UIRenderer.UnregisterKeyInputBlock("my-search-bar");
```

### Inventory Control

```csharp
UIRenderer.OpenInventory();    // Open player inventory
UIRenderer.CloseInventory();   // Close player inventory
bool open = UIRenderer.IsInventoryOpen;  // Check if inventory is open
```

### UIRenderer Members

| Member | Description |
|--------|-------------|
| `DrawRect()` | Draw filled rectangle |
| `DrawRectOutline()` | Draw rectangle outline |
| `DrawPanel()` | Draw panel with background and border |
| `DrawText()` | Draw text with border |
| `DrawTextShadow()` | Draw text (same as DrawText) |
| `DrawTextSmall()` | Draw smaller text (0.75 scale) |
| `DrawTextScaled()` | Draw text with custom scale |
| `DrawItem()` | Draw item icon |
| `LoadTexture()` | Load a PNG file as a texture (cached) |
| `DrawTexture()` | Draw a loaded texture (aspect-ratio preserved) |
| `BeginClip()`, `EndClip()` | Scissor clipping for scroll regions |
| `IsClipping` | True if clipping is active |
| `MouseX`, `MouseY` | Mouse position |
| `MouseLeft`, `MouseRight` | Mouse button held state |
| `MouseMiddle` | Middle mouse button held state |
| `MouseLeftClick`, `MouseRightClick` | Mouse button just clicked |
| `MouseMiddleClick` | Middle mouse button just clicked |
| `ScreenWidth`, `ScreenHeight` | Screen dimensions |
| `ScrollWheel` | Scroll wheel delta |
| `IsMouseOver()` | Hit test rectangle |
| `BlockInput()` | Block mouse/keyboard from game |
| `BlockMouseOnly()` | Block only mouse input |
| `BlockKeyboardInput()` | Block only keyboard input |
| `ConsumeClick()` | Clear left click state |
| `ConsumeRightClick()` | Clear right click state |
| `ConsumeMiddleClick()` | Clear middle click state |
| `ConsumeScroll()` | Clear scroll wheel state |
| `IsBlocking` | True if input blocking is active |
| `IsWaitingForKeyInput` | Set true when capturing key input |
| `RegisterPanelBounds()` | Register UI panel for click-through prevention |
| `UnregisterPanelBounds()` | Unregister UI panel |
| `ClearAllPanelBounds()` | Clear all registered panels |
| `IsMouseOverAnyPanel()` | Check if mouse over any registered panel |
| `IsMouseOverPanel()` | Check if mouse over specific panel |
| `ShouldBlockForHigherPriorityPanel()` | Check panel priority |
| `RegisterPanelDraw()` | Register draw callback for z-order management |
| `UnregisterPanelDraw()` | Unregister draw callback |
| `BringToFront()` | Bring panel to top of z-order |
| `DrawAllPanels()` | Draw all registered panels (called internally) |
| `OpenInventory()`, `CloseInventory()` | Inventory control |
| `IsInventoryOpen` | Check if inventory is open |
| `RegisterKeyInputBlock()` | Block keyboard input to world (for text fields) |
| `UnregisterKeyInputBlock()` | Unblock keyboard input |
| `SetMouseBlocking()` | Set mouse blocking state directly |
| `MeasureText()` | Measure text width in pixels (uses game font) |
| `EnableTextInput()`, `DisableTextInput()` | Text input mode |
| `HandleIME()` | Handle IME input |
| `ClearInput()` | Clear input state |
| `GetInputText()` | Get keyboard input |
| `CheckInputEscape()` | Check Escape pressed |

## Widget Library

The Widget Library (`TerrariaModder.Core.UI.Widgets`) provides a complete set of reusable UI widgets for building mod interfaces. Widgets handle input blocking, z-ordering, and focus management automatically.

```csharp
using TerrariaModder.Core.UI.Widgets;
```

### DraggablePanel

Top-level window container with title bar, close button, dragging, and automatic z-order management.

```csharp
// Create a panel
var panel = new DraggablePanel("my-panel", "My UI", 400, 300);

// Configuration
panel.HeaderHeight = 35;     // Title bar height (default 35)
panel.Padding = 8;           // Content padding (default 8)
panel.ShowCloseButton = true;
panel.Draggable = true;
panel.CloseOnEscape = true;
panel.ClipContent = true;    // Clip content to panel bounds
panel.OnClose = () => { };   // Optional close callback
panel.IconTexture = UIRenderer.LoadTexture("path/to/icon.png"); // Optional: override icon

// Icon auto-resolution: if panel ID matches a mod ID, the mod's icon.png
// is used automatically. Falls back to the default framework icon.

// Open/close
panel.Open();                // Open and auto-center
panel.Open(100, 200);        // Open at specific position
panel.Close();
panel.Toggle();

// Register draw callback (called automatically by z-order system)
panel.RegisterDrawCallback(DrawMyPanel);
panel.UnregisterDrawCallback();

// In your draw callback:
void DrawMyPanel()
{
    if (!panel.BeginDraw()) return;  // Returns false if panel is closed

    // Draw content using panel.ContentX, ContentY, ContentWidth, ContentHeight
    // Input blocking is handled automatically

    panel.EndDraw();
}
```

**Properties:**

| Property | Type | Description |
|----------|------|-------------|
| `PanelId` | `string` | Unique panel identifier |
| `Title` | `string` | Title bar text |
| `Width`, `Height` | `int` | Panel dimensions |
| `X`, `Y` | `int` | Panel position |
| `IsOpen` | `bool` | Whether panel is visible |
| `BlockInput` | `bool` | Whether a higher-z panel is blocking this one |
| `ContentX`, `ContentY` | `int` | Top-left of content area |
| `ContentWidth`, `ContentHeight` | `int` | Available content dimensions |
| `Padding` | `int` | Content padding from panel edge (default: 8) |

### StackLayout

Vertical layout helper that eliminates manual Y-coordinate math. Stack-allocated struct with zero GC pressure. Default spacing is 4 pixels.

```csharp
var layout = new StackLayout(panel.ContentX, panel.ContentY, panel.ContentWidth, spacing: 8);

// Integrated widget helpers (draw + auto-advance):
if (layout.Button("Click Me")) { /* handle click */ }
layout.SectionHeader("Settings");
if (layout.Toggle("Feature", isEnabled)) { isEnabled = !isEnabled; }
if (layout.Checkbox("Option", isChecked)) { isChecked = !isChecked; }
layout.Label("Some text");
layout.Divider();

// Manual advance for custom widgets:
int y = layout.Advance(30);  // Reserve 30px, returns the Y to draw at
layout.Space(16);             // Add blank space

// Position-override variants (draw at specific X, custom width):
if (layout.ButtonAt(x + 100, 120, "OK")) { /* handle */ }
if (layout.ToggleAt(x + 100, 120, "Mode", active)) { active = !active; }
```

**Properties:**

| Property | Type | Description |
|----------|------|-------------|
| `X` | `int` | Left edge of layout area |
| `Y` | `int` | Starting Y position |
| `Width` | `int` | Available width |
| `CurrentY` | `int` | Where the next widget draws |
| `TotalHeight` | `int` | Total height consumed |

**Methods:**

| Method | Returns | Description |
|--------|---------|-------------|
| `Button(label)` | `bool` | Full-width button, returns true on click |
| `ButtonAt(x, width, label)` | `bool` | Button at specific X and width |
| `Toggle(label, active)` | `bool` | Full-width toggle, returns true on click |
| `ToggleAt(x, width, label, active)` | `bool` | Toggle at specific X and width |
| `Checkbox(label, checked)` | `bool` | Checkbox with label, returns true on click |
| `SectionHeader(text)` | `void` | Divider line with label |
| `Label(text)` | `void` | Text label (default color) |
| `Label(text, color, height)` | `void` | Text label with Color4 and custom height |
| `Divider()` | `void` | Horizontal divider line |
| `Advance(height)` | `int` | Reserve space, returns Y to draw at |
| `Space(height)` | `void` | Add blank vertical space |

### Button

Clickable button with hover state.

```csharp
// Basic button
if (Button.Draw(x, y, width, height, "Click Me"))
{
    // Handle click
}

// Custom colors
if (Button.Draw(x, y, width, height, "Custom", normalBg, hoverBg, textColor))
{
    // Handle click
}
```

### Toggle

ON/OFF toggle button. Caller manages state.

```csharp
if (Toggle.Draw(x, y, width, height, "Feature Name", isActive))
{
    isActive = !isActive;  // Flip state on click
}
```

### Checkbox

Checkbox with optional label and partial state support.

```csharp
// Checkbox only
if (Checkbox.Draw(x, y, size, isChecked))
{
    isChecked = !isChecked;
}

// Checkbox with label (full-row click area)
if (Checkbox.DrawWithLabel(x, y, width, height, "Enable feature", isChecked))
{
    isChecked = !isChecked;
}

// Partial state (yellow indicator)
Checkbox.Draw(x, y, size, isChecked, partial: true);
```

### SectionHeader

Divider line with a label for grouping content.

```csharp
int height = SectionHeader.Draw(x, y, width, "Section Title");
```

### TextInput

Single-line text field with IME support. Create once and reuse.

```csharp
var textInput = new TextInput(placeholder: "Search...", maxLength: 100);

// In Update phase (required for IME):
textInput.Update();

// In Draw phase:
string value = textInput.Draw(x, y, width, height: 28);
if (textInput.HasChanged) { /* use value */ }

// Properties
textInput.KeyBlockId     // Keyboard input block ID (for RegisterKeyInputBlock)

// Control
textInput.Focus();
textInput.Unfocus();
textInput.Clear();
```

**Properties:**

| Property | Type | Description |
|----------|------|-------------|
| `Text` | `string` | Current text value |
| `IsFocused` | `bool` | Whether the input has focus |
| `HasChanged` | `bool` | Whether text changed this frame |

### Slider

Horizontal value slider with drag and click-to-seek.

```csharp
var slider = new Slider();

// Integer slider
int newValue = slider.Draw(x, y, width, height, currentValue, min: 0, max: 100);

// Float slider
float newFloat = slider.Draw(x, y, width, height, currentFloat, min: 0f, max: 1f);
```

### ScrollView

Virtual scrolling container. Uses culling instead of GPU clipping to avoid nesting limitations.

```csharp
var scroll = new ScrollView();

scroll.Begin(x, y, width, height, totalContentHeight);

for (int i = 0; i < items.Count; i++)
{
    int itemY = i * 30;
    if (!scroll.IsVisible(itemY, 30)) continue;  // Cull off-screen items

    int drawY = scroll.ContentY + itemY;
    // Draw item at drawY...
}

scroll.End();  // Draws scrollbar and handles scroll input
```

**Properties:**

| Property | Type | Description |
|----------|------|-------------|
| `ScrollOffset` | `int` | Current scroll position |
| `ContentHeight` | `int` | Total content height |
| `ViewHeight` | `int` | Visible area height |
| `ContentX`, `ContentY` | `int` | Content area origin |
| `ContentWidth` | `int` | Content area width |
| `ViewTop`, `ViewBottom` | `int` | Visible range in content space |
| `MaxScroll` | `int` | Maximum scroll offset |
| `NeedsScrolling` | `bool` | True if content exceeds view height |

**Methods:** `ResetScroll()`, `ScrollToY(y)`

### TabBar

Tab strip for switching between views.

```csharp
string[] tabs = { "General", "Advanced", "About" };
int activeTab = TabBar.Draw(x, y, width, tabs, activeTab, height: 28);
```

### Modal

Centered overlay dialog with dimming background. Blocks all background interaction.

```csharp
var modal = new Modal(width: 480);

// Open from a button click, etc.
if (someCondition) modal.Open();

// Draw (anywhere in your draw loop)
if (modal.BeginDraw("Confirm Action", contentHeight: 100))
{
    UIRenderer.DrawText("Are you sure?", modal.ContentX, modal.ContentY, UIColors.Text);

    if (Button.Draw(modal.ContentX, modal.ContentY + 40, 80, 26, "Yes"))
    {
        // Handle confirm
        modal.Close();
    }
    if (Button.Draw(modal.ContentX + 90, modal.ContentY + 40, 80, 26, "No"))
    {
        modal.Close();
    }

    modal.EndDraw();
}
```

### Tooltip

Deferred tooltip rendering. Last-writer-wins per frame.

```csharp
// Set tooltip when hovering over something
if (WidgetInput.IsMouseOver(x, y, width, height))
{
    Tooltip.Set("Simple tooltip text");
    // Or with title:
    Tooltip.Set("Item Name", "Detailed description here");
}

// Draw at end of panel (called automatically by DraggablePanel.EndDraw)
Tooltip.DrawDeferred();
```

### TextUtil

Text measurement and truncation utilities.

```csharp
int pixelWidth = TextUtil.MeasureWidth("Hello world");
string truncated = TextUtil.Truncate("Very long text here", maxPixelWidth: 200);
string tail = TextUtil.VisibleTail("Long input text", maxPixelWidth: 150);
```

### WidgetInput

Unified input access with z-order blocking. Used internally by all widgets.

```csharp
// Mouse state (respects z-order blocking)
int mx = WidgetInput.MouseX;
int my = WidgetInput.MouseY;
bool clicked = WidgetInput.MouseLeftClick;
bool hover = WidgetInput.IsMouseOver(x, y, width, height);

// Consume input to prevent propagation
WidgetInput.ConsumeClick();
WidgetInput.ConsumeRightClick();
WidgetInput.ConsumeScroll();

// Modifier keys
bool shift = WidgetInput.IsShiftHeld;
bool ctrl = WidgetInput.IsCtrlHeld;
bool alt = WidgetInput.IsAltHeld;
```

### UIColors and Color4

Centralized color palette with colorblind theme support.

```csharp
using TerrariaModder.Core.UI;

// Use semantic colors (all Color4 structs)
UIRenderer.DrawRect(x, y, w, h, UIColors.PanelBg);
UIRenderer.DrawText("Hello", x, y, UIColors.Text);

// Color4 struct
Color4 color = new Color4(255, 100, 100, 230);
Color4 transparent = color.WithAlpha(128);
```

**Color Categories:**

| Category | Colors |
|----------|--------|
| **Panel** | `PanelBg`, `HeaderBg`, `SectionBg`, `TooltipBg` |
| **Items** | `ItemBg`, `ItemHoverBg`, `ItemActiveBg` |
| **Text** | `Text`, `TextDim`, `TextHint`, `TextTitle` |
| **Status** | `Success`, `Error`, `Warning`, `Info` |
| **Interactive** | `Button`, `ButtonHover`, `Accent`, `AccentText` |
| **Chrome** | `Border`, `Divider`, `InputBg`, `InputFocusBg` |
| **Scroll** | `ScrollTrack`, `ScrollThumb` |
| **Slider** | `SliderTrack`, `SliderThumb`, `SliderThumbHover` |
| **Console** | `ConsoleCommand`, `ConsolePrompt` |
| **Close** | `CloseBtn`, `CloseBtnHover` |

**Utility Methods:**

```csharp
// Get color for item rarity (-1 to 11+)
Color4 rarityColor = UIColors.GetRarityColor(item.Rarity);
```

**Themes:** `"normal"`, `"red-green"`, `"blue-yellow"`, `"high-contrast"`

```csharp
UIColors.SetTheme("high-contrast");
string current = UIColors.CurrentTheme;
string[] available = UIColors.ThemeNames;
```

### Complete Example: Panel with Widgets

```csharp
public class Mod : IMod
{
    private DraggablePanel _panel;
    private ScrollView _scroll = new ScrollView();
    private Slider _slider = new Slider();
    private bool _godMode;
    private int _speed = 1;
    private List<string> _items = new List<string> { "Sword", "Shield", "Potion", "Arrow", "Torch" };

    public void Initialize(ModContext context)
    {
        _panel = new DraggablePanel("my-ui", "My Settings", 350, 400);
        _panel.RegisterDrawCallback(DrawUI);

        context.RegisterKeybind("toggle", "Toggle UI", "Open settings", "F7", () => _panel.Toggle());
    }

    private void DrawUI()
    {
        if (!_panel.BeginDraw()) return;

        var layout = new StackLayout(_panel.ContentX, _panel.ContentY, _panel.ContentWidth, spacing: 6);

        layout.SectionHeader("Gameplay");
        if (layout.Toggle("God Mode", _godMode)) _godMode = !_godMode;

        layout.SectionHeader("Speed");
        int y = layout.Advance(20);
        _speed = _slider.Draw(layout.X, y, layout.Width, 20, _speed, 1, 10);

        layout.SectionHeader("Items");
        // Scrollable list
        int listHeight = _panel.ContentHeight - layout.TotalHeight - 8;
        _scroll.Begin(layout.X, layout.CurrentY, layout.Width, listHeight, _items.Count * 24);
        for (int i = 0; i < _items.Count; i++)
        {
            int itemY = i * 24;
            if (!_scroll.IsVisible(itemY, 24)) continue;
            UIRenderer.DrawText(_items[i], layout.X, _scroll.ContentY + itemY, UIColors.Text);
        }
        _scroll.End();

        _panel.EndDraw();
    }

    public void Unload()
    {
        _panel.UnregisterDrawCallback();
    }
}
```

## Game Class

Convenience accessors for common game state. Uses direct Terraria references where possible.

```csharp
using TerrariaModder.Core.Reflection;
```

### Game State

```csharp
bool inMenu = Game.InMenu;           // True if in main menu
bool paused = Game.IsPaused;         // True if game is paused
bool loading = Game.IsLoading;       // True if loading
bool inWorld = Game.InWorld;         // True if in a world (not menu)

bool multiplayer = Game.IsMultiplayer;  // True if multiplayer
bool server = Game.IsServer;            // True if this is the server
bool client = Game.IsClient;            // True if this is a client
bool singleplayer = Game.IsSingleplayer; // True if singleplayer
```

### Screen and Mouse

```csharp
int screenW = Game.ScreenWidth;
int screenH = Game.ScreenHeight;
float uiScale = Game.UIScale;

int mouseX = Game.MouseX;
int mouseY = Game.MouseY;
Vec2 mouseScreen = Game.MouseScreen;  // Mouse in screen coordinates
Vec2 mouseWorld = Game.MouseWorld;    // Mouse in world coordinates
bool mouseL = Game.MouseLeft;
bool mouseR = Game.MouseRight;

Vec2 screenPos = Game.ScreenPosition;  // Top-left corner in world coords
```

### World

```csharp
int maxX = Game.MaxTilesX;        // World width in tiles
int maxY = Game.MaxTilesY;        // World height in tiles
int surface = Game.WorldSurface;  // Surface level Y
int rock = Game.RockLayer;        // Rock layer Y

double time = Game.Time;          // Current time of day
bool day = Game.IsDayTime;        // True if daytime
bool blood = Game.BloodMoon;      // True if blood moon
bool eclipse = Game.Eclipse;      // True if solar eclipse
bool rain = Game.Raining;         // True if raining
```

### Local Player

```csharp
int myIndex = Game.MyPlayerIndex;
Player player = Game.LocalPlayer;
Vec2 pos = Game.PlayerPosition;
Vec2 center = Game.PlayerCenter;

int health = Game.PlayerHealth;
int maxHealth = Game.PlayerMaxHealth;
int mana = Game.PlayerMana;
int maxMana = Game.PlayerMaxMana;
bool dead = Game.PlayerDead;

int slot = Game.SelectedItem;
Item held = Game.HeldItem;
```

### Input Helpers

```csharp
Game.BlockMouse();              // Block mouse input
bool chatOpen = Game.ChatOpen;  // True if chat is open
bool invOpen = Game.InventoryOpen;  // True if inventory open
bool uiBlocking = Game.UIBlocking;  // True if any UI blocking input
```

### Arrays

```csharp
Player[] players = Game.Players;
NPC[] npcs = Game.NPCs;
Projectile[] projectiles = Game.Projectiles;
```

### Utility Methods

```csharp
// Coordinate conversion
Vec2 worldPos = Game.TileToWorld(tileX, tileY);
(int x, int y) = Game.WorldToTile(worldPos);

// Get tile at position
Tile tile = Game.GetTile(tileX, tileY);

// Check tile solidity
bool solid = Game.IsTileSolid(tileType);
```

### Actions

```csharp
// Show chat message
Game.ShowMessage("Hello!", r, g, b);

// Place a tile
bool success = Game.PlaceTile(tileX, tileY, tileType, style);
```

## Type Wrappers

Lightweight wrappers for XNA types (no XNA dependency required):

### Vec2

Minimal wrapper for Vector2 to avoid XNA compile-time dependency.

```csharp
using TerrariaModder.Core.Reflection;

// Construction
Vec2 pos = new Vec2(100f, 200f);
Vec2 zero = Vec2.Zero;

// Properties
float x = pos.X;
float y = pos.Y;

// Convert from XNA (for reflection results)
Vec2 fromXna = Vec2.FromXna(xnaVector);

// Comparison
bool equal = (a == b);
bool notEqual = (a != b);

// String representation
string str = pos.ToString();  // "(100.00, 200.00)"
```

**Constructor:**
- `Vec2(float x, float y)` - Create with coordinates

**Fields:**
- `float X` - X coordinate
- `float Y` - Y coordinate

**Static:**
- `Vec2.Zero` - (0, 0)
- `Vec2 FromXna(object)` - Convert from XNA Vector2

**Methods:**
- `string ToString()` - Returns `"(X, Y)"` format with 2 decimal places
- `bool Equals(object)` - Value equality comparison
- `int GetHashCode()` - Hash code based on X and Y

**Operators:** `==`, `!=`

*Note: For math operations (distance, length, etc.), use XNA's Vector2 directly via reflection or calculate manually.*

## TypeFinder

Finds types across loaded assemblies with caching.

```csharp
using TerrariaModder.Core.Reflection;

// Find by name
Type type = TypeFinder.Find("Terraria.Main");       // Returns null if not found
Type type = TypeFinder.FindRequired("Terraria.Main"); // Throws if not found
bool found = TypeFinder.TryFind("Terraria.Main", out Type type);
TypeFinder.ClearCache();  // Clear type cache
```

**Common Type Properties:**

| Property | Type Name |
|----------|-----------|
| `TypeFinder.Main` | Terraria.Main |
| `TypeFinder.Player` | Terraria.Player |
| `TypeFinder.NPC` | Terraria.NPC |
| `TypeFinder.Item` | Terraria.Item |
| `TypeFinder.Projectile` | Terraria.Projectile |
| `TypeFinder.Tile` | Terraria.Tile |
| `TypeFinder.WorldGen` | Terraria.WorldGen |
| `TypeFinder.NetMessage` | Terraria.NetMessage |
| `TypeFinder.Lang` | Terraria.Lang |
| `TypeFinder.PlayerInput` | Terraria.GameInput.PlayerInput |
| `TypeFinder.Vector2` | Microsoft.Xna.Framework.Vector2 |
| `TypeFinder.Color` | Microsoft.Xna.Framework.Color |
| `TypeFinder.Rectangle` | Microsoft.Xna.Framework.Rectangle |
| `TypeFinder.Keyboard` | Microsoft.Xna.Framework.Input.Keyboard |
| `TypeFinder.Keys` | Microsoft.Xna.Framework.Input.Keys |
| `TypeFinder.Mouse` | Microsoft.Xna.Framework.Input.Mouse |
| `TypeFinder.SpriteBatch` | Microsoft.Xna.Framework.Graphics.SpriteBatch |
| `TypeFinder.Texture2D` | Microsoft.Xna.Framework.Graphics.Texture2D |
| `TypeFinder.SpriteFont` | Microsoft.Xna.Framework.Graphics.SpriteFont |

## GameAccessor

Safe, cached API for accessing Terraria internals via reflection.

```csharp
using TerrariaModder.Core.Reflection;

// Field access
int myPlayer = GameAccessor.GetMainField<int>("myPlayer");
GameAccessor.SetMainField<int>("someField", value);

T GetStaticField<T>(Type type, string fieldName)
void SetStaticField<T>(Type type, string fieldName, T value)
T GetField<T>(object instance, string fieldName)
void SetField<T>(object instance, string fieldName, T value)

// Property access
Vec2 mouseWorld = GameAccessor.GetMainProperty<Vec2>("MouseWorld");
T GetStaticProperty<T>(Type type, string propertyName)
T GetProperty<T>(object instance, string propertyName)
void SetProperty<T>(object instance, string propertyName, T value)

// Method invocation
T InvokeMainMethod<T>(string methodName, params object[] args)
T InvokeStaticMethod<T>(Type type, string methodName, params object[] args)
T InvokeMethod<T>(object instance, string methodName, params object[] args)
void InvokeMethod(object instance, string methodName, params object[] args)  // void overload

// Array access (for 2D arrays like Main.tile)
T GetArrayElement<T>(object array, params int[] indices)
void SetArrayElement<T>(object array, T value, params int[] indices)
```

**Safe Variants (return default instead of throwing):**

```csharp
T TryGetField<T>(object instance, string fieldName, T defaultValue = default)
T TryGetStaticField<T>(Type type, string fieldName, T defaultValue = default)
T TryGetMainField<T>(string fieldName, T defaultValue = default)
T TryGetProperty<T>(object instance, string propertyName, T defaultValue = default)
T TryGetStaticProperty<T>(Type type, string propertyName, T defaultValue = default)
```

## ConfigField

Metadata for a configuration field (from manifest config_schema).

| Property | Type | Description |
|----------|------|-------------|
| `Key` | `string` | Field key/name |
| `Type` | `ConfigFieldType` | Field type (Bool, Int, Float, String, Key, Enum) |
| `Default` | `object` | Default value |
| `Label` | `string` | Display label for UI |
| `Description` | `string` | Tooltip text |
| `Min` | `double?` | Minimum (numeric) |
| `Max` | `double?` | Maximum (numeric) |
| `Step` | `double?` | Step increment (numeric) |
| `MaxLength` | `int?` | Max length (string) |
| `Pattern` | `string` | Regex pattern for validation (string) |
| `Options` | `List<string>` | Valid options (enum) |

**Methods:**
- `bool Validate(object value, out string error)` - Validate against constraints
- `object Clamp(object value)` - Clamp to valid range

## CommandRegistry

Central registry for debug commands. Thread-safe. Core commands have no namespace prefix; mod commands are prefixed with `modid.`.

```csharp
using TerrariaModder.Core.Debug;
```

### Registering Commands

Mods register commands via `ModContext`:

```csharp
// In your mod's Initialize():
context.RegisterCommand("status", "Show mod status", args =>
{
    CommandRegistry.Write("Status: OK");
});
// Command is registered as "my-mod.status"
```

### Command Output

```csharp
// Write output from a command (fires OnOutput event and logs)
CommandRegistry.Write("Hello from my command");
```

### Executing Commands

```csharp
bool found = CommandRegistry.Execute("help");           // Execute by name
bool found = CommandRegistry.Execute("my-mod.status");  // Execute mod command
```

### Querying Commands

```csharp
IReadOnlyList<CommandInfo> all = CommandRegistry.GetCommands();
IReadOnlyList<CommandInfo> modCmds = CommandRegistry.GetCommandsForMod("my-mod");
IReadOnlyList<CommandInfo> coreCmds = CommandRegistry.GetCoreCommands();
string desc = CommandRegistry.GetHelp("help");
bool exists = CommandRegistry.HasCommand("help");
```

### Built-in Core Commands

| Command | Description |
|---------|-------------|
| `help` | List all registered commands |
| `mods` | List loaded mods with status |
| `config` | Show config values for a mod |
| `clear` | Clear console output |

### CommandInfo Properties

| Property | Type | Description |
|----------|------|-------------|
| `Name` | `string` | Full command name (e.g., "help" or "my-mod.status") |
| `Description` | `string` | Human-readable description |
| `ModId` | `string` | Mod that registered this command (null for core) |

### Events

```csharp
// Subscribe to command output (used by DebugTools mod)
CommandRegistry.OnOutput += (message) => { };

// Subscribe to clear events
CommandRegistry.OnClearOutput += () => { };

// Programmatically clear output (fires OnClearOutput)
CommandRegistry.Clear();
```

## Harmony

The framework includes Harmony for runtime patching. See [Harmony Basics](harmony-basics) for detailed usage.

**Attribute-based patches** are auto-applied by the injector at startup, no `PatchAll()` call needed:

```csharp
// This patch is applied automatically by the injector
[HarmonyPatch(typeof(Terraria.Player), "Update")]
public static class PlayerUpdatePatch
{
    [HarmonyPostfix]
    public static void Postfix(Player __instance) { /* ... */ }
}
```

**Manual patches** should be applied in the `OnGameReady` lifecycle hook:

```csharp
private static Harmony _harmony;

public static void OnGameReady()
{
    _harmony = new Harmony("com.yourname.yourmod");
    // Apply manual patches here -- game types are ready
    _harmony.Patch(method, postfix: new HarmonyMethod(typeof(Mod), "MyPostfix"));
}

public void Unload()
{
    _harmony?.UnpatchAll("com.yourname.yourmod");
}
```

See also: [Tested Patterns](tested-patterns) for practical examples.

## Custom Assets System

The Custom Assets system lets mods register new items with real Terraria item type IDs. Custom items work like vanilla items: they stack, equip, save/load, and display tooltips normally.

### Registering Items

```csharp
public void Initialize(ModContext context)
{
    // Register a custom weapon
    context.RegisterItem("fire-sword", new ItemDefinition
    {
        DisplayName = "Flame Blade",
        Tooltip = new[] { "Shoots fireballs", "Burns enemies on contact" },
        Damage = 50,
        Melee = true,
        UseStyle = 1,
        UseTime = 20,
        UseAnimation = 20,
        KnockBack = 5f,
        Shoot = 85,        // Vanilla projectile ID
        ShootSpeed = 10f,
        Rarity = 5,
        Value = 50000,
        Width = 40,
        Height = 40
    });

    // Register a custom accessory
    context.RegisterItem("speed-ring", new ItemDefinition
    {
        DisplayName = "Ring of Speed",
        Tooltip = new[] { "+10% movement speed" },
        Accessory = true,
        Rarity = 4,
        Value = 30000,
        Width = 20,
        Height = 20,
        UpdateEquip = (player) =>
        {
            // Called every frame while equipped
            var moveSpeed = player.GetType().GetField("moveSpeed");
            if (moveSpeed != null)
            {
                float current = (float)moveSpeed.GetValue(player);
                moveSpeed.SetValue(player, current + 0.1f);
            }
        }
    });
}
```

### ItemDefinition Properties

**Identity:**

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `DisplayName` | `string` | (required) | Item display name |
| `Tooltip` | `string[]` | null | Tooltip lines |
| `Texture` | `string` | null | Relative path to texture in mod folder |

**Combat:**

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `Damage` | `int` | 0 | Base damage |
| `KnockBack` | `float` | 0 | Knockback value |
| `UseTime` | `int` | 20 | Use speed |
| `UseAnimation` | `int` | 20 | Animation duration |
| `UseStyle` | `int` | 0 | How item is used (1=swing, 2=eat, 3=stab, 4=hold up, 5=shoot) |
| `Crit` | `int` | 4 | Crit chance % |
| `Mana` | `int` | 0 | Mana cost |
| `AutoReuse` | `bool` | false | Auto-swing/auto-use on hold |

**Damage Types** (set one to true):

| Property | Type | Default |
|----------|------|---------|
| `Melee` | `bool` | false |
| `Ranged` | `bool` | false |
| `Magic` | `bool` | false |
| `Summon` | `bool` | false |

**Projectile:**

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `Shoot` | `int` | 0 | Projectile type ID to fire |
| `ShootSpeed` | `float` | 0 | Projectile velocity |

**Equipment:**

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `Defense` | `int` | 0 | Defense (armor) |
| `Accessory` | `bool` | false | Equips as accessory |
| `Vanity` | `bool` | false | Vanity slot (no stats) |
| `HeadSlot` | `int` | -1 | Head equipment slot |
| `BodySlot` | `int` | -1 | Body equipment slot |
| `LegSlot` | `int` | -1 | Leg equipment slot |

**Consumable / Potion:**

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `MaxStack` | `int` | 1 | Max stack size |
| `Consumable` | `bool` | false | Consumed on use |
| `Potion` | `bool` | false | Potion (causes potion sickness) |
| `HealLife` | `int` | 0 | HP restored |
| `HealMana` | `int` | 0 | Mana restored |
| `BuffType` | `int` | 0 | Buff applied on use |
| `BuffTime` | `int` | 0 | Buff duration in frames |

**Ammo:**

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `Ammo` | `int` | 0 | Ammo category this item is |
| `UseAmmo` | `int` | 0 | Ammo category this weapon uses |

**Placement:**

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `CreateTile` | `int` | -1 | Tile type to place |
| `CreateWall` | `int` | -1 | Wall type to place |
| `PlaceStyle` | `int` | 0 | Tile/wall style variant |

**Visual:**

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `Width` | `int` | 20 | Sprite width |
| `Height` | `int` | 20 | Sprite height |
| `Scale` | `float` | 1f | Scale multiplier |
| `HoldStyle` | `int` | 0 | Hold animation style |
| `NoUseGraphic` | `bool` | false | Hide use animation |
| `NoMelee` | `bool` | false | No melee hitbox |
| `Channel` | `bool` | false | Hold to use continuously |

**Economy:**

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `Rarity` | `int` | 0 | Item rarity tier |
| `Value` | `int` | 0 | Sell value in copper |
| `Material` | `bool` | false | Can be used in recipes |

### Behavior Hooks

ItemDefinition supports runtime behavior hooks. All Terraria types are passed as `object` to avoid compile-time XNA dependencies. Use reflection or casting as needed.

```csharp
context.RegisterItem("eternal-potion", new ItemDefinition
{
    DisplayName = "Eternal Healing Potion",
    Consumable = true,
    Potion = true,
    HealLife = 150,
    UseStyle = 2,
    MaxStack = 30,

    // Prevent consumption (infinite potion)
    OnConsume = (player) => false,

    // Custom use logic
    OnUse = (player) =>
    {
        // Additional effects when used
        return true; // Allow use
    },

    // Modify tooltips dynamically
    ModifyTooltips = (lines) =>
    {
        lines.Add("Never consumed on use!");
    }
});
```

**Available Hooks:**

| Hook | Signature | Description |
|------|-----------|-------------|
| `CanUseItem` | `Func<object, bool>` | Return false to prevent use. (player) |
| `OnUse` | `Func<object, bool>` | Called on use start. Return false to cancel. (player) |
| `OnHitNPC` | `Action<object, object, int, float, bool>` | After hitting NPC. (player, npc, damage, knockback, crit) |
| `ModifyWeaponDamage` | `ModifyDamageDelegate` | Modify damage by ref. (player, ref damage) |
| `OnShoot` | `Func<object, int, float, int?>` | Override projectile. (player, projType, speed) → type or null |
| `OnConsume` | `Func<object, bool>` | Return false to prevent consuming. (player) |
| `UpdateEquip` | `Action<object>` | Per-frame while equipped as accessory. (player) |
| `OnHoldItem` | `Action<object>` | Per-frame while held/selected. (player) |
| `ModifyTooltips` | `Action<List<string>>` | Modify tooltip lines dynamically. (lines) |

### Recipes

Register crafting recipes for custom or vanilla items:

```csharp
context.RegisterRecipe(new RecipeDefinition
{
    Result = "my-mod:fire-sword",     // Custom item
    ResultStack = 1,
    Ingredients = new Dictionary<string, int>
    {
        { "IronBroadsword", 1 },      // Vanilla item by name
        { "HellstoneBar", 10 },        // Vanilla item by name
        { "my-mod:fire-gem", 5 }       // Custom item from this mod
    },
    Station = "Anvil"                  // Crafting station
});
```

**RecipeDefinition Properties:**

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `Result` | `string` | (required) | Result item: `"modid:name"` or vanilla name/ID |
| `ResultStack` | `int` | 1 | Stack size of result |
| `Ingredients` | `Dictionary<string, int>` | (required) | Key: item reference, Value: count |
| `Station` | `string` | null | Crafting station name (null = hand-crafted) |

**Item references** can be:
- Custom item: `"modid:itemname"` (e.g., `"my-mod:fire-sword"`)
- Vanilla item name: `"IronBroadsword"`, `"HellstoneBar"` (matches ItemID field names)
- Vanilla item ID: `"1"`, `"175"` (numeric string)

### NPC Shops

Add custom items to NPC shop inventories:

```csharp
context.AddShopItem(new ShopDefinition
{
    NpcType = 17,                      // Merchant NPC type ID
    ItemId = "my-mod:speed-ring",      // Custom item
    Price = 100000                     // 10 gold (in copper, 0 = use item value)
});
```

**ShopDefinition Properties:**

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `NpcType` | `int` | (required) | NPC type ID |
| `ItemId` | `string` | (required) | Item reference |
| `Price` | `int` | 0 | Price in copper (0 = item's Value) |

### NPC Drops

Register items as NPC drops:

```csharp
context.RegisterDrop(new DropDefinition
{
    NpcType = 4,                       // Eye of Cthulhu
    ItemId = "my-mod:fire-gem",
    Chance = 0.33f,                    // 33% drop chance
    MinStack = 1,
    MaxStack = 3
});
```

**DropDefinition Properties:**

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `NpcType` | `int` | (required) | NPC type ID |
| `ItemId` | `string` | (required) | Item reference |
| `Chance` | `float` | 1.0 | Drop chance (0.0 to 1.0) |
| `MinStack` | `int` | 1 | Minimum drop stack |
| `MaxStack` | `int` | 1 | Maximum drop stack |

### Custom Textures

Place texture files in your mod folder and reference them in the ItemDefinition:

```csharp
context.RegisterItem("fire-sword", new ItemDefinition
{
    DisplayName = "Flame Blade",
    Texture = "assets/textures/fire-sword.png",  // Relative to mod folder
    // ...
});
```

If no texture is specified, the item uses a default placeholder texture.

### Save/Load

Custom items are automatically saved and loaded. The framework intercepts the player save process:
- Before save: custom items are extracted from inventory (replaced with air)
- Custom item data is stored in a sidecar `.moddata` file next to the player save
- After save: custom items are restored to their original slots
- On load: custom items are restored from the sidecar file

This means custom items persist across game sessions without modifying vanilla save files.
