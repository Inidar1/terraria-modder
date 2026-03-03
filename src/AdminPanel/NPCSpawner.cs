using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.DataStructures;
using Terraria.ID;
using TerrariaModder.Core.Input;
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

        public static void Init(ILogger log)
        {
            _log = log;
            _log.Debug("NPCSpawner initialized");
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
            if (_rightClickSpawn && _selectedId > 0 && !Main.gameMenu)
            {
                if (InputState.IsKeyJustPressed(KeyCode.MouseRight) && !UIRenderer.IsMouseOverAnyPanel())
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

            try
            {
                int count = NPCID.Count;

                // Get Sets arrays for categorization
                bool[] shouldBeCountedAsBoss = NPCID.Sets.ShouldBeCountedAsBossForBestiary;
                bool[] townCritter = NPCID.Sets.TownCritter;

                for (int i = 1; i < count; i++)
                {
                    string name;
                    try
                    {
                        name = Lang.GetNPCNameValue(i);
                    }
                    catch { continue; }

                    if (string.IsNullOrEmpty(name) || name.Trim().Length == 0) continue;

                    var entry = new CatalogEntry { Id = i, Name = name };

                    // Categorize using ContentSamples
                    NPC sampleNpc = null;
                    if (ContentSamples.NpcsByNetId.TryGetValue(i, out var sample))
                        sampleNpc = sample;

                    Category category = CategorizeNPC(i, sampleNpc, shouldBeCountedAsBoss, townCritter);

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

        private static Category CategorizeNPC(int id, NPC sampleNpc,
            bool[] shouldBeCountedAsBoss, bool[] townCritter)
        {
            // Check via ContentSamples instance
            if (sampleNpc != null)
            {
                if (sampleNpc.boss) return Category.Boss;
                if (sampleNpc.townNPC) return Category.Town;
                if (sampleNpc.catchItem > 0) return Category.Critter;
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
                Vector2 center = Main.player[Main.myPlayer].Center;
                int worldX = (int)center.X;
                int worldY = (int)center.Y - SpawnOffsetY;
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
                int worldX = Main.mouseX + (int)Main.screenPosition.X;
                int worldY = Main.mouseY + (int)Main.screenPosition.Y;
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
            var source = new EntitySource_SpawnNPC();
            int npcIndex = NPC.NewNPC(source, worldX, worldY, _selectedId, Target: Main.myPlayer);

            // Extend timeLeft (prevents despawn, same as SpawnBoss)
            if (npcIndex >= 0 && npcIndex < Main.npc.Length)
            {
                Main.npc[npcIndex].timeLeft *= 20;
            }
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
