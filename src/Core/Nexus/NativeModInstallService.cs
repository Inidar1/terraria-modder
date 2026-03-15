using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using SharpCompress.Archives;
using SharpCompress.Archives.Zip;
using TerrariaModder.Core.Config;
using TerrariaModder.Core.Logging;
using TerrariaModder.Core.Manifest;

namespace TerrariaModder.Core.Nexus
{
    internal sealed class NativeModInstallService
    {
        private readonly ILogger _log;
        private readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions { WriteIndented = true };

        public NativeModInstallService(ILogger log)
        {
            _log = log;
        }

        public Task<InstallResult> InstallModAsync(string archivePath, ConfigPreservationMode configMode)
        {
            _log?.Info("[Nexus] Install opening archive " + Path.GetFileName(archivePath));

            string modsDir = CoreConfig.Instance.ModsPath;
            try
            {
                using (var archive = ZipArchive.Open(archivePath))
                {
                    var entries = archive.Entries.Where(e => !e.IsDirectory).Cast<IArchiveEntry>().ToList();
                    var manifestInfo = FindManifestInArchive(entries);
                    if (manifestInfo != null)
                    {
                        string modId = manifestInfo.Manifest.Id;
                        string targetDir = Path.Combine(modsDir, modId);
                        string disabledDir = Path.Combine(modsDir, "." + modId);
                        string existingDir = Directory.Exists(targetDir) ? targetDir : Directory.Exists(disabledDir) ? disabledDir : null;
                        var preservedConfigs = PreserveConfigs(existingDir, configMode);

                        if (Directory.Exists(targetDir))
                            Directory.Delete(targetDir, true);
                        if (Directory.Exists(disabledDir))
                            Directory.Delete(disabledDir, true);

                        Directory.CreateDirectory(targetDir);
                        ExtractEntries(entries, targetDir, manifestInfo.PathPrefix);
                        RestoreConfigs(preservedConfigs, targetDir);
                        return Task.FromResult(new InstallResult { Success = true, InstalledModId = modId });
                    }

                    string tmPrefix = FindTerrariaModderPrefix(entries);
                    if (tmPrefix != null)
                    {
                        string coreDir = CoreConfig.Instance.CorePath;
                        var preservedCoreConfigs = PreserveConfigs(Directory.Exists(coreDir) ? coreDir : null, configMode);
                        if (Directory.Exists(coreDir))
                            Directory.Delete(coreDir, true);

                        ExtractEntries(entries, CoreConfig.Instance.GameFolder, tmPrefix);
                        RestoreConfigs(preservedCoreConfigs, coreDir);
                        return Task.FromResult(new InstallResult { Success = true, InstalledModId = "core" });
                    }

                    var dllEntry = entries.FirstOrDefault(e =>
                    {
                        string name = Path.GetFileName(e.Key ?? string.Empty);
                        return name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                            && !name.StartsWith("0Harmony", StringComparison.OrdinalIgnoreCase);
                    });

                    if (dllEntry != null)
                    {
                        string dllName = Path.GetFileNameWithoutExtension(Path.GetFileName(dllEntry.Key));
                        string modId = ToKebabCase(dllName);
                        string targetDir = Path.Combine(modsDir, modId);
                        string disabledDir = Path.Combine(modsDir, "." + modId);
                        string existingDir = Directory.Exists(targetDir) ? targetDir : Directory.Exists(disabledDir) ? disabledDir : null;
                        var preservedConfigs = PreserveConfigs(existingDir, configMode);

                        if (Directory.Exists(targetDir))
                            Directory.Delete(targetDir, true);
                        if (Directory.Exists(disabledDir))
                            Directory.Delete(disabledDir, true);

                        Directory.CreateDirectory(targetDir);
                        ExtractEntries(entries, targetDir, FindCommonPrefix(entries));
                        RestoreConfigs(preservedConfigs, targetDir);
                        GenerateManifestIfMissing(targetDir, modId, dllName);
                        return Task.FromResult(new InstallResult { Success = true, InstalledModId = modId });
                    }
                }

                return Task.FromResult(new InstallResult
                {
                    Error = "Mod is not in a recognized format. Please contact the mod author.",
                    DownloadedFilePath = archivePath
                });
            }
            catch (Exception ex)
            {
                _log?.Error("[Nexus] Install failed", ex);
                return Task.FromResult(new InstallResult { Error = ex.Message, DownloadedFilePath = archivePath });
            }
        }

        public void StampManifestVersion(string modId, string version)
        {
            string manifestPath = Path.Combine(CoreConfig.Instance.ModsPath, modId, "manifest.json");
            if (!File.Exists(manifestPath))
                return;

            try
            {
                string json = File.ReadAllText(manifestPath);
                var doc = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
                if (doc == null)
                    return;

                using (var stream = new MemoryStream())
                {
                    using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
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

                    File.WriteAllText(manifestPath, Encoding.UTF8.GetString(stream.ToArray()));
                }
            }
            catch (Exception ex)
            {
                _log?.Warn("[Nexus] Failed to stamp manifest version for " + modId + ": " + ex.Message);
            }
        }

        private static string FindTerrariaModderPrefix(List<IArchiveEntry> entries)
        {
            foreach (var entry in entries)
            {
                string key = NormalizePath(entry.Key ?? string.Empty);
                if (key.StartsWith("Terraria/TerrariaModder/", StringComparison.OrdinalIgnoreCase))
                    return "Terraria/";
                if (key.StartsWith("TerrariaModder/", StringComparison.OrdinalIgnoreCase))
                    return string.Empty;
            }

            return null;
        }

        private static string FindCommonPrefix(List<IArchiveEntry> entries)
        {
            if (entries.Count == 0)
                return string.Empty;

            string firstKey = NormalizePath(entries[0].Key ?? string.Empty);
            int slashIndex = firstKey.IndexOf('/');
            if (slashIndex < 0)
                return string.Empty;

            string candidate = firstKey.Substring(0, slashIndex + 1);
            foreach (var entry in entries)
            {
                if (!NormalizePath(entry.Key ?? string.Empty).StartsWith(candidate, StringComparison.OrdinalIgnoreCase))
                    return string.Empty;
            }

            return candidate;
        }

        private static void ExtractEntries(List<IArchiveEntry> entries, string targetDir, string prefix)
        {
            foreach (var entry in entries)
            {
                string key = NormalizePath(entry.Key ?? string.Empty);
                string relativePath = !string.IsNullOrEmpty(prefix) && key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                    ? key.Substring(prefix.Length)
                    : key;

                if (string.IsNullOrEmpty(relativePath))
                    continue;

                string destPath = Path.Combine(targetDir, relativePath.Replace('/', Path.DirectorySeparatorChar));
                string destDir = Path.GetDirectoryName(destPath);
                if (!string.IsNullOrEmpty(destDir))
                    Directory.CreateDirectory(destDir);

                using (var input = entry.OpenEntryStream())
                using (var output = File.Create(destPath))
                {
                    input.CopyTo(output);
                }
            }
        }

        private Dictionary<string, byte[]> PreserveConfigs(string existingDir, ConfigPreservationMode mode)
        {
            var preserved = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            if (existingDir == null || mode == ConfigPreservationMode.Delete)
                return preserved;

            foreach (var file in Directory.GetFiles(existingDir, "*", SearchOption.AllDirectories))
            {
                string name = Path.GetFileName(file);
                if (string.Equals(name, "manifest.json", StringComparison.OrdinalIgnoreCase))
                    continue;

                string ext = Path.GetExtension(file).ToLowerInvariant();
                if (ext == ".json" || ext == ".cfg" || ext == ".ini" || ext == ".xml" || ext == ".config" ||
                    ext == ".txt" || ext == ".toml" || ext == ".yaml" || ext == ".yml")
                {
                    string relative = file.Substring(existingDir.TrimEnd(Path.DirectorySeparatorChar).Length + 1);
                    preserved[relative] = File.ReadAllBytes(file);
                }
            }

            return preserved;
        }

        private static void RestoreConfigs(Dictionary<string, byte[]> preserved, string targetDir)
        {
            foreach (var pair in preserved)
            {
                string configPath = Path.Combine(targetDir, pair.Key);
                string dir = Path.GetDirectoryName(configPath);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);
                File.WriteAllBytes(configPath, pair.Value);
            }
        }

        private static ManifestInfo FindManifestInArchive(List<IArchiveEntry> entries)
        {
            foreach (var entry in entries)
            {
                if (!string.Equals(Path.GetFileName(entry.Key ?? string.Empty), "manifest.json", StringComparison.OrdinalIgnoreCase))
                    continue;

                try
                {
                    using (var stream = entry.OpenEntryStream())
                    using (var reader = new StreamReader(stream))
                    {
                        string json = reader.ReadToEnd();
                        string temp = Path.GetTempFileName();
                        try
                        {
                            File.WriteAllText(temp, json);
                            var manifest = ManifestParser.Parse(temp);
                            if (manifest != null && !string.IsNullOrWhiteSpace(manifest.Id))
                            {
                                string key = NormalizePath(entry.Key ?? string.Empty);
                                string prefix = key.Substring(0, key.Length - "manifest.json".Length);
                                return new ManifestInfo { Manifest = manifest, PathPrefix = prefix };
                            }
                        }
                        finally
                        {
                            try { File.Delete(temp); } catch { }
                        }
                    }
                }
                catch
                {
                }
            }

            return null;
        }

        private void GenerateManifestIfMissing(string targetDir, string modId, string dllName)
        {
            string manifestPath = Path.Combine(targetDir, "manifest.json");
            if (File.Exists(manifestPath))
                return;

            var manifest = new Dictionary<string, object>
            {
                { "id", modId },
                { "name", dllName },
                { "version", "0.0.0" },
                { "author", "Unknown" },
                { "description", "No manifest provided" },
                { "entry_dll", dllName + ".dll" }
            };

            File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, _jsonOptions));
        }

        private static string NormalizePath(string path)
        {
            return path.Replace('\\', '/');
        }

        private static string ToKebabCase(string input)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < input.Length; i++)
            {
                char c = input[i];
                if (char.IsUpper(c) && i > 0 && !char.IsUpper(input[i - 1]))
                    sb.Append('-');
                sb.Append(char.ToLowerInvariant(c));
            }

            return sb.ToString();
        }

        private sealed class ManifestInfo
        {
            public ModManifest Manifest { get; set; }
            public string PathPrefix { get; set; }
        }
    }
}
