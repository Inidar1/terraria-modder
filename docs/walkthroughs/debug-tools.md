---
title: DebugTools Mod - HTTP Debug Server for Terraria 1.4.5
description: Walkthrough of the DebugTools mod for Terraria 1.4.5. REST API, in-game console, virtual input injection, and window management for automated testing.
parent: Walkthroughs
nav_order: 8
---

# DebugTools Walkthrough

**Difficulty:** Advanced
**Concepts:** HTTP server, background threads, in-game console, virtual input, P/Invoke window management

DebugTools is the most infrastructure-heavy mod in the framework. It provides an HTTP debug server, an in-game console, virtual input injection, and window management, enabling headless/remote game control.

## What It Does

- **HTTP Debug Server**: REST API on `localhost:7878` exposing game state, input control, and menu navigation
- **In-Game Console** (Ctrl+`): Command system with history, tab completion, and output scrolling
- **Virtual Input**: Programmatic game actions (movement, attacks, inventory) via trigger injection
- **Window Management**: Hide/show game and console windows for headless operation

## Architecture

DebugTools is a single mod that combines functionality from several subsystems. Each subsystem initializes independently and can fail without crashing the others:

```
Mod.Initialize()
├── WindowManager.Initialize()     ← P/Invoke window handles
├── DebugHttpServer.Start()        ← Background listener thread
├── ConsoleUI.Initialize()         ← In-game console with Ctrl+` keybind
├── VirtualInputManager.Init()     ← Trigger injection state
└── VirtualInputPatches.Apply()    ← Harmony patches for input pipeline
```

## Key Concepts

### 1. Config-Driven Feature Toggles

The mod's manifest defines three boolean config options:

```json
{
  "config_schema": {
    "enabled":     { "type": "bool", "default": true },
    "httpServer":  { "type": "bool", "default": true },
    "startHidden": { "type": "bool", "default": false }
  }
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
        // ... 25+ endpoints
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
| Status & Commands | `/api/status`, `/api/commands`, `/api/execute`, `/api/mods` |
| Game State | `/api/player`, `/api/world`, `/api/state/surroundings`, `/api/state/inventory`, `/api/state/entities`, `/api/state/tiles`, `/api/state/ui` |
| Virtual Input | `/api/input/key`, `/api/input/mouse`, `/api/input/action`, `/api/input/release_all`, `/api/input/actions`, `/api/input/state`, `/api/input/log` |
| Menu Navigation | `/api/menu/state`, `/api/menu/navigate`, `/api/menu/enter_world`, `/api/menu/wait` |
| Window Control | `/api/window/show`, `/api/window/hide`, `/api/window/state` |

## Lessons Learned

1. **Background threads need clean shutdown**: Use a volatile flag, catch `HttpListenerException` on close
2. **Game window isn't ready at init**: Use `OnGameReady()` lifecycle hook for anything requiring `Main.instance`
3. **Trigger injection, not raw keyboard**: Terraria overwrites keyboard state each frame; inject into the trigger system instead
4. **Wrap subsystem cleanup in try/catch**: One failure shouldn't prevent others from cleaning up
5. **CSRF matters even on localhost**: Reject `Origin` headers to prevent malicious websites from controlling your game

For more on reflection patterns, see [Tested Patterns](../tested-patterns).
