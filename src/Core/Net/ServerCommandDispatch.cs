using System;
using System.Reflection;
using TerrariaModder.Core.Logging;
using TerrariaModder.Core.Permissions;
using TerrariaModder.Core.Server;

namespace TerrariaModder.Core.Net
{
    /// <summary>
    /// Server-side handler for ServerCommandRequest packets (0x40).
    /// Validates caller permissions before executing admin operations.
    /// </summary>
    internal static class ServerCommandDispatch
    {
        private static ILogger _log;

        public static void Initialize(ILogger log) => _log = log;

        public static void Handle(int callerSlot, string type, string payload)
        {
            switch (type?.ToLowerInvariant())
            {
                case "time":
                    HandleTime(callerSlot, payload);
                    break;

                case "op":
                    HandleOp(callerSlot, payload, promote: true);
                    break;

                case "deop":
                    HandleOp(callerSlot, payload, promote: false);
                    break;

                case "kick":
                    HandleKick(callerSlot, payload);
                    break;

                case "grant":
                    HandleGrant(callerSlot, payload);
                    break;

                case "spawn":
                    HandleSpawn(callerSlot, payload);
                    break;

                case "reqop":
                    HandleReqop(callerSlot, payload);
                    break;

                case "worldcoords":
                    HandleWorldCoords(callerSlot);
                    break;

                case "spawnnpc":
                    HandleSpawnNPC(callerSlot, payload);
                    break;

                case "paintingchest":
                    HandlePaintingChest(callerSlot, payload);
                    break;

                case "paintingchest_configure":
                    HandlePaintingChestConfigure(callerSlot, payload);
                    break;

                default:
                    _log?.Warn($"[ServerCommand] Unknown type '{type}' from slot {callerSlot}");
                    NetSync.SendServerCommandResponseTo(callerSlot, type, $"Unknown command type: {type}");
                    break;
            }
        }

        private static void HandleTime(int callerSlot, string preset)
        {
            if (!PermissionService.IsAdmin(callerSlot))
            {
                NetSync.SendServerCommandResponseTo(callerSlot, "time", "Denied: requires Admin");
                return;
            }

            try
            {
                double time; bool dayTime;
                switch (preset?.ToLowerInvariant())
                {
                    case "dawn":   time = 0.0;     dayTime = true;  break;
                    case "noon":   time = 27000.0; dayTime = true;  break;
                    case "dusk":   time = 0.0;     dayTime = false; break;
                    case "night":  time = 16200.0; dayTime = false; break;
                    default:
                        NetSync.SendServerCommandResponseTo(callerSlot, "time", $"Unknown preset '{preset}'. Use: dawn, noon, dusk, night");
                        return;
                }

                DedServProxy.SetWorldTime(time, dayTime);
                _log?.Info($"[ServerCommand] Time set to {preset} by slot {callerSlot}");
                NetSync.SendServerCommandResponseTo(callerSlot, "time", $"Time set to {preset}");
            }
            catch (Exception ex)
            {
                _log?.Warn($"[ServerCommand] HandleTime error: {ex.Message}");
            }
        }

        private static void HandleOp(int callerSlot, string targetName, bool promote)
        {
            if (!PermissionService.IsAdmin(callerSlot))
            {
                NetSync.SendServerCommandResponseTo(callerSlot, promote ? "op" : "deop", "Denied: requires Admin");
                return;
            }

            int targetSlot = PermissionService.FindSlotByName(targetName);
            if (targetSlot < 0)
            {
                NetSync.SendServerCommandResponseTo(callerSlot, promote ? "op" : "deop", $"Player '{targetName}' not found");
                return;
            }

            if (promote) PermissionService.Promote(targetSlot);
            else PermissionService.Demote(targetSlot);

            // Resync permissions to target player
            var role = PermissionService.GetRole(targetSlot);
            var grants = PermissionService.GetModGrants(PermissionService.GetGuid(targetSlot));
            NetSync.SendPermissionSync(targetSlot, role, grants);

            // Update player list for all admins
            NetSync.BroadcastPlayerListUpdate();

            string verb = promote ? "promoted" : "demoted";
            _log?.Info($"[ServerCommand] {targetName} (slot {targetSlot}) {verb} by slot {callerSlot}");
            NetSync.SendServerCommandResponseTo(callerSlot, promote ? "op" : "deop", $"{targetName} {verb}");
        }

        private static void HandleKick(int callerSlot, string targetName)
        {
            if (!PermissionService.IsAdmin(callerSlot))
            {
                NetSync.SendServerCommandResponseTo(callerSlot, "kick", "Denied: requires Admin");
                return;
            }

            int targetSlot = PermissionService.FindSlotByName(targetName);
            if (targetSlot < 0)
            {
                NetSync.SendServerCommandResponseTo(callerSlot, "kick", $"Player '{targetName}' not found");
                return;
            }

            try
            {
                DedServProxy.KickPlayer(targetSlot);
                _log?.Info($"[ServerCommand] Kicked {targetName} (slot {targetSlot}) by slot {callerSlot}");
                NetSync.SendServerCommandResponseTo(callerSlot, "kick", $"Kicked {targetName}");
            }
            catch (Exception ex)
            {
                _log?.Warn($"[ServerCommand] Kick error: {ex.Message}");
            }
        }

        private static void HandleGrant(int callerSlot, string payload)
        {
            if (!PermissionService.IsAdmin(callerSlot))
            {
                NetSync.SendServerCommandResponseTo(callerSlot, "grant", "Denied: requires Admin");
                return;
            }

            // payload: "guid:modId:true/false"
            var parts = payload?.Split(':');
            if (parts == null || parts.Length < 3)
            {
                NetSync.SendServerCommandResponseTo(callerSlot, "grant", "Bad payload");
                return;
            }

            string guid = parts[0];
            string modId = parts[1];
            bool enabled = parts[2] == "true";

            PermissionService.SetModGrant(guid, modId, enabled);

            // Find connected slot for this GUID and resync
            int targetSlot = PermissionService.FindSlotByGuid(guid);
            if (targetSlot >= 0)
            {
                var role = PermissionService.GetRole(targetSlot);
                var grants = PermissionService.GetModGrants(guid);
                NetSync.SendPermissionSync(targetSlot, role, grants);
            }

            _log?.Info($"[ServerCommand] Grant {modId} → {guid}: {enabled} (by slot {callerSlot})");
            NetSync.SendServerCommandResponseTo(callerSlot, "grant", $"Grant updated");
        }

        private static void HandleSpawn(int callerSlot, string payload)
        {
            if (!PermissionService.IsAdmin(callerSlot) && !PermissionService.HasModAccess(callerSlot, "item-spawner"))
            {
                NetSync.SendServerCommandResponseTo(callerSlot, "spawn", "Denied: requires Admin or ItemSpawner grant");
                return;
            }

            // payload: "itemId:stack:prefix"
            var parts = payload?.Split(':');
            if (parts == null || parts.Length < 1 || !int.TryParse(parts[0], out int itemId))
            {
                NetSync.SendServerCommandResponseTo(callerSlot, "spawn", "Bad payload");
                return;
            }

            // Auth passed — client executes the item add locally after receiving "ok".
            // No server-side Terraria.Item/Main access needed (avoids TypeInitializationException).
            _log?.Info($"[ServerCommand] Spawn authorized: item {itemId} for slot {callerSlot}");
            NetSync.SendServerCommandResponseTo(callerSlot, "spawn", "ok");
        }

        // ── Reflection-safe Main.player access ─────────────────────────────────
        // Direct Terraria.Main static access triggers TypeInitializationException on
        // TerrariaServer.exe (Terraria.exe's Main static constructor calls XNA which
        // is unavailable headlessly). Cache the player field from TerrariaServer assembly.

        private static FieldInfo _playerArrayField;
        private static bool _playerArrayInitialized;

        private static void EnsurePlayerArrayField()
        {
            if (_playerArrayInitialized) return;
            _playerArrayInitialized = true;
            // Try direct field (works on Terraria.exe, stored so we skip assembly scan next time)
            // Try TerrariaServer assembly first (required on TerrariaServer.exe)
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var name = asm.GetName().Name;
                if (name != "Terraria" && name != "TerrariaServer") continue;
                var mainType = asm.GetType("Terraria.Main");
                if (mainType == null) continue;
                var f = mainType.GetField("player", BindingFlags.Public | BindingFlags.Static);
                if (f == null) continue;
                // Verify accessible (won't throw TypeInitializationException at field-level)
                try { f.GetValue(null); _playerArrayField = f; break; } catch { }
            }
        }

        private static object GetPlayerSafe(int slot)
        {
            try
            {
                EnsurePlayerArrayField();
                if (_playerArrayField == null) return null;
                var arr = _playerArrayField.GetValue(null) as object[];
                return arr != null && slot >= 0 && slot < arr.Length ? arr[slot] : null;
            }
            catch { return null; }
        }

        private static T GetPlayerField<T>(object player, string fieldName, T fallback = default)
        {
            try
            {
                var f = player?.GetType().GetField(fieldName, BindingFlags.Public | BindingFlags.Instance);
                if (f == null) return fallback;
                return (T)f.GetValue(player);
            }
            catch { return fallback; }
        }

        private static void HandleReqop(int callerSlot, string key)
        {
            if (PermissionService.TryReqop(callerSlot, key))
            {
                var role = PermissionService.GetRole(callerSlot);
                var grants = PermissionService.GetModGrants(PermissionService.GetGuid(callerSlot));
                NetSync.SendPermissionSync(callerSlot, role, grants);
                NetSync.BroadcastPlayerListUpdate();
                _log?.Info($"[ServerCommand] Slot {callerSlot} self-promoted via reqop");
                NetSync.SendServerCommandResponseTo(callerSlot, "reqop", "Promoted to Admin");
            }
            else
            {
                _log?.Warn($"[ServerCommand] Slot {callerSlot} tried invalid reqop key");
                NetSync.SendServerCommandResponseTo(callerSlot, "reqop", "Invalid key");
            }
        }

        private static void HandleSpawnNPC(int callerSlot, string payload)
        {
            if (!PermissionService.IsAdmin(callerSlot))
            {
                NetSync.SendServerCommandResponseTo(callerSlot, "spawnnpc", "Denied: requires Admin");
                return;
            }

            // payload: "npcType:worldX:worldY"
            var parts = payload?.Split(':');
            if (parts == null || parts.Length < 3 ||
                !int.TryParse(parts[0], out int npcType) ||
                !int.TryParse(parts[1], out int worldX) ||
                !int.TryParse(parts[2], out int worldY))
            {
                NetSync.SendServerCommandResponseTo(callerSlot, "spawnnpc", "Bad payload");
                return;
            }

            try
            {
                if (npcType <= 0 || npcType >= Terraria.ID.NPCID.Count)
                {
                    NetSync.SendServerCommandResponseTo(callerSlot, "spawnnpc", "Invalid NPC type");
                    return;
                }
                bool spawned = false;
                // Use reflection to call NPC.NewNPC on the correct assembly (ded server safe)
                bool isDedServ = Environment.GetEnvironmentVariable("TERRARIA_MODDER_DEDSERV") == "1";
                if (!isDedServ)
                {
                    var source = new Terraria.DataStructures.EntitySource_SpawnNPC();
                    int idx = Terraria.NPC.NewNPC(source, worldX, worldY, npcType, Target: callerSlot);
                    if (idx >= 0 && idx < Terraria.Main.npc.Length)
                    {
                        Terraria.Main.npc[idx].timeLeft *= 20;
                        spawned = true;
                    }
                    _log?.Info($"[ServerCommand] SpawnNPC: type={npcType} at ({worldX},{worldY}) idx={idx} for slot {callerSlot}");
                }
                else
                {
                    // Ded server: use reflection through TerrariaServer assembly.
                    // Must skip Terraria.exe's assembly (our compile-time reference) — it has
                    // Harmony-patched methods that access Terraria.Main (XNA cctor crash).
                    var clientAsm = typeof(Terraria.NPC).Assembly;
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        if (asm == clientAsm) continue; // Skip Terraria.exe
                        var asmName = asm.GetName().Name;
                        if (asmName != "TerrariaServer" && asmName != "Terraria") continue;
                        var npcClass = asm.GetType("Terraria.NPC");
                        if (npcClass == null) continue;

                        // Find EntitySource_SpawnNPC in the same assembly
                        var sourceType = asm.GetType("Terraria.DataStructures.EntitySource_SpawnNPC");
                        if (sourceType == null) continue;
                        var source = System.Activator.CreateInstance(sourceType);

                        // NPC.NewNPC(IEntitySource, int, int, int, ..., int Target)
                        var newNPC = npcClass.GetMethod("NewNPC", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                        if (newNPC == null) continue;

                        // Build args matching the full signature with defaults
                        var parms = newNPC.GetParameters();
                        var args = new object[parms.Length];
                        args[0] = source;     // IEntitySource
                        args[1] = worldX;     // X
                        args[2] = worldY;     // Y
                        args[3] = npcType;    // Type
                        for (int p = 4; p < parms.Length; p++)
                        {
                            if (parms[p].Name == "Target") args[p] = callerSlot;
                            else if (parms[p].HasDefaultValue) args[p] = parms[p].DefaultValue;
                            else args[p] = parms[p].ParameterType.IsValueType
                                ? System.Activator.CreateInstance(parms[p].ParameterType) : null;
                        }

                        var result = newNPC.Invoke(null, args);
                        var npcs = asm.GetType("Terraria.Main")?.GetField("npc", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) as Array;
                        if (result is int idx && npcs != null && idx >= 0 && idx < npcs.Length)
                        {
                            var npc = npcs.GetValue(idx);
                            var timeLeft = npcClass.GetField("timeLeft", BindingFlags.Public | BindingFlags.Instance);
                            if (timeLeft != null) timeLeft.SetValue(npc, (int)timeLeft.GetValue(npc) * 20);
                            spawned = true;
                        }
                        _log?.Info($"[ServerCommand] SpawnNPC (dedServ): type={npcType} at ({worldX},{worldY}) idx={result} for slot {callerSlot}");
                        break;
                    }
                }
                NetSync.SendServerCommandResponseTo(callerSlot, "spawnnpc", spawned ? "ok" : "No NPC spawn slot is available");
            }
            catch (Exception ex)
            {
                var inner = ex.InnerException ?? ex;
                _log?.Error($"[ServerCommand] SpawnNPC failed: {inner.GetType().Name}: {inner.Message} | Stack: {inner.StackTrace ?? "null"}");
                NetSync.SendServerCommandResponseTo(callerSlot, "spawnnpc", $"Error: {ex.Message}");
            }
        }

        private static void HandleWorldCoords(int callerSlot)
        {
            // No admin required — world coordinates are not sensitive
            DedServProxy.GetDungeonCoords(out int dungeonX, out int dungeonY);
            _log?.Info($"[ServerCommand] WorldCoords: dungeonX={dungeonX} dungeonY={dungeonY} for slot {callerSlot}");
            NetSync.SendServerCommandResponseTo(callerSlot, "worldcoords", $"{dungeonX},{dungeonY}");
        }

        private static void HandlePaintingChest(int callerSlot, string payload)
        {
            if (!PermissionService.IsAdmin(callerSlot))
            {
                NetSync.SendServerCommandResponseTo(callerSlot, "paintingchest", "failed:denied");
                return;
            }

            // payload: "topX,topY"
            var parts = payload?.Split(',');
            if (parts == null || parts.Length != 2 || !int.TryParse(parts[0], out int topX) || !int.TryParse(parts[1], out int topY))
            {
                NetSync.SendServerCommandResponseTo(callerSlot, "paintingchest", "failed:badpayload");
                return;
            }

            int slot = DedServProxy.CreatePaintingChest(topX, topY);
            _log?.Info($"[ServerCommand] PaintingChest created: slot={slot} at ({topX},{topY}) requested by slot {callerSlot}");

            if (slot < 0)
            {
                NetSync.SendServerCommandResponseTo(callerSlot, "paintingchest", "failed");
                return;
            }

            // Broadcast to all clients so they can register the chest locally
            NetSync.BroadcastServerCommandResponse("paintingchest_created", $"{slot},{topX},{topY}");
        }

        /// <summary>
        /// Configure an existing chest as a Mysterious Chest (set name + resize).
        /// Used after vanilla's AfterPlacement_Hook has already created the chest entry.
        /// For type 21 chests, vanilla handles creation — we just configure.
        /// </summary>
        private static void HandlePaintingChestConfigure(int callerSlot, string payload)
        {
            if (!PermissionService.IsAdmin(callerSlot))
            {
                NetSync.SendServerCommandResponseTo(callerSlot, "paintingchest_configure", "failed:denied");
                return;
            }

            var parts = payload?.Split(',');
            if (parts == null || parts.Length != 2 || !int.TryParse(parts[0], out int topX) || !int.TryParse(parts[1], out int topY))
            {
                NetSync.SendServerCommandResponseTo(callerSlot, "paintingchest_configure", "failed:badpayload");
                return;
            }

            // Find the chest that vanilla already created at this position
            int chestIdx = -1;
            try
            {
                chestIdx = Terraria.Chest.FindChest(topX, topY);
            }
            catch
            {
                // On ded server, use DedServProxy to find chest
                try
                {
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        if (asm.GetName().Name != "TerrariaServer") continue;
                        var chestType = asm.GetType("Terraria.Chest");
                        var findMethod = chestType?.GetMethod("FindChest",
                            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static,
                            null, new[] { typeof(int), typeof(int) }, null);
                        if (findMethod != null)
                            chestIdx = (int)findMethod.Invoke(null, new object[] { topX, topY });
                        break;
                    }
                }
                catch { }
            }

            if (chestIdx < 0)
            {
                _log?.Warn($"[ServerCommand] PaintingChestConfigure: no chest found at ({topX},{topY}) — vanilla may not have created it yet");
                NetSync.SendServerCommandResponseTo(callerSlot, "paintingchest_configure", "failed:nochest");
                return;
            }

            // Configure: set name + resize
            try
            {
                if (Environment.GetEnvironmentVariable("TERRARIA_MODDER_DEDSERV") == "1")
                {
                    // On ded server, access chest array via reflection through TerrariaServer assembly
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        if (asm.GetName().Name != "TerrariaServer") continue;
                        var mainType = asm.GetType("Terraria.Main");
                        var chestArr = mainType?.GetField("chest", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)?.GetValue(null) as Array;
                        if (chestArr != null && chestIdx >= 0 && chestIdx < chestArr.Length)
                        {
                            var chest = chestArr.GetValue(chestIdx);
                            if (chest != null)
                                chest.GetType().GetField("name", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)?.SetValue(chest, "Mysterious Chest");
                        }
                        break;
                    }
                }
                else
                {
                    var chest = Terraria.Main.chest[chestIdx];
                    if (chest != null)
                        chest.name = "Mysterious Chest";
                }
            }
            catch { }

            _log?.Info($"[ServerCommand] PaintingChestConfigure: idx={chestIdx} at ({topX},{topY}) requested by slot {callerSlot}");
            NetSync.BroadcastServerCommandResponse("paintingchest_configured", $"{topX},{topY}");
        }
    }
}
