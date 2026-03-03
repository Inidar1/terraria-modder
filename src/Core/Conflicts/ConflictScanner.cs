using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using TerrariaModder.Core.Input;
using TerrariaModder.Core.Logging;

namespace TerrariaModder.Core.Conflicts
{
    /// <summary>
    /// Scans runtime Harmony patches and keybinds to detect conflicts between mods.
    /// </summary>
    public static class ConflictScanner
    {
        private static ILogger _log;

        public static void Initialize(ILogger log)
        {
            _log = log;
        }

        /// <summary>
        /// Run a full conflict scan. Returns a report with patch conflicts,
        /// keybind conflicts, and current load order.
        /// </summary>
        public static ConflictReport Scan()
        {
            var report = new ConflictReport();

            try
            {
                ScanPatchConflicts(report);
            }
            catch (Exception ex)
            {
                _log?.Warn($"[ConflictScanner] Patch scan failed: {ex.Message}");
            }

            try
            {
                report.KeybindConflicts = KeybindManager.GetConflicts();
            }
            catch (Exception ex)
            {
                _log?.Warn($"[ConflictScanner] Keybind scan failed: {ex.Message}");
            }

            try
            {
                foreach (var mod in PluginLoader.Mods)
                {
                    report.LoadOrder.Add(mod.Manifest.Id);
                }
            }
            catch (Exception ex)
            {
                _log?.Warn($"[ConflictScanner] Load order read failed: {ex.Message}");
            }

            // Count severities
            foreach (var pc in report.PatchConflicts)
            {
                switch (pc.Severity)
                {
                    case ConflictSeverity.High: report.HighCount++; break;
                    case ConflictSeverity.Medium: report.MediumCount++; break;
                    case ConflictSeverity.Low: report.LowCount++; break;
                }
            }

            _log?.Info($"[ConflictScanner] Scan complete: {report.PatchConflicts.Count} patch conflicts " +
                       $"({report.HighCount} high, {report.MediumCount} medium, {report.LowCount} low), " +
                       $"{report.KeybindConflicts.Count} keybind conflicts");

            return report;
        }

        private static void ScanPatchConflicts(ConflictReport report)
        {
            // Build mod ID lookup from Harmony owner prefix
            var modLookup = BuildModLookup();

            // Get all methods patched by any Harmony instance
            var patchedMethods = Harmony.GetAllPatchedMethods();

            foreach (var method in patchedMethods)
            {
                HarmonyLib.Patches info;
                try
                {
                    info = Harmony.GetPatchInfo(method);
                }
                catch
                {
                    continue;
                }

                if (info == null) continue;

                // Collect all patch owners for this method
                var ownerPatches = new Dictionary<string, List<PatchOwner>>();

                CollectPatches(info.Prefixes, "Prefix", modLookup, ownerPatches);
                CollectPatches(info.Postfixes, "Postfix", modLookup, ownerPatches);
                CollectPatches(info.Transpilers, "Transpiler", modLookup, ownerPatches);
                CollectPatches(info.Finalizers, "Finalizer", modLookup, ownerPatches);

                // Only report if 2+ distinct mods patch this method
                // Group by mod ID (not harmony owner) to avoid flagging sub-components of the same mod
                var modGroups = new Dictionary<string, List<PatchOwner>>();
                foreach (var kv in ownerPatches)
                {
                    foreach (var po in kv.Value)
                    {
                        if (!modGroups.ContainsKey(po.ModId))
                            modGroups[po.ModId] = new List<PatchOwner>();
                        modGroups[po.ModId].Add(po);
                    }
                }

                // Only flag if 2+ non-core mods patch this method
                // Core + one mod is expected (framework patches), not a conflict
                var nonCoreMods = modGroups.Keys.Where(id => id != "core").ToList();
                if (nonCoreMods.Count < 2) continue;

                // Build the conflict entry — exclude core patches entirely (framework patches are expected)
                var modPatches = modGroups.Where(kv => kv.Key != "core")
                    .SelectMany(kv => kv.Value).ToList();
                var severity = ClassifySeverity(modPatches);

                var conflict = new PatchConflict
                {
                    TargetType = method.DeclaringType?.Name ?? "Unknown",
                    TargetMethod = method.Name,
                    Severity = severity,
                    Patches = modPatches
                };

                report.PatchConflicts.Add(conflict);
            }

            // Only report HIGH severity conflicts to users (real problems)
            // Medium/low are benign patch overlaps (multiple postfixes, prefix+postfix) that work fine
            report.PatchConflicts.RemoveAll(c => c.Severity != ConflictSeverity.High);
        }

        private static void CollectPatches(
            IEnumerable<Patch> patches,
            string patchType,
            Dictionary<string, string[]> modLookup,
            Dictionary<string, List<PatchOwner>> ownerPatches)
        {
            if (patches == null) return;

            foreach (var patch in patches)
            {
                string owner = patch.owner ?? "unknown";
                var modInfo = ResolveModId(owner, modLookup);

                bool returnsBool = patchType == "Prefix" &&
                                   patch.PatchMethod?.ReturnType == typeof(bool);

                var po = new PatchOwner
                {
                    ModId = modInfo[0],
                    ModName = modInfo[1],
                    HarmonyOwner = owner,
                    PatchType = patchType,
                    ReturnsBool = returnsBool
                };

                if (!ownerPatches.ContainsKey(owner))
                    ownerPatches[owner] = new List<PatchOwner>();
                ownerPatches[owner].Add(po);
            }
        }

        /// <summary>
        /// Build a lookup from Harmony owner prefix to [modId, modName].
        /// Sorted by prefix length descending so longer prefixes match first
        /// (e.g., "storagehub.paintingchest" matches "storage-hub" not "storagehub").
        /// </summary>
        private static Dictionary<string, string[]> BuildModLookup()
        {
            var lookup = new Dictionary<string, string[]>();

            foreach (var mod in PluginLoader.Mods)
            {
                string prefix = "com.terrariamodder." + mod.Manifest.Id.Replace("-", "");
                lookup[prefix] = new[] { mod.Manifest.Id, mod.Manifest.Name };
            }

            return lookup;
        }

        /// <summary>
        /// Resolve a Harmony owner string to [modId, modName].
        /// Uses longest-prefix matching against known mod Harmony IDs.
        /// </summary>
        private static string[] ResolveModId(string harmonyOwner, Dictionary<string, string[]> modLookup)
        {
            string bestMatch = null;
            int bestLen = 0;

            foreach (var kv in modLookup)
            {
                if (harmonyOwner.StartsWith(kv.Key, StringComparison.OrdinalIgnoreCase) &&
                    kv.Key.Length > bestLen)
                {
                    bestMatch = kv.Key;
                    bestLen = kv.Key.Length;
                }
            }

            if (bestMatch != null)
                return modLookup[bestMatch];

            return new[] { "core", "Core" };
        }

        private static ConflictSeverity ClassifySeverity(List<PatchOwner> patches)
        {
            // Count distinct mods per patch type
            var prefixMods = new HashSet<string>();
            var transpilerMods = new HashSet<string>();
            var postfixMods = new HashSet<string>();
            var boolPrefixMods = new HashSet<string>();

            foreach (var p in patches)
            {
                switch (p.PatchType)
                {
                    case "Prefix":
                        prefixMods.Add(p.ModId);
                        if (p.ReturnsBool)
                            boolPrefixMods.Add(p.ModId);
                        break;
                    case "Transpiler":
                        transpilerMods.Add(p.ModId);
                        break;
                    case "Postfix":
                        postfixMods.Add(p.ModId);
                        break;
                }
            }

            // HIGH: Two bool-returning prefixes from different mods
            if (boolPrefixMods.Count >= 2)
                return ConflictSeverity.High;

            // HIGH: Two transpilers from different mods
            if (transpilerMods.Count >= 2)
                return ConflictSeverity.High;

            // MEDIUM: Prefix + postfix from different mods (may interact)
            if (prefixMods.Count > 0 && postfixMods.Count > 0 &&
                !prefixMods.SetEquals(postfixMods))
                return ConflictSeverity.Medium;

            // LOW: Multiple postfixes (both run, usually fine)
            return ConflictSeverity.Low;
        }
    }
}
