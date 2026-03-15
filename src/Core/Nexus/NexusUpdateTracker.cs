using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TerrariaModder.Core.Config;
using TerrariaModder.Core.Logging;

namespace TerrariaModder.Core.Nexus
{
    internal sealed class NexusUpdateTracker
    {
        public const int CoreNexusModId = 135;

        private readonly string _trackingFile;
        private readonly string _versionsFile;
        private readonly ILogger _log;
        private readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions { WriteIndented = true };
        private Dictionary<string, int> _entries = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, string> _versions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public NexusUpdateTracker(ILogger log)
        {
            _log = log;
            _trackingFile = Path.Combine(CoreConfig.Instance.CorePath, "nexus-mod-map.json");
            _versionsFile = Path.Combine(CoreConfig.Instance.CorePath, "installed-versions.json");
            Load();
        }

        public void RecordInstall(string modId, int nexusModId)
        {
            if (string.IsNullOrWhiteSpace(modId) || nexusModId <= 0)
                return;

            _entries[modId] = nexusModId;
            Save(_trackingFile, _entries);
        }

        public void RecordVersion(string modId, string version)
        {
            if (string.IsNullOrWhiteSpace(modId) || string.IsNullOrWhiteSpace(version))
                return;

            _versions[modId] = version;
            Save(_versionsFile, _versions);
        }

        public string GetTrackedVersion(string modId)
        {
            return _versions.TryGetValue(modId ?? string.Empty, out string version) ? version : null;
        }

        public int GetNexusModId(InstalledModRecord mod)
        {
            if (mod == null)
                return 0;

            if (mod.IsCore)
                return CoreNexusModId;

            if (mod.Manifest != null && mod.Manifest.NexusId > 0)
                return mod.Manifest.NexusId;

            return _entries.TryGetValue(mod.Id ?? string.Empty, out int nexusId) ? nexusId : 0;
        }

        public string GetLocalModId(int nexusModId)
        {
            foreach (var pair in _entries)
            {
                if (pair.Value == nexusModId)
                    return pair.Key;
            }

            return null;
        }

        public async Task<int> CheckForUpdatesAsync(List<InstalledModRecord> mods, NexusApiService nexusApi, CancellationToken ct = default(CancellationToken))
        {
            if (mods == null || nexusApi == null || !nexusApi.HasApiKey)
                return 0;

            int updatesFound = 0;
            foreach (var mod in mods)
            {
                ct.ThrowIfCancellationRequested();
                int nexusId = GetNexusModId(mod);
                if (nexusId <= 0)
                    continue;

                mod.NexusModId = nexusId;

                try
                {
                    var files = await nexusApi.GetModFilesAsync(nexusId).ConfigureAwait(false);
                    if (files.Count == 0)
                        continue;

                    var mainFile = files.FirstOrDefault(f => f.IsPrimary) ?? files.OrderByDescending(f => f.UploadedTimestamp).First();
                    if (!string.IsNullOrWhiteSpace(mainFile.Version) && IsNewerVersion(mainFile.Version, mod.Version))
                    {
                        mod.HasUpdate = true;
                        mod.LatestVersion = mainFile.Version;
                        mod.LatestFileId = mainFile.FileId;
                        updatesFound++;
                    }
                }
                catch (Exception ex)
                {
                    _log?.Warn("[Nexus] Update check failed for " + mod.Id + ": " + ex.Message);
                }
            }

            return updatesFound;
        }

        public static bool IsNewerVersion(string nexusVersion, string localVersion)
        {
            var nexus = ParseVersion(nexusVersion);
            var local = ParseVersion(localVersion);

            if (nexus.BaseVersion == null || local.BaseVersion == null)
            {
                string rawNexus = (nexusVersion ?? string.Empty).TrimStart('v', 'V');
                string rawLocal = (localVersion ?? string.Empty).TrimStart('v', 'V');
                return string.Compare(rawNexus, rawLocal, StringComparison.OrdinalIgnoreCase) > 0;
            }

            if (nexus.BaseVersion > local.BaseVersion)
                return true;
            if (nexus.BaseVersion < local.BaseVersion)
                return false;

            return nexus.SuffixRank > local.SuffixRank;
        }

        private static VersionParseResult ParseVersion(string version)
        {
            version = (version ?? string.Empty).TrimStart('v', 'V');
            int plusIndex = version.IndexOf('+');
            if (plusIndex >= 0)
                version = version.Substring(0, plusIndex);

            int dashIndex = version.IndexOf('-');
            string baseString = dashIndex >= 0 ? version.Substring(0, dashIndex) : version;
            int suffixRank = 0;

            if (dashIndex >= 0)
            {
                string suffix = version.Substring(dashIndex + 1).ToLowerInvariant();
                if (suffix.StartsWith("hotfix") || suffix.StartsWith("patch") || suffix.StartsWith("fix"))
                {
                    suffixRank = 1;
                    int prefixLen = suffix.StartsWith("hotfix") ? 6 : suffix.StartsWith("patch") ? 5 : 3;
                    if (suffix.Length > prefixLen && int.TryParse(suffix.Substring(prefixLen), out int numericRank) && numericRank > 1)
                        suffixRank = numericRank;
                }
                else
                {
                    suffixRank = -1;
                }
            }

            return new VersionParseResult
            {
                BaseVersion = Version.TryParse(baseString, out Version parsed) ? parsed : null,
                SuffixRank = suffixRank
            };
        }

        private void Load()
        {
            _entries = LoadDictionary<int>(_trackingFile);
            _versions = LoadDictionary<string>(_versionsFile);
        }

        private Dictionary<string, T> LoadDictionary<T>(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    string json = File.ReadAllText(path);
                    return JsonSerializer.Deserialize<Dictionary<string, T>>(json) ?? new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);
                }
            }
            catch (Exception ex)
            {
                _log?.Warn("[Nexus] Failed to load " + Path.GetFileName(path) + ": " + ex.Message);
            }

            return new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);
        }

        private void Save<T>(string path, Dictionary<string, T> data)
        {
            try
            {
                File.WriteAllText(path, JsonSerializer.Serialize(data, _jsonOptions));
            }
            catch (Exception ex)
            {
                _log?.Warn("[Nexus] Failed to save " + Path.GetFileName(path) + ": " + ex.Message);
            }
        }

        private sealed class VersionParseResult
        {
            public Version BaseVersion { get; set; }
            public int SuffixRank { get; set; }
        }
    }
}
