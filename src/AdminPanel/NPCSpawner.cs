using System;
using System.Collections.Generic;
using System.Reflection;
using TerrariaModder.Core.Logging;
using TerrariaModder.Core.UI;
using TerrariaModder.Core.UI.Widgets;

namespace AdminPanel
{
    public static class NPCSpawner
    {
        private struct CatalogEntry
        {
            public int Id;
            public string Name;
        }

        private enum Category { Boss, Town, Critter, Enemy }

        private static ILogger _log;
        private static bool _catalogBuilt;

        // Categorized lists
        private static List<CatalogEntry> _bosses = new List<CatalogEntry>();
        private static List<CatalogEntry> _townNPCs = new List<CatalogEntry>();
        private static List<CatalogEntry> _critters = new List<CatalogEntry>();
        private static List<CatalogEntry> _enemies = new List<CatalogEntry>();

        // Filtered lists for display
        private static List<CatalogEntry> _filteredBosses = new List<CatalogEntry>();
        private static List<CatalogEntry> _filteredNPCs = new List<CatalogEntry>(); // Combined town+critter+enemy

        // Favourites
        private static HashSet<int> _favouriteBossIds = new HashSet<int>();
        private static HashSet<int> _favouriteNPCIds = new HashSet<int>();

        // UI state
        private static TextInput _bossSearch = new TextInput("Search bosses...", 100);
        private static TextInput _npcSearch = new TextInput("Search NPCs...", 100);
        private static ScrollView _bossScroll = new ScrollView();
        private static ScrollView _npcScroll = new ScrollView();

        private static int _selectedId = -1;
        private static string _selectedName = "";
        private static string _lastResult = "";
        private static bool _rightClickSpawn;

        private const int RowHeight = 24;
        private const int MaxVisibleRows = 10;
        private const int SpawnOffsetY = 80; // ~5 tiles above player

        // Section header sentinel (Id = -1, -2, -3)
        private const int SectionTown = -1;
        private const int SectionCritter = -2;
        private const int SectionEnemy = -3;

        #region Reflection Cache

        private static Type _npcType;
        private static Type _mainType;
        private static Type _langType;
        private static Type _contentSamplesType;
        private static Type _entitySourceType;
        private static Type _vector2Type;

        // NPC fields
        private static FieldInfo _npcBossField;
        private static FieldInfo _npcTownNPCField;
        private static FieldInfo _npcCatchItemField;
        private static FieldInfo _npcTypeField;
        private static FieldInfo _npcTimeLeftField;
        private static FieldInfo _npcActiveField;

        // Main fields
        private static FieldInfo _mainNpcField;
        private static FieldInfo _mainMyPlayerField;
        private static FieldInfo _mainPlayerArrayField;
        private static FieldInfo _mainMouseXField;
        private static FieldInfo _mainMouseYField;
        private static FieldInfo _mainScreenPositionField;
        private static FieldInfo _mainGameMenuField;

        // Vector2 fields
        private static FieldInfo _vector2XField;
        private static FieldInfo _vector2YField;

        // Player fields
        private static PropertyInfo _playerCenterProp;

        // Methods
        private static MethodInfo _langGetNPCNameValue;
        private static MethodInfo _npcNewNPC;

        // ContentSamples
        private static FieldInfo _npcsByNetIdField;

        // NPCID
        private static FieldInfo _npcidCountField;

        // NPCID.Sets
        private static FieldInfo _shouldBeCountedAsBossField;
        private static FieldInfo _townCritterField;

        #endregion

        public static void Init(ILogger log, Type mainType, Type playerType, Type vector2Type,
            FieldInfo vector2XField, FieldInfo vector2YField,
            FieldInfo myPlayerField, FieldInfo playerArrayField,
            PropertyInfo playerCenterProp)
        {
            _log = log;
            _mainType = mainType;
            _vector2Type = vector2Type;
            _vector2XField = vector2XField;
            _vector2YField = vector2YField;
            _mainMyPlayerField = myPlayerField;
            _mainPlayerArrayField = playerArrayField;
            _playerCenterProp = playerCenterProp;

            try
            {
                var asm = mainType.Assembly;
                _npcType = asm.GetType("Terraria.NPC");
                _langType = asm.GetType("Terraria.Lang");
                _contentSamplesType = asm.GetType("Terraria.ID.ContentSamples");
                var npcidType = asm.GetType("Terraria.ID.NPCID");
                var npcidSetsType = npcidType?.GetNestedType("Sets", BindingFlags.Public);

                // EntitySource
                _entitySourceType = asm.GetType("Terraria.DataStructures.EntitySource_SpawnNPC");

                // NPC fields
                if (_npcType != null)
                {
                    _npcBossField = _npcType.GetField("boss", BindingFlags.Public | BindingFlags.Instance);
                    _npcTownNPCField = _npcType.GetField("townNPC", BindingFlags.Public | BindingFlags.Instance);
                    _npcCatchItemField = _npcType.GetField("catchItem", BindingFlags.Public | BindingFlags.Instance);
                    _npcTypeField = _npcType.GetField("type", BindingFlags.Public | BindingFlags.Instance);
                    _npcTimeLeftField = _npcType.GetField("timeLeft", BindingFlags.Public | BindingFlags.Instance);
                    _npcActiveField = _npcType.GetField("active", BindingFlags.Public | BindingFlags.Instance);
                    _npcNewNPC = _npcType.GetMethod("NewNPC", BindingFlags.Public | BindingFlags.Static);
                }

                // Main fields
                _mainNpcField = mainType.GetField("npc", BindingFlags.Public | BindingFlags.Static);
                _mainMouseXField = mainType.GetField("mouseX", BindingFlags.Public | BindingFlags.Static);
                _mainMouseYField = mainType.GetField("mouseY", BindingFlags.Public | BindingFlags.Static);
                _mainScreenPositionField = mainType.GetField("screenPosition", BindingFlags.Public | BindingFlags.Static);
                _mainGameMenuField = mainType.GetField("gameMenu", BindingFlags.Public | BindingFlags.Static);

                // Lang
                if (_langType != null)
                    _langGetNPCNameValue = _langType.GetMethod("GetNPCNameValue", BindingFlags.Public | BindingFlags.Static);

                // ContentSamples
                if (_contentSamplesType != null)
                    _npcsByNetIdField = _contentSamplesType.GetField("NpcsByNetId", BindingFlags.Public | BindingFlags.Static);

                // NPCID.Count
                if (npcidType != null)
                    _npcidCountField = npcidType.GetField("Count", BindingFlags.Public | BindingFlags.Static);

                // NPCID.Sets
                if (npcidSetsType != null)
                {
                    _shouldBeCountedAsBossField = npcidSetsType.GetField("ShouldBeCountedAsBossForBestiary", BindingFlags.Public | BindingFlags.Static);
                    _townCritterField = npcidSetsType.GetField("TownCritter", BindingFlags.Public | BindingFlags.Static);
                }

                _log.Debug("NPCSpawner reflection initialized");
            }
            catch (Exception ex)
            {
                _log.Error($"NPCSpawner init error: {ex.Message}");
            }
        }

        public static void LoadFavourites(string bossFavs, string npcFavs)
        {
            _favouriteBossIds.Clear();
            _favouriteNPCIds.Clear();
            ParseFavourites(bossFavs, _favouriteBossIds);
            ParseFavourites(npcFavs, _favouriteNPCIds);
        }

        public static void LoadRightClickSpawn(bool enabled)
        {
            _rightClickSpawn = enabled;
        }

        private static void ParseFavourites(string csv, HashSet<int> target)
        {
            if (string.IsNullOrEmpty(csv)) return;
            foreach (var part in csv.Split(','))
            {
                if (int.TryParse(part.Trim(), out int id))
                    target.Add(id);
            }
        }

        public static string SaveBossFavourites() => string.Join(",", _favouriteBossIds);
        public static string SaveNPCFavourites() => string.Join(",", _favouriteNPCIds);
        public static bool RightClickSpawnEnabled => _rightClickSpawn;

        public static void Update(bool panelVisible)
        {
            // Text input update for focused search fields
            if (panelVisible)
            {
                _bossSearch.Update();
                _npcSearch.Update();
            }
            else
            {
                if (_bossSearch.IsFocused) _bossSearch.Unfocus();
                if (_npcSearch.IsFocused) _npcSearch.Unfocus();
            }

            // Right-click spawn: works even when panel is closed
            if (_rightClickSpawn && _selectedId > 0 && !IsGameMenu())
            {
                if (UIRenderer.MouseRightClick && !UIRenderer.IsMouseOverAnyPanel())
                {
                    SpawnAtCursor();
                }
            }
        }

        #region Boss Tab

        public static void DrawBossTab(ref StackLayout stack)
        {
            if (!_catalogBuilt) BuildCatalog();

            _bossSearch.KeyBlockId = "admin-panel";
            int searchY = stack.Advance(28);
            _bossSearch.Draw(stack.X, searchY, stack.Width, 28);
            if (_bossSearch.HasChanged)
                FilterBosses();

            if (_filteredBosses.Count > 0)
            {
                int listHeight = Math.Min(_filteredBosses.Count, MaxVisibleRows) * RowHeight;
                int listY = stack.Advance(listHeight);
                DrawBossList(stack.X, listY, stack.Width, listHeight);
            }
            else if (_bosses.Count > 0)
            {
                stack.Label("No matching bosses", UIColors.TextHint);
            }

            DrawSpawnControls(ref stack);
        }

        private static void DrawBossList(int x, int y, int width, int height)
        {
            int contentHeight = _filteredBosses.Count * RowHeight;
            _bossScroll.Begin(x, y, width, height, contentHeight);

            int cw = _bossScroll.ContentWidth;
            for (int i = 0; i < _filteredBosses.Count; i++)
            {
                int itemY = i * RowHeight;
                if (!_bossScroll.IsVisible(itemY, RowHeight)) continue;

                int drawY = _bossScroll.ContentY + itemY;
                var entry = _filteredBosses[i];
                bool isSelected = entry.Id == _selectedId;
                bool isFav = _favouriteBossIds.Contains(entry.Id);
                bool hover = WidgetInput.IsMouseOver(x, drawY, cw, RowHeight);

                Color4 bg = isSelected ? UIColors.ItemActiveBg
                    : (hover ? UIColors.ItemHoverBg : UIColors.ItemBg);
                UIRenderer.DrawRect(x, drawY, cw, RowHeight, bg);

                // Favourite indicator
                string prefix = isFav ? "* " : "  ";
                UIRenderer.DrawText(prefix + entry.Id, x + 4, drawY + 4,
                    isFav ? UIColors.Warning : UIColors.TextHint);
                string name = TextUtil.Truncate(entry.Name, cw - 60);
                UIRenderer.DrawText(name, x + 52, drawY + 4,
                    isFav ? UIColors.AccentText : UIColors.Text);

                if (hover)
                {
                    if (WidgetInput.MouseLeftClick)
                    {
                        _selectedId = entry.Id;
                        _selectedName = entry.Name;
                        WidgetInput.ConsumeClick();
                    }
                    else if (WidgetInput.MouseRightClick)
                    {
                        if (_favouriteBossIds.Contains(entry.Id))
                            _favouriteBossIds.Remove(entry.Id);
                        else
                            _favouriteBossIds.Add(entry.Id);
                        WidgetInput.ConsumeRightClick();
                        FilterBosses(); // Re-sort
                    }
                }
            }

            _bossScroll.End();
        }

        #endregion

        #region NPC Tab

        public static void DrawNPCTab(ref StackLayout stack)
        {
            if (!_catalogBuilt) BuildCatalog();

            _npcSearch.KeyBlockId = "admin-panel";
            int searchY = stack.Advance(28);
            _npcSearch.Draw(stack.X, searchY, stack.Width, 28);
            if (_npcSearch.HasChanged)
                FilterNPCs();

            if (_filteredNPCs.Count > 0)
            {
                int listHeight = Math.Min(CountVisibleRows(_filteredNPCs), MaxVisibleRows) * RowHeight;
                int listY = stack.Advance(listHeight);
                DrawNPCList(stack.X, listY, stack.Width, listHeight);
            }
            else if (_townNPCs.Count + _critters.Count + _enemies.Count > 0)
            {
                stack.Label("No matching NPCs", UIColors.TextHint);
            }

            DrawSpawnControls(ref stack);
        }

        private static int CountVisibleRows(List<CatalogEntry> list)
        {
            return list.Count; // Includes section headers
        }

        private static void DrawNPCList(int x, int y, int width, int height)
        {
            int contentHeight = _filteredNPCs.Count * RowHeight;
            _npcScroll.Begin(x, y, width, height, contentHeight);

            int cw = _npcScroll.ContentWidth;
            for (int i = 0; i < _filteredNPCs.Count; i++)
            {
                int itemY = i * RowHeight;
                if (!_npcScroll.IsVisible(itemY, RowHeight)) continue;

                int drawY = _npcScroll.ContentY + itemY;
                var entry = _filteredNPCs[i];

                // Section header
                if (entry.Id < 0)
                {
                    UIRenderer.DrawRect(x, drawY, cw, RowHeight, UIColors.SectionBg);
                    string sectionName = entry.Id == SectionTown ? "TOWN NPCs"
                        : entry.Id == SectionCritter ? "CRITTERS"
                        : "ENEMIES";
                    UIRenderer.DrawText(sectionName, x + 8, drawY + 4, UIColors.AccentText);
                    continue;
                }

                bool isSelected = entry.Id == _selectedId;
                bool isFav = _favouriteNPCIds.Contains(entry.Id);
                bool hover = WidgetInput.IsMouseOver(x, drawY, cw, RowHeight);

                Color4 bg = isSelected ? UIColors.ItemActiveBg
                    : (hover ? UIColors.ItemHoverBg : UIColors.ItemBg);
                UIRenderer.DrawRect(x, drawY, cw, RowHeight, bg);

                string prefix = isFav ? "* " : "  ";
                UIRenderer.DrawText(prefix + entry.Id, x + 4, drawY + 4,
                    isFav ? UIColors.Warning : UIColors.TextHint);
                string name = TextUtil.Truncate(entry.Name, cw - 60);
                UIRenderer.DrawText(name, x + 52, drawY + 4,
                    isFav ? UIColors.AccentText : UIColors.Text);

                if (hover)
                {
                    if (WidgetInput.MouseLeftClick)
                    {
                        _selectedId = entry.Id;
                        _selectedName = entry.Name;
                        WidgetInput.ConsumeClick();
                    }
                    else if (WidgetInput.MouseRightClick)
                    {
                        if (_favouriteNPCIds.Contains(entry.Id))
                            _favouriteNPCIds.Remove(entry.Id);
                        else
                            _favouriteNPCIds.Add(entry.Id);
                        WidgetInput.ConsumeRightClick();
                        FilterNPCs();
                    }
                }
            }

            _npcScroll.End();
        }

        #endregion

        #region Spawn Controls (shared between tabs)

        private static void DrawSpawnControls(ref StackLayout stack)
        {
            if (_selectedId > 0)
            {
                stack.Label($"Selected: {_selectedName} (ID: {_selectedId})", UIColors.AccentText);

                int hw = (stack.Width - 8) / 2;
                if (stack.ButtonAt(stack.X, hw, "Spawn"))
                    SpawnAtPlayerOffset();
                string toggleLabel = _rightClickSpawn ? "R-Click Spawn: ON" : "R-Click Spawn: OFF";
                if (stack.ButtonAt(stack.X + hw + 8, hw, toggleLabel))
                    _rightClickSpawn = !_rightClickSpawn;
                stack.Advance(26);
            }

            if (!string.IsNullOrEmpty(_lastResult))
            {
                stack.Label(_lastResult, UIColors.TextDim);
            }

            if (_rightClickSpawn && _selectedId <= 0)
            {
                stack.Label("Select an NPC to use R-click spawn", UIColors.Warning);
            }
        }

        #endregion

        #region Catalog Building

        private static void BuildCatalog()
        {
            _catalogBuilt = true;
            _bosses.Clear();
            _townNPCs.Clear();
            _critters.Clear();
            _enemies.Clear();

            if (_langGetNPCNameValue == null || _npcidCountField == null)
            {
                _log?.Info("NPCSpawner: Missing reflection for catalog building");
                return;
            }

            try
            {
                int count = Convert.ToInt32(_npcidCountField.GetValue(null));

                // Try to use ContentSamples for categorization
                Dictionary<int, object> npcSamples = null;
                if (_npcsByNetIdField != null)
                {
                    var rawDict = _npcsByNetIdField.GetValue(null);
                    if (rawDict != null)
                    {
                        // It's Dictionary<int, NPC> - use reflection to iterate
                        var dictType = rawDict.GetType();
                        var keysProperty = dictType.GetProperty("Keys");
                        var indexer = dictType.GetProperty("Item");
                        if (keysProperty != null && indexer != null)
                        {
                            npcSamples = new Dictionary<int, object>();
                            var keys = keysProperty.GetValue(rawDict) as System.Collections.IEnumerable;
                            if (keys != null)
                            {
                                foreach (var key in keys)
                                {
                                    int k = (int)key;
                                    if (k >= 1 && k < count)
                                    {
                                        try
                                        {
                                            var npc = indexer.GetValue(rawDict, new object[] { key });
                                            if (npc != null)
                                                npcSamples[k] = npc;
                                        }
                                        catch { }
                                    }
                                }
                            }
                        }
                    }
                }

                // Get Sets arrays for additional categorization
                bool[] shouldBeCountedAsBoss = _shouldBeCountedAsBossField?.GetValue(null) as bool[];
                bool[] townCritter = _townCritterField?.GetValue(null) as bool[];

                for (int i = 1; i < count; i++)
                {
                    string name;
                    try
                    {
                        name = (string)_langGetNPCNameValue.Invoke(null, new object[] { i });
                    }
                    catch { continue; }

                    if (string.IsNullOrEmpty(name) || name.Trim().Length == 0) continue;

                    var entry = new CatalogEntry { Id = i, Name = name };
                    Category category = CategorizeNPC(i, npcSamples, shouldBeCountedAsBoss, townCritter);

                    switch (category)
                    {
                        case Category.Boss: _bosses.Add(entry); break;
                        case Category.Town: _townNPCs.Add(entry); break;
                        case Category.Critter: _critters.Add(entry); break;
                        case Category.Enemy: _enemies.Add(entry); break;
                    }
                }

                _bosses.Sort(CompareByName);
                _townNPCs.Sort(CompareByName);
                _critters.Sort(CompareByName);
                _enemies.Sort(CompareByName);

                _log?.Info($"NPCSpawner catalog: {_bosses.Count} bosses, {_townNPCs.Count} town, {_critters.Count} critters, {_enemies.Count} enemies");

                FilterBosses();
                FilterNPCs();
            }
            catch (Exception ex)
            {
                _log?.Error($"NPCSpawner catalog build error: {ex.Message}");
            }
        }

        private static Category CategorizeNPC(int id, Dictionary<int, object> samples,
            bool[] shouldBeCountedAsBoss, bool[] townCritter)
        {
            // Check boss via ContentSamples instance or Sets array
            if (samples != null && samples.TryGetValue(id, out var npc))
            {
                try
                {
                    bool isBoss = _npcBossField != null && (bool)_npcBossField.GetValue(npc);
                    if (isBoss) return Category.Boss;

                    bool isTown = _npcTownNPCField != null && (bool)_npcTownNPCField.GetValue(npc);
                    if (isTown) return Category.Town;

                    short catchItem = _npcCatchItemField != null ? (short)_npcCatchItemField.GetValue(npc) : (short)0;
                    if (catchItem > 0) return Category.Critter;
                }
                catch { }
            }

            // Fallback to Sets arrays
            if (shouldBeCountedAsBoss != null && id < shouldBeCountedAsBoss.Length && shouldBeCountedAsBoss[id])
                return Category.Boss;

            if (townCritter != null && id < townCritter.Length && townCritter[id])
                return Category.Critter;

            return Category.Enemy;
        }

        private static int CompareByName(CatalogEntry a, CatalogEntry b)
            => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);

        #endregion

        #region Filtering

        private static void FilterBosses()
        {
            _filteredBosses.Clear();
            string search = _bossSearch.Text?.ToLower() ?? "";
            int searchId = 0;
            bool isNumeric = !string.IsNullOrEmpty(search) && int.TryParse(search, out searchId);

            // Favourites first, then non-favourites; each group alphabetical
            var favs = new List<CatalogEntry>();
            var rest = new List<CatalogEntry>();

            foreach (var entry in _bosses)
            {
                if (!string.IsNullOrEmpty(search))
                {
                    if (!(isNumeric && entry.Id == searchId) && !entry.Name.ToLower().Contains(search))
                        continue;
                }

                if (_favouriteBossIds.Contains(entry.Id))
                    favs.Add(entry);
                else
                    rest.Add(entry);
            }

            _filteredBosses.AddRange(favs);
            _filteredBosses.AddRange(rest);
            _bossScroll.ResetScroll();
        }

        private static void FilterNPCs()
        {
            _filteredNPCs.Clear();
            string search = _npcSearch.Text?.ToLower() ?? "";
            int searchId = 0;
            bool isNumeric = !string.IsNullOrEmpty(search) && int.TryParse(search, out searchId);

            AddSectionToFiltered(SectionTown, "Town NPCs", _townNPCs, search, isNumeric, searchId, _favouriteNPCIds);
            AddSectionToFiltered(SectionCritter, "Critters", _critters, search, isNumeric, searchId, _favouriteNPCIds);
            AddSectionToFiltered(SectionEnemy, "Enemies", _enemies, search, isNumeric, searchId, _favouriteNPCIds);
            _npcScroll.ResetScroll();
        }

        private static void AddSectionToFiltered(int sectionId, string sectionName,
            List<CatalogEntry> source, string search, bool isNumeric, int searchId,
            HashSet<int> favourites)
        {
            var favs = new List<CatalogEntry>();
            var rest = new List<CatalogEntry>();

            foreach (var entry in source)
            {
                if (!string.IsNullOrEmpty(search))
                {
                    if (!(isNumeric && entry.Id == searchId) && !entry.Name.ToLower().Contains(search))
                        continue;
                }

                if (favourites.Contains(entry.Id))
                    favs.Add(entry);
                else
                    rest.Add(entry);
            }

            if (favs.Count + rest.Count == 0) return;

            // Add section header
            _filteredNPCs.Add(new CatalogEntry { Id = sectionId, Name = sectionName });
            _filteredNPCs.AddRange(favs);
            _filteredNPCs.AddRange(rest);
        }

        #endregion

        #region Spawning

        private static void SpawnAtPlayerOffset()
        {
            if (_selectedId <= 0) return;
            try
            {
                GetPlayerCenter(out float cx, out float cy);
                int worldX = (int)cx;
                int worldY = (int)cy - SpawnOffsetY;
                DoSpawn(worldX, worldY);
                _lastResult = $"Spawned {_selectedName} near player";
            }
            catch (Exception ex)
            {
                _lastResult = $"Error: {ex.InnerException?.Message ?? ex.Message}";
            }
        }

        private static void SpawnAtCursor()
        {
            if (_selectedId <= 0) return;
            try
            {
                GetScreenPosition(out float screenX, out float screenY);
                int mouseX = GetMainMouseX();
                int mouseY = GetMainMouseY();
                int worldX = mouseX + (int)screenX;
                int worldY = mouseY + (int)screenY;
                DoSpawn(worldX, worldY);
                _lastResult = $"Spawned {_selectedName} at cursor";
            }
            catch (Exception ex)
            {
                _lastResult = $"Error: {ex.InnerException?.Message ?? ex.Message}";
            }
        }

        private static void DoSpawn(int worldX, int worldY)
        {
            if (_npcNewNPC == null)
                throw new Exception("NPC.NewNPC not found");

            object source = null;
            if (_entitySourceType != null)
                source = Activator.CreateInstance(_entitySourceType);
            if (source == null)
                throw new Exception("EntitySource not available");

            int myPlayer = _mainMyPlayerField != null ? (int)_mainMyPlayerField.GetValue(null) : 0;

            var parms = _npcNewNPC.GetParameters();
            var args = new object[parms.Length];
            args[0] = source;
            args[1] = worldX;
            args[2] = worldY;
            args[3] = _selectedId;
            for (int i = 4; i < parms.Length; i++)
            {
                args[i] = parms[i].HasDefaultValue
                    ? parms[i].DefaultValue
                    : (parms[i].ParameterType.IsValueType
                        ? Activator.CreateInstance(parms[i].ParameterType)
                        : null);
            }

            // Set Target parameter to local player (for boss AI)
            for (int i = 0; i < parms.Length; i++)
            {
                if (parms[i].Name == "Target")
                {
                    args[i] = myPlayer;
                    break;
                }
            }

            object result = _npcNewNPC.Invoke(null, args);

            // Extend timeLeft (prevents despawn, same as SpawnBoss)
            if (result is int npcIndex && npcIndex >= 0)
            {
                try
                {
                    var npcArray = _mainNpcField?.GetValue(null) as Array;
                    if (npcArray != null && npcIndex < npcArray.Length && _npcTimeLeftField != null)
                    {
                        var npc = npcArray.GetValue(npcIndex);
                        int current = (int)_npcTimeLeftField.GetValue(npc);
                        _npcTimeLeftField.SetValue(npc, current * 20);
                    }
                }
                catch { }
            }
        }

        #endregion

        #region Reflection Helpers

        private static void GetPlayerCenter(out float x, out float y)
        {
            int myPlayer = (int)_mainMyPlayerField.GetValue(null);
            var players = (Array)_mainPlayerArrayField.GetValue(null);
            var player = players.GetValue(myPlayer);
            var center = _playerCenterProp.GetValue(player);
            x = (float)_vector2XField.GetValue(center);
            y = (float)_vector2YField.GetValue(center);
        }

        private static void GetScreenPosition(out float x, out float y)
        {
            var screenPos = _mainScreenPositionField.GetValue(null);
            x = (float)_vector2XField.GetValue(screenPos);
            y = (float)_vector2YField.GetValue(screenPos);
        }

        private static int GetMainMouseX()
        {
            return (int)_mainMouseXField.GetValue(null);
        }

        private static int GetMainMouseY()
        {
            return (int)_mainMouseYField.GetValue(null);
        }

        private static bool IsGameMenu()
        {
            try { return _mainGameMenuField != null && (bool)_mainGameMenuField.GetValue(null); }
            catch { return false; }
        }

        #endregion

        public static void Unload()
        {
            _catalogBuilt = false;
            _bosses.Clear();
            _townNPCs.Clear();
            _critters.Clear();
            _enemies.Clear();
            _filteredBosses.Clear();
            _filteredNPCs.Clear();
            _favouriteBossIds.Clear();
            _favouriteNPCIds.Clear();
            _selectedId = -1;
            _selectedName = "";
            _lastResult = "";
            _rightClickSpawn = false;
            _log = null;
        }
    }
}
