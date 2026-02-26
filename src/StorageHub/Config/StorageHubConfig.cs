using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using TerrariaModder.Core.Logging;

namespace StorageHub.Config
{
    /// <summary>
    /// Per-character per-world configuration for Storage Hub.
    ///
    /// Design rationale:
    /// - Tier/chests/relays/stations are world-specific (different worlds have different chests)
    /// - Special unlocks are character achievements (carry across worlds)
    /// - This matches how Terraria itself handles character vs world progression
    ///
    /// Storage paths:
    /// - World data: TerrariaModder/mods/storage-hub/worlds/{world-name}/{character-name}.json
    /// - Character data: TerrariaModder/mods/storage-hub/characters/{character-name}.json
    /// </summary>
    public class StorageHubConfig
    {
        private readonly ILogger _log;
        private readonly string _modFolder;

        // World-specific data (per character per world)
        public int Tier { get; set; } = 0;
        public List<ChestPosition> RegisteredChests { get; set; } = new List<ChestPosition>();
        public List<RelayPosition> Relays { get; set; } = new List<RelayPosition>();
        public HashSet<int> RememberedStations { get; set; } = new HashSet<int>();
        /// <summary>
        /// Whether station memory is enabled (only affects Tier 3+).
        /// When enabled, visited crafting stations are remembered.
        /// </summary>
        public bool StationMemoryEnabled { get; set; } = true;
        public HashSet<int> FavoriteItems { get; set; } = new HashSet<int>();
        public SortMode ItemSortMode { get; set; } = SortMode.Name;
        public CategoryFilter ItemCategoryFilter { get; set; } = CategoryFilter.All;

        /// <summary>
        /// If true, hotbar slots 0-9 are never auto-deposited.
        /// </summary>
        public bool HotbarProtection { get; set; } = true;

        /// <summary>
        /// If true, Network tab shows all 39 stations (including unavailable ones dimmed).
        /// If false, only shows available stations.
        /// </summary>
        public bool ShowStationSpoilers { get; set; } = false;

        // Character-specific data (global across worlds)
        public Dictionary<string, bool> SpecialUnlocks { get; set; } = new Dictionary<string, bool>();

        // Current world/character names for path construction
        private string _currentWorldName;
        private string _currentCharacterName;

        public StorageHubConfig(ILogger log, string modFolder)
        {
            _log = log;
            _modFolder = modFolder;

            // Initialize all special unlocks to false
            foreach (var key in Config.SpecialUnlocks.Definitions.Keys)
            {
                SpecialUnlocks[key] = false;
            }
        }

        /// <summary>
        /// Load configuration for a specific world and character.
        /// Called when world loads. Will recover from backup if main file is corrupted.
        /// </summary>
        public void Load(string worldName, string characterName)
        {
            _currentWorldName = SanitizeFileName(worldName);
            _currentCharacterName = SanitizeFileName(characterName);
            _log.Info($"[Config] Loading config for world='{worldName}' (sanitized: '{_currentWorldName}'), char='{characterName}' (sanitized: '{_currentCharacterName}')");

            // Load world-specific data
            var worldConfigPath = GetWorldConfigPath();
            _log.Debug($"[Config] World config path: {worldConfigPath}");

            if (!TryLoadConfigFile(worldConfigPath, ParseWorldConfig, "world"))
            {
                _log.Warn($"[Config] No existing world config at {worldConfigPath}, using defaults");
            }
            else
            {
                _log.Info($"[Config] Loaded: Tier={Tier}, Chests={RegisteredChests.Count}, Stations={RememberedStations.Count}");
            }

            // Load character-specific data
            var charConfigPath = GetCharacterConfigPath();
            if (!TryLoadConfigFile(charConfigPath, ParseCharacterConfig, "character"))
            {
                _log.Debug($"[Config] No existing character config at {charConfigPath}, using defaults");
            }
            else
            {
                _log.Info($"Loaded character config from {charConfigPath}");
            }
        }

        /// <summary>
        /// Try to load a config file. If corrupted/empty, try loading from backup.
        /// </summary>
        private bool TryLoadConfigFile(string path, Action<string> parser, string configType)
        {
            // Try main file first
            if (File.Exists(path))
            {
                try
                {
                    var json = File.ReadAllText(path);
                    if (!string.IsNullOrWhiteSpace(json) && json.Contains("{"))
                    {
                        parser(json);
                        _log.Debug($"[Config] Loaded {configType} config from {path}");
                        return true;
                    }
                    else
                    {
                        _log.Warn($"[Config] {configType} config file is empty or invalid, trying backup...");
                    }
                }
                catch (Exception ex)
                {
                    _log.Warn($"[Config] Failed to load {configType} config: {ex.Message}, trying backup...");
                }
            }

            // Try backup if main file failed or doesn't exist
            string backupPath = path + ".bak";
            if (File.Exists(backupPath))
            {
                try
                {
                    var json = File.ReadAllText(backupPath);
                    if (!string.IsNullOrWhiteSpace(json) && json.Contains("{"))
                    {
                        parser(json);
                        _log.Info($"[Config] Recovered {configType} config from backup at {backupPath}");

                        // Restore backup to main file
                        try
                        {
                            File.Copy(backupPath, path, overwrite: true);
                            _log.Info($"[Config] Restored backup to main config file");
                        }
                        catch (Exception copyEx)
                        {
                            _log.Warn($"[Config] Could not restore backup: {copyEx.Message}");
                        }

                        return true;
                    }
                }
                catch (Exception ex)
                {
                    _log.Error($"[Config] Failed to load {configType} backup: {ex.Message}");
                }
            }

            return false;
        }

        /// <summary>
        /// Save configuration to disk.
        /// Should be called when world unloads or when significant changes are made.
        /// Uses atomic write with backup to prevent corruption on crash.
        /// </summary>
        public void Save()
        {
            if (string.IsNullOrEmpty(_currentWorldName) || string.IsNullOrEmpty(_currentCharacterName))
            {
                _log.Warn("Cannot save config: world or character name not set");
                return;
            }

            // Save world-specific data
            try
            {
                var worldConfigPath = GetWorldConfigPath();
                SafeWriteFile(worldConfigPath, SerializeWorldConfig());
                _log.Debug($"Saved world config to {worldConfigPath}");
            }
            catch (Exception ex)
            {
                _log.Error($"Failed to save world config: {ex.Message}");
            }

            // Save character-specific data
            try
            {
                var charConfigPath = GetCharacterConfigPath();
                SafeWriteFile(charConfigPath, SerializeCharacterConfig());
                _log.Debug($"Saved character config to {charConfigPath}");
            }
            catch (Exception ex)
            {
                _log.Error($"Failed to save character config: {ex.Message}");
            }
        }

        /// <summary>
        /// Atomic file write with backup to prevent corruption.
        /// Writes to temp file, then renames (atomic on most filesystems).
        /// Keeps one backup (.bak) of the previous version.
        /// </summary>
        private void SafeWriteFile(string filePath, string content)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(filePath));

            string tempPath = filePath + ".tmp";
            string backupPath = filePath + ".bak";

            // Write to temp file first
            File.WriteAllText(tempPath, content);

            // If original exists, create backup
            if (File.Exists(filePath))
            {
                // Delete old backup if exists
                if (File.Exists(backupPath))
                {
                    File.Delete(backupPath);
                }
                // Rename current to backup
                File.Move(filePath, backupPath);
            }

            // Rename temp to final (atomic on most filesystems)
            File.Move(tempPath, filePath);
        }

        /// <summary>
        /// Reset to defaults. Called when starting fresh.
        /// </summary>
        public void Reset()
        {
            Tier = 0;
            RegisteredChests.Clear();
            Relays.Clear();
            RememberedStations.Clear();
            StationMemoryEnabled = true;
            FavoriteItems.Clear();
            HotbarProtection = true;
            ShowStationSpoilers = false;
            ItemSortMode = SortMode.Name;
            ItemCategoryFilter = CategoryFilter.All;
        }

        private string GetWorldConfigPath()
        {
            return Path.Combine(_modFolder, "worlds", _currentWorldName, $"{_currentCharacterName}.json");
        }

        private string GetCharacterConfigPath()
        {
            return Path.Combine(_modFolder, "characters", $"{_currentCharacterName}.json");
        }

        private string SanitizeFileName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "unknown";

            var invalid = Path.GetInvalidFileNameChars();
            var result = new StringBuilder();
            foreach (var c in name)
            {
                if (Array.IndexOf(invalid, c) < 0)
                    result.Append(c);
                else
                    result.Append('_');
            }
            return result.ToString();
        }

        // Simple JSON serialization (no external dependencies)
        private string SerializeWorldConfig()
        {
            var sb = new StringBuilder();
            sb.AppendLine("{");
            sb.AppendLine($"  \"tier\": {Tier},");

            // Registered chests
            sb.AppendLine("  \"registeredChests\": [");
            for (int i = 0; i < RegisteredChests.Count; i++)
            {
                var chest = RegisteredChests[i];
                var comma = i < RegisteredChests.Count - 1 ? "," : "";
                sb.AppendLine($"    {{\"x\": {chest.X}, \"y\": {chest.Y}}}{comma}");
            }
            sb.AppendLine("  ],");

            // Relays
            sb.AppendLine("  \"relays\": [");
            for (int i = 0; i < Relays.Count; i++)
            {
                var relay = Relays[i];
                var comma = i < Relays.Count - 1 ? "," : "";
                sb.AppendLine($"    {{\"x\": {relay.X}, \"y\": {relay.Y}}}{comma}");
            }
            sb.AppendLine("  ],");

            // Remembered stations
            sb.Append("  \"rememberedStations\": [");
            var stationsList = new List<int>(RememberedStations);
            sb.Append(string.Join(", ", stationsList));
            sb.AppendLine("],");

            // Favorites
            sb.Append("  \"favorites\": [");
            var favoritesList = new List<int>(FavoriteItems);
            sb.Append(string.Join(", ", favoritesList));
            sb.AppendLine("],");

            // Station memory enabled (Tier 3+ feature)
            sb.AppendLine($"  \"stationMemoryEnabled\": {(StationMemoryEnabled ? "true" : "false")},");

            // Hotbar protection
            sb.AppendLine($"  \"hotbarProtection\": {(HotbarProtection ? "true" : "false")},");

            // Station spoilers (Network tab)
            sb.AppendLine($"  \"showStationSpoilers\": {(ShowStationSpoilers ? "true" : "false")},");

            // UI preferences
            sb.AppendLine($"  \"itemSortMode\": \"{ItemSortMode}\",");
            sb.AppendLine($"  \"itemCategoryFilter\": \"{ItemCategoryFilter}\"");

            sb.AppendLine("}");
            return sb.ToString();
        }

        private string SerializeCharacterConfig()
        {
            var sb = new StringBuilder();
            sb.AppendLine("{");
            sb.AppendLine("  \"specialUnlocks\": {");

            var unlockKeys = new List<string>(SpecialUnlocks.Keys);
            for (int i = 0; i < unlockKeys.Count; i++)
            {
                var key = unlockKeys[i];
                var value = SpecialUnlocks[key] ? "true" : "false";
                var comma = i < unlockKeys.Count - 1 ? "," : "";
                sb.AppendLine($"    \"{key}\": {value}{comma}");
            }

            sb.AppendLine("  },");
            sb.AppendLine("  \"dataVersion\": 2");
            sb.AppendLine("}");
            return sb.ToString();
        }

        private void ParseWorldConfig(string json)
        {
            // Simple JSON parsing without external dependencies
            // Parse tier
            var tierMatch = System.Text.RegularExpressions.Regex.Match(json, @"""tier""\s*:\s*(\d+)");
            if (tierMatch.Success && int.TryParse(tierMatch.Groups[1].Value, out int tier))
            {
                Tier = tier;
            }

            // Parse registered chests
            RegisteredChests.Clear();
            var chestsMatch = System.Text.RegularExpressions.Regex.Match(json, @"""registeredChests""\s*:\s*\[(.*?)\]", System.Text.RegularExpressions.RegexOptions.Singleline);
            if (chestsMatch.Success)
            {
                var chestMatches = System.Text.RegularExpressions.Regex.Matches(chestsMatch.Groups[1].Value, @"\{[^}]*""x""\s*:\s*(\d+)[^}]*""y""\s*:\s*(\d+)[^}]*\}");
                foreach (System.Text.RegularExpressions.Match m in chestMatches)
                {
                    if (int.TryParse(m.Groups[1].Value, out int x) && int.TryParse(m.Groups[2].Value, out int y))
                    {
                        RegisteredChests.Add(new ChestPosition(x, y));
                    }
                }
            }

            // Parse relays
            Relays.Clear();
            var relaysMatch = System.Text.RegularExpressions.Regex.Match(json, @"""relays""\s*:\s*\[(.*?)\]", System.Text.RegularExpressions.RegexOptions.Singleline);
            if (relaysMatch.Success)
            {
                var relayMatches = System.Text.RegularExpressions.Regex.Matches(relaysMatch.Groups[1].Value, @"\{[^}]*""x""\s*:\s*(\d+)[^}]*""y""\s*:\s*(\d+)[^}]*\}");
                foreach (System.Text.RegularExpressions.Match m in relayMatches)
                {
                    if (int.TryParse(m.Groups[1].Value, out int x) && int.TryParse(m.Groups[2].Value, out int y))
                    {
                        Relays.Add(new RelayPosition(x, y));
                    }
                }
            }

            // Parse remembered stations
            RememberedStations.Clear();
            var stationsMatch = System.Text.RegularExpressions.Regex.Match(json, @"""rememberedStations""\s*:\s*\[([\d,\s]*)\]");
            if (stationsMatch.Success)
            {
                var nums = System.Text.RegularExpressions.Regex.Matches(stationsMatch.Groups[1].Value, @"\d+");
                foreach (System.Text.RegularExpressions.Match m in nums)
                {
                    if (int.TryParse(m.Value, out int stationId))
                    {
                        RememberedStations.Add(stationId);
                    }
                }
            }

            // Parse favorites
            FavoriteItems.Clear();
            var favoritesMatch = System.Text.RegularExpressions.Regex.Match(json, @"""favorites""\s*:\s*\[([\d,\s]*)\]");
            if (favoritesMatch.Success)
            {
                var nums = System.Text.RegularExpressions.Regex.Matches(favoritesMatch.Groups[1].Value, @"\d+");
                foreach (System.Text.RegularExpressions.Match m in nums)
                {
                    if (int.TryParse(m.Value, out int itemId))
                    {
                        FavoriteItems.Add(itemId);
                    }
                }
            }

            // Parse station memory enabled
            var stationMemoryMatch = System.Text.RegularExpressions.Regex.Match(json, @"""stationMemoryEnabled""\s*:\s*(true|false)");
            if (stationMemoryMatch.Success)
            {
                StationMemoryEnabled = stationMemoryMatch.Groups[1].Value == "true";
            }

            // Parse hotbar protection
            var hotbarMatch = System.Text.RegularExpressions.Regex.Match(json, @"""hotbarProtection""\s*:\s*(true|false)");
            if (hotbarMatch.Success)
            {
                HotbarProtection = hotbarMatch.Groups[1].Value == "true";
            }

            // Parse station spoilers
            var spoilersMatch = System.Text.RegularExpressions.Regex.Match(json, @"""showStationSpoilers""\s*:\s*(true|false)");
            if (spoilersMatch.Success)
            {
                ShowStationSpoilers = spoilersMatch.Groups[1].Value == "true";
            }

            // Parse UI preferences
            var sortMatch = System.Text.RegularExpressions.Regex.Match(json, @"""itemSortMode""\s*:\s*""(\w+)""");
            if (sortMatch.Success && Enum.TryParse<SortMode>(sortMatch.Groups[1].Value, out var sortMode))
            {
                ItemSortMode = sortMode;
            }

            var filterMatch = System.Text.RegularExpressions.Regex.Match(json, @"""itemCategoryFilter""\s*:\s*""(\w+)""");
            if (filterMatch.Success && Enum.TryParse<CategoryFilter>(filterMatch.Groups[1].Value, out var catFilter))
            {
                ItemCategoryFilter = catFilter;
            }
        }

        private void ParseCharacterConfig(string json)
        {
            // Parse special unlocks
            foreach (var key in Config.SpecialUnlocks.Definitions.Keys)
            {
                var pattern = $@"""{key}""\s*:\s*(true|false)";
                var match = System.Text.RegularExpressions.Regex.Match(json, pattern);
                if (match.Success)
                {
                    SpecialUnlocks[key] = match.Groups[1].Value == "true";
                }
            }

            // Migration: "decrafting" was renamed to "shimmer" (unified unlock)
            // If old config had decrafting unlocked, transfer to shimmer
            var decraftingPattern = @"""decrafting""\s*:\s*(true|false)";
            var decraftingMatch = System.Text.RegularExpressions.Regex.Match(json, decraftingPattern);
            if (decraftingMatch.Success && decraftingMatch.Groups[1].Value == "true")
            {
                SpecialUnlocks["shimmer"] = true;
                _log.Debug("[Config] Migrated 'decrafting' unlock to 'shimmer'");
            }

        }

        /// <summary>
        /// Check if a special unlock is enabled.
        /// </summary>
        public bool HasSpecialUnlock(string key)
        {
            return SpecialUnlocks.TryGetValue(key, out bool value) && value;
        }

        /// <summary>
        /// Enable a special unlock (after consuming required items).
        /// </summary>
        public void SetSpecialUnlock(string key, bool value)
        {
            if (SpecialUnlocks.ContainsKey(key))
            {
                SpecialUnlocks[key] = value;
            }
        }
    }

    /// <summary>
    /// Represents a chest position in the world.
    /// </summary>
    public struct ChestPosition
    {
        public int X { get; }
        public int Y { get; }

        public ChestPosition(int x, int y)
        {
            X = x;
            Y = y;
        }

        public override bool Equals(object obj)
        {
            return obj is ChestPosition other && X == other.X && Y == other.Y;
        }

        public override int GetHashCode()
        {
            return X * 31 + Y;
        }
    }

    /// <summary>
    /// Represents a relay position in the world.
    /// </summary>
    public struct RelayPosition
    {
        public int X { get; }
        public int Y { get; }

        public RelayPosition(int x, int y)
        {
            X = x;
            Y = y;
        }

        public override bool Equals(object obj)
        {
            return obj is RelayPosition other && X == other.X && Y == other.Y;
        }

        public override int GetHashCode()
        {
            return X * 31 + Y;
        }
    }

    /// <summary>
    /// Item sorting modes.
    /// </summary>
    public enum SortMode
    {
        Name,       // Alphabetical by name
        Stack,      // By stack size (highest first)
        Rarity,     // By rarity (highest first)
        Type,       // By item type ID
        Recent,     // Most recently registered first
        MagicDefault // MagicStorage-style grouped ordering
    }

    /// <summary>
    /// Item category filters.
    /// </summary>
    public enum CategoryFilter
    {
        All,
        Weapons,
        Tools,
        Armor,
        Accessories,
        Consumables,
        Placeable,
        Materials,
        Misc
    }
}
