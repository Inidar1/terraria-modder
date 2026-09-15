using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Terraria;
using Terraria.ID;
using Terraria.UI;
using Microsoft.Xna.Framework;
using TerrariaModder.Core;
using TerrariaModder.Core.Debug;
using TerrariaModder.Core.Events;
using TerrariaModder.Core.Logging;

namespace QuickKeys
{
    public class Mod : IMod, IModLifecycle, IModStateProvider, IModActionProvider
    {
        public string Id => "quick-keys";
        public string Name => "Quick Keys";
        public string Version => "2.0.1";

        private ILogger _log;
        private ModContext _context;
        private bool _enabled;
        private bool _showMessages;
        private bool _enableExtendedHotbar;
        private QuickKeysConfig _config;

        // Ruler
        private static bool _rulerActive = false;

        // Item restoration state
        private static bool _autoRevertSelectedItem = false;
        private static int _originalSelectedItem = -1;
        private static int _swappedSlot = -1;
        private static bool _placingTorch = false;

        // One synthetic activation is released on subsequent native ItemCheck calls.
        private static bool _quickUseActive = false;
        private static Harmony _harmony;

        // Terraria item IDs for recall items (priority order)
        private static readonly int[] RecallItemIds = new int[] {
            ItemID.CellPhone,
            ItemID.Shellphone,
            ItemID.ShellphoneSpawn,
            ItemID.ShellphoneOcean,
            ItemID.ShellphoneHell,
            ItemID.MagicMirror,
            ItemID.IceMirror,
            ItemID.RecallPotion
        };

        // Extended hotbar: slots 11-20 (indices 10-19), registered as keybinds

        public void Initialize(ModContext context)
        {
            _log = context.Logger;
            _context = context;
            _config = context.GetConfig<QuickKeysConfig>();

            LoadConfig();

            if (Environment.GetEnvironmentVariable("TERRARIA_MODDER_DEDSERV") == "1")
            {
                _log.Info("QuickKeys: dedicated server — skipping client init");
                return;
            }

            // Always register keybinds so they appear in F6 menu, even if mod is disabled
            // The callback functions check _enabled before doing anything
            context.RegisterKeybind("auto-torch", "Auto Torch", "Place a torch near cursor", "OemTilde", OnAutoTorch);
            context.RegisterKeybind("auto-recall", "Auto Recall", "Use recall item (mirror/phone)", "Home", OnAutoRecall);
            context.RegisterKeybind("quick-stack", "Quick Stack", "Quick stack to nearby chests", "End", OnQuickStack);
            context.RegisterKeybind("ruler", "Ruler", "Toggle ruler distance overlay", "K", OnRulerToggle);

            // Extended hotbar keybinds (slots 11-20) - always register so they appear in F6,
            // but callbacks check _enableExtendedHotbar before acting
            context.RegisterKeybind("hotbar-11", "Hotbar Slot 11", "Quick-use item in slot 11", "NumPad1", () => OnHotbarSlot(10));
            context.RegisterKeybind("hotbar-12", "Hotbar Slot 12", "Quick-use item in slot 12", "NumPad2", () => OnHotbarSlot(11));
            context.RegisterKeybind("hotbar-13", "Hotbar Slot 13", "Quick-use item in slot 13", "NumPad3", () => OnHotbarSlot(12));
            context.RegisterKeybind("hotbar-14", "Hotbar Slot 14", "Quick-use item in slot 14", "NumPad4", () => OnHotbarSlot(13));
            context.RegisterKeybind("hotbar-15", "Hotbar Slot 15", "Quick-use item in slot 15", "NumPad5", () => OnHotbarSlot(14));
            context.RegisterKeybind("hotbar-16", "Hotbar Slot 16", "Quick-use item in slot 16", "NumPad6", () => OnHotbarSlot(15));
            context.RegisterKeybind("hotbar-17", "Hotbar Slot 17", "Quick-use item in slot 17", "NumPad7", () => OnHotbarSlot(16));
            context.RegisterKeybind("hotbar-18", "Hotbar Slot 18", "Quick-use item in slot 18", "NumPad8", () => OnHotbarSlot(17));
            context.RegisterKeybind("hotbar-19", "Hotbar Slot 19", "Quick-use item in slot 19", "NumPad9", () => OnHotbarSlot(18));
            context.RegisterKeybind("hotbar-20", "Hotbar Slot 20", "Quick-use item in slot 20", "NumPad0", () => OnHotbarSlot(19));

            if (!_enabled)
            {
                _log.Info("QuickKeys is disabled in config (keybinds registered but inactive)");
                return;
            }

            // Subscribe to post-update for item restoration
            FrameEvents.OnPostUpdate += OnPostUpdate;

            context.RegisterStateProvider(this);
            context.RegisterActionProvider(this);
            _log.Info("QuickKeys initialized - keybinds registered with F6 menu");
        }


        public Dictionary<string, object> GetModState()
        {
            return new Dictionary<string, object>
            {
                { "enabled", _enabled },
                { "rulerActive", _rulerActive },
                { "extendedHotbarEnabled", _enableExtendedHotbar },
                { "showMessages", _showMessages }
            };
        }

        public List<ModActionInfo> GetActions()
        {
            return new List<ModActionInfo>
            {
                new ModActionInfo("auto_torch", "Place a torch near cursor"),
                new ModActionInfo("auto_recall", "Use recall item (mirror/phone)"),
                new ModActionInfo("quick_stack", "Quick stack to nearby chests"),
                new ModActionInfo("toggle_ruler", "Toggle ruler distance overlay"),
            };
        }

        public ModActionResult ExecuteAction(string name, Dictionary<string, string> args)
        {
            switch (name)
            {
                case "auto_torch": OnAutoTorch(); EventLog.Emit("quick-keys", "auto_torch", "{\"action\":\"torch_placed\"}"); return ModActionResult.Ok("Auto torch triggered");
                case "auto_recall": OnAutoRecall(); EventLog.Emit("quick-keys", "auto_recall", "{\"action\":\"recall_used\"}"); return ModActionResult.Ok("Auto recall triggered");
                case "quick_stack": OnQuickStack(); EventLog.Emit("quick-keys", "quick_stack", "{\"action\":\"stacked\"}"); return ModActionResult.Ok("Quick stack triggered");
                case "toggle_ruler": OnRulerToggle(); EventLog.Emit("quick-keys", "toggle_ruler", $"{{\"active\":{(_rulerActive ? "true" : "false")}}}"); return ModActionResult.Ok(_rulerActive ? "Ruler ON" : "Ruler OFF");
                default: return null;
            }
        }

        private void LoadConfig()
        {
            if (_config == null) return;
            _enabled = _config.Enabled;
            _showMessages = _config.ShowMessages;
            _enableExtendedHotbar = _config.EnableExtendedHotbar;

            // Enable debug logging if configured
            if (_config.DebugLogging)
            {
                _log.MinLevel = TerrariaModder.Core.Logging.LogLevel.Debug;
            }
        }

        public void OnConfigChanged()
        {
            LoadConfig();
        }

        private void OnHotbarSlot(int slotIndex)
        {
            if (!_enabled || !_enableExtendedHotbar) return;

            Player player = Main.player[Main.myPlayer];
            if (player == null) return;

            Item[] inventory = player.inventory;
            if (inventory == null) return;

            if (slotIndex >= 0 && slotIndex < inventory.Length)
            {
                Item item = inventory[slotIndex];
                if (item.type != 0 && item.stack > 0)
                    QuickUseItemAt(player, inventory, slotIndex);
            }
        }

        private void ShowMessage(string message, byte r = 255, byte g = 255, byte b = 0)
        {
            if (!_showMessages) return;
            try
            {
                Main.NewText($"[QuickKeys] {message}", r, g, b);
            }
            catch { }
        }

        #region Ruler

        private void OnRulerToggle()
        {
            if (!_enabled) return;
            _rulerActive = !_rulerActive;

            // When toggling off, actively disable the ruler
            if (!_rulerActive)
            {
                try
                {
                    Player player = Main.player[Main.myPlayer];
                    if (player != null)
                    {
                        int[] accStatus = player.builderAccStatus;
                        if (accStatus != null && accStatus.Length > 0)
                            accStatus[0] = 1; // 1 = disabled
                    }
                }
                catch { }
            }

            _log.Info($"Ruler toggled: {_rulerActive}");
            ShowMessage(_rulerActive ? "Ruler ON" : "Ruler OFF", 173, 216, 230);
        }

        #endregion

        #region Auto Torch

        private void OnAutoTorch()
        {
            if (!_enabled) return;
            if (_autoRevertSelectedItem) return;
            if (_placingTorch) return;
            _placingTorch = true;

            try
            {
                Player player = Main.player[Main.myPlayer];
                if (player == null) return;

                Item[] inventory = player.inventory;
                if (inventory == null) return;

                // Get torch set
                bool[] torchSet = TileID.Sets.Torches;
                if (torchSet == null) return;

                // Find a torch in inventory (must have stack > 0)
                int torchSlot = -1;
                Item torchItem = null;
                int torchTileType = -1;
                int torchPlaceStyle = 0;

                for (int i = 0; i < inventory.Length; i++)
                {
                    Item item = inventory[i];
                    if (item.stack <= 0) continue; // Skip empty or depleted slots

                    int createTile = item.createTile;
                    if (createTile >= 0 && createTile < torchSet.Length && torchSet[createTile])
                    {
                        torchSlot = i;
                        torchItem = item;
                        torchTileType = createTile;
                        torchPlaceStyle = item.placeStyle;
                        break;
                    }
                }

                if (torchSlot == -1 || torchItem == null)
                {
                    ShowMessage("No torches in inventory!", 255, 255, 0);
                    return;
                }

                // Select the torch item by swapping it to the current hotbar slot
                int originalSelected = player.selectedItem;
                bool needRestore = (originalSelected != torchSlot);

                if (needRestore)
                {
                    SwapInventorySlots(inventory, originalSelected, torchSlot);
                    _autoRevertSelectedItem = true;
                    _originalSelectedItem = originalSelected;
                    _swappedSlot = torchSlot;
                }

                // Get player position
                Vector2 pos = player.position;
                int width = player.width;
                int height = player.height;

                // Set initial tile target to player center
                int centerX = (int)((pos.X + width / 2f) / 16f);
                int centerY = (int)((pos.Y + height / 2f) / 16f);
                Player.tileTargetX = centerX;
                Player.tileTargetY = centerY;

                // Get tile ranges
                int tileRangeX = Math.Min(Player.tileRangeX, 50);
                int tileRangeY = Math.Min(Player.tileRangeY, 50);
                int blockRange = player.blockRange;

                // Build list of positions sorted by distance from mouse
                Vector2 mouseWorld = Main.MouseWorld;
                float mouseX = mouseWorld.X;
                float mouseY = mouseWorld.Y;
                // If mouse is 0,0, use player center
                if (mouseX == 0 && mouseY == 0)
                {
                    mouseX = pos.X + width / 2f;
                    mouseY = pos.Y + height / 2f;
                }

                var targets = new List<Tuple<float, int, int>>();
                int minX = -tileRangeX - blockRange + (int)(pos.X / 16f) + 1;
                int maxX = tileRangeX + blockRange - 1 + (int)((pos.X + width) / 16f);
                int minY = -tileRangeY - blockRange + (int)(pos.Y / 16f) + 1;
                int maxY = tileRangeY + blockRange - 2 + (int)((pos.Y + height) / 16f);

                for (int j = minX; j <= maxX; j++)
                {
                    for (int k = minY; k <= maxY; k++)
                    {
                        float dist = (float)Math.Sqrt((mouseX - j * 16f) * (mouseX - j * 16f) + (mouseY - k * 16f) * (mouseY - k * 16f));
                        targets.Add(new Tuple<float, int, int>(dist, j, k));
                    }
                }
                targets.Sort((a, b) => a.Item1.CompareTo(b.Item1));

                // Try each position using direct tile placement
                bool placeSuccess = false;

                foreach (var target in targets)
                {
                    int tileX = target.Item2;
                    int tileY = target.Item3;

                    // Get tile state before
                    Tile tile = Main.tile[tileX, tileY];
                    bool hadTileBefore = tile != null && tile.active();

                    Player.tileTargetX = tileX;
                    Player.tileTargetY = tileY;

                    // Use direct WorldGen.PlaceTile with correct tile type and style
                    if (!hadTileBefore)
                    {
                        try
                        {
                            // Apply Torch God's Favor biome conversion for regular torches (tile 4, style 0)
                            int placeTileType = torchTileType;
                            int placeStyle = torchPlaceStyle;
                            if (placeTileType == 4 && placeStyle == 0 && player.UsingBiomeTorches)
                            {
                                player.BiomeTorchPlaceStyle(ref placeTileType, ref placeStyle);
                            }

                            bool placed = WorldGen.PlaceTile(tileX, tileY, placeTileType, false, false, Main.myPlayer, placeStyle);
                            if (placed)
                            {
                                // Consume one torch from inventory
                                if (torchItem.stack > 0)
                                {
                                    torchItem.stack--;
                                    if (torchItem.stack <= 0)
                                        torchItem.TurnToAir();
                                }

                                // Replicate to other players in multiplayer.
                                // WorldGen.PlaceTile does NOT send any packets — we must do it manually.
                                // Packet 17 action=1 = place tile. Server broadcasts to all clients except sender.
                                // Packet 5 syncs the consumed torch inventory slot.
                                if (Main.netMode != 0)
                                {
                                    NetMessage.TrySendData(17, -1, -1, null, 1, tileX, tileY, placeTileType, placeStyle);
                                    // Sync the slot with the consumed torch (originalSelected after swap, or torchSlot if no swap)
                                    NetMessage.TrySendData(5, -1, -1, null, Main.myPlayer, originalSelected);
                                    if (needRestore)
                                        NetMessage.TrySendData(5, -1, -1, null, Main.myPlayer, torchSlot);
                                }

                                placeSuccess = true;
                                break;
                            }
                        }
                        catch
                        {
                            // Continue trying other positions
                        }
                    }
                }

                if (!placeSuccess)
                    ShowMessage("No valid spot for torch", 255, 255, 0);
            }
            catch (Exception ex)
            {
                _log.Error($"Auto-torch error: {ex.Message}");
                _log.Error($"Stack: {ex.StackTrace}");
            }
            finally
            {
                _placingTorch = false;
            }
        }

        #endregion

        #region Auto Recall

        private void OnAutoRecall()
        {
            if (!_enabled) return;
            try
            {
                Player player = Main.player[Main.myPlayer];
                if (player == null) return;

                Item[] inventory = player.inventory;
                if (inventory == null) return;

                int foundSlot = -1;
                string itemName = "";
                foreach (int itemId in RecallItemIds)
                {
                    for (int i = 0; i < Math.Min(58, inventory.Length); i++)
                    {
                        Item item = inventory[i];
                        if (item.type == itemId && item.stack > 0)
                        {
                            foundSlot = i;
                            itemName = item.Name;
                            break;
                        }
                    }
                    if (foundSlot >= 0) break;
                }

                if (foundSlot == -1)
                {
                    ShowMessage("No recall item found!", 255, 255, 0);
                    return;
                }

                if (!QuickUseItemAt(player, inventory, foundSlot)) return;

                ShowMessage($"Using {itemName}", 173, 216, 230);
            }
            catch (Exception ex)
            {
                _log.Error($"Auto-recall error: {ex.Message}");
            }
        }

        #endregion

        #region Quick Stack

        private void OnQuickStack()
        {
            if (!_enabled) return;
            Player player = Main.player[Main.myPlayer];
            if (player == null) return;

            try
            {
                if (player.chest >= 0)
                {
                    ChestUI.QuickStack();
                    ShowMessage("Quick stacked to chest", 144, 238, 144);
                }
                else
                {
                    player.QuickStackAllChests();
                    ShowMessage("Quick stacked to nearby chests", 144, 238, 144);
                }
            }
            catch (Exception ex)
            {
                _log.Error($"Quick stack error: {ex.Message}");
            }
        }

        #endregion

        #region Item Quick Use & Restoration

        private bool QuickUseItemAt(Player player, Item[] inventory, int slot)
        {
            if (_autoRevertSelectedItem || player == null || inventory == null) return false;

            int selectedItem = player.selectedItem;
            if (slot < 0 || slot >= inventory.Length || selectedItem < 0 || selectedItem >= inventory.Length) return false;
            if (inventory[slot].IsAir) return false;

            // Check if player can switch
            int itemAnimation = player.itemAnimation;
            bool itemTimeIsZero = player.ItemTimeIsZero;
            int reuseDelay = player.reuseDelay;

            if (itemAnimation == 0 && itemTimeIsZero && reuseDelay == 0)
            {
                _originalSelectedItem = selectedItem;
                _swappedSlot = slot;
                _autoRevertSelectedItem = true;
                // Swap the target item into the currently selected hotbar slot.
                // We deliberately avoid player.selectedItemState.Select() — that method sets
                // an internal 'buffered' field on the struct which Player.Update() applies one
                // frame later, causing selectedItem to jump to the wrong slot mid-animation
                // and breaking the swap-back revert logic.
                SwapInventorySlots(inventory, selectedItem, slot);

                // Start the item use immediately in PreUpdate, before Player.Update() runs.
                // This is required because Player.Update() resets releaseUseItem=false when the
                // player isn't holding LMB, which prevents ItemCheck from starting the animation.
                // By calling ItemCheck() here (while releaseUseItem is still true from the
                // previous idle frame), we bypass that gate and start the animation correctly.
                player.releaseUseItem = true;
                player.controlUseItem = true;
                player.ItemCheck();

                // A hotkey is one activation. Native timers continue after release;
                // holding synthetic use here repeatedly restarts auto-reuse items.
                _quickUseActive = true;
                return player.itemAnimation > 0;
            }
            return false;
        }

        // Harmony prefix on Player.ItemCheck() — runs right before each call from Player.Update().
        // Release the synthetic activation while its item finishes. Mirrors/phones use
        // native animation timers, not channelled input. Never synthesize an endless hold.
        private static void ItemCheck_Prefix(Player __instance)
        {
            if (_quickUseActive && _autoRevertSelectedItem && __instance.whoAmI == Main.myPlayer)
                __instance.controlUseItem = false;
        }

        private void SwapInventorySlots(Item[] inventory, int slotA, int slotB)
        {
            if (inventory == null) return;
            Item temp = inventory[slotA];
            inventory[slotA] = inventory[slotB];
            inventory[slotB] = temp;
        }

        private void OnPostUpdate()
        {
            // Apply ruler flags every frame (vanilla ResetEffects clears rulerLine)
            if (_rulerActive)
            {
                try
                {
                    Player player = Main.player[Main.myPlayer];
                    if (player != null)
                    {
                        player.rulerLine = true;
                        int[] accStatus = player.builderAccStatus;
                        if (accStatus != null && accStatus.Length > 0)
                            accStatus[0] = 0;
                    }
                }
                catch { }
            }

            if (!_autoRevertSelectedItem) return;

            try
            {
                Player player = Main.player[Main.myPlayer];
                if (player == null) return;

                int itemAnimation = player.itemAnimation;
                bool itemTimeIsZero = player.ItemTimeIsZero;
                int reuseDelay = player.reuseDelay;

                if (itemAnimation == 0 && reuseDelay == 0)
                    RestorePendingQuickUse(player, syncInventory: Main.netMode != 0);
            }
            catch (Exception ex)
            {
                _log.Error($"Quick-use item restoration failed: {ex}");
            }
        }

        private void RestorePendingQuickUse(Player player, bool syncInventory)
        {
            if (!_autoRevertSelectedItem) return;
            try
            {
                if (player != null && player.inventory != null &&
                    _originalSelectedItem >= 0 && _originalSelectedItem < player.inventory.Length &&
                    _swappedSlot >= 0 && _swappedSlot < player.inventory.Length)
                {
                    SwapInventorySlots(player.inventory, _originalSelectedItem, _swappedSlot);
                    if (syncInventory)
                    {
                        NetMessage.TrySendData(5, -1, -1, null, Main.myPlayer, _originalSelectedItem);
                        NetMessage.TrySendData(5, -1, -1, null, Main.myPlayer, _swappedSlot);
                    }
                }
            }
            catch (Exception ex)
            {
                _log?.Error($"Quick-use item restoration failed: {ex}");
            }
            finally
            {
                _quickUseActive = false;
                _autoRevertSelectedItem = false;
                _originalSelectedItem = -1;
                _swappedSlot = -1;
            }
        }

        #endregion

        public void OnContentReady(ModContext context)
        {
            // Patch on the game-thread lifecycle after graphics/content initialization.
            _harmony = new Harmony("com.terrariamodder.quickkeys");
            try
            {
                var itemCheckMethod = typeof(Player).GetMethod("ItemCheck",
                    BindingFlags.Public | BindingFlags.Instance,
                    null, Type.EmptyTypes, null);
                if (itemCheckMethod != null)
                {
                    _harmony.Patch(itemCheckMethod,
                        prefix: new HarmonyMethod(typeof(Mod), nameof(ItemCheck_Prefix)));
                    _log.Debug("Patched Player.ItemCheck for single-activation quick use");
                }
                else
                {
                    _log.Warn("Player.ItemCheck not found — quick-use input release is unavailable");
                }
            }
            catch (Exception ex)
            {
                _log.Warn($"Failed to patch Player.ItemCheck: {ex.Message}");
            }

        }

        public void OnWorldLoad()
        {
        }

        public void OnWorldUnload()
        {
            Player player = null;
            try { player = Main.player[Main.myPlayer]; } catch { }
            RestorePendingQuickUse(player, syncInventory: false);
            _rulerActive = false;
        }

        public void Unload()
        {
            Player player = null;
            try { player = Main.player[Main.myPlayer]; } catch { }
            RestorePendingQuickUse(player, syncInventory: false);
            _harmony?.UnpatchAll("com.terrariamodder.quickkeys");
            FrameEvents.OnPostUpdate -= OnPostUpdate;

            // Reset static state for hot-reload support
            _placingTorch = false;
            _quickUseActive = false;
            _rulerActive = false;
            _autoRevertSelectedItem = false;
            _originalSelectedItem = -1;
            _swappedSlot = -1;

            _log.Info("QuickKeys unloaded");
        }
    }
}
