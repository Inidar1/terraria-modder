# Debug Tools

This README documents the Debug Tools 2.0.1 feature set for Terraria 1.4.5.8. The feature set is stable; compatibility updates and minor defect fixes continue.

All-in-one debug and remote control suite for TerrariaModder. Provides an HTTP API, in-game debug console, virtual input injection, window management, and game state observation.

## Features

- **HTTP Debug Server**: REST API on `localhost:7878` with 70+ endpoints for game state, input control, menu navigation, inventory/equipment mutation, world manipulation, NPC control, and command execution. Local tools can discover and use the API directly.
- **Runtime Introspection**: Reflection browser, field read/write, property path evaluation, dynamic method tracing, and field watching — all via HTTP API. Inspect any Terraria object at runtime without recompiling.
- **In-Game Console**: Toggle with Ctrl+` (tilde). Supports command history (Up/Down), tab completion, scrollable output, and all registered debug commands.
- **Virtual Input**: Inject movement, actions, key presses, and mouse events into Terraria's input pipeline via trigger injection (Harmony postfix on `PlayerInput.UpdateInput`).
- **Game State API**: Read player stats (health, mana, position, death state, direction, velocity, zones, buffs), full inventory (including banks/vault/trash/miscEquips), nearby entities with buff data, structured tile data, projectiles, and world info.
- **Inventory & Equipment Control**: Set any inventory slot, equip armor/accessories/pets, select hotbar slots, modify chest contents — all via API.
- **World Manipulation**: Set/fill tiles, place liquids (including shimmer), toggle hardmode/blood moon/events, set boss progression flags, trigger invasions, teleport player.
- **NPC Control**: List, spawn, kill, and reposition NPCs.
- **Snapshots**: Save and restore game state snapshots for repeatable testing.
- **Menu Navigation**: Programmatically navigate menus, select characters/worlds, enter singleplayer or Host & Play worlds.
- **Window Management**: Hide/show both the game window and injector console via P/Invoke for headless operation.
- **Input Logger**: Toggle-able logging of real mouse clicks with screen coordinates.
- **System Health**: Mod health checks, Harmony patch audit, EventLog access, FPS/memory diagnostics, network stats.

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

All endpoints are on `http://localhost:7878`. The quick reference below lists the public routes and request shapes.

**Status & System:**
- `GET /api/status` — Server uptime
- `GET /api/mods` — Loaded mods
- `GET /api/commands` — Registered debug commands
- `GET /api/capabilities` — Discover all targets, actions, mod actions, keybinds
- `GET /api/logs` — EventLog entries (last 500)
- `GET /api/diagnostics` — FPS, memory, frame time
- `GET /api/health` — Mod health, error counts
- `GET /api/harmony` — All Harmony patches by owner
- `GET /api/net/stats` — netMode, role, players
- `POST /api/execute` — Run command: `{"command": "help"}`

**Game State (read-only, requires world):**
- `GET /api/player` — Health, mana, position, dead, direction, velocity, spawnX/Y, selectedItem, zones, buffs
- `GET /api/world` — Time, hardmode, weather
- `GET /api/state/surroundings` — Combined snapshot
- `GET /api/state/inventory` — Full inventory + banks/vault/trash/miscEquips
- `GET /api/state/entities` — Nearby NPCs with buff data
- `GET /api/state/tiles` — Tile charmap grid
- `GET /api/state/tiles/raw` — Structured tile data (type IDs, walls, liquids)
- `GET /api/state/ui` — UI/menu state
- `GET /api/npcs` — All NPCs
- `GET /api/projectiles` — Active projectiles (filter: ?type=N, ?owner=N)

**Inventory & Equipment:**
- `POST /api/inventory/set` — Set slot: `{slot, type, stack?, prefix?}`
- `POST /api/equip` — Equip: `{slot, index, type}` (armor/accessory/misc/dye)
- `POST /api/hotbar/select` — Select: `{slot}` (0-9)
- `POST /api/chest/set` — Set chest slot or clear/fill

**Player Actions:**
- `POST /api/player/give` — Give item: `{type, stack?, prefix?}`
- `POST /api/teleport` — Teleport: `{x, y}` (tile coords)
- `POST /api/player/buff` — Buff: `{type, duration}`
- `POST /api/save` — Save player

**World & Tile Mutations:**
- `POST /api/tiles/set` — Set tile or liquid
- `POST /api/tiles/fill` — Fill rectangle (max 100x100)
- `POST /api/world/set` — Set field: `{field, value}` (hardMode, bloodMoon, etc.)
- `POST /api/progression/set` — Set boss flags: `{flags: {downedBoss1: true}}`
- `POST /api/event` — Trigger invasion: `{event: "goblin_invasion"}`

**NPC Control:**
- `POST /api/npcs/kill` — Kill by type
- `POST /api/npcs/set_position` — Move: `{type, toPlayer: true}`
- `POST /api/spawn/npc` — Spawn: `{type}`

**Snapshots:**
- `GET /api/snapshot/list` — List snapshots
- `POST /api/snapshot/save` — Save: `{name}`
- `POST /api/snapshot/restore` — Restore: `{name}`

**Virtual Input:**
- `POST /api/input/key` — Key: `{"key": "Space", "action": "press"}`
- `POST /api/input/action` — Action: `{"name": "jump", "action": "execute", "duration": 200}`
- `POST /api/input/mouse` — Mouse: `{"action": "click", "x": 100, "y": 200}`
- `POST /api/input/release_all` — Safety reset
- `POST /api/input/log` — Toggle click logging

**Menu Navigation:**
- `GET /api/menu/state` — Current menu, characters, worlds
- `POST /api/menu/enter_world` — Enter: `{"character": 0, "world": 0}` (add `"multiplayer": true` for H&P)
- `POST /api/menu/join_world` — Join MP: `{"character": 0, "ip": "...", "port": 7777}`
- `POST /api/menu/exit_world` — Exit to title
- `POST /api/menu/navigate` — Navigate: `{"target": "singleplayer"}`
- `POST /api/menu/wait` — Wait: `{"condition": "in_world", "timeout": 30000}`

**Mod Actions & Keybinds:**
- `POST /api/mod-action` — Mod action: `{"mod": "admin-panel", "action": "toggle_god_mode"}`
- `POST /api/keybind` — Trigger keybind: `{"id": "storage-hub.toggle"}`
- `POST /api/chat/send` — Chat: `{"message": "hello"}`
- `GET /api/screenshot` — Capture screenshot (base64 PNG)

**Runtime Introspection:**
- `GET /api/reflect/type?name=Terraria.NPC` — Browse type (filter: ?search=, ?filter=static)
- `GET /api/reflect/field?type=...&field=...` — Read static field
- `POST /api/reflect/field` — Set static field: `{type, field, value}`
- `GET /api/reflect/instance?type=...&index=N` — Dump object fields
- `GET /api/eval?path=Main.player[0].statLife` — Walk object graph
- `POST /api/trace/add` — Trace method: `{type, method}` (max 20)
- `GET /api/trace/log` — Read traces
- `POST /api/watch/add` — Watch field: `{type, field, index?}` (max 50)
- `GET /api/watch/log` — Read changes

**Config:**
- `GET /api/config` — List configs
- `GET /api/config/{mod-id}` — Read config
- `POST /api/config/{mod-id}/set` — Set property
- `POST /api/config/{mod-id}/reload` — Reload from disk
- `POST /api/config/{mod-id}/reset` — Reset defaults

**Window Control:**
- `POST /api/window/hide` — Hide all windows
- `POST /api/window/show` — Show all windows
- `GET /api/window/state` — Check visibility

## Architecture

This mod was created by merging three previously separate components:

| Component | Source | Description |
|-----------|--------|-------------|
| ConsoleUI | formerly `DebugConsole` mod | In-game console UI (Ctrl+` toggle, command history, tab completion) |
| WindowManager | formerly `RunHidden` mod | P/Invoke window hide/show for headless operation |
| DebugHttpServer | formerly in `Core/Debug/` | HTTP server with 70+ endpoints for state, input, mutations, control |
| RuntimeIntrospection | new | Reflection browser, field R/W, path eval, method tracing, field watching |
| GameSenseState | formerly in `Core/Debug/` | Rich game state reader (inventory, entities, tiles, UI) |
| VirtualInputManager | formerly in `Core/Debug/` | Thread-safe virtual keyboard/mouse/trigger injection |
| VirtualInputActions | formerly in `Core/Debug/` | High-level game action API (jump, move, attack, etc.) |
| MenuNavigator | formerly in `Core/Debug/` | Programmatic menu navigation and world entry |
| ScreenCapture | new | Screenshot capture via DoDraw postfix |
| MainThreadDispatcher | new | Thread-safe game-thread execution from HTTP handlers |

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
