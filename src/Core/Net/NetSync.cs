using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using TerrariaModder.Core.Assets;
using TerrariaModder.Core.Config;
using TerrariaModder.Core.Identity;
using TerrariaModder.Core.Logging;
using TerrariaModder.Core.Manifest;
using TerrariaModder.Core.Permissions;
using TerrariaModder.Core.Server;

namespace TerrariaModder.Core.Net
{
    /// <summary>
    /// Sends and receives TerrariaModder custom packets (ID 250).
    ///
    /// Wire format (matching Terraria's MessageBuffer layout):
    ///   [ushort: total_length]  2 bytes, inclusive of this field
    ///   [byte:   packet_id]     always 250 (PacketIds.TerrariaModder)
    ///   [byte:   sub_type]      PacketSubTypes.*
    ///   [payload bytes...]
    ///
    /// In GetData, `start` points at the packet_id byte.
    /// NetSyncPatches calls HandlePacket(buffer, start+1, length-1)
    /// so here payload[0] = sub_type, payload[1...] = sub-type body.
    ///
    /// Threading: GetData runs on Terraria's main XNA thread (via CheckBytes).
    /// M2 outcome: no locking required. Config mutations are safe to apply directly.
    /// </summary>
    public static class NetSync
    {
        private static ILogger _log;

        // Reflected Terraria types (lazy-loaded once)
        private static Type _netplay;
        private static FieldInfo _clientsField;
        private static FieldInfo _connectionField;
        private static Type _remoteClientType;
        private static FieldInfo _clientSocketField;
        private static FieldInfo _clientIsActiveField;
        private static FieldInfo _connectionSocketField;
        private static MethodInfo _asyncSendMethod;
        private static Type _socketSendCallbackType;
        private static MethodInfo _getRemoteAddressMethod; // ISocket.GetRemoteAddress() — explicit interface impl
        private static bool _reflected;

        /// <summary>Last change rejection reason (set on client when server rejects).</summary>
        public static string LastRejectionReason { get; private set; }

        public static void Initialize(ILogger log)
        {
            _log = log;
        }

        /// <summary>
        /// Reset client-side session state on world unload / disconnect.
        /// Called from SaveAndQuit patches or world-unload handlers.
        /// </summary>
        public static void OnWorldUnload()
        {
            _localRole = PermissionService.PlayerRole.Player;
            _localModGrants.Clear();
            _playerList.Clear();
            _deferredServerItems = null;
        }

        // ---- Public send API ----

        /// <summary>
        /// Server: send a packet to a specific client slot.
        /// No-op if client is not active or slot is invalid.
        /// </summary>
        public static void SendToClient(int clientIndex, byte subType, byte[] payload)
        {
            if (!EnsureReflection()) return;
            try
            {
                var clients = (Array)_clientsField.GetValue(null);
                if (clientIndex < 0 || clientIndex >= clients.Length) return;
                object remoteClient = clients.GetValue(clientIndex);
                bool isActive = (bool)_clientIsActiveField.GetValue(remoteClient);
                if (!isActive) return;
                object socket = _clientSocketField.GetValue(remoteClient);
                if (socket == null) return;

                byte[] packet = BuildPacket(subType, payload);
                SendViaSocket(socket, packet);
            }
            catch (Exception ex)
            {
                _log?.Warn($"[NetSync] SendToClient({clientIndex}) failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Server: broadcast to all active clients, optionally skipping one.
        /// </summary>
        public static void BroadcastToClients(byte subType, byte[] payload, int ignoreClient = -1)
        {
            if (!EnsureReflection()) return;
            try
            {
                var clients = (Array)_clientsField.GetValue(null);
                for (int i = 0; i < clients.Length; i++)
                {
                    if (i == ignoreClient) continue;
                    object remoteClient = clients.GetValue(i);
                    bool isActive = (bool)_clientIsActiveField.GetValue(remoteClient);
                    if (!isActive) continue;
                    object socket = _clientSocketField.GetValue(remoteClient);
                    if (socket == null) continue;

                    byte[] packet = BuildPacket(subType, payload);
                    SendViaSocket(socket, packet);
                }
            }
            catch (Exception ex)
            {
                _log?.Warn($"[NetSync] BroadcastToClients failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Client: send a packet to the server.
        /// </summary>
        public static void SendToServer(byte subType, byte[] payload)
        {
            if (!EnsureReflection()) { _log?.Warn("[NetSync] SendToServer: EnsureReflection failed"); return; }
            try
            {
                object connection = _connectionField.GetValue(null);
                object socket = _connectionSocketField.GetValue(connection);
                if (socket == null) { _log?.Warn($"[NetSync] SendToServer: socket is null (subType={subType})"); return; }

                byte[] packet = BuildPacket(subType, payload);
                SendViaSocket(socket, packet);
            }
            catch (Exception ex)
            {
                _log?.Warn($"[NetSync] SendToServer failed: {ex.Message}");
            }
        }

        // ---- Packet handling (called from NetSyncPatches) ----

        /// <summary>
        /// Handle an incoming TerrariaModder packet.
        /// payload[0] = sub-type, payload[1...] = sub-type body.
        /// whoAmI = Terraria's MessageBuffer.whoAmI (client slot on server, 256 on client-side buffer).
        /// </summary>
        public static void HandlePacket(byte[] readBuffer, int start, int length, int whoAmI)
        {
            if (length < 1) return;
            byte subType = readBuffer[start];
            int bodyStart = start + 1;
            int bodyLen = length - 1;

            // Dispatch guards: reject packets that arrive on the wrong side.
            // Prevents malicious clients from injecting server→client packets and vice versa.
            bool isDedServ = Environment.GetEnvironmentVariable("TERRARIA_MODDER_DEDSERV") == "1";
            bool isServer = isDedServ;
            if (!isDedServ)
            {
                try { isServer = Terraria.Main.netMode == 2 || Terraria.Netplay.IsHostAndPlay; } catch { }
            }

            // Client-only sub-types (sent by server, processed by client).
            // If we ARE the server, a client is spoofing these — discard.
            if (isDedServ)
            {
                switch (subType)
                {
                    case PacketSubTypes.ConfigChangeBroadcast:
                    case PacketSubTypes.ServerConfigSync:
                    case PacketSubTypes.PermissionSync:
                    case PacketSubTypes.PlayerListUpdate:
                    case PacketSubTypes.ModListExchange:
                    case PacketSubTypes.StorageResponse:
                    case PacketSubTypes.ServerCommandResponse:
                    case PacketSubTypes.CustomItemSync:
                    case PacketSubTypes.TypeIdManifest:
                    case PacketSubTypes.ConfigChangeRejected:
                        _log?.Warn($"[NetSync] Discarding client-only sub-type 0x{subType:X2} received on server from slot {whoAmI}");
                        return;
                }
            }

            // Server-only sub-types (sent by client, processed by server).
            // If we are NOT a server, someone is sending server-bound packets to a client — discard.
            if (!isServer)
            {
                switch (subType)
                {
                    case PacketSubTypes.ConfigChangeRequest:
                    case PacketSubTypes.IdentityAnnounce:
                    case PacketSubTypes.ModListClientAnnounce:
                    case PacketSubTypes.StorageRequest:
                    case PacketSubTypes.ServerCommandRequest:
                    case PacketSubTypes.CustomItemSave:
                        _log?.Warn($"[NetSync] Discarding server-only sub-type 0x{subType:X2} received on client");
                        return;
                }
            }

            try
            {
                switch (subType)
                {
                    case PacketSubTypes.Ping:
                        HandlePing(readBuffer, bodyStart, bodyLen, whoAmI);
                        break;

                    case PacketSubTypes.ServerConfigSync:
                        HandleServerConfigSync(readBuffer, bodyStart, bodyLen);
                        break;

                    case PacketSubTypes.ConfigChangeRequest:
                        HandleConfigChangeRequest(readBuffer, bodyStart, bodyLen, whoAmI);
                        break;

                    case PacketSubTypes.ConfigChangeBroadcast:
                        HandleConfigChangeBroadcast(readBuffer, bodyStart, bodyLen);
                        break;

                    case PacketSubTypes.ConfigChangeRejected:
                        HandleConfigChangeRejected(readBuffer, bodyStart, bodyLen);
                        break;

                    case PacketSubTypes.IdentityAnnounce:
                        HandleIdentityAnnounce(readBuffer, bodyStart, bodyLen, whoAmI);
                        break;

                    case PacketSubTypes.PermissionSync:
                        HandlePermissionSync(readBuffer, bodyStart, bodyLen);
                        break;

                    case PacketSubTypes.PlayerListUpdate:
                        HandlePlayerListUpdate(readBuffer, bodyStart, bodyLen);
                        break;

                    case PacketSubTypes.ModListExchange:
                        HandleModListExchange(readBuffer, bodyStart, bodyLen);
                        break;

                    case PacketSubTypes.ModListClientAnnounce:
                        HandleModListClientAnnounce(readBuffer, bodyStart, bodyLen, whoAmI);
                        break;

                    case PacketSubTypes.StorageRequest:
                        HandleStorageRequest(readBuffer, bodyStart, bodyLen, whoAmI);
                        break;

                    case PacketSubTypes.StorageResponse:
                        HandleStorageResponse(readBuffer, bodyStart, bodyLen);
                        break;

                    case PacketSubTypes.ServerCommandRequest:
                        HandleServerCommandRequest(readBuffer, bodyStart, bodyLen, whoAmI);
                        break;

                    case PacketSubTypes.ServerCommandResponse:
                        HandleServerCommandResponse(readBuffer, bodyStart, bodyLen);
                        break;

                    case PacketSubTypes.CustomItemSync:
                        HandleCustomItemSync(readBuffer, bodyStart, bodyLen);
                        break;

                    case PacketSubTypes.CustomItemSave:
                        HandleCustomItemSave(readBuffer, bodyStart, bodyLen, whoAmI);
                        break;

                    case PacketSubTypes.TypeIdManifest:
                        HandleTypeIdManifest(readBuffer, bodyStart, bodyLen);
                        break;

                    default:
                        _log?.Debug($"[NetSync] Unknown sub-type 0x{subType:X2}, ignoring");
                        break;
                }
            }
            catch (Exception ex)
            {
                _log?.Warn($"[NetSync] HandlePacket sub=0x{subType:X2} error: {ex.Message}");
            }
        }

        // ---- Phase 5: ModListExchange ----

        /// <summary>
        /// Server: send active mod list to a connecting client so it can validate compatibility.
        /// Mods with registered custom items are auto-upgraded to Required regardless of manifest setting.
        /// Called after SendServerConfigSync at State=1.
        /// </summary>
        public static void SendModListExchange(int clientIndex)
        {
            try
            {
                var mods = PluginLoader.Mods;
                var buf = new List<byte>();

                // Count active mods (loaded only)
                int count = 0;
                foreach (var mod in mods)
                {
                    if (mod.State == ModState.Loaded && mod.Manifest != null)
                        count++;
                }

                // Write count (up to 255)
                buf.Add((byte)Math.Min(count, 255));

                int written = 0;
                foreach (var mod in mods)
                {
                    if (written >= 255) break;
                    if (mod.State != ModState.Loaded || mod.Manifest == null) continue;

                    // Auto-upgrade to Required if mod has custom items and did NOT explicitly declare "optional"
                    // I1: mods explicitly declaring "multiplayer": "optional" stay Optional even with custom items
                    MultiplayerCategory cat = mod.Manifest.Multiplayer;
                    if (cat == MultiplayerCategory.Optional && !mod.Manifest.MultiplayerExplicit
                        && ItemRegistry.HasCustomItems(mod.Manifest.Id))
                        cat = MultiplayerCategory.Required;

                    WriteString(buf, mod.Manifest.Id);
                    WriteString(buf, mod.Manifest.Version ?? "0.0.0");
                    buf.Add((byte)cat);
                    written++;
                }

                byte[] payload = buf.ToArray();
                _log?.Debug($"[NetSync] Sending ModListExchange to client {clientIndex}: {written} mod(s)");
                SendToClient(clientIndex, PacketSubTypes.ModListExchange, payload);
            }
            catch (Exception ex)
            {
                _log?.Warn($"[NetSync] SendModListExchange({clientIndex}) failed: {ex.Message}");
            }
        }

        private static void HandleModListExchange(byte[] buf, int start, int len)
        {
            try
            {
                int pos = start;
                int limit = start + len;
                if (pos >= limit) return;

                int count = buf[pos++];

                var missingRequired = new List<string>();
                var missingOptional = new List<string>();

                for (int i = 0; i < count && pos < limit; i++)
                {
                    pos = ReadString(buf, pos, limit, out string modId);
                    pos = ReadString(buf, pos, limit, out string version);
                    if (pos >= limit) break;
                    byte catByte = buf[pos++];
                    var cat = (MultiplayerCategory)catByte;

                    if (cat == MultiplayerCategory.ClientOnly) continue;

                    var localMod = PluginLoader.GetMod(modId);
                    bool haveIt = localMod != null && localMod.State == ModState.Loaded;

                    if (!haveIt)
                    {
                        if (cat == MultiplayerCategory.Required)
                            missingRequired.Add($"{modId} (required, server has v{version})");
                        else
                            missingOptional.Add($"{modId} (optional, server has v{version})");
                    }
                    else if (cat == MultiplayerCategory.Required)
                    {
                        // G2: version must match exactly for Required mods
                        string localVersion = localMod.Manifest?.Version ?? "0.0.0";
                        if (localVersion != version)
                            missingRequired.Add($"{modId} (version mismatch: local v{localVersion}, server v{version})");
                    }
                    else if (cat == MultiplayerCategory.Optional)
                    {
                        // G2: optional version mismatch — info only, no disconnect
                        string localVersion = localMod.Manifest?.Version ?? "0.0.0";
                        if (localVersion != version)
                            _log?.Info($"[NetSync] Optional mod {modId} version differs (local v{localVersion}, server v{version})");
                    }
                }

                if (missingRequired.Count > 0)
                {
                    string list = string.Join(", ", missingRequired);
                    _log?.Warn($"[NetSync] Mod mismatch — missing/incompatible required: {list}. Disconnecting.");
                    ModListMismatch.SetBlocked($"Missing/incompatible required mod(s): {list}");
                    DisconnectClient();
                    return; // don't send client announce if we're disconnecting
                }

                if (missingOptional.Count > 0)
                {
                    string warning = $"[TerrariaModder] Optional mod(s) not installed: {string.Join(", ", missingOptional)}";
                    ModListMismatch.SetOptionalWarning(warning);
                    _log?.Info($"[NetSync] Mod mismatch — optional only: {string.Join(", ", missingOptional)}");
                }
                else
                {
                    _log?.Info("[NetSync] ModListExchange: all required mods present");
                }

                // G1: Send our own mod list to the server so it can validate no extra Required mods
                SendModListClientAnnounce();
            }
            catch (Exception ex)
            {
                _log?.Warn($"[NetSync] HandleModListExchange error: {ex.Message}");
            }
        }

        /// <summary>
        /// Client: send own mod list to server after receiving and passing ModListExchange.
        /// Enables server-side validation (G1) that client has no extra Required mods server lacks.
        /// </summary>
        private static void SendModListClientAnnounce()
        {
            try
            {
                var mods = PluginLoader.Mods;
                var buf = new List<byte>();

                int count = 0;
                foreach (var mod in mods)
                {
                    if (mod.State == ModState.Loaded && mod.Manifest != null)
                        count++;
                }

                buf.Add((byte)Math.Min(count, 255));

                int written = 0;
                foreach (var mod in mods)
                {
                    if (written >= 255) break;
                    if (mod.State != ModState.Loaded || mod.Manifest == null) continue;

                    // Auto-upgrade to Required if mod has custom items and did NOT explicitly declare "optional"
                    // I1: mods explicitly declaring "multiplayer": "optional" stay Optional even with custom items
                    MultiplayerCategory cat = mod.Manifest.Multiplayer;
                    if (cat == MultiplayerCategory.Optional && !mod.Manifest.MultiplayerExplicit
                        && Assets.ItemRegistry.HasCustomItems(mod.Manifest.Id))
                        cat = MultiplayerCategory.Required;

                    WriteString(buf, mod.Manifest.Id);
                    WriteString(buf, mod.Manifest.Version ?? "0.0.0");
                    buf.Add((byte)cat);
                    written++;
                }

                _log?.Debug($"[NetSync] Sending ModListClientAnnounce to server: {written} mod(s)");
                SendToServer(PacketSubTypes.ModListClientAnnounce, buf.ToArray());
            }
            catch (Exception ex)
            {
                _log?.Warn($"[NetSync] SendModListClientAnnounce failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Server: handle client's mod list announcement. Disconnect if client has Required mods server lacks.
        /// </summary>
        private static void HandleModListClientAnnounce(byte[] buf, int start, int len, int whoAmI)
        {
            try
            {
                int pos = start;
                int limit = start + len;
                if (pos >= limit) return;

                int count = buf[pos++];
                var extraRequired = new List<string>();

                for (int i = 0; i < count && pos < limit; i++)
                {
                    pos = ReadString(buf, pos, limit, out string modId);
                    pos = ReadString(buf, pos, limit, out string version);
                    if (pos >= limit) break;
                    byte catByte = buf[pos++];
                    var cat = (MultiplayerCategory)catByte;

                    if (cat != MultiplayerCategory.Required) continue;

                    var serverMod = PluginLoader.GetMod(modId);
                    bool serverHasIt = serverMod != null && serverMod.State == ModState.Loaded;

                    if (!serverHasIt)
                    {
                        extraRequired.Add($"{modId} v{version} (client has it, server does not)");
                    }
                    else
                    {
                        // G2: version must match exactly for Required mods
                        string serverVersion = serverMod.Manifest?.Version ?? "0.0.0";
                        if (serverVersion != version)
                            extraRequired.Add($"{modId} (version mismatch: server v{serverVersion}, client v{version})");
                    }
                }

                if (extraRequired.Count > 0)
                {
                    string list = string.Join(", ", extraRequired);
                    _log?.Warn($"[NetSync] Client slot {whoAmI} has extra/incompatible Required mods: {list}. Disconnecting.");
                    try
                    {
                        string msg = $"Client has Required mod(s) server lacks: {list}";
                        Terraria.NetMessage.SendData(2, whoAmI, -1,
                            Terraria.Localization.NetworkText.FromLiteral(msg));
                    }
                    catch { }
                }
                else
                {
                    _log?.Info($"[NetSync] ModListClientAnnounce from slot {whoAmI}: all Required mods compatible");
                    // I2: Send TypeIdManifest so client can register Optional mod type IDs as KnownUnknowns
                    SendTypeIdManifest(whoAmI);
                }
            }
            catch (Exception ex)
            {
                _log?.Warn($"[NetSync] HandleModListClientAnnounce error: {ex.Message}");
            }
        }

        /// <summary>
        /// Server: send type IDs of Optional mod items to a client after successful mod list exchange.
        /// Client uses these to register KnownUnknown placeholders for items from mods it doesn't have.
        /// </summary>
        private static void SendTypeIdManifest(int clientIndex)
        {
            try
            {
                var buf = new List<byte>();

                // Reserve 4 bytes for count (written at end)
                buf.AddRange(new byte[4]);
                int count = 0;

                foreach (var mod in PluginLoader.Mods)
                {
                    if (mod.State != ModState.Loaded || mod.Manifest == null) continue;

                    // Only send items from Optional mods (Required mods' clients already have the mod)
                    MultiplayerCategory effectiveCat = mod.Manifest.Multiplayer;
                    if (effectiveCat == MultiplayerCategory.Optional && !mod.Manifest.MultiplayerExplicit
                        && Assets.ItemRegistry.HasCustomItems(mod.Manifest.Id))
                        effectiveCat = MultiplayerCategory.Required;

                    if (effectiveCat != MultiplayerCategory.Optional) continue;
                    if (!Assets.ItemRegistry.HasCustomItems(mod.Manifest.Id)) continue;

                    foreach (var itemName in Assets.ItemRegistry.GetItemsForMod(mod.Manifest.Id))
                    {
                        string fullId = $"{mod.Manifest.Id}:{itemName}";
                        int typeId = Assets.ItemRegistry.GetRuntimeType(fullId);
                        if (typeId < 0) continue;

                        byte[] typeBytes = BitConverter.GetBytes(typeId);
                        buf.AddRange(typeBytes);
                        WriteString(buf, fullId);
                        count++;
                    }
                }

                // Patch count at position 0
                byte[] countBytes = BitConverter.GetBytes(count);
                buf[0] = countBytes[0]; buf[1] = countBytes[1];
                buf[2] = countBytes[2]; buf[3] = countBytes[3];

                _log?.Debug($"[NetSync] Sending TypeIdManifest to client {clientIndex}: {count} Optional type ID(s)");
                SendToClient(clientIndex, PacketSubTypes.TypeIdManifest, buf.ToArray());
            }
            catch (Exception ex)
            {
                _log?.Warn($"[NetSync] SendTypeIdManifest failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Client: receive TypeIdManifest from server. Register type IDs for Optional mods as KnownUnknowns.
        /// </summary>
        private static void HandleTypeIdManifest(byte[] buf, int start, int len)
        {
            try
            {
                int pos = start;
                int limit = start + len;
                if (pos + 4 > limit) return;

                int count = BitConverter.ToInt32(buf, pos); pos += 4;
                int added = 0;

                for (int i = 0; i < count && pos < limit; i++)
                {
                    if (pos + 4 > limit) break;
                    int typeId = BitConverter.ToInt32(buf, pos); pos += 4;
                    pos = ReadString(buf, pos, limit, out string fullId);
                    if (string.IsNullOrEmpty(fullId)) continue;

                    Assets.ItemRegistry.AddKnownUnknown(typeId, fullId);
                    added++;
                }

                if (added > 0)
                    _log?.Info($"[NetSync] TypeIdManifest: registered {added} Optional mod type ID(s) as KnownUnknowns");
                else
                    _log?.Debug("[NetSync] TypeIdManifest received (no Optional mod items)");

                // Re-inject any deferred CustomItemSync items now that KnownUnknowns are populated
                if (_deferredServerItems != null && _deferredServerItems.Count > 0)
                {
                    _log?.Info($"[NetSync] TypeIdManifest: injecting {_deferredServerItems.Count} deferred server item(s)");
                    var player = Terraria.Main.LocalPlayer;
                    if (player != null)
                        Assets.PlayerSavePatches.InjectFromServer(player, _deferredServerItems);
                    _deferredServerItems = null;
                }
            }
            catch (Exception ex)
            {
                _log?.Warn($"[NetSync] HandleTypeIdManifest error: {ex.Message}");
            }
        }

        // ---- Phase 4: Identity + Permissions ----

        /// <summary>
        /// Client: send our install GUID to the server so it can establish our identity and role.
        /// Called automatically after receiving ServerConfigSync.
        /// </summary>
        public static void SendIdentityAnnounce()
        {
            try
            {
                string id = IdentityService.InstallId ?? "";
                var buf = new List<byte>();
                WriteString(buf, id);
                _log?.Debug($"[NetSync] Sending IdentityAnnounce: {id}");
                SendToServer(PacketSubTypes.IdentityAnnounce, buf.ToArray());
            }
            catch (Exception ex)
            {
                _log?.Warn($"[NetSync] SendIdentityAnnounce failed: {ex.Message}");
            }
        }

        private static void HandleIdentityAnnounce(byte[] buf, int start, int len, int whoAmI)
        {
            try
            {
                int pos = ReadString(buf, start, start + len, out string guid);
                _log?.Info($"[NetSync] IdentityAnnounce from slot {whoAmI}: {guid}");

                // Get the client's remote address for localhost auto-admin and ban checks
                string remoteAddress = GetClientAddress(whoAmI);
                _log?.Info($"[NetSync] IdentityAnnounce slot {whoAmI} addr='{remoteAddress}'");

                // Check ban list before allowing the player in
                var ban = Permissions.BanService.GetMatchedBan(guid, remoteAddress);
                if (ban.HasValue)
                {
                    _log?.Info($"[NetSync] Rejecting banned player slot {whoAmI} (guid={guid}, addr={remoteAddress}): {ban.Value.Reason}");
                    try
                    {
                        string banMsg = string.IsNullOrEmpty(ban.Value.Reason)
                            ? "You are banned from this server."
                            : $"You are banned: {ban.Value.Reason}";
                        Terraria.NetMessage.SendData(2, whoAmI, -1, Terraria.Localization.NetworkText.FromLiteral(banMsg));
                    }
                    catch { }
                    return;
                }

                // Assign role via PermissionService
                var role = PermissionService.OnClientConnect(whoAmI, guid, remoteAddress);
                string verifiedGuid = PermissionService.GetGuid(whoAmI);

                // All persistence and grant lookups use the validated session identity.
                SendPermissionSync(whoAmI, role, PermissionService.GetModGrants(verifiedGuid));

                // H4: Send server-authoritative custom item data to client
                SendCustomItemSync(whoAmI, verifiedGuid);

                // Send MOTD if configured (cap at 300 chars to stay within Terraria chat limits)
                string motd = Server.ServerConfig.Instance.Motd;
                if (!string.IsNullOrWhiteSpace(motd))
                {
                    if (motd.Length > 300) motd = motd.Substring(0, 300);
                    SendChatToClient(whoAmI, motd);
                }

                // Broadcast updated player list to all admins.
                // Also schedule a delayed re-broadcast (3s) because Main.player[slot].active
                // may not be true yet at identity-announce time (player is still mid-handshake).
                BroadcastPlayerListUpdate();
                System.Threading.Tasks.Task.Delay(3000).ContinueWith(_ => BroadcastPlayerListUpdate());
            }
            catch (Exception ex)
            {
                _log?.Warn($"[NetSync] HandleIdentityAnnounce error: {ex.Message}");
            }
        }

        // Cached reflection for chat delivery
        private static MethodInfo _networkTextFromLiteral;
        private static MethodInfo _sendChatToClientMethod;
        private static bool _chatReflected;

        /// <summary>
        /// Server: deliver a chat message to a single client using ChatHelper.SendChatMessageToClient.
        /// Color is a light grey (200, 200, 200) — clearly a server message, not player chat.
        /// </summary>
        private static void SendChatToClient(int clientIndex, string message)
        {
            try
            {
                if (!_chatReflected)
                {
                    _chatReflected = true;
                    Assembly terraria = null;
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        if (asm.GetName().Name == "Terraria" || asm.GetName().Name == "TerrariaServer") { terraria = asm; break; }
                    }
                    if (terraria != null)
                    {
                        Type chatHelper = terraria.GetType("Terraria.Chat.ChatHelper");
                        Type networkText = terraria.GetType("Terraria.Localization.NetworkText");
                        _networkTextFromLiteral = networkText?.GetMethod("FromLiteral",
                            BindingFlags.Public | BindingFlags.Static);
                        // Match SendChatMessageToClient(NetworkText, Color, int)
                        if (chatHelper != null)
                        {
                            foreach (var m in chatHelper.GetMethods(BindingFlags.Public | BindingFlags.Static))
                            {
                                if (m.Name != "SendChatMessageToClient") continue;
                                var p = m.GetParameters();
                                if (p.Length == 3 && p[2].ParameterType == typeof(int))
                                {
                                    _sendChatToClientMethod = m;
                                    break;
                                }
                            }
                        }
                    }
                }

                if (_networkTextFromLiteral == null || _sendChatToClientMethod == null)
                {
                    _log?.Warn("[NetSync] SendChatToClient: ChatHelper reflection unavailable");
                    return;
                }

                object text = _networkTextFromLiteral.Invoke(null, new object[] { message });
                // Instantiate XNA Color(r, g, b) via the parameter type from the method signature
                Type colorType = _sendChatToClientMethod.GetParameters()[1].ParameterType;
                object color = Activator.CreateInstance(colorType, (byte)200, (byte)200, (byte)200);
                _sendChatToClientMethod.Invoke(null, new object[] { text, color, clientIndex });
            }
            catch (Exception ex)
            {
                _log?.Warn($"[NetSync] SendChatToClient({clientIndex}) failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Server: send role and mod grants to a client.
        /// </summary>
        public static void SendPermissionSync(int clientIndex, PermissionService.PlayerRole role, HashSet<string> modGrants)
        {
            try
            {
                var buf = new List<byte>();
                buf.Add((byte)role);                  // 0=Player, 1=Admin

                var grants = modGrants?.ToArray() ?? new string[0];
                buf.Add((byte)Math.Min(grants.Length, 255));
                for (int i = 0; i < Math.Min(grants.Length, 255); i++)
                    WriteString(buf, grants[i]);

                _log?.Debug($"[NetSync] Sending PermissionSync to {clientIndex}: role={role}, grants={grants.Length}");
                SendToClient(clientIndex, PacketSubTypes.PermissionSync, buf.ToArray());
            }
            catch (Exception ex)
            {
                _log?.Warn($"[NetSync] SendPermissionSync failed: {ex.Message}");
            }
        }

        // Client-side session role (set by HandlePermissionSync)
        private static PermissionService.PlayerRole _localRole = PermissionService.PlayerRole.Player;
        private static readonly HashSet<string> _localModGrants = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Deferred CustomItemSync items: server sent items before TypeIdManifest arrived.
        // Re-injected in HandleTypeIdManifest after KnownUnknowns are populated.
        private static List<Assets.ModdataFile.ItemEntry> _deferredServerItems = null;

        /// <summary>True if the local player has Admin role on the current server.
        /// H&amp;P host is implicitly admin (vanilla server never sends PermissionSync).</summary>
        public static bool LocalPlayerIsAdmin
            => _localRole == PermissionService.PlayerRole.Admin || Terraria.Netplay.IsHostAndPlay;

        /// <summary>True if local player has access to a specific mod (admin or explicit grant).
        /// H&amp;P host is implicitly admin and has access to all mods.</summary>
        public static bool LocalPlayerHasModAccess(string modId)
            => Terraria.Netplay.IsHostAndPlay
            || _localRole == PermissionService.PlayerRole.Admin
            || _localModGrants.Contains(modId);

        private static void HandlePermissionSync(byte[] buf, int start, int len)
        {
            try
            {
                int pos = start;
                if (pos >= start + len) return;

                _localRole = (PermissionService.PlayerRole)buf[pos++];
                PermissionService.SetClientRole(_localRole);
                _localModGrants.Clear();

                if (pos < start + len)
                {
                    int grantCount = buf[pos++];
                    for (int i = 0; i < grantCount && pos < start + len; i++)
                    {
                        pos = ReadString(buf, pos, start + len, out string modId);
                        if (!string.IsNullOrEmpty(modId))
                            _localModGrants.Add(modId);
                    }
                }

                _log?.Info($"[NetSync] PermissionSync received: role={_localRole}, grants={_localModGrants.Count}");

                // Show chat notification so the player knows their role
                try
                {
                    if (_localRole == PermissionService.PlayerRole.Admin)
                        Terraria.Main.NewText("[TerrariaModder] Connected as Admin", 80, 255, 80);
                    else
                        Terraria.Main.NewText("[TerrariaModder] Connected as Player", 255, 255, 255);
                }
                catch { /* Main.NewText may fail during early connect */ }
            }
            catch (Exception ex)
            {
                _log?.Warn($"[NetSync] HandlePermissionSync error: {ex.Message}");
            }
        }

        // Cached player list for client-side Players tab display
        private static readonly List<(int slot, string name, string guid, PermissionService.PlayerRole role)> _playerList
            = new List<(int, string, string, PermissionService.PlayerRole)>();

        public static IReadOnlyList<(int slot, string name, string guid, PermissionService.PlayerRole role)> PlayerList
            => _playerList.AsReadOnly();

        /// <summary>
        /// Server: send current connected player list to all admins.
        /// </summary>
        public static void BroadcastPlayerListUpdate()
        {
            try
            {
                var players = PermissionService.GetConnectedPlayers();
                var buf = new List<byte>();
                buf.Add((byte)Math.Min(players.Count, 255));
                foreach (var (slot, name, guid, role) in players)
                {
                    buf.Add((byte)slot);
                    WriteString(buf, name);
                    WriteString(buf, guid);
                    buf.Add((byte)role);
                }
                byte[] payload = buf.ToArray();

                // Send to all admins only
                if (!EnsureReflection()) return;
                var clients = (Array)_clientsField.GetValue(null);
                for (int i = 0; i < clients.Length; i++)
                {
                    object client = clients.GetValue(i);
                    bool active = (bool)_clientIsActiveField.GetValue(client);
                    if (!active) continue;
                    if (!PermissionService.IsAdmin(i)) continue;
                    object socket = _clientSocketField.GetValue(client);
                    if (socket == null) continue;
                    SendViaSocket(socket, BuildPacket(PacketSubTypes.PlayerListUpdate, payload));
                }
            }
            catch (Exception ex)
            {
                _log?.Warn($"[NetSync] BroadcastPlayerListUpdate error: {ex.Message}");
            }
        }

        private static void HandlePlayerListUpdate(byte[] buf, int start, int len)
        {
            try
            {
                int pos = start;
                if (pos >= start + len) return;
                int count = buf[pos++];

                _playerList.Clear();
                for (int i = 0; i < count && pos < start + len; i++)
                {
                    int slot = buf[pos++];
                    pos = ReadString(buf, pos, start + len, out string name);
                    pos = ReadString(buf, pos, start + len, out string guid);
                    if (pos >= start + len) break;
                    var role = (PermissionService.PlayerRole)buf[pos++];
                    _playerList.Add((slot, name, guid, role));
                }
                _log?.Debug($"[NetSync] PlayerListUpdate received: {_playerList.Count} players");
            }
            catch (Exception ex)
            {
                _log?.Warn($"[NetSync] HandlePlayerListUpdate error: {ex.Message}");
            }
        }

        // ---- Phase 7: Server Command Requests ----

        /// <summary>
        /// Client: send a server command request (time, spawn, op, deop, kick, etc.).
        /// </summary>
        public static void SendServerCommandRequest(string type, string payload)
        {
            try
            {
                // Ded server (netMode==2): handle directly, no TCP needed.
                // H&P: server is a separate process — must send via TCP, not shortcircuit.
                // The old shortcircuit assumed H&P was in-process, but Terraria 1.4.5 always
                // launches TerrariaServer.exe as a child process for H&P.
                if (Environment.GetEnvironmentVariable("TERRARIA_MODDER_DEDSERV") == "1")
                {
                    // Ded server calling itself (e.g. server console command) — handle locally
                    _log?.Info($"[NetSync] ServerCommandRequest shortcircuit (dedServ): {type} {payload}");
                    ServerCommandDispatch.Handle(0, type, payload);
                    return;
                }
                var buf = new List<byte>();
                WriteString(buf, type);
                WriteString(buf, payload ?? "");
                _log?.Debug($"[NetSync] Sending ServerCommandRequest: {type} {payload}");
                SendToServer(PacketSubTypes.ServerCommandRequest, buf.ToArray());
            }
            catch (Exception ex)
            {
                _log?.Warn($"[NetSync] SendServerCommandRequest failed: {ex.Message}");
            }
        }

        private static void HandleServerCommandRequest(byte[] buf, int start, int len, int whoAmI)
        {
            try
            {
                int pos = ReadString(buf, start, start + len, out string type);
                pos = ReadString(buf, pos, start + len, out string payload);

                _log?.Debug($"[NetSync] ServerCommandRequest from {whoAmI}: {type} {payload}");
                ServerCommandDispatch.Handle(whoAmI, type, payload);
            }
            catch (Exception ex)
            {
                _log?.Warn($"[NetSync] HandleServerCommandRequest error: {ex.Message}");
            }
        }

        /// <summary>Server: send a command result back to one client.</summary>
        public static void BroadcastServerCommandResponse(string type, string result)
        {
            try
            {
                var buf = new List<byte>();
                WriteString(buf, type);
                WriteString(buf, result);
                byte[] packet = BuildPacket(PacketSubTypes.ServerCommandResponse, buf.ToArray());

                // In H&P, the host player is not in the clients array — fire event locally too.
                // Skip on ded server — no local player, and Terraria.Main access crashes (XNA cctor).
                if (Environment.GetEnvironmentVariable("TERRARIA_MODDER_DEDSERV") != "1")
                {
                    if (Terraria.Netplay.IsHostAndPlay || Terraria.Main.netMode == 2)
                        OnServerCommandResponse?.Invoke(type, result);
                }

                if (!EnsureReflection()) return;
                var clients = (Array)_clientsField.GetValue(null);
                for (int i = 0; i < clients.Length; i++)
                {
                    object client = clients.GetValue(i);
                    bool active = (bool)_clientIsActiveField.GetValue(client);
                    if (!active) continue;
                    object socket = _clientSocketField.GetValue(client);
                    if (socket == null) continue;
                    SendViaSocket(socket, packet);
                }
            }
            catch (Exception ex)
            {
                _log?.Warn($"[NetSync] BroadcastServerCommandResponse failed: {ex.Message}");
            }
        }

        public static void SendServerCommandResponseTo(int clientIndex, string type, string result)
        {
            try
            {
                // H&P host player: custom packets don't work via loopback — fire event directly.
                // Skip on ded server — never H&P, and Terraria.Main access crashes (XNA cctor).
                if (Environment.GetEnvironmentVariable("TERRARIA_MODDER_DEDSERV") != "1")
                {
                    if ((Terraria.Netplay.IsHostAndPlay || Terraria.Main.netMode == 2) && clientIndex == Terraria.Main.myPlayer)
                    {
                        _log?.Info($"[NetSync] ServerCommandResponse shortcircuit [{type}]: {result}");
                        OnServerCommandResponse?.Invoke(type, result);
                        return;
                    }
                }
                var buf = new List<byte>();
                WriteString(buf, type);
                WriteString(buf, result);
                SendToClient(clientIndex, PacketSubTypes.ServerCommandResponse, buf.ToArray());
            }
            catch (Exception ex)
            {
                _log?.Warn($"[NetSync] SendServerCommandResponseTo failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Fired on the client when a ServerCommandResponse is received.
        /// Parameters: (type, result). Mods subscribe to handle their own command responses.
        /// </summary>
        public static event Action<string, string> OnServerCommandResponse;

        private static void HandleServerCommandResponse(byte[] buf, int start, int len)
        {
            try
            {
                int pos = ReadString(buf, start, start + len, out string type);
                pos = ReadString(buf, pos, start + len, out string result);
                _log?.Info($"[NetSync] ServerCommandResponse [{type}]: {result}");
                OnServerCommandResponse?.Invoke(type, result);

                // Fallback: show response as chat message for known important types
                // that have no mod subscriber (e.g. reqop is handled server-side only)
                if (type == "reqop")
                {
                    try { Terraria.Main.NewText($"[Server] {result}", 255, 200, 80); } catch { }
                }
            }
            catch (Exception ex)
            {
                _log?.Warn($"[NetSync] HandleServerCommandResponse error: {ex.Message}");
            }
        }

        // ---- Phase 7b: Storage Requests ----

        /// <summary>
        /// Server-side handler registered by StorageHub when running as host.
        /// Receives (callerSlot, operation, payload) and executes the storage op.
        /// </summary>
        public static Action<int, string, string> OnStorageRequest;

        /// <summary>
        /// Client-side handler registered by StorageHub to receive storage results.
        /// Receives (success, operation, resultPayload).
        /// </summary>
        public static Action<bool, string, string> OnStorageResponse;

        /// <summary>Client: send a storage operation request to the server.</summary>
        public static void SendStorageRequest(string operation, string payload)
        {
            try
            {
                // Ded server calling itself — handle locally.
                // H&P: server is a separate process, send via TCP.
                if (Environment.GetEnvironmentVariable("TERRARIA_MODDER_DEDSERV") == "1")
                {
                    _log?.Info($"[NetSync] StorageRequest shortcircuit (dedServ): {operation} {payload}");
                    OnStorageRequest?.Invoke(0, operation, payload);
                    return;
                }
                var buf = new List<byte>();
                WriteString(buf, operation);
                WriteString(buf, payload ?? "");
                _log?.Debug($"[NetSync] Sending StorageRequest: {operation} {payload}");
                SendToServer(PacketSubTypes.StorageRequest, buf.ToArray());
            }
            catch (Exception ex)
            {
                _log?.Warn($"[NetSync] SendStorageRequest failed: {ex.Message}");
            }
        }

        private static void HandleStorageRequest(byte[] buf, int start, int len, int whoAmI)
        {
            try
            {
                int pos = ReadString(buf, start, start + len, out string operation);
                pos = ReadString(buf, pos, start + len, out string payload);
                _log?.Debug($"[NetSync] StorageRequest from {whoAmI}: {operation} {payload}");
                OnStorageRequest?.Invoke(whoAmI, operation, payload);
            }
            catch (Exception ex)
            {
                _log?.Warn($"[NetSync] HandleStorageRequest error: {ex.Message}");
            }
        }

        /// <summary>Server: send a storage response back to a client.</summary>
        public static void SendStorageResponseTo(int clientIndex, bool success, string operation, string resultPayload)
        {
            try
            {
                // H&P host: custom packets don't work via loopback — fire event directly.
                // Skip on ded server — never H&P, and Terraria.Main access crashes (XNA cctor).
                if (Environment.GetEnvironmentVariable("TERRARIA_MODDER_DEDSERV") != "1")
                {
                    if ((Terraria.Netplay.IsHostAndPlay || Terraria.Main.netMode == 2) && clientIndex == Terraria.Main.myPlayer)
                    {
                        _log?.Info($"[NetSync] StorageResponse shortcircuit [{operation}]: {(success ? "ok" : "fail")}");
                        OnStorageResponse?.Invoke(success, operation, resultPayload);
                        return;
                    }
                }
                var buf = new List<byte>();
                buf.Add(success ? (byte)1 : (byte)0);
                WriteString(buf, operation);
                WriteString(buf, resultPayload ?? "");
                SendToClient(clientIndex, PacketSubTypes.StorageResponse, buf.ToArray());
            }
            catch (Exception ex)
            {
                _log?.Warn($"[NetSync] SendStorageResponseTo failed: {ex.Message}");
            }
        }

        private static void HandleStorageResponse(byte[] buf, int start, int len)
        {
            try
            {
                int pos = start;
                if (pos >= start + len) return;
                bool success = buf[pos++] != 0;
                pos = ReadString(buf, pos, start + len, out string operation);
                pos = ReadString(buf, pos, start + len, out string payload);
                _log?.Debug($"[NetSync] StorageResponse [{operation}]: {(success ? "ok" : "fail")} {payload}");
                OnStorageResponse?.Invoke(success, operation, payload);
            }
            catch (Exception ex)
            {
                _log?.Warn($"[NetSync] HandleStorageResponse error: {ex.Message}");
            }
        }

        // ---- Helpers ----

        /// <summary>Get the remote IP address string of a connected client slot.</summary>
        public static string GetClientAddress(int whoAmI)
        {
            try
            {
                if (!EnsureReflection()) return "";
                var clients = (Array)_clientsField.GetValue(null);
                if (whoAmI < 0 || whoAmI >= clients.Length) return "";
                object remoteClient = clients.GetValue(whoAmI);
                object socket = _clientSocketField.GetValue(remoteClient);
                if (socket == null) return "";

                // ISocket.GetRemoteAddress() is an explicit interface impl — use the method cached from ISocket type
                if (_getRemoteAddressMethod == null) return "";
                object remoteAddr = _getRemoteAddressMethod.Invoke(socket, null);
                if (remoteAddr == null) return "";

                // Use GetIdentifier() to get just the IP string (TcpAddress.GetIdentifier returns Address.ToString())
                var getIdMethod = remoteAddr.GetType().GetMethod("GetIdentifier");
                return getIdMethod?.Invoke(remoteAddr, null)?.ToString() ?? remoteAddr.ToString() ?? "";
            }
            catch { return ""; }
        }

        private static void DisconnectClient()
        {
            try
            {
                Assembly terraria = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (asm.GetName().Name == "Terraria" || asm.GetName().Name == "TerrariaServer") { terraria = asm; break; }
                }
                if (terraria == null) return;

                Type netplayType = terraria.GetType("Terraria.Netplay");
                var disconnectField = netplayType?.GetField("Disconnect",
                    BindingFlags.Public | BindingFlags.Static);
                disconnectField?.SetValue(null, true);
            }
            catch (Exception ex)
            {
                _log?.Warn($"[NetSync] DisconnectClient failed: {ex.Message}");
            }
        }

        // ---- M3: ServerConfigSync ----

        /// <summary>
        /// Server: serialize all [Server] config properties and send to a connecting client.
        /// Called when client State transitions to 1 (post-auth, pre-world-data).
        /// </summary>
        public static void SendServerConfigSync(int clientIndex)
        {
            try
            {
                var buf = new List<byte>();
                var mods = PluginLoader.Mods;

                // Count mods with [Server] properties
                int modCount = 0;
                foreach (var mod in mods)
                {
                    if (mod.Context?.Config == null) continue;
                    bool hasServer = false;
                    foreach (var meta in mod.Context.Config.GetPropertyMetadata())
                    {
                        if (meta.Scope == ConfigScope.Server) { hasServer = true; break; }
                    }
                    if (hasServer) modCount++;
                }

                buf.Add((byte)modCount);

                foreach (var mod in mods)
                {
                    if (mod.Context?.Config == null) continue;
                    var config = mod.Context.Config;

                    var serverProps = new List<ConfigPropertyMeta>();
                    foreach (var meta in config.GetPropertyMetadata())
                    {
                        if (meta.Scope == ConfigScope.Server)
                            serverProps.Add(meta);
                    }
                    if (serverProps.Count == 0) continue;

                    WriteString(buf, mod.Manifest.Id);
                    buf.Add((byte)Math.Min(serverProps.Count, 255));

                    foreach (var meta in serverProps)
                    {
                        WriteString(buf, meta.Key);
                        WriteTypedValue(buf, meta.GetValue(config), meta.PropertyType);
                    }
                }

                byte[] payload = buf.ToArray();
                _log?.Debug($"[NetSync] Sending ServerConfigSync to client {clientIndex}: {modCount} mod(s), {payload.Length} bytes");
                SendToClient(clientIndex, PacketSubTypes.ServerConfigSync, payload);
            }
            catch (Exception ex)
            {
                _log?.Warn($"[NetSync] SendServerConfigSync({clientIndex}) failed: {ex.Message}");
            }
        }

        private static void HandleServerConfigSync(byte[] buf, int start, int len)
        {
            int pos = start;
            if (pos >= start + len) return;

            int modCount = buf[pos++];
            int applied = 0;

            for (int m = 0; m < modCount; m++)
            {
                pos = ReadString(buf, pos, start + len, out string modId);
                if (pos >= start + len) break;
                int propCount = buf[pos++];

                var modInfo = PluginLoader.GetMod(modId);
                var config = modInfo?.Context?.Config;

                for (int p = 0; p < propCount; p++)
                {
                    pos = ReadString(buf, pos, start + len, out string key);
                    pos = ReadTypedValue(buf, pos, start + len, out object value, out Type valueType);

                    if (config == null)
                    {
                        _log?.Debug($"[NetSync] ServerConfigSync: mod '{modId}' not found, skipping {propCount - p} remaining props");
                        // Consume remaining property bytes to keep buffer position correct
                        for (int skip = p + 1; skip < propCount; skip++)
                        {
                            pos = ReadString(buf, pos, start + len, out _);
                            pos = ReadTypedValue(buf, pos, start + len, out _, out _);
                        }
                        break;
                    }

                    ConfigPropertyMeta meta = null;
                    foreach (var m2 in config.GetPropertyMetadata())
                    {
                        if (m2.Key == key && m2.Scope == ConfigScope.Server) { meta = m2; break; }
                    }

                    if (meta == null)
                    {
                        _log?.Debug($"[NetSync] ServerConfigSync: prop '{modId}.{key}' not found, skipping");
                        continue;
                    }

                    try
                    {
                        meta.SetValue(config, value);
                        applied++;
                    }
                    catch (Exception ex)
                    {
                        _log?.Debug($"[NetSync] ServerConfigSync: failed to set '{modId}.{key}': {ex.Message}");
                    }
                }
            }

            _log?.Info($"[NetSync] ServerConfigSync applied: {applied} [Server] prop(s) from host");

            // Phase 4: respond with our identity (client → server)
            SendIdentityAnnounce();
        }

        // ---- M4: ConfigChangeRequest / ConfigChangeBroadcast ----

        /// <summary>
        /// Client: request a [Server] field change from the server.
        /// </summary>
        public static void SendConfigChangeRequest(string modId, string key, object value, Type propertyType)
        {
            try
            {
                var buf = new List<byte>();
                WriteString(buf, modId);
                WriteString(buf, key);
                WriteTypedValue(buf, value, propertyType);
                _log?.Debug($"[NetSync] Sending ConfigChangeRequest: {modId}.{key} = {value}");
                SendToServer(PacketSubTypes.ConfigChangeRequest, buf.ToArray());
            }
            catch (Exception ex)
            {
                _log?.Warn($"[NetSync] SendConfigChangeRequest failed: {ex.Message}");
            }
        }

        private static void HandleConfigChangeRequest(byte[] buf, int start, int len, int whoAmI)
        {
            if (!PermissionService.IsAdmin(whoAmI))
            {
                _log?.Debug($"[NetSync] ConfigChangeRequest denied for slot {whoAmI}: not admin");
                SendConfigChangeRejected(whoAmI, "Denied: requires Admin");
                return;
            }

            int pos = start;
            pos = ReadString(buf, pos, start + len, out string modId);
            pos = ReadString(buf, pos, start + len, out string key);
            pos = ReadTypedValue(buf, pos, start + len, out object value, out Type valueType);

            var modInfo = PluginLoader.GetMod(modId);
            var config = modInfo?.Context?.Config;

            if (config == null)
            {
                _log?.Debug($"[NetSync] ConfigChangeRequest: mod '{modId}' not found");
                SendConfigChangeRejected(whoAmI, $"Mod '{modId}' not found");
                return;
            }

            ConfigPropertyMeta meta = null;
            foreach (var m in config.GetPropertyMetadata())
            {
                if (m.Key == key) { meta = m; break; }
            }

            if (meta == null)
            {
                _log?.Debug($"[NetSync] ConfigChangeRequest: prop '{key}' not found in {modId}");
                SendConfigChangeRejected(whoAmI, $"Property '{key}' not found");
                return;
            }

            if (meta.Scope != ConfigScope.Server)
            {
                SendConfigChangeRejected(whoAmI, $"'{key}' is a [Client] property, not [Server]");
                return;
            }

            if (meta.RestartRequired)
            {
                SendConfigChangeRejected(whoAmI, $"'{key}' requires restart and cannot be changed mid-session");
                return;
            }

            // Apply (SetValue already clamps to range)
            try
            {
                meta.SetValue(config, value);
                config.Save();
                _log?.Info($"[NetSync] ConfigChangeRequest accepted: {modId}.{key} = {meta.GetValue(config)} (from client {whoAmI})");

                // Broadcast to ALL clients (including requester so they get server-validated value)
                BroadcastConfigChange(modId, meta, config, ignoreClient: -1);
            }
            catch (Exception ex)
            {
                _log?.Warn($"[NetSync] Failed to apply change {modId}.{key}: {ex.Message}");
                SendConfigChangeRejected(whoAmI, $"Failed to apply: {ex.Message}");
            }
        }

        /// <summary>
        /// Server: broadcast a single [Server] field change to all clients.
        /// Called when the server host changes a field via the UI.
        /// </summary>
        public static void BroadcastConfigChange(string modId, ConfigPropertyMeta meta, ModConfig config, int ignoreClient = -1)
        {
            try
            {
                var buf = new List<byte>();
                WriteString(buf, modId);
                WriteString(buf, meta.Key);
                WriteTypedValue(buf, meta.GetValue(config), meta.PropertyType);
                _log?.Debug($"[NetSync] Broadcasting config change: {modId}.{meta.Key} = {meta.GetValue(config)}");
                BroadcastToClients(PacketSubTypes.ConfigChangeBroadcast, buf.ToArray(), ignoreClient);
            }
            catch (Exception ex)
            {
                _log?.Warn($"[NetSync] BroadcastConfigChange failed: {ex.Message}");
            }
        }

        private static void HandleConfigChangeBroadcast(byte[] buf, int start, int len)
        {
            int pos = start;
            pos = ReadString(buf, pos, start + len, out string modId);
            pos = ReadString(buf, pos, start + len, out string key);
            pos = ReadTypedValue(buf, pos, start + len, out object value, out Type valueType);

            var modInfo = PluginLoader.GetMod(modId);
            var config = modInfo?.Context?.Config;

            if (config == null)
            {
                _log?.Debug($"[NetSync] ConfigChangeBroadcast: mod '{modId}' not found, ignoring");
                return;
            }

            ConfigPropertyMeta meta = null;
            foreach (var m in config.GetPropertyMetadata())
            {
                if (m.Key == key && m.Scope == ConfigScope.Server) { meta = m; break; }
            }

            if (meta == null)
            {
                _log?.Debug($"[NetSync] ConfigChangeBroadcast: prop '{key}' not found in {modId}, ignoring");
                return;
            }

            try
            {
                meta.SetValue(config, value);
                _log?.Info($"[NetSync] Applied server config change: {modId}.{key} = {meta.GetValue(config)}");
            }
            catch (Exception ex)
            {
                _log?.Warn($"[NetSync] ConfigChangeBroadcast: failed to set '{modId}.{key}': {ex.Message}");
            }
        }

        private static void SendConfigChangeRejected(int clientIndex, string reason)
        {
            try
            {
                var buf = new List<byte>();
                WriteString(buf, reason);
                SendToClient(clientIndex, PacketSubTypes.ConfigChangeRejected, buf.ToArray());
            }
            catch (Exception ex)
            {
                _log?.Warn($"[NetSync] SendConfigChangeRejected failed: {ex.Message}");
            }
        }

        private static void HandleConfigChangeRejected(byte[] buf, int start, int len)
        {
            int pos = ReadString(buf, start, start + len, out string reason);
            LastRejectionReason = reason;
            _log?.Info($"[NetSync] Config change rejected by server: {reason}");

            // Show rejection in game chat (runs on main thread, safe to call directly)
            try
            {
                Assembly terraria = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (asm.GetName().Name == "Terraria" || asm.GetName().Name == "TerrariaServer") { terraria = asm; break; }
                }
                if (terraria != null)
                {
                    Type mainType = terraria.GetType("Terraria.Main");
                    var newTextMethod = mainType?.GetMethod("NewText",
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static,
                        null, new[] { typeof(string), typeof(byte), typeof(byte), typeof(byte) }, null);
                    newTextMethod?.Invoke(null, new object[] { $"[Config] Server rejected change: {reason}", (byte)255, (byte)100, (byte)100 });
                }
            }
            catch { /* Non-fatal — message already logged */ }
        }

        // ---- Ping (M1 probe) ----

        private static void HandlePing(byte[] buf, int start, int len, int whoAmI)
        {
            if (len < 1) return;
            bool isReply = buf[start] == 1;
            string msg = len > 1 ? Encoding.UTF8.GetString(buf, start + 1, len - 1) : "";

            if (!isReply)
            {
                _log?.Info($"[NetSync] Ping received (whoAmI={whoAmI}): \"{msg}\" — sending pong");
                SendPingReply(whoAmI, msg);
            }
            else
            {
                _log?.Info($"[NetSync] Pong received: \"{msg}\"");
            }
        }

        public static void SendPingToClient(int clientIndex, string message = "ping")
        {
            byte[] msgBytes = Encoding.UTF8.GetBytes(message);
            byte[] payload = new byte[1 + msgBytes.Length];
            payload[0] = 0;
            Buffer.BlockCopy(msgBytes, 0, payload, 1, msgBytes.Length);
            _log?.Info($"[NetSync] Sending ping to client {clientIndex}: \"{message}\"");
            SendToClient(clientIndex, PacketSubTypes.Ping, payload);
        }

        public static void SendPingToServer(string message = "ping")
        {
            byte[] msgBytes = Encoding.UTF8.GetBytes(message);
            byte[] payload = new byte[1 + msgBytes.Length];
            payload[0] = 0;
            Buffer.BlockCopy(msgBytes, 0, payload, 1, msgBytes.Length);
            _log?.Info($"[NetSync] Sending ping to server: \"{message}\"");
            SendToServer(PacketSubTypes.Ping, payload);
        }

        private static void SendPingReply(int whoAmI, string originalMsg)
        {
            byte[] msgBytes = Encoding.UTF8.GetBytes(originalMsg);
            byte[] payload = new byte[1 + msgBytes.Length];
            payload[0] = 1;
            Buffer.BlockCopy(msgBytes, 0, payload, 1, msgBytes.Length);

            if (whoAmI >= 0 && whoAmI < 255)
                SendToClient(whoAmI, PacketSubTypes.Ping, payload);
            else
                SendToServer(PacketSubTypes.Ping, payload);
        }

        // ---- Packet building ----

        public static byte[] BuildPacket(byte subType, byte[] payload)
        {
            int payloadLen = payload?.Length ?? 0;
            int total = 4 + payloadLen; // 2 (length) + 1 (packet id) + 1 (sub-type) + payload
            byte[] packet = new byte[total];
            packet[0] = (byte)(total & 0xFF);
            packet[1] = (byte)((total >> 8) & 0xFF);
            packet[2] = PacketIds.TerrariaModder;
            packet[3] = subType;
            if (payloadLen > 0)
                Buffer.BlockCopy(payload, 0, packet, 4, payloadLen);
            return packet;
        }

        // ---- Typed value serialization ----

        private const byte TypeBool = 0;
        private const byte TypeInt = 1;
        private const byte TypeFloat = 2;
        private const byte TypeDouble = 3;
        private const byte TypeString = 4;

        private static void WriteTypedValue(List<byte> buf, object value, Type propertyType)
        {
            if (propertyType == typeof(bool))
            {
                buf.Add(TypeBool);
                buf.Add(value is bool b && b ? (byte)1 : (byte)0);
            }
            else if (propertyType == typeof(int))
            {
                buf.Add(TypeInt);
                int iv = value is int i ? i : Convert.ToInt32(value ?? 0);
                buf.Add((byte)(iv & 0xFF));
                buf.Add((byte)((iv >> 8) & 0xFF));
                buf.Add((byte)((iv >> 16) & 0xFF));
                buf.Add((byte)((iv >> 24) & 0xFF));
            }
            else if (propertyType == typeof(float))
            {
                buf.Add(TypeFloat);
                float fv = value is float f ? f : Convert.ToSingle(value ?? 0f);
                byte[] fb = BitConverter.GetBytes(fv);
                buf.AddRange(fb);
            }
            else if (propertyType == typeof(double))
            {
                buf.Add(TypeDouble);
                double dv = value is double d ? d : Convert.ToDouble(value ?? 0.0);
                byte[] db = BitConverter.GetBytes(dv);
                buf.AddRange(db);
            }
            else // string fallback
            {
                buf.Add(TypeString);
                WriteString(buf, value?.ToString() ?? "");
            }
        }

        private static int ReadTypedValue(byte[] buf, int pos, int limit, out object value, out Type valueType)
        {
            value = null;
            valueType = typeof(string);

            if (pos >= limit) return pos;
            byte type = buf[pos++];

            switch (type)
            {
                case TypeBool:
                    valueType = typeof(bool);
                    if (pos < limit) { value = buf[pos++] != 0; }
                    break;

                case TypeInt:
                    valueType = typeof(int);
                    if (pos + 3 < limit)
                    {
                        value = buf[pos] | (buf[pos + 1] << 8) | (buf[pos + 2] << 16) | (buf[pos + 3] << 24);
                        pos += 4;
                    }
                    break;

                case TypeFloat:
                    valueType = typeof(float);
                    if (pos + 3 < limit)
                    {
                        value = BitConverter.ToSingle(buf, pos);
                        pos += 4;
                    }
                    break;

                case TypeDouble:
                    valueType = typeof(double);
                    if (pos + 7 < limit)
                    {
                        value = BitConverter.ToDouble(buf, pos);
                        pos += 8;
                    }
                    break;

                case TypeString:
                    valueType = typeof(string);
                    pos = ReadString(buf, pos, limit, out string sv);
                    value = sv;
                    break;
            }

            return pos;
        }

        // ---- String helpers ----

        private static void WriteString(List<byte> buf, string s)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(s ?? "");
            int len = Math.Min(bytes.Length, 255);
            buf.Add((byte)len);
            for (int i = 0; i < len; i++) buf.Add(bytes[i]);
        }

        public static int WriteString(byte[] buf, int offset, string s)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(s ?? "");
            if (bytes.Length > 255) throw new ArgumentException($"String too long: {bytes.Length} bytes");
            buf[offset++] = (byte)bytes.Length;
            Buffer.BlockCopy(bytes, 0, buf, offset, bytes.Length);
            return offset + bytes.Length;
        }

        public static int ReadString(byte[] buf, int offset, int limit, out string result)
        {
            if (offset >= limit) { result = ""; return offset; }
            int len = buf[offset++];
            if (offset + len > limit) { result = ""; return offset; }
            result = Encoding.UTF8.GetString(buf, offset, len);
            return offset + len;
        }

        // ---- Reflection ----

        /// <summary>
        /// Find the correct Terraria assembly.
        ///
        /// In dedicated server mode, AppDomain contains BOTH Terraria.exe and TerrariaServer.exe.
        /// We identify the server assembly by Location path or Main.dedServ.
        /// In client/H&amp;P mode there is only one "Terraria" assembly.
        /// </summary>
        private static Assembly FindTerrariaAssembly()
        {
            // In dedicated server mode, AppDomain contains BOTH Terraria.exe (client, loaded from disk)
            // and TerrariaServer.exe (headless, loaded from memory by injector via Assembly.Load(byte[])).
            // Both have assembly name "Terraria". Detection priority:
            //   1. Location / ManifestModule name ends with "TerrariaServer.exe"
            //   2. Location is empty → loaded from memory → TerrariaServer.exe
            //   3. No XNA/FNA references (headless server)
            //   4. Main.dedServ == true (only after server init)
            Assembly fallback = null;
            Assembly noLocationMatch = null;
            Assembly noXnaMatch = null;
            Assembly dedServMatch = null;

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var asmName = asm.GetName().Name;
                if (asmName != "Terraria" && asmName != "TerrariaServer") continue;
                if (asmName == "TerrariaServer") return asm;

                string loc = "";
                try { loc = asm.Location ?? ""; } catch { }
                string mod = "";
                try { mod = asm.ManifestModule?.Name ?? ""; } catch { }
                string scp = "";
                try { scp = asm.ManifestModule?.ScopeName ?? ""; } catch { }

                // Check 1: filename
                if ((!string.IsNullOrEmpty(loc) && loc.EndsWith("TerrariaServer.exe", StringComparison.OrdinalIgnoreCase)) ||
                    mod.Equals("TerrariaServer.exe", StringComparison.OrdinalIgnoreCase) ||
                    scp.Equals("TerrariaServer.exe", StringComparison.OrdinalIgnoreCase))
                    return asm;

                // Check 2: empty Location → memory-loaded → TerrariaServer.exe
                if (string.IsNullOrEmpty(loc) && noLocationMatch == null)
                {
                    noLocationMatch = asm;
                    continue;
                }

                // Check 3: no XNA/FNA references (headless server)
                if (noXnaMatch == null)
                {
                    try
                    {
                        bool hasXna = false;
                        foreach (var refName in asm.GetReferencedAssemblies())
                        {
                            if (refName.Name.StartsWith("Microsoft.Xna.Framework", StringComparison.OrdinalIgnoreCase) ||
                                refName.Name.StartsWith("FNA", StringComparison.OrdinalIgnoreCase))
                            {
                                hasXna = true;
                                break;
                            }
                        }
                        if (!hasXna) { noXnaMatch = asm; continue; }
                    }
                    catch { }
                }

                // Check 4: Main.dedServ == true (works at runtime)
                if (dedServMatch == null)
                {
                    try
                    {
                        var mainType = asm.GetType("Terraria.Main");
                        var dedServField = mainType?.GetField("dedServ",
                            BindingFlags.Public | BindingFlags.Static);
                        if (dedServField != null && (bool)dedServField.GetValue(null))
                        {
                            dedServMatch = asm;
                            continue;
                        }
                    }
                    catch { }
                }

                fallback ??= asm;
            }
            return dedServMatch ?? noXnaMatch ?? noLocationMatch ?? fallback;
        }

        private static bool EnsureReflection()
        {
            if (_reflected) return _clientsField != null;
            _reflected = true;
            try
            {
                Assembly terraria = FindTerrariaAssembly();
                if (terraria == null)
                {
                    _log?.Warn("[NetSync] Could not find Terraria assembly");
                    return false;
                }

                _netplay = terraria.GetType("Terraria.Netplay");
                _remoteClientType = terraria.GetType("Terraria.RemoteClient");
                Type remoteServerType = terraria.GetType("Terraria.RemoteServer");
                _socketSendCallbackType = terraria.GetType("Terraria.Net.Sockets.SocketSendCallback");

                _clientsField = _netplay?.GetField("Clients", BindingFlags.Public | BindingFlags.Static);
                _connectionField = _netplay?.GetField("Connection", BindingFlags.Public | BindingFlags.Static);
                _clientSocketField = _remoteClientType?.GetField("Socket", BindingFlags.Public | BindingFlags.Instance);
                _clientIsActiveField = _remoteClientType?.GetField("IsActive", BindingFlags.Public | BindingFlags.Instance);
                _connectionSocketField = remoteServerType?.GetField("Socket", BindingFlags.Public | BindingFlags.Instance);

                Type iSocketType = terraria.GetType("Terraria.Net.Sockets.ISocket");
                _asyncSendMethod = iSocketType?.GetMethod("AsyncSend");
                // GetRemoteAddress is an explicit interface impl — must get from the interface type, not concrete type
                _getRemoteAddressMethod = iSocketType?.GetMethod("GetRemoteAddress");

                bool ok = _clientsField != null && _connectionField != null &&
                          _clientSocketField != null && _clientIsActiveField != null &&
                          _connectionSocketField != null && _asyncSendMethod != null;

                if (!ok) _log?.Warn("[NetSync] Reflection incomplete — some fields missing");
                else _log?.Debug("[NetSync] Reflection ready");

                return ok;
            }
            catch (Exception ex)
            {
                _log?.Warn($"[NetSync] Reflection failed: {ex.Message}");
                return false;
            }
        }

        private static void SendViaSocket(object socket, byte[] packet)
        {
            Delegate callback = GetNoOpCallback();
            _asyncSendMethod.Invoke(socket, new object[] { packet, 0, packet.Length, callback, null });
        }

        private static Delegate _noOpDelegate;
        private static Delegate GetNoOpCallback()
        {
            if (_noOpDelegate == null && _socketSendCallbackType != null)
            {
                _noOpDelegate = Delegate.CreateDelegate(
                    _socketSendCallbackType,
                    typeof(NetSync).GetMethod(nameof(NoOpSendCallback), BindingFlags.NonPublic | BindingFlags.Static));
            }
            return _noOpDelegate;
        }

        private static void NoOpSendCallback(object state) { }

        // ---- Phase 8: H4 — Server-authoritative custom item sync ----

        /// <summary>
        /// Server: send the player's stored custom items to a connecting client.
        /// Called after PermissionSync in HandleIdentityAnnounce.
        /// Reads items from server's player-data/moddata/{guid}.json.
        /// </summary>
        private static void SendCustomItemSync(int clientIndex, string guid)
        {
            try
            {
                var items = ServerModdataStore.ReadPlayer(guid);

                // Merge pending grants from PlayerDataStore (admin give commands)
                var grants = Server.PlayerDataStore.GetPendingGrants(guid);
                if (grants.Count > 0)
                {
                    foreach (var grant in grants)
                    {
                        items.Add(new Assets.ModdataFile.ItemEntry
                        {
                            ItemId   = grant.FullId,
                            Location = "inventory",
                            Slot     = 0,   // slot 0 will likely be occupied; InjectFromServer falls through to FindEmptySlot
                            Stack    = grant.Stack,
                            Prefix   = grant.Prefix,
                        });
                    }
                    // Write merged items back to moddata and clear pending grants
                    ServerModdataStore.WritePlayer(guid, items);
                    Server.PlayerDataStore.ClearGrants(guid);
                    _log?.Info($"[NetSync] CustomItemSync: merged {grants.Count} pending grant(s) for guid={guid}");
                }

                if (items.Count == 0)
                {
                    _log?.Debug($"[NetSync] CustomItemSync: no items for guid={guid}");
                    // Send empty sync so client knows to clear locally-loaded items
                    SendToClient(clientIndex, PacketSubTypes.CustomItemSync, new byte[] { 0, 0 });
                    return;
                }

                var buf = new List<byte>();
                int count = Math.Min(items.Count, ushort.MaxValue);
                buf.Add((byte)(count & 0xFF));
                buf.Add((byte)((count >> 8) & 0xFF));

                int written = 0;
                foreach (var entry in items)
                {
                    if (written >= count) break;
                    WriteString(buf, entry.ItemId ?? "");
                    WriteString(buf, entry.Location ?? "inventory");
                    buf.Add((byte)(entry.Slot & 0xFF));
                    buf.Add((byte)((entry.Slot >> 8) & 0xFF));
                    buf.Add((byte)(entry.Stack & 0xFF));
                    buf.Add((byte)((entry.Stack >> 8) & 0xFF));
                    buf.Add((byte)((entry.Stack >> 16) & 0xFF));
                    buf.Add((byte)((entry.Stack >> 24) & 0xFF));
                    buf.Add((byte)(entry.Prefix & 0xFF));
                    buf.Add(entry.Favorited ? (byte)1 : (byte)0);
                    written++;
                }

                _log?.Info($"[NetSync] CustomItemSync → client {clientIndex}: {written} item(s) for guid={guid}");
                SendToClient(clientIndex, PacketSubTypes.CustomItemSync, buf.ToArray());
            }
            catch (Exception ex)
            {
                _log?.Warn($"[NetSync] SendCustomItemSync({clientIndex}) failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Client: receive server-authoritative custom item data.
        /// Injects items into the local player's inventory, replacing any locally-loaded items.
        /// </summary>
        private static void HandleCustomItemSync(byte[] buf, int start, int len)
        {
            try
            {
                int pos = start;
                int limit = start + len;
                if (pos + 2 > limit) return;

                int count = buf[pos] | (buf[pos + 1] << 8);
                pos += 2;

                var items = new List<Assets.ModdataFile.ItemEntry>(count);

                for (int i = 0; i < count && pos < limit; i++)
                {
                    pos = ReadString(buf, pos, limit, out string itemId);
                    pos = ReadString(buf, pos, limit, out string location);

                    if (pos + 8 > limit) break;
                    int slot = buf[pos] | (buf[pos + 1] << 8);
                    pos += 2;
                    int stack = buf[pos] | (buf[pos + 1] << 8) | (buf[pos + 2] << 16) | (buf[pos + 3] << 24);
                    pos += 4;
                    int prefix = buf[pos++];
                    bool favorited = buf[pos++] != 0;

                    items.Add(new Assets.ModdataFile.ItemEntry
                    {
                        ItemId = itemId,
                        Location = location,
                        Slot = slot,
                        Stack = stack,
                        Prefix = prefix,
                        Favorited = favorited
                    });
                }

                _log?.Info($"[NetSync] CustomItemSync received: {items.Count} item(s) from server");

                // Check if any items are from Optional mods whose type IDs aren't yet known
                // (TypeIdManifest may not have arrived yet). Defer injection until TypeIdManifest.
                bool hasUnresolvable = false;
                foreach (var entry in items)
                {
                    if (Assets.ItemRegistry.GetRuntimeType(entry.ItemId) < 0
                        && Assets.ItemRegistry.GetKnownUnknownType(entry.ItemId) < 0)
                    {
                        hasUnresolvable = true;
                        break;
                    }
                }

                if (hasUnresolvable)
                {
                    _log?.Info($"[NetSync] CustomItemSync: deferring {items.Count} item(s) until TypeIdManifest arrives");
                    _deferredServerItems = items;
                }
                else
                {
                    // Inject into local player
                    var player = Terraria.Main.LocalPlayer;
                    if (player != null)
                        Assets.PlayerSavePatches.InjectFromServer(player, items);
                }
            }
            catch (Exception ex)
            {
                _log?.Warn($"[NetSync] HandleCustomItemSync error: {ex.Message}");
            }
        }

        /// <summary>
        /// Client: send current custom item inventory snapshot to server.
        /// Called from SavePlayer_Prefix when netMode==1.
        /// </summary>
        public static void SendCustomItemSave(List<Assets.ModdataFile.ItemEntry> items)
        {
            try
            {
                var buf = new List<byte>();
                int count = items != null ? Math.Min(items.Count, ushort.MaxValue) : 0;
                buf.Add((byte)(count & 0xFF));
                buf.Add((byte)((count >> 8) & 0xFF));

                int written = 0;
                if (items != null)
                {
                    foreach (var entry in items)
                    {
                        if (written >= count) break;
                        WriteString(buf, entry.ItemId ?? "");
                        WriteString(buf, entry.Location ?? "inventory");
                        buf.Add((byte)(entry.Slot & 0xFF));
                        buf.Add((byte)((entry.Slot >> 8) & 0xFF));
                        buf.Add((byte)(entry.Stack & 0xFF));
                        buf.Add((byte)((entry.Stack >> 8) & 0xFF));
                        buf.Add((byte)((entry.Stack >> 16) & 0xFF));
                        buf.Add((byte)((entry.Stack >> 24) & 0xFF));
                        buf.Add((byte)(entry.Prefix & 0xFF));
                        buf.Add(entry.Favorited ? (byte)1 : (byte)0);
                        written++;
                    }
                }

                SendToServer(PacketSubTypes.CustomItemSave, buf.ToArray());
                _log?.Debug($"[NetSync] CustomItemSave sent to server: {written} item(s)");
            }
            catch (Exception ex)
            {
                _log?.Warn($"[NetSync] SendCustomItemSave failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Server: receive custom item snapshot from a client (on their save).
        /// Validates each item (must be from a mod the server has installed) and persists to
        /// server-side player-data/moddata/{guid}.json. Rejects items from unknown mods.
        /// </summary>
        private static void HandleCustomItemSave(byte[] buf, int start, int len, int whoAmI)
        {
            try
            {
                string guid = PermissionService.GetGuid(whoAmI);
                if (string.IsNullOrEmpty(guid))
                {
                    _log?.Warn($"[NetSync] CustomItemSave from slot {whoAmI} but no GUID — ignoring");
                    return;
                }

                int pos = start;
                int limit = start + len;
                if (pos + 2 > limit) return;

                int count = buf[pos] | (buf[pos + 1] << 8);
                pos += 2;

                var validItems = new List<Assets.ModdataFile.ItemEntry>();
                int rejected = 0;

                for (int i = 0; i < count && pos < limit; i++)
                {
                    pos = ReadString(buf, pos, limit, out string itemId);
                    pos = ReadString(buf, pos, limit, out string location);

                    if (pos + 8 > limit) break;
                    int slot = buf[pos] | (buf[pos + 1] << 8);
                    pos += 2;
                    int stack = buf[pos] | (buf[pos + 1] << 8) | (buf[pos + 2] << 16) | (buf[pos + 3] << 24);
                    pos += 4;
                    int prefix = buf[pos++];
                    bool favorited = buf[pos++] != 0;

                    // Validate: item must be registered on the server
                    int runtimeType = Assets.ItemRegistry.GetRuntimeType(itemId);
                    if (runtimeType < 0)
                    {
                        _log?.Warn($"[NetSync] CustomItemSave: slot {whoAmI} sent unknown item \"{itemId}\" — rejected");
                        rejected++;
                        continue;
                    }

                    validItems.Add(new Assets.ModdataFile.ItemEntry
                    {
                        ItemId = itemId,
                        Location = location,
                        Slot = slot,
                        Stack = Math.Max(1, stack),
                        Prefix = prefix,
                        Favorited = favorited
                    });
                }

                // Persist validated items
                ServerModdataStore.WritePlayer(guid, validItems);

                _log?.Info($"[NetSync] CustomItemSave from slot {whoAmI} (guid={guid}): " +
                    $"{validItems.Count} saved, {rejected} rejected");
            }
            catch (Exception ex)
            {
                _log?.Warn($"[NetSync] HandleCustomItemSave error: {ex.Message}");
            }
        }
    }
}
