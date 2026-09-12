using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using HarmonyLib;
using Terraria;
using Terraria.DataStructures;
using Terraria.GameContent.Drawing;
using TerrariaModder.Core.Assets;
using TerrariaModder.Core.Logging;
using TerrariaModder.Core.Net;

namespace StorageHub.PaintingChest
{
    /// <summary>
    /// Harmony patches for the Mysterious Chest (type 21, style 69).
    ///
    /// Vanilla handles most chest operations natively for type 21:
    /// - Placement: Chest.AfterPlacement_Hook creates chest entry
    /// - Opening: Player.TileInteractionsUse handles chest open/close
    /// - Break protection: Chest.CanDestroyChest prevents breaking if has items
    /// - MP sync: Packets 32/33/34 handle chest content sync
    /// - Save/load: SaveChests/LoadChests handle persistence
    ///
    /// We patch:
    /// 1. AfterPlacement_Hook postfix → resize chest + set name
    /// 2. GetItemDrop_Chests prefix → return our custom item on break (both assemblies)
    /// 3. CheckChest prefix → handle client-side destruction in H&P (Terraria.exe only)
    /// 4. TileInteractionsUse prefix → CoinSlot resize for large capacity
    /// </summary>
    internal static class PaintingChestPatches
    {
        private static Harmony _harmony;
        private static ILogger _log;
        private static bool _patchesApplied;

        private static FieldInfo _chestEntriesField;

        public static void ApplyPatches(ILogger logger)
        {
            if (_patchesApplied) return;
            _log = logger;
            _harmony = new Harmony("com.terrariamodder.storagehub.paintingchest");

            NetSync.OnServerCommandResponse += OnServerCommandResponse;

            var patchType = typeof(PaintingChestPatches);

            // Patch 1: Chest.AfterPlacement_Hook postfix — resize chest + set name after creation
            var afterPlaceMethod = typeof(Chest).GetMethod("AfterPlacement_Hook",
                BindingFlags.Public | BindingFlags.Static);
            if (afterPlaceMethod != null)
            {
                _harmony.Patch(afterPlaceMethod, postfix: new HarmonyMethod(
                    patchType.GetMethod(nameof(AfterPlacement_Postfix), BindingFlags.Public | BindingFlags.Static)));
                _log?.Info("Patched Chest.AfterPlacement_Hook");
            }

            // Patch 2: GetItemDrop_Chests prefix — return our custom item for style 69.
            // Patched on BOTH Terraria.exe and TerrariaServer.exe (ded server is a separate process).
            var dropPrefix = new HarmonyMethod(
                patchType.GetMethod(nameof(GetItemDrop_Chests_Prefix), BindingFlags.Public | BindingFlags.Static));

            var getDropMethod = typeof(WorldGen).GetMethod("GetItemDrop_Chests",
                BindingFlags.Public | BindingFlags.Static);
            if (getDropMethod != null)
            {
                _harmony.Patch(getDropMethod, prefix: dropPrefix);
                _log?.Info("Patched WorldGen.GetItemDrop_Chests (Terraria.exe)");
            }

            // Scan for TerrariaServer assembly and patch its GetItemDrop_Chests too
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                string asmName = asm.GetName().Name;
                if (asmName != "TerrariaServer" && asmName != "Terraria") continue;
                var serverWorldGen = asm.GetType("Terraria.WorldGen");
                if (serverWorldGen == null) continue;

                var serverGetDrop = serverWorldGen.GetMethod("GetItemDrop_Chests",
                    BindingFlags.Public | BindingFlags.Static);
                if (serverGetDrop != null && serverGetDrop != getDropMethod)
                {
                    _harmony.Patch(serverGetDrop, prefix: dropPrefix);
                    _log?.Info($"Patched WorldGen.GetItemDrop_Chests ({asmName} assembly)");
                }
            }

            // Patch 3: CheckChest prefix — handles client-side chest destruction in H&P.
            // In H&P, the server is a separate process (TerrariaServer.exe) that handles
            // the actual break + item drop. On the client, CheckChest cascades via SquareTileFrame
            // after the tile is killed. We intercept to handle the correct item drop client-side.
            // On ded server, vanilla handles CheckChest — our GetItemDrop_Chests prefix is enough.
            var checkChestMethod = typeof(WorldGen).GetMethod("CheckChest",
                BindingFlags.Public | BindingFlags.Static, null,
                new[] { typeof(int), typeof(int), typeof(int) }, null);
            if (checkChestMethod != null)
            {
                _harmony.Patch(checkChestMethod, prefix: new HarmonyMethod(
                    patchType.GetMethod(nameof(CheckChest_Prefix), BindingFlags.NonPublic | BindingFlags.Static)));
                _log?.Info("Patched WorldGen.CheckChest");
            }

            // Patch 4: TileInteractionsUse prefix — resize CoinSlot for large chests
            var coinSlotType = typeof(Main).Assembly.GetType("Terraria.UI.CoinSlot");
            _chestEntriesField = coinSlotType?.GetField("ChestEntries",
                BindingFlags.NonPublic | BindingFlags.Static);

            var interactMethod = typeof(Player).GetMethod("TileInteractionsUse",
                BindingFlags.NonPublic | BindingFlags.Instance, null,
                new[] { typeof(int), typeof(int) }, null);
            if (interactMethod != null)
            {
                _harmony.Patch(interactMethod, prefix: new HarmonyMethod(
                    patchType.GetMethod(nameof(TileInteractionsUse_Prefix), BindingFlags.Public | BindingFlags.Static)));
                _log?.Info("Patched Player.TileInteractionsUse (CoinSlot resize)");
            }

            var drawMethod = typeof(TileDrawing).GetMethod("CacheSpecialDraws_Part2",
                BindingFlags.NonPublic | BindingFlags.Instance, null,
                new[] { typeof(int), typeof(int), typeof(TileDrawInfo) }, null);
            if (drawMethod == null) throw new MissingMethodException("TileDrawing.CacheSpecialDraws_Part2");
            _harmony.Patch(drawMethod, postfix: new HarmonyMethod(patchType,
                nameof(ChestDraw_Postfix)));
            _patchesApplied = true;
        }

        private static void ChestDraw_Postfix(TileDrawInfo drawData)
        {
            if (!TileTextureExtender.IsTileExtended || drawData.typeCache != PaintingChestManager.TILE_TYPE ||
                drawData.tileCache.frameX / 36 != PaintingChestManager.OUR_PLACE_STYLE) return;
            drawData.tileFrameX = (short)(drawData.tileFrameX % 36);
            drawData.addFrY += TileTextureExtender.AtlasYOffset;
        }

        public static void Unpatch()
        {
            NetSync.OnServerCommandResponse -= OnServerCommandResponse;
            _harmony?.UnpatchAll("com.terrariamodder.storagehub.paintingchest");
            _patchesApplied = false;
        }

        #region Server command handling (MP placement)

        private static void OnServerCommandResponse(string type, string result)
        {
            // Handle both old "paintingchest_created" (legacy) and new "paintingchest_configured"
            if (type != "paintingchest_created" && type != "paintingchest_configured") return;

            var parts = result.Split(',');
            if (parts.Length < 2) return;

            try
            {
                int topX, topY;
                if (type == "paintingchest_created" && parts.Length >= 3)
                {
                    // Legacy: slot,x,y — create chest locally
                    if (!int.TryParse(parts[0], out int slot) ||
                        !int.TryParse(parts[1], out topX) ||
                        !int.TryParse(parts[2], out topY)) return;
                    int idx = Chest.CreateChest(topX, topY, slot);
                    _log?.Info($"[Chest] Legacy: created chest slot={slot} idx={idx} at ({topX},{topY})");
                    if (idx < 0) return;
                }
                else
                {
                    // New: x,y — find existing chest and configure
                    if (!int.TryParse(parts[0], out topX) ||
                        !int.TryParse(parts[1], out topY)) return;
                }

                // Find and configure the chest (may already exist from vanilla sync)
                int chestIdx = Chest.FindChest(topX, topY);
                if (chestIdx < 0)
                {
                    _log?.Warn($"[Chest] No chest found at ({topX},{topY}) after server configure");
                    return;
                }

                var chest = Main.chest[chestIdx];
                if (chest == null) return;

                int capacity = PaintingChestManager.GetCurrentCapacity();
                chest.Resize(capacity);
                chest.name = PaintingChestManager.CHEST_NAME;
                _log?.Info($"[Chest] Configured chest idx={chestIdx} at ({topX},{topY}) maxItems={chest.maxItems}");
            }
            catch (Exception ex)
            {
                _log?.Error($"[Chest] OnServerCommandResponse error: {ex.Message}");
            }
        }

        #endregion

        #region Helpers

        private static void EnsureCoinSlotCapacity(int requiredSlots)
        {
            if (_chestEntriesField == null) return;
            try
            {
                var arr = _chestEntriesField.GetValue(null) as Array;
                if (arr != null && arr.Length < requiredSlots)
                {
                    var elemType = arr.GetType().GetElementType();
                    var newArr = Array.CreateInstance(elemType, requiredSlots);
                    Array.Copy(arr, newArr, arr.Length);
                    _chestEntriesField.SetValue(null, newArr);
                    _log?.Info($"Resized CoinSlot.ChestEntries: {arr.Length} → {requiredSlots}");
                }
            }
            catch (Exception ex)
            {
                _log?.Error($"EnsureCoinSlotCapacity error: {ex.Message}");
            }
        }

        private static bool IsOurChest(int x, int y)
        {
            try
            {
                var tile = Main.tile[x, y];
                if (tile == null || !tile.active() || tile.type != PaintingChestManager.TILE_TYPE)
                    return false;
                int style = tile.frameX / 36;
                return style == PaintingChestManager.OUR_PLACE_STYLE;
            }
            catch { return false; }
        }

        private static int _cachedItemType = -1;

        private static int GetOurItemType()
        {
            if (_cachedItemType > 0) return _cachedItemType;

            int type = ItemRegistry.GetRuntimeType(PaintingChestManager.FULL_ITEM_ID);
            if (type > 0)
            {
                _cachedItemType = type;
                return type;
            }

            _log?.Error($"[CRITICAL] GetOurItemType: ItemRegistry returned {type} for '{PaintingChestManager.FULL_ITEM_ID}' — chest break will lose the item!");
            return 0;
        }

        #endregion

        #region Patch 1: Chest.AfterPlacement_Hook postfix — resize + name

        /// <summary>
        /// Runs after Chest.AfterPlacement_Hook creates the chest entry.
        /// In SP/server: chest exists in Main.chest[__result].
        /// In MP client: chest doesn't exist locally yet (server creates it), so we poll.
        /// </summary>
        public static void AfterPlacement_Postfix(int __result, int x, int y, int type, int style)
        {
            if (!PaintingChestManager.Enabled) return;
            if (type != PaintingChestManager.TILE_TYPE) return;
            if (style != PaintingChestManager.OUR_PLACE_STYLE) return;

            try
            {
                // AfterPlacement_Hook converts origin→topLeft internally, so x,y here are
                // still the raw origin coords. Convert to top-left for FindChest.
                var tileData = Terraria.ObjectData.TileObjectData.GetTileData(type, style);
                int topX = x, topY = y;
                if (tileData != null)
                {
                    topX -= tileData.Origin.X;
                    topY -= tileData.Origin.Y;
                }

                if (Main.netMode == 1)
                {
                    // MP client: server creates the chest, syncs to client later.
                    // Poll until client has the chest entry, then configure.
                    _log?.Info($"[Chest] MP client — polling for chest at ({topX},{topY})");
                    int capturedX = topX, capturedY = topY;
                    System.Threading.Tasks.Task.Run(async () =>
                    {
                        for (int attempt = 0; attempt < 20; attempt++)
                        {
                            await System.Threading.Tasks.Task.Delay(250);
                            try
                            {
                                int idx = Chest.FindChest(capturedX, capturedY);
                                if (idx >= 0)
                                {
                                    var c = Main.chest[idx];
                                    if (c != null)
                                    {
                                        int capacity = PaintingChestManager.GetCurrentCapacity();
                                        c.Resize(capacity);
                                        c.name = PaintingChestManager.CHEST_NAME;
                                        _log?.Info($"[Chest] MP configured idx={idx} at ({capturedX},{capturedY}) after {attempt+1} attempts");

                                        // Sync name to server via packet 69 (SyncChestName)
                                        try
                                        {
                                            var netMsgType = typeof(Terraria.Main).Assembly.GetType("Terraria.NetMessage");
                                            var trySend = netMsgType?.GetMethod("TrySendData",
                                                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                                            if (trySend != null)
                                            {
                                                var ntType = typeof(Terraria.Main).Assembly.GetType("Terraria.Localization.NetworkText");
                                                var fromLiteral = ntType?.GetMethod("FromLiteral",
                                                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                                                var nameText = fromLiteral?.Invoke(null, new object[] { PaintingChestManager.CHEST_NAME });
                                                trySend.Invoke(null, new object[] { 69, -1, -1, nameText, idx, (float)capturedX, (float)capturedY, 0f, 0, 0, 0 });
                                                _log?.Info($"[Chest] Sent name sync packet for idx={idx}");
                                            }
                                        }
                                        catch (Exception syncEx) { _log?.Warn($"[Chest] Name sync failed: {syncEx.Message}"); }
                                        return;
                                    }
                                }
                            }
                            catch { }
                        }
                        _log?.Warn($"[Chest] MP configure timed out for ({capturedX},{capturedY})");
                    });
                    return;
                }

                // SP or server: __result is the chest index from AfterPlacement_Hook
                if (__result < 0) return;

                var chest = Main.chest[__result];
                if (chest == null) return;

                int cap = PaintingChestManager.GetCurrentCapacity();
                chest.Resize(cap);
                chest.name = PaintingChestManager.CHEST_NAME;
                _log?.Info($"[Chest] Configured chest idx={__result} at ({topX},{topY}) maxItems={chest.maxItems}");

                // Fill from migration sidecar if there are pending legacy painting contents
                TryFillFromMigration(__result, PaintingChestManager.GetWorldFolder(), _log);
            }
            catch (Exception ex)
            {
                _log?.Error($"AfterPlacement_Postfix error: {ex.Message}");
            }
        }

        #endregion

        #region Patch 2: GetItemDrop_Chests prefix — correct item drop

        /// <summary>
        /// Returns our custom item type for style 69. Patched on both Terraria.exe and
        /// TerrariaServer.exe assemblies. Does not check PaintingChestManager.Enabled
        /// because the ded server doesn't initialize the manager (UI-only).
        /// </summary>
        public static bool GetItemDrop_Chests_Prefix(int style, bool secondType, ref int __result)
        {
            try
            {
                if (secondType || style != PaintingChestManager.OUR_PLACE_STYLE) return true;

                int itemType = GetOurItemType();
                if (itemType <= 0) return true;

                __result = itemType;
                return false;
            }
            catch { return true; }
        }

        /// <summary>
        /// Client-side CheckChest prefix for H&P. In H&P, the server is a separate process
        /// (TerrariaServer.exe) that handles the actual break. On the client, CheckChest
        /// cascades via SquareTileFrame after the tile is killed by the server's response.
        /// We intercept to drop our custom item. On ded server, we skip — vanilla handles
        /// it with our GetItemDrop_Chests prefix providing the correct type.
        /// </summary>
        private static bool CheckChest_Prefix(int i, int j, int type)
        {
            if (type != PaintingChestManager.TILE_TYPE) return true;
            if (Environment.GetEnvironmentVariable("TERRARIA_MODDER_DEDSERV") == "1") return true;
            if (WorldGen.destroyObject) return true;
            if (!PaintingChestManager.Enabled) return true;

            try
            {
                // Vanilla retains frame coordinates on the tile being destroyed. Resolve
                // that chest only: searching neighbors can intercept a different chest's
                // framing and leave its remaining three tiles behind.
                var tile = Main.tile[i, j];
                if (tile == null || tile.type != PaintingChestManager.TILE_TYPE ||
                    tile.frameX / 36 != PaintingChestManager.OUR_PLACE_STYLE) return true;
                int num = i - (tile.frameX % 36) / 18;
                int num2 = j - tile.frameY / 18;

                // Check structural integrity (same as vanilla CheckChest)
                bool flag = false;
                for (int k = num; k < num + 2; k++)
                {
                    for (int l = num2; l < num2 + 2; l++)
                    {
                        var t = Main.tile[k, l];
                        if (t == null) { t = new Tile(); Main.tile[k, l] = t; }
                        int fx = t.frameX / 18;
                        while (fx > 1) fx -= 2;
                        if (!t.active() || t.type != type || fx != k - num || t.frameY != (l - num2) * 18)
                            flag = true;
                    }
                    var below = Main.tile[k, num2 + 2];
                    if (below == null) { below = new Tile(); Main.tile[k, num2 + 2] = below; }
                    if ((!below.active() || !Main.tileSolid[below.type]) && Chest.CanDestroyChest(num, num2))
                        flag = true;
                }

                if (!flag) return false; // Structure intact, skip vanilla too

                int ourItemType = GetOurItemType();

                bool previousDestroyObject = WorldGen.destroyObject;
                try
                {
                    WorldGen.destroyObject = true;
                    for (int m = num; m < num + 2; m++)
                    {
                        for (int n = num2; n < num2 + 3; n++)
                        {
                            var t = Main.tile[m, n];
                            if (t != null && t.type == type && t.active())
                            {
                                Chest.DestroyChest(m, n);
                                WorldGen.KillTile(m, n);
                            }
                        }
                    }

                    if (ourItemType > 0)
                    {
                        Item.NewItem(WorldGen.GetItemSource_FromTileBreak(i, j),
                            i * 16, j * 16, 32, 32, ourItemType);
                    }

                }
                finally { WorldGen.destroyObject = previousDestroyObject; }
                return false;
            }
            catch (Exception ex)
            {
                _log?.Error($"[Drop] CheckChest_Prefix exception: {ex.Message}");
                return true;
            }
        }

        #endregion

        #region Patch 3: TileInteractionsUse prefix — CoinSlot resize

        /// <summary>
        /// Before vanilla opens our chest, resize CoinSlot.ChestEntries if needed.
        /// Our chests can have up to 5000 slots; vanilla allocates 200.
        /// </summary>
        public static bool TileInteractionsUse_Prefix(object __instance, int myX, int myY)
        {
            if (!PaintingChestManager.Enabled) return true;

            try
            {
                if (!IsOurChest(myX, myY)) return true;

                // Find our chest and resize CoinSlot before vanilla opens it
                int chestIdx = Chest.FindChest(myX, myY);
                if (chestIdx < 0)
                {
                    // Try nearby (2x2 chest, might click on different tile)
                    var tile = Main.tile[myX, myY];
                    if (tile != null)
                    {
                        int topX = myX - (tile.frameX % 36) / 18;
                        int topY = myY - tile.frameY / 18;
                        chestIdx = Chest.FindChest(topX, topY);
                    }
                }

                if (chestIdx >= 0)
                {
                    var chest = Main.chest[chestIdx];
                    if (chest != null)
                        EnsureCoinSlotCapacity(chest.maxItems);
                }
            }
            catch { }

            return true; // Always let vanilla handle the actual open/close
        }

        #endregion

        #region Legacy tile 246 migration

        private const string MIGRATION_FILE = "painting-migration.json";

        /// <summary>
        /// Migrate old-style painting tiles (type 246, style 37) to sidecar file.
        /// Extracts chest contents before deleting tiles so items can be restored
        /// when the user places new Mysterious Chests.
        /// </summary>
        public static void MigrateLegacyTiles(ILogger log, string worldFolder)
        {
            if (string.IsNullOrEmpty(worldFolder)) return;

            var sidecarPath = Path.Combine(worldFolder, MIGRATION_FILE);
            if (File.Exists(sidecarPath))
            {
                log?.Info("[Migration] Sidecar exists, skipping tile scan");
                return;
            }

            var extractedChests = new List<MigrationChestData>();
            int tilesRemoved = 0;

            try
            {
                var paintingPositions = new List<(int x, int y)>();
                for (int x = 0; x < Main.maxTilesX; x++)
                {
                    for (int y = 0; y < Main.maxTilesY; y++)
                    {
                        var tile = Main.tile[x, y];
                        if (tile == null || !tile.active() || tile.type != 246) continue;
                        int style = tile.frameY / 36;
                        if (style != 37) continue;
                        paintingPositions.Add((x, y));
                    }
                }

                if (paintingPositions.Count == 0)
                {
                    log?.Info("[Migration] No legacy painting tiles found");
                    return;
                }

                log?.Info($"[Migration] Found {paintingPositions.Count} legacy painting tile(s)");

                var processedChests = new HashSet<int>();
                foreach (var (px, py) in paintingPositions)
                {
                    int chestIdx = Chest.FindChest(px, py);
                    if (chestIdx < 0)
                    {
                        for (int dx = -2; dx <= 0 && chestIdx < 0; dx++)
                            for (int dy = -2; dy <= 0 && chestIdx < 0; dy++)
                                if (dx != 0 || dy != 0)
                                    chestIdx = Chest.FindChest(px + dx, py + dy);
                    }

                    if (chestIdx >= 0 && !processedChests.Contains(chestIdx))
                    {
                        processedChests.Add(chestIdx);
                        var chest = Main.chest[chestIdx];
                        if (chest != null && chest.item != null)
                        {
                            var items = new List<MigrationItemData>();
                            for (int s = 0; s < chest.item.Length; s++)
                            {
                                var item = chest.item[s];
                                if (item != null && item.type > 0 && item.stack > 0)
                                    items.Add(new MigrationItemData { Type = item.type, Stack = item.stack, Prefix = item.prefix });
                            }
                            if (items.Count > 0)
                            {
                                extractedChests.Add(new MigrationChestData { Items = items });
                                log?.Info($"[Migration] Extracted {items.Count} items from chest idx={chestIdx} at ({chest.x},{chest.y})");
                            }
                            Chest.DestroyChest(chest.x, chest.y);
                        }
                    }

                    var tile = Main.tile[px, py];
                    if (tile != null) { tile.active(false); tile.type = 0; tilesRemoved++; }
                }
            }
            catch (Exception ex) { log?.Error($"[Migration] Error scanning tiles: {ex.Message}"); }

            try
            {
                Directory.CreateDirectory(worldFolder);
                var sidecar = new MigrationSidecar { Chests = extractedChests };
                WriteMigrationSidecar(sidecarPath, sidecar);
                log?.Info($"[Migration] Saved sidecar: {extractedChests.Count} chest(s), {tilesRemoved} tile(s) removed");
            }
            catch (Exception ex) { log?.Error($"[Migration] Failed to write sidecar: {ex.Message}"); }
        }

        /// <summary>
        /// Called after a new Mysterious Chest is placed and configured.
        /// Fills it with items from the migration sidecar if available.
        /// </summary>
        public static void TryFillFromMigration(int chestIdx, string worldFolder, ILogger log)
        {
            if (string.IsNullOrEmpty(worldFolder) || chestIdx < 0) return;
            var sidecarPath = Path.Combine(worldFolder, MIGRATION_FILE);
            if (!File.Exists(sidecarPath)) return;

            try
            {
                var sidecar = ReadMigrationSidecar(sidecarPath);
                if (sidecar == null) return;

                MigrationChestData pending = null;
                for (int i2 = 0; i2 < sidecar.Chests.Count; i2++)
                {
                    if (!sidecar.Chests[i2].Migrated && sidecar.Chests[i2].Items.Count > 0)
                    { pending = sidecar.Chests[i2]; break; }
                }
                if (pending == null) return;

                var chest = Main.chest[chestIdx];
                if (chest == null || chest.item == null) return;

                int filled = 0, slot = 0;
                foreach (var mItem in pending.Items)
                {
                    if (slot >= chest.item.Length) break;
                    while (slot < chest.item.Length && chest.item[slot] != null && chest.item[slot].type > 0) slot++;
                    if (slot >= chest.item.Length) break;
                    chest.item[slot] = new Item();
                    chest.item[slot].SetDefaults(mItem.Type);
                    chest.item[slot].stack = mItem.Stack;
                    if (mItem.Prefix > 0) chest.item[slot].Prefix(mItem.Prefix);
                    filled++; slot++;
                }

                pending.Migrated = true;
                WriteMigrationSidecar(sidecarPath, sidecar);

                int remaining = 0;
                foreach (var c in sidecar.Chests)
                    if (!c.Migrated && c.Items.Count > 0) remaining++;

                log?.Info($"[Migration] Filled chest idx={chestIdx} with {filled} items from legacy painting. {remaining} chest(s) remaining.");
            }
            catch (Exception ex) { log?.Warn($"[Migration] TryFillFromMigration error: {ex.Message}"); }
        }

        #region Migration sidecar IO

        private class MigrationSidecar { public List<MigrationChestData> Chests { get; set; } = new List<MigrationChestData>(); }
        private class MigrationChestData { public List<MigrationItemData> Items { get; set; } = new List<MigrationItemData>(); public bool Migrated { get; set; } }
        private class MigrationItemData { public int Type { get; set; } public int Stack { get; set; } public int Prefix { get; set; } }

        private static MigrationSidecar ReadMigrationSidecar(string path)
        {
            try
            {
                var json = File.ReadAllText(path);
                var sidecar = new MigrationSidecar();
                int idx = 0;
                while ((idx = json.IndexOf("\"items\"", idx)) >= 0)
                {
                    var chestData = new MigrationChestData();
                    int migratedPos = json.IndexOf("\"migrated\"", idx);
                    int nextChestPos = json.IndexOf("\"items\"", idx + 7);
                    if (migratedPos >= 0 && (nextChestPos < 0 || migratedPos < nextChestPos))
                    {
                        int colonPos = json.IndexOf(':', migratedPos + 10);
                        if (colonPos >= 0)
                        {
                            string val = json.Substring(colonPos + 1, Math.Min(10, json.Length - colonPos - 1)).Trim().ToLowerInvariant();
                            chestData.Migrated = val.StartsWith("true");
                        }
                    }
                    int arrStart = json.IndexOf('[', idx);
                    int arrEnd = json.IndexOf(']', arrStart);
                    if (arrStart < 0 || arrEnd < 0) { idx += 7; continue; }
                    string itemsStr = json.Substring(arrStart, arrEnd - arrStart + 1);
                    int pos = 0;
                    while (pos < itemsStr.Length)
                    {
                        int tPos = itemsStr.IndexOf("\"t\"", pos);
                        if (tPos < 0) break;
                        int t = 0, s = 0, p = 0;
                        int colon = itemsStr.IndexOf(':', tPos + 3);
                        if (colon >= 0) int.TryParse(ReadJsonNumber(itemsStr, colon + 1), out t);
                        int sPos = itemsStr.IndexOf("\"s\"", tPos);
                        if (sPos >= 0) { colon = itemsStr.IndexOf(':', sPos + 3); if (colon >= 0) int.TryParse(ReadJsonNumber(itemsStr, colon + 1), out s); }
                        int pPos = itemsStr.IndexOf("\"p\"", tPos);
                        if (pPos >= 0 && pPos < itemsStr.IndexOf('}', tPos) + 1) { colon = itemsStr.IndexOf(':', pPos + 3); if (colon >= 0) int.TryParse(ReadJsonNumber(itemsStr, colon + 1), out p); }
                        if (t > 0 && s > 0) chestData.Items.Add(new MigrationItemData { Type = t, Stack = s, Prefix = p });
                        pos = itemsStr.IndexOf('}', tPos);
                        if (pos < 0) break;
                        pos++;
                    }
                    sidecar.Chests.Add(chestData);
                    idx += 7;
                }
                return sidecar;
            }
            catch { return null; }
        }

        private static string ReadJsonNumber(string json, int startIdx)
        {
            var sb = new StringBuilder();
            for (int i2 = startIdx; i2 < json.Length; i2++)
            {
                char c = json[i2];
                if (char.IsDigit(c) || c == '-') sb.Append(c);
                else if (sb.Length > 0) break;
            }
            return sb.ToString();
        }

        private static void WriteMigrationSidecar(string path, MigrationSidecar sidecar)
        {
            var sb = new StringBuilder();
            sb.AppendLine("{");
            sb.AppendLine("  \"version\": 1,");
            sb.AppendLine("  \"chests\": [");
            for (int c = 0; c < sidecar.Chests.Count; c++)
            {
                var chest = sidecar.Chests[c];
                sb.AppendLine("    {");
                sb.Append("      \"items\": [");
                for (int i2 = 0; i2 < chest.Items.Count; i2++)
                {
                    var item = chest.Items[i2];
                    sb.Append($"{{\"t\":{item.Type},\"s\":{item.Stack},\"p\":{item.Prefix}}}");
                    if (i2 < chest.Items.Count - 1) sb.Append(",");
                }
                sb.AppendLine("],");
                sb.AppendLine($"      \"migrated\": {(chest.Migrated ? "true" : "false")}");
                sb.Append("    }");
                if (c < sidecar.Chests.Count - 1) sb.Append(",");
                sb.AppendLine();
            }
            sb.AppendLine("  ]");
            sb.AppendLine("}");
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, sb.ToString());
        }

        #endregion

        #endregion
    }
}
