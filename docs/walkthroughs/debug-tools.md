---
title: Debug Tools - HTTP Debug Server for Terraria 1.4.5.8
description: Walkthrough of Debug Tools 2.0.1 for Terraria 1.4.5.8, including its HTTP API, console, virtual input, state inspection, and window controls.
parent: Walkthroughs
nav_order: 8
---

# Debug Tools Walkthrough

**Difficulty:** Advanced
**Concepts:** HTTP server, background threads, in-game console, virtual input, P/Invoke window management

Debug Tools 2.0.1 provides an HTTP debug server, runtime introspection, an in-game console, virtual input, inventory/equipment/world control, and window management. Its feature set is stable and continues to receive Terraria/Core compatibility updates and minor fixes.

## What It Does

- **HTTP Debug Server**: REST API on `localhost:7878` with 70+ endpoints for game state queries, inventory/equipment control, world manipulation, NPC management, and runtime introspection
- **Runtime Introspection**: Browse any type via reflection, read/write fields, evaluate property paths, trace method calls, and watch field changes — all at runtime via HTTP
- **In-Game Console** (Ctrl+`): Command system with history, tab completion, and output scrolling
- **Virtual Input**: Programmatic game actions (movement, attacks, inventory) via trigger injection
- **Inventory & Equipment Control**: Set any inventory slot, equip armor/accessories/pets, select hotbar, modify chest contents
- **World Manipulation**: Place/fill tiles and liquids, toggle hardmode/blood moon/events, set boss progression, trigger invasions, teleport
- **NPC Control**: List, spawn, kill, and reposition NPCs
- **Snapshots**: Save and restore game state for repeatable testing
- **Window Management**: Hide/show game and console windows for headless operation
- **System Health**: Mod health checks, Harmony patch audit, EventLog, FPS/memory diagnostics, network stats

## Architecture

Debug Tools is a single mod that combines functionality from several subsystems. Each subsystem initializes independently and can fail without crashing the others:

```
Mod.Initialize()
├── WindowManager.Initialize()       ← P/Invoke window handles
├── DebugHttpServer.Start()          ← Background listener thread (70+ endpoints)
├── RuntimeIntrospection.Init()      ← Reflection browser, tracing, watching
├── MainThreadDispatcher.Init()      ← Thread-safe game-thread execution
├── ConsoleUI.Initialize()           ← In-game console with Ctrl+` keybind
├── VirtualInputManager.Init()       ← Trigger injection state
└── VirtualInputPatches.Apply()      ← Harmony patches for input pipeline
```

## Key Concepts

### 1. Config-Driven Feature Toggles

The mod uses a class-based config with three boolean options (saved to `core/configs/debug-tools.client.json`):

```csharp
public class DebugToolsConfig : ModConfig
{
    [Client] public bool Enabled { get; set; } = true;
    [Client] public bool HttpServer { get; set; } = true;
    [Client] public bool StartHidden { get; set; } = false;
}
```

In `Initialize()`, the mod checks these before starting subsystems:

```csharp
public void Initialize(ModContext context)
{
    bool httpEnabled = context.Config.Get("httpServer", true);
    bool startHidden = context.Config.Get("startHidden", false);

    WindowManager.Initialize(_log);

    if (httpEnabled)
        DebugHttpServer.Start(_log);

    ConsoleUI.Initialize(context, _log);
}
```

This lets users disable the HTTP server without disabling the console, or start hidden for automated testing.

### 2. Background Thread HTTP Server

The HTTP server runs on a background thread using `HttpListener`, keeping the game thread free:

```csharp
public static void Start(ILogger log)
{
    _listener = new HttpListener();
    _listener.Prefixes.Add("http://localhost:7878/");
    _listener.Start();

    _listenerThread = new Thread(ListenLoop) { IsBackground = true };
    _listenerThread.Start();
}

private static void ListenLoop()
{
    while (_running)
    {
        var context = _listener.GetContext();
        ThreadPool.QueueUserWorkItem(_ => HandleRequest(context));
    }
}
```

Each request is dispatched to the thread pool, so slow requests don't block the listener. The `_running` flag (volatile) enables clean shutdown.

**CSRF protection:** The server rejects requests with an `Origin` header, blocking browser-originated requests while allowing curl and scripts.

### 3. Route Dispatch

Routes are dispatched with a simple switch on the URL path:

```csharp
private static void HandleRequest(HttpListenerContext ctx)
{
    string path = ctx.Request.Url.AbsolutePath;

    switch (path)
    {
        case "/api/status":    HandleStatus(ctx); break;
        case "/api/player":    HandlePlayer(ctx); break;
        case "/api/input/key": HandleKeyInput(ctx); break;
        // ... 70+ endpoints
    }
}
```

Game state reads use reflection (thread-safe because Terraria's static fields are stable references). Command execution uses a lock to serialize output capture.

### 4. In-Game Console with Event Subscriptions

The console subscribes to framework events for update and draw:

```csharp
public static void Initialize(ModContext context, ILogger log)
{
    // Update logic runs every frame
    FrameEvents.OnPreUpdate += OnUpdate;

    // Draw callback registered with UIRenderer
    UIRenderer.RegisterPanelDraw("debug-console", DrawConsole, priority: 100);

    // Capture command output
    CommandRegistry.OnOutput += OnCommandOutput;
    CommandRegistry.OnClearOutput += OnClearOutput;
}
```

When the console opens, it blocks keyboard input from reaching the game:

```csharp
UIRenderer.RegisterKeyInputBlock("debug-console");
UIRenderer.EnableTextInput();
```

This prevents typing in the console from moving your character or triggering keybinds.

### 5. Virtual Input: Action-to-Trigger Mapping

Virtual input works by injecting into Terraria's trigger system (not raw keyboard state). Each "action" maps to a trigger name:

```csharp
private static readonly Dictionary<string, string> ActionToTrigger = new Dictionary<string, string>
{
    { "move_left",  "Left" },
    { "move_right", "Right" },
    { "jump",       "Jump" },
    { "attack",     "MouseLeft" },
    // ... 28+ actions
};
```

A Harmony postfix on `PlayerInput.UpdateInput()` injects active triggers each frame:

```csharp
[HarmonyPostfix]
static void UpdateInput_Postfix()
{
    foreach (var trigger in VirtualInputManager.GetActiveTriggers())
    {
        PlayerInput.Triggers.Current.KeyStatus[trigger] = true;
    }
}
```

Actions use reference counting so multiple overlapping actions on the same trigger don't conflict.

### 6. Window Management via P/Invoke

Window control uses Win32 APIs to hide/show both the game and console windows:

```csharp
[DllImport("user32.dll")]
private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

[DllImport("kernel32.dll")]
private static extern IntPtr GetConsoleWindow();

private const int SW_HIDE = 0;
private const int SW_SHOW = 5;
```

The console handle is available immediately (`GetConsoleWindow()`), but the game window handle requires reflection through XNA and is only available after `Main.Initialize()` completes:

```csharp
public static void OnGameReady()
{
    // Game window handle via direct access (Main.instance is public)
    if (Main.instance?.Window != null)
        _gameHandle = Main.instance.Window.Handle;

    if (_startHidden)
        Hide();
}
```

### 7. Graceful Cleanup

Each subsystem cleans up independently, wrapped in try/catch:

```csharp
public void Unload()
{
    try { ConsoleUI.Unload(); } catch { }
    try { DebugHttpServer.Stop(); } catch { }
    try { WindowManager.RestoreIfHidden(); } catch { }
    try { VirtualInputManager.ReleaseAll(); } catch { }
}
```

This ensures that a failure in one subsystem (e.g., HTTP server already stopped) doesn't prevent window restoration or input cleanup.

## Debug Commands

The mod registers commands accessible via console or HTTP:

| Command | Description |
|---------|-------------|
| `menu.state` | Show current menu screen and available characters/worlds |
| `menu.select <target>` | Navigate menus (singleplayer, character_N, world_N, play, back, title) |
| `menu.back` | Return to title screen |
| `menu.enter [char] [world]` | Enter a world by index |
| `debug-tools.echo <text>` | Print text to console |

## HTTP Endpoints (Summary)

| Category | Endpoints |
|----------|-----------|
| Status & System | `/api/status`, `/api/commands`, `/api/execute`, `/api/mods`, `/api/capabilities`, `/api/logs`, `/api/diagnostics`, `/api/health`, `/api/harmony`, `/api/net/stats` |
| Game State (read) | `/api/player`, `/api/world`, `/api/state/surroundings`, `/api/state/inventory`, `/api/state/entities`, `/api/state/tiles`, `/api/state/tiles/raw`, `/api/state/ui`, `/api/npcs`, `/api/projectiles` |
| Inventory & Equip | `/api/inventory/set`, `/api/equip`, `/api/hotbar/select`, `/api/chest/set` |
| Player Actions | `/api/player/give`, `/api/teleport`, `/api/player/buff`, `/api/save` |
| World & Tiles | `/api/tiles/set`, `/api/tiles/fill`, `/api/world/set`, `/api/progression/set`, `/api/event` |
| NPC Control | `/api/npcs/kill`, `/api/npcs/set_position`, `/api/spawn/npc` |
| Snapshots | `/api/snapshot/list`, `/api/snapshot/save`, `/api/snapshot/restore` |
| Virtual Input | `/api/input/key`, `/api/input/mouse`, `/api/input/action`, `/api/input/release_all`, `/api/input/actions`, `/api/input/state`, `/api/input/log` |
| Menu Navigation | `/api/menu/state`, `/api/menu/navigate`, `/api/menu/enter_world`, `/api/menu/join_world`, `/api/menu/exit_world`, `/api/menu/wait` |
| Mod Actions | `/api/mod-action`, `/api/keybind`, `/api/chat/send`, `/api/screenshot` |
| Introspection | `/api/reflect/type`, `/api/reflect/field`, `/api/reflect/instance`, `/api/eval`, `/api/trace/add`, `/api/trace/remove`, `/api/trace/log`, `/api/watch/add`, `/api/watch/remove`, `/api/watch/log` |
| Config | `/api/config`, `/api/config/{id}`, `/api/config/{id}/set`, `/api/config/{id}/reload`, `/api/config/{id}/reset` |
| Window Control | `/api/window/show`, `/api/window/hide`, `/api/window/state` |

## Lessons Learned

1. **Background threads need clean shutdown**: Use a volatile flag, catch `HttpListenerException` on close
2. **Game window isn't ready at init**: Use `OnGameReady()` lifecycle hook for anything requiring `Main.instance`
3. **Trigger injection, not raw keyboard**: Terraria overwrites keyboard state each frame; inject into the trigger system instead
4. **Wrap subsystem cleanup in try/catch**: One failure shouldn't prevent others from cleaning up
5. **CSRF matters even on localhost**: Reject `Origin` headers to prevent malicious websites from controlling your game

For more on reflection patterns, see [Tested Patterns](../tested-patterns.md).
