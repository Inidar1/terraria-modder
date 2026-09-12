using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Terraria;
using TerrariaModder.Core.Logging;

namespace TerrariaModder.Core.Assets
{
    /// <summary>
    /// Reads and writes moddata files in the mod-keyed JSON format (version 2).
    ///
    /// Version 1 (legacy): flat item list sidecar at {savepath}.moddata — no longer written,
    ///   read-only for one-time migration.
    ///
    /// Version 2 (current): mod-keyed JSON at:
    ///   Client: Main.SavePath/TerrariaModder/players/{CharName}.json
    ///   Client: Main.SavePath/TerrariaModder/worlds/{WorldName}.json
    ///   Server: CorePath/player-data/moddata/{guid}.json
    ///   Server: CorePath/world-data/{WorldName}.json
    ///
    /// Mod-keyed format:
    /// {
    ///   "version": 2,
    ///   "mod-id-1": {
    ///     "items": [
    ///       { "item_id": "mod-id-1:item-name", "location": "inventory", "slot": 0,
    ///         "stack": 1, "prefix": 0, "favorited": false }
    ///     ]
    ///   },
    ///   "mod-id-2": { "items": [...] }
    /// }
    ///
    /// Preservation: On save, items from currently-loaded mods are written fresh.
    /// Items from unloaded mods (passed as preservedItems) are written back verbatim,
    /// preventing item loss when a mod is temporarily uninstalled.
    /// </summary>
    public static class ModdataFile
    {
        private static ILogger _log;
        private const int FORMAT_VERSION = 2;
        private const int LEGACY_VERSION = 1;

        public static void Initialize(ILogger logger)
        {
            _log = logger;
        }

        // ─── Item entry (shared between v1 and v2) ───────────────────────────

        public class ItemEntry
        {
            public string Location { get; set; }
            public int Slot { get; set; }
            public string ItemId { get; set; } // "modid:itemname"
            public int Stack { get; set; } = 1;
            public int Prefix { get; set; }
            public bool Favorited { get; set; }
        }

        // ─── Path helpers ─────────────────────────────────────────────────────

        /// <summary>
        /// New client player moddata path: Main.SavePath/TerrariaModder/players/{CharName}.json
        /// </summary>
        public static string GetPlayerModdataPath(string playerPath)
        {
            try
            {
                string savePath = Main.SavePath;
                string fileName = Path.GetFileNameWithoutExtension(playerPath);
                return Path.Combine(savePath, "TerrariaModder", "players", fileName + ".json");
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// World moddata path: stored as sidecar next to the .wld file (e.g., FTW.wld.moddata).
        /// </summary>
        public static string GetWorldModdataPath(string worldPath)
        {
            try
            {
                // Store moddata NEXT TO the world file (FTW.wld -> FTW.wld.moddata)
                // so both client and server processes find the same file.
                // Previously used Main.SavePath which differs between client (Documents)
                // and H&P server (game directory), causing items in chests to be lost.
                if (!string.IsNullOrEmpty(worldPath))
                    return worldPath + ".moddata";

                return null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Legacy v1 player moddata path (sidecar). Used only for migration detection.</summary>
        public static string GetLegacyPlayerModdataPath(string playerPath) =>
            string.IsNullOrEmpty(playerPath) ? null : playerPath + ".moddata";

        /// <summary>Legacy v1 world moddata path (sidecar). Used only for migration detection.</summary>
        public static string GetLegacyWorldModdataPath(string worldPath)
        {
            // Legacy: was stored in Main.SavePath/TerrariaModder/worlds/Name.json
            try
            {
                if (string.IsNullOrEmpty(worldPath)) return null;
                string savePath = Main.SavePath;
                string fileName = Path.GetFileNameWithoutExtension(worldPath);
                return Path.Combine(savePath, "TerrariaModder", "worlds", fileName + ".json");
            }
            catch { return null; }
        }

        // ─── Read (v2 format) ─────────────────────────────────────────────────

        /// <summary>
        /// Read a moddata file.
        /// Returns items belonging to currently-loaded mods (inject into game state).
        /// Items from mods NOT in loadedModIds are returned in preservedItems (write back on save).
        /// Handles both v1 (legacy) and v2 (current) formats.
        /// </summary>
        /// <param name="path">Path to the moddata file.</param>
        /// <param name="loadedModIds">Mod IDs that are currently loaded (used to split items).</param>
        /// <param name="preservedItems">OUT: items from unloaded mods, to be written back on next save.</param>
        public static List<ItemEntry> Read(string path, ICollection<string> loadedModIds,
            out List<ItemEntry> preservedItems)
        {
            preservedItems = new List<ItemEntry>();
            var activeItems = new List<ItemEntry>();

            if (string.IsNullOrEmpty(path))
                return activeItems;

            // Recovery: if primary file is missing, try .tmp then .bak
            // Retain recovery for files left by historical delete-then-move writers.
            if (!File.Exists(path))
            {
                string tmpRecovery = path + ".tmp";
                string bakRecovery = path + ".bak";

                if (File.Exists(tmpRecovery))
                {
                    _log?.Warn($"[ModdataFile] Primary missing, recovering from .tmp: {tmpRecovery}");
                    try
                    {
                        File.Move(tmpRecovery, path);
                    }
                    catch (Exception ex)
                    {
                        _log?.Warn($"[ModdataFile] Failed to recover .tmp: {ex.Message}");
                    }
                }

                if (!File.Exists(path) && File.Exists(bakRecovery))
                {
                    _log?.Warn($"[ModdataFile] Primary missing, recovering from .bak: {bakRecovery}");
                    try
                    {
                        File.Copy(bakRecovery, path);
                    }
                    catch (Exception ex)
                    {
                        _log?.Warn($"[ModdataFile] Failed to recover .bak: {ex.Message}");
                    }
                }

                if (!File.Exists(path))
                    return activeItems;
            }

            try
            {
                string json = File.ReadAllText(path, Encoding.UTF8);
                int version = ExtractVersion(json);

                List<ItemEntry> allItems = version >= FORMAT_VERSION
                    ? ParseAllItems(json)
                    : ParseLegacyItems(json);

                if (allItems.Count == 0) return activeItems;

                // Split: loaded mods → activeItems, unloaded mods → preservedItems
                var loadedSet = loadedModIds != null
                    ? new HashSet<string>(loadedModIds, StringComparer.OrdinalIgnoreCase)
                    : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var entry in allItems)
                {
                    string modId = ExtractModId(entry.ItemId);
                    // "pending" and "pending_world" locations belong to loaded mods (they're
                    // overflow items from a loaded mod that ran out of inventory space).
                    // Always put them in activeItems so the save patch can re-process them.
                    // "vanilla:NNN" items are vanilla items in overflow chest slots (>= 40)
                    // that vanilla save can't persist — always active regardless of loaded mods.
                    bool isPending = entry.Location == "pending" || entry.Location == "pending_world";
                    bool isVanillaOverflow = entry.ItemId != null && entry.ItemId.StartsWith("vanilla:");
                    if (isPending || isVanillaOverflow || string.IsNullOrEmpty(modId) || loadedSet.Contains(modId))
                        activeItems.Add(entry);
                    else
                        preservedItems.Add(entry);
                }

                return activeItems;
            }
            catch (Exception ex)
            {
                _log?.Warn($"[ModdataFile] Failed to read {path}: {ex.Message}");

                // Try backup
                string bakPath = path + ".bak";
                if (File.Exists(bakPath))
                {
                    try
                    {
                        _log?.Info("[ModdataFile] Trying backup file...");
                        string json = File.ReadAllText(bakPath, Encoding.UTF8);
                        int version = ExtractVersion(json);
                        var allItems = version >= FORMAT_VERSION
                            ? ParseAllItems(json)
                            : ParseLegacyItems(json);

                        var loadedSet = loadedModIds != null
                            ? new HashSet<string>(loadedModIds, StringComparer.OrdinalIgnoreCase)
                            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                        foreach (var entry in allItems)
                        {
                            string modId = ExtractModId(entry.ItemId);
                            bool isPending = entry.Location == "pending" || entry.Location == "pending_world";
                            bool isVanillaOverflow = entry.ItemId != null && entry.ItemId.StartsWith("vanilla:");
                            if (isPending || isVanillaOverflow || string.IsNullOrEmpty(modId) || loadedSet.Contains(modId))
                                activeItems.Add(entry);
                            else
                                preservedItems.Add(entry);
                        }
                    }
                    catch (Exception bakEx)
                    {
                        _log?.Error($"[ModdataFile] Backup also failed: {bakEx.Message}");
                    }
                }

                return activeItems;
            }
        }

        // ─── Write (v2 format) ────────────────────────────────────────────────

        /// <summary>
        /// Write a moddata file in v2 mod-keyed format.
        /// <paramref name="items"/> = items for currently-loaded mods (written fresh).
        /// <paramref name="preservedItems"/> = items from unloaded mods (written verbatim, preserving data).
        /// Omitting preservedItems is safe — just means unloaded-mod items won't survive this save.
        /// </summary>
        internal static string Serialize(List<ItemEntry> items, List<ItemEntry> preservedItems = null)
        {
            // Combine and group by modId
            var allItems = new List<ItemEntry>(items ?? new List<ItemEntry>());
            if (preservedItems != null) allItems.AddRange(preservedItems);

            var byMod = new Dictionary<string, List<ItemEntry>>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in allItems)
            {
                string modId = ExtractModId(entry.ItemId);
                if (string.IsNullOrEmpty(modId)) modId = "__unknown__";
                if (!byMod.TryGetValue(modId, out var list))
                    byMod[modId] = list = new List<ItemEntry>();
                list.Add(entry);
            }

            var sb = new StringBuilder();
            sb.AppendLine("{");
            sb.AppendLine($"  \"version\": {FORMAT_VERSION}");

            foreach (var kvp in byMod)
            {
                sb.Append($",\n  \"{Escape(kvp.Key)}\": {{");
                sb.AppendLine();
                sb.AppendLine("    \"items\": [");

                var modItems = kvp.Value;
                for (int i = 0; i < modItems.Count; i++)
                {
                    var item = modItems[i];
                    sb.Append("      { ");
                    sb.Append($"\"item_id\": \"{Escape(item.ItemId)}\", ");
                    sb.Append($"\"location\": \"{Escape(item.Location)}\", ");
                    sb.Append($"\"slot\": {item.Slot}, ");
                    sb.Append($"\"stack\": {item.Stack}, ");
                    sb.Append($"\"prefix\": {item.Prefix}, ");
                    sb.Append($"\"favorited\": {(item.Favorited ? "true" : "false")}");
                    sb.Append(" }");
                    if (i < modItems.Count - 1) sb.Append(",");
                    sb.AppendLine();
                }

                sb.Append("    ]");
                sb.AppendLine();
                sb.Append("  }");
            }

            sb.AppendLine();
            sb.Append("}");

            return sb.ToString();
        }

        public static bool Write(string path, List<ItemEntry> items, List<ItemEntry> preservedItems = null)
        {
            if (string.IsNullOrEmpty(path)) return false;

            try
            {
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                string json = Serialize(items, preservedItems);

                TerrariaModder.Core.IO.AtomicFile.WriteText(path, json, Encoding.UTF8, recoverLegacyTemporary: true);

                return true;
            }
            catch (Exception ex)
            {
                _log?.Error($"[ModdataFile] Failed to write {path}: {ex.Message}");
                return false;
            }
        }

        // ─── Migration ────────────────────────────────────────────────────────

        /// <summary>
        /// One-time migration from legacy v1 sidecar to v2 new location.
        /// If v2 path doesn't exist but v1 sidecar does, reads v1, writes v2, deletes v1.
        /// Returns true if migration was performed.
        /// </summary>
        public static bool MigrateIfNeeded(string v2Path, string v1Path)
        {
            if (string.IsNullOrEmpty(v2Path) || string.IsNullOrEmpty(v1Path)) return false;
            if (File.Exists(v2Path) || !File.Exists(v1Path)) return false;

            try
            {
                var items = ParseLegacyFile(v1Path);
                if (items.Count > 0)
                {
                    Write(v2Path, items, null);
                    _log?.Info($"[ModdataFile] Migrated {items.Count} item(s) from legacy sidecar → {v2Path}");
                }
                else
                {
                    _log?.Info("[ModdataFile] Legacy sidecar was empty — no migration needed");
                }

                // Delete old sidecar and backups
                Delete(v1Path);
                return true;
            }
            catch (Exception ex)
            {
                _log?.Warn($"[ModdataFile] Migration failed ({v1Path} → {v2Path}): {ex.Message}");
                return false;
            }
        }

        // ─── Delete ───────────────────────────────────────────────────────────

        /// <summary>Delete moddata file and backup files.</summary>
        public static void Delete(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
                if (File.Exists(path + ".bak")) File.Delete(path + ".bak");
                if (File.Exists(path + ".tmp")) File.Delete(path + ".tmp");
            }
            catch (Exception ex)
            {
                _log?.Warn($"[ModdataFile] Error deleting {path}: {ex.Message}");
            }
        }

        // ─── JSON parsing ─────────────────────────────────────────────────────

        /// <summary>
        /// Parse ALL items from a v2 mod-keyed file. Finds every "items" array in the JSON
        /// and parses entries from each. The modId is inferred from each entry's item_id prefix.
        /// </summary>
        private static List<ItemEntry> ParseAllItems(string json)
        {
            var items = new List<ItemEntry>();
            int searchFrom = 0;

            while (true)
            {
                // Find next "items" key
                int keyPos = json.IndexOf("\"items\"", searchFrom, StringComparison.Ordinal);
                if (keyPos < 0) break;

                // Skip to opening '['
                int arrStart = json.IndexOf('[', keyPos + 7);
                if (arrStart < 0) break;

                // Find matching ']' (depth-tracking, skipping strings)
                int arrEnd = FindMatchingBracket(json, arrStart, '[', ']');
                if (arrEnd < 0) break;

                string arrContent = json.Substring(arrStart + 1, arrEnd - arrStart - 1);
                ParseItemObjects(arrContent, items);

                searchFrom = arrEnd + 1;
            }

            return items;
        }

        /// <summary>Parse items from a legacy v1 flat-format file.</summary>
        private static List<ItemEntry> ParseLegacyFile(string path)
        {
            try
            {
                if (!File.Exists(path)) return new List<ItemEntry>();
                string json = File.ReadAllText(path, Encoding.UTF8);
                return ParseLegacyItems(json);
            }
            catch (Exception ex)
            {
                _log?.Warn($"[ModdataFile] Could not read legacy file {path}: {ex.Message}");
                return new List<ItemEntry>();
            }
        }

        /// <summary>Parse items from a v1 flat JSON string.</summary>
        private static List<ItemEntry> ParseLegacyItems(string json)
        {
            var items = new List<ItemEntry>();
            int arrStart = json.IndexOf("\"items\"", StringComparison.Ordinal);
            if (arrStart < 0) return items;
            arrStart = json.IndexOf('[', arrStart);
            if (arrStart < 0) return items;
            int arrEnd = FindMatchingBracket(json, arrStart, '[', ']');
            if (arrEnd < 0) return items;
            string arrContent = json.Substring(arrStart + 1, arrEnd - arrStart - 1);
            ParseItemObjects(arrContent, items);
            return items;
        }

        /// <summary>Parse individual item JSON objects from an array content string.</summary>
        private static void ParseItemObjects(string arrContent, List<ItemEntry> output)
        {
            int depth = 0;
            int objStart = -1;
            for (int i = 0; i < arrContent.Length; i++)
            {
                char c = arrContent[i];
                if (c == '"')
                {
                    // Skip string content to avoid false brace matches
                    i++;
                    while (i < arrContent.Length && arrContent[i] != '"')
                    {
                        if (arrContent[i] == '\\') i++;
                        i++;
                    }
                    continue;
                }
                if (c == '{')
                {
                    if (depth == 0) objStart = i;
                    depth++;
                }
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0 && objStart >= 0)
                    {
                        string objJson = arrContent.Substring(objStart, i - objStart + 1);
                        var entry = ParseEntry(objJson);
                        if (entry != null) output.Add(entry);
                        objStart = -1;
                    }
                }
            }
        }

        private static ItemEntry ParseEntry(string json)
        {
            try
            {
                // Support both "item_id" (v1) and "item_id"/"itemId" (v2 uses "item_id" too)
                string itemId = ExtractString(json, "item_id") ?? ExtractString(json, "itemId");
                if (string.IsNullOrEmpty(itemId)) return null;

                return new ItemEntry
                {
                    ItemId = itemId,
                    Location = ExtractString(json, "location") ?? "",
                    Slot = ExtractInt(json, "slot"),
                    Stack = Math.Max(1, ExtractInt(json, "stack", 1)),
                    Prefix = ExtractInt(json, "prefix"),
                    Favorited = ExtractBool(json, "favorited")
                };
            }
            catch
            {
                return null;
            }
        }

        // ─── Helpers ──────────────────────────────────────────────────────────

        private static int ExtractVersion(string json)
        {
            var m = Regex.Match(json, "\"version\"\\s*:\\s*(\\d+)");
            return m.Success && int.TryParse(m.Groups[1].Value, out int v) ? v : 1;
        }

        private static string ExtractModId(string itemId)
        {
            if (string.IsNullOrEmpty(itemId)) return null;
            int colon = itemId.IndexOf(':');
            return colon > 0 ? itemId.Substring(0, colon) : null;
        }

        /// <summary>Find the matching closing bracket, skipping strings. Returns -1 if not found.</summary>
        private static int FindMatchingBracket(string json, int openPos, char open, char close)
        {
            int depth = 0;
            for (int i = openPos; i < json.Length; i++)
            {
                char c = json[i];
                if (c == '"')
                {
                    i++;
                    while (i < json.Length && json[i] != '"')
                    {
                        if (json[i] == '\\') i++;
                        i++;
                    }
                    continue;
                }
                if (c == open) depth++;
                else if (c == close) { depth--; if (depth == 0) return i; }
            }
            return -1;
        }

        private static string ExtractString(string json, string key)
        {
            var match = Regex.Match(json, $"\"{Regex.Escape(key)}\"\\s*:\\s*\"([^\"]*)\"");
            return match.Success ? Unescape(match.Groups[1].Value) : null;
        }

        private static int ExtractInt(string json, string key, int defaultVal = 0)
        {
            var match = Regex.Match(json, $"\"{Regex.Escape(key)}\"\\s*:\\s*(-?\\d+)");
            return match.Success && int.TryParse(match.Groups[1].Value, out int val) ? val : defaultVal;
        }

        private static bool ExtractBool(string json, string key, bool defaultVal = false)
        {
            var match = Regex.Match(json, $"\"{Regex.Escape(key)}\"\\s*:\\s*(true|false)");
            return match.Success ? match.Groups[1].Value == "true" : defaultVal;
        }

        private static string Escape(string s)
        {
            if (s == null) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        private static string Unescape(string s)
        {
            if (s == null) return "";
            return s.Replace("\\\"", "\"").Replace("\\\\", "\\");
        }
    }
}
