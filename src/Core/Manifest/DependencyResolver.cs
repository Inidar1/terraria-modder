using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using TerrariaModder.Core.Logging;

namespace TerrariaModder.Core.Manifest
{
    /// <summary>
    /// Result of dependency resolution.
    /// </summary>
    public class DependencyResult
    {
        /// <summary>Mods in correct load order (dependencies first).</summary>
        public List<ModManifest> LoadOrder { get; set; } = new List<ModManifest>();

        /// <summary>Mods with missing dependencies. Key = mod ID, Value = missing dependency IDs.</summary>
        public Dictionary<string, List<string>> MissingDependencies { get; set; } = new Dictionary<string, List<string>>();

        /// <summary>Mods involved in circular dependencies.</summary>
        public List<string> CircularDependencies { get; set; } = new List<string>();

        /// <summary>Incompatible mod pairs detected. Key = mod ID, Value = conflicting mod IDs.</summary>
        public Dictionary<string, List<string>> Incompatibilities { get; set; } = new Dictionary<string, List<string>>();

        /// <summary>True if all mods can be loaded.</summary>
        public bool Success => MissingDependencies.Count == 0 && CircularDependencies.Count == 0 && Incompatibilities.Count == 0;
    }

    /// <summary>
    /// Resolves mod dependencies using topological sort.
    /// </summary>
    public class DependencyResolver
    {
        private readonly ILogger _log;

        public DependencyResolver(ILogger logger)
        {
            _log = logger;
        }

        /// <summary>
        /// Resolve dependencies and return load order.
        /// </summary>
        public DependencyResult Resolve(IEnumerable<ModManifest> manifests)
        {
            var result = new DependencyResult();
            var manifestList = manifests.ToList();
            var available = new HashSet<string>(manifestList.Select(m => m.Id));
            var manifestById = manifestList.ToDictionary(m => m.Id);

            // Step 1: Check for missing dependencies
            foreach (var manifest in manifestList)
            {
                var missing = manifest.Dependencies
                    .Where(dep => !available.Contains(dep))
                    .ToList();

                if (missing.Count > 0)
                {
                    result.MissingDependencies[manifest.Id] = missing;
                    _log.Error($"[{manifest.Id}] Missing dependencies: {string.Join(", ", missing)}");
                }
            }

            // Step 1b: Check for incompatibilities
            foreach (var manifest in manifestList)
            {
                if (manifest.IncompatibleWith == null) continue;
                var conflicts = manifest.IncompatibleWith
                    .Where(id => available.Contains(id))
                    .ToList();
                if (conflicts.Count > 0)
                {
                    result.Incompatibilities[manifest.Id] = conflicts;
                    _log.Error($"[{manifest.Id}] Incompatible with loaded mods: {string.Join(", ", conflicts)}");
                }
            }

            // Remove mods with missing dependencies from consideration
            var validMods = manifestList
                .Where(m => !result.MissingDependencies.ContainsKey(m.Id))
                .Where(m => !result.Incompatibilities.ContainsKey(m.Id))
                .ToList();

            // Step 2: Build dependency graph (including optional deps and soft ordering)
            var graph = new Dictionary<string, HashSet<string>>();
            foreach (var manifest in validMods)
            {
                var deps = new HashSet<string>(manifest.Dependencies);

                // Add optional dependencies only if they're available
                foreach (var optDep in manifest.OptionalDependencies)
                {
                    if (available.Contains(optDep) && !result.MissingDependencies.ContainsKey(optDep))
                    {
                        deps.Add(optDep);
                    }
                }

                // Add load_after as soft ordering edges (only if target is present)
                foreach (var afterId in manifest.LoadAfter)
                {
                    if (available.Contains(afterId) && !result.MissingDependencies.ContainsKey(afterId))
                    {
                        deps.Add(afterId);
                    }
                }

                graph[manifest.Id] = deps;
            }

            // Second pass: apply load_before as reverse edges (target depends on this)
            foreach (var manifest in validMods)
            {
                foreach (var beforeId in manifest.LoadBefore)
                {
                    if (graph.ContainsKey(beforeId))
                    {
                        graph[beforeId].Add(manifest.Id);
                    }
                }
            }

            // Apply user-defined load order from load-order.json (soft edges)
            ApplyUserLoadOrder(graph, validMods);

            // Step 3: Detect circular dependencies
            var cycles = FindCycles(graph);
            if (cycles.Count > 0)
            {
                result.CircularDependencies = cycles;
                foreach (var modId in cycles)
                {
                    _log.Error($"[{modId}] Involved in circular dependency");
                }

                // Remove cyclic mods from graph
                foreach (var modId in cycles)
                {
                    graph.Remove(modId);
                }
            }

            // Step 4: Topological sort (Kahn's algorithm)
            var sorted = TopologicalSort(graph);

            // Convert back to manifests
            result.LoadOrder = sorted
                .Where(id => manifestById.ContainsKey(id))
                .Select(id => manifestById[id])
                .ToList();

            return result;
        }

        /// <summary>
        /// Apply user-defined load order from load-order.json as soft ordering edges.
        /// Each consecutive pair in the saved order gets an edge: later mod depends on earlier mod.
        /// Only applies edges between mods that are both present in the graph.
        /// </summary>
        private void ApplyUserLoadOrder(Dictionary<string, HashSet<string>> graph, List<ModManifest> validMods)
        {
            var orderPath = GetLoadOrderPath(validMods);
            if (orderPath == null || !File.Exists(orderPath)) return;

            try
            {
                string json = File.ReadAllText(orderPath);
                var order = ExtractOrderArray(json);
                if (order == null || order.Count < 2) return;

                // Filter to mods actually in the graph
                var validOrder = order.Where(id => graph.ContainsKey(id)).ToList();

                // Add edges: each mod in the list should load after the previous one
                for (int i = 1; i < validOrder.Count; i++)
                {
                    graph[validOrder[i]].Add(validOrder[i - 1]);
                }

                _log.Info($"Applied user load order from load-order.json ({validOrder.Count} mods)");
            }
            catch (Exception ex)
            {
                _log.Warn($"Failed to read load-order.json: {ex.Message}");
            }
        }

        /// <summary>
        /// Get the path to load-order.json based on Core's folder location.
        /// </summary>
        private static string GetLoadOrderPath(List<ModManifest> validMods)
        {
            // Find any mod's folder to derive the core folder path
            // Mods are in TerrariaModder/mods/{modid}/, core is at TerrariaModder/core/
            if (validMods.Count == 0) return null;
            var modsDir = Path.GetDirectoryName(validMods[0].FolderPath);
            if (modsDir == null) return null;
            var rootDir = Path.GetDirectoryName(modsDir);
            if (rootDir == null) return null;
            return Path.Combine(rootDir, "core", "load-order.json");
        }

        /// <summary>
        /// Get the load-order.json path from a known core folder path.
        /// </summary>
        public static string GetLoadOrderPathFromCore(string coreFolder)
        {
            return Path.Combine(coreFolder, "load-order.json");
        }

        /// <summary>
        /// Save user-defined load order to load-order.json.
        /// </summary>
        public static void SaveLoadOrder(string coreFolder, List<string> modIds)
        {
            var path = GetLoadOrderPathFromCore(coreFolder);
            var items = string.Join(", ", modIds.Select(id => $"\"{id}\""));
            var json = "{\n  \"order\": [" + items + "]\n}";
            File.WriteAllText(path, json);
        }

        /// <summary>
        /// Load user-defined load order from load-order.json.
        /// </summary>
        public static List<string> LoadUserOrder(string coreFolder)
        {
            var path = GetLoadOrderPathFromCore(coreFolder);
            if (!File.Exists(path)) return null;
            try
            {
                string json = File.ReadAllText(path);
                return ExtractOrderArray(json);
            }
            catch
            {
                return null;
            }
        }

        private static List<string> ExtractOrderArray(string json)
        {
            var result = new List<string>();
            var match = Regex.Match(json, "\"order\"\\s*:\\s*\\[([^\\]]*)\\]");
            if (!match.Success) return result;

            var stringMatches = Regex.Matches(match.Groups[1].Value, "\"([^\"]+)\"");
            foreach (Match m in stringMatches)
            {
                result.Add(m.Groups[1].Value);
            }
            return result;
        }

        /// <summary>
        /// Find all mods involved in circular dependencies using DFS.
        /// </summary>
        private List<string> FindCycles(Dictionary<string, HashSet<string>> graph)
        {
            var visiting = new HashSet<string>(); // Currently in recursion stack
            var visited = new HashSet<string>();  // Completely processed
            var inCycle = new HashSet<string>();

            foreach (var node in graph.Keys)
            {
                if (!visited.Contains(node))
                {
                    FindCyclesDFS(node, graph, visiting, visited, inCycle, new List<string>());
                }
            }

            return inCycle.ToList();
        }

        private void FindCyclesDFS(
            string node,
            Dictionary<string, HashSet<string>> graph,
            HashSet<string> visiting,
            HashSet<string> visited,
            HashSet<string> inCycle,
            List<string> path)
        {
            if (visited.Contains(node)) return;

            if (visiting.Contains(node))
            {
                // Found a cycle - mark all nodes in the cycle
                int cycleStart = path.IndexOf(node);
                for (int i = cycleStart; i < path.Count; i++)
                {
                    inCycle.Add(path[i]);
                }
                inCycle.Add(node);
                return;
            }

            visiting.Add(node);
            path.Add(node);

            if (graph.TryGetValue(node, out var deps))
            {
                foreach (var dep in deps)
                {
                    // Only follow edges to nodes in the graph
                    if (graph.ContainsKey(dep))
                    {
                        FindCyclesDFS(dep, graph, visiting, visited, inCycle, path);
                    }
                }
            }

            path.RemoveAt(path.Count - 1);
            visiting.Remove(node);
            visited.Add(node);
        }

        /// <summary>
        /// Topological sort using Kahn's algorithm.
        /// Returns nodes in order where dependencies come first.
        /// </summary>
        private List<string> TopologicalSort(Dictionary<string, HashSet<string>> graph)
        {
            var result = new List<string>();
            var inDegree = new Dictionary<string, int>();

            // Initialize in-degrees: count how many dependencies each node has
            // If A depends on B, we want B to load first, so inDegree[A]++
            foreach (var node in graph.Keys)
            {
                inDegree[node] = 0;
            }

            foreach (var node in graph.Keys)
            {
                foreach (var dep in graph[node])
                {
                    if (graph.ContainsKey(dep))
                    {
                        inDegree[node]++;
                    }
                }
            }

            // Queue of nodes with no dependencies
            var queue = new Queue<string>();
            foreach (var node in graph.Keys)
            {
                if (inDegree[node] == 0)
                {
                    queue.Enqueue(node);
                }
            }

            while (queue.Count > 0)
            {
                var node = queue.Dequeue();
                result.Add(node);

                // For each node that depends on this one, decrement its in-degree
                foreach (var other in graph.Keys)
                {
                    if (graph[other].Contains(node))
                    {
                        inDegree[other]--;
                        if (inDegree[other] == 0)
                        {
                            queue.Enqueue(other);
                        }
                    }
                }
            }

            return result;
        }
    }
}
