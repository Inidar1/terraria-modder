using System;
using System.IO;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading;
using Terraria;
using TerrariaModder.Core.Assets;
using TerrariaModder.Core.Config;
using TerrariaModder.Core.Debug;
using TerrariaModder.Core.Logging;
using TerrariaModder.Core.Manifest;
using TerrariaModder.Core.Permissions;

namespace TerrariaModder.Core.Server
{
    /// <summary>
    /// HTTP management API for the dedicated server, exposed on port 7879 (default).
    ///
    /// Auth: every request requires Authorization: Bearer &lt;key&gt; header matching
    /// ServerConfig.ManagementApiKey. Localhost requests are exempt by default.
    ///
    /// Endpoints:
    ///   GET  /api/server/status          — uptime, player count, world name
    ///   GET  /api/server/mods            — loaded mods with version + multiplayer category
    ///   GET  /api/server/players         — connected players (name, slot, role)
    ///   GET  /api/server/config          — all [Server] config values across all mods
    ///   GET  /api/server/config/{modId}  — one mod's [Server] config
    ///   POST /api/server/config/{modId}/{key} — set a [Server] config value (body: plain string value)
    ///   POST /api/server/time            — set world time (body: dawn|noon|dusk|night)
    ///   POST /api/server/players/op      — promote player (body: player name)
    ///   POST /api/server/players/deop    — demote player (body: player name)
    ///   POST /api/server/players/kick    — kick player (body: player name)
    ///   POST /api/server/command         — execute console command (body: command string)
    ///   POST /api/server/stop            — graceful shutdown
    ///   POST /api/management/give        — grant custom item to player (body: JSON)
    ///   GET  /api/management/audit-items — list pending item grants by mod
    /// </summary>
    internal sealed class ServerManagementApi : IDisposable
    {
        private readonly HttpListener _listener;
        private readonly ILogger _log;
        private readonly ServerConfig _config;
        private readonly DateTime _startTime;
        private Thread _thread;
        private volatile bool _running;

        public ServerManagementApi(ILogger log, ServerConfig config)
        {
            _log = log;
            _config = config;
            _startTime = DateTime.UtcNow;
            _listener = new HttpListener();

            // Use localhost/127.0.0.1 instead of * to avoid needing Windows URL ACL registration.
            // External callers on the same machine still work; remote callers use SSH tunneling.
            _listener.Prefixes.Add($"http://localhost:{_config.ManagementApiPort}/");
            _listener.Prefixes.Add($"http://127.0.0.1:{_config.ManagementApiPort}/");
        }

        public void Start()
        {
            if (_running) return;

            try
            {
                _listener.Start();
                _running = true;
                _thread = new Thread(ListenLoop) { Name = "ServerManagementApi", IsBackground = true };
                _thread.Start();
                _log.Info($"[ServerManagementApi] Started on port {_config.ManagementApiPort}");
            }
            catch (HttpListenerException ex)
            {
                _log.Error($"[ServerManagementApi] Failed to start on port {_config.ManagementApiPort}: {ex.Message}");
                try { _listener.Close(); } catch { }
            }
            catch (Exception ex)
            {
                _log.Error($"[ServerManagementApi] Failed to start: {ex.Message}");
                try { _listener.Close(); } catch { }
            }
        }

        public void Stop()
        {
            if (!_running) return;
            _running = false;
            try { _listener.Stop(); } catch { }
            _log.Info("[ServerManagementApi] Stopped");
        }

        public void Dispose()
        {
            Stop();
            try { _listener.Close(); } catch { }
        }

        private void ListenLoop()
        {
            while (_running)
            {
                try
                {
                    var ctx = _listener.GetContext();
                    ThreadPool.QueueUserWorkItem(_ => HandleRequest(ctx));
                }
                catch (HttpListenerException) when (!_running) { }
                catch (ObjectDisposedException) when (!_running) { }
                catch (Exception ex)
                {
                    if (_running) _log.Warn($"[ServerManagementApi] Listener error: {ex.Message}");
                }
            }
        }

        // ─── Auth ─────────────────────────────────────────────────────────────

        private bool IsAuthorized(HttpListenerRequest req)
        {
            if (_config.ManagementApiLocalhostExempt && IsLocalhost(req))
                return true;
            string auth = req.Headers["Authorization"] ?? "";
            return auth == $"Bearer {_config.ManagementApiKey}";
        }

        private static bool IsLocalhost(HttpListenerRequest req)
        {
            var addr = req.RemoteEndPoint?.Address;
            if (addr == null) return false;
            return addr.Equals(IPAddress.Loopback)
                || addr.Equals(IPAddress.IPv6Loopback)
                || addr.ToString() == "::1"
                || addr.ToString() == "127.0.0.1";
        }

        // ─── Request dispatch ─────────────────────────────────────────────────

        private void HandleRequest(HttpListenerContext ctx)
        {
            var req = ctx.Request;
            var resp = ctx.Response;

            try
            {
                string path = req.Url.AbsolutePath.TrimEnd('/');
                string method = req.HttpMethod;

                _log.Debug($"[ServerManagementApi] {method} {path}");

                // Block browser cross-origin requests (CSRF protection)
                if (req.Headers["Origin"] != null)
                {
                    Send(resp, 403, "{\"error\":\"Forbidden: browser requests blocked\"}");
                    return;
                }

                if (!IsAuthorized(req))
                {
                    Send(resp, 401, "{\"error\":\"Unauthorized\"}");
                    return;
                }

                // GET routes
                if (method == "GET")
                {
                    if (path == "/api/server/status")           { Send(resp, 200, HandleStatus()); return; }
                    if (path == "/api/server/mods")             { Send(resp, 200, HandleMods()); return; }
                    if (path == "/api/server/players")          { Send(resp, 200, HandlePlayers()); return; }
                    if (path == "/api/server/bans")             { Send(resp, 200, HandleBanList()); return; }
                    if (path == "/api/server/config")           { Send(resp, 200, HandleConfigAll()); return; }
                    if (path == "/api/management/audit-items")  { Send(resp, 200, HandleAuditItems()); return; }
                    if (path.StartsWith("/api/server/config/") && path.IndexOf('/', "/api/server/config/".Length) < 0)
                    {
                        string modId = path.Substring("/api/server/config/".Length);
                        Send(resp, 200, HandleConfigMod(modId));
                        return;
                    }
                }

                // POST routes
                if (method == "POST")
                {
                    // POST /api/server/config/{modId}/{key}
                    if (path.StartsWith("/api/server/config/"))
                    {
                        string rest = path.Substring("/api/server/config/".Length);
                        int slash = rest.LastIndexOf('/');
                        if (slash > 0)
                        {
                            string modId = rest.Substring(0, slash);
                            string key = rest.Substring(slash + 1);
                            string value = ReadBody(req);
                            Send(resp, 200, HandleConfigSet(modId, key, value));
                            return;
                        }
                    }

                    if (path == "/api/server/mods/enable")   { Send(resp, 200, HandleModEnable(ReadBody(req), true)); return; }
                    if (path == "/api/server/mods/disable")  { Send(resp, 200, HandleModEnable(ReadBody(req), false)); return; }
                    if (path == "/api/server/time")          { Send(resp, 200, HandleTime(ReadBody(req))); return; }
                    if (path == "/api/server/players/op")    { Send(resp, 200, HandleOp(ReadBody(req), promote: true)); return; }
                    if (path == "/api/server/players/deop")  { Send(resp, 200, HandleOp(ReadBody(req), promote: false)); return; }
                    if (path == "/api/server/players/kick")  { Send(resp, 200, HandleKick(ReadBody(req))); return; }
                    if (path == "/api/server/bans/ban")      { Send(resp, 200, HandleBan(ReadBody(req))); return; }
                    if (path == "/api/server/bans/unban")    { Send(resp, 200, HandleUnban(ReadBody(req))); return; }
                    if (path == "/api/server/command")       { Send(resp, 200, HandleCommand(ReadBody(req))); return; }
                    if (path == "/api/server/stop")          { Send(resp, 200, HandleStop()); return; }
                    if (path == "/api/management/give")      { Send(resp, 200, HandleGiveItem(ReadBody(req))); return; }
                }

                Send(resp, 404, "{\"error\":\"Not found\"}");
            }
            catch (Exception ex)
            {
                _log.Warn($"[ServerManagementApi] Request error: {ex.Message}");
                try { Send(resp, 500, $"{{\"error\":\"{Esc(ex.Message)}\"}}"); } catch { }
            }
        }

        // ─── Handlers ─────────────────────────────────────────────────────────

        private static string GetWorldName()
        {
            // Direct reference (fast path — works in client/H&P mode)
            try
            {
                string n = Main.worldName;
                if (!string.IsNullOrEmpty(n)) return n;
            }
            catch { }

            // Reflection fallback: in server mode, the compile-time Terraria reference may resolve to
            // Terraria.exe (default Load context) while running server types are in TerrariaServer.exe
            // (LoadFrom context). Both have assembly name "Terraria". Identify the right one by
            // checking Main.dedServ == true (only true in the actual running server instance).
            try
            {
                string fallback = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    var asmN = asm.GetName().Name;
                    if (asmN != "Terraria" && asmN != "TerrariaServer") continue;
                    try
                    {
                        var mainType = asm.GetType("Terraria.Main");
                        if (mainType == null) continue;
                        var dedServField   = mainType.GetField("dedServ",   BindingFlags.Public | BindingFlags.Static);
                        var worldNameField = mainType.GetField("worldName", BindingFlags.Public | BindingFlags.Static);
                        if (worldNameField == null) continue;
                        string n = (string)worldNameField.GetValue(null);
                        bool isDedServ = dedServField != null && (bool)dedServField.GetValue(null);
                        if (isDedServ && !string.IsNullOrEmpty(n)) return n; // found server assembly with name
                        if (!string.IsNullOrEmpty(n)) fallback = n;
                    }
                    catch { }
                }
                if (!string.IsNullOrEmpty(fallback)) return fallback;
            }
            catch { }

            // Last resort: server-config world name (the configured world is the loaded world)
            try { return ServerConfig.Instance?.World; } catch { }
            return null;
        }

        private string HandleStatus()
        {
            var uptime = DateTime.UtcNow - _startTime;
            string worldName = GetWorldName() ?? "none";

            int playerCount = 0;
            try { playerCount = PermissionService.GetConnectedPlayers().Count; } catch { }

            return $"{{\"uptime\":\"{(int)uptime.TotalSeconds}s\",\"worldName\":\"{Esc(worldName)}\",\"playerCount\":{playerCount},\"port\":{_config.ManagementApiPort}," +
                $"\"pid\":{System.Diagnostics.Process.GetCurrentProcess().Id}," +
                $"\"sessionId\":\"{Esc(Environment.GetEnvironmentVariable("TERRARIA_MODDER_DEV_SESSION") ?? "")}\"," +
                $"\"coreAssembly\":\"{Esc(typeof(PluginLoader).Assembly.Location)}\"}}";
        }

        private string HandleMods()
        {
            var sb = new StringBuilder("[");
            bool first = true;
            try
            {
                foreach (var mod in PluginLoader.Mods)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    string mp = mod.Manifest?.Multiplayer.ToString()?.ToLowerInvariant() ?? "optional";
                    sb.Append($"{{\"id\":\"{Esc(mod.Manifest?.Id ?? "")}\",\"name\":\"{Esc(mod.Manifest?.Name ?? "")}\",\"version\":\"{Esc(mod.Manifest?.Version ?? "")}\",\"state\":\"{mod.State}\",\"multiplayer\":\"{mp}\"}}");
                }
            }
            catch (Exception ex)
            {
                return $"{{\"error\":\"{Esc(ex.Message)}\"}}";
            }
            sb.Append(']');
            return sb.ToString();
        }

        private string HandleModEnable(string modId, bool enable)
        {
            try
            {
                modId = (modId ?? "").Trim();
                if (string.IsNullOrEmpty(modId)) return "{\"error\":\"modId is required\"}";

                string corePath = CoreConfig.Instance.CorePath;
                string filePath = Path.Combine(corePath, "enabled-mods.server.json");

                // Build current set; if no file, seed from all currently loaded mods
                var ids = new System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
                if (File.Exists(filePath))
                {
                    string json = File.ReadAllText(filePath);
                    int i = 0;
                    while (i < json.Length)
                    {
                        while (i < json.Length && json[i] != '"') i++;
                        if (i >= json.Length) break;
                        i++;
                        var sb2 = new StringBuilder();
                        while (i < json.Length && json[i] != '"')
                        {
                            if (json[i] == '\\' && i + 1 < json.Length) i++;
                            sb2.Append(json[i++]);
                        }
                        i++;
                        string id2 = sb2.ToString();
                        if (!string.IsNullOrEmpty(id2)) ids.Add(id2);
                    }
                }
                else
                {
                    foreach (var m in PluginLoader.Mods)
                        if (m.Manifest?.Id != null) ids.Add(m.Manifest.Id);
                }

                if (enable) ids.Add(modId);
                else ids.Remove(modId);

                var arr = new StringBuilder("[");
                bool first = true;
                foreach (var id in ids) { if (!first) arr.Append(','); first = false; arr.Append($"\"{Esc(id)}\""); }
                arr.Append(']');
                File.WriteAllText(filePath, arr.ToString());

                return $"{{\"ok\":true,\"modId\":\"{Esc(modId)}\",\"enabled\":{(enable ? "true" : "false")},\"note\":\"effective on next restart\"}}";
            }
            catch (Exception ex)
            {
                return $"{{\"error\":\"{Esc(ex.Message)}\"}}";
            }
        }

        private string HandlePlayers()
        {
            var sb = new StringBuilder("[");
            bool first = true;
            try
            {
                foreach (var (slot, name, guid, role) in PermissionService.GetConnectedPlayers())
                {
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append($"{{\"slot\":{slot},\"name\":\"{Esc(name)}\",\"guid\":\"{Esc(guid)}\",\"role\":\"{role}\"}}");
                }
            }
            catch (Exception ex)
            {
                return $"{{\"error\":\"{Esc(ex.Message)}\"}}";
            }
            sb.Append(']');
            return sb.ToString();
        }

        private string HandleConfigAll()
        {
            var sb = new StringBuilder("{");
            bool firstMod = true;
            try
            {
                foreach (var mod in PluginLoader.Mods)
                {
                    var config = mod.Context?.Config;
                    if (config == null) continue;

                    if (!firstMod) sb.Append(',');
                    firstMod = false;
                    sb.Append($"\"{Esc(mod.Manifest?.Id ?? mod.Instance?.Id ?? "?")}\":{{");

                    bool firstProp = true;
                    foreach (var meta in config.GetPropertyMetadata())
                    {
                        if (meta.Scope != ConfigScope.Server) continue;
                        if (!firstProp) sb.Append(',');
                        firstProp = false;
                        object val = meta.GetValue(config);
                        sb.Append($"\"{Esc(meta.Key)}\":\"{Esc(val?.ToString() ?? "")}\"");
                    }
                    sb.Append('}');
                }
            }
            catch (Exception ex)
            {
                return $"{{\"error\":\"{Esc(ex.Message)}\"}}";
            }
            sb.Append('}');
            return sb.ToString();
        }

        private string HandleConfigMod(string modId)
        {
            try
            {
                var mod = PluginLoader.GetMod(modId);
                if (mod == null) return $"{{\"error\":\"Mod not found: {Esc(modId)}\"}}";

                var config = mod.Context?.Config;
                if (config == null) return $"{{\"error\":\"No config for {Esc(modId)}\"}}";

                var sb = new StringBuilder("{");
                bool first = true;
                foreach (var meta in config.GetPropertyMetadata())
                {
                    if (meta.Scope != ConfigScope.Server) continue;
                    if (!first) sb.Append(',');
                    first = false;
                    object val = meta.GetValue(config);
                    sb.Append($"\"{Esc(meta.Key)}\":\"{Esc(val?.ToString() ?? "")}\"");
                }
                sb.Append('}');
                return sb.ToString();
            }
            catch (Exception ex)
            {
                return $"{{\"error\":\"{Esc(ex.Message)}\"}}";
            }
        }

        private string HandleConfigSet(string modId, string key, string value)
        {
            try
            {
                var mod = PluginLoader.GetMod(modId);
                if (mod == null) return $"{{\"error\":\"Mod not found: {Esc(modId)}\"}}";

                var config = mod.Context?.Config;
                if (config == null) return $"{{\"error\":\"No config for {Esc(modId)}\"}}";

                foreach (var meta in config.GetPropertyMetadata())
                {
                    if (!string.Equals(meta.Key, key, StringComparison.OrdinalIgnoreCase)) continue;
                    if (meta.Scope != ConfigScope.Server) return $"{{\"error\":\"Property {Esc(key)} is not a [Server] property\"}}";

                    meta.SetValue(config, value);
                    ConfigManager.Save(config);
                    return $"{{\"ok\":true,\"key\":\"{Esc(meta.Key)}\",\"value\":\"{Esc(meta.GetValue(config)?.ToString() ?? "")}\"}}";
                }

                return $"{{\"error\":\"Property not found: {Esc(key)}\"}}";
            }
            catch (Exception ex)
            {
                return $"{{\"error\":\"{Esc(ex.Message)}\"}}";
            }
        }

        private string HandleTime(string preset)
        {
            try
            {
                preset = (preset ?? "").Trim().ToLowerInvariant();
                double time; bool dayTime;
                switch (preset)
                {
                    case "dawn":  time = 0.0;     dayTime = true;  break;
                    case "noon":  time = 27000.0; dayTime = true;  break;
                    case "dusk":  time = 0.0;     dayTime = false; break;
                    case "night": time = 16200.0; dayTime = false; break;
                    default:
                        return $"{{\"error\":\"Unknown preset '{Esc(preset)}'. Use: dawn, noon, dusk, night\"}}";
                }

                DedServProxy.SetWorldTime(time, dayTime);
                return $"{{\"ok\":true,\"preset\":\"{preset}\"}}";
            }
            catch (Exception ex)
            {
                return $"{{\"error\":\"{Esc(ex.Message)}\"}}";
            }
        }

        private string HandleOp(string playerName, bool promote)
        {
            try
            {
                playerName = (playerName ?? "").Trim();
                int slot = PermissionService.FindSlotByName(playerName);
                if (slot < 0) return $"{{\"error\":\"Player not found: {Esc(playerName)}\"}}";

                if (promote) PermissionService.Promote(slot);
                else PermissionService.Demote(slot);

                var role = PermissionService.GetRole(slot);
                var grants = PermissionService.GetModGrants(PermissionService.GetGuid(slot));
                Net.NetSync.SendPermissionSync(slot, role, grants);
                Net.NetSync.BroadcastPlayerListUpdate();

                string verb = promote ? "promoted" : "demoted";
                _log.Info($"[ServerManagementApi] {playerName} (slot {slot}) {verb} via API");
                return $"{{\"ok\":true,\"player\":\"{Esc(playerName)}\",\"role\":\"{role}\"}}";
            }
            catch (Exception ex)
            {
                return $"{{\"error\":\"{Esc(ex.Message)}\"}}";
            }
        }

        private string HandleKick(string playerName)
        {
            try
            {
                playerName = (playerName ?? "").Trim();
                int slot = PermissionService.FindSlotByName(playerName);
                if (slot < 0) return $"{{\"error\":\"Player not found: {Esc(playerName)}\"}}";

                SendKickPacket(slot);
                _log.Info($"[ServerManagementApi] Kicked {playerName} (slot {slot}) via API");
                return $"{{\"ok\":true,\"player\":\"{Esc(playerName)}\"}}";
            }
            catch (Exception ex)
            {
                return $"{{\"error\":\"{Esc(ex.Message)}\"}}";
            }
        }

        // SendKickPacket: uses reflection to avoid compile-time Terraria.exe ref (XNA cctor crash on dedServ)
        private static void SendKickPacket(int slot)
        {
            Assembly tsv = null;
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            { var n = a.GetName().Name; if (n == "TerrariaServer" || n == "Terraria") { tsv = a; if (n == "TerrariaServer") break; } }
            if (tsv == null) return;
            Type netMsgType = tsv.GetType("Terraria.NetMessage");
            Type networkTextType = tsv.GetType("Terraria.Localization.NetworkText");
            if (netMsgType == null || networkTextType == null) return;
            var fromKey = networkTextType.GetMethod("FromKey", new[] { typeof(string) });
            if (fromKey == null) return;
            object kickText = fromKey.Invoke(null, new object[] { "CLI.KickMessage" });
            // SendData(int msgType, int remoteClient, int ignoreClient, NetworkText text, ...)
            // The overload we need: SendData(2, slot, -1, NetworkText, 0,0,0,0,0,0,0)
            var sendData = netMsgType.GetMethod("SendData", new[] { typeof(int), typeof(int), typeof(int), networkTextType, typeof(int), typeof(float), typeof(float), typeof(float), typeof(int), typeof(int), typeof(int) });
            sendData?.Invoke(null, new object[] { 2, slot, -1, kickText, 0, 0f, 0f, 0f, 0, 0, 0 });
        }

        private string HandleCommand(string command)
        {
            try
            {
                command = (command ?? "").Trim();
                if (string.IsNullOrEmpty(command)) return "{\"error\":\"Empty command\"}";

                var output = new System.Collections.Generic.List<string>();
                Action<string> capture = line => output.Add(line);
                bool executed;
                CommandRegistry.OnOutput += capture;
                try
                {
                    executed = CommandRegistry.Execute(command);
                }
                finally
                {
                    CommandRegistry.OnOutput -= capture;
                }

                if (!executed)
                    return $"{{\"ok\":false,\"error\":\"Unknown command: {Esc(command.Split(' ')[0])}\"}}";

                var sb = new StringBuilder("[");
                for (int i = 0; i < output.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append($"\"{Esc(output[i])}\"");
                }
                sb.Append(']');
                return $"{{\"ok\":true,\"output\":{sb}}}";
            }
            catch (Exception ex)
            {
                return $"{{\"error\":\"{Esc(ex.Message)}\"}}";
            }
        }

        private static string HandleBanList()
        {
            var bans = BanService.GetBans();
            var sb = new StringBuilder();
            sb.Append("[");
            for (int i = 0; i < bans.Count; i++)
            {
                var b = bans[i];
                if (i > 0) sb.Append(",");
                sb.Append($"{{\"guid\":\"{Esc(b.Guid)}\",\"name\":\"{Esc(b.Name)}\",\"ip\":\"{Esc(b.Ip)}\",\"reason\":\"{Esc(b.Reason)}\",\"bannedAt\":\"{Esc(b.BannedAt)}\"}}");
            }
            sb.Append("]");
            return sb.ToString();
        }

        private string HandleBan(string body)
        {
            // body: player name (must be connected)
            string playerName = body.Trim();
            if (string.IsNullOrEmpty(playerName))
                return "{\"error\":\"Player name required\"}";

            int slot = PermissionService.FindSlotByName(playerName);
            if (slot < 0)
                return $"{{\"error\":\"Player '{Esc(playerName)}' not found\"}}";

            string guid = PermissionService.GetGuid(slot);
            string ip   = Net.NetSync.GetClientAddress(slot);

            BanService.AddBan(guid, playerName, ip, "Banned via API");

            try { SendKickPacket(slot); }
            catch { }

            _log.Info($"[ServerManagementApi] Banned {playerName} (guid={guid})");
            return $"{{\"ok\":true,\"player\":\"{Esc(playerName)}\",\"guid\":\"{Esc(guid)}\"}}";
        }

        private string HandleUnban(string body)
        {
            string guid = body.Trim();
            if (string.IsNullOrEmpty(guid))
                return "{\"error\":\"GUID required\"}";

            bool removed = BanService.RemoveBan(guid);
            _log.Info($"[ServerManagementApi] Unban {guid}: {removed}");
            return removed
                ? $"{{\"ok\":true,\"guid\":\"{Esc(guid)}\"}}"
                : $"{{\"error\":\"No ban found for GUID: {Esc(guid)}\"}}";
        }

        private static string HandleStop()
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                Thread.Sleep(200);
                try { Environment.Exit(0); } catch { }
            });
            return "{\"ok\":true,\"message\":\"Server shutting down\"}";
        }

        /// <summary>
        /// POST /api/management/give
        /// Body: { "player": "guid-or-name", "item": "mod-id:item-name", "stack": 1, "prefix": 0 }
        /// Grants a custom item to a player (delivered on next join if offline).
        /// </summary>
        private string HandleGiveItem(string body)
        {
            try
            {
                string playerRef = ExtractJsonString(body, "player");
                string fullId    = ExtractJsonString(body, "item");
                int stack        = ExtractJsonInt(body, "stack", 1);
                int prefix       = ExtractJsonInt(body, "prefix", 0);

                if (string.IsNullOrEmpty(playerRef))
                    return "{\"error\":\"'player' field required (GUID or name)\"}";
                if (string.IsNullOrEmpty(fullId))
                    return "{\"error\":\"'item' field required (mod-id:item-name)\"}";
                if (!fullId.Contains(":"))
                    return $"{{\"error\":\"Invalid item format '{Esc(fullId)}' — expected 'mod-id:item-name'\"}}";
                if (stack < 1) stack = 1;

                // Validate item type is registered
                if (ItemRegistry.GetDefinitionById(fullId) == null)
                    return $"{{\"error\":\"Unknown item '{Esc(fullId)}' — not registered by any loaded mod\"}}";

                // Resolve player GUID + name (try GUID match first, then name)
                string guid = null;
                string name = null;

                // Try exact GUID match among connected players
                foreach (var (_, pname, pguid, _) in PermissionService.GetConnectedPlayers())
                {
                    if (string.Equals(pguid, playerRef, StringComparison.OrdinalIgnoreCase))
                    {
                        guid = pguid; name = pname; break;
                    }
                }

                // Try name match among connected players
                if (guid == null)
                {
                    foreach (var (_, pname, pguid, _) in PermissionService.GetConnectedPlayers())
                    {
                        if (string.Equals(pname, playerRef, StringComparison.OrdinalIgnoreCase))
                        {
                            guid = pguid; name = pname; break;
                        }
                    }
                }

                // If still not found, use the ref as a GUID (offline player not yet connected)
                if (guid == null)
                {
                    guid = playerRef;
                    name = null; // name unknown for offline player
                }

                bool online = !string.IsNullOrEmpty(name); // if we resolved a name, player is online
                bool stored = PlayerDataStore.AddGrant(guid, name ?? playerRef, fullId, stack, prefix);

                if (!stored)
                    return "{\"error\":\"Failed to store item grant — check server logs\"}";

                string deliveryStatus = online ? "pending_session" : "pending_join";
                _log.Info($"[ServerManagementApi] Give: {fullId} x{stack} → guid={guid} name={name ?? playerRef} ({deliveryStatus})");
                return $"{{\"ok\":true,\"guid\":\"{Esc(guid)}\",\"player\":\"{Esc(name ?? playerRef)}\",\"item\":\"{Esc(fullId)}\",\"stack\":{stack},\"prefix\":{prefix},\"delivery\":\"{deliveryStatus}\"}}";
            }
            catch (Exception ex)
            {
                return $"{{\"error\":\"{Esc(ex.Message)}\"}}";
            }
        }

        /// <summary>
        /// GET /api/management/audit-items
        /// Returns pending item grants grouped by mod, showing which players have pending items.
        /// </summary>
        private static string HandleAuditItems()
        {
            try
            {
                var byMod = PlayerDataStore.GetAllGrantsByMod();
                var sb = new StringBuilder("{");
                bool firstMod = true;
                foreach (var kvp in byMod)
                {
                    if (!firstMod) sb.Append(',');
                    firstMod = false;
                    sb.Append($"\"{Esc(kvp.Key)}\":{{\"playerCount\":{kvp.Value.Count},\"players\":[");
                    bool firstPlayer = true;
                    foreach (var (guid, pname, count) in kvp.Value)
                    {
                        if (!firstPlayer) sb.Append(',');
                        firstPlayer = false;
                        sb.Append($"{{\"guid\":\"{Esc(guid)}\",\"name\":\"{Esc(pname)}\",\"itemCount\":{count}}}");
                    }
                    sb.Append("]}");
                }
                sb.Append('}');
                return sb.ToString();
            }
            catch (Exception ex)
            {
                return $"{{\"error\":\"{Esc(ex.Message)}\"}}";
            }
        }

        // ─── JSON mini-parser helpers ──────────────────────────────────────────

        private static string ExtractJsonString(string json, string key)
        {
            if (string.IsNullOrEmpty(json)) return null;
            var m = System.Text.RegularExpressions.Regex.Match(json, $"\"{System.Text.RegularExpressions.Regex.Escape(key)}\"\\s*:\\s*\"([^\"]*)\"");
            return m.Success ? m.Groups[1].Value : null;
        }

        private static int ExtractJsonInt(string json, string key, int defaultVal = 0)
        {
            if (string.IsNullOrEmpty(json)) return defaultVal;
            var m = System.Text.RegularExpressions.Regex.Match(json, $"\"{System.Text.RegularExpressions.Regex.Escape(key)}\"\\s*:\\s*(-?\\d+)");
            return m.Success && int.TryParse(m.Groups[1].Value, out int v) ? v : defaultVal;
        }

        // ─── Helpers ──────────────────────────────────────────────────────────

        private static string ReadBody(HttpListenerRequest req)
        {
            try
            {
                if (req.ContentLength64 > 64 * 1024) return "";
                using (var reader = new StreamReader(req.InputStream, Encoding.UTF8))
                    return reader.ReadToEnd().Trim();
            }
            catch { return ""; }
        }

        private static void Send(HttpListenerResponse resp, int statusCode, string json)
        {
            resp.StatusCode = statusCode;
            resp.ContentType = "application/json";
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            resp.ContentLength64 = bytes.Length;
            try
            {
                resp.OutputStream.Write(bytes, 0, bytes.Length);
                resp.OutputStream.Close();
            }
            catch { }
        }

        /// <summary>Escape a string for JSON output.</summary>
        private static string Esc(string s)
        {
            if (s == null) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r");
        }
    }
}
