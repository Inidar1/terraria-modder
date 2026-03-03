using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using Terraria;
using TerrariaModder.Core;
using TerrariaModder.Core.Debug;
using TerrariaModder.Core.Logging;
using TerrariaModder.Core.Reflection;

namespace DebugTools
{
    /// <summary>
    /// HTTP debug server that exposes game state and command execution via REST API.
    /// Runs on a background thread listening on localhost. External tools (curl,
    /// scripts, etc.) can query game state while the game is running.
    /// </summary>
    public sealed class DebugHttpServer : IDisposable
    {
        private readonly HttpListener _listener;
        private readonly ILogger _log;
        private readonly int _port;
        private Thread _listenerThread;
        private volatile bool _running;
        private readonly DateTime _startTime;
        private readonly MenuNavigator _menuNav;
        private readonly object _executeLock = new object(); // Serializes /api/execute to prevent output mixing

        /// <summary>
        /// Create a new debug HTTP server.
        /// </summary>
        /// <param name="logger">Logger instance.</param>
        /// <param name="port">Port to listen on (default 7878).</param>
        public DebugHttpServer(ILogger logger, int port = 7878)
        {
            _log = logger;
            _port = port;
            _startTime = DateTime.UtcNow;
            _menuNav = new MenuNavigator(logger);
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://localhost:{_port}/");
            _listener.Prefixes.Add($"http://127.0.0.1:{_port}/");
        }

        /// <summary>
        /// Start the HTTP server on a background thread.
        /// </summary>
        public void Start()
        {
            if (_running) return;

            try
            {
                _listener.Start();
                _running = true;

                _listenerThread = new Thread(ListenLoop)
                {
                    Name = "DebugHttpServer",
                    IsBackground = true
                };
                _listenerThread.Start();

                _log.Info($"[DebugHttpServer] Started on http://localhost:{_port}/");
            }
            catch (HttpListenerException ex)
            {
                _log.Error($"[DebugHttpServer] Failed to start on port {_port} (is another instance running?): {ex}");
                try { _listener.Close(); } catch { }
            }
            catch (Exception ex)
            {
                _log.Error($"[DebugHttpServer] Failed to start on port {_port}: {ex}");
                try { _listener.Close(); } catch { }
            }
        }

        /// <summary>
        /// Stop the HTTP server.
        /// </summary>
        public void Stop()
        {
            if (!_running) return;

            _running = false;

            try
            {
                _listener.Stop();
            }
            catch (Exception ex)
            {
                _log.Error($"[DebugHttpServer] Error stopping listener: {ex}");
            }

            _log.Info("[DebugHttpServer] Stopped");
        }

        /// <summary>
        /// Dispose the server and release resources.
        /// </summary>
        public void Dispose()
        {
            Stop();
            _listener.Close();
        }

        private void ListenLoop()
        {
            while (_running)
            {
                try
                {
                    var context = _listener.GetContext();
                    ThreadPool.QueueUserWorkItem(_ => HandleRequest(context));
                }
                catch (HttpListenerException) when (!_running)
                {
                    // Expected when stopping
                }
                catch (ObjectDisposedException) when (!_running)
                {
                    // Expected when stopping
                }
                catch (Exception ex)
                {
                    if (_running)
                    {
                        _log.Error($"[DebugHttpServer] Listener error: {ex}");
                    }
                }
            }
        }

        private void HandleRequest(HttpListenerContext context)
        {
            var request = context.Request;
            var response = context.Response;

            try
            {
                string path = request.Url.AbsolutePath.TrimEnd('/');
                string method = request.HttpMethod;

                // Block browser-originated requests (CSRF protection).
                // Legitimate callers (curl, Node.js, scripts) never send Origin headers.
                string origin = request.Headers["Origin"];
                if (origin != null)
                {
                    _log.Warn($"[DebugHttpServer] Rejected browser request from origin: {origin}");
                    SendError(response, 403, "Browser requests are not allowed. Use curl or a script client.");
                    return;
                }

                _log.Debug($"[DebugHttpServer] {method} {path}");

                string json;
                int statusCode = 200;

                switch (path)
                {
                    case "/api/status":
                        json = HandleStatus();
                        break;

                    case "/api/commands":
                        json = HandleCommands();
                        break;

                    case "/api/execute":
                        if (method != "POST")
                        {
                            SendError(response, 405, "Method not allowed. Use POST.");
                            return;
                        }
                        string body = ReadRequestBody(request);
                        json = HandleExecute(body);
                        break;

                    case "/api/mods":
                        json = HandleMods();
                        break;

                    case "/api/player":
                        json = HandlePlayer();
                        break;

                    case "/api/world":
                        json = HandleWorld();
                        break;

                    case "/api/input/key":
                        if (method != "POST")
                        {
                            SendError(response, 405, "Method not allowed. Use POST.");
                            return;
                        }
                        json = HandleInputKey(ReadRequestBody(request));
                        break;

                    case "/api/input/mouse":
                        if (method != "POST")
                        {
                            SendError(response, 405, "Method not allowed. Use POST.");
                            return;
                        }
                        json = HandleInputMouse(ReadRequestBody(request));
                        break;

                    case "/api/input/action":
                        if (method != "POST")
                        {
                            SendError(response, 405, "Method not allowed. Use POST.");
                            return;
                        }
                        json = HandleInputAction(ReadRequestBody(request));
                        break;

                    case "/api/input/release_all":
                        if (method != "POST")
                        {
                            SendError(response, 405, "Method not allowed. Use POST.");
                            return;
                        }
                        VirtualInputManager.ReleaseAll();
                        json = JsonObject(JsonPair("success", true));
                        break;

                    case "/api/input/actions":
                        json = HandleInputActionsList();
                        break;

                    case "/api/input/state":
                        json = HandleInputState();
                        break;

                    case "/api/input/log":
                        if (method == "POST")
                            json = HandleInputLogToggle(ReadRequestBody(request));
                        else
                            json = HandleInputLogGet();
                        break;

                    case "/api/state/surroundings":
                        json = GameSenseState.GetSurroundings();
                        break;

                    case "/api/state/inventory":
                        json = GameSenseState.GetInventory();
                        break;

                    case "/api/state/entities":
                        json = GameSenseState.GetEntities();
                        break;

                    case "/api/state/tiles":
                        json = GameSenseState.GetTiles();
                        break;

                    case "/api/state/ui":
                        json = GameSenseState.GetUIState();
                        break;

                    case "/api/menu/state":
                        json = HandleMenuState();
                        break;

                    case "/api/menu/navigate":
                        if (method != "POST")
                        {
                            SendError(response, 405, "Method not allowed. Use POST.");
                            return;
                        }
                        json = HandleMenuNavigate(ReadRequestBody(request), out statusCode);
                        break;

                    case "/api/menu/enter_world":
                        if (method != "POST")
                        {
                            SendError(response, 405, "Method not allowed. Use POST.");
                            return;
                        }
                        json = HandleEnterWorld(ReadRequestBody(request), out statusCode);
                        break;

                    case "/api/menu/wait":
                        if (method != "POST")
                        {
                            SendError(response, 405, "Method not allowed. Use POST.");
                            return;
                        }
                        json = HandleMenuWait(ReadRequestBody(request), out statusCode);
                        break;

                    case "/api/window/show":
                        if (method != "POST")
                        {
                            SendError(response, 405, "Method not allowed. Use POST.");
                            return;
                        }
                        json = HandleWindowShow();
                        break;

                    case "/api/window/hide":
                        if (method != "POST")
                        {
                            SendError(response, 405, "Method not allowed. Use POST.");
                            return;
                        }
                        json = HandleWindowHide();
                        break;

                    case "/api/window/state":
                        json = HandleWindowState();
                        break;

                    default:
                        SendError(response, 404, $"Not found: {path}");
                        return;
                }

                SendJson(response, statusCode, json);
            }
            catch (RequestTooLargeException)
            {
                try { SendError(response, 413, "Request body exceeds 64KB limit"); }
                catch { }
            }
            catch (Exception ex)
            {
                _log.Error($"[DebugHttpServer] Request error: {ex}");
                try
                {
                    SendError(response, 500, $"Internal error: {ex.Message}");
                }
                catch
                {
                    // Response may already be closed
                }
            }
            finally
            {
                try { response.Close(); }
                catch { }
            }
        }

        #region Endpoint Handlers

        private string HandleStatus()
        {
            var uptime = (int)(DateTime.UtcNow - _startTime).TotalSeconds;
            return JsonObject(
                JsonPair("status", "ok"),
                JsonPair("uptime", uptime),
                JsonPair("port", _port)
            );
        }

        private string HandleCommands()
        {
            var commands = CommandRegistry.GetCommands();
            var items = new List<string>();

            foreach (var cmd in commands)
            {
                items.Add(JsonObject(
                    JsonPair("name", cmd.Name),
                    JsonPair("description", cmd.Description),
                    JsonPair("modId", cmd.ModId)
                ));
            }

            return JsonObject(
                JsonArray("commands", items)
            );
        }

        private string HandleExecute(string body)
        {
            // Parse "command" from JSON body
            string command = ExtractJsonString(body, "command");
            if (string.IsNullOrWhiteSpace(command))
            {
                return JsonObject(
                    JsonPair("success", false),
                    JsonPair("error", "Missing or empty 'command' in request body")
                );
            }

            // Serialize command execution to prevent concurrent requests from mixing output
            lock (_executeLock)
            {
                var outputLines = new List<string>();
                Action<string> captureHandler = line => outputLines.Add(line);

                try
                {
                    CommandRegistry.OnOutput += captureHandler;
                    bool found = CommandRegistry.Execute(command);

                    if (!found)
                    {
                        return JsonObject(
                            JsonPair("success", false),
                            JsonPair("error", $"Unknown command: {command.Split(' ')[0]}")
                        );
                    }

                    return JsonObject(
                        JsonPair("success", true),
                        JsonStringArray("output", outputLines)
                    );
                }
                finally
                {
                    CommandRegistry.OnOutput -= captureHandler;
                }
            }
        }

        private string HandleMods()
        {
            var mods = PluginLoader.Mods;
            var items = new List<string>();

            foreach (var mod in mods)
            {
                var pairs = new List<string>
                {
                    JsonPair("id", mod.Manifest.Id),
                    JsonPair("name", mod.Manifest.Name),
                    JsonPair("version", mod.Manifest.Version ?? "unknown"),
                    JsonPair("status", mod.State.ToString())
                };

                if (!string.IsNullOrEmpty(mod.ErrorMessage))
                    pairs.Add(JsonPair("error", mod.ErrorMessage));

                if (!string.IsNullOrEmpty(mod.VersionWarning))
                    pairs.Add(JsonPair("versionWarning", mod.VersionWarning));

                items.Add(JsonObject(pairs.ToArray()));
            }

            return JsonObject(
                JsonArray("mods", items)
            );
        }

        private string HandlePlayer()
        {
            try
            {
                if (!Game.InWorld)
                {
                    return JsonObject(
                        JsonPair("inWorld", false),
                        JsonPair("error", "Not in a world")
                    );
                }

                var player = Game.LocalPlayer;
                if (player == null)
                {
                    return JsonObject(
                        JsonPair("inWorld", false),
                        JsonPair("error", "Player not available")
                    );
                }

                var pos = Game.PlayerPosition;

                return JsonObject(
                    JsonPair("inWorld", true),
                    JsonPair("name", player.name ?? ""),
                    JsonPair("health", Game.PlayerHealth),
                    JsonPair("maxHealth", Game.PlayerMaxHealth),
                    JsonPair("mana", Game.PlayerMana),
                    JsonPair("maxMana", Game.PlayerMaxMana),
                    JsonPair("dead", Game.PlayerDead),
                    JsonPair("positionX", (double)pos.X),
                    JsonPair("positionY", (double)pos.Y),
                    JsonPair("selectedItem", Game.SelectedItem)
                );
            }
            catch (Exception ex)
            {
                _log.Error($"[DebugHttpServer] Failed to read player state: {ex}");
                return JsonObject(
                    JsonPair("inWorld", false),
                    JsonPair("error", $"Failed to read player state: {ex.Message}")
                );
            }
        }

        private string HandleWorld()
        {
            try
            {
                if (!Game.InWorld)
                {
                    return JsonObject(
                        JsonPair("inWorld", false),
                        JsonPair("error", "Not in a world")
                    );
                }

                string worldName = Main.worldName ?? "";
                bool hardMode = Main.hardMode;
                bool expertMode = Main.expertMode;
                bool masterMode = Main.masterMode;

                return JsonObject(
                    JsonPair("inWorld", true),
                    JsonPair("name", worldName),
                    JsonPair("time", Game.Time),
                    JsonPair("dayTime", Game.IsDayTime),
                    JsonPair("hardMode", hardMode),
                    JsonPair("expertMode", expertMode),
                    JsonPair("masterMode", masterMode),
                    JsonPair("bloodMoon", Game.BloodMoon),
                    JsonPair("eclipse", Game.Eclipse),
                    JsonPair("raining", Game.Raining),
                    JsonPair("maxTilesX", Game.MaxTilesX),
                    JsonPair("maxTilesY", Game.MaxTilesY),
                    JsonPair("worldSurface", Game.WorldSurface),
                    JsonPair("rockLayer", Game.RockLayer)
                );
            }
            catch (Exception ex)
            {
                _log.Error($"[DebugHttpServer] Failed to read world state: {ex}");
                return JsonObject(
                    JsonPair("inWorld", false),
                    JsonPair("error", $"Failed to read world state: {ex.Message}")
                );
            }
        }

        private string HandleInputKey(string body)
        {
            string action = ExtractJsonString(body, "action");
            string key = ExtractJsonString(body, "key");

            if (string.IsNullOrEmpty(action))
                return JsonObject(JsonPair("success", false), JsonPair("error", "Missing 'action' (press|release|hold)"));
            if (string.IsNullOrEmpty(key))
                return JsonObject(JsonPair("success", false), JsonPair("error", "Missing 'key' name"));

            switch (action.ToLowerInvariant())
            {
                case "press":
                    VirtualInputManager.PressKey(key);
                    return JsonObject(JsonPair("success", true), JsonPair("action", "press"), JsonPair("key", key));

                case "release":
                    VirtualInputManager.ReleaseKey(key);
                    return JsonObject(JsonPair("success", true), JsonPair("action", "release"), JsonPair("key", key));

                case "hold":
                    int duration = ExtractJsonInt(body, "duration", 100);
                    VirtualInputManager.HoldKey(key, duration);
                    return JsonObject(JsonPair("success", true), JsonPair("action", "hold"), JsonPair("key", key), JsonPair("duration", duration));

                default:
                    return JsonObject(JsonPair("success", false), JsonPair("error", $"Unknown action: {action}. Use press, release, or hold."));
            }
        }

        private string HandleInputMouse(string body)
        {
            string action = ExtractJsonString(body, "action");

            if (string.IsNullOrEmpty(action))
                return JsonObject(JsonPair("success", false), JsonPair("error", "Missing 'action' (move|click|down|up|scroll)"));

            switch (action.ToLowerInvariant())
            {
                case "move":
                {
                    int x = ExtractJsonInt(body, "x", -1);
                    int y = ExtractJsonInt(body, "y", -1);
                    if (x < 0 || y < 0)
                        return JsonObject(JsonPair("success", false), JsonPair("error", "Missing 'x' and 'y' coordinates"));
                    VirtualInputManager.SetMousePosition(x, y);
                    return JsonObject(JsonPair("success", true), JsonPair("action", "move"), JsonPair("x", x), JsonPair("y", y));
                }

                case "click":
                {
                    int x = ExtractJsonInt(body, "x", -1);
                    int y = ExtractJsonInt(body, "y", -1);
                    string button = ExtractJsonString(body, "button") ?? "left";
                    int duration = ExtractJsonInt(body, "duration", 100);
                    if (x < 0 || y < 0)
                        return JsonObject(JsonPair("success", false), JsonPair("error", "Missing 'x' and 'y' coordinates"));
                    VirtualInputManager.ClickMouse(x, y, button, duration);
                    return JsonObject(JsonPair("success", true), JsonPair("action", "click"),
                        JsonPair("x", x), JsonPair("y", y), JsonPair("button", button));
                }

                case "down":
                {
                    string button = ExtractJsonString(body, "button") ?? "left";
                    VirtualInputManager.MouseDown(button);
                    return JsonObject(JsonPair("success", true), JsonPair("action", "down"), JsonPair("button", button));
                }

                case "up":
                {
                    string button = ExtractJsonString(body, "button") ?? "left";
                    VirtualInputManager.MouseUp(button);
                    return JsonObject(JsonPair("success", true), JsonPair("action", "up"), JsonPair("button", button));
                }

                case "scroll":
                {
                    int delta = ExtractJsonInt(body, "delta", 0);
                    if (delta == 0)
                        return JsonObject(JsonPair("success", false), JsonPair("error", "Missing or zero 'delta'"));
                    VirtualInputManager.ScrollMouse(delta);
                    return JsonObject(JsonPair("success", true), JsonPair("action", "scroll"), JsonPair("delta", delta));
                }

                case "clear":
                    VirtualInputManager.ClearMousePosition();
                    return JsonObject(JsonPair("success", true), JsonPair("action", "clear"));

                default:
                    return JsonObject(JsonPair("success", false),
                        JsonPair("error", $"Unknown action: {action}. Use move, click, down, up, scroll, or clear."));
            }
        }

        private string HandleInputAction(string body)
        {
            string name = ExtractJsonString(body, "name");
            if (string.IsNullOrEmpty(name))
                return JsonObject(JsonPair("success", false), JsonPair("error", "Missing 'name' field"));

            string action = ExtractJsonString(body, "action") ?? "execute";
            int duration = ExtractJsonInt(body, "duration", 100);

            switch (action.ToLowerInvariant())
            {
                case "execute":
                    if (!VirtualInputActions.ExecuteAction(name, duration))
                        return JsonObject(JsonPair("success", false),
                            JsonPair("error", $"Unknown action: {name}"));
                    return JsonObject(JsonPair("success", true), JsonPair("name", name), JsonPair("duration", duration));

                case "start":
                    if (!VirtualInputActions.StartAction(name))
                        return JsonObject(JsonPair("success", false),
                            JsonPair("error", $"Unknown action: {name}"));
                    return JsonObject(JsonPair("success", true), JsonPair("name", name), JsonPair("action", "start"));

                case "stop":
                    VirtualInputActions.StopAction(name);
                    return JsonObject(JsonPair("success", true), JsonPair("name", name), JsonPair("action", "stop"));

                default:
                    return JsonObject(JsonPair("success", false),
                        JsonPair("error", $"Unknown action type: {action}. Use execute, start, or stop."));
            }
        }

        private string HandleInputActionsList()
        {
            var actions = new List<string>();
            foreach (var name in VirtualInputActions.GetAvailableActions())
            {
                string trigger = VirtualInputActions.GetTriggerName(name);
                actions.Add(JsonObject(
                    JsonPair("name", name),
                    JsonPair("trigger", trigger ?? "")
                ));
            }
            return JsonObject(JsonArray("actions", actions));
        }

        private string HandleInputState()
        {
            var (keys, triggers, mouseActive, mouseX, mouseY, mouseLeft, mouseRight, mouseMiddle) =
                VirtualInputManager.GetState();

            var keyList = new List<string>();
            foreach (var k in keys)
                keyList.Add($"\"{EscapeJson(k)}\"");

            var triggerList = new List<string>();
            foreach (var t in triggers)
                triggerList.Add($"\"{EscapeJson(t)}\"");

            var sb = new StringBuilder();
            sb.Append("{");
            sb.Append(JsonPair("active", VirtualInputManager.HasActiveInput));
            sb.Append(", ");

            // Keys array
            sb.Append("\"keys\": [");
            sb.Append(string.Join(", ", keyList));
            sb.Append("], ");

            // Triggers array
            sb.Append("\"triggers\": [");
            sb.Append(string.Join(", ", triggerList));
            sb.Append("], ");

            // Mouse
            sb.Append("\"mouse\": ");
            sb.Append(JsonObject(
                JsonPair("positionActive", mouseActive),
                JsonPair("x", mouseX),
                JsonPair("y", mouseY),
                JsonPair("leftDown", mouseLeft),
                JsonPair("rightDown", mouseRight),
                JsonPair("middleDown", mouseMiddle)
            ));
            sb.Append("}");

            return sb.ToString();
        }

        private string HandleInputLogToggle(string body)
        {
            string action = ExtractJsonString(body, "action");
            if (string.IsNullOrEmpty(action))
                return JsonObject(JsonPair("success", false), JsonPair("error", "Missing 'action' (enable|disable|clear)"));

            switch (action.ToLowerInvariant())
            {
                case "enable":
                    InputLogger.Enabled = true;
                    _log.Info("[DebugHttpServer] Input logging enabled");
                    return JsonObject(JsonPair("success", true), JsonPair("enabled", true));

                case "disable":
                    InputLogger.Enabled = false;
                    _log.Info("[DebugHttpServer] Input logging disabled");
                    return JsonObject(JsonPair("success", true), JsonPair("enabled", false));

                case "clear":
                    InputLogger.Clear();
                    return JsonObject(JsonPair("success", true), JsonPair("cleared", true));

                default:
                    return JsonObject(JsonPair("success", false),
                        JsonPair("error", $"Unknown action: {action}. Use enable, disable, or clear."));
            }
        }

        private string HandleInputLogGet()
        {
            var entries = InputLogger.GetEntries();
            var items = new List<string>();
            foreach (var e in entries)
            {
                var pairs = new List<string>
                {
                    JsonPair("time", e.Timestamp.ToString("HH:mm:ss.fff")),
                    JsonPair("x", e.X),
                    JsonPair("y", e.Y),
                    JsonPair("button", e.Button),
                    JsonPair("inWorld", e.InWorld)
                };
                if (!e.InWorld)
                    pairs.Add(JsonPair("menuMode", e.MenuMode));
                items.Add(JsonObject(pairs.ToArray()));
            }

            return JsonObject(
                JsonPair("enabled", InputLogger.Enabled),
                JsonPair("count", entries.Count),
                JsonArray("clicks", items)
            );
        }

        #endregion

        #region Menu Navigation Handlers

        private string HandleMenuState()
        {
            try
            {
                var state = _menuNav.GetMenuState();

                var pairs = new List<string>
                {
                    JsonPair("inMenu", state.InMenu),
                    JsonPair("inWorld", state.InWorld),
                    JsonPair("menuMode", state.MenuMode),
                    JsonPair("menuDescription", state.MenuDescription)
                };

                if (state.InWorld)
                {
                    pairs.Add(JsonPair("worldName", state.WorldName));
                }

                if (state.InMenu)
                {
                    pairs.Add(JsonPair("playerCount", state.PlayerCount));
                    pairs.Add(JsonPair("worldCount", state.WorldCount));

                    if (state.Players != null)
                    {
                        var playerItems = new List<string>();
                        foreach (var p in state.Players)
                        {
                            playerItems.Add(JsonObject(
                                JsonPair("index", p.Index),
                                JsonPair("name", p.Name),
                                JsonPair("difficulty", p.Difficulty)
                            ));
                        }
                        pairs.Add(JsonArray("players", playerItems));
                    }

                    if (state.Worlds != null)
                    {
                        var worldItems = new List<string>();
                        foreach (var w in state.Worlds)
                        {
                            worldItems.Add(JsonObject(
                                JsonPair("index", w.Index),
                                JsonPair("name", w.Name),
                                JsonPair("seed", w.Seed),
                                JsonPair("isHardMode", w.IsHardMode),
                                JsonPair("gameMode", w.GameMode)
                            ));
                        }
                        pairs.Add(JsonArray("worlds", worldItems));
                    }
                }

                return JsonObject(pairs.ToArray());
            }
            catch (Exception ex)
            {
                _log.Error($"[DebugHttpServer] Failed to get menu state: {ex}");
                return JsonObject(
                    JsonPair("success", false),
                    JsonPair("error", $"Failed to get menu state: {ex.Message}")
                );
            }
        }

        private string HandleMenuNavigate(string body, out int statusCode)
        {
            string target = ExtractJsonString(body, "target");
            if (string.IsNullOrEmpty(target))
            {
                statusCode = 400;
                return JsonObject(JsonPair("success", false), JsonPair("error", "Missing 'target' field"));
            }

            var result = _menuNav.Navigate(target);
            statusCode = result.Success ? 200 : 400;
            return NavigationResultToJson(result);
        }

        /// <summary>Maximum timeout for blocking menu operations (60 seconds).</summary>
        private const int MaxMenuTimeoutMs = 60_000;

        private string HandleEnterWorld(string body, out int statusCode)
        {
            int character = ExtractJsonInt(body, "character", 0);
            int world = ExtractJsonInt(body, "world", 0);
            int timeout = ExtractJsonInt(body, "timeout", 30000);
            if (timeout > MaxMenuTimeoutMs) timeout = MaxMenuTimeoutMs;
            if (timeout <= 0) timeout = 30000;

            var result = _menuNav.EnterWorld(character, world, timeout);
            statusCode = result.Success ? 200 : 400;
            return NavigationResultToJson(result);
        }

        private string HandleMenuWait(string body, out int statusCode)
        {
            string condition = ExtractJsonString(body, "condition");
            if (string.IsNullOrEmpty(condition))
            {
                statusCode = 400;
                return JsonObject(JsonPair("success", false), JsonPair("error", "Missing 'condition' field"));
            }

            int timeout = ExtractJsonInt(body, "timeout", 15000);
            if (timeout > MaxMenuTimeoutMs) timeout = MaxMenuTimeoutMs;
            if (timeout <= 0) timeout = 15000;

            var result = _menuNav.WaitForState(condition, timeout);
            statusCode = result.Success ? 200 : 408;
            return NavigationResultToJson(result);
        }

        private string NavigationResultToJson(MenuNavigator.NavigationResult result)
        {
            var pairs = new List<string>
            {
                JsonPair("success", result.Success),
                JsonPair("message", result.Message)
            };

            if (result.WorldName != null)
                pairs.Add(JsonPair("worldName", result.WorldName));

            return JsonObject(pairs.ToArray());
        }

        #endregion

        #region Window Control

        private string HandleWindowShow()
        {
            WindowManager.Show();
            return JsonObject(JsonPair("success", true), JsonPair("visible", true));
        }

        private string HandleWindowHide()
        {
            WindowManager.Hide();
            return JsonObject(JsonPair("success", true), JsonPair("visible", false));
        }

        private string HandleWindowState()
        {
            bool hidden = WindowManager.IsHidden;

            return JsonObject(
                JsonPair("visible", !hidden),
                JsonPair("hidden", hidden));
        }

        #endregion

        #region HTTP Helpers

        /// <summary>Maximum request body size (64KB). Prevents OOM from large payloads.</summary>
        private const int MaxRequestBodySize = 64 * 1024;

        private static string ReadRequestBody(HttpListenerRequest request)
        {
            if (!request.HasEntityBody) return "";

            // Check Content-Length if available - throw so HandleRequest sends 413
            if (request.ContentLength64 > MaxRequestBodySize)
                throw new RequestTooLargeException();

            using (var reader = new StreamReader(request.InputStream, request.ContentEncoding))
            {
                var buffer = new char[MaxRequestBodySize];
                int read = reader.Read(buffer, 0, buffer.Length);
                return new string(buffer, 0, read);
            }
        }

        private class RequestTooLargeException : Exception
        {
            public RequestTooLargeException() : base("Request body exceeds 64KB limit") { }
        }

        private static void SendJson(HttpListenerResponse response, int statusCode, string json)
        {
            response.StatusCode = statusCode;
            response.ContentType = "application/json";
            // No CORS headers — only non-browser clients (curl, scripts, MCP) should access this API.
            byte[] buffer = Encoding.UTF8.GetBytes(json);
            response.ContentLength64 = buffer.Length;
            var stream = response.OutputStream;
            try
            {
                stream.Write(buffer, 0, buffer.Length);
            }
            finally
            {
                stream.Close();
            }
        }

        private static void SendError(HttpListenerResponse response, int statusCode, string message)
        {
            string json = JsonObject(
                JsonPair("error", message),
                JsonPair("status", statusCode)
            );
            SendJson(response, statusCode, json);
        }

        #endregion

        #region JSON Serialization Helpers

        private static string JsonPair(string key, string value)
        {
            if (value == null)
                return $"\"{EscapeJson(key)}\": null";
            return $"\"{EscapeJson(key)}\": \"{EscapeJson(value)}\"";
        }

        private static string JsonPair(string key, int value)
        {
            return $"\"{EscapeJson(key)}\": {value}";
        }

        private static string JsonPair(string key, double value)
        {
            return $"\"{EscapeJson(key)}\": {value.ToString("G", CultureInfo.InvariantCulture)}";
        }

        private static string JsonPair(string key, bool value)
        {
            return $"\"{EscapeJson(key)}\": {(value ? "true" : "false")}";
        }

        private static string JsonArray(string key, List<string> items)
        {
            if (items.Count == 0)
                return $"\"{EscapeJson(key)}\": []";

            var sb = new StringBuilder();
            sb.Append($"\"{EscapeJson(key)}\": [");
            for (int i = 0; i < items.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(items[i]);
            }
            sb.Append("]");
            return sb.ToString();
        }

        private static string JsonStringArray(string key, List<string> items)
        {
            if (items.Count == 0)
                return $"\"{EscapeJson(key)}\": []";

            var sb = new StringBuilder();
            sb.Append($"\"{EscapeJson(key)}\": [");
            for (int i = 0; i < items.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append($"\"{EscapeJson(items[i])}\"");
            }
            sb.Append("]");
            return sb.ToString();
        }

        private static string JsonObject(params string[] pairs)
        {
            return "{" + string.Join(", ", pairs) + "}";
        }

        private static string EscapeJson(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\\", "\\\\")
                    .Replace("\"", "\\\"")
                    .Replace("\n", "\\n")
                    .Replace("\r", "\\r")
                    .Replace("\t", "\\t");
        }

        /// <summary>
        /// Extract a string value from a simple JSON object by key.
        /// Handles: {"key": "value"} patterns.
        /// </summary>
        private static string ExtractJsonString(string json, string key)
        {
            if (string.IsNullOrEmpty(json)) return null;

            // Find "key": "value" pattern
            string searchKey = $"\"{key}\"";
            int keyIndex = json.IndexOf(searchKey, StringComparison.Ordinal);
            if (keyIndex < 0) return null;

            // Skip past key and colon
            int colonIndex = json.IndexOf(':', keyIndex + searchKey.Length);
            if (colonIndex < 0) return null;

            // Find opening quote of value
            int valueStart = json.IndexOf('"', colonIndex + 1);
            if (valueStart < 0) return null;
            valueStart++; // Skip the opening quote

            // Find closing quote (handle escaped quotes)
            int valueEnd = valueStart;
            while (valueEnd < json.Length)
            {
                if (json[valueEnd] == '\\')
                {
                    valueEnd += 2; // Skip escaped character
                    continue;
                }
                if (json[valueEnd] == '"')
                    break;
                valueEnd++;
            }

            if (valueEnd >= json.Length) return null;

            return json.Substring(valueStart, valueEnd - valueStart);
        }

        /// <summary>
        /// Extract an integer value from a simple JSON object by key.
        /// Handles: {"key": 123} patterns.
        /// </summary>
        private static int ExtractJsonInt(string json, string key, int defaultValue = 0)
        {
            if (string.IsNullOrEmpty(json)) return defaultValue;

            string searchKey = $"\"{key}\"";
            int keyIndex = json.IndexOf(searchKey, StringComparison.Ordinal);
            if (keyIndex < 0) return defaultValue;

            int colonIndex = json.IndexOf(':', keyIndex + searchKey.Length);
            if (colonIndex < 0) return defaultValue;

            // Skip whitespace after colon
            int valueStart = colonIndex + 1;
            while (valueStart < json.Length && char.IsWhiteSpace(json[valueStart]))
                valueStart++;

            if (valueStart >= json.Length) return defaultValue;

            // Read digits (and optional leading minus)
            int valueEnd = valueStart;
            if (valueEnd < json.Length && json[valueEnd] == '-')
                valueEnd++;
            while (valueEnd < json.Length && char.IsDigit(json[valueEnd]))
                valueEnd++;

            if (valueEnd == valueStart) return defaultValue;

            string numStr = json.Substring(valueStart, valueEnd - valueStart);
            if (int.TryParse(numStr, out int result))
                return result;

            return defaultValue;
        }

        #endregion
    }
}
