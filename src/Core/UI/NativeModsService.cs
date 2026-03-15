using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Net.Http;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using TerrariaModder.Core.Config;
using TerrariaModder.Core.Conflicts;
using TerrariaModder.Core.Input;
using TerrariaModder.Core.Logging;
using TerrariaModder.Core.Manifest;
using TerrariaModder.Core.Nexus;

namespace TerrariaModder.Core.UI
{
    internal sealed class NativeModsService
    {
        private readonly ILogger _log;
        private ConflictReport _conflictReport;
        private readonly NexusApiService _nexusApi;
        private readonly NexusAuthService _nexusAuth;
        private readonly NativeModInstallService _installService;
        private readonly NexusUpdateTracker _updateTracker;
        private readonly NativeModStateService _modStateService;
        private readonly string _downloadDir;
        private readonly string _imageCacheDir;

        public NativeModsService(ILogger log)
        {
            _log = log;
            ConflictScanner.Initialize(log);
            _nexusApi = new NexusApiService(log);
            _nexusAuth = new NexusAuthService(log, _nexusApi);
            _installService = new NativeModInstallService(log);
            _updateTracker = new NexusUpdateTracker(log);
            _modStateService = new NativeModStateService(_updateTracker);
            _downloadDir = Path.Combine(CoreConfig.Instance.CorePath, "downloads");
            _imageCacheDir = Path.Combine(CoreConfig.Instance.CorePath, "cache", "nexus-images");
            Directory.CreateDirectory(_downloadDir);
            Directory.CreateDirectory(_imageCacheDir);
        }

        public IReadOnlyList<ModInfo> GetMods() => PluginLoader.Mods;

        public IEnumerable<Keybind> GetKeybinds(string modId)
            => KeybindManager.GetKeybindsForMod(modId).OrderBy(k => k.Label);

        public List<LogEntry> GetRecentLogs(int count = 50) => LogManager.GetRecentLogs(count);

        public ConflictReport GetConflictReport(bool forceRefresh = false)
        {
            if (_conflictReport == null || forceRefresh)
                _conflictReport = ConflictScanner.Scan();
            return _conflictReport;
        }

        public List<string> GetEditableLoadOrder()
        {
            var saved = DependencyResolver.LoadUserOrder(CoreConfig.Instance.CorePath);
            var current = PluginLoader.Mods.Select(m => m.Manifest.Id).ToList();
            if (saved == null || saved.Count == 0)
                return current;

            var merged = new List<string>();
            foreach (var modId in saved)
            {
                if (current.Contains(modId) && !merged.Contains(modId))
                    merged.Add(modId);
            }

            foreach (var modId in current)
            {
                if (!merged.Contains(modId))
                    merged.Add(modId);
            }

            return merged;
        }

        public bool MoveLoadOrderEntry(List<string> order, int fromIndex, int toIndex, out string error)
        {
            error = null;

            if (fromIndex < 0 || fromIndex >= order.Count || toIndex < 0 || toIndex >= order.Count)
            {
                error = "Invalid load order index.";
                return false;
            }

            if (fromIndex == toIndex)
                return true;

            string movingModId = order[fromIndex];
            var movingMod = PluginLoader.Mods.FirstOrDefault(m => m.Manifest.Id == movingModId);
            if (movingMod == null)
            {
                error = "Mod not found.";
                return false;
            }

            if (toIndex < fromIndex)
            {
                foreach (var dependency in movingMod.Manifest.Dependencies)
                {
                    int dependencyIndex = order.IndexOf(dependency);
                    if (dependencyIndex >= 0 && dependencyIndex >= toIndex)
                    {
                        error = $"{movingMod.Manifest.Name} cannot be moved before dependency {dependency}.";
                        return false;
                    }
                }
            }
            else
            {
                for (int i = fromIndex + 1; i <= toIndex; i++)
                {
                    string otherModId = order[i];
                    var otherMod = PluginLoader.Mods.FirstOrDefault(m => m.Manifest.Id == otherModId);
                    if (otherMod != null && otherMod.Manifest.Dependencies.Contains(movingModId))
                    {
                        error = $"{movingMod.Manifest.Name} cannot be moved after dependent {otherMod.Manifest.Name}.";
                        return false;
                    }
                }
            }

            string item = order[fromIndex];
            order.RemoveAt(fromIndex);
            order.Insert(toIndex, item);
            return true;
        }

        public void SaveLoadOrder(List<string> order)
        {
            DependencyResolver.SaveLoadOrder(CoreConfig.Instance.CorePath, order);
            _log?.Info($"[NativeMods] Saved load order: {string.Join(", ", order)}");
            _conflictReport = null;
        }

        public bool ModSupportsHotReload(ModInfo mod)
        {
            if (mod?.Instance == null) return false;

            try
            {
                return mod.Instance.GetType().GetMethod("OnConfigChanged",
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly) != null;
            }
            catch
            {
                return false;
            }
        }

        public bool ModNeedsRestart(ModInfo mod)
        {
            if (mod == null) return false;

            bool configChanged = (mod.Context?.Config as ModConfig)?.HasChangesFromBaseline() ?? false;
            bool keybindChanged = KeybindManager.HasKeybindChangesFromBaseline(mod.Manifest.Id);
            return !ModSupportsHotReload(mod) && (configChanged || keybindChanged);
        }

        public void ResetConfigToDefaults(ModInfo mod)
        {
            if (mod?.Context?.Config == null) return;
            mod.Context.Config.ResetToDefaults();
            AutoSaveConfig(mod.Manifest.Id);
        }

        public void SetConfigValue(ModInfo mod, ConfigField field, object value)
        {
            if (mod?.Context?.Config == null || field == null) return;
            mod.Context.Config.Set(field.Key, value);
            AutoSaveConfig(mod.Manifest.Id);
        }

        public void AutoSaveConfig(string modId)
        {
            try
            {
                var mod = PluginLoader.Mods.FirstOrDefault(m => m.Manifest.Id == modId);
                if (mod?.Context?.Config == null) return;

                mod.Context.Config.Save();
                if (ModSupportsHotReload(mod))
                    CallOnConfigChanged(mod);
            }
            catch (Exception ex)
            {
                _log?.Error($"[NativeMods] AutoSaveConfig failed for {modId}: {ex.Message}");
            }
        }

        public void SetKeybind(Keybind keybind, KeyCombo combo)
        {
            if (keybind != null)
                KeybindManager.SetBinding(keybind.Id, combo);
        }

        public void ResetKeybind(Keybind keybind)
        {
            if (keybind != null)
                KeybindManager.ResetToDefault(keybind.Id);
        }

        public NexusApiService NexusApi => _nexusApi;
        public NexusAuthService NexusAuth => _nexusAuth;
        public NexusUpdateTracker NexusTracker => _updateTracker;

        public async Task<NexusUser> ValidateStoredNexusAuthAsync()
        {
            return await _nexusAuth.ValidateStoredKeyAsync().ConfigureAwait(false);
        }

        public Task<bool> SetManualNexusApiKeyAsync(string apiKey)
        {
            return _nexusAuth.SetManualApiKeyAsync(apiKey);
        }

        public void ClearNexusApiKey()
        {
            _nexusAuth.ClearApiKey();
        }

        public Task<string> StartNexusBrowserLoginAsync()
        {
            return _nexusAuth.StartBrowserLoginAsync();
        }

        public async Task<List<InstalledModRecord>> GetInstalledModsAsync(bool includeUpdates)
        {
            var mods = _modStateService.ScanInstalledMods();
            if (includeUpdates && _nexusAuth.HasApiKey)
                await _updateTracker.CheckForUpdatesAsync(mods, _nexusApi).ConfigureAwait(false);
            return mods;
        }

        public async Task<List<NexusMod>> BrowseNexusModsAsync(string feed)
        {
            if (!_nexusAuth.HasApiKey)
                return new List<NexusMod>();

            List<NexusMod> mods;
            switch ((feed ?? "all").ToLowerInvariant())
            {
                case "latest":
                    mods = await FilterTerrariaModderModsAsync(await _nexusApi.GetLatestAddedAsync().ConfigureAwait(false) ?? new List<NexusMod>()).ConfigureAwait(false);
                    break;
                case "trending":
                    mods = await FilterTerrariaModderModsAsync(await _nexusApi.GetTrendingAsync().ConfigureAwait(false) ?? new List<NexusMod>()).ConfigureAwait(false);
                    break;
                case "updated":
                    mods = await FilterTerrariaModderModsAsync(await _nexusApi.GetLatestUpdatedAsync().ConfigureAwait(false) ?? new List<NexusMod>()).ConfigureAwait(false);
                    break;
                default:
                    mods = await LoadAllTerrariaModderModsAsync().ConfigureAwait(false);
                    break;
            }

            await ApplyInstallStatesAsync(mods).ConfigureAwait(false);
            return mods.Where(m => m.Available).OrderBy(m => m.Name).ToList();
        }

        public async Task<List<NexusMod>> SearchNexusModsAsync(string text)
        {
            if (string.IsNullOrWhiteSpace(text) || !_nexusAuth.HasApiKey)
                return new List<NexusMod>();

            int modId = ParseNexusModId(text);
            var results = new List<NexusMod>();
            if (modId > 0)
            {
                var mod = await _nexusApi.GetModInfoAsync(modId).ConfigureAwait(false);
                if (mod != null && IsTerrariaModder(mod.Name, mod.Description ?? mod.Summary))
                    results.Add(mod);
            }
            else
            {
                var feedResults = await BrowseNexusModsAsync("all").ConfigureAwait(false);
                string query = text.Trim();
                results = feedResults.Where(m =>
                        (!string.IsNullOrEmpty(m.Name) && m.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0) ||
                        (!string.IsNullOrEmpty(m.Summary) && m.Summary.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0))
                    .ToList();
            }

            await ApplyInstallStatesAsync(results).ConfigureAwait(false);
            return results;
        }

        public async Task<NexusMod> GetNexusModDetailAsync(int modId)
        {
            var mod = await _nexusApi.GetModInfoAsync(modId).ConfigureAwait(false);
            if (mod != null && IsTerrariaModder(mod.Name, mod.Description ?? mod.Summary))
                await ApplyInstallStatesAsync(new List<NexusMod> { mod }).ConfigureAwait(false);
            return mod != null && IsTerrariaModder(mod.Name, mod.Description ?? mod.Summary) ? mod : null;
        }

        public Task<List<NexusModFile>> GetNexusModFilesAsync(int modId)
        {
            return _nexusApi.GetModFilesAsync(modId);
        }

        public async Task<InstallResult> DownloadAndInstallNexusModAsync(int modId, int fileId, ConfigPreservationMode configMode, string key = null, long? expires = null)
        {
            var links = await _nexusApi.GetDownloadLinksAsync(modId, fileId, key, expires).ConfigureAwait(false);
            if (links.Count == 0)
            {
                return new InstallResult { Error = "No download links returned from Nexus API." };
            }

            var files = await _nexusApi.GetModFilesAsync(modId).ConfigureAwait(false);
            var fileInfo = files.FirstOrDefault(f => f.FileId == fileId);
            string fileName = fileInfo?.FileName ?? ("mod_" + modId + "_" + fileId + ".zip");
            string filePath = Path.Combine(_downloadDir, Guid.NewGuid().ToString("N") + "_" + fileName);

            try
            {
                using (var http = new HttpClient())
                using (var response = await http.GetAsync(links[0].Uri).ConfigureAwait(false))
                {
                    response.EnsureSuccessStatusCode();
                    using (var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    using (var output = File.Create(filePath))
                    {
                        await stream.CopyToAsync(output).ConfigureAwait(false);
                    }
                }

                var result = await _installService.InstallModAsync(filePath, configMode).ConfigureAwait(false);
                if (result.Success && !string.IsNullOrWhiteSpace(result.InstalledModId))
                {
                    _updateTracker.RecordInstall(result.InstalledModId, modId);
                    if (!string.IsNullOrWhiteSpace(fileInfo?.Version))
                    {
                        _installService.StampManifestVersion(result.InstalledModId, fileInfo.Version);
                        _updateTracker.RecordVersion(result.InstalledModId, fileInfo.Version);
                    }
                }

                return result;
            }
            catch (Exception ex)
            {
                _log?.Warn("[Nexus] Download/install failed for " + modId + "/" + fileId + ": " + ex.Message);
                return new InstallResult { Error = ex.Message };
            }
            finally
            {
                try
                {
                    if (File.Exists(filePath))
                        File.Delete(filePath);
                }
                catch
                {
                }
            }
        }

        public void OpenNexusPage(int modId, bool filesTab = false)
        {
            string url = "https://www.nexusmods.com/terraria/mods/" + modId + (filesTab ? "?tab=files" : string.Empty);
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }

        public void UninstallMod(string modId, bool deleteSettings)
        {
            _modStateService.UninstallMod(modId, deleteSettings);
        }

        public async Task<string> EnsureNexusImageCachedAsync(NexusMod mod)
        {
            if (mod == null || string.IsNullOrWhiteSpace(mod.PictureUrl))
                return null;

            try
            {
                string filePath = Path.Combine(_imageCacheDir, mod.ModId.ToString(CultureInfo.InvariantCulture) + ".png");
                if (File.Exists(filePath) && new FileInfo(filePath).Length > 0)
                {
                    byte[] cached = File.ReadAllBytes(filePath);
                    if (!IsWebp(cached))
                        return filePath;

                    try { File.Delete(filePath); } catch { }
                }

                using (var http = new HttpClient())
                using (var response = await http.GetAsync(mod.PictureUrl).ConfigureAwait(false))
                {
                    response.EnsureSuccessStatusCode();
                    byte[] bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                    bool converted = false;

                    if (IsWebp(bytes))
                    {
                        try
                        {
                            using (var image = Image.Load(bytes))
                            using (var output = File.Create(filePath))
                            {
                                image.Save(output, new PngEncoder());
                            }
                            converted = true;
                        }
                        catch (Exception ex)
                        {
                            _log?.Warn("[Nexus] WEBP to PNG conversion failed for mod " + mod.ModId + ": " + ex.Message);
                        }
                    }

                    if (!converted)
                    {
                        File.WriteAllBytes(filePath, bytes);
                    }
                }

                return File.Exists(filePath) ? filePath : null;
            }
            catch (Exception ex)
            {
                _log?.Warn("[Nexus] Failed to cache image for mod " + mod.ModId + ": " + ex.Message);
                return null;
            }
        }

        internal string GetCachedNexusImagePath(int modId)
        {
            string filePath = Path.Combine(_imageCacheDir, modId.ToString(CultureInfo.InvariantCulture) + ".png");
            return File.Exists(filePath) ? filePath : null;
        }

        private static bool IsWebp(byte[] bytes)
        {
            return bytes != null
                && bytes.Length > 12
                && bytes[0] == (byte)'R'
                && bytes[1] == (byte)'I'
                && bytes[2] == (byte)'F'
                && bytes[3] == (byte)'F'
                && bytes[8] == (byte)'W'
                && bytes[9] == (byte)'E'
                && bytes[10] == (byte)'B'
                && bytes[11] == (byte)'P';
        }

        private async Task ApplyInstallStatesAsync(List<NexusMod> mods)
        {
            if (mods == null || mods.Count == 0)
                return;

            var installed = _modStateService.ScanInstalledMods();
            if (_nexusAuth.HasApiKey)
                await _updateTracker.CheckForUpdatesAsync(installed, _nexusApi).ConfigureAwait(false);

            var installedByNexusId = new Dictionary<int, InstalledModRecord>();
            foreach (var mod in installed)
            {
                int nexusId = _updateTracker.GetNexusModId(mod);
                if (nexusId > 0 && !installedByNexusId.ContainsKey(nexusId))
                    installedByNexusId[nexusId] = mod;
            }

            foreach (var mod in mods)
            {
                InstalledModRecord local = null;
                if (!installedByNexusId.TryGetValue(mod.ModId, out local))
                    local = FindInstalledModMatch(mod, installed);

                if (local != null)
                {
                    LinkInstalledMod(local, mod.ModId);
                    mod.IsInstalled = true;
                    mod.InstalledVersion = local.Version;
                    mod.HasNewerVersion = local.HasUpdate || NexusUpdateTracker.IsNewerVersion(mod.Version ?? string.Empty, local.Version ?? string.Empty);
                    mod.InstalledFileId = local.LatestFileId;
                }
                else
                {
                    mod.IsInstalled = false;
                    mod.InstalledVersion = null;
                    mod.HasNewerVersion = false;
                    mod.InstalledFileId = 0;
                }
            }
        }

        internal InstalledModRecord FindInstalledModMatch(NexusMod nexusMod, IEnumerable<InstalledModRecord> installedMods)
        {
            if (nexusMod == null || installedMods == null)
                return null;

            InstalledModRecord directMatch = installedMods.FirstOrDefault(mod => _updateTracker.GetNexusModId(mod) == nexusMod.ModId);
            if (directMatch != null)
                return directMatch;

            string nexusCompactName = NormalizeCompactToken(nexusMod.Name);
            string nexusCompactAuthor = NormalizeCompactToken(nexusMod.Author);
            string nexusIdCandidate = NormalizeKebabToken(nexusMod.Name);
            InstalledModRecord bestMatch = null;
            int bestScore = 0;

            foreach (var installed in installedMods)
            {
                if (installed == null || installed.IsCore)
                    continue;

                int score = 0;
                string installedIdKebab = NormalizeKebabToken(installed.Id);
                string installedIdCompact = NormalizeCompactToken(installed.Id);
                string installedNameCompact = NormalizeCompactToken(installed.Name);
                string installedAuthorCompact = NormalizeCompactToken(installed.Author);

                if (!string.IsNullOrWhiteSpace(nexusIdCandidate) && string.Equals(installedIdKebab, nexusIdCandidate, StringComparison.Ordinal))
                    score = Math.Max(score, 90);
                if (!string.IsNullOrWhiteSpace(nexusCompactName) && string.Equals(installedNameCompact, nexusCompactName, StringComparison.Ordinal))
                    score = Math.Max(score, 96);
                if (!string.IsNullOrWhiteSpace(nexusCompactName) && string.Equals(installedIdCompact, nexusCompactName, StringComparison.Ordinal))
                    score = Math.Max(score, 92);
                if (!string.IsNullOrWhiteSpace(nexusCompactAuthor) && string.Equals(installedAuthorCompact, nexusCompactAuthor, StringComparison.Ordinal))
                    score += 2;

                if (score > bestScore)
                {
                    bestScore = score;
                    bestMatch = installed;
                }
            }

            return bestScore >= 90 ? bestMatch : null;
        }

        internal void LinkInstalledMod(InstalledModRecord installed, int nexusModId)
        {
            if (installed == null || nexusModId <= 0)
                return;

            installed.NexusModId = nexusModId;
            _updateTracker.RecordInstall(installed.Id, nexusModId);
        }

        private static string NormalizeCompactToken(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            return new string(value
                .ToLowerInvariant()
                .Where(char.IsLetterOrDigit)
                .ToArray());
        }

        private static string NormalizeKebabToken(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            var chars = value.ToLowerInvariant()
                .Select(ch => char.IsLetterOrDigit(ch) ? ch : '-')
                .ToArray();
            string normalized = new string(chars);
            while (normalized.Contains("--"))
                normalized = normalized.Replace("--", "-");
            return normalized.Trim('-');
        }

        private async Task<List<NexusMod>> LoadAllTerrariaModderModsAsync()
        {
            var feedTask = Task.WhenAll(
                _nexusApi.GetLatestAddedAsync(),
                _nexusApi.GetTrendingAsync(),
                _nexusApi.GetLatestUpdatedAsync());
            var updatedTask = _nexusApi.GetUpdatedModIdsAsync("1m");
            await Task.WhenAll(feedTask, updatedTask).ConfigureAwait(false);

            var knownMods = new Dictionary<int, NexusMod>();
            foreach (var feed in feedTask.Result)
            {
                if (feed == null)
                    continue;

                foreach (var mod in feed)
                {
                    if (mod.Available && !knownMods.ContainsKey(mod.ModId))
                        knownMods[mod.ModId] = mod;
                }
            }

            var results = new List<NexusMod>();
            foreach (var mod in knownMods.Values)
            {
                if (IsTerrariaModder(mod.Name, mod.Summary))
                    results.Add(mod);
            }

            var uncheckedFeedMods = knownMods.Values.Where(m => !results.Any(r => r.ModId == m.ModId)).ToList();
            results.AddRange(await DeepCheckModsAsync(uncheckedFeedMods).ConfigureAwait(false));

            var unknownIds = updatedTask.Result.Select(e => e.ModId).Where(id => !knownMods.ContainsKey(id)).Distinct().ToList();
            if (unknownIds.Count > 0)
            {
                using (var semaphore = new SemaphoreSlim(5))
                {
                    var fetchTasks = unknownIds.Select(async modId =>
                    {
                        await semaphore.WaitAsync().ConfigureAwait(false);
                        try
                        {
                            var mod = await _nexusApi.GetModInfoAsync(modId).ConfigureAwait(false);
                            return mod != null && mod.Available && IsTerrariaModder(mod.Name, mod.Summary ?? mod.Description) ? mod : null;
                        }
                        finally
                        {
                            semaphore.Release();
                        }
                    });

                    var fetched = await Task.WhenAll(fetchTasks).ConfigureAwait(false);
                    results.AddRange(fetched.Where(m => m != null));
                }
            }

            return results
                .GroupBy(m => m.ModId)
                .Select(g => g.First())
                .ToList();
        }

        private async Task<List<NexusMod>> FilterTerrariaModderModsAsync(List<NexusMod> mods)
        {
            var results = new List<NexusMod>();
            var uncheckedMods = new List<NexusMod>();

            foreach (var mod in mods.Where(m => m != null && m.Available))
            {
                if (IsTerrariaModder(mod.Name, mod.Summary))
                    results.Add(mod);
                else
                    uncheckedMods.Add(mod);
            }

            results.AddRange(await DeepCheckModsAsync(uncheckedMods).ConfigureAwait(false));
            return results
                .GroupBy(m => m.ModId)
                .Select(g => g.First())
                .ToList();
        }

        private async Task<List<NexusMod>> DeepCheckModsAsync(List<NexusMod> mods)
        {
            if (mods == null || mods.Count == 0)
                return new List<NexusMod>();

            using (var semaphore = new SemaphoreSlim(5))
            {
                var tasks = mods.Select(async mod =>
                {
                    await semaphore.WaitAsync().ConfigureAwait(false);
                    try
                    {
                        var full = await _nexusApi.GetModInfoAsync(mod.ModId).ConfigureAwait(false);
                        return full != null && IsTerrariaModder(full.Name, full.Description ?? full.Summary) ? mod : null;
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                });

                var results = await Task.WhenAll(tasks).ConfigureAwait(false);
                return results.Where(m => m != null).ToList();
            }
        }

        private static bool IsTerrariaModder(string name, string text)
        {
            return (!string.IsNullOrWhiteSpace(name) && name.IndexOf("TerrariaModder", StringComparison.OrdinalIgnoreCase) >= 0)
                || (!string.IsNullOrWhiteSpace(text) && text.IndexOf("TerrariaModder", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static int ParseNexusModId(string text)
        {
            if (int.TryParse(text, out int directId))
                return directId;

            int markerIndex = (text ?? string.Empty).IndexOf("nexusmods.com/terraria/mods/", StringComparison.OrdinalIgnoreCase);
            if (markerIndex >= 0)
            {
                string tail = text.Substring(markerIndex);
                int modsIndex = tail.IndexOf("mods/", StringComparison.OrdinalIgnoreCase);
                if (modsIndex >= 0)
                {
                    string digits = new string(tail.Substring(modsIndex + 5).TakeWhile(char.IsDigit).ToArray());
                    if (int.TryParse(digits, out int parsed))
                        return parsed;
                }
            }

            return 0;
        }

        private void CallOnConfigChanged(ModInfo mod)
        {
            try
            {
                var method = mod.Instance.GetType().GetMethod("OnConfigChanged",
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                method?.Invoke(mod.Instance, null);
            }
            catch (Exception ex)
            {
                _log?.Error($"[NativeMods] OnConfigChanged failed for {mod.Manifest.Id}: {ex.Message}");
            }
        }
    }
}
