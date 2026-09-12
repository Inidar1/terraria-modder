using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using TerrariaModder.Core.IO;
using TerrariaModder.Core.Logging;

namespace SeedLab
{
    /// <summary>
    /// Manages world-generation feature group overrides with two-state logic:
    ///   null  = unchecked (don't interfere with this feature)
    ///   true  = checked (force feature ON during world generation)
    ///
    /// Overrides are applied via WorldGenResetPatch (global flag) and WorldGenPassPatch (per-pass).
    /// State persists to state-worldgen.json in the mod folder.
    /// </summary>
    public class WorldGenOverrideManager
    {
        private readonly ILogger _log;
        private readonly string _configPath;

        // groupId → override state (null = unchecked, true = checked/force on)
        private readonly Dictionary<string, bool?> _overrides = new Dictionary<string, bool?>();

        // Lookup tables
        private readonly Dictionary<string, WGSeedDef> _seedsById;
        private readonly Dictionary<string, WGFeatureGroupDef> _groupsById;
        private readonly Dictionary<string, WGSeedDef> _groupToSeed;
        private readonly Dictionary<string, List<GroupSeedPair>> _passToGroups;

        // Collapsed state for UI (persisted)
        private readonly HashSet<string> _collapsedSeeds = new HashSet<string>();

        // Custom presets
        private readonly string _presetsPath;
        private readonly Dictionary<string, string[]> _customPresets = new Dictionary<string, string[]>();

        // Standard categories (cross-seed grouping)
        public static readonly string[] StandardCategories = new[]
        {
            "Terrain", "Structures", "Ores & Resources", "Loot & Chests",
            "Enemies & Spawns", "Traps & Hazards", "Biomes", "Liquids",
            "Visual", "Events", "Trees & Plants", "Misc"
        };

        public WorldGenOverrideManager(ILogger log, string configPath)
        {
            _log = log;
            _configPath = configPath;

            WorldGenFeatureCatalog.BuildLookups(out _seedsById, out _groupsById, out _groupToSeed, out _passToGroups);

            // Initialize all groups to default (null = unchecked)
            foreach (var kvp in _groupsById)
                _overrides[kvp.Key] = null;

            Load();

            _presetsPath = Path.Combine(Path.GetDirectoryName(configPath) ?? ".", "presets-worldgen.json");
            LoadPresets();
        }

        #region State Queries

        /// <summary>Whether any overrides are active (checked).</summary>
        public bool HasOverrides
        {
            get
            {
                foreach (var kvp in _overrides)
                    if (kvp.Value == true) return true;
                return false;
            }
        }

        /// <summary>Count of active (checked) overrides.</summary>
        public int ActiveCount
        {
            get
            {
                int count = 0;
                foreach (var kvp in _overrides)
                    if (kvp.Value == true) count++;
                return count;
            }
        }

        /// <summary>Get the override state for a feature group (null=unchecked, true=checked).</summary>
        public bool? GetGroupOverride(string groupId)
        {
            return _overrides.TryGetValue(groupId, out var val) ? val : null;
        }

        /// <summary>Whether a group is checked (force on).</summary>
        public bool IsGroupChecked(string groupId)
        {
            return _overrides.TryGetValue(groupId, out var val) && val == true;
        }

        /// <summary>Whether any group in a seed is checked.</summary>
        public bool HasAnyCheckedForSeed(string seedId)
        {
            if (!_seedsById.TryGetValue(seedId, out var seed)) return false;
            foreach (var group in seed.Groups)
                if (_overrides.TryGetValue(group.Id, out var val) && val == true) return true;
            return false;
        }

        /// <summary>Whether all groups in a seed are checked.</summary>
        public bool IsAllCheckedForSeed(string seedId)
        {
            if (!_seedsById.TryGetValue(seedId, out var seed)) return false;
            foreach (var group in seed.Groups)
            {
                if (!_overrides.TryGetValue(group.Id, out var val) || val != true) return false;
            }
            return true;
        }

        /// <summary>
        /// Determine what the seed flag should be set to by WorldGenResetPatch.
        /// Returns null if no groups are checked (don't touch the flag).
        /// Returns true if any group is checked.
        /// </summary>
        public bool? ShouldEnableSeedFlag(string seedId)
        {
            if (!_seedsById.TryGetValue(seedId, out var seed)) return null;

            foreach (var group in seed.Groups)
            {
                if (_overrides.TryGetValue(group.Id, out var val) && val == true)
                    return true;
            }

            return null; // no groups checked — don't interfere
        }

        /// <summary>
        /// Get what a seed flag should be for a specific gen pass.
        /// This implements the flag leak fix: for seeds with any checked group,
        /// only passes mapped to a checked group get the flag set to true.
        /// Unmapped or unchecked passes get false (suppressed).
        ///
        /// Returns null if no groups for this seed are checked (don't interfere).
        /// Returns true if a checked group maps to this pass.
        /// Returns false if the seed has checked groups but none map to this pass (leak suppression).
        /// </summary>
        public bool? GetFlagOverrideForPass(string passName, string seedId)
        {
            if (!_seedsById.TryGetValue(seedId, out var seed)) return null;

            // First check if this seed has ANY checked groups
            bool seedHasCheckedGroups = false;
            foreach (var group in seed.Groups)
            {
                if (_overrides.TryGetValue(group.Id, out var val) && val == true)
                {
                    seedHasCheckedGroups = true;
                    break;
                }
            }

            if (!seedHasCheckedGroups) return null; // no interference

            // Composite seed groups such as Zenith own the whole generation pipeline.
            foreach (var group in seed.Groups)
            {
                if (group.AppliesToAllPasses && _overrides.TryGetValue(group.Id, out var allPasses) && allPasses == true)
                    return true;
            }

            // Seed has checked groups. Check if any checked group maps to this pass.
            if (_passToGroups.TryGetValue(passName, out var pairs))
            {
                foreach (var pair in pairs)
                {
                    if (pair.Seed.Id != seedId) continue;
                    if (_overrides.TryGetValue(pair.Group.Id, out var val) && val == true)
                        return true; // checked group maps to this pass
                }
            }

            // Seed has checked groups but none map to this pass → suppress flag
            return false;
        }

        /// <summary>
        /// Get the override for a secret seed's finalize processing.
        /// Returns null if unchecked (use original .Enabled state).
        /// Returns true if checked (force run).
        /// </summary>
        public bool? GetSecretSeedFinalizeOverride(string groupId)
        {
            return GetGroupOverride(groupId);
        }

        #endregion

        #region State Mutations

        /// <summary>Toggle a group: null → true → null (two-state).</summary>
        public void ToggleGroupOverride(string groupId)
        {
            if (!_overrides.ContainsKey(groupId)) return;
            var current = _overrides[groupId];
            _overrides[groupId] = (current == true) ? (bool?)null : true;
            Save();
        }

        /// <summary>Set a specific group override value (true=checked, false/null=unchecked).</summary>
        public void SetGroupOverride(string groupId, bool? value)
        {
            if (!_overrides.ContainsKey(groupId)) return;
            // Two-state: only null and true are valid. false maps to null.
            _overrides[groupId] = (value == true) ? true : (bool?)null;
            Save();
        }

        /// <summary>Set a group checked state (true=checked, false=unchecked).</summary>
        public void SetGroupChecked(string groupId, bool isChecked)
        {
            if (!_overrides.ContainsKey(groupId)) return;
            _overrides[groupId] = isChecked ? true : (bool?)null;
            Save();
        }

        /// <summary>Check or uncheck all groups in a seed.</summary>
        public void SetAllForSeed(string seedId, bool isChecked)
        {
            if (!_seedsById.TryGetValue(seedId, out var seed)) return;
            foreach (var group in seed.Groups)
                _overrides[group.Id] = isChecked ? true : (bool?)null;
            Save();
        }

        /// <summary>Toggle all groups in a seed: if any unchecked → all checked, else all unchecked.</summary>
        public void ToggleSeedOverride(string seedId)
        {
            if (!_seedsById.TryGetValue(seedId, out var seed)) return;

            bool allChecked = IsAllCheckedForSeed(seedId);
            foreach (var group in seed.Groups)
                _overrides[group.Id] = allChecked ? (bool?)null : true;
            Save();
        }

        /// <summary>Clear all overrides back to unchecked.</summary>
        public void ClearAll()
        {
            foreach (var key in new List<string>(_overrides.Keys))
                _overrides[key] = null;
            Save();
            _log.Info("[SeedLab] Cleared all world-gen overrides");
        }

        #endregion

        #region Category & EasyGroup Queries

        /// <summary>
        /// Get all groups organized by category (cross-seed view).
        /// Returns category → list of (seed, group) pairs.
        /// </summary>
        public Dictionary<string, List<GroupSeedPair>> GetGroupsByCategory()
        {
            var result = new Dictionary<string, List<GroupSeedPair>>();
            foreach (var seed in WorldGenFeatureCatalog.Seeds)
            {
                foreach (var group in seed.Groups)
                {
                    string cat = group.Category ?? "Misc";
                    if (!result.TryGetValue(cat, out var list))
                    {
                        list = new List<GroupSeedPair>();
                        result[cat] = list;
                    }
                    list.Add(new GroupSeedPair { Group = group, Seed = seed });
                }
            }
            return result;
        }

        /// <summary>
        /// Get all easy groups organized by category (cross-seed view, Easy mode).
        /// Groups with the same EasyGroup value within a seed are collapsed.
        /// Cross-category easy groups are placed in their primary category.
        /// </summary>
        public Dictionary<string, List<CategoryEasyPair>> GetEasyGroupsByCategory()
        {
            var result = new Dictionary<string, List<CategoryEasyPair>>();
            foreach (var seed in WorldGenFeatureCatalog.Seeds)
            {
                var easyGroups = GetEasyGroups(seed.Id);
                foreach (var eg in easyGroups)
                {
                    string cat = eg.Category ?? "Misc";
                    if (!result.TryGetValue(cat, out var list))
                    {
                        list = new List<CategoryEasyPair>();
                        result[cat] = list;
                    }
                    list.Add(new CategoryEasyPair { Seed = seed, EasyGroup = eg });
                }
            }
            return result;
        }

        /// <summary>
        /// Get easy-mode groups for a seed. Groups with the same EasyGroup value
        /// are collapsed into one entry. Returns list of (easyGroupName, groupIds[]).
        /// </summary>
        public List<EasyGroupEntry> GetEasyGroups(string seedId)
        {
            if (!_seedsById.TryGetValue(seedId, out var seed)) return new List<EasyGroupEntry>();

            var byEasyGroup = new Dictionary<string, List<WGFeatureGroupDef>>();
            foreach (var group in seed.Groups)
            {
                string key = group.EasyGroup ?? group.Id;
                if (!byEasyGroup.TryGetValue(key, out var list))
                {
                    list = new List<WGFeatureGroupDef>();
                    byEasyGroup[key] = list;
                }
                list.Add(group);
            }

            var result = new List<EasyGroupEntry>();
            foreach (var kvp in byEasyGroup)
            {
                var groups = kvp.Value;
                // Use first group's display name as the easy group name, but combine descriptions
                string name = groups[0].DisplayName;
                string category = groups[0].Category;
                if (groups.Count > 1)
                {
                    // Combine: "Terrain + Structures"
                    var names = new List<string>();
                    foreach (var g in groups)
                        if (!names.Contains(g.DisplayName)) names.Add(g.DisplayName);
                    name = string.Join(" + ", names);
                }

                var ids = new string[groups.Count];
                for (int i = 0; i < groups.Count; i++)
                    ids[i] = groups[i].Id;

                bool allChecked = true;
                bool anyChecked = false;
                foreach (var g in groups)
                {
                    bool c = IsGroupChecked(g.Id);
                    if (c) anyChecked = true;
                    else allChecked = false;
                }

                result.Add(new EasyGroupEntry
                {
                    EasyGroupKey = kvp.Key,
                    DisplayName = name,
                    Category = category,
                    Description = groups[0].Description,
                    GroupIds = ids,
                    AllChecked = allChecked,
                    AnyChecked = anyChecked
                });
            }

            return result;
        }

        /// <summary>Check or uncheck all groups in an easy group.</summary>
        public void SetEasyGroupChecked(string[] groupIds, bool isChecked)
        {
            foreach (var id in groupIds)
            {
                if (_overrides.ContainsKey(id))
                    _overrides[id] = isChecked ? true : (bool?)null;
            }
            Save();
        }

        #endregion

        #region Conflict Detection

        /// <summary>
        /// Get active conflict group IDs for a given group.
        /// Returns groups where both this group and a conflicting group are checked.
        /// </summary>
        public List<string> GetActiveConflicts(string groupId)
        {
            if (!_groupsById.TryGetValue(groupId, out var group)) return null;
            if (group.Conflicts == null || group.Conflicts.Length == 0) return null;

            if (!IsGroupChecked(groupId)) return null; // Only warn when both are checked

            List<string> active = null;
            foreach (var conflictId in group.Conflicts)
            {
                if (IsGroupChecked(conflictId))
                {
                    if (active == null) active = new List<string>();
                    active.Add(conflictId);
                }
            }
            return active;
        }

        /// <summary>Get display name for a group ID.</summary>
        public string GetGroupDisplayName(string groupId)
        {
            return _groupsById.TryGetValue(groupId, out var g) ? g.DisplayName : groupId;
        }

        /// <summary>Get the seed display name that owns a group.</summary>
        public string GetSeedDisplayNameForGroup(string groupId)
        {
            return _groupToSeed.TryGetValue(groupId, out var s) ? s.DisplayName : "";
        }

        #endregion

        #region UI Collapsed State

        public bool IsSeedCollapsed(string seedId) => _collapsedSeeds.Contains(seedId);

        public void ToggleSeedCollapsed(string seedId)
        {
            if (_collapsedSeeds.Contains(seedId))
                _collapsedSeeds.Remove(seedId);
            else
                _collapsedSeeds.Add(seedId);
        }

        #endregion

        #region Accessors

        public Dictionary<string, WGSeedDef> SeedsById => _seedsById;
        public Dictionary<string, WGFeatureGroupDef> GroupsById => _groupsById;
        public Dictionary<string, WGSeedDef> GroupToSeed => _groupToSeed;
        public Dictionary<string, List<GroupSeedPair>> PassToGroups => _passToGroups;

        #endregion

        #region Persistence

        private void Save()
        {
            try
            {
                var data = new Dictionary<string, object>();
                foreach (var entry in _overrides)
                {
                    if (entry.Value == true)
                        data[entry.Key] = true;
                }

                AtomicFile.WriteText(_configPath, SidecarJson.Serialize(data), new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                _log.Error($"[SeedLab] Failed to save world-gen overrides: {ex.Message}");
            }
        }

        private void Load()
        {
            if (!File.Exists(_configPath)) return;

            try
            {
                var data = SidecarJson.Deserialize(SidecarJson.ReadText(_configPath));
                foreach (var entry in data)
                {
                    if (!_overrides.ContainsKey(entry.Key)) continue;
                    _overrides[entry.Key] = entry.Value is bool enabled && enabled ? true : (bool?)null;
                }

                _log.Info("[SeedLab] Loaded world-gen overrides");
            }
            catch (Exception ex)
            {
                _log.Error($"[SeedLab] Failed to load world-gen overrides: {ex.Message}");
            }
        }

        #endregion

        #region Custom Presets

        public IReadOnlyDictionary<string, string[]> CustomPresets => _customPresets;

        public List<string> GetCheckedGroupIds()
        {
            var ids = new List<string>();
            foreach (var kvp in _overrides)
                if (kvp.Value == true) ids.Add(kvp.Key);
            return ids;
        }

        public void ApplyCheckedGroupIds(IEnumerable<string> groupIds)
        {
            foreach (var key in new List<string>(_overrides.Keys))
                _overrides[key] = null;
            foreach (var id in groupIds)
                if (_overrides.ContainsKey(id)) _overrides[id] = true;
            Save();
        }

        public void SaveCustomPreset(string name)
        {
            _customPresets[name] = GetCheckedGroupIds().ToArray();
            SavePresets();
            _log.Info($"[SeedLab] Saved world-gen preset '{name}'");
        }

        public void ApplyCustomPreset(string name)
        {
            if (!_customPresets.TryGetValue(name, out var ids)) return;
            ApplyCheckedGroupIds(ids);
            _log.Info($"[SeedLab] Applied world-gen preset '{name}'");
        }

        public void DeleteCustomPreset(string name)
        {
            if (_customPresets.Remove(name))
            {
                SavePresets();
                _log.Info($"[SeedLab] Deleted world-gen preset '{name}'");
            }
        }

        private void SavePresets()
        {
            try
            {
                var data = new Dictionary<string, object>();
                foreach (var entry in _customPresets)
                    data[entry.Key] = entry.Value;

                AtomicFile.WriteText(_presetsPath, SidecarJson.Serialize(data), new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                _log.Error($"[SeedLab] Failed to save presets: {ex.Message}");
            }
        }

        private void LoadPresets()
        {
            if (!File.Exists(_presetsPath)) return;
            try
            {
                var data = SidecarJson.Deserialize(SidecarJson.ReadText(_presetsPath));
                foreach (var entry in data)
                {
                    if (!(entry.Value is System.Collections.IList rawIds))
                        continue;

                    var ids = new List<string>();
                    foreach (object rawId in rawIds)
                    {
                        if (rawId is string id)
                            ids.Add(id);
                    }
                    _customPresets[entry.Key] = ids.ToArray();
                }
                if (_customPresets.Count > 0)
                    _log.Info($"[SeedLab] Loaded {_customPresets.Count} custom world-gen presets");
            }
            catch (Exception ex)
            {
                _log.Error($"[SeedLab] Failed to load presets: {ex.Message}");
            }
        }

        #endregion
    }

    public struct EasyGroupEntry
    {
        public string EasyGroupKey;
        public string DisplayName;
        public string Category;
        public string Description;
        public string[] GroupIds;
        public bool AllChecked;
        public bool AnyChecked;
    }

    public struct CategoryEasyPair
    {
        public WGSeedDef Seed;
        public EasyGroupEntry EasyGroup;
    }
}
