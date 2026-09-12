using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using Terraria;
using TerrariaModder.Core;
using TerrariaModder.Core.Config;
using TerrariaModder.Core.Debug;
using TerrariaModder.Core.Input;
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

                    case "/api/state/tiles/raw":
                        {
                            int tx = 0, ty = 0, tw = 20, th = 20;
                            if (request.QueryString["x"] != null) int.TryParse(request.QueryString["x"], out tx);
                            if (request.QueryString["y"] != null) int.TryParse(request.QueryString["y"], out ty);
                            if (request.QueryString["w"] != null) int.TryParse(request.QueryString["w"], out tw);
                            if (request.QueryString["h"] != null) int.TryParse(request.QueryString["h"], out th);
                            bool centered = request.QueryString["x"] == null;
                            json = GameSenseState.GetTilesRaw(tx, ty, tw, th, centered);
                        }
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

                    case "/api/menu/join_world":
                        if (method != "POST")
                        {
                            SendError(response, 405, "Method not allowed. Use POST.");
                            return;
                        }
                        json = HandleJoinWorld(ReadRequestBody(request), out statusCode);
                        break;

                    case "/api/menu/exit_world":
                        if (method != "POST")
                        {
                            SendError(response, 405, "Method not allowed. Use POST.");
                            return;
                        }
                        json = HandleExitWorld(out statusCode);
                        break;

                    case "/api/menu/wait":
                        if (method != "POST")
                        {
                            SendError(response, 405, "Method not allowed. Use POST.");
                            return;
                        }
                        json = HandleMenuWait(ReadRequestBody(request), out statusCode);
                        break;

                    case "/api/menu/create_world":
                        if (method != "POST")
                        {
                            SendError(response, 405, "Method not allowed. Use POST.");
                            return;
                        }
                        json = HandleCreateWorld(ReadRequestBody(request), out statusCode);
                        break;

                    case "/api/menu/delete_world":
                        if (method != "POST")
                        {
                            SendError(response, 405, "Method not allowed. Use POST.");
                            return;
                        }
                        json = HandleDeleteWorld(ReadRequestBody(request), out statusCode);
                        break;

                    case "/api/save":
                        if (method != "POST")
                        {
                            SendError(response, 405, "Method not allowed. Use POST.");
                            return;
                        }
                        json = HandleSave(out statusCode);
                        break;

                    case "/api/tiles/set":
                        if (method != "POST")
                        {
                            SendError(response, 405, "Method not allowed. Use POST.");
                            return;
                        }
                        json = HandleTileSet(ReadRequestBody(request), out statusCode);
                        break;

                    case "/api/tiles/kill":
                        if (method != "POST")
                        {
                            SendError(response, 405, "Method not allowed. Use POST.");
                            return;
                        }
                        json = HandleTileKill(ReadRequestBody(request), out statusCode);
                        break;

                    case "/api/npcs":
                        json = HandleNpcList();
                        break;

                    case "/api/npcs/kill":
                        if (method != "POST")
                        {
                            SendError(response, 405, "Method not allowed. Use POST.");
                            return;
                        }
                        json = HandleNpcKill(ReadRequestBody(request), out statusCode);
                        break;

                    case "/api/snapshot/save":
                        if (method != "POST")
                        {
                            SendError(response, 405, "Method not allowed. Use POST.");
                            return;
                        }
                        json = HandleSnapshotSave(ReadRequestBody(request), out statusCode);
                        break;

                    case "/api/snapshot/restore":
                        if (method != "POST")
                        {
                            SendError(response, 405, "Method not allowed. Use POST.");
                            return;
                        }
                        json = HandleSnapshotRestore(ReadRequestBody(request), out statusCode);
                        break;

                    case "/api/snapshot/list":
                        json = HandleSnapshotList();
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

                    // ── Keybind endpoints ──────────────────────────────────────
                    case "/api/keybinds":
                        json = HandleKeybindList();
                        break;

                    case "/api/keybind":
                        if (method != "POST")
                        {
                            SendError(response, 405, "Method not allowed. Use POST.");
                            return;
                        }
                        json = HandleKeybindTrigger(ReadRequestBody(request), out statusCode);
                        break;

                    // ── Screenshot endpoint ────────────────────────────────────
                    case "/api/screenshot":
                    {
                        var screenshot = HandleScreenshot(request);
                        if (screenshot.Error != null)
                        {
                            SendError(response, 500, screenshot.Error);
                        }
                        else
                        {
                            SendBinary(response, 200, screenshot.ContentType, screenshot.Data);
                        }
                        return; // Already sent response
                    }

                    // ── World progression ──────────────────────────────────────
                    case "/api/world/progression":
                        json = HandleWorldProgression();
                        break;

                    // ── Chest contents ─────────────────────────────────────────
                    case "/api/state/chests":
                        json = HandleChestList();
                        break;

                    case "/api/painting-chests":
                        json = HandlePaintingChests(method == "POST" ? ReadRequestBody(request) : null, method, out statusCode);
                        break;

                    case "/api/tiles/setframe":
                        if (method != "POST") { SendError(response, 405, "Use POST."); return; }
                        json = HandleTileSetFrame(ReadRequestBody(request), out statusCode);
                        break;

                    // ── Player actions ─────────────────────────────────────────
                    case "/api/player/give":
                        if (method != "POST") { SendError(response, 405, "Use POST."); return; }
                        json = HandlePlayerGive(ReadRequestBody(request), out statusCode);
                        break;

                    case "/api/player/teleport":
                        if (method != "POST") { SendError(response, 405, "Use POST."); return; }
                        json = HandlePlayerTeleport(ReadRequestBody(request), out statusCode);
                        break;

                    case "/api/player/buff":
                        if (method != "POST") { SendError(response, 405, "Use POST."); return; }
                        json = HandlePlayerBuff(ReadRequestBody(request), out statusCode);
                        break;

                    // ── NPC spawning ──────────────────────────────────────────
                    case "/api/spawn/npc":
                        if (method != "POST") { SendError(response, 405, "Use POST."); return; }
                        json = HandleSpawnNpc(ReadRequestBody(request), out statusCode);
                        break;

                    // ── Chat ──────────────────────────────────────────────────
                    case "/api/chat/send":
                        if (method != "POST") { SendError(response, 405, "Use POST."); return; }
                        json = HandleChatSend(ReadRequestBody(request), out statusCode);
                        break;

                    // ── Events ───────────────────────────────────────────────
                    case "/api/events":
                        json = HandleEvents(request);
                        break;

                    // ── Inventory & Equipment Control ────────────────────────
                    case "/api/inventory/set":
                        if (method != "POST") { SendError(response, 405, "Use POST."); return; }
                        json = HandleInventorySet(ReadRequestBody(request), out statusCode);
                        break;

                    case "/api/equip":
                        if (method != "POST") { SendError(response, 405, "Use POST."); return; }
                        json = HandleEquip(ReadRequestBody(request), out statusCode);
                        break;

                    case "/api/hotbar/select":
                        if (method != "POST") { SendError(response, 405, "Use POST."); return; }
                        json = HandleHotbarSelect(ReadRequestBody(request), out statusCode);
                        break;

                    case "/api/chest/set":
                        if (method != "POST") { SendError(response, 405, "Use POST."); return; }
                        json = HandleChestSet(ReadRequestBody(request), out statusCode);
                        break;

                    case "/api/chest/open":
                        if (method != "POST") { SendError(response, 405, "Use POST."); return; }
                        json = HandleChestOpen(ReadRequestBody(request), out statusCode);
                        break;

                    case "/api/world2screen":
                        // Convert tile coords to screen coords for clicking
                        {
                            int wx = 0, wy = 0;
                            if (request.QueryString["x"] != null) int.TryParse(request.QueryString["x"], out wx);
                            if (request.QueryString["y"] != null) int.TryParse(request.QueryString["y"], out wy);
                            json = MainThreadDispatcher.RunOnMainThread(() =>
                            {
                                if (!Game.InWorld) return JsonObject(JsonPair("error", "Not in a world"));
                                float worldPx = wx * 16f + 8f; // tile center
                                float worldPy = wy * 16f + 8f;
                                float scrX = worldPx - Main.screenPosition.X;
                                float scrY = worldPy - Main.screenPosition.Y;
                                return JsonObject(
                                    JsonPair("tileX", wx), JsonPair("tileY", wy),
                                    JsonPair("screenX", (int)scrX), JsonPair("screenY", (int)scrY),
                                    JsonPair("onScreen", scrX >= 0 && scrX < Main.screenWidth && scrY >= 0 && scrY < Main.screenHeight));
                            });
                        }
                        break;

                    case "/api/chest/close":
                        if (method != "POST") { SendError(response, 405, "Use POST."); return; }
                        json = MainThreadDispatcher.RunOnMainThread(() =>
                        {
                            if (!Game.InWorld) return JsonObject(JsonPair("error", "Not in a world"));
                            if (Main.LocalPlayer.chest >= 0)
                            {
                                int was = Main.LocalPlayer.chest;
                                Main.LocalPlayer.chest = -1;
                                Main.npcChatText = "";
                                return JsonObject(JsonPair("success", true), JsonPair("closed", was));
                            }
                            return JsonObject(JsonPair("success", true), JsonPair("message", "No chest was open"));
                        });
                        break;

                    // ── World Control & Spatial ──────────────────────────────
                    case "/api/teleport":
                        if (method != "POST") { SendError(response, 405, "Use POST."); return; }
                        json = HandleTeleportXY(ReadRequestBody(request), out statusCode);
                        break;

                    case "/api/projectiles":
                        json = HandleProjectiles(request);
                        break;

                    case "/api/world/set":
                        if (method != "POST") { SendError(response, 405, "Use POST."); return; }
                        json = HandleWorldSet(ReadRequestBody(request), out statusCode);
                        break;

                    case "/api/progression/set":
                        if (method != "POST") { SendError(response, 405, "Use POST."); return; }
                        json = HandleProgressionSet(ReadRequestBody(request), out statusCode);
                        break;

                    case "/api/event":
                        if (method != "POST") { SendError(response, 405, "Use POST."); return; }
                        json = HandleEventTrigger(ReadRequestBody(request), out statusCode);
                        break;

                    case "/api/tiles/fill":
                        if (method != "POST") { SendError(response, 405, "Use POST."); return; }
                        json = HandleTilesFill(ReadRequestBody(request), out statusCode);
                        break;

                    case "/api/npcs/set_position":
                        if (method != "POST") { SendError(response, 405, "Use POST."); return; }
                        json = HandleNpcSetPosition(ReadRequestBody(request), out statusCode);
                        break;

                    // ── Runtime Introspection ────────────────────────────────
                    case "/api/reflect/type":
                        json = RuntimeIntrospection.HandleReflectType(request);
                        break;

                    case "/api/reflect/field":
                        if (method == "POST")
                            json = RuntimeIntrospection.HandleReflectFieldSet(ReadRequestBody(request), out statusCode);
                        else
                            json = RuntimeIntrospection.HandleReflectFieldGet(request);
                        break;

                    case "/api/reflect/instance":
                        json = RuntimeIntrospection.HandleReflectInstance(request);
                        break;

                    case "/api/eval":
                        json = RuntimeIntrospection.HandleEval(request);
                        break;

                    case "/api/trace/add":
                        if (method != "POST") { SendError(response, 405, "Use POST."); return; }
                        json = RuntimeIntrospection.HandleTraceAdd(ReadRequestBody(request), out statusCode);
                        break;

                    case "/api/trace/remove":
                        if (method != "POST") { SendError(response, 405, "Use POST."); return; }
                        json = RuntimeIntrospection.HandleTraceRemove(ReadRequestBody(request), out statusCode);
                        break;

                    case "/api/trace/log":
                        json = RuntimeIntrospection.HandleTraceLog(request);
                        break;

                    case "/api/watch/add":
                        if (method != "POST") { SendError(response, 405, "Use POST."); return; }
                        json = RuntimeIntrospection.HandleWatchAdd(ReadRequestBody(request), out statusCode);
                        break;

                    case "/api/watch/remove":
                        if (method != "POST") { SendError(response, 405, "Use POST."); return; }
                        json = RuntimeIntrospection.HandleWatchRemove(ReadRequestBody(request), out statusCode);
                        break;

                    case "/api/watch/log":
                        json = RuntimeIntrospection.HandleWatchLog(request);
                        break;

                    // ── Capabilities (discovery) ────────────────────────────
                    case "/api/capabilities":
                        json = HandleCapabilities();
                        break;

                    // ── Mod actions (universal dispatch) ────────────────────
                    case "/api/mod-action":
                        if (method != "POST") { SendError(response, 405, "Use POST."); return; }
                        json = HandleModAction(ReadRequestBody(request), out statusCode);
                        break;

                    // ── Logs ──────────────────────────────────────────────────
                    case "/api/logs":
                        json = HandleLogs(request);
                        break;

                    // ── Diagnostics ───────────────────────────────────────────
                    case "/api/diagnostics":
                        json = HandleDiagnostics();
                        break;

                    // ── Health ─────────────────────────────────────────────────
                    case "/api/health":
                        json = HandleHealth();
                        break;

                    case "/api/harmony":
                        json = HandleHarmony();
                        break;

                    case "/api/net/stats":
                        json = HandleNetStats();
                        break;

                    // ── Mod state ─────────────────────────────────────────────

                    default:
                        if (path.StartsWith("/api/state/chest") && path != "/api/state/chests")
                        {
                            // /api/state/chest?index=N  or  /api/state/chest/N
                            int chestIdx = -1;
                            string qIdx = request.QueryString["index"];
                            if (qIdx != null) int.TryParse(qIdx, out chestIdx);
                            else
                            {
                                string suffix = path.Substring("/api/state/chest".Length).TrimStart('/');
                                if (!string.IsNullOrEmpty(suffix)) int.TryParse(suffix, out chestIdx);
                            }
                            json = HandleChestContents(chestIdx, out statusCode);
                        }
                        else if (path.StartsWith("/api/mods/") && path.EndsWith("/state"))
                        {
                            string modId = path.Substring("/api/mods/".Length);
                            modId = modId.Substring(0, modId.Length - "/state".Length);
                            json = HandleModState(modId, out statusCode);
                        }
                        else if (path == "/api/config")
                        {
                            json = HandleConfigList();
                        }
                        else if (path.StartsWith("/api/config/"))
                        {
                            string rest = path.Substring("/api/config/".Length);
                            int slashIdx = rest.IndexOf('/');
                            string modId = slashIdx < 0 ? rest : rest.Substring(0, slashIdx);
                            string action = slashIdx < 0 ? "" : rest.Substring(slashIdx + 1);
                            switch (action)
                            {
                                case "":
                                    json = HandleConfigGet(modId);
                                    break;
                                case "set":
                                    if (method != "POST") { SendError(response, 405, "Method not allowed. Use POST."); return; }
                                    json = HandleConfigSet(modId, ReadRequestBody(request));
                                    break;
                                case "reload":
                                    if (method != "POST") { SendError(response, 405, "Method not allowed. Use POST."); return; }
                                    json = HandleConfigReload(modId);
                                    break;
                                case "reset":
                                    if (method != "POST") { SendError(response, 405, "Method not allowed. Use POST."); return; }
                                    json = HandleConfigReset(modId);
                                    break;
                                default:
                                    SendError(response, 404, $"Not found: {path}");
                                    return;
                            }
                        }
                        else
                        {
                            SendError(response, 404, $"Not found: {path}");
                            return;
                        }
                        break;
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

                // Build buff array
                var buffSb = new StringBuilder();
                buffSb.Append("[");
                bool firstBuff = true;
                try
                {
                    for (int i = 0; i < player.buffType.Length; i++)
                    {
                        if (player.buffType[i] == 0) continue;
                        if (!firstBuff) buffSb.Append(",");
                        firstBuff = false;
                        string buffName = "";
                        try { buffName = Terraria.Lang.GetBuffName(player.buffType[i]); } catch { }
                        buffSb.Append("{\"id\":").Append(player.buffType[i]);
                        buffSb.Append(",\"name\":\"").Append(EscapeJson(buffName)).Append("\"");
                        buffSb.Append(",\"time\":").Append(player.buffTime[i] / 60).Append("}");
                    }
                }
                catch { }
                buffSb.Append("]");

                // Build zones object
                var zonesSb = new StringBuilder();
                zonesSb.Append("{");
                try
                {
                    zonesSb.Append("\"dungeon\":").Append(player.ZoneDungeon ? "true" : "false");
                    zonesSb.Append(",\"corrupt\":").Append(player.ZoneCorrupt ? "true" : "false");
                    zonesSb.Append(",\"hallow\":").Append(player.ZoneHallow ? "true" : "false");
                    zonesSb.Append(",\"meteor\":").Append(player.ZoneMeteor ? "true" : "false");
                    zonesSb.Append(",\"jungle\":").Append(player.ZoneJungle ? "true" : "false");
                    zonesSb.Append(",\"snow\":").Append(player.ZoneSnow ? "true" : "false");
                    zonesSb.Append(",\"crimson\":").Append(player.ZoneCrimson ? "true" : "false");
                    zonesSb.Append(",\"desert\":").Append(player.ZoneDesert ? "true" : "false");
                    zonesSb.Append(",\"glowshroom\":").Append(player.ZoneGlowshroom ? "true" : "false");
                    zonesSb.Append(",\"undergroundDesert\":").Append(player.ZoneUndergroundDesert ? "true" : "false");
                    zonesSb.Append(",\"beach\":").Append(player.ZoneBeach ? "true" : "false");
                    zonesSb.Append(",\"rain\":").Append(player.ZoneRain ? "true" : "false");
                    zonesSb.Append(",\"sandstorm\":").Append(player.ZoneSandstorm ? "true" : "false");
                    zonesSb.Append(",\"granite\":").Append(player.ZoneGranite ? "true" : "false");
                    zonesSb.Append(",\"marble\":").Append(player.ZoneMarble ? "true" : "false");
                    zonesSb.Append(",\"hive\":").Append(player.ZoneHive ? "true" : "false");
                    zonesSb.Append(",\"gemCave\":").Append(player.ZoneGemCave ? "true" : "false");
                    zonesSb.Append(",\"lihzhardTemple\":").Append(player.ZoneLihzhardTemple ? "true" : "false");
                    zonesSb.Append(",\"graveyard\":").Append(player.ZoneGraveyard ? "true" : "false");
                    zonesSb.Append(",\"shimmer\":").Append(player.ZoneShimmer ? "true" : "false");
                    zonesSb.Append(",\"skyHeight\":").Append(player.ZoneSkyHeight ? "true" : "false");
                    zonesSb.Append(",\"overworldHeight\":").Append(player.ZoneOverworldHeight ? "true" : "false");
                    zonesSb.Append(",\"dirtLayerHeight\":").Append(player.ZoneDirtLayerHeight ? "true" : "false");
                    zonesSb.Append(",\"rockLayerHeight\":").Append(player.ZoneRockLayerHeight ? "true" : "false");
                    zonesSb.Append(",\"underworldHeight\":").Append(player.ZoneUnderworldHeight ? "true" : "false");
                }
                catch { }
                zonesSb.Append("}");

                return JsonObject(
                    JsonPair("inWorld", true),
                    JsonPair("name", player.name ?? ""),
                    JsonPair("health", Game.PlayerHealth),
                    JsonPair("maxHealth", Game.PlayerMaxHealth),
                    JsonPair("mana", Game.PlayerMana),
                    JsonPair("maxMana", Game.PlayerMaxMana),
                    JsonPair("dead", Game.PlayerDead),
                    JsonPair("respawnTimer", player.respawnTimer),
                    JsonPair("direction", player.direction),
                    JsonPair("velocityX", (double)player.velocity.X),
                    JsonPair("velocityY", (double)player.velocity.Y),
                    JsonPair("positionX", (double)pos.X),
                    JsonPair("positionY", (double)pos.Y),
                    JsonPair("spawnX", player.SpawnX),
                    JsonPair("spawnY", player.SpawnY),
                    JsonPair("selectedItem", Game.SelectedItem),
                    "\"buffs\": " + buffSb.ToString(),
                    "\"zones\": " + zonesSb.ToString()
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

            // Route trigger_keybind to the keybind handler (advertised as core action in capabilities)
            if (name == "trigger_keybind")
            {
                int sc;
                string keybindBody = "{\"id\":\"" + (ExtractJsonString(body, "id") ?? "") + "\"}";
                string result = HandleKeybindTrigger(keybindBody, out sc);
                return result;
            }

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

        /// <summary>Maximum timeout for blocking menu operations (5 minutes).</summary>
        private const int MaxMenuTimeoutMs = 300_000;

        private string HandleExitWorld(out int statusCode)
        {
            if (Main.gameMenu)
            {
                statusCode = 400;
                return JsonObject(JsonPair("success", false), JsonPair("message", "Not in world"));
            }

            _log.Info("[DebugHttpServer] ExitWorld: calling WorldGen.SaveAndQuit...");
            try
            {
                WorldGen.SaveAndQuit();
            }
            catch (Exception ex)
            {
                _log.Warn($"[DebugHttpServer] ExitWorld: SaveAndQuit threw: {ex.Message}");
                statusCode = 500;
                return JsonObject(JsonPair("success", false), JsonPair("message", $"SaveAndQuit error: {ex.Message}"));
            }

            // Wait up to 15s for world exit
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (!Main.gameMenu && sw.ElapsedMilliseconds < 15000)
                System.Threading.Thread.Sleep(100);

            if (!Main.gameMenu)
            {
                statusCode = 408;
                return JsonObject(JsonPair("success", false), JsonPair("message", "Timeout waiting for world exit"));
            }

            // Brief pause for title screen to stabilize
            System.Threading.Thread.Sleep(500);
            _log.Info("[DebugHttpServer] ExitWorld: world exited successfully");
            statusCode = 200;
            return JsonObject(JsonPair("success", true), JsonPair("message", "World saved and exited"));
        }

        private string HandleJoinWorld(string body, out int statusCode)
        {
            string ip = ExtractJsonString(body, "ip") ?? "127.0.0.1";
            int character = ExtractJsonInt(body, "character", 0);
            int timeout = ExtractJsonInt(body, "timeout", 30000);
            if (timeout > MaxMenuTimeoutMs) timeout = MaxMenuTimeoutMs;
            if (timeout <= 0) timeout = 30000;

            var result = _menuNav.JoinWorld(ip, character, timeout);
            statusCode = result.Success ? 200 : 400;
            return NavigationResultToJson(result);
        }

        private string HandleEnterWorld(string body, out int statusCode)
        {
            int character = ExtractJsonInt(body, "character", 0);
            int world = ExtractJsonInt(body, "world", 0);
            int timeout = ExtractJsonInt(body, "timeout", 30000);
            bool multiplayer = ExtractJsonBool(body, "multiplayer", false);
            if (timeout > MaxMenuTimeoutMs) timeout = MaxMenuTimeoutMs;
            if (timeout <= 0) timeout = 30000;

            var result = _menuNav.EnterWorld(character, world, timeout, multiplayer);
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

        private string HandleCreateWorld(string body, out int statusCode)
        {
            statusCode = 200;
            try
            {
                // Must be in the game menu
                if (!Terraria.Main.gameMenu)
                {
                    statusCode = 400;
                    return JsonObject(JsonPair("success", false), JsonPair("error", "Must be in the game menu to create a world. Exit the current world first."));
                }

                string name = ExtractJsonString(body, "name");
                if (string.IsNullOrEmpty(name)) name = "TestWorld_" + new System.Random().Next(1000, 9999);

                string seed = ExtractJsonString(body, "seed");
                string size = ExtractJsonString(body, "size");
                string difficulty = ExtractJsonString(body, "difficulty");
                string evil = ExtractJsonString(body, "evil");

                // Map size string to dimensions
                int sizeX = 4200, sizeY = 1200; // small default
                if (!string.IsNullOrEmpty(size))
                {
                    switch (size.ToLower())
                    {
                        case "medium": sizeX = 6400; sizeY = 1800; break;
                        case "large": sizeX = 8400; sizeY = 2400; break;
                    }
                }

                // Map difficulty string to GameMode int
                int gameMode = 0; // classic
                if (!string.IsNullOrEmpty(difficulty))
                {
                    switch (difficulty.ToLower())
                    {
                        case "expert": gameMode = 1; break;
                        case "master": gameMode = 2; break;
                        case "journey": gameMode = 3; break;
                    }
                }

                // Determine evil type (0=random, 1=corruption, 2=crimson)
                int evilType = 0;
                if (!string.IsNullOrEmpty(evil))
                {
                    switch (evil.ToLower())
                    {
                        case "corruption": evilType = 1; break;
                        case "crimson": evilType = 2; break;
                    }
                }

                MainThreadDispatcher.RunOnMainThreadAndWait(() =>
                {
                    try
                    {
                        // Set up world file data
                        string savePath = Terraria.Main.WorldPath;
                        string worldPath = System.IO.Path.Combine(savePath, name + ".wld");

                        var wfdType = typeof(Terraria.IO.WorldFileData);
                        var wfd = new Terraria.IO.WorldFileData(worldPath, false);
                        wfd.Name = name;
                        wfd.SetWorldSize(sizeX, sizeY);
                        wfd.GameMode = gameMode;

                        if (!string.IsNullOrEmpty(seed))
                            wfd.SetSeed(seed);
                        else
                            wfd.SetSeedToRandom();

                        // Set evil type
                        if (evilType == 2) wfd.HasCrimson = true;
                        else if (evilType == 1) wfd.HasCorruption = true;
                        // evilType 0 = random (default)

                        // Set as active world
                        Terraria.Main.ActiveWorldFileData = wfd;

                        // Set Main globals that worldgen reads
                        Terraria.Main.maxTilesX = sizeX;
                        Terraria.Main.maxTilesY = sizeY;
                        Terraria.Main.GameMode = gameMode;

                        // Set evil param via reflection
                        var worldGenType = typeof(Terraria.WorldGen);
                        var evilField = worldGenType.GetField("WorldGenParam_Evil",
                            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                        if (evilField != null) evilField.SetValue(null, evilType);

                        // Start world generation
                        Terraria.WorldGen.CreateNewWorld();
                    }
                    catch (Exception ex)
                    {
                        _log.Error($"[CreateWorld] Failed: {ex.Message}");
                    }
                });

                statusCode = 200;
                return JsonObject(
                    JsonPair("success", true),
                    JsonPair("status", "generating"),
                    JsonPair("name", name),
                    JsonPair("message", "World generation started. Use wait_for condition 'in_menu' or poll menu/state to check completion.")
                );
            }
            catch (Exception ex)
            {
                statusCode = 500;
                return JsonObject(JsonPair("success", false), JsonPair("error", ex.Message));
            }
        }

        private string HandleDeleteWorld(string body, out int statusCode)
        {
            statusCode = 200;
            try
            {
                if (!Terraria.Main.gameMenu)
                {
                    statusCode = 400;
                    return JsonObject(JsonPair("success", false), JsonPair("error", "Must be in the game menu to delete a world."));
                }

                int index = ExtractJsonInt(body, "index", -1);
                string name = ExtractJsonString(body, "name");

                string deletedName = MainThreadDispatcher.RunOnMainThread(() =>
                {
                    try
                    {
                        // Refresh world list from disk before searching
                        Terraria.Main.LoadWorlds();
                        var worldList = Terraria.Main.WorldList;
                        if (worldList == null || worldList.Count == 0)
                            return "ERROR:No worlds found";

                        // If name provided, find matching index
                        if (index < 0 && !string.IsNullOrEmpty(name))
                        {
                            for (int i = 0; i < worldList.Count; i++)
                            {
                                if (worldList[i].Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                                {
                                    index = i;
                                    break;
                                }
                            }
                            if (index < 0) return $"ERROR:World '{name}' not found";
                        }

                        if (index < 0 || index >= worldList.Count)
                            return $"ERROR:Invalid index {index}. {worldList.Count} worlds available.";

                        string worldName = worldList[index].Name;

                        // Main.EraseWorld is private — use reflection
                        var eraseMethod = typeof(Terraria.Main).GetMethod("EraseWorld",
                            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                        if (eraseMethod == null)
                            return "ERROR:Could not find Main.EraseWorld via reflection";

                        eraseMethod.Invoke(null, new object[] { index });
                        return worldName;
                    }
                    catch (Exception ex)
                    {
                        return $"ERROR:{ex.Message}";
                    }
                });

                if (deletedName.StartsWith("ERROR:"))
                {
                    statusCode = 400;
                    return JsonObject(JsonPair("success", false), JsonPair("error", deletedName.Substring(6)));
                }

                return JsonObject(JsonPair("success", true), JsonPair("deleted", deletedName));
            }
            catch (Exception ex)
            {
                statusCode = 500;
                return JsonObject(JsonPair("success", false), JsonPair("error", ex.Message));
            }
        }

        private string HandleSave(out int statusCode)
        {
            statusCode = 200;
            try
            {
                if (Terraria.Main.gameMenu)
                {
                    statusCode = 400;
                    return JsonObject(JsonPair("success", false), JsonPair("error", "Not in a world. Nothing to save."));
                }

                MainThreadDispatcher.RunOnMainThreadAndWait(() =>
                {
                    try
                    {
                        Terraria.Player.SavePlayer(Terraria.Main.ActivePlayerFileData, false);
                    }
                    catch (Exception ex)
                    {
                        _log.Error($"[Save] Failed: {ex.Message}");
                    }
                });

                return JsonObject(JsonPair("success", true), JsonPair("message", "Player saved."));
            }
            catch (Exception ex)
            {
                statusCode = 500;
                return JsonObject(JsonPair("success", false), JsonPair("error", ex.Message));
            }
        }

        #endregion

        #region Tiles, NPCs, Snapshots

        /// <summary>
        /// POST /api/tiles/setframe — set frameX/frameY on a tile directly.
        /// Body: {"x": N, "y": N, "frameX": N, "frameY": N}
        /// Also supports: {"x": N, "y": N, "type": N, "frameX": N, "frameY": N, "createChest": true}
        /// for placing chest tiles with specific frame values and creating chest entries.
        /// </summary>
        private string HandleTileSetFrame(string body, out int statusCode)
        {
            statusCode = 200;
            try
            {
                if (!Game.InWorld) { statusCode = 400; return JsonObject(JsonPair("error", "Not in a world")); }

                int x = ExtractJsonInt(body, "x", -1);
                int y = ExtractJsonInt(body, "y", -1);
                int frameX = ExtractJsonInt(body, "frameX", -1);
                int frameY = ExtractJsonInt(body, "frameY", -1);
                int type = ExtractJsonInt(body, "type", -1);
                bool createChest = body.Contains("\"createChest\"") && body.Contains("true");
                string name = ExtractJsonString(body, "name");

                if (x < 0 || y < 0) { statusCode = 400; return JsonObject(JsonPair("error", "x and y required")); }

                string result = MainThreadDispatcher.RunOnMainThread(() =>
                {
                    var tile = Main.tile[x, y];
                    if (tile == null) return "error:Tile is null";

                    if (type >= 0)
                    {
                        tile.active(true);
                        tile.type = (ushort)type;
                    }
                    if (frameX >= 0) tile.frameX = (short)frameX;
                    if (frameY >= 0) tile.frameY = (short)frameY;

                    if (createChest)
                    {
                        int chestIdx = Chest.CreateChest(x, y, -1);
                        if (chestIdx >= 0)
                        {
                            var chest = Main.chest[chestIdx];
                            if (chest != null && !string.IsNullOrEmpty(name))
                                chest.name = name;
                            return $"ok:chest={chestIdx}";
                        }
                        return "ok:no_chest";
                    }
                    return "ok";
                });

                if (result.StartsWith("error:")) { statusCode = 400; return JsonObject(JsonPair("error", result.Substring(6))); }
                return JsonObject(JsonPair("success", true), JsonPair("result", result));
            }
            catch (Exception ex) { statusCode = 500; return JsonObject(JsonPair("error", ex.Message)); }
        }

        private string HandleTileSet(string body, out int statusCode)
        {
            statusCode = 200;
            try
            {
                if (Terraria.Main.gameMenu)
                {
                    statusCode = 400;
                    return JsonObject(JsonPair("success", false), JsonPair("error", "Not in a world."));
                }

                int x = ExtractJsonInt(body, "x", -1);
                int y = ExtractJsonInt(body, "y", -1);
                int type = ExtractJsonInt(body, "type", -1);
                int wall = ExtractJsonInt(body, "wall", -1);
                string action = ExtractJsonString(body, "action");
                string liquid = ExtractJsonString(body, "liquid");
                int amount = ExtractJsonInt(body, "amount", 255);

                if (x < 0 || y < 0 || x >= Terraria.Main.maxTilesX || y >= Terraria.Main.maxTilesY)
                {
                    statusCode = 400;
                    return JsonObject(JsonPair("success", false), JsonPair("error", "Missing or out-of-bounds x/y coordinates."));
                }

                string result = MainThreadDispatcher.RunOnMainThread(() =>
                {
                    try
                    {
                        if (liquid != null)
                        {
                            if (liquid == "remove")
                            {
                                Terraria.Main.tile[x, y].liquid = 0;
                                Terraria.Liquid.AddWater(x, y);
                                if (Terraria.Main.netMode != 0)
                                    Terraria.NetMessage.sendWater(x, y);
                                return "OK:Liquid removed";
                            }
                            byte liqType = 0;
                            switch (liquid)
                            {
                                case "water": liqType = 0; break;
                                case "lava": liqType = 1; break;
                                case "honey": liqType = 2; break;
                                case "shimmer": liqType = 3; break;
                                default: return $"FAIL:Unknown liquid type '{liquid}'";
                            }
                            bool placed = Terraria.WorldGen.PlaceLiquid(x, y, liqType, (byte)amount);
                            if (Terraria.Main.netMode != 0)
                                Terraria.NetMessage.sendWater(x, y);
                            return placed ? $"OK:Liquid {liquid} placed" : $"FAIL:PlaceLiquid returned false";
                        }
                        else if (action == "remove")
                        {
                            Terraria.WorldGen.KillTile(x, y, false, false, false);
                            if (Terraria.Main.netMode != 0)
                                Terraria.NetMessage.SendData(17, -1, -1, null, 0, x, y);
                            return "OK:Tile removed";
                        }
                        else if (action == "remove_wall")
                        {
                            Terraria.WorldGen.KillWall(x, y, false);
                            if (Terraria.Main.netMode != 0)
                                Terraria.NetMessage.SendData(17, -1, -1, null, 2, x, y);
                            return "OK:Wall removed";
                        }
                        else if (wall >= 0)
                        {
                            Terraria.WorldGen.PlaceWall(x, y, wall, false);
                            if (Terraria.Main.netMode != 0)
                                Terraria.NetMessage.SendData(17, -1, -1, null, 3, x, y, wall);
                            return $"OK:Wall {wall} placed";
                        }
                        else if (type >= 0)
                        {
                            bool placed = Terraria.WorldGen.PlaceTile(x, y, type, false, true);
                            if (placed && Terraria.Main.netMode != 0)
                                Terraria.NetMessage.SendData(17, -1, -1, null, 1, x, y, type);
                            return placed ? $"OK:Tile {type} placed" : "FAIL:PlaceTile returned false";
                        }
                        else
                        {
                            return "FAIL:Specify type, wall, or action";
                        }
                    }
                    catch (Exception ex)
                    {
                        return $"FAIL:{ex.Message}";
                    }
                });

                if (result.StartsWith("FAIL:"))
                {
                    statusCode = 400;
                    return JsonObject(JsonPair("success", false), JsonPair("error", result.Substring(5)));
                }
                return JsonObject(JsonPair("success", true), JsonPair("message", result.Substring(3)));
            }
            catch (Exception ex)
            {
                statusCode = 500;
                return JsonObject(JsonPair("success", false), JsonPair("error", ex.Message));
            }
        }

        /// <summary>
        /// POST /api/tiles/kill — call WorldGen.KillTile to simulate mining a tile.
        /// Body: {"x": N, "y": N, "fail": false}
        /// Returns: tile type before kill, whether it was removed, and any dropped items.
        /// </summary>
        private string HandleTileKill(string body, out int statusCode)
        {
            statusCode = 200;
            try
            {
                if (!Game.InWorld) { statusCode = 400; return JsonObject(JsonPair("error", "Not in a world")); }

                int x = ExtractJsonInt(body, "x", -1);
                int y = ExtractJsonInt(body, "y", -1);
                bool fail = body != null && body.Contains("\"fail\"") && body.Contains("true");

                if (x < 0 || y < 0) { statusCode = 400; return JsonObject(JsonPair("error", "x and y required")); }

                string result = MainThreadDispatcher.RunOnMainThread(() =>
                {
                    var tile = Main.tile[x, y];
                    if (tile == null) return "error:Tile is null";
                    if (!tile.active()) return "error:No active tile at position";

                    int typeBefore = tile.type;
                    int frameXBefore = tile.frameX;

                    // Count items before kill (to detect drops)
                    int itemCountBefore = 0;
                    for (int i = 0; i < Main.maxItems; i++)
                        if (Main.item[i] != null && Main.item[i].active) itemCountBefore++;

                    WorldGen.KillTile(x, y, fail, false, false);

                    bool removed = !tile.active() || tile.type != typeBefore;

                    // Find newly dropped items
                    var dropped = new System.Collections.Generic.List<string>();
                    for (int i = 0; i < Main.maxItems; i++)
                    {
                        if (Main.item[i] != null && Main.item[i].active && Main.item[i].type > 0)
                        {
                            // Check if item is near the tile
                            float dx = Main.item[i].position.X - x * 16;
                            float dy = Main.item[i].position.Y - y * 16;
                            if (Math.Abs(dx) < 64 && Math.Abs(dy) < 64)
                            {
                                dropped.Add($"{{\"type\":{Main.item[i].type},\"stack\":{Main.item[i].stack},\"name\":\"{Main.item[i].Name}\"}}");
                            }
                        }
                    }

                    if (Main.netMode != 0)
                        NetMessage.SendTileSquare(-1, x, y, 3);

                    return $"ok:{typeBefore}:{frameXBefore}:{(removed ? "removed" : "intact")}:{string.Join(",", dropped)}";
                });

                if (result.StartsWith("error:"))
                { statusCode = 400; return JsonObject(JsonPair("error", result.Substring(6))); }

                var parts = result.Substring(3).Split(':');
                int tileType = int.Parse(parts[0]);
                int frameX = int.Parse(parts[1]);
                string state = parts[2];
                string droppedItems = parts.Length > 3 ? parts[3] : "";

                return JsonObject(
                    JsonPair("success", true),
                    JsonPair("tileType", tileType),
                    JsonPair("frameX", frameX),
                    JsonPair("removed", state == "removed"),
                    "\"droppedItems\": [" + droppedItems + "]"
                );
            }
            catch (Exception ex)
            {
                statusCode = 500;
                return JsonObject(JsonPair("success", false), JsonPair("error", ex.Message));
            }
        }

        private string HandleNpcList()
        {
            try
            {
                if (Terraria.Main.gameMenu)
                    return JsonObject(JsonPair("error", "Not in a world."));

                var sb = new System.Text.StringBuilder(4096);
                sb.Append("{\"npcs\": [");
                bool first = true;

                for (int i = 0; i < Terraria.Main.maxNPCs; i++)
                {
                    try
                    {
                        var npc = Terraria.Main.npc[i];
                        if (npc == null || !npc.active) continue;

                        if (!first) sb.Append(",");
                        first = false;

                        sb.Append("{\"index\":").Append(i);
                        sb.Append(",\"type\":").Append(npc.type);
                        sb.Append(",\"name\":\"").Append(EscapeJson(npc.GivenOrTypeName)).Append("\"");
                        sb.Append(",\"life\":").Append(npc.life);
                        sb.Append(",\"lifeMax\":").Append(npc.lifeMax);
                        sb.Append(",\"x\":").Append((int)npc.position.X);
                        sb.Append(",\"y\":").Append((int)npc.position.Y);
                        sb.Append(",\"friendly\":").Append(npc.friendly ? "true" : "false");
                        sb.Append(",\"boss\":").Append(npc.boss ? "true" : "false");

                        // Buffs
                        sb.Append(",\"buffs\":[");
                        bool firstBuff = true;
                        for (int b = 0; b < npc.buffType.Length; b++)
                        {
                            if (npc.buffType[b] == 0) continue;
                            if (!firstBuff) sb.Append(",");
                            firstBuff = false;
                            sb.Append("{\"id\":").Append(npc.buffType[b]);
                            sb.Append(",\"time\":").Append(npc.buffTime[b] / 60).Append("}");
                        }
                        sb.Append("]");

                        sb.Append("}");
                    }
                    catch { }
                }

                sb.Append("]}");
                return sb.ToString();
            }
            catch (Exception ex)
            {
                return JsonObject(JsonPair("error", ex.Message));
            }
        }

        private string HandleNpcKill(string body, out int statusCode)
        {
            statusCode = 200;
            try
            {
                if (Terraria.Main.gameMenu)
                {
                    statusCode = 400;
                    return JsonObject(JsonPair("success", false), JsonPair("error", "Not in a world."));
                }

                int type = ExtractJsonInt(body, "type", -1);
                bool all = ExtractJsonBool(body, "all", false);

                int killed = MainThreadDispatcher.RunOnMainThread(() =>
                {
                    int count = 0;
                    for (int i = 0; i < Terraria.Main.maxNPCs; i++)
                    {
                        try
                        {
                            var npc = Terraria.Main.npc[i];
                            if (npc == null || !npc.active) continue;
                            if (!all && type >= 0 && npc.type != type) continue;
                            if (!all && type < 0) continue;

                            npc.life = 0;
                            npc.active = false;
                            if (Terraria.Main.netMode != 0)
                                Terraria.NetMessage.SendData(23, -1, -1, null, i);
                            count++;
                        }
                        catch { }
                    }
                    return count;
                });

                return JsonObject(JsonPair("success", true), JsonPair("killed", killed));
            }
            catch (Exception ex)
            {
                statusCode = 500;
                return JsonObject(JsonPair("success", false), JsonPair("error", ex.Message));
            }
        }

        // Snapshot storage
        private class SnapshotData
        {
            public string Name;
            public long Timestamp;
            public float PlayerX, PlayerY;
            public int Health, HealthMax, Mana, ManaMax;
            public int[] InventoryTypes;
            public int[] InventoryStacks;
            public int[] InventoryPrefixes;
            public int[] BuffTypes;
            public int[] BuffTimes;
            public double WorldTime;
            public bool DayTime;
        }

        private static readonly Dictionary<string, SnapshotData> _snapshots = new Dictionary<string, SnapshotData>();

        private string HandleSnapshotSave(string body, out int statusCode)
        {
            statusCode = 200;
            try
            {
                if (Terraria.Main.gameMenu)
                {
                    statusCode = 400;
                    return JsonObject(JsonPair("success", false), JsonPair("error", "Not in a world."));
                }

                string name = ExtractJsonString(body, "name");
                if (string.IsNullOrEmpty(name)) name = "default";

                var snap = MainThreadDispatcher.RunOnMainThread(() =>
                {
                    var player = Terraria.Main.LocalPlayer;
                    if (player == null) return (SnapshotData)null;

                    var s = new SnapshotData
                    {
                        Name = name,
                        Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        PlayerX = player.position.X,
                        PlayerY = player.position.Y,
                        Health = player.statLife,
                        HealthMax = player.statLifeMax2,
                        Mana = player.statMana,
                        ManaMax = player.statManaMax2,
                        WorldTime = Terraria.Main.time,
                        DayTime = Terraria.Main.dayTime,
                        InventoryTypes = new int[50],
                        InventoryStacks = new int[50],
                        InventoryPrefixes = new int[50],
                        BuffTypes = (int[])player.buffType.Clone(),
                        BuffTimes = (int[])player.buffTime.Clone()
                    };

                    for (int i = 0; i < 50; i++)
                    {
                        if (player.inventory[i] != null)
                        {
                            s.InventoryTypes[i] = player.inventory[i].type;
                            s.InventoryStacks[i] = player.inventory[i].stack;
                            s.InventoryPrefixes[i] = player.inventory[i].prefix;
                        }
                    }

                    return s;
                });

                if (snap == null)
                {
                    statusCode = 400;
                    return JsonObject(JsonPair("success", false), JsonPair("error", "No local player"));
                }

                lock (_snapshots)
                {
                    _snapshots[name] = snap;
                }

                return JsonObject(JsonPair("success", true), JsonPair("name", name), JsonPair("message", "Snapshot saved."));
            }
            catch (Exception ex)
            {
                statusCode = 500;
                return JsonObject(JsonPair("success", false), JsonPair("error", ex.Message));
            }
        }

        private string HandleSnapshotRestore(string body, out int statusCode)
        {
            statusCode = 200;
            try
            {
                if (Terraria.Main.gameMenu)
                {
                    statusCode = 400;
                    return JsonObject(JsonPair("success", false), JsonPair("error", "Not in a world."));
                }

                string name = ExtractJsonString(body, "name");
                if (string.IsNullOrEmpty(name)) name = "default";

                SnapshotData snap;
                lock (_snapshots)
                {
                    if (!_snapshots.TryGetValue(name, out snap))
                    {
                        statusCode = 404;
                        return JsonObject(JsonPair("success", false), JsonPair("error", $"Snapshot '{name}' not found."));
                    }
                }

                MainThreadDispatcher.RunOnMainThreadAndWait(() =>
                {
                    try
                    {
                        var player = Terraria.Main.LocalPlayer;
                        if (player == null) return;

                        player.position.X = snap.PlayerX;
                        player.position.Y = snap.PlayerY;
                        player.statLife = snap.Health;
                        player.statMana = snap.Mana;

                        Terraria.Main.time = snap.WorldTime;
                        Terraria.Main.dayTime = snap.DayTime;

                        // Restore inventory
                        for (int i = 0; i < 50 && i < player.inventory.Length; i++)
                        {
                            if (snap.InventoryTypes[i] > 0)
                            {
                                player.inventory[i].SetDefaults(snap.InventoryTypes[i]);
                                player.inventory[i].stack = snap.InventoryStacks[i];
                                int prefixId = snap.InventoryPrefixes[i];
                                if (prefixId > 0)
                                    player.inventory[i].Prefix(prefixId);
                            }
                            else
                            {
                                player.inventory[i].SetDefaults(0);
                            }
                        }

                        // Restore buffs
                        Array.Copy(snap.BuffTypes, player.buffType, Math.Min(snap.BuffTypes.Length, player.buffType.Length));
                        Array.Copy(snap.BuffTimes, player.buffTime, Math.Min(snap.BuffTimes.Length, player.buffTime.Length));
                    }
                    catch (Exception ex)
                    {
                        _log.Error($"[Snapshot] Restore failed: {ex.Message}");
                    }
                });

                return JsonObject(JsonPair("success", true), JsonPair("name", name), JsonPair("message", "Snapshot restored."));
            }
            catch (Exception ex)
            {
                statusCode = 500;
                return JsonObject(JsonPair("success", false), JsonPair("error", ex.Message));
            }
        }

        private string HandleSnapshotList()
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("{\"snapshots\": [");
            bool first = true;
            lock (_snapshots)
            {
                foreach (var kv in _snapshots)
                {
                    if (!first) sb.Append(",");
                    first = false;
                    sb.Append("{\"name\":\"").Append(EscapeJson(kv.Key)).Append("\"");
                    sb.Append(",\"timestamp\":").Append(kv.Value.Timestamp);
                    sb.Append(",\"health\":").Append(kv.Value.Health);
                    sb.Append(",\"position\":{\"x\":").Append((int)kv.Value.PlayerX);
                    sb.Append(",\"y\":").Append((int)kv.Value.PlayerY).Append("}");
                    sb.Append("}");
                }
            }
            sb.Append("]}");
            return sb.ToString();
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

        #region Config Endpoints

        private static ModConfig FindConfigForMod(string modId)
        {
            foreach (var mod in PluginLoader.Mods)
                if (mod.Manifest.Id == modId)
                    return mod.Context?.Config;
            return null;
        }

        private string HandleConfigList()
        {
            var items = new List<string>();
            foreach (var mod in PluginLoader.Mods)
            {
                var config = mod.Context?.Config;
                if (config == null) continue;

                var metadata = config.GetPropertyMetadata();
                int clientCount = 0, serverCount = 0;
                foreach (var m in metadata)
                {
                    if (m.Scope == ConfigScope.Server) serverCount++;
                    else clientCount++;
                }

                items.Add(JsonObject(
                    JsonPair("modId", mod.Manifest.Id),
                    JsonPair("version", config.Version),
                    JsonPair("filePath", config.FilePath ?? ""),
                    JsonPair("clientProps", clientCount),
                    JsonPair("serverProps", serverCount)
                ));
            }
            return JsonObject(JsonArray("configs", items));
        }

        private string HandleConfigGet(string modId)
        {
            var config = FindConfigForMod(modId);
            if (config == null)
                return JsonObject(JsonPair("error", $"No config found for mod: {modId}"), JsonPair("modId", modId));

            object fresh = null;
            try { fresh = Activator.CreateInstance(config.GetType()); } catch { }

            var props = new List<string>();
            foreach (var meta in config.GetPropertyMetadata())
            {
                object value = meta.GetValue(config);
                object defaultVal = fresh != null ? meta.Property.GetValue((ModConfig)fresh) : null;

                var pairs = new List<string>
                {
                    JsonPair("name", meta.Key),
                    JsonPair("label", meta.Label),
                    JsonPair("description", meta.Description ?? ""),
                    JsonPair("type", GetConfigTypeName(meta.PropertyType)),
                    JsonPair("scope", meta.Scope == ConfigScope.Server ? "Server" : "Client"),
                    JsonPair("restartRequired", meta.RestartRequired),
                    ConfigValueToJsonPair("value", value, meta.PropertyType),
                    ConfigValueToJsonPair("default", defaultVal, meta.PropertyType)
                };

                if (meta.Min.HasValue) pairs.Add(JsonPair("min", meta.Min.Value));
                if (meta.Max.HasValue) pairs.Add(JsonPair("max", meta.Max.Value));
                if (meta.Options != null && meta.Options.Length > 0)
                {
                    var opts = new List<string>();
                    foreach (var opt in meta.Options)
                        opts.Add($"\"{EscapeJson(opt)}\"");
                    pairs.Add(JsonArray("options", opts));
                }

                props.Add(JsonObject(pairs.ToArray()));
            }

            return JsonObject(
                JsonPair("modId", modId),
                JsonPair("version", config.Version),
                JsonPair("filePath", config.FilePath ?? ""),
                JsonArray("properties", props)
            );
        }

        private string HandleConfigSet(string modId, string body)
        {
            var config = FindConfigForMod(modId);
            if (config == null)
                return JsonObject(JsonPair("success", false), JsonPair("error", $"No config found for mod: {modId}"));

            string propName = ExtractJsonString(body, "property");
            if (string.IsNullOrEmpty(propName))
                return JsonObject(JsonPair("success", false), JsonPair("error", "Missing 'property' field"));

            ConfigPropertyMeta target = null;
            foreach (var m in config.GetPropertyMetadata())
                if (m.Key == propName) { target = m; break; }

            if (target == null)
                return JsonObject(JsonPair("success", false), JsonPair("error", $"Unknown property: {propName}"));

            try
            {
                var raw = ConfigManager.ParseJsonRaw(body);
                if (!raw.TryGetValue("value", out object rawValue))
                    return JsonObject(JsonPair("success", false), JsonPair("error", "Missing 'value' field"));

                // Validate string Options constraint before setting
                if (target.Options != null && target.Options.Length > 0 && target.PropertyType == typeof(string))
                {
                    string strVal = rawValue?.ToString() ?? "";
                    bool found = false;
                    foreach (var opt in target.Options)
                        if (opt == strVal) { found = true; break; }
                    if (!found)
                        return JsonObject(JsonPair("success", false),
                            JsonPair("error", $"Value '{strVal}' is not a valid option for '{propName}'. Allowed: {string.Join(", ", target.Options)}"));
                }

                // Check scope mismatch (set in memory but warn about persistence)
                string warning = null;
                var currentScope = ConfigManager.GetCurrentScope();
                if (target.Scope != currentScope)
                    warning = $"Property '{propName}' has [{target.Scope}] scope and cannot be persisted from this process type";

                target.SetValue(config, rawValue);
                config.Save();
                NotifyModConfigChanged(modId);

                if (warning != null)
                    return JsonObject(
                        JsonPair("success", true),
                        JsonPair("warning", warning),
                        JsonPair("property", propName),
                        ConfigValueToJsonPair("value", target.GetValue(config), target.PropertyType)
                    );

                return JsonObject(
                    JsonPair("success", true),
                    JsonPair("property", propName),
                    ConfigValueToJsonPair("value", target.GetValue(config), target.PropertyType)
                );
            }
            catch (Exception ex)
            {
                return JsonObject(JsonPair("success", false), JsonPair("error", ex.Message));
            }
        }

        private string HandleConfigReload(string modId)
        {
            var config = FindConfigForMod(modId);
            if (config == null)
                return JsonObject(JsonPair("success", false), JsonPair("error", $"No config found for mod: {modId}"));
            config.Reload();
            NotifyModConfigChanged(modId);
            return JsonObject(JsonPair("success", true), JsonPair("modId", modId));
        }

        private string HandleConfigReset(string modId)
        {
            var config = FindConfigForMod(modId);
            if (config == null)
                return JsonObject(JsonPair("success", false), JsonPair("error", $"No config found for mod: {modId}"));
            config.ResetToDefaults();
            NotifyModConfigChanged(modId);
            return JsonObject(JsonPair("success", true), JsonPair("modId", modId));
        }

        private void NotifyModConfigChanged(string modId)
        {
            try
            {
                var modInfo = PluginLoader.GetMod(modId);
                if (modInfo?.Instance == null) return;
                var method = modInfo.Instance.GetType().GetMethod("OnConfigChanged",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly);
                method?.Invoke(modInfo.Instance, null);
            }
            catch (Exception ex)
            {
                _log?.Warn($"[Config] OnConfigChanged failed for {modId}: {ex.Message}");
            }
        }

        private static string GetConfigTypeName(Type t)
        {
            if (t == typeof(bool)) return "bool";
            if (t == typeof(int)) return "int";
            if (t == typeof(float)) return "float";
            if (t == typeof(double)) return "double";
            if (t == typeof(string)) return "string";
            return t.Name;
        }

        private static string ConfigValueToJsonPair(string key, object value, Type propertyType)
        {
            if (value == null) return $"\"{EscapeJson(key)}\": null";
            if (propertyType == typeof(bool) && value is bool b) return JsonPair(key, b);
            if (propertyType == typeof(int)) return JsonPair(key, Convert.ToInt32(value));
            if (propertyType == typeof(float) || propertyType == typeof(double))
                return JsonPair(key, Convert.ToDouble(value));
            return JsonPair(key, value.ToString());
        }

        #endregion

        #region Keybind Endpoints

        private string HandleKeybindList()
        {
            var keybinds = KeybindManager.GetAllKeybinds();
            var items = new List<string>();
            foreach (var kb in keybinds)
            {
                items.Add(JsonObject(
                    JsonPair("id", kb.Id),
                    JsonPair("modId", kb.ModId),
                    JsonPair("label", kb.Label),
                    JsonPair("description", kb.Description),
                    JsonPair("currentKey", kb.CurrentKey?.ToString() ?? ""),
                    JsonPair("defaultKey", kb.DefaultKey?.ToString() ?? ""),
                    JsonPair("enabled", kb.Enabled)
                ));
            }
            return JsonObject(JsonArray("keybinds", items));
        }

        private string HandleKeybindTrigger(string body, out int statusCode)
        {
            string id = ExtractJsonString(body, "id");
            if (string.IsNullOrEmpty(id))
            {
                statusCode = 400;
                return JsonObject(JsonPair("error", "Missing 'id' field (e.g. 'storage-hub.toggle')"));
            }

            var keybind = KeybindManager.GetKeybind(id);
            if (keybind == null)
            {
                statusCode = 404;
                return JsonObject(JsonPair("error", $"Keybind not found: {id}"));
            }

            if (keybind.Callback == null)
            {
                statusCode = 400;
                return JsonObject(JsonPair("error", $"Keybind has no callback: {id}"));
            }

            try
            {
                MainThreadDispatcher.RunOnMainThreadAndWait(() => keybind.Callback());
                statusCode = 200;
                return JsonObject(JsonPair("success", true), JsonPair("keybind", id));
            }
            catch (Exception ex)
            {
                statusCode = 500;
                return JsonObject(JsonPair("error", $"Callback failed: {ex.Message}"));
            }
        }

        #endregion

        #region Screenshot Endpoint

        private struct ScreenshotResult
        {
            public byte[] Data;
            public string ContentType;
            public string Error;
        }

        private ScreenshotResult HandleScreenshot(HttpListenerRequest request)
        {
            try
            {
                int maxWidth = 0;
                string qWidth = request.QueryString["width"];
                if (qWidth != null) int.TryParse(qWidth, out maxWidth);

                string format = request.QueryString["format"] ?? "png";
                int quality = 80;
                string qQuality = request.QueryString["quality"];
                if (qQuality != null) int.TryParse(qQuality, out quality);

                byte[] imageData = ScreenCapture.CaptureScreen(maxWidth, format, quality);

                if (imageData == null || imageData.Length == 0)
                    return new ScreenshotResult { Error = "Screenshot capture returned empty data" };

                string contentType = format.Equals("jpeg", StringComparison.OrdinalIgnoreCase) ||
                                     format.Equals("jpg", StringComparison.OrdinalIgnoreCase)
                    ? "image/jpeg" : "image/png";

                return new ScreenshotResult { Data = imageData, ContentType = contentType };
            }
            catch (Exception ex)
            {
                return new ScreenshotResult { Error = $"Screenshot failed: {ex.Message}" };
            }
        }

        private static void SendBinary(HttpListenerResponse response, int statusCode, string contentType, byte[] data)
        {
            response.StatusCode = statusCode;
            response.ContentType = contentType;
            response.ContentLength64 = data.Length;
            var stream = response.OutputStream;
            try
            {
                stream.Write(data, 0, data.Length);
            }
            finally
            {
                stream.Close();
            }
        }

        #endregion

        #region World Progression

        private string HandleWorldProgression()
        {
            try
            {
                if (!Game.InWorld)
                    return JsonObject(JsonPair("inWorld", false), JsonPair("error", "Not in a world"));

                return JsonObject(
                    JsonPair("inWorld", true),
                    JsonPair("hardMode", Main.hardMode),
                    JsonPair("downedBoss1", NPC.downedBoss1),
                    JsonPair("downedBoss2", NPC.downedBoss2),
                    JsonPair("downedBoss3", NPC.downedBoss3),
                    JsonPair("downedSlimeKing", NPC.downedSlimeKing),
                    JsonPair("downedQueenBee", NPC.downedQueenBee),
                    JsonPair("downedMechBoss1", NPC.downedMechBoss1),
                    JsonPair("downedMechBoss2", NPC.downedMechBoss2),
                    JsonPair("downedMechBoss3", NPC.downedMechBoss3),
                    JsonPair("downedMechBossAny", NPC.downedMechBossAny),
                    JsonPair("downedPlantBoss", NPC.downedPlantBoss),
                    JsonPair("downedGolemBoss", NPC.downedGolemBoss),
                    JsonPair("downedFishron", NPC.downedFishron),
                    JsonPair("downedEmpressOfLight", NPC.downedEmpressOfLight),
                    JsonPair("downedAncientCultist", NPC.downedAncientCultist),
                    JsonPair("downedMoonlord", NPC.downedMoonlord),
                    JsonPair("downedPirates", NPC.downedPirates),
                    JsonPair("downedGoblins", NPC.downedGoblins),
                    JsonPair("downedFrost", NPC.downedFrost),
                    JsonPair("downedMartians", NPC.downedMartians),
                    JsonPair("downedClown", NPC.downedClown),
                    JsonPair("downedHalloweenTree", NPC.downedHalloweenTree),
                    JsonPair("downedHalloweenKing", NPC.downedHalloweenKing),
                    JsonPair("downedChristmasTree", NPC.downedChristmasTree),
                    JsonPair("downedChristmasIceQueen", NPC.downedChristmasIceQueen),
                    JsonPair("downedChristmasSantank", NPC.downedChristmasSantank)
                );
            }
            catch (Exception ex)
            {
                return JsonObject(JsonPair("error", $"Failed: {ex.Message}"));
            }
        }

        #endregion

        #region Chest Contents

        private string HandleChestList()
        {
            try
            {
                if (!Game.InWorld)
                    return JsonObject(JsonPair("inWorld", false), JsonPair("error", "Not in a world"));

                var chests = Main.chest;
                if (chests == null)
                    return JsonObject(JsonPair("inWorld", true), JsonArray("chests", new List<string>()));

                var items = new List<string>();
                for (int i = 0; i < chests.Length && i < 8000; i++)
                {
                    var chest = chests[i];
                    if (chest == null) continue;

                    // Build preview of first 3 non-empty items
                    var preview = new List<string>();
                    if (chest.item != null)
                    {
                        for (int s = 0; s < chest.item.Length && preview.Count < 3; s++)
                        {
                            var item = chest.item[s];
                            if (item != null && item.type > 0 && item.stack > 0)
                                preview.Add($"{item.Name} x{item.stack}");
                        }
                    }

                    items.Add(JsonObject(
                        JsonPair("index", i),
                        JsonPair("x", chest.x),
                        JsonPair("y", chest.y),
                        JsonPair("name", chest.name ?? ""),
                        JsonStringArray("preview", preview)
                    ));

                    if (items.Count >= 200) break; // Cap output size
                }

                return JsonObject(
                    JsonPair("inWorld", true),
                    JsonPair("count", items.Count),
                    JsonArray("chests", items)
                );
            }
            catch (Exception ex)
            {
                return JsonObject(JsonPair("error", $"Failed: {ex.Message}"));
            }
        }

        private string HandleChestContents(int chestIndex, out int statusCode)
        {
            statusCode = 200;
            try
            {
                if (!Game.InWorld)
                {
                    statusCode = 400;
                    return JsonObject(JsonPair("error", "Not in a world"));
                }

                var chests = Main.chest;
                if (chests == null || chestIndex < 0 || chestIndex >= chests.Length)
                {
                    statusCode = 404;
                    return JsonObject(JsonPair("error", $"Invalid chest index: {chestIndex}"));
                }

                var chest = chests[chestIndex];
                if (chest == null)
                {
                    statusCode = 404;
                    return JsonObject(JsonPair("error", $"Chest {chestIndex} is null"));
                }

                var slots = new List<string>();
                if (chest.item != null)
                {
                    for (int s = 0; s < chest.item.Length; s++)
                    {
                        var item = chest.item[s];
                        if (item == null || item.type == 0) continue;
                        slots.Add(JsonObject(
                            JsonPair("slot", s),
                            JsonPair("type", item.type),
                            JsonPair("name", item.Name ?? ""),
                            JsonPair("stack", item.stack),
                            JsonPair("prefix", (int)item.prefix)
                        ));
                    }
                }

                return JsonObject(
                    JsonPair("index", chestIndex),
                    JsonPair("x", chest.x),
                    JsonPair("y", chest.y),
                    JsonPair("name", chest.name ?? ""),
                    JsonArray("items", slots)
                );
            }
            catch (Exception ex)
            {
                statusCode = 500;
                return JsonObject(JsonPair("error", $"Failed: {ex.Message}"));
            }
        }

        /// <summary>
        /// <summary>
        /// Mysterious Chest debug endpoint (type 21, style 41).
        /// GET  /api/painting-chests — scan for all mysterious chests + orphaned tiles
        /// POST /api/painting-chests — actions: place, destroy, scan
        ///   place: {"x": N, "y": N} — places a 2x2 chest with ground below
        ///   destroy: {"x": N, "y": N} — removes chest tiles + entry
        ///   scan: {} — same as GET
        /// </summary>
        private string HandlePaintingChests(string body, string method, out int statusCode)
        {
            statusCode = 200;
            const int TILE_TYPE = 21;  // Vanilla chest tile
            const int OUR_STYLE = 41;  // Our custom style
            const string CHEST_NAME = "Mysterious Chest";
            const string LEGACY_CHEST_NAME = "Mysterious Painting";

            try
            {
                if (!Game.InWorld) { statusCode = 400; return JsonObject(JsonPair("error", "Not in a world")); }

                if (method == "GET" || (method == "POST" && body != null && body.Contains("\"scan\"")))
                {
                    var results = new List<string>();

                    // Find all chests named "Mysterious Painting" at type 21 style 41 tiles
                    var chests = Main.chest;
                    for (int i = 0; i < 8000 && chests != null; i++)
                    {
                        var chest = chests[i];
                        if (chest == null || (chest.name != CHEST_NAME && chest.name != LEGACY_CHEST_NAME)) continue;

                        var tile = Main.tile[chest.x, chest.y];
                        if (tile == null || !tile.active() || tile.type != TILE_TYPE) continue;
                        if (tile.frameX / 36 != OUR_STYLE) continue;

                        int usedSlots = 0;
                        if (chest.item != null)
                            for (int s = 0; s < chest.maxItems; s++)
                                if (chest.item[s] != null && chest.item[s].type > 0 && chest.item[s].stack > 0)
                                    usedSlots++;

                        results.Add(JsonObject(
                            JsonPair("chestIndex", i),
                            JsonPair("x", chest.x),
                            JsonPair("y", chest.y),
                            JsonPair("name", chest.name ?? ""),
                            JsonPair("maxItems", chest.maxItems),
                            JsonPair("usedSlots", usedSlots)
                        ));
                    }

                    return JsonObject(
                        JsonPair("count", results.Count),
                        JsonArray("chests", results)
                    );
                }
                else if (method == "POST")
                {
                    int x = ExtractJsonInt(body, "x", -1);
                    int y = ExtractJsonInt(body, "y", -1);
                    string action = ExtractJsonString(body, "action") ?? "place";

                    if (action == "place")
                    {
                        if (x < 0 || y < 0) { statusCode = 400; return JsonObject(JsonPair("error", "x and y required")); }

                        string result = MainThreadDispatcher.RunOnMainThread(() =>
                        {
                            // Ensure solid ground below the 2x2 chest
                            for (int dx = 0; dx < 2; dx++)
                            {
                                if (Main.tile[x + dx, y + 2] == null)
                                    Main.tile[x + dx, y + 2] = new Tile();
                                var below = Main.tile[x + dx, y + 2];
                                if (!below.active() || !Main.tileSolid[below.type])
                                {
                                    below.active(true);
                                    below.type = 1; // stone
                                }
                            }

                            // Clear the 2x2 area for the chest
                            for (int dx = 0; dx < 2; dx++)
                                for (int dy = 0; dy < 2; dy++)
                                {
                                    if (Main.tile[x + dx, y + dy] == null)
                                        Main.tile[x + dx, y + dy] = new Tile();
                                    Main.tile[x + dx, y + dy].active(false);
                                    Main.tile[x + dx, y + dy].type = 0;
                                }

                            // Place chest tiles (type 21, style 41)
                            int frameXBase = OUR_STYLE * 36; // 1476
                            for (int dx = 0; dx < 2; dx++)
                            {
                                for (int dy = 0; dy < 2; dy++)
                                {
                                    var t = Main.tile[x + dx, y + dy];
                                    t.active(true);
                                    t.type = (ushort)TILE_TYPE;
                                    t.frameX = (short)(frameXBase + dx * 18);
                                    t.frameY = (short)(dy * 18);
                                }
                            }

                            // Create chest entry via vanilla
                            int chestIdx = Chest.CreateChest(x, y, -1);
                            if (chestIdx < 0) return "error:CreateChest failed (max chests?)";

                            var chest = Main.chest[chestIdx];
                            if (chest == null) return "error:Chest null after create";

                            chest.Resize(40); // Default capacity
                            chest.name = CHEST_NAME;

                            // Frame update + net sync
                            for (int dx = -1; dx < 3; dx++)
                                for (int dy = -1; dy < 4; dy++)
                                    if (x + dx >= 0 && y + dy >= 0)
                                        WorldGen.SquareTileFrame(x + dx, y + dy);

                            if (Main.netMode != 0)
                                NetMessage.SendTileSquare(-1, x, y, 3, 4);

                            return $"ok:{chestIdx}";
                        });

                        if (result.StartsWith("error:"))
                        { statusCode = 400; return JsonObject(JsonPair("error", result.Substring(6))); }
                        int idx = int.Parse(result.Substring(3));
                        return JsonObject(JsonPair("success", true), JsonPair("chestIndex", idx), JsonPair("x", x), JsonPair("y", y));
                    }
                    else if (action == "destroy")
                    {
                        if (x < 0 || y < 0) { statusCode = 400; return JsonObject(JsonPair("error", "x and y required")); }

                        string result = MainThreadDispatcher.RunOnMainThread(() =>
                        {
                            // Find and remove chest entry
                            int chestIdx = Chest.FindChest(x, y);
                            if (chestIdx >= 0)
                            {
                                Main.chest[chestIdx] = null;
                            }
                            // Clear tiles
                            for (int dx = 0; dx < 2; dx++)
                                for (int dy = 0; dy < 2; dy++)
                                {
                                    Main.tile[x + dx, y + dy].active(false);
                                    Main.tile[x + dx, y + dy].type = 0;
                                }
                            // Frame update
                            for (int dx = -1; dx < 3; dx++)
                                for (int dy = -1; dy < 3; dy++)
                                    if (x + dx >= 0 && y + dy >= 0)
                                        WorldGen.SquareTileFrame(x + dx, y + dy);

                            return $"ok:{chestIdx}";
                        });

                        return JsonObject(JsonPair("success", true), JsonPair("result", result));
                    }

                    statusCode = 400;
                    return JsonObject(JsonPair("error", $"Unknown action: {action}. Use place, destroy, or scan."));
                }

                statusCode = 405;
                return JsonObject(JsonPair("error", "Use GET or POST"));
            }
            catch (Exception ex)
            {
                statusCode = 500;
                return JsonObject(JsonPair("error", $"Failed: {ex.Message}"));
            }
        }

        #endregion

        #region Player Actions

        private string HandlePlayerGive(string body, out int statusCode)
        {
            statusCode = 200;
            try
            {
                if (!Game.InWorld) { statusCode = 400; return JsonObject(JsonPair("error", "Not in a world")); }

                int itemId = ExtractJsonInt(body, "itemId", 0);
                int stack = ExtractJsonInt(body, "stack", 1);
                int prefix = ExtractJsonInt(body, "prefix", 0);

                // Support string item IDs like "storage-hub:painting-chest"
                if (itemId <= 0)
                {
                    string itemIdStr = ExtractJsonString(body, "itemId");
                    if (!string.IsNullOrEmpty(itemIdStr) && itemIdStr.Contains(":"))
                    {
                        itemId = TerrariaModder.Core.Assets.ItemRegistry.GetRuntimeType(itemIdStr);
                        if (itemId < 0) { statusCode = 400; return JsonObject(JsonPair("error", $"Custom item not found: {itemIdStr}")); }
                    }
                    // Also support "type" field as alias for backward compat
                    if (itemId <= 0)
                    {
                        int typeField = ExtractJsonInt(body, "type", 0);
                        if (typeField > 0) itemId = typeField;
                    }
                }

                if (itemId <= 0) { statusCode = 400; return JsonObject(JsonPair("error", "Missing or invalid 'itemId'. Use integer type or string like 'storage-hub:painting-chest'")); }

                string result = MainThreadDispatcher.RunOnMainThread(() =>
                {
                    var player = Main.LocalPlayer;
                    if (player == null) return "Player not available";

                    // Find first empty inventory slot
                    for (int i = 0; i < 50; i++)
                    {
                        if (player.inventory[i] == null || player.inventory[i].type == 0 || player.inventory[i].stack == 0)
                        {
                            player.inventory[i] = new Item();
                            player.inventory[i].SetDefaults(itemId);
                            player.inventory[i].stack = stack;
                            if (prefix > 0) player.inventory[i].Prefix(prefix);
                            return null; // success
                        }
                    }
                    return "No empty inventory slot";
                });

                if (result != null) { statusCode = 400; return JsonObject(JsonPair("error", result)); }
                return JsonObject(JsonPair("success", true), JsonPair("itemId", itemId), JsonPair("stack", stack));
            }
            catch (Exception ex) { statusCode = 500; return JsonObject(JsonPair("error", ex.Message)); }
        }

        private string HandlePlayerTeleport(string body, out int statusCode)
        {
            statusCode = 200;
            try
            {
                if (!Game.InWorld) { statusCode = 400; return JsonObject(JsonPair("error", "Not in a world")); }

                string target = ExtractJsonString(body, "target");
                int x = ExtractJsonInt(body, "x", -1);
                int y = ExtractJsonInt(body, "y", -1);

                string result = MainThreadDispatcher.RunOnMainThread(() =>
                {
                    var player = Main.LocalPlayer;
                    if (player == null) return "Player not available";

                    if (!string.IsNullOrEmpty(target))
                    {
                        switch (target.ToLowerInvariant())
                        {
                            case "spawn":
                                player.Teleport(new Microsoft.Xna.Framework.Vector2(Main.spawnTileX * 16, Main.spawnTileY * 16));
                                return null;
                            case "dungeon":
                                if (Main.dungeonX <= 0 || Main.dungeonY <= 0)
                                    return "Dungeon not found in this world";
                                player.Teleport(new Microsoft.Xna.Framework.Vector2(Main.dungeonX * 16, Main.dungeonY * 16));
                                return null;
                            case "hell":
                                player.Teleport(new Microsoft.Xna.Framework.Vector2(player.position.X, (Main.maxTilesY - 200) * 16));
                                return null;
                            case "bed":
                                if (player.SpawnX > 0 && player.SpawnY > 0)
                                    player.Teleport(new Microsoft.Xna.Framework.Vector2(player.SpawnX * 16, player.SpawnY * 16));
                                else
                                    return "No bed spawn set";
                                return null;
                            case "surface":
                                player.Teleport(new Microsoft.Xna.Framework.Vector2(player.position.X, (float)(Main.worldSurface * 16 - 400)));
                                return null;
                            default:
                                return $"Unknown target: {target}. Use spawn, dungeon, hell, bed, surface.";
                        }
                    }

                    if (x >= 0 && y >= 0)
                    {
                        player.Teleport(new Microsoft.Xna.Framework.Vector2(x * 16, y * 16));
                        return null;
                    }

                    return "Provide 'target' or 'x'+'y' (tile coordinates)";
                });

                if (result != null) { statusCode = 400; return JsonObject(JsonPair("error", result)); }
                return JsonObject(JsonPair("success", true));
            }
            catch (Exception ex) { statusCode = 500; return JsonObject(JsonPair("error", ex.Message)); }
        }

        private string HandlePlayerBuff(string body, out int statusCode)
        {
            statusCode = 200;
            try
            {
                if (!Game.InWorld) { statusCode = 400; return JsonObject(JsonPair("error", "Not in a world")); }

                int type = ExtractJsonInt(body, "type", 0);
                int duration = ExtractJsonInt(body, "duration", 3600); // 60 seconds default

                if (type <= 0) { statusCode = 400; return JsonObject(JsonPair("error", "Missing or invalid 'type'")); }

                MainThreadDispatcher.RunOnMainThreadAndWait(() =>
                {
                    Main.LocalPlayer?.AddBuff(type, duration);
                });

                return JsonObject(JsonPair("success", true), JsonPair("type", type), JsonPair("duration", duration));
            }
            catch (Exception ex) { statusCode = 500; return JsonObject(JsonPair("error", ex.Message)); }
        }

        #endregion

        #region NPC Spawning

        private string HandleSpawnNpc(string body, out int statusCode)
        {
            statusCode = 200;
            try
            {
                if (!Game.InWorld) { statusCode = 400; return JsonObject(JsonPair("error", "Not in a world")); }

                int type = ExtractJsonInt(body, "type", 0);
                int count = ExtractJsonInt(body, "count", 1);
                if (count < 1) count = 1;
                if (count > 20) count = 20;

                if (type <= 0) { statusCode = 400; return JsonObject(JsonPair("error", "Missing or invalid 'type'")); }

                var spawned = MainThreadDispatcher.RunOnMainThread(() =>
                {
                    var player = Main.LocalPlayer;
                    if (player == null) return new List<int>();

                    int worldX = (int)player.position.X;
                    int worldY = (int)player.position.Y - 80;

                    var ids = new List<int>();
                    for (int i = 0; i < count; i++)
                    {
                        int idx = NPC.NewNPC(
                            new Terraria.DataStructures.EntitySource_SpawnNPC(),
                            worldX + (i * 40), worldY, type, Target: Main.myPlayer);
                        if (idx >= 0 && idx < Main.npc.Length)
                        {
                            Main.npc[idx].timeLeft *= 20; // Prevent despawn
                            ids.Add(idx);
                        }
                    }
                    return ids;
                });

                return JsonObject(
                    JsonPair("success", true),
                    JsonPair("type", type),
                    JsonPair("count", spawned.Count),
                    JsonStringArray("npcIndices", spawned.Select(i => i.ToString()).ToList())
                );
            }
            catch (Exception ex) { statusCode = 500; return JsonObject(JsonPair("error", ex.Message)); }
        }

        #endregion

        #region Chat

        private string HandleChatSend(string body, out int statusCode)
        {
            statusCode = 200;
            try
            {
                string text = ExtractJsonString(body, "text");
                if (string.IsNullOrEmpty(text)) { statusCode = 400; return JsonObject(JsonPair("error", "Missing 'text'")); }

                int r = ExtractJsonInt(body, "r", 255);
                int g = ExtractJsonInt(body, "g", 255);
                int b = ExtractJsonInt(body, "b", 255);

                MainThreadDispatcher.RunOnMainThreadAndWait(() =>
                {
                    Main.NewText(text, (byte)r, (byte)g, (byte)b);
                });

                return JsonObject(JsonPair("success", true));
            }
            catch (Exception ex) { statusCode = 500; return JsonObject(JsonPair("error", ex.Message)); }
        }

        #endregion

        #region Events

        private string HandleEvents(HttpListenerRequest request)
        {
            try
            {
                long sinceId = 0;
                string qSince = request.QueryString["since"];
                if (qSince != null) long.TryParse(qSince, out sinceId);

                string source = request.QueryString["source"];

                int limit = 50;
                string qLimit = request.QueryString["limit"];
                if (qLimit != null) int.TryParse(qLimit, out limit);

                var events = EventLog.GetEvents(sinceId, source, limit);
                var items = new List<string>();
                foreach (var e in events)
                {
                    var pairs = new List<string>
                    {
                        JsonPair("id", (int)e.Id),
                        JsonPair("timestamp", (double)e.TimestampMs),
                        JsonPair("source", e.Source),
                        JsonPair("type", e.Type)
                    };
                    if (e.Data != null) pairs.Add(JsonPair("data", e.Data));
                    items.Add(JsonObject(pairs.ToArray()));
                }

                return JsonObject(
                    JsonPair("count", items.Count),
                    JsonArray("events", items)
                );
            }
            catch (Exception ex)
            {
                return JsonObject(JsonPair("error", ex.Message));
            }
        }

        // ── Logs ──────────────────────────────────────────────────────────

        private string HandleLogs(HttpListenerRequest request)
        {
            try
            {
                int count = 50;
                string qCount = request.QueryString["count"];
                if (qCount != null) int.TryParse(qCount, out count);
                if (count < 1) count = 1;
                if (count > 100) count = 100;

                string levelFilter = request.QueryString["level"];
                string modFilter = request.QueryString["modId"];

                var logs = TerrariaModder.Core.Logging.LogManager.GetRecentLogs(100);
                var items = new List<string>();

                foreach (var entry in logs)
                {
                    if (items.Count >= count) break;

                    if (levelFilter != null &&
                        !string.Equals(entry.Level.ToString(), levelFilter, StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (modFilter != null &&
                        !string.Equals(entry.ModId, modFilter, StringComparison.OrdinalIgnoreCase))
                        continue;

                    items.Add(JsonObject(
                        JsonPair("modId", entry.ModId ?? ""),
                        JsonPair("level", entry.Level.ToString()),
                        JsonPair("message", entry.Message ?? ""),
                        JsonPair("timestamp", entry.Timestamp.ToString("yyyy-MM-dd HH:mm:ss"))
                    ));
                }

                return JsonObject(
                    JsonPair("count", items.Count),
                    JsonArray("logs", items)
                );
            }
            catch (Exception ex)
            {
                return JsonObject(JsonPair("error", ex.Message));
            }
        }

        // ── Diagnostics ───────────────────────────────────────────────────

        private string HandleDiagnostics()
        {
            try
            {
                var pairs = new List<string>();

                // FPS from Main.frameRate
                try
                {
                    int fps = (int)typeof(Terraria.Main).GetField("frameRate",
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
                        ?.GetValue(null);
                    pairs.Add(JsonPair("fps", fps));
                }
                catch { pairs.Add(JsonPair("fps", 0)); }

                // DetailedFPS metrics via reflection
                try
                {
                    var dfType = Type.GetType("Terraria.Testing.DetailedFPS, Terraria")
                        ?? typeof(Terraria.Main).Assembly.GetType("Terraria.Testing.DetailedFPS");
                    if (dfType != null)
                    {
                        var cft = dfType.GetProperty("CurrentFrameTime",
                            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                        if (cft != null)
                        {
                            var ts = (TimeSpan)cft.GetValue(null);
                            pairs.Add(JsonPair("frameTimeMs", ts.TotalMilliseconds));
                        }

                        var cpuUtil = dfType.GetMethod("GetCPUUtilization",
                            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                        if (cpuUtil != null)
                        {
                            float util = (float)cpuUtil.Invoke(null, new object[] { 60 });
                            pairs.Add(JsonPair("cpuUtilization", (double)util));
                        }

                        var vsync = dfType.GetMethod("VsyncAppearsActive",
                            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                        if (vsync != null)
                        {
                            bool active = (bool)vsync.Invoke(null, null);
                            pairs.Add(JsonPair("vsyncActive", active));
                        }
                    }
                }
                catch { }

                // GC stats (no reflection needed)
                pairs.Add(JsonPair("memoryMB", (double)(GC.GetTotalMemory(false) / (1024.0 * 1024.0))));
                pairs.Add(JsonPair("gc0Count", GC.CollectionCount(0)));
                pairs.Add(JsonPair("gc1Count", GC.CollectionCount(1)));
                pairs.Add(JsonPair("gc2Count", GC.CollectionCount(2)));

                return JsonObject(pairs.ToArray());
            }
            catch (Exception ex)
            {
                return JsonObject(JsonPair("error", ex.Message));
            }
        }

        // ── Health ────────────────────────────────────────────────────────

        private string HandleHealth()
        {
            try
            {
                var recentLogs = TerrariaModder.Core.Logging.LogManager.GetRecentLogs(100);
                var modErrors = new Dictionary<string, List<string>>();

                foreach (var entry in recentLogs)
                {
                    if (entry.Level != TerrariaModder.Core.Logging.LogLevel.Error) continue;
                    string mid = entry.ModId ?? "core";
                    if (!modErrors.ContainsKey(mid))
                        modErrors[mid] = new List<string>();
                    if (modErrors[mid].Count < 3) // cap at 3 recent errors per mod
                        modErrors[mid].Add(entry.Message);
                }

                // Build per-mod health
                var mods = new List<string>();
                foreach (var kv in ModStateRegistry.All)
                {
                    var errs = modErrors.ContainsKey(kv.Key) ? modErrors[kv.Key] : new List<string>();
                    var errItems = new List<string>();
                    foreach (var e in errs)
                        errItems.Add("\"" + EscapeJson(e ?? "") + "\"");

                    mods.Add(JsonObject(
                        JsonPair("id", kv.Key),
                        JsonPair("loaded", true),
                        JsonPair("errorCount", errs.Count),
                        "\"recentErrors\": [" + string.Join(", ", errItems) + "]"
                    ));
                }

                // Add mods with errors that don't have state providers
                foreach (var kv in modErrors)
                {
                    if (ModStateRegistry.GetProvider(kv.Key) != null) continue;
                    var errItems = new List<string>();
                    foreach (var e in kv.Value)
                        errItems.Add("\"" + EscapeJson(e ?? "") + "\"");
                    mods.Add(JsonObject(
                        JsonPair("id", kv.Key),
                        JsonPair("loaded", true),
                        JsonPair("errorCount", kv.Value.Count),
                        "\"recentErrors\": [" + string.Join(", ", errItems) + "]"
                    ));
                }

                return JsonObject(
                    JsonPair("totalMods", mods.Count),
                    JsonPair("totalErrors", recentLogs.Count(e => e.Level == TerrariaModder.Core.Logging.LogLevel.Error)),
                    JsonArray("mods", mods)
                );
            }
            catch (Exception ex)
            {
                return JsonObject(JsonPair("error", ex.Message));
            }
        }

        private string HandleHarmony()
        {
            try
            {
                var sb = new System.Text.StringBuilder(2048);
                sb.Append("{");

                var allPatched = HarmonyLib.Harmony.GetAllPatchedMethods().ToList();
                sb.Append("\"totalPatched\": ").Append(allPatched.Count).Append(", ");

                // Group patches by owner (mod)
                var modPatches = new Dictionary<string, int>();
                var methodDetails = new List<string>();

                foreach (var method in allPatched)
                {
                    try
                    {
                        var info = HarmonyLib.Harmony.GetPatchInfo(method);
                        if (info == null) continue;

                        var prefixes = info.Prefixes?.Count ?? 0;
                        var postfixes = info.Postfixes?.Count ?? 0;
                        var transpilers = info.Transpilers?.Count ?? 0;

                        // Track per-owner counts
                        void CountOwner(string owner)
                        {
                            if (!modPatches.ContainsKey(owner)) modPatches[owner] = 0;
                            modPatches[owner]++;
                        }
                        if (info.Prefixes != null) foreach (var p in info.Prefixes) CountOwner(p.owner);
                        if (info.Postfixes != null) foreach (var p in info.Postfixes) CountOwner(p.owner);
                        if (info.Transpilers != null) foreach (var p in info.Transpilers) CountOwner(p.owner);

                        if (methodDetails.Count < 50) // cap detail list
                        {
                            string methodName = method.DeclaringType?.Name + "." + method.Name;
                            methodDetails.Add("{\"method\":\"" + EscapeJson(methodName) + "\"" +
                                ",\"prefixes\":" + prefixes +
                                ",\"postfixes\":" + postfixes +
                                ",\"transpilers\":" + transpilers + "}");
                        }
                    }
                    catch { }
                }

                // Mod summary
                sb.Append("\"mods\": [");
                bool first = true;
                foreach (var kv in modPatches)
                {
                    if (!first) sb.Append(",");
                    first = false;
                    sb.Append("{\"owner\":\"").Append(EscapeJson(kv.Key)).Append("\"");
                    sb.Append(",\"patchCount\":").Append(kv.Value).Append("}");
                }
                sb.Append("], ");

                sb.Append("\"methods\": [").Append(string.Join(",", methodDetails)).Append("]");
                sb.Append("}");
                return sb.ToString();
            }
            catch (Exception ex)
            {
                return JsonObject(JsonPair("error", ex.Message));
            }
        }

        private string HandleNetStats()
        {
            try
            {
                int netMode = 0;
                try { netMode = Terraria.Main.netMode; } catch { }

                var sb = new System.Text.StringBuilder();
                sb.Append("{");
                sb.Append("\"netMode\": ").Append(netMode).Append(", ");
                sb.Append("\"connected\": ").Append(netMode > 0 ? "true" : "false").Append(", ");

                if (netMode == 1) // client
                {
                    sb.Append("\"role\": \"client\", ");
                    int playerCount = 0;
                    try
                    {
                        for (int i = 0; i < Terraria.Main.maxPlayers; i++)
                            if (Terraria.Main.player[i] != null && Terraria.Main.player[i].active) playerCount++;
                    }
                    catch { }
                    sb.Append("\"playerCount\": ").Append(playerCount);
                }
                else if (netMode == 2) // server
                {
                    sb.Append("\"role\": \"server\", ");
                    int playerCount = 0;
                    try
                    {
                        for (int i = 0; i < Terraria.Main.maxPlayers; i++)
                            if (Terraria.Main.player[i] != null && Terraria.Main.player[i].active) playerCount++;
                    }
                    catch { }
                    sb.Append("\"playerCount\": ").Append(playerCount);
                }
                else
                {
                    sb.Append("\"role\": \"singleplayer\"");
                }

                sb.Append("}");
                return sb.ToString();
            }
            catch (Exception ex)
            {
                return JsonObject(JsonPair("error", ex.Message));
            }
        }

        #endregion

        #region Mod State

        private string HandleModState(string modId, out int statusCode)
        {
            statusCode = 200;
            try
            {
                var provider = ModStateRegistry.GetProvider(modId);
                if (provider == null)
                {
                    statusCode = 404;
                    return JsonObject(JsonPair("error", $"No state provider for mod: {modId}"));
                }

                var state = provider.GetModState();
                if (state == null)
                    return JsonObject(JsonPair("modId", modId));

                var pairs = new List<string> { JsonPair("modId", modId) };
                foreach (var kv in state)
                {
                    if (kv.Value is bool bv)
                        pairs.Add(JsonPair(kv.Key, bv));
                    else if (kv.Value is int iv)
                        pairs.Add(JsonPair(kv.Key, iv));
                    else if (kv.Value is double dv)
                        pairs.Add(JsonPair(kv.Key, dv));
                    else if (kv.Value is float fv)
                        pairs.Add(JsonPair(kv.Key, (double)fv));
                    else if (kv.Value is System.Collections.IEnumerable enumerable && !(kv.Value is string))
                    {
                        var items = new List<string>();
                        foreach (var item in enumerable)
                            items.Add("\"" + EscapeJson(item?.ToString()) + "\"");
                        pairs.Add("\"" + EscapeJson(kv.Key) + "\":[" + string.Join(",", items) + "]");
                    }
                    else
                        pairs.Add(JsonPair(kv.Key, kv.Value?.ToString()));
                }
                return JsonObject(pairs.ToArray());
            }
            catch (Exception ex)
            {
                statusCode = 500;
                return JsonObject(JsonPair("error", ex.Message));
            }
        }

        #endregion

        #region Capabilities & Mod Actions

        private string HandleCapabilities()
        {
            var sections = new List<string>();

            // Query targets
            var queries = new List<string>();
            string[] queryTargets = { "capabilities", "surroundings", "player", "world", "progression",
                "inventory", "entities", "tiles", "ui", "chests", "events", "keybinds",
                "menu", "window", "input", "input_log", "status", "mods", "actions", "commands",
                "logs", "diagnostics", "health", "tiles_raw", "npcs", "snapshots", "harmony", "net_stats",
                "projectiles", "reflect_type", "reflect_field", "reflect_instance", "eval", "trace_log", "watch_log" };
            foreach (var t in queryTargets)
                queries.Add(JsonObject(JsonPair("name", t)));
            // Mod state targets
            foreach (var kv in ModStateRegistry.All)
                queries.Add(JsonObject(JsonPair("name", "mod:" + kv.Key), JsonPair("description", "State for " + kv.Key)));
            sections.Add(JsonArray("queryTargets", queries));

            // Core actions
            var coreActions = new List<string>();
            string[][] coreActionDefs = {
                new[] { "game_action", "Execute a game action (move, jump, attack, etc.)" },
                new[] { "press_key", "Press a keyboard key" },
                new[] { "release_key", "Release a keyboard key" },
                new[] { "click", "Click at screen position" },
                new[] { "move_mouse", "Move cursor to screen position" },
                new[] { "release_all", "Release all held keys/buttons" },
                new[] { "show_window", "Show game window" },
                new[] { "hide_window", "Hide game window" },
                new[] { "enter_world", "Enter a world (character, world index)" },
                new[] { "wait_for", "Wait for game state condition" },
                new[] { "command", "Run a debug command" },
                new[] { "input_log", "Toggle input click logging" },
                new[] { "trigger_keybind", "Trigger a mod keybind by ID" },
                new[] { "give_item", "Give item to player" },
                new[] { "teleport", "Teleport player" },
                new[] { "buff", "Apply buff to player" },
                new[] { "spawn_npc", "Spawn NPC near player" },
                new[] { "chat", "Send chat message" },
                new[] { "create_world", "Create a new world (name, seed, size, difficulty, evil)" },
                new[] { "delete_world", "Delete a world by index or name" },
                new[] { "save", "Save current player data" },
                new[] { "set_tile", "Place or remove a tile (x, y, type or action:remove)" },
                new[] { "kill_npcs", "Kill NPCs by type or all" },
                new[] { "snapshot_save", "Save game state snapshot (name)" },
                new[] { "snapshot_restore", "Restore game state snapshot (name)" },
                new[] { "inventory_set", "Set item in inventory slot (slot, type, stack, prefix)" },
                new[] { "equip", "Equip item to armor/accessory/dye/misc slot" },
                new[] { "hotbar_select", "Select hotbar slot (0-9)" },
                new[] { "chest_set", "Set chest slot contents or clear/fill chest" },
                new[] { "teleport_xy", "Teleport to tile coordinates (x, y)" },
                new[] { "world_set", "Set world field (hardMode, dayTime, time, bloodMoon, etc.)" },
                new[] { "progression_set", "Set boss/NPC downed flags" },
                new[] { "trigger_event", "Trigger invasion/event (goblin, pirate, martian, stop)" },
                new[] { "tiles_fill", "Fill rectangle with tiles/walls/liquid or clear" },
                new[] { "npc_set_position", "Move NPC to position or to player" },
                new[] { "reflect_set_field", "Set static field value via reflection" },
                new[] { "trace_add", "Add method trace (dynamic Harmony patch)" },
                new[] { "trace_remove", "Remove method trace" },
                new[] { "watch_add", "Watch field for changes" },
                new[] { "watch_remove", "Remove field watch" },
                new[] { "exit_world", "Exit current world to title screen" },
                new[] { "join_world", "Join multiplayer world (character, ip with optional :port)" },
                new[] { "navigate", "Navigate menu (target: singleplayer, multiplayer, title, etc.)" }
            };
            foreach (var def in coreActionDefs)
                coreActions.Add(JsonObject(JsonPair("name", def[0]), JsonPair("description", def[1])));
            sections.Add(JsonArray("coreActions", coreActions));

            // Mod actions
            var modActions = new List<string>();
            foreach (var kv in ModActionRegistry.All)
            {
                try
                {
                    var actions = kv.Value.GetActions();
                    if (actions == null) continue;
                    foreach (var a in actions)
                    {
                        var actionPairs = new List<string>
                        {
                            JsonPair("name", kv.Key + "." + a.Name),
                            JsonPair("mod", kv.Key),
                            JsonPair("description", a.Description ?? "")
                        };
                        if (a.Params != null && a.Params.Count > 0)
                        {
                            var paramItems = new List<string>();
                            foreach (var p in a.Params)
                            {
                                paramItems.Add(JsonObject(
                                    JsonPair("name", p.Name),
                                    JsonPair("type", p.Type ?? "string"),
                                    JsonPair("required", p.Required),
                                    JsonPair("description", p.Description ?? "")
                                ));
                            }
                            actionPairs.Add(JsonArray("params", paramItems));
                        }
                        modActions.Add(JsonObject(actionPairs.ToArray()));
                    }
                }
                catch { }
            }
            sections.Add(JsonArray("modActions", modActions));

            // Debug commands
            var cmds = new List<string>();
            foreach (var cmd in CommandRegistry.GetCommands())
                cmds.Add(JsonObject(JsonPair("name", cmd.Name), JsonPair("description", cmd.Description)));
            sections.Add(JsonArray("commands", cmds));

            // Keybinds
            var kbs = new List<string>();
            foreach (var kb in KeybindManager.GetAllKeybinds())
            {
                kbs.Add(JsonObject(
                    JsonPair("id", kb.Id),
                    JsonPair("modId", kb.ModId),
                    JsonPair("label", kb.Label),
                    JsonPair("description", kb.Description)
                ));
            }
            sections.Add(JsonArray("keybinds", kbs));

            return JsonObject(sections.ToArray());
        }

        private string HandleModAction(string body, out int statusCode)
        {
            statusCode = 200;
            string modId = ExtractJsonString(body, "mod");
            string actionName = ExtractJsonString(body, "action");

            if (string.IsNullOrEmpty(modId) || string.IsNullOrEmpty(actionName))
            {
                statusCode = 400;
                return JsonObject(JsonPair("error", "Missing 'mod' or 'action' field"));
            }

            var provider = ModActionRegistry.GetProvider(modId);
            if (provider == null)
            {
                statusCode = 404;
                return JsonObject(JsonPair("error", $"No action provider for mod: {modId}"));
            }

            // Parse params from body — supports both nested {"params":{...}} and flat top-level keys
            var args = new Dictionary<string, string>();
            // First try "params" as a nested JSON object
            string paramsObj = ExtractJsonObject(body, "params");
            if (!string.IsNullOrEmpty(paramsObj))
            {
                try { ExtractAllJsonStrings(paramsObj, args); } catch { }
            }
            else
            {
                // Fallback: extract individual params from top-level body (excluding mod/action)
                try
                {
                    ExtractAllJsonStrings(body, args, "mod", "action");
                }
                catch { }
            }

            try
            {
                var result = MainThreadDispatcher.RunOnMainThread(() =>
                    provider.ExecuteAction(actionName, args));

                if (result == null)
                {
                    statusCode = 404;
                    return JsonObject(JsonPair("error", $"Unknown action: {actionName}"));
                }

                var pairs = new List<string>
                {
                    JsonPair("success", result.Success),
                    JsonPair("mod", modId),
                    JsonPair("action", actionName)
                };
                if (result.Message != null)
                    pairs.Add(JsonPair("message", result.Message));
                if (result.Data != null)
                {
                    foreach (var kv in result.Data)
                    {
                        if (kv.Value is bool bv) pairs.Add(JsonPair(kv.Key, bv));
                        else if (kv.Value is int iv) pairs.Add(JsonPair(kv.Key, iv));
                        else if (kv.Value is double dv) pairs.Add(JsonPair(kv.Key, dv));
                        else if (kv.Value is float fv) pairs.Add(JsonPair(kv.Key, (double)fv));
                        else pairs.Add(JsonPair(kv.Key, kv.Value?.ToString()));
                    }
                }
                return JsonObject(pairs.ToArray());
            }
            catch (Exception ex)
            {
                statusCode = 500;
                return JsonObject(JsonPair("error", $"Action failed: {ex.Message}"));
            }
        }

        /// <summary>
        /// Extract all string key-value pairs from a flat JSON object, excluding specified keys.
        /// Used by HandleModAction to pass through arbitrary params to mod action providers.
        /// </summary>
        private static void ExtractAllJsonStrings(string json, Dictionary<string, string> target, params string[] exclude)
        {
            var excludeSet = new HashSet<string>(exclude, StringComparer.OrdinalIgnoreCase);
            // Simple regex-free parser for flat {"key":"value",...} objects
            int i = 0;
            while (i < json.Length)
            {
                // Find next "key"
                int keyStart = json.IndexOf('"', i);
                if (keyStart < 0) break;
                int keyEnd = json.IndexOf('"', keyStart + 1);
                if (keyEnd < 0) break;
                string key = json.Substring(keyStart + 1, keyEnd - keyStart - 1);

                // Find colon
                int colon = json.IndexOf(':', keyEnd + 1);
                if (colon < 0) break;

                // Find value start
                int valStart = colon + 1;
                while (valStart < json.Length && json[valStart] == ' ') valStart++;

                if (valStart < json.Length && json[valStart] == '"')
                {
                    // String value (handle escaped quotes)
                    int valEnd = valStart + 1;
                    while (valEnd < json.Length)
                    {
                        if (json[valEnd] == '\\') { valEnd += 2; continue; }
                        if (json[valEnd] == '"') break;
                        valEnd++;
                    }
                    if (valEnd >= json.Length) break;
                    string value = json.Substring(valStart + 1, valEnd - valStart - 1);
                    if (!excludeSet.Contains(key))
                        target[key] = value;
                    i = valEnd + 1;
                }
                else
                {
                    // Non-string value — find next comma or brace
                    int end = valStart;
                    while (end < json.Length && json[end] != ',' && json[end] != '}') end++;
                    string value = json.Substring(valStart, end - valStart).Trim();
                    if (!excludeSet.Contains(key) && value != "null" && value != "{")
                        target[key] = value;
                    i = end + 1;
                }
            }
        }

        #endregion

        #region Phase 6: Inventory, Equipment, World Control

        private string HandleInventorySet(string body, out int statusCode)
        {
            statusCode = 200;
            try
            {
                if (!Game.InWorld) { statusCode = 400; return JsonObject(JsonPair("error", "Not in a world")); }

                int slot = ExtractJsonInt(body, "slot", -1);
                int type = ExtractJsonInt(body, "type", 0);
                int stack = ExtractJsonInt(body, "stack", 1);
                int prefix = ExtractJsonInt(body, "prefix", 0);

                if (slot < 0 || slot > 57) { statusCode = 400; return JsonObject(JsonPair("error", "Slot must be 0-57")); }

                string result = MainThreadDispatcher.RunOnMainThread(() =>
                {
                    var player = Main.LocalPlayer;
                    if (player == null) return "Player not available";
                    player.inventory[slot].SetDefaults(type);
                    if (type != 0)
                    {
                        player.inventory[slot].stack = stack;
                        if (prefix > 0) player.inventory[slot].Prefix(prefix);
                    }
                    string name = type == 0 ? "Air" : (player.inventory[slot].Name ?? "");
                    return $"OK:{name}";
                });

                if (result.StartsWith("OK:"))
                    return JsonObject(JsonPair("success", true), JsonPair("slot", slot), JsonPair("item", result.Substring(3)));
                statusCode = 400;
                return JsonObject(JsonPair("error", result));
            }
            catch (Exception ex) { statusCode = 500; return JsonObject(JsonPair("error", ex.Message)); }
        }

        private string HandleEquip(string body, out int statusCode)
        {
            statusCode = 200;
            try
            {
                if (!Game.InWorld) { statusCode = 400; return JsonObject(JsonPair("error", "Not in a world")); }

                string slot = ExtractJsonString(body, "slot");
                int index = ExtractJsonInt(body, "index", 0);
                int type = ExtractJsonInt(body, "type", 0);
                int prefix = ExtractJsonInt(body, "prefix", 0);

                string result = MainThreadDispatcher.RunOnMainThread(() =>
                {
                    var player = Main.LocalPlayer;
                    if (player == null) return "Player not available";

                    switch (slot)
                    {
                        case "armor":
                            if (index < 0 || index > 2) return "Armor index must be 0-2";
                            player.armor[index].SetDefaults(type);
                            if (prefix > 0) player.armor[index].Prefix(prefix);
                            break;
                        case "accessory":
                            if (index < 0 || index > 6) return "Accessory index must be 0-6";
                            player.armor[3 + index].SetDefaults(type);
                            if (prefix > 0) player.armor[3 + index].Prefix(prefix);
                            break;
                        case "vanity_armor":
                            if (index < 0 || index > 2) return "Vanity armor index must be 0-2";
                            player.armor[10 + index].SetDefaults(type);
                            break;
                        case "vanity_accessory":
                            if (index < 0 || index > 6) return "Vanity accessory index must be 0-6";
                            player.armor[13 + index].SetDefaults(type);
                            break;
                        case "dye":
                            if (index < 0 || index >= player.dye.Length) return $"Dye index must be 0-{player.dye.Length - 1}";
                            player.dye[index].SetDefaults(type);
                            break;
                        case "misc":
                            if (index < 0 || index >= 5) return "Misc index must be 0-4 (pet, lightPet, minecart, mount, grapple)";
                            player.miscEquips[index].SetDefaults(type);
                            break;
                        case "misc_dye":
                            if (index < 0 || index >= 5) return "Misc dye index must be 0-4";
                            player.miscDyes[index].SetDefaults(type);
                            break;
                        default:
                            return $"Unknown slot '{slot}'. Use: armor, accessory, vanity_armor, vanity_accessory, dye, misc, misc_dye";
                    }
                    return null;
                });

                if (result != null) { statusCode = 400; return JsonObject(JsonPair("error", result)); }
                return JsonObject(JsonPair("success", true), JsonPair("slot", slot), JsonPair("index", index), JsonPair("type", type));
            }
            catch (Exception ex) { statusCode = 500; return JsonObject(JsonPair("error", ex.Message)); }
        }

        private string HandleHotbarSelect(string body, out int statusCode)
        {
            statusCode = 200;
            try
            {
                if (!Game.InWorld) { statusCode = 400; return JsonObject(JsonPair("error", "Not in a world")); }

                int slot = ExtractJsonInt(body, "slot", -1);
                if (slot < 0 || slot > 9) { statusCode = 400; return JsonObject(JsonPair("error", "Slot must be 0-9")); }

                string result = MainThreadDispatcher.RunOnMainThread(() =>
                {
                    var player = Main.LocalPlayer;
                    if (player == null) return "Player not available";
                    try
                    {
                        // selectedItemState is a public struct with a Select(int) method
                        var stateField = player.GetType().GetField("selectedItemState",
                            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                        if (stateField != null)
                        {
                            var state = stateField.GetValue(player);
                            var selectMethod = state.GetType().GetMethod("Select",
                                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                            if (selectMethod != null)
                            {
                                // SelectedItemState is a struct — box, invoke, write back
                                selectMethod.Invoke(state, new object[] { slot });
                                stateField.SetValue(player, state);
                                return null;
                            }
                            // Fallback: set private 'selected' field directly
                            var selectedField = state.GetType().GetField("selected",
                                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                            if (selectedField != null)
                            {
                                selectedField.SetValue(state, slot);
                                stateField.SetValue(player, state);
                                return null;
                            }
                        }
                        return "Could not find selectedItemState on player";
                    }
                    catch (Exception ex) { return ex.Message; }
                });

                if (result != null) { statusCode = 400; return JsonObject(JsonPair("error", result)); }
                return JsonObject(JsonPair("success", true), JsonPair("slot", slot));
            }
            catch (Exception ex) { statusCode = 500; return JsonObject(JsonPair("error", ex.Message)); }
        }

        private string HandleChestSet(string body, out int statusCode)
        {
            statusCode = 200;
            try
            {
                if (!Game.InWorld) { statusCode = 400; return JsonObject(JsonPair("error", "Not in a world")); }

                int chestIndex = ExtractJsonInt(body, "chestIndex", -1);
                if (chestIndex < 0) { statusCode = 400; return JsonObject(JsonPair("error", "Missing chestIndex")); }

                string action = ExtractJsonString(body, "action");
                int slot = ExtractJsonInt(body, "slot", -1);
                int type = ExtractJsonInt(body, "type", 0);
                int stack = ExtractJsonInt(body, "stack", 1);
                int prefix = ExtractJsonInt(body, "prefix", 0);

                string result = MainThreadDispatcher.RunOnMainThread(() =>
                {
                    if (chestIndex >= Main.maxChests || Main.chest[chestIndex] == null)
                        return "Chest not found";

                    var chest = Main.chest[chestIndex];

                    if (action == "clear")
                    {
                        for (int i = 0; i < chest.item.Length; i++)
                            chest.item[i].SetDefaults(0);
                        return null;
                    }

                    if (action == "fill")
                    {
                        // Parse items array manually from body
                        int itemsStart = body.IndexOf("\"items\"");
                        if (itemsStart < 0) return "Missing 'items' array for fill action";

                        int arrStart = body.IndexOf('[', itemsStart);
                        int arrEnd = body.LastIndexOf(']');
                        if (arrStart < 0 || arrEnd < 0) return "Invalid items array";

                        string itemsStr = body.Substring(arrStart + 1, arrEnd - arrStart - 1);
                        // Parse each {...} object
                        int pos = 0;
                        int filled = 0;
                        while (pos < itemsStr.Length)
                        {
                            int objStart = itemsStr.IndexOf('{', pos);
                            if (objStart < 0) break;
                            int objEnd = itemsStr.IndexOf('}', objStart);
                            if (objEnd < 0) break;
                            string obj = itemsStr.Substring(objStart, objEnd - objStart + 1);

                            int s = ExtractJsonInt(obj, "slot", filled);
                            int t = ExtractJsonInt(obj, "type", 0);
                            int st = ExtractJsonInt(obj, "stack", 1);
                            int p = ExtractJsonInt(obj, "prefix", 0);

                            if (s >= 0 && s < chest.item.Length)
                            {
                                chest.item[s].SetDefaults(t);
                                if (t != 0)
                                {
                                    chest.item[s].stack = st;
                                    if (p > 0) chest.item[s].Prefix(p);
                                }
                                filled++;
                            }
                            pos = objEnd + 1;
                        }
                        return null;
                    }

                    // Single slot set
                    if (slot < 0 || slot >= chest.item.Length)
                        return $"Slot must be 0-{chest.item.Length - 1}";

                    chest.item[slot].SetDefaults(type);
                    if (type != 0)
                    {
                        chest.item[slot].stack = stack;
                        if (prefix > 0) chest.item[slot].Prefix(prefix);
                    }
                    return null;
                });

                if (result != null) { statusCode = 400; return JsonObject(JsonPair("error", result)); }
                return JsonObject(JsonPair("success", true));
            }
            catch (Exception ex) { statusCode = 500; return JsonObject(JsonPair("error", ex.Message)); }
        }

        private string HandleChestOpen(string body, out int statusCode)
        {
            statusCode = 200;
            try
            {
                if (!Game.InWorld) { statusCode = 400; return JsonObject(JsonPair("error", "Not in a world")); }

                int chestIndex = ExtractJsonInt(body, "index", -1);
                int tileX = ExtractJsonInt(body, "x", -1);
                int tileY = ExtractJsonInt(body, "y", -1);

                string result = MainThreadDispatcher.RunOnMainThread(() =>
                {
                    int idx = chestIndex;

                    // Find chest by tile coords if no index given
                    if (idx < 0 && tileX >= 0 && tileY >= 0)
                    {
                        var chestType = Type.GetType("Terraria.Chest, Terraria")
                            ?? System.Reflection.Assembly.Load("Terraria").GetType("Terraria.Chest");
                        var findChest = chestType?.GetMethod("FindChest",
                            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static,
                            null, new[] { typeof(int), typeof(int) }, null);
                        if (findChest != null)
                        {
                            idx = (int)findChest.Invoke(null, new object[] { tileX, tileY });
                            // Try nearby tiles (painting chests are 3x2)
                            if (idx < 0)
                            {
                                for (int dx = -2; dx <= 2 && idx < 0; dx++)
                                    for (int dy = -1; dy <= 1 && idx < 0; dy++)
                                        if (dx != 0 || dy != 0)
                                            idx = (int)findChest.Invoke(null, new object[] { tileX + dx, tileY + dy });
                            }
                        }
                        if (idx < 0) return "No chest found at or near those coordinates";
                    }

                    if (idx < 0) return "Provide 'index' or 'x'+'y' tile coordinates";
                    if (idx >= Main.maxChests || Main.chest[idx] == null)
                        return "Chest not found at index " + idx;

                    var chest = Main.chest[idx];

                    // Call Player.OpenChest via reflection for full UI (inventory, glow, etc.)
                    try
                    {
                        var openMethod = typeof(Terraria.Player).GetMethod("OpenChest",
                            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                        if (openMethod != null)
                        {
                            openMethod.Invoke(Main.LocalPlayer, new object[] { chest.x, chest.y, idx });
                            // Set the chat text to chest name (vanilla does this in TileInteractionsUse)
                            Main.npcChatText = chest.name ?? "";
                            if (string.IsNullOrEmpty(Main.npcChatText))
                            {
                                // Use default chest name from Lang.chestType
                                try
                                {
                                    var tile = Main.tile[chest.x, chest.y];
                                    if (tile != null && tile.type == 21)
                                    {
                                        int style = tile.frameX / 36;
                                        var langType = typeof(Main).Assembly.GetType("Terraria.Lang");
                                        var chestTypeField = langType?.GetField("chestType",
                                            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                                        var arr = chestTypeField?.GetValue(null) as Terraria.Localization.LocalizedText[];
                                        if (arr != null && style < arr.Length)
                                            Main.npcChatText = arr[style].Value;
                                    }
                                }
                                catch { }
                            }
                        }
                        else
                        {
                            // Fallback: manual open
                            Main.LocalPlayer.chest = idx;
                            Main.playerInventory = true;
                        }
                    }
                    catch
                    {
                        Main.LocalPlayer.chest = idx;
                        Main.playerInventory = true;
                    }

                    // Build item list
                    var sb = new System.Text.StringBuilder("[");
                    bool first = true;
                    for (int i = 0; i < chest.item.Length; i++)
                    {
                        if (chest.item[i] == null || chest.item[i].type == 0) continue;
                        if (!first) sb.Append(",");
                        first = false;
                        sb.Append("{\"slot\":").Append(i)
                          .Append(",\"type\":").Append(chest.item[i].type)
                          .Append(",\"stack\":").Append(chest.item[i].stack)
                          .Append(",\"name\":\"").Append(chest.item[i].Name.Replace("\"", "\\\"")).Append("\"}");
                    }
                    sb.Append("]");

                    return "{\"success\":true,\"chestIndex\":" + idx +
                           ",\"x\":" + chest.x + ",\"y\":" + chest.y +
                           ",\"name\":\"" + (chest.name ?? "").Replace("\"", "\\\"") + "\"" +
                           ",\"items\":" + sb + "}";
                });

                if (result != null && result.StartsWith("{\"success\"")) return result;
                if (result != null) { statusCode = 400; return JsonObject(JsonPair("error", result)); }
                return JsonObject(JsonPair("success", true));
            }
            catch (Exception ex) { statusCode = 500; return JsonObject(JsonPair("error", ex.Message)); }
        }

        private string HandleTeleportXY(string body, out int statusCode)
        {
            // Delegate to existing teleport handler which already supports x,y
            return HandlePlayerTeleport(body, out statusCode);
        }

        private string HandleProjectiles(HttpListenerRequest request)
        {
            try
            {
                if (!Game.InWorld)
                    return JsonObject(JsonPair("error", "Not in a world"));

                string filterType = request.QueryString["type"];
                string filterOwner = request.QueryString["owner"];
                string filterHostile = request.QueryString["hostile"];

                int ftType = -1, ftOwner = -1;
                bool? ftHostile = null;
                if (filterType != null) int.TryParse(filterType, out ftType);
                if (filterOwner != null) int.TryParse(filterOwner, out ftOwner);
                if (filterHostile != null) ftHostile = filterHostile == "true";

                var sb = new StringBuilder(4096);
                sb.Append("{\"projectiles\":[");
                bool first = true;

                for (int i = 0; i < Main.maxProjectiles; i++)
                {
                    var proj = Main.projectile[i];
                    if (proj == null || !proj.active) continue;
                    if (ftType >= 0 && proj.type != ftType) continue;
                    if (ftOwner >= 0 && proj.owner != ftOwner) continue;
                    if (ftHostile.HasValue && proj.hostile != ftHostile.Value) continue;

                    if (!first) sb.Append(",");
                    first = false;
                    sb.Append("{\"index\":").Append(i);
                    sb.Append(",\"type\":").Append(proj.type);
                    string name = "";
                    try { name = proj.Name ?? ""; } catch { }
                    sb.Append(",\"name\":\"").Append(EscapeJson(name)).Append("\"");
                    sb.Append(",\"x\":").Append((int)proj.position.X);
                    sb.Append(",\"y\":").Append((int)proj.position.Y);
                    sb.Append(",\"velocityX\":").Append(Math.Round(proj.velocity.X, 2));
                    sb.Append(",\"velocityY\":").Append(Math.Round(proj.velocity.Y, 2));
                    sb.Append(",\"damage\":").Append(proj.damage);
                    sb.Append(",\"owner\":").Append(proj.owner);
                    sb.Append(",\"hostile\":").Append(proj.hostile ? "true" : "false");
                    sb.Append(",\"friendly\":").Append(proj.friendly ? "true" : "false");
                    sb.Append(",\"timeLeft\":").Append(proj.timeLeft);
                    sb.Append(",\"penetrate\":").Append(proj.penetrate);
                    sb.Append("}");
                }
                sb.Append("]}");
                return sb.ToString();
            }
            catch (Exception ex) { return JsonObject(JsonPair("error", ex.Message)); }
        }

        private string HandleWorldSet(string body, out int statusCode)
        {
            statusCode = 200;
            try
            {
                if (!Game.InWorld) { statusCode = 400; return JsonObject(JsonPair("error", "Not in a world")); }

                string field = ExtractJsonString(body, "field");
                if (string.IsNullOrEmpty(field)) { statusCode = 400; return JsonObject(JsonPair("error", "Missing 'field'")); }

                bool boolVal = ExtractJsonBool(body, "value", false);
                int intVal = ExtractJsonInt(body, "value", 0);

                string result = MainThreadDispatcher.RunOnMainThread(() =>
                {
                    switch (field)
                    {
                        case "hardMode":
                            Main.hardMode = boolVal;
                            return null;
                        case "dayTime":
                            Main.dayTime = boolVal;
                            return null;
                        case "time":
                            Main.time = ExtractJsonDouble(body, "value", (double)intVal);
                            return null;
                        case "bloodMoon":
                            Main.bloodMoon = boolVal;
                            return null;
                        case "eclipse":
                            Main.eclipse = boolVal;
                            return null;
                        case "pumpkinMoon":
                            Main.pumpkinMoon = boolVal;
                            return null;
                        case "snowMoon":
                            Main.snowMoon = boolVal;
                            return null;
                        case "raining":
                            Main.raining = boolVal;
                            if (boolVal) { Main.rainTime = 86400; Main.maxRaining = 0.5f; }
                            return null;
                        case "sandStorm":
                            try
                            {
                                var sandstormType = Type.GetType("Terraria.GameContent.Events.Sandstorm, Terraria")
                                    ?? System.Reflection.Assembly.Load("Terraria").GetType("Terraria.GameContent.Events.Sandstorm");
                                if (sandstormType != null)
                                {
                                    var happeningField = sandstormType.GetField("Happening", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                                    var timeLeftField = sandstormType.GetField("TimeLeft", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                                    if (happeningField != null) happeningField.SetValue(null, boolVal);
                                    if (boolVal && timeLeftField != null) timeLeftField.SetValue(null, 86400);
                                }
                                return null;
                            }
                            catch (Exception ex) { return $"Failed to set sandStorm: {ex.Message}"; }
                        default:
                            return $"Unknown field '{field}'. Supported: hardMode, dayTime, time, bloodMoon, eclipse, pumpkinMoon, snowMoon, raining, sandStorm";
                    }
                });

                if (result != null) { statusCode = 400; return JsonObject(JsonPair("error", result)); }
                return JsonObject(JsonPair("success", true), JsonPair("field", field));
            }
            catch (Exception ex) { statusCode = 500; return JsonObject(JsonPair("error", ex.Message)); }
        }

        private string HandleProgressionSet(string body, out int statusCode)
        {
            statusCode = 200;
            try
            {
                if (!Game.InWorld) { statusCode = 400; return JsonObject(JsonPair("error", "Not in a world")); }

                // Support single flag or batch flags
                string singleFlag = ExtractJsonString(body, "flag");
                bool singleValue = ExtractJsonBool(body, "value", false);

                var flagsToSet = new Dictionary<string, bool>();
                if (!string.IsNullOrEmpty(singleFlag))
                {
                    flagsToSet[singleFlag] = singleValue;
                }
                else
                {
                    // Try batch flags: {"flags": {"downedBoss1": true, ...}}
                    int flagsStart = body.IndexOf("\"flags\"");
                    if (flagsStart >= 0)
                    {
                        int objStart = body.IndexOf('{', flagsStart + 7);
                        int objEnd = body.IndexOf('}', objStart + 1);
                        if (objStart >= 0 && objEnd >= 0)
                        {
                            string flagsStr = body.Substring(objStart + 1, objEnd - objStart - 1);
                            // Parse "key": true/false pairs
                            var parts = flagsStr.Split(',');
                            foreach (var part in parts)
                            {
                                var kv = part.Split(':');
                                if (kv.Length == 2)
                                {
                                    string key = kv[0].Trim().Trim('"');
                                    bool val = kv[1].Trim().ToLower() == "true";
                                    if (!string.IsNullOrEmpty(key)) flagsToSet[key] = val;
                                }
                            }
                        }
                    }
                }

                if (flagsToSet.Count == 0) { statusCode = 400; return JsonObject(JsonPair("error", "Provide 'flag'+'value' or 'flags' object")); }

                string result = MainThreadDispatcher.RunOnMainThread(() =>
                {
                    var npcType = typeof(Terraria.NPC);
                    var setFlags = new List<string>();

                    foreach (var kv in flagsToSet)
                    {
                        if (kv.Key == "hardMode")
                        {
                            Main.hardMode = kv.Value;
                            setFlags.Add(kv.Key);
                            continue;
                        }

                        var field = npcType.GetField(kv.Key, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                        if (field != null && field.FieldType == typeof(bool))
                        {
                            field.SetValue(null, kv.Value);
                            setFlags.Add(kv.Key);
                        }
                        else
                        {
                            return $"Unknown flag '{kv.Key}'";
                        }
                    }
                    return "OK:" + string.Join(",", setFlags);
                });

                if (result != null && result.StartsWith("OK:"))
                    return JsonObject(JsonPair("success", true), JsonPair("flagsSet", result.Substring(3)));
                statusCode = 400;
                return JsonObject(JsonPair("error", result ?? "Unknown error"));
            }
            catch (Exception ex) { statusCode = 500; return JsonObject(JsonPair("error", ex.Message)); }
        }

        private string HandleEventTrigger(string body, out int statusCode)
        {
            statusCode = 200;
            try
            {
                if (!Game.InWorld) { statusCode = 400; return JsonObject(JsonPair("error", "Not in a world")); }

                string eventName = ExtractJsonString(body, "event");
                if (string.IsNullOrEmpty(eventName)) { statusCode = 400; return JsonObject(JsonPair("error", "Missing 'event'")); }

                string result = MainThreadDispatcher.RunOnMainThread(() =>
                {
                    switch (eventName)
                    {
                        case "goblin_invasion":
                            Main.StartInvasion(1);
                            return null;
                        case "frost_legion":
                            Main.StartInvasion(2);
                            return null;
                        case "pirate_invasion":
                            Main.StartInvasion(3);
                            return null;
                        case "martian_madness":
                            Main.StartInvasion(4);
                            return null;
                        case "stop":
                            Main.invasionType = 0;
                            Main.invasionSize = 0;
                            return null;
                        default:
                            return $"Unknown event '{eventName}'. Supported: goblin_invasion, frost_legion, pirate_invasion, martian_madness, stop";
                    }
                });

                if (result != null) { statusCode = 400; return JsonObject(JsonPair("error", result)); }
                return JsonObject(JsonPair("success", true), JsonPair("event", eventName));
            }
            catch (Exception ex) { statusCode = 500; return JsonObject(JsonPair("error", ex.Message)); }
        }

        private string HandleTilesFill(string body, out int statusCode)
        {
            statusCode = 200;
            try
            {
                if (Terraria.Main.gameMenu) { statusCode = 400; return JsonObject(JsonPair("error", "Not in a world")); }

                int x = ExtractJsonInt(body, "x", -1);
                int y = ExtractJsonInt(body, "y", -1);
                int w = ExtractJsonInt(body, "w", 1);
                int h = ExtractJsonInt(body, "h", 1);
                int type = ExtractJsonInt(body, "type", -1);
                int wall = ExtractJsonInt(body, "wall", -1);
                string action = ExtractJsonString(body, "action");
                string liquid = ExtractJsonString(body, "liquid");
                int amount = ExtractJsonInt(body, "amount", 255);

                if (x < 0 || y < 0) { statusCode = 400; return JsonObject(JsonPair("error", "Missing x or y")); }
                if (w > 100 || h > 100) { statusCode = 400; return JsonObject(JsonPair("error", "Max 100x100 area")); }

                string result = MainThreadDispatcher.RunOnMainThread(() =>
                {
                    int count = 0;
                    for (int tx = x; tx < x + w; tx++)
                    {
                        for (int ty = y; ty < y + h; ty++)
                        {
                            if (tx < 0 || tx >= Main.maxTilesX || ty < 0 || ty >= Main.maxTilesY) continue;

                            if (action == "clear")
                            {
                                Terraria.WorldGen.KillTile(tx, ty, false, false, false);
                                count++;
                            }
                            else if (action == "clear_wall")
                            {
                                Terraria.WorldGen.KillWall(tx, ty, false);
                                count++;
                            }
                            else if (liquid != null)
                            {
                                byte liqType = 0;
                                switch (liquid) { case "water": liqType = 0; break; case "lava": liqType = 1; break; case "honey": liqType = 2; break; case "shimmer": liqType = 3; break; }
                                Terraria.WorldGen.PlaceLiquid(tx, ty, liqType, (byte)amount);
                                count++;
                            }
                            else if (wall >= 0)
                            {
                                Terraria.WorldGen.PlaceWall(tx, ty, wall, false);
                                count++;
                            }
                            else if (type >= 0)
                            {
                                Terraria.WorldGen.PlaceTile(tx, ty, type, false, true);
                                count++;
                            }
                        }
                    }
                    return count.ToString();
                });

                return JsonObject(JsonPair("success", true), JsonPair("tilesModified", int.Parse(result)));
            }
            catch (Exception ex) { statusCode = 500; return JsonObject(JsonPair("error", ex.Message)); }
        }

        private string HandleNpcSetPosition(string body, out int statusCode)
        {
            statusCode = 200;
            try
            {
                if (!Game.InWorld) { statusCode = 400; return JsonObject(JsonPair("error", "Not in a world")); }

                int index = ExtractJsonInt(body, "index", -1);
                int npcType = ExtractJsonInt(body, "type", -1);
                int x = ExtractJsonInt(body, "x", -1);
                int y = ExtractJsonInt(body, "y", -1);
                bool toPlayer = ExtractJsonBool(body, "toPlayer", false);

                string result = MainThreadDispatcher.RunOnMainThread(() =>
                {
                    Terraria.NPC target = null;

                    if (index >= 0 && index < Main.maxNPCs)
                    {
                        target = Main.npc[index];
                        if (target == null || !target.active) return "NPC at index not active";
                    }
                    else if (npcType >= 0)
                    {
                        for (int i = 0; i < Main.maxNPCs; i++)
                        {
                            if (Main.npc[i] != null && Main.npc[i].active && Main.npc[i].type == npcType)
                            {
                                target = Main.npc[i];
                                break;
                            }
                        }
                        if (target == null) return $"No active NPC of type {npcType} found";
                    }
                    else return "Provide 'index' or 'type'";

                    if (toPlayer)
                    {
                        var player = Main.LocalPlayer;
                        if (player == null) return "Player not available";
                        target.position = player.position;
                    }
                    else if (x >= 0 && y >= 0)
                    {
                        target.position = new Microsoft.Xna.Framework.Vector2(x * 16f, y * 16f);
                    }
                    else return "Provide 'x'+'y' or 'toPlayer: true'";

                    target.netUpdate = true;
                    return null;
                });

                if (result != null) { statusCode = 400; return JsonObject(JsonPair("error", result)); }
                return JsonObject(JsonPair("success", true));
            }
            catch (Exception ex) { statusCode = 500; return JsonObject(JsonPair("error", ex.Message)); }
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
        /// Extract a nested JSON object value by key, returning the substring including braces.
        /// Handles: {"key": {...}} patterns. Returns null if not found or value is not an object.
        /// </summary>
        private static string ExtractJsonObject(string json, string key)
        {
            if (string.IsNullOrEmpty(json)) return null;
            string searchKey = $"\"{key}\"";
            int keyIndex = json.IndexOf(searchKey, StringComparison.Ordinal);
            if (keyIndex < 0) return null;
            int colonIndex = json.IndexOf(':', keyIndex + searchKey.Length);
            if (colonIndex < 0) return null;
            // Skip whitespace after colon
            int i = colonIndex + 1;
            while (i < json.Length && char.IsWhiteSpace(json[i])) i++;
            if (i >= json.Length || json[i] != '{') return null;
            // Match braces
            int depth = 1;
            int start = i;
            i++;
            while (i < json.Length && depth > 0)
            {
                if (json[i] == '{') depth++;
                else if (json[i] == '}') depth--;
                else if (json[i] == '"') { i++; while (i < json.Length && json[i] != '"') { if (json[i] == '\\') i++; i++; } }
                i++;
            }
            if (depth != 0) return null;
            return json.Substring(start, i - start);
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

        private static double ExtractJsonDouble(string json, string key, double defaultValue = 0.0)
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

            // Read digits, optional minus, decimal point
            int valueEnd = valueStart;
            if (valueEnd < json.Length && json[valueEnd] == '-')
                valueEnd++;
            while (valueEnd < json.Length && (char.IsDigit(json[valueEnd]) || json[valueEnd] == '.'))
                valueEnd++;

            if (valueEnd == valueStart) return defaultValue;

            string numStr = json.Substring(valueStart, valueEnd - valueStart);
            if (double.TryParse(numStr, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double result))
                return result;

            return defaultValue;
        }

        private static bool ExtractJsonBool(string json, string key, bool defaultValue = false)
        {
            if (string.IsNullOrEmpty(json)) return defaultValue;

            string searchKey = $"\"{key}\"";
            int keyIndex = json.IndexOf(searchKey, StringComparison.Ordinal);
            if (keyIndex < 0) return defaultValue;

            int colonIndex = json.IndexOf(':', keyIndex + searchKey.Length);
            if (colonIndex < 0) return defaultValue;

            int valueStart = colonIndex + 1;
            while (valueStart < json.Length && char.IsWhiteSpace(json[valueStart]))
                valueStart++;

            if (valueStart >= json.Length) return defaultValue;

            if (json.Length >= valueStart + 4 && json.Substring(valueStart, 4) == "true")  return true;
            if (json.Length >= valueStart + 5 && json.Substring(valueStart, 5) == "false") return false;
            return defaultValue;
        }

        #endregion
    }
}
