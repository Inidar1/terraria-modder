using System.IO;
using System.Text.Json;
using SharpCompress.Archives;
using TerrariaModManager.Models;

namespace TerrariaModManager.Services;

public class InstallResult
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public string? InstalledModId { get; set; }
    public string? DownloadedFilePath { get; set; }
}

public enum ConfigAction { Keep, Delete }

public class ModInstallService
{
    private const long MaxExtractSize = 1_000_000_000;
    private const int MaxArchiveEntries = 20_000;
    private readonly Logger _logger;
    private readonly SettingsService _settings;
    private readonly CompatibilityService _compatibility;
    private readonly Action<string>? _testCheckpoint;
    private string? _terrariaPath;
    private static readonly SemaphoreSlim _installLock = new(1, 1);

    public ModInstallService(Logger logger, SettingsService settings, CompatibilityService compatibility)
        : this(logger, settings, compatibility, null) { }

    internal ModInstallService(Logger logger, SettingsService settings, CompatibilityService compatibility, Action<string>? testCheckpoint)
    {
        _logger = logger;
        _settings = settings;
        _compatibility = compatibility;
        _testCheckpoint = testCheckpoint;
    }

    /// <summary>
    /// Called when existing config files are found during install.
    /// Return Keep to preserve old settings, Delete for a clean install.
    /// If null, defaults to Keep.
    /// </summary>
    public Func<string, List<string>, Task<ConfigAction>>? OnExistingConfigFound { get; set; }

    /// <summary>Returns true to continue after advisory compatibility warnings.</summary>
    public Func<string, IReadOnlyList<CompatibilityWarning>, Task<bool>>? OnCompatibilityWarningsFound { get; set; }

    public void SetTerrariaPath(string path)
    {
        _terrariaPath = path;
    }

    /// <summary>
    /// Returns the Terraria path, preferring fresh settings over cached value.
    /// </summary>
    private string? GetTerrariaPath()
    {
        // Always read fresh from settings to avoid stale cached path
        var fresh = _settings.Load().TerrariaPath;
        if (!string.IsNullOrWhiteSpace(fresh))
        {
            _terrariaPath = fresh;
            return fresh;
        }
        return _terrariaPath;
    }

    public async Task<InstallResult> InstallModAsync(string archivePath, bool forceKeepSettings = false)
    {
        // Serialize all installs — prevents concurrent modification of mod folders
        await _installLock.WaitAsync();
        try
        {
            return await InstallModCoreAsync(archivePath, forceKeepSettings);
        }
        finally
        {
            _installLock.Release();
        }
    }

    private async Task<InstallResult> InstallModCoreAsync(string archivePath, bool forceKeepSettings)
    {
        _logger.Info($"Install: opening archive {Path.GetFileName(archivePath)}");

        var terrariaPath = GetTerrariaPath();
        if (terrariaPath == null)
        {
            _logger.Error("Install: Terraria path not configured");
            return new InstallResult { Error = "Terraria path not configured", DownloadedFilePath = archivePath };
        }

        var modsDir = Path.Combine(terrariaPath, "TerrariaModder", "mods");
        var centralConfigsDir = Path.Combine(terrariaPath, "TerrariaModder", "core", "configs");

        try
        {
            using var archive = ArchiveFactory.OpenArchive(archivePath);
            var entries = archive.Entries.Where(e => !e.IsDirectory).ToList();
            _logger.Info($"Install: archive has {entries.Count} file entries");

            if (entries.Count > MaxArchiveEntries)
                return new InstallResult { Error = $"Archive contains too many files (>{MaxArchiveEntries:N0})", DownloadedFilePath = archivePath };

            // Zip bomb protection: reject archives claiming > 1GB uncompressed
            long totalSize = entries.Sum(e => e.Size);
            if (totalSize > MaxExtractSize)
            {
                _logger.Error($"Install: archive claims {totalSize:N0} bytes uncompressed, exceeds 1GB limit");
                return new InstallResult { Error = "Archive is too large (>1GB uncompressed)", DownloadedFilePath = archivePath };
            }

            // Strategy 1: Look for manifest.json to determine mod structure
            var manifest = FindManifestInArchive(entries);
            if (manifest != null)
            {
                var modId = manifest.Manifest.Id;
                _logger.Info($"Install: found manifest, id='{modId}', version='{manifest.Manifest.Version}', prefix='{manifest.PathPrefix}'");

                if (manifest.ValidationWarnings.Count > 0)
                {
                    foreach (var warn in manifest.ValidationWarnings)
                        _logger.Warn($"Install: manifest warning for '{modId}': {warn}");
                }

                if (!IsSafeModId(modId))
                    return new InstallResult { Error = $"Mod ID '{modId}' cannot be used as a mod folder name", DownloadedFilePath = archivePath };

                if (!await ApproveCompatibilityAsync(manifest.Manifest, terrariaPath))
                    return new InstallResult { Error = "Install cancelled after compatibility warning", DownloadedFilePath = archivePath };

                var targetDir = Path.Combine(modsDir, modId);
                var disabledDir = Path.Combine(modsDir, "." + modId);

                // Check for existing config files to preserve
                var existingDir = Directory.Exists(targetDir) ? targetDir
                    : Directory.Exists(disabledDir) ? disabledDir : null;
                _logger.Info($"Install: existing dir = {(existingDir != null ? Path.GetFileName(existingDir) : "none")}");

                var preservedConfigs = await PreserveConfigsAsync(existingDir, modId, forceKeepSettings,
                    GetCentralConfigPaths(centralConfigsDir, modId),
                    GetArchiveRelativePaths(entries, manifest.PathPrefix));
                _logger.Info($"Install: preserved {preservedConfigs.Count} config file(s)");

                StageAndCommitMod(entries, manifest.PathPrefix, terrariaPath, targetDir, disabledDir,
                    modId, preservedConfigs);

                var installedDir = Directory.Exists(disabledDir) ? disabledDir : targetDir;
                var extractedFiles = Directory.GetFiles(installedDir, "*", SearchOption.AllDirectories);
                _logger.Info($"Install: extracted {extractedFiles.Length} files to {modId}/");

                return new InstallResult { Success = true, InstalledModId = modId };
            }

            // Strategy 2: Look for TerrariaModder/ folder structure (core install)
            var tmPrefix = FindTerrariaModderPrefix(entries);
            if (tmPrefix != null)
            {
                _logger.Info($"Install: TerrariaModder folder structure detected, prefix='{tmPrefix}'");

                var coreDir = Path.Combine(terrariaPath, "TerrariaModder", "core");

                // Preserve config files in core/ if they exist
                var existingCoreDir = Directory.Exists(coreDir) ? coreDir : null;
                var preservedConfigs = await PreserveConfigsAsync(existingCoreDir, "core", forceKeepSettings,
                    incomingRelativePaths: GetArchiveRelativePaths(entries,
                        tmPrefix + "TerrariaModder/core/"));
                _logger.Info($"Install: preserved {preservedConfigs.Count} core config file(s)");

                // Atomic core install: extract to a staging area, then swap core/ in place.
                // The zip contains both root files (TerrariaInjector.exe) and TerrariaModder/core/*.
                // Root files go directly into terrariaPath. Core files go through staging for safety.
                StageAndCommitCore(entries, tmPrefix, terrariaPath, coreDir, preservedConfigs);
                _logger.Info("Install: core update complete");

                return new InstallResult { Success = true, InstalledModId = "core" };
            }

            // Strategy 3: Flat archive with DLL + other files — use DLL name as mod-id
            var dll = entries.FirstOrDefault(e =>
            {
                var name = Path.GetFileName(e.Key ?? "");
                return name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                    && !name.StartsWith("0Harmony", StringComparison.OrdinalIgnoreCase);
            });

            if (dll != null)
            {
                var dllName = Path.GetFileNameWithoutExtension(Path.GetFileName(dll.Key!));
                var modId = ToKebabCase(dllName);
                _logger.Info($"Install: flat archive, DLL='{dllName}.dll', mod-id='{modId}'");
                var targetDir = Path.Combine(modsDir, modId);
                var disabledDir = Path.Combine(modsDir, "." + modId);

                // Preserve config files before deleting
                var existingDir = Directory.Exists(targetDir) ? targetDir
                    : Directory.Exists(disabledDir) ? disabledDir : null;
                var prefix = FindCommonPrefix(entries);
                var preservedConfigs = await PreserveConfigsAsync(existingDir, modId, forceKeepSettings,
                    GetCentralConfigPaths(centralConfigsDir, modId), GetArchiveRelativePaths(entries, prefix));
                _logger.Info($"Install: preserved {preservedConfigs.Count} config file(s)");

                // Strip common top-level folder if all entries share one
                StageAndCommitMod(entries, prefix, terrariaPath, targetDir, disabledDir,
                    modId, preservedConfigs, dllName);

                var installedDir = Directory.Exists(disabledDir) ? disabledDir : targetDir;
                var extractedFiles = Directory.GetFiles(installedDir, "*", SearchOption.AllDirectories);
                _logger.Info($"Install: extracted {extractedFiles.Length} files to {modId}/");

                return new InstallResult { Success = true, InstalledModId = modId };
            }

            _logger.Warn($"Install: no manifest, no TerrariaModder folder, no DLL found. Archive entries: {string.Join(", ", entries.Take(10).Select(e => e.Key))}");
            return new InstallResult
            {
                Error = "Mod is not in a recognized format. Please contact the mod author.",
                DownloadedFilePath = archivePath
            };
        }
        catch (Exception ex)
        {
            _logger.Error("Install failed", ex);
            return new InstallResult { Error = ex.Message, DownloadedFilePath = archivePath };
        }
    }

    /// <summary>
    /// Update the version field in an installed mod's manifest.json to match
    /// the Nexus file version (e.g. "1.1.1-hotfix"), so the update checker
    /// sees the correct version after install.
    /// </summary>
    public void StampManifestVersion(string modId, string version)
    {
        var terrariaPath = GetTerrariaPath();
        if (terrariaPath == null) return;

        var manifestPath = Path.Combine(terrariaPath, "TerrariaModder", "mods", modId, "manifest.json");
        if (!File.Exists(manifestPath)) return;

        try
        {
            var json = File.ReadAllText(manifestPath);
            var doc = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
            if (doc == null) return;

            // Replace version value
            using var ms = new MemoryStream();
            using (var writer = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartObject();
                foreach (var prop in doc)
                {
                    if (prop.Key == "version")
                        writer.WriteString("version", version);
                    else
                    {
                        writer.WritePropertyName(prop.Key);
                        prop.Value.WriteTo(writer);
                    }
                }
                writer.WriteEndObject();
            }

            File.WriteAllText(manifestPath, System.Text.Encoding.UTF8.GetString(ms.ToArray()));
            _logger.Info($"Install: stamped manifest version '{version}' for '{modId}'");
        }
        catch (Exception ex)
        {
            _logger.Warn($"Install: failed to stamp manifest version for '{modId}': {ex.Message}");
        }
    }

    /// <summary>
    /// Find the prefix to strip so that "TerrariaModder/" ends up at the Terraria root.
    /// Handles: "TerrariaModder/...", "Terraria/TerrariaModder/..."
    /// </summary>
    private static string? FindTerrariaModderPrefix(List<IArchiveEntry> entries)
    {
        foreach (var entry in entries)
        {
            var key = NormalizePath(entry.Key ?? "");

            // Case: Terraria/TerrariaModder/...
            if (key.StartsWith("Terraria/TerrariaModder/", StringComparison.OrdinalIgnoreCase))
                return "Terraria/";

            // Case: TerrariaModder/...
            if (key.StartsWith("TerrariaModder/", StringComparison.OrdinalIgnoreCase))
                return "";
        }
        return null;
    }

    /// <summary>
    /// If all entries share a common top-level folder (e.g. "ModName/file.dll"),
    /// returns that folder as a prefix to strip. Otherwise returns empty string.
    /// </summary>
    private static string FindCommonPrefix(List<IArchiveEntry> entries)
    {
        if (entries.Count == 0) return "";

        var firstKey = NormalizePath(entries[0].Key ?? "");
        var slashIndex = firstKey.IndexOf('/');
        if (slashIndex < 0) return ""; // flat file, no folder

        var candidate = firstKey[..(slashIndex + 1)];

        // Check if ALL entries start with this folder
        foreach (var entry in entries)
        {
            var key = NormalizePath(entry.Key ?? "");
            if (!key.StartsWith(candidate, StringComparison.OrdinalIgnoreCase))
                return ""; // not all entries share this prefix
        }

        return candidate;
    }

    private static void ExtractEntries(List<IArchiveEntry> entries, string targetDir, string? prefix)
    {
        var fullTargetDir = Path.GetFullPath(targetDir).TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var destinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long extractedBytes = 0;
        var buffer = new byte[128 * 1024];

        foreach (var entry in entries)
        {
            var key = NormalizePath(entry.Key ?? "");

            string relativePath;
            if (!string.IsNullOrEmpty(prefix) && key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                relativePath = key[prefix.Length..];
            else if (!string.IsNullOrEmpty(prefix))
                continue;
            else
                relativePath = key;

            if (string.IsNullOrEmpty(relativePath)) continue;

            var destPath = Path.Combine(targetDir, relativePath.Replace('/', Path.DirectorySeparatorChar));
            var fullDestPath = Path.GetFullPath(destPath);

            // Prevent path traversal attacks (e.g., archive entries with ../../)
            if (!fullDestPath.StartsWith(fullTargetDir, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"Archive entry '{key}' would extract outside the target directory.");
            if (!destinations.Add(fullDestPath))
                throw new InvalidOperationException($"Archive contains duplicate destination '{relativePath}'.");

            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);

            using var entryStream = entry.OpenEntryStream();
            using var fileStream = File.Create(destPath);
            int read;
            while ((read = entryStream.Read(buffer, 0, buffer.Length)) > 0)
            {
                extractedBytes += read;
                if (extractedBytes > MaxExtractSize)
                    throw new InvalidOperationException("Archive expands beyond the 1GB install limit.");
                fileStream.Write(buffer, 0, read);
            }
        }
    }

    private static bool IsSafeModId(string modId) =>
        System.Text.RegularExpressions.Regex.IsMatch(modId, @"^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$")
        && modId is not "." and not "..";

    private static string CreateTransactionRoot(string terrariaPath)
    {
        var transactions = Path.Combine(terrariaPath, "TerrariaModder", ".vault-transactions");
        Directory.CreateDirectory(transactions);
        var root = Path.Combine(transactions, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private void StageAndCommitMod(
        List<IArchiveEntry> entries,
        string prefix,
        string terrariaPath,
        string targetDir,
        string disabledDir,
        string modId,
        Dictionary<string, byte[]> preservedConfigs,
        string? generatedDllName = null)
    {
        var transactionRoot = CreateTransactionRoot(terrariaPath);
        try
        {
            var stagedDir = Path.Combine(transactionRoot, "payload");
            Directory.CreateDirectory(stagedDir);
            ExtractEntries(entries, stagedDir, prefix);
            if (generatedDllName != null)
                GenerateManifestIfMissing(stagedDir, modId, generatedDllName);
            ValidateStagedMod(stagedDir, modId);
            CommitStagedMod(stagedDir, targetDir, disabledDir, preservedConfigs, transactionRoot);
        }
        finally
        {
            if (Directory.Exists(transactionRoot))
                try { Directory.Delete(transactionRoot, true); } catch { }
        }
    }

    private static void ValidateStagedMod(string stagedDir, string expectedModId)
    {
        var manifestPath = Path.Combine(stagedDir, "manifest.json");
        if (!File.Exists(manifestPath))
            throw new InvalidOperationException("Staged mod has no root manifest.json.");

        ModManifest manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<ModManifest>(File.ReadAllText(manifestPath))
                ?? throw new InvalidOperationException("Staged manifest is empty.");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("Staged manifest is not valid JSON.", ex);
        }

        if (!string.Equals(manifest.Id, expectedModId, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"Staged manifest id '{manifest.Id}' does not match expected id '{expectedModId}'.");

        if (!string.IsNullOrWhiteSpace(manifest.EntryDll))
        {
            var fullRoot = Path.GetFullPath(stagedDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var entryPath = Path.GetFullPath(Path.Combine(stagedDir,
                manifest.EntryDll.Replace('/', Path.DirectorySeparatorChar)));
            if (!entryPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase) || !File.Exists(entryPath))
                throw new InvalidOperationException($"Manifest entry_dll '{manifest.EntryDll}' is missing from the package.");
        }
    }

    private void CommitStagedMod(
        string stagedDir,
        string targetDir,
        string disabledDir,
        Dictionary<string, byte[]> preservedConfigs,
        string transactionRoot)
    {
        if (Directory.Exists(targetDir) && Directory.Exists(disabledDir))
            throw new InvalidOperationException("Both enabled and disabled folders exist for this mod; resolve the duplicate before updating.");

        var existingDir = Directory.Exists(disabledDir) ? disabledDir
            : Directory.Exists(targetDir) ? targetDir : null;
        var finalDir = existingDir == disabledDir ? disabledDir : targetDir;
        var backupDir = Path.Combine(transactionRoot, "previous");
        var activated = false;

        try
        {
            if (existingDir != null)
                Directory.Move(existingDir, backupDir);
            _testCheckpoint?.Invoke("mod-after-backup");

            Directory.CreateDirectory(Path.GetDirectoryName(finalDir)!);
            Directory.Move(stagedDir, finalDir);
            activated = true;
            _testCheckpoint?.Invoke("mod-after-activate");

            RestoreConfigs(preservedConfigs, finalDir);
            _testCheckpoint?.Invoke("mod-after-config-restore");

            if (Directory.Exists(backupDir))
                Directory.Delete(backupDir, true);
        }
        catch
        {
            if (activated && Directory.Exists(finalDir))
                Directory.Delete(finalDir, true);
            if (Directory.Exists(backupDir) && existingDir != null)
                Directory.Move(backupDir, existingDir);
            throw;
        }
    }

    private void StageAndCommitCore(
        List<IArchiveEntry> entries,
        string prefix,
        string terrariaPath,
        string coreDir,
        Dictionary<string, byte[]> preservedConfigs)
    {
        var transactionRoot = CreateTransactionRoot(terrariaPath);
        var payload = Path.Combine(transactionRoot, "payload");
        var coreBackup = Path.Combine(transactionRoot, "previous-core");
        var rootBackup = Path.Combine(transactionRoot, "previous-root");
        var installedRootFiles = new List<string>();
        var coreActivated = false;

        try
        {
            Directory.CreateDirectory(payload);
            ExtractEntries(entries, payload, prefix);
            var stagedCore = Path.Combine(payload, "TerrariaModder", "core");
            if (!Directory.Exists(stagedCore) || !Directory.EnumerateFiles(stagedCore, "*", SearchOption.AllDirectories).Any())
                throw new InvalidOperationException("Core package has no TerrariaModder/core payload.");
            if (File.Exists(Path.Combine(payload, "Terraria.exe")))
                throw new InvalidOperationException("Core package must not replace Terraria.exe.");

            if (Directory.Exists(coreDir))
                Directory.Move(coreDir, coreBackup);

            Directory.CreateDirectory(rootBackup);
            foreach (var stagedFile in Directory.GetFiles(payload))
            {
                var fileName = Path.GetFileName(stagedFile);
                var destination = Path.Combine(terrariaPath, fileName);
                if (File.Exists(destination))
                    File.Move(destination, Path.Combine(rootBackup, fileName));
                File.Move(stagedFile, destination);
                installedRootFiles.Add(destination);
            }
            _testCheckpoint?.Invoke("core-after-backup");

            Directory.CreateDirectory(Path.GetDirectoryName(coreDir)!);
            Directory.Move(stagedCore, coreDir);
            coreActivated = true;
            RestoreConfigs(preservedConfigs, coreDir);
            _testCheckpoint?.Invoke("core-after-activate");

            if (Directory.Exists(coreBackup)) Directory.Delete(coreBackup, true);
            if (Directory.Exists(rootBackup)) Directory.Delete(rootBackup, true);
        }
        catch
        {
            if (coreActivated && Directory.Exists(coreDir)) Directory.Delete(coreDir, true);
            if (Directory.Exists(coreBackup) && !Directory.Exists(coreDir)) Directory.Move(coreBackup, coreDir);
            foreach (var destination in installedRootFiles)
                if (File.Exists(destination)) File.Delete(destination);
            if (Directory.Exists(rootBackup))
                foreach (var backup in Directory.GetFiles(rootBackup))
                    File.Move(backup, Path.Combine(terrariaPath, Path.GetFileName(backup)), overwrite: true);
            throw;
        }
        finally
        {
            if (Directory.Exists(transactionRoot))
                try { Directory.Delete(transactionRoot, true); } catch { }
        }
    }

    private async Task<Dictionary<string, byte[]>> PreserveConfigsAsync(
        string? existingDir, string modId, bool forceKeep = false,
        IEnumerable<string>? extraAbsolutePaths = null,
        IReadOnlySet<string>? incomingRelativePaths = null)
    {
        var preserved = new Dictionary<string, byte[]>();
        var relativeFiles = new List<string>(); // relative to existingDir
        var absoluteFiles = new List<string>(); // absolute paths outside the mod folder

        // Scan mod folder for all user data files (configs, save data, presets, etc.)
        // Everything that isn't a build artifact gets preserved — this covers .json, .cfg,
        // .bin, .dat, and any other format a mod might use for persistent data.
        if (existingDir != null)
        {
            foreach (var file in Directory.GetFiles(existingDir, "*", SearchOption.AllDirectories))
            {
                var name = Path.GetFileName(file);
                var ext = Path.GetExtension(file).ToLowerInvariant();

                // Skip build artifacts and bundled assets — these come from the new archive
                var relative = NormalizePath(Path.GetRelativePath(existingDir, file));
                if (name.Equals("manifest.json", StringComparison.OrdinalIgnoreCase)) continue;
                if (name.EndsWith(".deps.json", StringComparison.OrdinalIgnoreCase) ||
                    name.EndsWith(".runtimeconfig.json", StringComparison.OrdinalIgnoreCase)) continue;
                if (ext is ".dll" or ".pdb" or ".exe" or ".so" or ".dylib") continue;
                if (ext is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".ico" or ".svg") continue;

                var configLike = ext is ".json" or ".cfg" or ".ini" or ".xml" or ".config" or
                    ".txt" or ".toml" or ".yaml" or ".yml" or ".bin" or ".dat";
                if (!configLike && incomingRelativePaths?.Contains(relative) == true) continue;

                relativeFiles.Add(relative);
            }
        }

        // Also include centralized config files (e.g. core/configs/{modId}.json)
        if (extraAbsolutePaths != null)
        {
            foreach (var path in extraAbsolutePaths)
            {
                if (File.Exists(path))
                    absoluteFiles.Add(path);
            }
        }

        if (relativeFiles.Count == 0 && absoluteFiles.Count == 0)
            return preserved;

        var action = ConfigAction.Keep;
        if (!forceKeep && OnExistingConfigFound != null)
        {
            var displayNames = relativeFiles.Select(Path.GetFileName)
                .Concat(absoluteFiles.Select(Path.GetFileName))
                .Distinct()
                .ToList();

            action = await OnExistingConfigFound(modId, displayNames!);
        }

        if (action == ConfigAction.Keep)
        {
            foreach (var name in relativeFiles)
            {
                var filePath = Path.Combine(existingDir!, name);
                if (File.Exists(filePath))
                    preserved[name] = File.ReadAllBytes(filePath);
            }
            foreach (var path in absoluteFiles)
            {
                preserved[path] = File.ReadAllBytes(path);
            }
        }

        return preserved;
    }

    private async Task<bool> ApproveCompatibilityAsync(ModManifest manifest, string terrariaPath)
    {
        var warnings = _compatibility.Analyze(manifest, terrariaPath);
        if (warnings.Count == 0) return true;

        foreach (var warning in warnings)
            _logger.Warn($"Compatibility '{manifest.Id}' [{warning.Code}]: {warning.Message}");
        if (OnCompatibilityWarningsFound == null) return true;

        return await OnCompatibilityWarningsFound(manifest.Id, warnings);
    }

    private static void RestoreConfigs(Dictionary<string, byte[]> preserved, string targetDir)
    {
        foreach (var (name, data) in preserved)
        {
            // Absolute path keys (centralized configs) restore to their original location.
            // Relative path keys restore inside the mod's target folder.
            var configPath = Path.IsPathRooted(name)
                ? name
                : Path.Combine(targetDir, name);
            Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
            File.WriteAllBytes(configPath, data);
        }
    }

    private static ManifestInfo? FindManifestInArchive(List<IArchiveEntry> entries)
    {
        var manifests = entries.Where(entry =>
            Path.GetFileName(entry.Key ?? "").Equals("manifest.json", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (manifests.Length > 1)
            throw new InvalidOperationException("Archive contains multiple manifest.json files; select a single-mod package.");

        foreach (var entry in manifests)
        {
            try
            {
                using var stream = entry.OpenEntryStream();
                using var reader = new StreamReader(stream);
                var json = reader.ReadToEnd();
                var manifest = JsonSerializer.Deserialize<ModManifest>(json);

                if (manifest == null || string.IsNullOrWhiteSpace(manifest.Id))
                    throw new InvalidOperationException("Archive manifest.json has no id field.");

                // Validate required fields — reject malformed manifests early
                var errors = ValidateManifest(manifest);
                if (errors.Count > 0)
                {
                    // Store errors for reporting but still allow install with warnings
                    var key = NormalizePath(entry.Key ?? "");
                    var prefix = key[..^"manifest.json".Length];
                    return new ManifestInfo
                    {
                        Manifest = manifest, PathPrefix = prefix,
                        ValidationWarnings = errors
                    };
                }

                {
                    var key = NormalizePath(entry.Key ?? "");
                    var prefix = key[..^"manifest.json".Length];
                    return new ManifestInfo { Manifest = manifest, PathPrefix = prefix };
                }
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException("Archive manifest.json is not valid JSON.", ex);
            }
        }
        return null;
    }

    /// <summary>
    /// Validate manifest required fields. Returns list of warnings (empty = valid).
    /// Does not reject the install — mods with warnings can still be installed.
    /// </summary>
    private static List<string> ValidateManifest(ModManifest manifest)
    {
        var errors = new List<string>();

        // ID format: lowercase, alphanumeric + hyphens
        if (!System.Text.RegularExpressions.Regex.IsMatch(manifest.Id, @"^[a-z0-9][a-z0-9\-]*$"))
            errors.Add($"Mod ID '{manifest.Id}' should be lowercase alphanumeric with hyphens only");

        if (string.IsNullOrWhiteSpace(manifest.Name))
            errors.Add("Manifest is missing 'name' field");

        if (string.IsNullOrWhiteSpace(manifest.Version))
            errors.Add("Manifest is missing 'version' field");

        if (string.IsNullOrWhiteSpace(manifest.Author))
            errors.Add("Manifest is missing 'author' field");

        return errors;
    }

    private void GenerateManifestIfMissing(string targetDir, string modId, string dllName)
    {
        var manifestPath = Path.Combine(targetDir, "manifest.json");
        if (File.Exists(manifestPath)) return;

        var manifest = new ModManifest
        {
            Id = modId,
            Name = dllName,
            Version = "0.0.0",
            EntryDll = $"{dllName}.dll"
        };

        try
        {
            var json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(manifestPath, json);
            _logger.Info($"Install: generated manifest.json for '{modId}'");
        }
        catch (Exception ex)
        {
            _logger.Warn($"Install: failed to generate manifest for '{modId}': {ex.Message}");
        }
    }

    /// <summary>
    /// Returns the central config file paths for a mod (scoped format introduced in Phase 3).
    /// Only returns paths that may realistically exist — the legacy per-mod config.json
    /// is handled separately by the existingDir scan in PreserveConfigsAsync.
    /// </summary>
    private static IEnumerable<string> GetCentralConfigPaths(string configsDir, string modId)
    {
        yield return Path.Combine(configsDir, modId + ".client.json");
        yield return Path.Combine(configsDir, modId + ".server.json");
    }

    private static string NormalizePath(string path) => path.Replace('\\', '/');

    private static string ToKebabCase(string input)
    {
        var result = new System.Text.StringBuilder();
        for (int i = 0; i < input.Length; i++)
        {
            var c = input[i];
            if (char.IsUpper(c) && i > 0 && !char.IsUpper(input[i - 1]))
                result.Append('-');
            result.Append(char.ToLower(c));
        }
        return result.ToString();
    }

    public async Task<InstallResult> InstallFromFolderAsync(string sourceFolder)
    {
        await _installLock.WaitAsync();
        try
        {
            return await InstallFromFolderCoreAsync(sourceFolder);
        }
        finally
        {
            _installLock.Release();
        }
    }

    private async Task<InstallResult> InstallFromFolderCoreAsync(string sourceFolder)
    {
        var terrariaPath = GetTerrariaPath();
        if (terrariaPath == null)
            return new InstallResult { Error = "Terraria path not configured" };

        var modsDir = Path.Combine(terrariaPath, "TerrariaModder", "mods");
        var centralConfigsDir = Path.Combine(terrariaPath, "TerrariaModder", "core", "configs");

        try
        {
            // Validate: look for manifest.json
            string? modId = null;
            ModManifest? sourceManifest = null;
            var manifestPath = Path.Combine(sourceFolder, "manifest.json");

            if (File.Exists(manifestPath))
            {
                try
                {
                    var json = File.ReadAllText(manifestPath);
                    var manifest = JsonSerializer.Deserialize<ModManifest>(json);
                    if (manifest != null && !string.IsNullOrWhiteSpace(manifest.Id))
                    {
                        modId = manifest.Id;
                        sourceManifest = manifest;
                    }
                }
                catch (JsonException ex)
                {
                    return new InstallResult { Error = $"Folder manifest.json is not valid JSON: {ex.Message}" };
                }
            }

            // Fallback: DLL name → kebab-case
            if (File.Exists(manifestPath) && modId == null)
                return new InstallResult { Error = "Folder manifest.json has no id field." };

            if (modId == null)
            {
                var dll = Directory.GetFiles(sourceFolder, "*.dll")
                    .Select(Path.GetFileName)
                    .FirstOrDefault(n => !n!.StartsWith("0Harmony", StringComparison.OrdinalIgnoreCase));

                if (dll != null)
                    modId = ToKebabCase(Path.GetFileNameWithoutExtension(dll)!);
            }

            if (modId == null)
                return new InstallResult { Error = "NO_MOD_FOUND" };
            if (!IsSafeModId(modId))
                return new InstallResult { Error = $"Mod ID '{modId}' cannot be used as a mod folder name" };
            if (sourceManifest != null && !await ApproveCompatibilityAsync(sourceManifest, terrariaPath))
                return new InstallResult { Error = "Install cancelled after compatibility warning" };

            var targetDir = Path.Combine(modsDir, modId);
            var disabledDir = Path.Combine(modsDir, "." + modId);

            // Check if source IS the target (already in the right place)
            var normalizedSource = Path.GetFullPath(sourceFolder).TrimEnd(Path.DirectorySeparatorChar);
            var normalizedTarget = Path.GetFullPath(targetDir).TrimEnd(Path.DirectorySeparatorChar);
            var normalizedDisabled = Path.GetFullPath(disabledDir).TrimEnd(Path.DirectorySeparatorChar);

            if (string.Equals(normalizedSource, normalizedTarget, StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalizedSource, normalizedDisabled, StringComparison.OrdinalIgnoreCase))
            {
                return new InstallResult { Error = "ALREADY_INSTALLED", InstalledModId = modId };
            }

            // Preserve configs from existing install
            var existingDir = Directory.Exists(targetDir) ? targetDir
                : Directory.Exists(disabledDir) ? disabledDir : null;
            var incomingPaths = Directory.GetFiles(sourceFolder, "*", SearchOption.AllDirectories)
                .Select(path => NormalizePath(Path.GetRelativePath(sourceFolder, path)))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var preservedConfigs = await PreserveConfigsAsync(existingDir, modId,
                extraAbsolutePaths: GetCentralConfigPaths(centralConfigsDir, modId),
                incomingRelativePaths: incomingPaths);

            var transactionRoot = CreateTransactionRoot(terrariaPath);
            try
            {
                var stagedDir = Path.Combine(transactionRoot, "payload");
                CopyDirectory(sourceFolder, stagedDir);
                GenerateManifestIfMissing(stagedDir, modId,
                    Path.GetFileNameWithoutExtension(Directory.GetFiles(stagedDir, "*.dll").FirstOrDefault() ?? modId));
                ValidateStagedMod(stagedDir, modId);
                CommitStagedMod(stagedDir, targetDir, disabledDir, preservedConfigs, transactionRoot);
            }
            finally
            {
                if (Directory.Exists(transactionRoot))
                    try { Directory.Delete(transactionRoot, true); } catch { }
            }

            return new InstallResult { Success = true, InstalledModId = modId };
        }
        catch (Exception ex)
        {
            return new InstallResult { Error = ex.Message };
        }
    }

    private static void CopyDirectory(string sourceDir, string targetDir)
    {
        Directory.CreateDirectory(targetDir);

        foreach (var file in Directory.GetFiles(sourceDir))
        {
            var destFile = Path.Combine(targetDir, Path.GetFileName(file));
            File.Copy(file, destFile, true);
        }

        foreach (var dir in Directory.GetDirectories(sourceDir))
        {
            var destDir = Path.Combine(targetDir, Path.GetFileName(dir));
            CopyDirectory(dir, destDir);
        }
    }

    private static IReadOnlySet<string> GetArchiveRelativePaths(IEnumerable<IArchiveEntry> entries, string prefix)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            var key = NormalizePath(entry.Key ?? "");
            if (!string.IsNullOrEmpty(prefix))
            {
                if (!key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                key = key[prefix.Length..];
            }
            if (!string.IsNullOrEmpty(key)) result.Add(key);
        }
        return result;
    }

    private class ManifestInfo
    {
        public ModManifest Manifest { get; set; } = null!;
        public string PathPrefix { get; set; } = "";
        public List<string> ValidationWarnings { get; set; } = new();
    }
}
