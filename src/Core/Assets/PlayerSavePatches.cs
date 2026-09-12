using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.IO;
using System.Text;
using System.Threading;
using TerrariaModder.Core.IO;
using HarmonyLib;
using Terraria;
using Terraria.IO;
using TerrariaModder.Core.Logging;

namespace TerrariaModder.Core.Assets
{
    /// <summary>
    /// Saves custom items through an isolated per-call player/item snapshot.
    /// Native disk serialization is sanitized without removing live gameplay items.
    /// Existing sidecar load, alias, missing-mod and pending-item recovery is retained.
    /// </summary>
    internal static class PlayerSavePatches
    {
        private static Harmony _harmony;
        private static ILogger _log;
        private static bool _applied;
        private static MethodInfo _serializeMethod;

        // Per-invocation save context, restored by the finalizer.
        [ThreadStatic] private static PlayerSaveSnapshot _currentSave;
        [ThreadStatic] private static bool _serializingSave;


        public static void Initialize(ILogger logger)
        {
            _log = logger;
            _harmony = new Harmony("com.terrariamodder.assets.v3.playersave");
        }

        public static void ApplyPatches()
        {
            if (_applied) return;

            try
            {
                PatchSavePlayer();
                _log?.Info("[PlayerSavePatches] SavePlayer patched");
                PatchLoadPlayer();
                _log?.Info("[PlayerSavePatches] LoadPlayer patched");
                _applied = true;
                _log?.Info("[PlayerSavePatches] Applied successfully");
            }
            catch (Exception ex)
            {
                _harmony.UnpatchAll(_harmony.Id);
                _log?.Error($"[PlayerSavePatches] Failed: {ex.Message}\n{ex.StackTrace}");
            }
        }

        private static void PatchSavePlayer()
        {
            var saveMethod = typeof(Player).GetMethod("SavePlayer",
                BindingFlags.Public | BindingFlags.Static, null,
                new[] { typeof(PlayerFileData), typeof(bool), typeof(bool) }, null)
                ?? typeof(Player).GetMethod("SavePlayer",
                    BindingFlags.Public | BindingFlags.Static, null,
                    new[] { typeof(PlayerFileData), typeof(bool) }, null);

            if (saveMethod == null)
            {
                _log?.Warn("[PlayerSavePatches] Player.SavePlayer not found");
                return;
            }

            var update = typeof(Main).GetMethod("Update", BindingFlags.NonPublic | BindingFlags.Instance,
                null, new[] { typeof(Microsoft.Xna.Framework.GameTime) }, null)
                ?? throw new MissingMethodException("Main.Update(GameTime)");
            const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Static;
            var fileMethod = typeof(Player).GetMethod("InternalSavePlayerFile", flags)
                ?? throw new MissingMethodException("Player.InternalSavePlayerFile");
            var serialize = typeof(Player).GetMethod("Serialize", flags)
                ?? throw new MissingMethodException("Player.Serialize");
            _serializeMethod = serialize;
            var temporary = typeof(Player).GetMethod("SaveTemporaryItemSlotContents", BindingFlags.NonPublic | BindingFlags.Instance)
                ?? throw new MissingMethodException("Player.SaveTemporaryItemSlotContents");
            var refunds = typeof(Terraria.GameContent.CraftingRequests).GetMethod("SavePossibleRefunds")
                ?? throw new MissingMethodException("CraftingRequests.SavePossibleRefunds");
            _harmony.Patch(update, prefix: new HarmonyMethod(typeof(SaveCaptureThread), nameof(SaveCaptureThread.ObserveUpdateThread)));
            _harmony.Patch(saveMethod,
                prefix: new HarmonyMethod(typeof(PlayerSavePatches), nameof(SavePlayer_Prefix)),
                finalizer: new HarmonyMethod(typeof(PlayerSavePatches), nameof(SavePlayer_Finalizer)));
            _harmony.Patch(fileMethod, prefix: new HarmonyMethod(typeof(PlayerSavePatches), nameof(SaveFile_Prefix)),
                postfix: new HarmonyMethod(typeof(PlayerSavePatches), nameof(SaveFile_Postfix)),
                finalizer: new HarmonyMethod(typeof(PlayerSavePatches), nameof(SaveFile_Finalizer)));
            _harmony.Patch(serialize, prefix: new HarmonyMethod(typeof(PlayerSavePatches), nameof(Serialize_Prefix)),
                finalizer: new HarmonyMethod(typeof(PlayerSavePatches), nameof(Serialize_Finalizer)));
            _harmony.Patch(temporary, prefix: new HarmonyMethod(typeof(PlayerSavePatches), nameof(TemporarySlots_Prefix)));
            _harmony.Patch(refunds, prefix: new HarmonyMethod(typeof(PlayerSavePatches), nameof(Refunds_Prefix)));
        }

        private static void PatchLoadPlayer()
        {
            var loadMethod = typeof(Player).GetMethod("LoadPlayer",
                BindingFlags.Public | BindingFlags.Static, null,
                new[] { typeof(string), typeof(bool) }, null);

            if (loadMethod == null)
            {
                _log?.Warn("[PlayerSavePatches] Player.LoadPlayer not found");
                return;
            }

            var getFileData = typeof(Player).GetMethod("GetFileData", BindingFlags.Public | BindingFlags.Static,
                null, new[] { typeof(string), typeof(bool) }, null)
                ?? throw new MissingMethodException("Player.GetFileData");
            _harmony.Patch(getFileData, postfix: new HarmonyMethod(typeof(PlayerSavePatches), nameof(GetFileData_Postfix)),
                finalizer: new HarmonyMethod(typeof(PlayerSavePatches), nameof(GetFileData_Finalizer)));
            if (!PluginLoader.IsDedicatedServer) TerrariaModder.Core.UI.PlayerLoadFailureUI.Apply(_harmony);
            _harmony.Patch(loadMethod,
                prefix: new HarmonyMethod(typeof(PlayerSavePatches), nameof(LoadPlayer_Prefix)),
                postfix: new HarmonyMethod(typeof(PlayerSavePatches), nameof(LoadPlayer_Postfix)),
                finalizer: new HarmonyMethod(typeof(PlayerSavePatches), nameof(LoadPlayer_Finalizer)));
        }

        // Capture custom items without removing them from live gameplay.

        private static void SavePlayer_Prefix(ref PlayerFileData playerFile, out PlayerSaveSnapshot __state)
        {
            __state = null;
            if (playerFile?.Player == null) return;
            var original = playerFile;
            var previous = _currentSave;
            var snapshot = SaveCaptureThread.Capture(() => CaptureSnapshot(original, previous));
            __state = snapshot;
            _currentSave = snapshot;
            playerFile = snapshot.File;
        }

        private static PlayerSaveSnapshot CaptureSnapshot(PlayerFileData original, PlayerSaveSnapshot previous)
        {
            var snapshot = new PlayerSaveSnapshot(original, PlayerItemSaveState.For(original.Player).Preserved, previous);
            // Serialize while all native mutable/global inputs still belong to this
            // game-thread capture. Background I/O consumes only the resulting bytes.
            var priorContext = _currentSave;
            _currentSave = snapshot;
            try
            {
                using (var stream = new MemoryStream())
                using (var writer = new BinaryWriter(stream))
                {
                    try { _serializeMethod.Invoke(null, new object[] { snapshot.File, snapshot.File.Player, writer }); }
                    catch (TargetInvocationException ex) when (ex.InnerException != null)
                    {
                        System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                        throw;
                    }
                    writer.Flush();
                    snapshot.SerializedPlayer = stream.ToArray();
                }
                return snapshot;
            }
            finally { _currentSave = priorContext; }
        }

        private static void SavePlayer_Finalizer(ref PlayerFileData playerFile, PlayerSaveSnapshot __state)
        {
            if (__state == null) return;
            playerFile = __state.OriginalFile;
            _currentSave = __state.Previous;
        }

        private sealed class NativeWrite
        {
            internal PlayerSaveSnapshot Snapshot;
            internal string StagingPath;
        }
        private static void SaveFile_Prefix(ref PlayerFileData playerFile, out NativeWrite __state)
        {
            __state = null;
            var snapshot = _currentSave;
            if (snapshot == null || !ReferenceEquals(snapshot.File, playerFile) || playerFile.ServerSideCharacter
                || string.IsNullOrEmpty(playerFile.Path)) return;
            if (!playerFile.IsCloudSave && Main.netMode == 0)
            {
                __state = new NativeWrite { Snapshot = snapshot,
                    StagingPath = playerFile.Path + ".capturing-" + Guid.NewGuid().ToString("N") };
                playerFile = snapshot.CreateStagingFile(__state.StagingPath);
                return;
            }
            // Cloud and remote-authority saves retain their existing route until
            // their distinct storage/authority recovery is implemented and tested.
            if (Main.netMode == 1) { Net.NetSync.SendCustomItemSave(snapshot.Items); return; }
            string path = ModdataFile.GetPlayerModdataPath(playerFile.Path);
            if (path == null || !ModdataFile.Write(path, snapshot.Items, snapshot.Preserved))
                throw new IOException("Unable to publish player item metadata; native save aborted");
        }
        private static void SaveFile_Postfix(PlayerFileData playerFile, NativeWrite __state)
        {
            if (__state != null)
            {
                var snapshot = __state.Snapshot;
                string path = snapshot.File.Path;
                string corePath = ModdataFile.GetPlayerModdataPath(path);
                var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
                {
                    [path] = File.ReadAllBytes(__state.StagingPath),
                    [corePath] = Encoding.UTF8.GetBytes(ModdataFile.Serialize(snapshot.Items, snapshot.Preserved))
                };
                foreach (var sidecar in snapshot.Sidecars) files.Add(sidecar.Path, Encoding.UTF8.GetBytes(sidecar.Text));
                PlayerSaveJournal.Commit(path, corePath, files);
                return;
            }
            var current = _currentSave;
            if (current != null && ReferenceEquals(current.File, playerFile) && !playerFile.ServerSideCharacter
                && !string.IsNullOrEmpty(playerFile.Path)) PlayerSaveContributors.Publish(current.Sidecars);
        }
        private static void SaveFile_Finalizer(ref PlayerFileData playerFile, NativeWrite __state)
        {
            if (__state == null) return;
            playerFile = __state.Snapshot.File;
            __state.Snapshot.StagingFile = null;
            try { if (File.Exists(__state.StagingPath)) File.Delete(__state.StagingPath); } catch { }
        }
        private static void LoadPlayer_Prefix(string playerPath, bool cloudSave, out object __state)
        {
            __state = null;
            if (cloudSave || string.IsNullOrEmpty(playerPath)) return;
            __state = PlayerSaveJournal.Gate(playerPath);
            Monitor.Enter(__state);
            try { PlayerSaveJournal.Recover(playerPath, ModdataFile.GetPlayerModdataPath(playerPath)); }
            catch (Exception ex) { throw new PlayerSaveRecoveryException(playerPath, ex); }
        }
        private static void LoadPlayer_Finalizer(object __state)
        {
            if (__state != null) Monitor.Exit(__state);
        }

        private static void GetFileData_Postfix(PlayerFileData __result)
        {
            // Apply only after vanilla's backup decision. Setting this in LoadPlayer
            // would cause GetFileData to move the native .bak independently of sidecars.
            if (__result?.Player != null && PlayerSaveContributors.GetLoadErrors(__result.Player).Count > 0)
                __result.Player.loadStatus = Terraria.ID.StatusID.UnknownError;
        }

        private static Exception GetFileData_Finalizer(string file, bool cloudSave, Exception __exception, ref PlayerFileData __result)
        {
            if (!(__exception is PlayerSaveRecoveryException)) return __exception;
            // Stop only the recovery failure. Native GetFileData must not replace
            // the player file alone from .bak while its sidecars await recovery.
            __result = new PlayerFileData(file, cloudSave)
            {
                Metadata = FileMetadata.FromCurrentSettings(FileType.Player),
                Player = new Player { name = Path.GetFileNameWithoutExtension(file), loadStatus = Terraria.ID.StatusID.UnknownError }
            };
            _log?.Warn("[PlayerSavePatches] " + __exception.Message);
            return null;
        }

        private static bool Serialize_Prefix(PlayerFileData playerFile, BinaryWriter fileIO, out bool __state)
        {
            __state = _serializingSave;
            _serializingSave = _currentSave != null && (ReferenceEquals(playerFile, _currentSave.File) || ReferenceEquals(playerFile, _currentSave.StagingFile));
            if (_serializingSave && _currentSave.SerializedPlayer != null)
            {
                fileIO.Write(_currentSave.SerializedPlayer);
                return false;
            }
            return true;
        }
        private static void Serialize_Finalizer(bool __state) { _serializingSave = __state; }
        private static bool TemporarySlots_Prefix(Player __instance, BinaryWriter writer)
        {
            if (!_serializingSave || _currentSave == null || !ReferenceEquals(__instance, _currentSave.File.Player)) return true;
            var slots = _currentSave.TemporarySlots;
            BitsByte present = (byte)0;
            for (int i = 0; i < slots.Length; i++) present[i] = !slots[i].IsAir;
            writer.Write(present);
            for (int i = 0; i < slots.Length; i++) if (present[i]) slots[i].Serialize(writer);
            return false;
        }
        private static bool Refunds_Prefix(BinaryWriter writer)
        {
            if (!_serializingSave || _currentSave == null) return true;
            writer.Write(_currentSave.Refunds.Length);
            foreach (var item in _currentSave.Refunds) item.Serialize(writer);
            return false;
        }

        // Load recovery remains compatible with the existing sidecar locations.
        private static void LoadPlayer_Postfix(string playerPath, PlayerFileData __result)
        {
            if (__result?.Player == null) return;

            try
            {
                // H4: When connected to a REMOTE dedicated server, skip loading local moddata.
                // CustomItemSync from the server will inject the authoritative items.
                // Exception: H&P host (IsHostAndPlay) uses local moddata since the server
                // was just started and doesn't have the player's sidecar data yet.
                if (Main.netMode == 1 && !Terraria.Netplay.IsHostAndPlay)
                {
                    _log?.Debug("[PlayerSavePatches] netMode==1 (remote server): skipping local moddata load (awaiting CustomItemSync)");
                    return;
                }

                // H3: One-time migration from legacy sidecar format
                string v2Path = ModdataFile.GetPlayerModdataPath(playerPath);
                string v1Path = ModdataFile.GetLegacyPlayerModdataPath(playerPath);
                if (v2Path != null && v1Path != null)
                    ModdataFile.MigrateIfNeeded(v2Path, v1Path);

                if (v2Path == null)
                {
                    _log?.Warn("[PlayerSavePatches] Could not determine moddata path");
                    return;
                }

                // Get loaded mod IDs (mods that have registered custom items)
                var loadedModIds = new HashSet<string>(
                    ItemRegistry.AllIds.Select(id =>
                    {
                        int c = id.IndexOf(':');
                        return c > 0 ? id.Substring(0, c) : null;
                    }).Where(m => m != null),
                    StringComparer.OrdinalIgnoreCase);

                // Read moddata: active items (loaded mods) + preserved items (unloaded mods)
                var items = ModdataFile.Read(v2Path, loadedModIds, out var preserved);
                var player = __result.Player;
                var state = PlayerItemSaveState.For(player);
                state.Preserved = preserved ?? new List<ModdataFile.ItemEntry>();
                PendingItemStore.ClearPlayer(player);

                if (state.Preserved.Count > 0)
                    _log?.Info($"[PlayerSavePatches] Preserving {state.Preserved.Count} item(s) from unloaded mod(s)");

                if (items.Count == 0)
                {
                    _log?.Debug("[PlayerSavePatches] No active moddata items to inject");
                    return;
                }

                int injected = 0, skipped = 0;

                foreach (var entry in items)
                {
                    try
                    {
                        // Resolve string ID to runtime type (checks canonical + alias map)
                        int runtimeType = ItemRegistry.ResolvePersistentId(entry.ItemId);
                        if (runtimeType < 0)
                        {
                            // Pending entries are classified as active even when their mod
                            // is absent. Keep their original identity/location for future
                            // resolution instead of losing them at the next save.
                            state.Preserved.Add(entry);
                            _log?.Debug($"[PlayerSavePatches] Preserving unresolvable item: {entry.ItemId} (mod not loaded?)");
                            skipped++;
                            continue;
                        }

                        // Log alias resolution for transparency
                        string resolvedId = ItemRegistry.GetFullId(runtimeType);
                        if (resolvedId != null && !string.Equals(resolvedId, entry.ItemId, StringComparison.OrdinalIgnoreCase))
                            _log?.Info($"[Moddata] Resolved alias \"{entry.ItemId}\" → \"{resolvedId}\"");

                        // Pending items from previous session — re-add to store
                        if (entry.Location == "pending")
                        {
                            PendingItemStore.AddPlayerItem(player, new PendingItemStore.PendingItem
                            {
                                ItemId = resolvedId ?? entry.ItemId,
                                RuntimeType = runtimeType,
                                Stack = entry.Stack,
                                Prefix = entry.Prefix,
                                Favorited = entry.Favorited
                            });
                            skipped++;
                            continue;
                        }

                        // Create item via SetDefaults (our prefix handles custom types)
                        var item = new Item();
                        item.SetDefaults(runtimeType);
                        item.stack = entry.Stack;
                        item.prefix = (byte)entry.Prefix;
                        item.favorited = entry.Favorited;
                        if (entry.Prefix > 0) item.Prefix(entry.Prefix);

                        // Try to inject at saved slot
                        if (IsSlotEmpty(player, entry.Location, entry.Slot))
                        {
                            SetItem(player, entry.Location, entry.Slot, item);
                            injected++;
                        }
                        else
                        {
                            // Saved slot occupied — find alternative
                            int alt = FindEmptySlot(player, entry.Location);
                            if (alt >= 0)
                            {
                                SetItem(player, entry.Location, alt, item);
                                injected++;
                            }
                            else
                            {
                                // Overflow: try inventory, then banks
                                bool placed = false;
                                foreach (var overflow in new[] { "inventory", "bank", "bank2", "bank3", "bank4" })
                                {
                                    alt = FindEmptySlot(player, overflow);
                                    if (alt >= 0)
                                    {
                                        SetItem(player, overflow, alt, item);
                                        injected++;
                                        placed = true;
                                        break;
                                    }
                                }
                                if (!placed)
                                {
                                    _log?.Info($"[PlayerSavePatches] No slot for {entry.ItemId} — added to pending items");
                                    PendingItemStore.AddPlayerItem(player, new PendingItemStore.PendingItem
                                    {
                                        ItemId = resolvedId ?? entry.ItemId,
                                        RuntimeType = runtimeType,
                                        Stack = entry.Stack,
                                        Prefix = entry.Prefix,
                                        Favorited = entry.Favorited
                                    });
                                    skipped++;
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _log?.Error($"[PlayerSavePatches] Failed to inject {entry.ItemId}: {ex.Message}");
                        skipped++;
                    }
                }

                int pending = PendingItemStore.GetPlayerItems(player).Count;
                _log?.Info($"[PlayerSavePatches] Injected {injected} items, skipped {skipped}" +
                    (pending > 0 ? $", {pending} pending (overflow)" : ""));
            }
            catch (Exception ex)
            {
                _log?.Error($"[PlayerSavePatches] Load postfix error: {ex.Message}");
            }
        }

        // ── Called by NetSync when CustomItemSync arrives from server (H4) ──

        /// <summary>
        /// Inject custom items received from server (H4 server-authoritative flow).
        /// Clears any locally-loaded custom items first, then injects server items.
        /// </summary>
        internal static void InjectFromServer(Player player, List<ModdataFile.ItemEntry> serverItems)
        {
            if (player == null || serverItems == null) return;

            try
            {
                // H&P host: local moddata was already loaded during LoadPlayer. Don't overwrite
                // with server data (server may not have the sidecar files yet).
                if (Terraria.Netplay.IsHostAndPlay)
                {
                    _log?.Info("[PlayerSavePatches] H&P host: keeping locally-loaded custom items (skipping server sync)");
                    return;
                }

                // Clear any custom items already in inventory (from local moddata load)
                ClearCustomItems(player);
                PendingItemStore.ClearPlayer(player);

                int injected = 0;
                foreach (var entry in serverItems)
                {
                    int runtimeType = ItemRegistry.GetRuntimeType(entry.ItemId);
                    if (runtimeType < 0)
                        runtimeType = ItemRegistry.GetKnownUnknownType(entry.ItemId);
                    if (runtimeType < 0)
                    {
                        _log?.Debug($"[PlayerSavePatches] Server sent unknown item: {entry.ItemId}");
                        continue;
                    }

                    var item = new Item();
                    item.SetDefaults(runtimeType);
                    item.stack = entry.Stack;
                    item.prefix = (byte)entry.Prefix;
                    item.favorited = entry.Favorited;
                    if (entry.Prefix > 0) item.Prefix(entry.Prefix);

                    if (IsSlotEmpty(player, entry.Location, entry.Slot))
                    {
                        SetItem(player, entry.Location, entry.Slot, item);
                        injected++;
                    }
                    else
                    {
                        int alt = FindEmptySlot(player, entry.Location);
                        if (alt >= 0) { SetItem(player, entry.Location, alt, item); injected++; }
                        else
                        {
                            // Last resort: inventory or pending
                            bool placed = false;
                            foreach (var overflow in new[] { "inventory", "bank", "bank2", "bank3", "bank4" })
                            {
                                alt = FindEmptySlot(player, overflow);
                                if (alt >= 0) { SetItem(player, overflow, alt, item); injected++; placed = true; break; }
                            }
                            if (!placed)
                            {
                                PendingItemStore.AddPlayerItem(player, new PendingItemStore.PendingItem
                                {
                                    ItemId = entry.ItemId,
                                    RuntimeType = runtimeType,
                                    Stack = entry.Stack,
                                    Prefix = entry.Prefix,
                                    Favorited = entry.Favorited
                                });
                            }
                        }
                    }
                }

                _log?.Info($"[PlayerSavePatches] Injected {injected} server-authoritative items (CustomItemSync)");
            }
            catch (Exception ex)
            {
                _log?.Error($"[PlayerSavePatches] InjectFromServer error: {ex.Message}");
            }
        }

        private static void ClearCustomItems(Player player)
        {
            void ClearArray(Item[] arr) {
                if (arr == null) return;
                for (int i = 0; i < arr.Length; i++)
                    if (arr[i] != null && !arr[i].IsAir && arr[i].type >= ItemRegistry.VanillaItemCount)
                        arr[i] = new Item();
            }

            ClearArray(player.inventory);
            ClearArray(player.armor);
            ClearArray(player.dye);
            ClearArray(player.miscEquips);
            ClearArray(player.miscDyes);
            ClearArray(player.bank?.item);
            ClearArray(player.bank2?.item);
            ClearArray(player.bank3?.item);
            ClearArray(player.bank4?.item);
            if (player.Loadouts != null)
                foreach (var lo in player.Loadouts) { ClearArray(lo?.Armor); ClearArray(lo?.Dye); }
            if (player.trashItem != null && !player.trashItem.IsAir && player.trashItem.type >= ItemRegistry.VanillaItemCount)
                player.trashItem = new Item();
        }

        // ── Scanning helpers ──

        private static Item GetItem(Player player, string location, int slot)
        {
            try
            {
                switch (location)
                {
                    case "inventory": return slot < player.inventory.Length ? player.inventory[slot] : null;
                    case "armor": return slot < player.armor.Length ? player.armor[slot] : null;
                    case "dye": return slot < player.dye.Length ? player.dye[slot] : null;
                    case "misc_equips": return slot < player.miscEquips.Length ? player.miscEquips[slot] : null;
                    case "misc_dyes": return slot < player.miscDyes.Length ? player.miscDyes[slot] : null;
                    case "bank": return player.bank?.item != null && slot < player.bank.item.Length ? player.bank.item[slot] : null;
                    case "bank2": return player.bank2?.item != null && slot < player.bank2.item.Length ? player.bank2.item[slot] : null;
                    case "bank3": return player.bank3?.item != null && slot < player.bank3.item.Length ? player.bank3.item[slot] : null;
                    case "bank4": return player.bank4?.item != null && slot < player.bank4.item.Length ? player.bank4.item[slot] : null;
                    case "mouse": return PlayerTemporaryItems.Get(player, 0);
                    case "creative": return PlayerTemporaryItems.Get(player, 1);
                    case "guide": return PlayerTemporaryItems.Get(player, 2);
                    case "reforge": return PlayerTemporaryItems.Get(player, 3);
                    case "trash": return player.trashItem;
                    default:
                        if (location.StartsWith("loadout_"))
                        {
                            var parts = location.Split('_');
                            if (parts.Length == 3 && int.TryParse(parts[1], out int l) && l < player.Loadouts?.Length)
                            {
                                if (parts[2] == "armor") return slot < player.Loadouts[l].Armor.Length ? player.Loadouts[l].Armor[slot] : null;
                                if (parts[2] == "dye") return slot < player.Loadouts[l].Dye.Length ? player.Loadouts[l].Dye[slot] : null;
                            }
                        }
                        return null;
                }
            }
            catch { return null; }
        }

        private static void SetItem(Player player, string location, int slot, Item item)
        {
            switch (location)
            {
                case "inventory": if (slot < player.inventory.Length) player.inventory[slot] = item; break;
                case "armor": if (slot < player.armor.Length) player.armor[slot] = item; break;
                case "dye": if (slot < player.dye.Length) player.dye[slot] = item; break;
                case "misc_equips": if (slot < player.miscEquips.Length) player.miscEquips[slot] = item; break;
                case "misc_dyes": if (slot < player.miscDyes.Length) player.miscDyes[slot] = item; break;
                case "bank": if (player.bank?.item != null && slot < player.bank.item.Length) player.bank.item[slot] = item; break;
                case "bank2": if (player.bank2?.item != null && slot < player.bank2.item.Length) player.bank2.item[slot] = item; break;
                case "bank3": if (player.bank3?.item != null && slot < player.bank3.item.Length) player.bank3.item[slot] = item; break;
                case "bank4": if (player.bank4?.item != null && slot < player.bank4.item.Length) player.bank4.item[slot] = item; break;
                case "mouse": PlayerTemporaryItems.Set(player, 0, item); break;
                case "creative": PlayerTemporaryItems.Set(player, 1, item); break;
                case "guide": PlayerTemporaryItems.Set(player, 2, item); break;
                case "reforge": PlayerTemporaryItems.Set(player, 3, item); break;
                case "trash": player.trashItem = item; break;
                default:
                    if (location.StartsWith("loadout_"))
                    {
                        var parts = location.Split('_');
                        if (parts.Length == 3 && int.TryParse(parts[1], out int l) && l < player.Loadouts?.Length)
                        {
                            if (parts[2] == "armor" && slot < player.Loadouts[l].Armor.Length) player.Loadouts[l].Armor[slot] = item;
                            if (parts[2] == "dye" && slot < player.Loadouts[l].Dye.Length) player.Loadouts[l].Dye[slot] = item;
                        }
                    }
                    break;
            }
        }

        private static bool IsSlotEmpty(Player player, string location, int slot)
        {
            var item = GetItem(player, location, slot);
            return item == null || item.type == 0 || item.IsAir;
        }

        private static int FindEmptySlot(Player player, string location)
        {
            int max = GetSlotCount(player, location);
            for (int i = 0; i < max; i++)
            {
                if (IsSlotEmpty(player, location, i)) return i;
            }
            return -1;
        }

        private static int GetSlotCount(Player player, string location)
        {
            switch (location)
            {
                case "inventory": return Math.Min(player.inventory.Length, 50); // main inventory only
                case "armor": return player.armor.Length;
                case "dye": return player.dye.Length;
                case "bank": return player.bank?.item?.Length ?? 0;
                case "bank2": return player.bank2?.item?.Length ?? 0;
                case "bank3": return player.bank3?.item?.Length ?? 0;
                case "bank4": return player.bank4?.item?.Length ?? 0;
                default: return 0;
            }
        }

    }
}
