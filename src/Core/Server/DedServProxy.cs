using System;
using System.Reflection;
using TerrariaModder.Core.Logging;

namespace TerrariaModder.Core.Server
{
    /// <summary>
    /// Reflection-based proxy for Terraria.Main and Terraria.Item access on TerrariaServer.exe.
    ///
    /// On TerrariaServer.exe, the Terraria.exe assembly is also loaded but its Main static
    /// constructor triggers XNA initialization which fails headlessly — every direct static
    /// access to Terraria.Main throws TypeInitializationException.
    ///
    /// This proxy resolves Terraria.Main from the TerrariaServer assembly (name="TerrariaServer")
    /// where the cctor succeeds. All server-side code that runs on TerrariaServer.exe must use
    /// this proxy instead of direct Terraria.Main / Terraria.Item references.
    ///
    /// On Terraria.exe (client/singleplayer/H&amp;P), direct access works fine; this proxy
    /// delegates to direct access in that case.
    /// </summary>
    public static class DedServProxy
    {
        private static ILogger _log;
        private static bool _initialized;
        private static bool _isDedServ;

        // Main fields
        private static FieldInfo _chestField;     // Main.chest  (Chest[])
        private static FieldInfo _playerField;    // Main.player (Player[])
        private static FieldInfo _itemField;      // Main.item   (Item[])

        // Item.NewItem method (the ergonomic overload used for spawning)
        // NewItem(IEntitySource, Vector2, int, int, int, NewItemOwnership, Vector2?, NewItemModifier, bool)
        private static MethodInfo _itemNewItem;

        // NetMessage.TrySendData for packet 32 (SyncChestItem) and packet 21 (ItemDrop)
        // TrySendData(int msgType, int remoteClient, int ignoreClient, NetworkText text, int number, ...)
        private static MethodInfo _trySendData;

        public static void Initialize(ILogger log)
        {
            if (_initialized) return;
            _initialized = true;
            _log = log;
            _isDedServ = Environment.GetEnvironmentVariable("TERRARIA_MODDER_DEDSERV") == "1";

            if (!_isDedServ) return;

            // Resolve from TerrariaServer assembly — avoids Terraria.exe XNA cctor
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (asm.GetName().Name != "TerrariaServer") continue;

                var mainType = asm.GetType("Terraria.Main");
                if (mainType != null)
                {
                    _chestField  = mainType.GetField("chest",  BindingFlags.Public | BindingFlags.Static);
                    _playerField = mainType.GetField("player", BindingFlags.Public | BindingFlags.Static);
                    _itemField   = mainType.GetField("item",   BindingFlags.Public | BindingFlags.Static);
                    log?.Info($"[DedServProxy] Main resolved from TerrariaServer: chest={_chestField != null} player={_playerField != null} item={_itemField != null}");
                }

                var itemType = asm.GetType("Terraria.Item");
                if (itemType != null)
                {
                    // NewItem(IEntitySource source, Vector2 center, int type, int stack = 1, int prefix = 0, NewItemOwnership ownership = NewItemOwnership.None, Vector2? velocity = null, NewItemModifier modifier = null, bool noBroadcast = false)
                    foreach (var m in itemType.GetMethods(BindingFlags.Public | BindingFlags.Static))
                    {
                        if (m.Name != "NewItem") continue;
                        var prms = m.GetParameters();
                        if (prms.Length >= 5 && prms[1].ParameterType.Name == "Vector2")
                        {
                            _itemNewItem = m;
                            break;
                        }
                    }
                    log?.Info($"[DedServProxy] Item.NewItem resolved: {_itemNewItem != null}");
                }

                var netMsgType = asm.GetType("Terraria.NetMessage");
                if (netMsgType != null)
                {
                    foreach (var m in netMsgType.GetMethods(BindingFlags.Public | BindingFlags.Static))
                    {
                        if (m.Name != "TrySendData") continue;
                        var prms = m.GetParameters();
                        if (prms.Length >= 2 && prms[0].ParameterType == typeof(int))
                        {
                            _trySendData = m;
                            break;
                        }
                    }
                    log?.Info($"[DedServProxy] NetMessage.TrySendData resolved: {_trySendData != null}");
                }
                break;
            }
        }

        // ── Chest access ────────────────────────────────────────────────────────

        /// <summary>Get Main.chest array. Returns null on failure.</summary>
        public static object[] GetChestArray()
        {
            if (!_isDedServ) try { return Terraria.Main.chest as object[]; } catch { }
            try { return _chestField?.GetValue(null) as object[]; } catch { return null; }
        }

        /// <summary>Get the item array from a chest object (chest.item).</summary>
        public static object[] GetChestItems(object chest)
        {
            if (chest == null) return null;
            try
            {
                var f = chest.GetType().GetField("item", BindingFlags.Public | BindingFlags.Instance);
                return f?.GetValue(chest) as object[];
            }
            catch { return null; }
        }

        /// <summary>Get a chest at a specific index.</summary>
        public static object GetChest(int index)
        {
            var chests = GetChestArray();
            if (chests == null || index < 0 || index >= chests.Length) return null;
            return chests[index];
        }

        // ── Item slot access ────────────────────────────────────────────────────

        public static int GetItemType(object item) => GetField<int>(item, "type");
        public static int GetItemStack(object item) => GetField<int>(item, "stack");
        public static int GetItemPrefix(object item) => GetField<int>(item, "prefix");
        public static int GetItemMaxStack(object item) => GetField<int>(item, "maxStack", 9999);

        public static void SetItemType(object item, int type) => SetField(item, "type", type);
        public static void SetItemStack(object item, int stack) => SetField(item, "stack", stack);
        public static void SetItemPrefix(object item, int prefix) => SetField(item, "prefix", (byte)prefix);

        /// <summary>Call item.SetDefaults(id) via reflection.</summary>
        public static void ItemSetDefaults(object item, int id)
        {
            try
            {
                var m = item?.GetType().GetMethod("SetDefaults", new[] { typeof(int) });
                m?.Invoke(item, new object[] { id });
            }
            catch { }
        }

        // ── NetMessage.TrySendData ───────────────────────────────────────────────

        /// <summary>Broadcast packet 32 (SyncChestItem) for a chest slot.</summary>
        public static void BroadcastChestSlot(int chestIndex, int slot)
        {
            if (!_isDedServ)
            {
                try { Terraria.NetMessage.TrySendData(32, -1, -1, null, chestIndex, slot); return; } catch { }
            }
            try
            {
                // TrySendData(int msgType, int remoteClient=-1, int ignoreClient=-1, NetworkText text=null, int number=0, ...)
                if (_trySendData != null)
                {
                    var prms = _trySendData.GetParameters();
                    var args = new object[prms.Length];
                    for (int i = 0; i < args.Length; i++) args[i] = prms[i].HasDefaultValue ? prms[i].DefaultValue : 0;
                    args[0] = 32;  // msgType
                    if (prms.Length > 1) args[1] = -1;  // remoteClient
                    if (prms.Length > 2) args[2] = -1;  // ignoreClient
                    if (prms.Length > 3) args[3] = null; // text
                    if (prms.Length > 4) args[4] = chestIndex;
                    if (prms.Length > 5) args[5] = (float)slot;
                    _trySendData.Invoke(null, args);
                }
            }
            catch (Exception ex) { _log?.Warn($"[DedServProxy] BroadcastChestSlot({chestIndex},{slot}) failed: {ex.Message}"); }
        }

        // ── Item.NewItem ────────────────────────────────────────────────────────

        /// <summary>Spawn an item drop in the world (server-safe).</summary>
        public static void ItemNewItem(Microsoft.Xna.Framework.Vector2 pos, int width, int height, int itemId, int stack, int prefix)
        {
            var center = new Microsoft.Xna.Framework.Vector2(pos.X + width / 2, pos.Y + height / 2);
            if (!_isDedServ)
            {
                try { Terraria.Item.NewItem(null, center, itemId, stack, prefix); return; } catch { }
            }
            try
            {
                if (_itemNewItem != null)
                {
                    var prms = _itemNewItem.GetParameters();
                    var args = new object[prms.Length];
                    for (int i = 0; i < args.Length; i++) args[i] = prms[i].HasDefaultValue ? prms[i].DefaultValue : null;
                    args[0] = null; // IEntitySource
                    args[1] = center;
                    if (prms.Length > 2) args[2] = itemId;
                    if (prms.Length > 3) args[3] = stack;
                    if (prms.Length > 4) args[4] = prefix;
                    _itemNewItem.Invoke(null, args);
                }
            }
            catch (Exception ex) { _log?.Warn($"[DedServProxy] ItemNewItem failed: {ex.Message}"); }
        }

        /// <summary>
        /// Spawn an item for a connected player. Reads position via raw field access to avoid
        /// cross-assembly Vector2 cast failure (TerrariaServer assembly vs Terraria.exe).
        /// </summary>
        public static void SpawnItemForPlayer(int slot, int itemId, int stack, int prefix)
        {
            try
            {
                var player = GetPlayer(slot);
                if (player == null) { _log?.Warn($"[DedServProxy] SpawnItemForPlayer: player slot {slot} is null"); return; }

                // Read position as raw object to avoid cross-assembly XNA Vector2 cast
                float x = 0f, y = 0f;
                int pw = 16, ph = 48;
                var posField = player.GetType().GetField("position", BindingFlags.Public | BindingFlags.Instance);
                if (posField != null)
                {
                    var posObj = posField.GetValue(player);
                    if (posObj != null)
                    {
                        var xf = posObj.GetType().GetField("X", BindingFlags.Public | BindingFlags.Instance);
                        var yf = posObj.GetType().GetField("Y", BindingFlags.Public | BindingFlags.Instance);
                        if (xf != null) x = (float)xf.GetValue(posObj);
                        if (yf != null) y = (float)yf.GetValue(posObj);
                    }
                }
                var wf2 = player.GetType().GetField("width", BindingFlags.Public | BindingFlags.Instance);
                var hf2 = player.GetType().GetField("height", BindingFlags.Public | BindingFlags.Instance);
                if (wf2 != null) pw = (int)wf2.GetValue(player);
                if (hf2 != null) ph = (int)hf2.GetValue(player);

                _log?.Info($"[DedServProxy] SpawnItemForPlayer slot={slot} pos=({x},{y}) item={itemId}x{stack}");

                var pos = new Microsoft.Xna.Framework.Vector2(x, y);
                ItemNewItem(pos, pw, ph, itemId, stack, prefix);
            }
            catch (Exception ex) { _log?.Warn($"[DedServProxy] SpawnItemForPlayer failed: {ex.Message}"); }
        }

        // ── High-level chest operations (server-safe) ───────────────────────────

        /// <summary>
        /// Remove up to <paramref name="count"/> items from chest[chestIndex].item[slot].
        /// Broadcasts packet 32 on success. Returns actual count taken.
        /// </summary>
        public static int TakeFromChest(int chestIndex, int slot, int count)
        {
            try
            {
                var chests = GetChestArray();
                if (chests == null || chestIndex < 0 || chestIndex >= chests.Length) return 0;
                var chest = chests[chestIndex];
                var items = GetChestItems(chest);
                if (items == null || slot < 0 || slot >= items.Length) return 0;
                var item = items[slot];
                if (item == null) return 0;

                int currentStack = GetItemStack(item);
                if (currentStack <= 0) return 0;

                int take = Math.Min(count, currentStack);
                int remaining = currentStack - take;

                if (remaining <= 0)
                {
                    // Clear the slot
                    ItemSetDefaults(item, 0);
                    SetItemStack(item, 0);
                }
                else
                {
                    SetItemStack(item, remaining);
                }

                BroadcastChestSlot(chestIndex, slot);
                return take;
            }
            catch (Exception ex) { _log?.Warn($"[DedServProxy] TakeFromChest({chestIndex},{slot},{count}) failed: {ex.Message}"); return 0; }
        }

        /// <summary>
        /// Deposit <paramref name="count"/> of <paramref name="itemId"/> into chest[chestIndex].
        /// Stacks with existing matching items first, then fills empty slots.
        /// Broadcasts packet 32 for each modified slot. Returns count deposited.
        /// </summary>
        public static int DepositToChest(int chestIndex, int itemId, int count, int prefix)
        {
            try
            {
                var chests = GetChestArray();
                if (chests == null || chestIndex < 0 || chestIndex >= chests.Length) return 0;
                var chest = chests[chestIndex];
                var items = GetChestItems(chest);
                if (items == null) return 0;

                // We need a temporary item to get maxStack — create one via reflection
                int maxStack = GetMaxStackForItem(itemId);
                int remaining = count;

                // Pass 1: stack with matching items
                for (int i = 0; i < items.Length && remaining > 0; i++)
                {
                    var slot = items[i];
                    if (slot == null) continue;
                    if (GetItemType(slot) != itemId || GetItemPrefix(slot) != prefix) continue;
                    int current = GetItemStack(slot);
                    if (current >= maxStack) continue;

                    int canTake = maxStack - current;
                    int take = Math.Min(canTake, remaining);
                    SetItemStack(slot, current + take);
                    remaining -= take;
                    BroadcastChestSlot(chestIndex, i);
                }

                // Pass 2: fill empty slots
                for (int i = 0; i < items.Length && remaining > 0; i++)
                {
                    var slot = items[i];
                    if (slot == null) continue;
                    if (GetItemType(slot) != 0) continue;

                    int take = Math.Min(maxStack, remaining);
                    ItemSetDefaults(slot, itemId);
                    SetItemStack(slot, take);
                    SetItemPrefix(slot, prefix);
                    remaining -= take;
                    BroadcastChestSlot(chestIndex, i);
                }

                return count - remaining;
            }
            catch (Exception ex) { _log?.Warn($"[DedServProxy] DepositToChest({chestIndex},{itemId},{count}) failed: {ex.Message}"); return 0; }
        }

        private static int GetMaxStackForItem(int itemId)
        {
            try
            {
                // Instantiate a temporary item, call SetDefaults, read maxStack
                Type itemType = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (asm.GetName().Name != "TerrariaServer" && asm.GetName().Name != "Terraria") continue;
                    itemType = asm.GetType("Terraria.Item");
                    if (itemType != null) break;
                }
                if (itemType == null) return 9999;

                var tempItem = Activator.CreateInstance(itemType);
                var setDefaults = itemType.GetMethod("SetDefaults", new[] { typeof(int) });
                setDefaults?.Invoke(tempItem, new object[] { itemId });
                var maxStackField = itemType.GetField("maxStack", BindingFlags.Public | BindingFlags.Instance);
                if (maxStackField != null)
                {
                    int ms = (int)maxStackField.GetValue(tempItem);
                    return ms > 0 ? ms : 9999;
                }
            }
            catch { }
            return 9999;
        }

        // ── Player access ────────────────────────────────────────────────────────

        public static object GetPlayer(int slot)
        {
            if (!_isDedServ)
            {
                try { var p = Terraria.Main.player; return p != null && slot >= 0 && slot < p.Length ? p[slot] : null; } catch { }
            }
            try
            {
                var arr = _playerField?.GetValue(null) as object[];
                return arr != null && slot >= 0 && slot < arr.Length ? arr[slot] : null;
            }
            catch { return null; }
        }

        // ── Chest creation (server-safe) ────────────────────────────────────────

        /// <summary>
        /// Create a chest at (topX, topY) on the server and return the slot index.
        /// On TerrariaServer.exe uses reflection. On Terraria.exe (SP/H&amp;P) calls directly.
        /// Returns -1 on failure.
        /// </summary>
        public static int CreatePaintingChest(int topX, int topY)
        {
            if (!_isDedServ)
            {
                // SP or H&P host — direct call works (netMode == 0 on server)
                try { return Terraria.Chest.CreateChest(topX, topY, -1); } catch { return -1; }
            }
            try
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (asm.GetName().Name != "TerrariaServer") continue;
                    var chestType = asm.GetType("Terraria.Chest");
                    if (chestType == null) break;

                    // FindEmptyChest(int x, int y, int type=21, int style=0, int direction=1, int alternate=0)
                    var findEmpty = chestType.GetMethod("FindEmptyChest", BindingFlags.Public | BindingFlags.Static);
                    if (findEmpty == null) break;
                    int slot = (int)findEmpty.Invoke(null, new object[] { topX, topY, 21, 0, 1, 0 });
                    if (slot < 0) return -1;

                    // CreateWorldChest(int index, int x, int y)
                    var createWorld = chestType.GetMethod("CreateWorldChest",
                        BindingFlags.Public | BindingFlags.Static, null,
                        new[] { typeof(int), typeof(int), typeof(int) }, null);
                    if (createWorld == null) break;
                    createWorld.Invoke(null, new object[] { slot, topX, topY });
                    return slot;
                }
            }
            catch (Exception ex) { _log?.Warn($"[DedServProxy] CreatePaintingChest failed: {ex.Message}"); }
            return -1;
        }

        // ── World data (server-safe) ─────────────────────────────────────────────

        /// <summary>Get Main.dungeonX and Main.dungeonY from the server's Main.</summary>
        public static bool GetDungeonCoords(out int dungeonX, out int dungeonY)
        {
            dungeonX = 0; dungeonY = 0;
            try
            {
                if (!_isDedServ)
                {
                    dungeonX = Terraria.Main.dungeonX;
                    dungeonY = Terraria.Main.dungeonY;
                    // In H&P, Main.dungeonX/Y may be 0 (host client doesn't load world data).
                    // Fall through to TerrariaServer assembly path to get correct coords from child server.
                    if (dungeonX > 0 || dungeonY > 0)
                        return true;
                }
                // Resolve from TerrariaServer assembly
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (asm.GetName().Name != "TerrariaServer") continue;
                    var mainType = asm.GetType("Terraria.Main");
                    if (mainType == null) break;
                    var fx = mainType.GetField("dungeonX", BindingFlags.Public | BindingFlags.Static);
                    var fy = mainType.GetField("dungeonY", BindingFlags.Public | BindingFlags.Static);
                    if (fx != null) dungeonX = (int)fx.GetValue(null);
                    if (fy != null) dungeonY = (int)fy.GetValue(null);
                    return true;
                }
            }
            catch (Exception ex) { _log?.Warn($"[DedServProxy] GetDungeonCoords failed: {ex.Message}"); }
            return false;
        }

        /// <summary>Set Main.time and Main.dayTime then broadcast WorldInfo (packet 7).</summary>
        public static void SetWorldTime(double time, bool dayTime)
        {
            if (!_isDedServ)
            {
                Terraria.Main.time = time;
                Terraria.Main.dayTime = dayTime;
                Terraria.NetMessage.SendData(7);
                return;
            }
            try
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (asm.GetName().Name != "TerrariaServer") continue;
                    var mainType = asm.GetType("Terraria.Main");
                    if (mainType == null) break;
                    mainType.GetField("time", BindingFlags.Public | BindingFlags.Static)?.SetValue(null, time);
                    mainType.GetField("dayTime", BindingFlags.Public | BindingFlags.Static)?.SetValue(null, dayTime);

                    // Broadcast WorldInfo to all clients
                    var netMsgType = asm.GetType("Terraria.NetMessage");
                    var sendData = netMsgType?.GetMethod("SendData",
                        new[] { typeof(int), typeof(int), typeof(int), asm.GetType("Terraria.Localization.NetworkText"),
                                typeof(int), typeof(float), typeof(float), typeof(float), typeof(int), typeof(int), typeof(int) });
                    if (sendData != null)
                        sendData.Invoke(null, new object[] { 7, -1, -1, null, 0, 0f, 0f, 0f, 0, 0, 0 });
                    else
                    {
                        // Try TrySendData fallback
                        if (_trySendData != null)
                        {
                            var prms = _trySendData.GetParameters();
                            var args = new object[prms.Length];
                            for (int i = 0; i < args.Length; i++) args[i] = prms[i].HasDefaultValue ? prms[i].DefaultValue : 0;
                            args[0] = 7;
                            if (prms.Length > 1) args[1] = -1;
                            if (prms.Length > 2) args[2] = -1;
                            _trySendData.Invoke(null, args);
                        }
                    }
                    break;
                }
            }
            catch (Exception ex) { _log?.Warn($"[DedServProxy] SetWorldTime failed: {ex.Message}"); }
        }

        /// <summary>Kick a player by slot. Works on both H&amp;P host and dedicated server.</summary>
        public static void KickPlayer(int slot)
        {
            if (!_isDedServ)
            {
                Terraria.NetMessage.SendData(2, slot, -1, Terraria.Localization.NetworkText.FromKey("CLI.KickMessage"));
                return;
            }
            try
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (asm.GetName().Name != "TerrariaServer") continue;
                    var netMsgType = asm.GetType("Terraria.NetMessage");
                    var networkTextType = asm.GetType("Terraria.Localization.NetworkText");
                    var fromKey = networkTextType?.GetMethod("FromKey", new[] { typeof(string), typeof(object[]) });
                    var kickText = fromKey?.Invoke(null, new object[] { "CLI.KickMessage", Array.Empty<object>() });

                    var sendData = netMsgType?.GetMethod("SendData",
                        new[] { typeof(int), typeof(int), typeof(int), networkTextType,
                                typeof(int), typeof(float), typeof(float), typeof(float), typeof(int), typeof(int), typeof(int) });
                    if (sendData != null)
                        sendData.Invoke(null, new object[] { 2, slot, -1, kickText, 0, 0f, 0f, 0f, 0, 0, 0 });
                    break;
                }
            }
            catch (Exception ex) { _log?.Warn($"[DedServProxy] KickPlayer failed: {ex.Message}"); }
        }

        // ── Generic helpers ─────────────────────────────────────────────────────

        private static T GetField<T>(object obj, string name, T fallback = default)
        {
            try
            {
                var f = obj?.GetType().GetField(name, BindingFlags.Public | BindingFlags.Instance);
                if (f == null) return fallback;
                return (T)f.GetValue(obj);
            }
            catch { return fallback; }
        }

        private static void SetField(object obj, string name, object value)
        {
            try
            {
                var f = obj?.GetType().GetField(name, BindingFlags.Public | BindingFlags.Instance);
                f?.SetValue(obj, value);
            }
            catch { }
        }
    }
}
