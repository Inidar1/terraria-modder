using System;
using Terraria;
using TerrariaModder.Core;
using TerrariaModder.Core.Assets;
using TerrariaModder.Core.Logging;
using TerrariaModder.Core.UI;
using StorageHub.Config;

namespace StorageHub.PaintingChest
{
    /// <summary>
    /// Manages the "Mysterious Chest" — a custom chest tile (type 21, style 69) with
    /// progression-based capacity. Uses vanilla chest tile infrastructure for full MP
    /// compatibility (placement, sync, save/load all handled by vanilla).
    ///
    /// History: Originally used tile 246 (painting) with custom style 37. Style 37's
    /// frameY=1332 was beyond vanilla's spritesheet range, causing H&P client hang
    /// during tile section processing. Switched to chest type 21 which uses frameX
    /// (unbounded) and has full vanilla MP support.
    /// </summary>
    public static class PaintingChestManager
    {
        public const int TILE_TYPE = 21;       // Vanilla chest tile — full MP support
        public const int OUR_PLACE_STYLE = 69; // Well beyond vanilla's 52 styles — no collisions
        public const string FULL_ITEM_ID = "storage-hub:painting-chest";
        public const string CHEST_NAME = "Mysterious Chest";
        public const string LEGACY_CHEST_NAME = "Mysterious Painting"; // Migration from painting tile era

        public static bool Enabled { get; private set; }

        private static ILogger _log;
        private static StorageHubConfig _config;

        /// <summary>Get the current world's storage folder path (for migration sidecars).</summary>
        public static string GetWorldFolder() => _config?.GetWorldFolder();

        public static void Initialize(ILogger logger, ModContext context)
        {
            _log = logger;
            Enabled = true;

            // Register the placeable item — creates a type 21 chest with style 69
            context.RegisterItem("painting-chest", new ItemDefinition
            {
                DisplayName = "Mysterious Chest",
                Tooltip = new[] { "A chest that holds far more than it appears", "Capacity grows with world progression" },
                CreateTile = TILE_TYPE,
                PlaceStyle = OUR_PLACE_STYLE,
                Width = 32,
                Height = 32,
                MaxStack = 99,
                Consumable = true,
                UseStyle = 1,
                UseTime = 10,
                UseAnimation = 15,
                AutoReuse = true,
                Rarity = 2,
                Value = 10000
            });

            context.AddShopItem(new ShopDefinition
            {
                NpcType = 1,
                ItemId = FULL_ITEM_ID,
                Price = 10000
            });

            UIRenderer.RegisterPanelDraw("painting-chest-label", Draw);

            // tileContainer[21] is already true in vanilla — no need to set it.
            // Vanilla handles chest save/load, break protection, MP sync natively.

            TileTextureExtender.Initialize(_log);

            // Extend and override Lang.chestType and Chest.chestTypeToIcon arrays.
            // Vanilla arrays are size 52. Our style 69 is OOB — multiple vanilla code paths
            // access Lang.chestType[frameX/36] without bounds checks (ChestUI, IngameFancyUI, Main.DrawMap).
            // Must extend BEFORE any chest interactions happen.
            ExtendChestArrays(logger);

            // Apply our minimal patches — only need:
            // 1. Chest.AfterPlacement_Hook postfix → resize chest + set name on placement
            // 2. GetItemDrop_Chests prefix → drop our custom item (not vanilla default for style 69)
            try
            {
                PaintingChestPatches.ApplyPatches(_log);
                _log.Info("Mysterious Chest patches applied");
            }
            catch (Exception ex)
            {
                _log.Error($"Patch error: {ex.Message}");
            }

            _log.Info("Mysterious Chest initialized (type 21, style 69)");
        }

        /// <summary>Retry texture installation when graphics resources become ready.</summary>
        public static void Update()
        {
            TileTextureExtender.TryExtend();
        }

        public static void OnWorldLoad(StorageHubConfig config)
        {
            _config = config;

            // Migrate legacy painting tiles (type 246 style 37) — extracts chest contents
            // to a sidecar file before removing tiles. Contents are restored when user
            // places new Mysterious Chests.
            PaintingChestPatches.MigrateLegacyTiles(_log, config.GetWorldFolder());

            // Skip texture/resize on server — no GraphicsDevice, no UI
            bool isDedServ = Environment.GetEnvironmentVariable("TERRARIA_MODDER_DEDSERV") == "1";
            if (!isDedServ)
            {
                TileTextureExtender.TryExtend();
                ResizeAllPaintingChests(GetCurrentCapacity());
            }
        }

        private static void ExtendChestArrays(ILogger log)
        {
            int requiredSize = OUR_PLACE_STYLE + 1; // 70

            // 1. Extend Lang.chestType (LocalizedText[52] → LocalizedText[70+])
            try
            {
                var langType = typeof(Main).Assembly.GetType("Terraria.Lang");
                var chestTypeField = langType?.GetField("chestType",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                if (chestTypeField != null)
                {
                    var arr = chestTypeField.GetValue(null) as Terraria.Localization.LocalizedText[];
                    if (arr != null)
                    {
                        var ctor = typeof(Terraria.Localization.LocalizedText).GetConstructor(
                            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
                            null, new[] { typeof(string), typeof(string) }, null);

                        if (arr.Length < requiredSize && ctor != null)
                        {
                            int oldLen = arr.Length;
                            var newArr = new Terraria.Localization.LocalizedText[requiredSize];
                            Array.Copy(arr, newArr, arr.Length);
                            var defaultText = (Terraria.Localization.LocalizedText)ctor.Invoke(
                                new object[] { "", "Chest" });
                            for (int i2 = oldLen; i2 < requiredSize; i2++)
                                newArr[i2] = defaultText;
                            chestTypeField.SetValue(null, newArr);
                            arr = newArr;
                            log?.Info($"Extended Lang.chestType: {oldLen} → {requiredSize}");
                        }

                        if (ctor != null && OUR_PLACE_STYLE < arr.Length)
                        {
                            var lt = (Terraria.Localization.LocalizedText)ctor.Invoke(
                                new object[] { "MysteriousChest", CHEST_NAME });
                            // Force the _value field to our string — otherwise the localization
                            // system returns the key "LegacyChestType.69" for unknown entries
                            var valueField = typeof(Terraria.Localization.LocalizedText).GetField("_value",
                                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                            if (valueField != null)
                                valueField.SetValue(lt, CHEST_NAME);
                            arr[OUR_PLACE_STYLE] = lt;
                            log?.Info($"Set Lang.chestType[{OUR_PLACE_STYLE}] = \"{CHEST_NAME}\"");
                        }
                    }
                }
            }
            catch (Exception ex) { log?.Warn($"Failed to extend Lang.chestType: {ex.Message}"); }

            // 2. Extend Chest.chestTypeToIcon (int[52] → int[70+])
            try
            {
                var chestTypeToIconField = typeof(Chest).GetField("chestTypeToIcon",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                if (chestTypeToIconField != null)
                {
                    var arr = chestTypeToIconField.GetValue(null) as int[];
                    if (arr != null && arr.Length < requiredSize)
                    {
                        var newArr = new int[requiredSize];
                        Array.Copy(arr, newArr, arr.Length);
                        newArr[OUR_PLACE_STYLE] = 327; // Crystal shard map icon
                        chestTypeToIconField.SetValue(null, newArr);
                        log?.Info($"Extended Chest.chestTypeToIcon: {arr.Length} → {requiredSize}");
                    }
                    else if (arr != null && OUR_PLACE_STYLE < arr.Length)
                    {
                        arr[OUR_PLACE_STYLE] = 327;
                    }
                }
            }
            catch (Exception ex) { log?.Warn($"Failed to extend Chest.chestTypeToIcon: {ex.Message}"); }
        }

        public static void Unload()
        {
            PaintingChestPatches.Unpatch();
            TileTextureExtender.Unload();
            UIRenderer.UnregisterPanelDraw("painting-chest-label");
            Enabled = false;
            _config = null;
        }

        public static int GetCurrentCapacity()
        {
            int level = _config?.PaintingChestLevel ?? 0;
            return PaintingChestProgression.GetCapacity(level);
        }

        private static void Draw()
        {
            if (!Enabled) return;

            // Check if one of our chests is currently open
            int myPlayer = Main.myPlayer;
            var player = Main.player[myPlayer];
            int chestIdx = player.chest;
            if (chestIdx < 0) return;

            var chest = Main.chest[chestIdx];
            if (chest == null) return;

            // Verify it's our tile type and style
            try
            {
                var tile = Main.tile[chest.x, chest.y];
                if (tile == null || !tile.active() || tile.type != TILE_TYPE) return;
                int style = tile.frameX / 36;
                if (style != OUR_PLACE_STYLE) return;
            }
            catch { return; }

            // Count non-empty slots
            int usedSlots = 0;
            for (int i = 0; i < chest.maxItems; i++)
            {
                if (chest.item[i] != null && chest.item[i].type > 0 && chest.item[i].stack > 0)
                    usedSlots++;
            }

            // Position: below the 4 visible chest rows
            const float chestScale = 0.755f;
            const int visibleRows = 4;
            int labelX = 73;
            int labelY = (int)(Main.instance.invBottom + visibleRows * 56 * chestScale) + 8;

            string text = $"Capacity  {usedSlots} / {chest.maxItems}";
            UIRenderer.DrawText(text, labelX, labelY, UIColors.TextDim);
        }

        public static void ResizeAllPaintingChests(int capacity)
        {
            int resized = 0;
            for (int i = 0; i < 8000; i++)
            {
                var chest = Main.chest[i];
                if (chest == null) continue;

                try
                {
                    // Native type/style identifies the chest; its name belongs to the player.
                    var tile = Main.tile[chest.x, chest.y];
                    if (tile == null || !tile.active() || tile.type != TILE_TYPE) continue;

                    int style = tile.frameX / 36;
                    if (style != OUR_PLACE_STYLE) continue;

                    if (chest.maxItems != capacity)
                    {
                        // Never shrink below highest used slot to prevent data loss
                        int effectiveCapacity = capacity;
                        if (capacity < chest.maxItems && chest.item != null)
                        {
                            int highestUsed = -1;
                            for (int s = chest.item.Length - 1; s >= 0; s--)
                            {
                                if (chest.item[s] != null && chest.item[s].type > 0 && chest.item[s].stack > 0)
                                { highestUsed = s; break; }
                            }
                            if (highestUsed >= capacity)
                            {
                                effectiveCapacity = highestUsed + 1;
                                _log?.Warn($"Chest {i} has items in slot {highestUsed} — keeping capacity at {effectiveCapacity} instead of shrinking to {capacity}");
                            }
                        }
                        chest.Resize(effectiveCapacity);
                        resized++;
                    }
                }
                catch (Exception ex) { _log?.Debug($"ResizeAllPaintingChests failed for chest {i}: {ex.Message}"); }
            }

            if (resized > 0)
                _log?.Info($"Resized {resized} mysterious chests to {capacity} slots");
        }
    }
}
