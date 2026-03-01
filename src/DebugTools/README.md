# Debug Tools

All-in-one debug and remote control suite for TerrariaModder. Provides an HTTP API, in-game debug console, virtual input injection, window management, and game state observation.

## Features

- **HTTP Debug Server**:REST API on `localhost:7878` exposing game state, input control, menu navigation, and command execution. 
- **In-Game Console**:Toggle with Ctrl+` (tilde). Supports command history (Up/Down), tab completion, scrollable output, and all registered debug commands.
- **Virtual Input**:Inject movement, actions, key presses, and mouse events into Terraria's input pipeline via trigger injection (Harmony postfix on `PlayerInput.UpdateInput`).
- **Game State API**:Read player stats, inventory, nearby entities, tile grids, UI state, and world info via HTTP endpoints.
- **Menu Navigation**:Programmatically navigate menus, select characters/worlds, and enter worlds via API.
- **Window Management**:Hide/show both the game window and injector console via P/Invoke for headless operation.
- **Input Logger**:Toggle-able logging of real mouse clicks with screen coordinates.

## Keybinds

| Key | Action |
|-----|--------|
| `Ctrl+`` | Toggle debug console |

Rebindable via the F6 Mod Menu.

## Configuration

Config file: `mods/debug-tools/config.json`

| Setting | Default | Description |
|---------|---------|-------------|
| enabled | true | Enable the mod |
| httpServer | true | Start the HTTP API server on port 7878 |
| startHidden | false | Hide game and console windows on startup (headless mode) |

## HTTP API Quick Reference

All endpoints on `http://localhost:7878`. See `docs/API.md` for the full endpoint list.

**Status & Commands:**
- `GET /api/status`:Server uptime
- `GET /api/mods`:Loaded mods
- `GET /api/commands`:Registered debug commands
- `POST /api/execute`:Run a command: `{"command": "help"}`

**Game State (requires being in a world):**
- `GET /api/player`:Health, mana, position
- `GET /api/world`:Time, hardmode, weather
- `GET /api/state/surroundings`:Combined snapshot (primary observation tool)
- `GET /api/state/inventory`:Full inventory
- `GET /api/state/entities`:Nearby NPCs/enemies
- `GET /api/state/tiles`:Tile grid around player
- `GET /api/state/ui`:UI/menu state

**Virtual Input:**
- `POST /api/input/key`:Press/release keys: `{"key": "Space", "action": "press"}`
- `POST /api/input/action`:Game actions: `{"name": "jump", "action": "execute", "duration": 200}`
- `POST /api/input/mouse`:Mouse control: `{"action": "click", "x": 100, "y": 200}`
- `POST /api/input/release_all`:Safety reset

**Menu Navigation:**
- `GET /api/menu/state`:Current menu, characters, worlds
- `POST /api/menu/enter_world`:Auto-enter: `{"character": 0, "world": 0}`
- `POST /api/menu/navigate`:Navigate: `{"target": "singleplayer"}`

**Window Control:**
- `POST /api/window/hide`:Hide all windows
- `POST /api/window/show`:Show all windows
- `GET /api/window/state`:Check visibility

## Architecture

This mod was created by merging three previously separate components:

| Component | Source | Description |
|-----------|--------|-------------|
| ConsoleUI | formerly `DebugConsole` mod | In-game console UI (F8 toggle, command history, tab completion) |
| WindowManager | formerly `RunHidden` mod | P/Invoke window hide/show for headless operation |
| DebugHttpServer + 6 support files | formerly in `Core/Debug/` | HTTP server, virtual input, game state, menu navigation, input logging |

Only `CommandRegistry.cs` remains in Core; it's the public API that all mods use to register commands via `context.RegisterCommand()`.

## Debug Commands

The mod registers these commands (accessible via console or `POST /api/execute`):

- `menu.state`:Show current menu state
- `menu.select <target>`:Navigate menus (singleplayer, character_N, world_N, play, back, title)
- `menu.back`:Go back to title screen
- `menu.enter [character] [world]`:Enter a world
- `debug-tools.echo <text>`:Print text to console

Other mods register their own commands (e.g., `help`, `mods`, `config` from Core).

## Technical Details

- **Lifecycle**: Uses the injector's `LifecycleHooks.CallLifecycleMethod("OnGameReady")` scan; the `Mod` class has a `public static void OnGameReady()` that the injector discovers and calls automatically.
- **Virtual input**: Injects into Terraria's trigger system, NOT raw keyboard state. Mod keybinds that use `Keyboard.GetState()` will not respond to virtual input.
- **Security**: HTTP server binds to localhost only. Rejects browser-originated requests (Origin header check) to prevent CSRF.

## Multiplayer

Works in multiplayer.

## Installation

Requires TerrariaModder Core.

Extract this zip into your Terraria folder. The mod goes into
`TerrariaModder/mods/debug-tools/`.
