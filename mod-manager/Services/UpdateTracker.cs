using System.IO;
using System.Text.Json;
using TerrariaModManager.Models;

namespace TerrariaModManager.Services;

/// <summary>
/// Tracks which Nexus mod ID corresponds to each installed mod,
/// and checks for available updates by comparing versions.
/// </summary>
public class UpdateTracker
{
    public const int CoreNexusModId = 135;
    private readonly string _trackingFile;
    private readonly string _versionsFile;
    private readonly Logger _logger;
    private Dictionary<string, int> _entries = new();
    private Dictionary<string, string> _versions = new();

    public UpdateTracker(SettingsService settings, Logger logger)
    {
        _logger = logger;
        // Re-calculate or use SettingsService properties
        var appDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TerrariaModManager");
        _trackingFile = Path.Combine(appDataDir, "nexus-mod-map.json");
        _versionsFile = Path.Combine(appDataDir, "installed-versions.json");
        Load();
    }

    /// <summary>
    /// Record that a local mod ID was installed from a specific Nexus mod.
    /// </summary>
    public void RecordInstall(string modId, int nexusModId)
    {
        _entries[modId] = nexusModId;
        Save();
    }

    /// <summary>
    /// Record the installed Nexus version for a mod (used for mods without manifest.json, like core).
    /// </summary>
    public void RecordVersion(string modId, string version)
    {
        _versions[modId] = version;
        SaveVersions();
    }

    /// <summary>
    /// Get the tracked installed version for a mod, or null if not tracked.
    /// </summary>
    public string? GetTrackedVersion(string modId)
    {
        return _versions.TryGetValue(modId, out var v) ? v : null;
    }

    /// <summary>
    /// Get the Nexus mod ID for an installed mod.
    /// Core always maps to mod 135. Others check manifest/tracking file.
    /// </summary>
    public int GetNexusModId(InstalledMod mod)
    {
        if (mod.IsCore)
            return CoreNexusModId;

        if (mod.Manifest?.NexusId > 0)
            return mod.Manifest.NexusId;

        if (_entries.TryGetValue(mod.Id, out var nexusId) && nexusId > 0)
            return nexusId;

        return 0;
    }

    /// <summary>
    /// Reverse lookup: find the local mod ID installed from a given Nexus mod ID.
    /// </summary>
    public string? GetLocalModId(int nexusModId)
    {
        foreach (var (modId, nid) in _entries)
            if (nid == nexusModId) return modId;
        return null;
    }

    /// <summary>
    /// Check for updates on all installed mods that have known Nexus mod IDs.
    /// Returns the number of mods with available updates.
    /// </summary>
    public async Task<int> CheckForUpdatesAsync(List<InstalledMod> mods, NexusApiService nexusApi,
        CancellationToken ct = default)
    {
        if (!nexusApi.HasApiKey) return 0;

        int updatesFound = 0;

        foreach (var mod in mods)
        {
            ct.ThrowIfCancellationRequested();

            var nexusId = GetNexusModId(mod);
            if (nexusId <= 0) continue;

            mod.NexusModId = nexusId;

            try
            {
                var files = await nexusApi.GetModFilesAsync(nexusId);
                if (files.Count == 0) continue;

                // Find the primary/main file, or fall back to latest uploaded
                var mainFile = files.FirstOrDefault(f => f.IsPrimary)
                    ?? files.OrderByDescending(f => f.UploadedTimestamp).First();

                var hasUpdate = !string.IsNullOrWhiteSpace(mainFile.Version) &&
                    IsNewerVersion(mainFile.Version, mod.Version);
                _logger.Info($"Update check '{mod.Id}': installed v{mod.Version}, latest v{mainFile.Version}, update={hasUpdate}");

                if (hasUpdate)
                {
                    mod.HasUpdate = true;
                    mod.LatestVersion = mainFile.Version;
                    mod.LatestFileId = mainFile.FileId;
                    updatesFound++;
                }
            }
            catch (Exception ex)
            {
                _logger.Warn($"Update check '{mod.Id}' failed: {ex.Message}");
            }
        }

        return updatesFound;
    }

    /// <summary>
    /// Version comparison. Returns true if nexusVersion is newer than localVersion.
    /// Supports suffixes: -beta/-rc (pre-release, below base), -hotfix/-patch/-fix (post-release, above base).
    /// Numbered variants like -hotfix2 are ordered above -hotfix.
    /// Example ordering: 1.1.1-beta &lt; 1.1.1 &lt; 1.1.1-hotfix &lt; 1.1.1-hotfix2 &lt; 1.1.2
    /// </summary>
    private static bool IsNewerVersion(string nexusVersion, string localVersion)
    {
        var (nBase, nRank) = ParseVersion(nexusVersion);
        var (lBase, lRank) = ParseVersion(localVersion);

        if (nBase == null || lBase == null)
        {
            // Unparseable — fall back to string comparison on raw input
            var rawN = nexusVersion.TrimStart('v', 'V');
            var rawL = localVersion.TrimStart('v', 'V');
            if (rawN == rawL) return false;
            return string.Compare(rawN, rawL, StringComparison.OrdinalIgnoreCase) > 0;
        }

        if (nBase > lBase) return true;
        if (nBase < lBase) return false;

        // Same base version — suffix rank breaks the tie
        return nRank > lRank;
    }

    /// <summary>
    /// Parses "1.1.1-hotfix2" into (Version(1.1.1), SuffixRank=2).
    /// Suffix ranks: -1 = pre-release (-beta, -rc), 0 = release (no suffix),
    /// 1 = post-release (-fix/-hotfix/-patch), 2+ = numbered (-hotfix2, -patch3).
    /// Returns (null, 0) if the base version can't be parsed.
    /// </summary>
    private static (Version? Base, int SuffixRank) ParseVersion(string version)
    {
        version = version.TrimStart('v', 'V');

        var idx = version.IndexOf('-');
        int rank = 0;
        string baseStr = version;

        if (idx >= 0)
        {
            var suffix = version[(idx + 1)..].ToLowerInvariant();
            baseStr = version[..idx];

            if (suffix.StartsWith("hotfix") || suffix.StartsWith("patch") || suffix.StartsWith("fix"))
            {
                rank = 1;
                // Extract trailing number: "hotfix2" → 2
                var numStr = suffix.TrimStart("hotfix".ToCharArray())
                    .TrimStart("patch".ToCharArray())
                    .TrimStart("fix".ToCharArray());
                // Re-parse more carefully: find first digit run after the prefix
                var prefixLen = suffix.StartsWith("hotfix") ? 6
                    : suffix.StartsWith("patch") ? 5 : 3;
                numStr = suffix[prefixLen..];
                if (int.TryParse(numStr, out var n) && n > 1)
                    rank = n;
            }
            else
            {
                // Unknown suffix (-beta, -rc1, -alpha, etc.) = pre-release
                rank = -1;
            }
        }

        return Version.TryParse(baseStr, out var v) ? (v, rank) : (null, 0);
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_trackingFile))
            {
                var json = File.ReadAllText(_trackingFile);
                _entries = JsonSerializer.Deserialize<Dictionary<string, int>>(json) ?? new();
            }
        }
        catch { _entries = new(); }

        try
        {
            if (File.Exists(_versionsFile))
            {
                var json = File.ReadAllText(_versionsFile);
                _versions = JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new();
            }
        }
        catch { _versions = new(); }
    }

    private void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(_entries, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_trackingFile, json);
        }
        catch { }
    }

    private void SaveVersions()
    {
        try
        {
            var json = JsonSerializer.Serialize(_versions, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_versionsFile, json);
        }
        catch { }
    }
}
