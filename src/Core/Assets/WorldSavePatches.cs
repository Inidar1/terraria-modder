using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Terraria;
using Terraria.IO;
using TerrariaModder.Core.Logging;

namespace TerrariaModder.Core.Assets
{
    /// <summary>
    /// Save interception for world files.
    ///
    /// Scans all world chests (Main.chest[0-7999]) for custom items.
    /// Same pattern as player: extract → save moddata → air → vanilla save → restore.
    /// </summary>
    internal static class WorldSavePatches
    {
        private static Harmony _harmony;
        private static ILogger _log;
        private static bool _applied;

        private static readonly Dictionary<string, Item> _extractedItems = new Dictionary<string, Item>();

        public static void Initialize(ILogger logger)
        {
            _log = logger;
            _harmony = new Harmony("com.terrariamodder.assets.v3.worldsave");
        }

        public static void ApplyPatches()
        {
            if (_applied) return;

            try
            {
                PatchSaveWorld();
                PatchLoadWorld();
                _applied = true;
                _log?.Info("[WorldSavePatches] Applied successfully");
            }
            catch (Exception ex)
            {
                _log?.Error($"[WorldSavePatches] Failed: {ex.Message}");
            }
        }

        private static void PatchSaveWorld()
        {
            // WorldFile.SaveWorld() or WorldFile.SaveWorld(bool useCloudSaving, bool resetTime)
            var worldFileType = typeof(Terraria.IO.WorldFile);
            var saveMethod = worldFileType.GetMethod("SaveWorld",
                BindingFlags.Public | BindingFlags.Static, null,
                new[] { typeof(bool), typeof(bool) }, null);

            if (saveMethod == null)
            {
                saveMethod = worldFileType.GetMethod("SaveWorld",
                    BindingFlags.Public | BindingFlags.Static, null,
                    Type.EmptyTypes, null);
            }

            if (saveMethod == null)
            {
                _log?.Warn("[WorldSavePatches] WorldFile.SaveWorld not found");
                return;
            }

            _harmony.Patch(saveMethod,
                prefix: new HarmonyMethod(typeof(WorldSavePatches), nameof(SaveWorld_Prefix)),
                postfix: new HarmonyMethod(typeof(WorldSavePatches), nameof(SaveWorld_Postfix)));
        }

        private static void PatchLoadWorld()
        {
            var worldFileType = typeof(Terraria.IO.WorldFile);

            // LoadWorld() is public static with no parameters in Terraria 1.4.5
            var loadMethod = worldFileType.GetMethod("LoadWorld",
                BindingFlags.Public | BindingFlags.Static, null,
                Type.EmptyTypes, null);

            if (loadMethod == null)
            {
                _log?.Warn("[WorldSavePatches] WorldFile.LoadWorld() not found");
                return;
            }

            _harmony.Patch(loadMethod,
                postfix: new HarmonyMethod(typeof(WorldSavePatches), nameof(LoadWorld_Postfix)));
        }

        // ── Save prefix: extract custom items from chests ──

        private static void SaveWorld_Prefix()
        {
            _extractedItems.Clear();

            try
            {
                var customItems = new List<ModdataFile.ItemEntry>();

                // Scan all world chests
                for (int c = 0; c < Main.maxChests; c++)
                {
                    var chest = Main.chest[c];
                    if (chest?.item == null) continue;
                    if (IsCustomContainerChest(chest)) continue;

                    for (int s = 0; s < chest.item.Length; s++)
                    {
                        var item = chest.item[s];
                        if (item == null || item.IsAir || item.type < ItemRegistry.VanillaItemCount) continue;

                        string fullId = ItemRegistry.GetFullId(item.type);
                        if (fullId == null)
                        {
                            _log?.Warn($"[Save] Custom item type {item.type} in chest_{c}[{s}] has no registered ID - item will be lost on save");
                            continue;
                        }

                        string key = $"chest_{c}:{s}";
                        customItems.Add(new ModdataFile.ItemEntry
                        {
                            Location = $"chest_{c}",
                            Slot = s,
                            ItemId = fullId,
                            Stack = item.stack,
                            Prefix = item.prefix,
                            Favorited = false
                        });
                        _extractedItems[key] = item;
                    }
                }

                // Include pending world items in moddata so they persist
                customItems.AddRange(PendingItemStore.GetWorldModdataEntries());

                // Determine world path early (needed for both write and cleanup)
                string worldPath = GetCurrentWorldPath();
                if (worldPath == null)
                {
                    _log?.Warn("[WorldSavePatches] Could not determine world path");
                    RestoreAll();
                    return;
                }

                string moddataPath = ModdataFile.GetWorldModdataPath(worldPath);

                if (customItems.Count == 0)
                {
                    // Delete stale moddata so deleted pending items don't reappear on next load
                    ModdataFile.Delete(moddataPath);
                    _log?.Debug("[WorldSavePatches] No custom items in chests, cleaned up moddata");
                    return;
                }

                if (!ModdataFile.Write(moddataPath, customItems))
                {
                    _log?.Error("[WorldSavePatches] Failed to write moddata");
                    RestoreAll();
                    return;
                }

                // Replace with air
                foreach (var kvp in _extractedItems)
                {
                    var parts = kvp.Key.Split(':');
                    string chestKey = parts[0]; // "chest_N"
                    int slot = int.Parse(parts[1]);
                    int chestIdx = int.Parse(chestKey.Substring(6)); // after "chest_"
                    Main.chest[chestIdx].item[slot] = new Item();
                }

                _log?.Info($"[WorldSavePatches] Extracted {customItems.Count} custom items from chests");
            }
            catch (Exception ex)
            {
                _log?.Error($"[WorldSavePatches] Prefix error: {ex.Message}");
                RestoreAll();
            }
        }

        // ── Save postfix: restore items ──

        private static void SaveWorld_Postfix()
        {
            if (_extractedItems.Count == 0) return;

            try
            {
                RestoreAll();
                _log?.Debug("[WorldSavePatches] Restored items after save");
            }
            catch (Exception ex)
            {
                _log?.Error($"[WorldSavePatches] Postfix error: {ex.Message}");
            }
            finally
            {
                _extractedItems.Clear();
            }
        }

        // ── Load postfix: inject items into chests ──

        private static void LoadWorld_Postfix()
        {
            try
            {
                string worldPath = GetCurrentWorldPath();
                if (worldPath == null) return;

                string moddataPath = ModdataFile.GetWorldModdataPath(worldPath);
                var items = ModdataFile.Read(moddataPath);
                if (items.Count == 0) return;

                // Clear previous pending world items
                PendingItemStore.ClearWorld();

                int injected = 0, skipped = 0;

                foreach (var entry in items)
                {
                    try
                    {
                        // Pending world items from previous session — re-add to store
                        if (entry.Location == "pending_world")
                        {
                            int rt = ItemRegistry.GetRuntimeType(entry.ItemId);
                            if (rt >= 0)
                            {
                                PendingItemStore.AddWorldItem(new PendingItemStore.PendingItem
                                {
                                    ItemId = entry.ItemId,
                                    RuntimeType = rt,
                                    Stack = entry.Stack,
                                    Prefix = entry.Prefix,
                                    Favorited = false
                                });
                            }
                            skipped++;
                            continue;
                        }

                        // Parse chest index from location "chest_N"
                        if (!entry.Location.StartsWith("chest_")) { skipped++; continue; }
                        if (!int.TryParse(entry.Location.Substring(6), out int chestIdx)) { skipped++; continue; }
                        if (chestIdx < 0 || chestIdx >= Main.maxChests || Main.chest[chestIdx]?.item == null) { skipped++; continue; }

                        int runtimeType = ItemRegistry.GetRuntimeType(entry.ItemId);
                        if (runtimeType < 0) { skipped++; continue; }

                        var item = new Item();
                        item.SetDefaults(runtimeType);
                        item.stack = entry.Stack;
                        item.prefix = (byte)entry.Prefix;
                        if (entry.Prefix > 0) item.Prefix(entry.Prefix);

                        var chest = Main.chest[chestIdx];
                        if (entry.Slot >= 0 && entry.Slot < chest.item.Length &&
                            (chest.item[entry.Slot] == null || chest.item[entry.Slot].IsAir))
                        {
                            chest.item[entry.Slot] = item;
                            injected++;
                        }
                        else
                        {
                            // Find empty slot in same chest
                            bool placed = false;
                            for (int s = 0; s < chest.item.Length; s++)
                            {
                                if (chest.item[s] == null || chest.item[s].IsAir)
                                {
                                    chest.item[s] = item;
                                    injected++;
                                    placed = true;
                                    break;
                                }
                            }
                            if (!placed)
                            {
                                _log?.Info($"[WorldSavePatches] No slot for {entry.ItemId} in chest {chestIdx} — added to pending items");
                                PendingItemStore.AddWorldItem(new PendingItemStore.PendingItem
                                {
                                    ItemId = entry.ItemId,
                                    RuntimeType = runtimeType,
                                    Stack = entry.Stack,
                                    Prefix = entry.Prefix,
                                    Favorited = false
                                });
                                skipped++;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _log?.Error($"[WorldSavePatches] Failed to inject {entry.ItemId}: {ex.Message}");
                        skipped++;
                    }
                }

                _log?.Info($"[WorldSavePatches] Injected {injected} items into chests, skipped {skipped}");
            }
            catch (Exception ex)
            {
                _log?.Error($"[WorldSavePatches] Load postfix error: {ex.Message}");
            }
        }

        // ── Helpers ──
        private static bool IsCustomContainerChest(Chest chest)
        {
            if (chest == null) return false;

            int x = chest.x;
            int y = chest.y;
            if (x < 0 || x >= Main.maxTilesX || y < 0 || y >= Main.maxTilesY)
                return false;

            if (!CustomTileContainers.TryGetTileDefinition(x, y, out var definition, out _))
                return false;

            return definition != null && definition.IsContainer;
        }

        private static void RestoreAll()
        {
            foreach (var kvp in _extractedItems)
            {
                try
                {
                    var parts = kvp.Key.Split(':');
                    string chestKey = parts[0];
                    int slot = int.Parse(parts[1]);
                    int chestIdx = int.Parse(chestKey.Substring(6));
                    if (chestIdx >= 0 && chestIdx < Main.maxChests && Main.chest[chestIdx]?.item != null)
                        Main.chest[chestIdx].item[slot] = kvp.Value;
                }
                catch { }
            }
        }

        private static string GetCurrentWorldPath()
        {
            try
            {
                // Try Main.ActiveWorldFileData.Path
                var worldFileData = Main.ActiveWorldFileData;
                if (worldFileData != null)
                {
                    var pathProp = worldFileData.GetType().GetProperty("Path");
                    if (pathProp != null)
                    {
                        return pathProp.GetValue(worldFileData) as string;
                    }
                }

                // Fallback: Main.worldPathName (getter-only property)
                var worldPathProp = typeof(Main).GetProperty("worldPathName", BindingFlags.Public | BindingFlags.Static);
                return worldPathProp?.GetValue(null) as string;
            }
            catch
            {
                return null;
            }
        }
    }
}
