using System;
using System.Collections.Generic;
using System.Linq;
using Terraria;
using Terraria.DataStructures;
using Terraria.ID;
using TerrariaModder.Core;
using TerrariaModder.Core.Assets;
using TerrariaModder.Core.Debug;
using TerrariaModder.Core.Events;
using TerrariaModder.Core.Input;
using TerrariaModder.Core.Logging;
using TerrariaModder.Core.UI;
using TerrariaModder.Core.UI.Widgets;

namespace ItemSpawner
{
    public class Mod : IMod, IModLifecycle, IModStateProvider, IModActionProvider
    {
        public string Id => "item-spawner";
        public string Name => "Item Spawner";
        public string Version => "2.0.0";

        private ILogger _log;
        private ModContext _context;
        private bool _enabled;
        private ItemSpawnerConfig _config;

        private List<ItemEntry> _allItems = new List<ItemEntry>();
        private List<ItemEntry> _filteredItems = new List<ItemEntry>();
        private List<string> _registeredItemsInCatalog = new List<string>();

        // UI - grid layout
        private const int GridSlotSize = 44;
        private const int SlotOuter = GridSlotSize - 2;
        private const int IconPad = 4;
        private const int IconSize = SlotOuter - IconPad * 2;

        private DraggablePanel _panel = new DraggablePanel("item-spawner", "Item Spawner", 520, 520);
        private TextInput _searchInput = new TextInput("Type to search...", 200);
        private ScrollView _scroll = new ScrollView();

        // Pending spawn queue: server validates, client executes locally on "ok".
        // FIFO assumption: the server responds to spawn requests in the same order they
        // were sent. The response payload is just "ok"/"denied" with no item type, so we
        // cannot match responses to specific requests. If the server ever reorders responses,
        // the wrong pending item would be dequeued. This is acceptable because:
        //   1. ServerCommandRequest/Response is ordered per-client in Terraria's net layer.
        //   2. The queue is cleared on world unload (OnWorldUnload) to prevent stale entries.
        // Each entry: (itemId, stack, toCursor)
        private readonly Queue<(int itemId, int stack, bool toCursor)> _pendingSpawns
            = new Queue<(int, int, bool)>();

        public void Initialize(ModContext context)
        {
            _log = context.Logger;
            _context = context;
            _config = context.GetConfig<ItemSpawnerConfig>();

            // Item spawner is client-only UI — no UI on dedicated server
            // Use env var instead of Main.dedServ — direct Main access throws TypeInitializationException on TerrariaServer.exe
            if (Environment.GetEnvironmentVariable("TERRARIA_MODDER_DEDSERV") == "1")
            {
                _log.Info("ItemSpawner: skipping UI init (dedicated server)");
                return;
            }

            LoadConfig();

            if (!_enabled)
            {
                _log.Info("ItemSpawner is disabled in config");
                return;
            }

            _searchInput.KeyBlockId = "item-spawner";
            _panel.ClipContent = false; // Virtual scrolling via _scroll.IsVisible handles culling; BeginClip causes transform issues
            _panel.OnClose = OnPanelClose;
            _panel.RegisterDrawCallback(OnDraw);

            context.RegisterKeybind("toggle", "Toggle Spawner", "Open/close spawner UI", "Insert", OnToggleUI);
            FrameEvents.OnPreUpdate += OnUpdate;
            TerrariaModder.Core.Net.NetSync.OnServerCommandResponse += OnServerCommandResponse;

            context.RegisterStateProvider(this);
            context.RegisterActionProvider(this);
            _log.Info("ItemSpawner initialized - Press Insert to open");
        }

        public Dictionary<string, object> GetModState()
        {
            return new Dictionary<string, object>
            {
                { "enabled", _enabled },
                { "panelOpen", _panel.IsOpen },
                { "searchText", _searchInput.Text ?? "" },
                { "totalItems", _allItems.Count },
                { "filteredItems", _filteredItems.Count },
                { "registeredItemsInCatalog", _registeredItemsInCatalog.ToArray() }
            };
        }

        public List<ModActionInfo> GetActions()
        {
            return new List<ModActionInfo>
            {
                new ModActionInfo("open_panel", "Open the Item Spawner panel"),
                new ModActionInfo("close_panel", "Close the Item Spawner panel"),
            };
        }

        public ModActionResult ExecuteAction(string name, Dictionary<string, string> args)
        {
            switch (name)
            {
                case "open_panel":
                    if (!_panel.IsOpen) OnToggleUI();
                    EventLog.Emit("item-spawner", "open_panel", "{\"open\":true}");
                    return ModActionResult.Ok("Panel opened");
                case "close_panel":
                    if (_panel.IsOpen) _panel.Close();
                    EventLog.Emit("item-spawner", "close_panel", "{\"open\":false}");
                    return ModActionResult.Ok("Panel closed");
                default:
                    return null;
            }
        }

        private void LoadConfig()
        {
            if (_config == null) return;
            _enabled = _config.Enabled;
        }

        private void BuildItemCatalog()
        {
            try
            {
                int itemCount = ItemID.Count;
                int errorCount = 0;

                var seenTypes = new HashSet<int>();
                for (int i = 1; i < itemCount; i++)
                    TryAddCatalogItem(i, seenTypes, ref errorCount);

                var registeredItemsInCatalog = new List<string>();
                foreach (string fullId in ItemRegistry.AllIds)
                {
                    int customType = ItemRegistry.ResolveItemType(fullId);
                    if (customType > 0 && TryAddCatalogItem(customType, seenTypes, ref errorCount))
                        registeredItemsInCatalog.Add(fullId);
                }

                _registeredItemsInCatalog = registeredItemsInCatalog;
                _allItems = _allItems.OrderBy(i => i.Name).ToList();
                _filteredItems = new List<ItemEntry>(_allItems);
                _log.Info($"Item catalog built with {_allItems.Count} items");
            }
            catch (Exception ex)
            {
                _log.Error($"Failed to build item catalog: {ex.Message}");
            }
        }

        private bool TryAddCatalogItem(int itemType, HashSet<int> seenTypes, ref int errorCount)
        {
            if (!seenTypes.Add(itemType)) return false;
            try
            {
                var item = new Item();
                item.SetDefaults(itemType);
                string name = item.Name ?? "";
                if (!string.IsNullOrWhiteSpace(name))
                    _allItems.Add(new ItemEntry { Id = itemType, Name = name, MaxStack = item.maxStack });
                return !string.IsNullOrWhiteSpace(name);
            }
            catch
            {
                errorCount++;
                return false;
            }
        }

        #region UI

        private void OnToggleUI()
        {
            if (!_panel.IsOpen)
            {
                _searchInput.Clear();
                _scroll.ResetScroll();
                if (_allItems.Count == 0) BuildItemCatalog();
                FilterItems();
                _panel.Open();
                UIRenderer.OpenInventory();
            }
            else
            {
                _panel.Close();
            }
        }

        private bool HasSpawnPermission()
        {
            if (Main.netMode == 0) return true;
            return TerrariaModder.Core.Net.NetSync.LocalPlayerIsAdmin
                || TerrariaModder.Core.Net.NetSync.LocalPlayerHasModAccess("item-spawner");
        }

        private void OnPanelClose()
        {
            _searchInput.Unfocus();
            UIRenderer.CloseInventory();
        }

        private void FilterItems()
        {
            string search = _searchInput.Text;
            if (string.IsNullOrEmpty(search))
            {
                _filteredItems = new List<ItemEntry>(_allItems);
            }
            else
            {
                _filteredItems.Clear();
                string lower = search.ToLower();
                bool isIdSearch = int.TryParse(search, out int searchId);
                foreach (var item in _allItems)
                {
                    if ((isIdSearch && item.Id == searchId) || item.Name.ToLower().Contains(lower))
                        _filteredItems.Add(item);
                }
            }
            _scroll.ResetScroll();
            _panel.Title = $"Item Spawner ({_filteredItems.Count}/{_allItems.Count})";
        }

        private void OnUpdate()
        {
            if (!_panel.IsOpen) return;
            _searchInput.Update();
        }

        private void OnDraw()
        {
            if (!_panel.BeginDraw()) return;
            try
            {
                var s = new StackLayout(_panel.ContentX, _panel.ContentY, _panel.ContentWidth);

                // Search bar
                int searchY = s.Advance(30);
                _searchInput.Draw(s.X, searchY, s.Width, 28);
                if (_searchInput.HasChanged) FilterItems();

                // Help text
                int helpY = s.Advance(34);
                UIRenderer.DrawRect(s.X, helpY, s.Width, 30, UIColors.SectionBg);
                if (Main.netMode != 0 && !HasSpawnPermission())
                {
                    UIRenderer.DrawTextSmall("[Admin] Spawn requires Admin or ItemSpawner grant", s.X + 4, helpY + 2, UIColors.Warning);
                    UIRenderer.DrawTextSmall("Contact the server admin to get access", s.X + 4, helpY + 16, UIColors.TextDim);
                }
                else
                {
                    UIRenderer.DrawTextSmall("L-Click: +1 to cursor    R-Click: Full stack to cursor", s.X + 4, helpY + 2, UIColors.Success);
                    UIRenderer.DrawTextSmall("Shift+L: +1 to inventory  Shift+R: Full stack to inventory", s.X + 4, helpY + 16, UIColors.Info);
                }

                // Item grid
                int gridX = s.X;
                int gridY = s.CurrentY;
                int gridWidth = s.Width;
                int gridHeight = _panel.ContentHeight - s.TotalHeight;

                int gridColumns = Math.Max(1, _scroll.ContentWidth / GridSlotSize);
                int totalRows = (_filteredItems.Count + gridColumns - 1) / gridColumns;
                int totalContentHeight = totalRows * GridSlotSize;

                _scroll.Begin(gridX, gridY, gridWidth, gridHeight, totalContentHeight);

                bool shiftHeld = WidgetInput.IsShiftHeld;
                int contentWidth = _scroll.ContentWidth;
                // Recalculate columns after Begin (ContentWidth may shrink for scrollbar)
                gridColumns = Math.Max(1, contentWidth / GridSlotSize);

                int visibleRows = gridHeight / GridSlotSize;
                int startRow = _scroll.ScrollOffset / GridSlotSize;
                int startIndex = startRow * gridColumns;

                for (int i = 0; i < (visibleRows + 1) * gridColumns && startIndex + i < _filteredItems.Count; i++)
                {
                    int itemIdx = startIndex + i;
                    int row = i / gridColumns;
                    int col = i % gridColumns;

                    int itemContentY = (startRow + row) * GridSlotSize;
                    if (!_scroll.IsVisible(itemContentY, GridSlotSize)) continue;

                    int slotX = gridX + col * GridSlotSize;
                    int slotY = _scroll.ContentY + itemContentY;

                    var item = _filteredItems[itemIdx];
                    bool isInScrollBounds = WidgetInput.IsMouseOver(gridX, gridY, gridWidth, gridHeight);
                    bool isHover = isInScrollBounds && WidgetInput.IsMouseOver(slotX, slotY, SlotOuter, SlotOuter);

                    // Slot background
                    UIRenderer.DrawRect(slotX, slotY, SlotOuter, SlotOuter,
                        isHover ? UIColors.InputFocusBg : UIColors.ItemBg);

                    // Hover border
                    if (isHover)
                        UIRenderer.DrawRectOutline(slotX, slotY, SlotOuter, SlotOuter, UIColors.Accent, 1);

                    // Item icon
                    UIRenderer.DrawItem(item.Id, slotX + IconPad, slotY + IconPad, IconSize, IconSize);

                    // Hover tooltip + click handling
                    if (isHover)
                    {
                        ItemTooltip.Set(item.Id);
                        bool canSpawn = HasSpawnPermission();

                        if (WidgetInput.MouseLeftClick)
                        {
                            _searchInput.Unfocus();
                            if (canSpawn)
                            {
                                if (shiftHeld)
                                    SpawnToInventory(item.Id, 1);
                                else
                                    SpawnToCursor(item.Id, 1);
                            }
                            WidgetInput.ConsumeClick();
                        }
                        else if (WidgetInput.MouseRightClick)
                        {
                            _searchInput.Unfocus();
                            if (canSpawn)
                            {
                                if (shiftHeld)
                                    SpawnToInventory(item.Id, item.MaxStack);
                                else
                                    SpawnToCursor(item.Id, item.MaxStack);
                            }
                            WidgetInput.ConsumeRightClick();
                        }
                    }
                }

                _scroll.End();

                if (_filteredItems.Count == 0)
                {
                    UIRenderer.DrawText("No items found", gridX + 5, gridY + 10, UIColors.Warning);
                }
            }
            catch (Exception ex)
            {
                _log.Error($"Draw error: {ex.Message}");
            }
            finally
            {
                _panel.EndDraw();
            }
        }

        #endregion

        #region Spawning

        private void SpawnToCursor(int itemId, int stack)
        {
            if (Main.netMode != 0)
            {
                SpawnViaServer(itemId, stack, toCursor: true);
                return;
            }

            try
            {
                Item mouseItem = Main.mouseItem;
                if (mouseItem == null)
                {
                    SpawnToInventory(itemId, stack);
                    return;
                }

                if (mouseItem.type == 0)
                {
                    var newItem = new Item();
                    newItem.SetDefaults(itemId);
                    newItem.stack = stack;
                    Main.mouseItem = newItem;
                }
                else if (mouseItem.type == itemId)
                {
                    int canAdd = mouseItem.maxStack - mouseItem.stack;
                    if (canAdd > 0)
                    {
                        int toAdd = Math.Min(stack, canAdd);
                        mouseItem.stack += toAdd;
                    }
                }
                else
                {
                    SpawnToInventory(itemId, stack);
                }
            }
            catch (Exception ex)
            {
                _log.Error($"Failed to spawn to cursor: {ex.Message}");
            }
        }

        private void SpawnToInventory(int itemId, int stack)
        {
            if (Main.netMode != 0)
            {
                SpawnViaServer(itemId, stack, toCursor: false);
                return;
            }

            try
            {
                Player player = Main.player[Main.myPlayer];
                var source = new EntitySource_Parent(player);
                player.QuickSpawnItem(source, itemId, stack);
            }
            catch (Exception ex)
            {
                _log.Error($"Failed to spawn item: {ex.Message}");
            }
        }

        private void SpawnViaServer(int itemId, int stack, bool toCursor)
        {
            _log.Info($"[ItemSpawner] SpawnViaServer: item={itemId} stack={stack} toCursor={toCursor} netMode={Main.netMode} H&P={Netplay.IsHostAndPlay}");
            if (!HasSpawnPermission())
            {
                _log.Info("[ItemSpawner] SpawnViaServer: permission denied");
                try { Main.NewText("[ItemSpawner] Requires Admin or ItemSpawner grant.", 255, 80, 80); } catch { }
                return;
            }

            // Enqueue pending spawn — executed locally when server responds "ok"
            _pendingSpawns.Enqueue((itemId, stack, toCursor));
            _log.Info($"[ItemSpawner] SpawnViaServer: enqueued, sending ServerCommandRequest");
            // Payload only needs auth info; actual item add is client-side
            TerrariaModder.Core.Net.NetSync.SendServerCommandRequest("spawn", $"{itemId}:{stack}:0");
        }

        private void OnServerCommandResponse(string type, string result)
        {
            if (type != "spawn") return;
            if (_pendingSpawns.Count == 0) return;

            var (itemId, stack, toCursor) = _pendingSpawns.Dequeue();

            if (result != "ok" && result != "OK")
            {
                try { Main.NewText($"[ItemSpawner] Spawn denied: {result}", 255, 80, 80); } catch { }
                return;
            }

            try
            {
                if (toCursor)
                    AddToCursor(itemId, stack);
                else
                    AddToInventory(itemId, stack);
            }
            catch (Exception ex)
            {
                _log.Error($"[ItemSpawner] OnServerCommandResponse local add failed: {ex.Message}");
            }
        }

        private void AddToCursor(int itemId, int stack)
        {
            Item mouseItem = Main.mouseItem;
            if (mouseItem == null || mouseItem.type == 0)
            {
                var newItem = new Item();
                newItem.SetDefaults(itemId);
                newItem.stack = stack;
                Main.mouseItem = newItem;
            }
            else if (mouseItem.type == itemId)
            {
                int canAdd = mouseItem.maxStack - mouseItem.stack;
                if (canAdd > 0) mouseItem.stack += Math.Min(stack, canAdd);
                else AddToInventory(itemId, stack);
            }
            else
            {
                AddToInventory(itemId, stack);
            }
        }

        private void AddToInventory(int itemId, int stack)
        {
            Player player = Main.player[Main.myPlayer];
            var source = new EntitySource_Parent(player);
            player.QuickSpawnItem(source, itemId, stack);
        }

        #endregion

        public void OnContentReady(ModContext context) { }

        public void OnWorldLoad()
        {
            if (_allItems.Count == 0)
                BuildItemCatalog();
        }

        public void OnWorldUnload()
        {
            _panel.Close();
            _pendingSpawns?.Clear();
        }

        public void Unload()
        {
            FrameEvents.OnPreUpdate -= OnUpdate;
            TerrariaModder.Core.Net.NetSync.OnServerCommandResponse -= OnServerCommandResponse;
            _panel.UnregisterDrawCallback();
            _searchInput.Unfocus();

            _allItems.Clear();
            _filteredItems.Clear();

            _log.Info("ItemSpawner unloaded");
        }

        private class ItemEntry
        {
            public int Id;
            public string Name;
            public int MaxStack;
        }
    }
}
