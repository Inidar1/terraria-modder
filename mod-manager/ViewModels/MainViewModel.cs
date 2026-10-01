using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Windows.Input;
using Avalonia.Media;
using Avalonia.Threading;
using MsBox.Avalonia.Enums;
using TerrariaModManager.Helpers;
using TerrariaModManager.Models;
using TerrariaModManager.Services;

namespace TerrariaModManager.ViewModels;

public class MainViewModel : ViewModelBase
{
    private const int VaultNexusModId = 159;
    // Derive exe name from the running process so it matches regardless of how the user named it.
    private static readonly string AppExeName =
        Path.GetFileName(Environment.ProcessPath)
        ?? (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "TerrariaModderVault.exe" : "TerrariaModderVault");

    private ViewModelBase _currentView;
    private string _statusText = "Ready";
    private bool _needsSetup;
    private bool _isNxmRegistered;

    private bool _vaultUpdateAvailable;
    private string _vaultLatestVersion = "";
    private int _vaultLatestFileId;
    private bool _isVaultUpdating;

    private static readonly IBrush NxmRegisteredBrush = new SolidColorBrush(Color.Parse("#FF58EB1C"));
    private static readonly IBrush NxmNotRegisteredBrush = new SolidColorBrush(Color.Parse("#FFE05252"));

    private readonly SettingsService _settingsService;
    private readonly ModStateService _modState;
    private readonly NxmProtocolRegistrar _nxmRegistrar;
    private readonly DownloadManager _downloadManager;
    private readonly NxmLinkHandler _nxmHandler;
    private readonly NexusApiService _nexusApi;
    private readonly AppPaths _paths;
    private AppSettings _appSettings = null!;

    public InstalledModsViewModel InstalledModsVm { get; }
    public BrowseViewModel BrowseVm { get; }
    public DownloadsViewModel DownloadsVm { get; }
    public SettingsViewModel SettingsVm { get; }

    public Func<string, string, string?, Task<InlineBrowserResult?>>? OpenBrowser { get; set; }

    /// <summary>
    /// Set by the view to allow registering a callback that fires the moment a file download
    /// begins in the inline browser (i.e. before the download completes).
    /// </summary>
    public Action<Action>? SetBrowserDownloadCallback { get; set; }

    public ViewModelBase CurrentView
    {
        get => _currentView;
        set
        {
            if (SetProperty(ref _currentView, value))
            {
                OnPropertyChanged(nameof(IsInstalledActive));
                OnPropertyChanged(nameof(IsBrowseActive));
                OnPropertyChanged(nameof(IsDownloadsActive));
                OnPropertyChanged(nameof(IsSettingsActive));
            }
        }
    }

    public bool IsInstalledActive => _currentView is InstalledModsViewModel;
    public bool IsBrowseActive => _currentView is BrowseViewModel;
    public bool IsDownloadsActive => _currentView is DownloadsViewModel;
    public bool IsSettingsActive => _currentView is SettingsViewModel;

    public bool HasActiveDownloads => _downloadManager.Downloads.Any(d => d.IsDownloading);
    public int ActiveDownloadCount => _downloadManager.Downloads.Count(d => d.IsDownloading);
    public string DownloadStatusText
    {
        get
        {
            var count = ActiveDownloadCount;
            return count == 1 ? "1 downloading" : $"{count} downloading";
        }
    }

    public string StatusText { get => _statusText; set => SetProperty(ref _statusText, value); }
    public bool NeedsSetup { get => _needsSetup; set => SetProperty(ref _needsSetup, value); }

    public bool IsNxmRegistered
    {
        get => _isNxmRegistered;
        set
        {
            if (SetProperty(ref _isNxmRegistered, value))
            {
                OnPropertyChanged(nameof(NxmStatusText));
                OnPropertyChanged(nameof(NxmStatusValue));
                OnPropertyChanged(nameof(NxmStatusColor));
            }
        }
    }

    public string NxmStatusHeader => "Vortex Link Support";
    public string NxmStatusValue => IsNxmRegistered ? "Enabled" : "Disabled";
    public string NxmStatusText => IsNxmRegistered
        ? "Listening to \"Vortex\" Links: Enabled"
        : "Listening to \"Vortex\" Links: Disabled";
    public IBrush NxmStatusColor => IsNxmRegistered ? NxmRegisteredBrush : NxmNotRegisteredBrush;

    // --- Vault version / update ---

    public string VaultVersion { get; } = GetAppVersion();

    public bool VaultUpdateAvailable
    {
        get => _vaultUpdateAvailable;
        set { if (SetProperty(ref _vaultUpdateAvailable, value)) OnPropertyChanged(nameof(VaultVersionLabel)); }
    }

    public string VaultLatestVersion
    {
        get => _vaultLatestVersion;
        set { if (SetProperty(ref _vaultLatestVersion, value)) OnPropertyChanged(nameof(VaultVersionLabel)); }
    }

    public bool IsVaultUpdating
    {
        get => _isVaultUpdating;
        set { if (SetProperty(ref _isVaultUpdating, value)) OnPropertyChanged(nameof(VaultUpdateButtonText)); }
    }

    public string VaultVersionLabel => VaultUpdateAvailable
        ? $"v{VaultVersion} -> v{VaultLatestVersion}"
        : $"v{VaultVersion}";

    public string VaultUpdateButtonText => IsVaultUpdating ? "Updating..." : "Update";

    // --- Commands ---

    public ICommand ShowInstalledCommand { get; }
    public ICommand ShowBrowseCommand { get; }
    public ICommand ShowDownloadsCommand { get; }
    public ICommand ShowSettingsCommand { get; }
    public ICommand LaunchModdedCommand { get; }
    public ICommand LaunchVanillaCommand { get; }
    public ICommand RegisterNxmCommand { get; }
    public ICommand UpdateVaultCommand { get; }

    public MainViewModel(
        InstalledModsViewModel installedModsVm,
        BrowseViewModel browseVm,
        DownloadsViewModel downloadsVm,
        SettingsViewModel settingsVm,
        SettingsService settingsService,
        ModStateService modState,
        NxmProtocolRegistrar nxmRegistrar,
        DownloadManager downloadManager,
        NxmLinkHandler nxmHandler,
        NexusApiService nexusApi,
        AppPaths paths)
    {
        InstalledModsVm = installedModsVm;
        BrowseVm = browseVm;
        DownloadsVm = downloadsVm;
        SettingsVm = settingsVm;
        _settingsService = settingsService;
        _modState = modState;
        _nxmRegistrar = nxmRegistrar;
        _downloadManager = downloadManager;
        _nxmHandler = nxmHandler;
        _nexusApi = nexusApi;
        _paths = paths;

        _appSettings = _settingsService.Load();
        NeedsSetup = string.IsNullOrWhiteSpace(_appSettings.TerrariaPath);
        _currentView = NeedsSetup ? SettingsVm : InstalledModsVm;

        SettingsVm.LoginSucceeded += () =>
            Dispatcher.UIThread.InvokeAsync(() =>
            {
                _ = BrowseVm.LoadFeedAsync();
                _ = CheckVaultUpdateAsync();
            });

        // When a free user successfully downloads via browser, mark them as logged in
        BrowseVm.BrowserDownloadSucceeded += () =>
            Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!SettingsVm.IsLoggedIn)
                {
                    SettingsVm.IsLoggedIn = true;
                    SettingsVm.MarkBrowserAuthenticated();
                }
            });

        ShowInstalledCommand = new RelayCommand(() =>
        {
            SyncNxmStatus();
            CurrentView = InstalledModsVm;
            InstalledModsVm.Refresh();
            if (_settingsService.Load().AutoCheckForUpdates)
                InstalledModsVm.StartUpdateCheck();
        });
        ShowBrowseCommand = new RelayCommand(() =>
        {
            SyncNxmStatus();
            CurrentView = BrowseVm;
            if (BrowseVm.Mods.Count == 0) _ = BrowseVm.LoadFeedAsync();
            else BrowseVm.RefreshInstallStates();
        });
        ShowDownloadsCommand = new RelayCommand(() => { SyncNxmStatus(); CurrentView = DownloadsVm; });
        ShowSettingsCommand = new RelayCommand(() =>
        {
            SyncNxmStatus();
            CurrentView = SettingsVm;
            SettingsVm.RefreshCoreInfo();
        });
        LaunchModdedCommand = new RelayCommand(() => LaunchGame(modded: true));
        LaunchVanillaCommand = new RelayCommand(() => LaunchGame(modded: false));
        RegisterNxmCommand = new AsyncRelayCommand(RegisterNxm);
        UpdateVaultCommand = new AsyncRelayCommand(DoVaultUpdateAsync);
        InstalledModsVm.NavigateToBrowseCommand = new RelayCommand(() => ShowBrowseCommand.Execute(null));
        BrowseVm.NavigateToSettingsCommand = new RelayCommand(() => ShowSettingsCommand.Execute(null));

        _isNxmRegistered = _nxmRegistrar.IsRegistered();
        _nxmRegistrar.RegistrationChanged += SyncNxmStatus;

        CheckUpdateLog();

        if (!NeedsSetup)
        {
            InstalledModsVm.Refresh();
            SettingsVm.LoadFromSettings();
            UpdateStatus();
            if (_appSettings.AutoCheckForUpdates)
                InstalledModsVm.StartUpdateCheck();
            _ = CheckVaultUpdateAsync();
        }
        else
        {
            StatusText = "Welcome! Set your Terraria path to get started.";
            SettingsVm.LoadFromSettings();
            SettingsVm.AutoDetect();
            if (!string.IsNullOrWhiteSpace(SettingsVm.TerrariaPath))
            {
                _appSettings = _settingsService.Load();
                NeedsSetup = false;
                _currentView = InstalledModsVm;
                InstalledModsVm.Refresh();
                UpdateStatus();
                if (_appSettings.AutoCheckForUpdates)
                    InstalledModsVm.StartUpdateCheck();
                _ = CheckVaultUpdateAsync();
            }
        }

        _downloadManager.DownloadCompleted += _ =>
        {
            try
            {
                Dispatcher.UIThread.InvokeAsync(() =>
                {
                    InstalledModsVm.Refresh();
                    BrowseVm.RefreshInstallStates();
                    BrowseVm.ApplyCurrentSort();
                    UpdateStatus();
                    NotifyDownloadStatus();
                });
            }
            catch (InvalidOperationException) { }
        };

        _downloadManager.DownloadFailed += (_, _) =>
        {
            try
            {
                Dispatcher.UIThread.InvokeAsync(() =>
                {
                    BrowseVm.RefreshInstallStates();
                    UpdateStatus();
                    NotifyDownloadStatus();
                });
            }
            catch (InvalidOperationException) { }
        };

        _downloadManager.Downloads.CollectionChanged += (_, e) =>
        {
            NotifyDownloadStatus();
            if (e.NewItems != null)
                foreach (DownloadItem item in e.NewItems)
                    item.PropertyChanged += (_, _) => NotifyDownloadStatus();
        };
    }

    public void HandleNxmLink(NxmLink link)
    {
        CurrentView = DownloadsVm;
        _ = _downloadManager.EnqueueAsync(link.ModId, link.FileId, link.Key, link.Expires);
        StatusText = $"Downloading mod {link.ModId}...";
    }

    // -----------------------------------------------------------------------
    // Vault self-update
    // -----------------------------------------------------------------------

    private async Task CheckVaultUpdateAsync()
    {
        if (!_nexusApi.HasApiKey) return;
        try
        {
            var files = await _nexusApi.GetModFilesAsync(VaultNexusModId);
            if (files.Count == 0) return;

            var mainFile = NexusFileSelector.SelectCurrentMain(files);
            if (mainFile == null) return;

            if (string.IsNullOrWhiteSpace(mainFile.Version)) return;

            if (UpdateTracker.IsNewerVersion(mainFile.Version, VaultVersion))
            {
                _vaultLatestFileId = mainFile.FileId;
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    VaultLatestVersion = mainFile.Version;
                    VaultUpdateAvailable = true;
                });
            }
        }
        catch { }
    }

    private async Task DoVaultUpdateAsync()
    {
        if (IsVaultUpdating) return;

        if (_nexusApi.IsPremium)
        {
            IsVaultUpdating = true;
            try
            {
                StatusText = "Getting download link...";
                var links = await _nexusApi.GetDownloadLinksAsync(VaultNexusModId, _vaultLatestFileId);
                if (links.Count == 0)
                {
                    StatusText = "Update failed: no download link available";
                    IsVaultUpdating = false;
                    return;
                }
                await ApplyVaultUpdateAsync(links[0].Uri);
            }
            catch (Exception ex)
            {
                StatusText = $"Update failed: {ex.Message}";
                IsVaultUpdating = false;
            }
        }
        else
        {
            if (OpenBrowser == null) return;
            var url = $"https://www.nexusmods.com/terraria/mods/{VaultNexusModId}?tab=files&file_id={_vaultLatestFileId}";
            // Register callback so overlay appears the moment the download starts, not after it completes
            SetBrowserDownloadCallback?.Invoke(() => IsVaultUpdating = true);
            var result = await OpenBrowser(url, "Vault Update",
                "Click 'Manual Download' on the latest version to update");
            if (result?.DownloadedFilePath == null)
            {
                IsVaultUpdating = false; // cancelled or interrupted — dismiss overlay if it appeared
                return;
            }

            try
            {
                await ApplyVaultUpdateFromFileAsync(result.DownloadedFilePath);
            }
            catch (Exception ex)
            {
                StatusText = $"Update failed: {ex.Message}";
                IsVaultUpdating = false;
            }
        }
    }

    private string UpdateLogPath => _paths.UpdateLogFile;

    private void CheckUpdateLog()
    {
        if (!File.Exists(UpdateLogPath)) return;
        try
        {
            var content = File.ReadAllText(UpdateLogPath).Trim();
            File.Delete(UpdateLogPath);
            if (content.Contains("FAILED"))
                StatusText = $"Last update failed: {content.Split('\n').LastOrDefault(l => l.Contains("FAILED"))?.Trim()}";
        }
        catch { }
    }

    private async Task ApplyVaultUpdateAsync(string downloadUrl)
    {
        var tempDir = Path.Combine(_paths.UpdateDirectory, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var zipPath = Path.Combine(tempDir, "update.zip");

        try
        {
            using var http = new HttpClient();
            using var resp = await http.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead);
            resp.EnsureSuccessStatusCode();

            var total = resp.Content.Headers.ContentLength ?? -1L;
            long downloaded = 0;
            using (var fs = File.OpenWrite(zipPath))
            using (var stream = await resp.Content.ReadAsStreamAsync())
            {
                var buffer = new byte[65536];
                int read;
                while ((read = await stream.ReadAsync(buffer)) > 0)
                {
                    await fs.WriteAsync(buffer.AsMemory(0, read));
                    downloaded += read;
                    if (total > 0)
                        StatusText = $"Downloading update ({downloaded * 100 / total}%)...";
                }
            }

            // Verify download integrity: size must match Content-Length
            if (total > 0)
            {
                var actualSize = new FileInfo(zipPath).Length;
                if (actualSize != total)
                {
                    StatusText = $"Update download incomplete ({actualSize}/{total} bytes). Please retry.";
                    try { Directory.Delete(tempDir, true); } catch { }
                    IsVaultUpdating = false;
                    return;
                }
            }

            await ApplyZipUpdateAsync(zipPath, tempDir);
        }
        catch
        {
            try { Directory.Delete(tempDir, true); } catch { }
            IsVaultUpdating = false;
            throw;
        }
    }

    // Used when the user manually downloads the zip (free-user path).
    private async Task ApplyVaultUpdateFromFileAsync(string sourceZipPath)
    {
        var tempDir = Path.Combine(_paths.UpdateDirectory, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var zipPath = Path.Combine(tempDir, "update.zip");
        try
        {
            File.Copy(sourceZipPath, zipPath, overwrite: true);
            await ApplyZipUpdateAsync(zipPath, tempDir);
        }
        catch
        {
            try { Directory.Delete(tempDir, true); } catch { }
            IsVaultUpdating = false;
            throw;
        }
    }

    private async Task ApplyZipUpdateAsync(string zipPath, string tempDir)
    {
        StatusText = "Extracting update...";
        var extractDir = Path.Combine(tempDir, "extracted");
        ExtractUpdateArchive(zipPath, extractDir);

        var (sourceDir, foundExeName) = FindExtractedAppDir(extractDir);
        if (foundExeName == null)
        {
            var found = Directory.GetFiles(extractDir, "*", SearchOption.AllDirectories)
                .Select(Path.GetFileName).Distinct().Take(6);
            StatusText = $"Update failed: package must contain exactly one {AppExeName}. Contents: {string.Join(", ", found)}";
            try { Directory.Delete(tempDir, true); } catch { }
            IsVaultUpdating = false;
            return;
        }

        var destDir = AppContext.BaseDirectory.TrimEnd('/', '\\');

        // Check write permission before committing to the update
        try
        {
            var probe = Path.Combine(destDir, ".update_probe");
            File.WriteAllText(probe, "");
            File.Delete(probe);
        }
        catch
        {
            StatusText = "Update failed: no write permission to app directory (try running as administrator)";
            try { Directory.Delete(tempDir, true); } catch { }
            IsVaultUpdating = false;
            return;
        }

        WriteAndLaunchUpdaterScript(sourceDir, destDir, foundExeName, tempDir);

        await Dispatcher.UIThread.InvokeAsync(() => StatusText = "Restarting to apply update...");
        await Task.Delay(800);
        Environment.Exit(0);
    }

    // Returns the package directory only when it contains this application's exact executable name.
    internal static (string dir, string? exeName) FindExtractedAppDir(string extractRoot, string? expectedExeName = null)
    {
        expectedExeName ??= AppExeName;
        var comparison = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var matches = Directory.GetFiles(extractRoot, "*", SearchOption.AllDirectories)
            .Where(path => string.Equals(Path.GetFileName(path), expectedExeName, comparison))
            .ToArray();
        return matches.Length == 1
            ? (Path.GetDirectoryName(matches[0])!, Path.GetFileName(matches[0]))
            : (extractRoot, null);
    }

    internal static void ExtractUpdateArchive(string zipPath, string extractDir)
    {
        const long maxBytes = 1_000_000_000;
        const int maxEntries = 20_000;
        Directory.CreateDirectory(extractDir);
        var fullRoot = Path.GetFullPath(extractDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var destinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var zip = ZipFile.OpenRead(zipPath);
        if (zip.Entries.Count > maxEntries)
            throw new InvalidOperationException($"Update archive contains too many files (>{maxEntries:N0}).");
        if (zip.Entries.Sum(entry => entry.Length) > maxBytes)
            throw new InvalidOperationException("Update archive expands beyond 1GB.");

        long actualBytes = 0;
        var buffer = new byte[128 * 1024];
        foreach (var entry in zip.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name)) continue;
            var destination = Path.GetFullPath(Path.Combine(extractDir,
                entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
            if (!destination.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Update entry '{entry.FullName}' leaves the package directory.");
            if (!destinations.Add(destination))
                throw new InvalidOperationException($"Update archive contains duplicate destination '{entry.FullName}'.");
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            using var input = entry.Open();
            using var output = File.Create(destination);
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                actualBytes += read;
                if (actualBytes > maxBytes)
                    throw new InvalidOperationException("Update archive expands beyond 1GB.");
                output.Write(buffer, 0, read);
            }
        }
    }

    private void WriteAndLaunchUpdaterScript(string sourceDir, string destDir, string exeName, string cleanupDir)
    {
        var pid = Environment.ProcessId;
        var logPath = UpdateLogPath;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Directory.CreateDirectory(_paths.UpdateDirectory);
            var scriptPath = Path.Combine(_paths.UpdateDirectory, $"vault_update_{pid}.ps1");
            var exePath = Path.Combine(destDir, exeName);
            // Use variable assignment so paths with spaces and apostrophes are safe.
            // robocopy /E /IS /IT: copy all package files, overwriting same/newer/tweaked.
            // Exit codes 0-7 are success (bit flags for what was copied); >=8 means error.
            var script = string.Join("\r\n",
                $"$appPid = {pid}",
                $"$src = \"{EscapeForDoubleQuotedPs(sourceDir)}\"",
                $"$dst = \"{EscapeForDoubleQuotedPs(destDir)}\"",
                $"$exe = \"{EscapeForDoubleQuotedPs(exePath)}\"",
                $"$log = \"{EscapeForDoubleQuotedPs(logPath)}\"",
                $"$cleanup = \"{EscapeForDoubleQuotedPs(cleanupDir)}\"",
                "$rollback = Join-Path $cleanup 'rollback'",
                "$newFiles = [System.Collections.Generic.List[string]]::new()",
                "\"Updater started, waiting for PID $appPid\" | Set-Content $log",
                "while (Get-Process -Id $appPid -ErrorAction SilentlyContinue) { Start-Sleep 1 }",
                "try {",
                "  New-Item -ItemType Directory -Path $rollback -Force | Out-Null",
                "  Get-ChildItem $src -File -Recurse | ForEach-Object {",
                "    $relative = [IO.Path]::GetRelativePath($src, $_.FullName)",
                "    $existing = Join-Path $dst $relative",
                "    if (Test-Path -LiteralPath $existing) {",
                "      $backup = Join-Path $rollback $relative",
                "      New-Item -ItemType Directory -Path (Split-Path $backup -Parent) -Force | Out-Null",
                "      Copy-Item -LiteralPath $existing -Destination $backup -Force",
                "    } else { $newFiles.Add($existing) }",
                "  }",
                "  & robocopy $src $dst /E /IS /IT /NFL /NDL /NJH /NJS | Out-Null",
                "  if ($LASTEXITCODE -ge 8) { throw \"robocopy exit $LASTEXITCODE\" }",
                "  $updated = Start-Process -FilePath $exe -PassThru -ErrorAction Stop",
                "  Start-Sleep -Milliseconds 1200",
                "  if ($updated.HasExited) { throw 'updated app exited during startup' }",
                "  \"Copy succeeded (robocopy $LASTEXITCODE)\" | Add-Content $log",
                "} catch {",
                "  $failure = $_.Exception.Message",
                "  foreach ($file in $newFiles) { Remove-Item -LiteralPath $file -Force -ErrorAction SilentlyContinue }",
                "  & robocopy $rollback $dst /E /IS /IT /NFL /NDL /NJH /NJS | Out-Null",
                "  \"FAILED and rolled back: $failure\" | Add-Content $log",
                "  if (Test-Path -LiteralPath $exe) { Start-Process -FilePath $exe -ErrorAction SilentlyContinue }",
                "}",
                "Remove-Item $cleanup -Recurse -Force -ErrorAction SilentlyContinue",
                "Remove-Item $MyInvocation.MyCommand.Path -Force -ErrorAction SilentlyContinue");
            File.WriteAllText(scriptPath, script);
            Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -NonInteractive -File \"{scriptPath}\"",
                CreateNoWindow = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden
            });
        }
        else
        {
            Directory.CreateDirectory(_paths.UpdateDirectory);
            var scriptPath = Path.Combine(_paths.UpdateDirectory, $"vault_update_{pid}.sh");
            var exePath = Path.Combine(destDir, exeName);
            // Use double-quoted variables so paths with spaces are safe.
            // Back up replaced files and track new files so a failed copy/start can be rolled back.
            var script = string.Join("\n",
                "#!/bin/bash",
                "set -u",
                $"app_pid={pid}",
                $"src=\"{EscapeForDoubleQuotedBash(sourceDir)}\"",
                $"dst=\"{EscapeForDoubleQuotedBash(destDir)}\"",
                $"exe=\"{EscapeForDoubleQuotedBash(exePath)}\"",
                $"log=\"{EscapeForDoubleQuotedBash(logPath)}\"",
                $"cleanup=\"{EscapeForDoubleQuotedBash(cleanupDir)}\"",
                "rollback=\"$cleanup/rollback\"",
                "new_files=\"$cleanup/new-files.txt\"",
                "echo \"Updater started\" > \"$log\"",
                "while kill -0 \"$app_pid\" 2>/dev/null; do sleep 1; done",
                "mkdir -p \"$rollback\"",
                ": > \"$new_files\"",
                "while IFS= read -r -d '' file; do",
                "  relative=\"${file#\"$src/\"}\"",
                "  existing=\"$dst/$relative\"",
                "  if [ -f \"$existing\" ]; then",
                "    mkdir -p \"$(dirname \"$rollback/$relative\")\"",
                "    cp -p -- \"$existing\" \"$rollback/$relative\"",
                "  else printf '%s\\0' \"$existing\" >> \"$new_files\"; fi",
                "done < <(find \"$src\" -type f -print0)",
                "failure=''",
                "cp -a \"$src/.\" \"$dst/\" || failure='copy failed'",
                "if [ -z \"$failure\" ]; then chmod +x \"$exe\" || failure='chmod failed'; fi",
                "if [ -z \"$failure\" ]; then \"$exe\" >/dev/null 2>&1 & new_pid=$!; sleep 1; kill -0 \"$new_pid\" 2>/dev/null || failure='updated app did not start'; fi",
                "if [ -n \"$failure\" ]; then",
                "  while IFS= read -r -d '' file; do rm -f -- \"$file\"; done < \"$new_files\"",
                "  cp -a \"$rollback/.\" \"$dst/\"",
                "  echo \"FAILED and rolled back: $failure\" >> \"$log\"",
                "  chmod +x \"$exe\" 2>/dev/null || true",
                "  \"$exe\" >/dev/null 2>&1 &",
                "else echo \"Copy succeeded\" >> \"$log\"; fi",
                "rm -rf -- \"$cleanup\"",
                "rm -- \"$0\"");
            File.WriteAllText(scriptPath, script);
            File.SetUnixFileMode(scriptPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Process.Start(new ProcessStartInfo
            {
                FileName = "/bin/bash",
                Arguments = scriptPath,
                CreateNoWindow = true,
                UseShellExecute = false
            });
        }
    }

    // Escape a path for use inside a double-quoted PowerShell string.
    // In PS double-quoted strings the only special char that needs escaping is backtick (`) and $.
    // Double-quotes inside a double-quoted string are escaped as `".
    // Backslashes are literal — no escaping needed.
    private static string EscapeForDoubleQuotedPs(string path) =>
        path.Replace("`", "``").Replace("\"", "`\"").Replace("$", "`$");

    // Escape a path for use inside a double-quoted bash string.
    // In bash "..." the special chars are: $ ` " \ and !.
    private static string EscapeForDoubleQuotedBash(string path) =>
        path.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("$", "\\$").Replace("`", "\\`");

    private static string GetAppVersion()
    {
        var v = typeof(MainViewModel).Assembly.GetName().Version;
        return v != null ? $"{v.Major}.{v.Minor}.{v.Build}" : "0.1.0";
    }

    // -----------------------------------------------------------------------
    // Misc
    // -----------------------------------------------------------------------

    private void LaunchGame(bool modded)
    {
        var path = _appSettings.TerrariaPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            StatusText = "Set your Terraria path in Settings first";
            return;
        }
        var injector = System.IO.Path.Combine(path, "TerrariaInjector.exe");
        var terraria = System.IO.Path.Combine(path, "Terraria.exe");
        string exe, label;
        if (modded)
        {
            exe = System.IO.File.Exists(injector) ? injector : terraria;
            label = System.IO.File.Exists(injector) ? "modded" : "vanilla (injector not found)";
        }
        else { exe = terraria; label = "vanilla"; }

        if (!System.IO.File.Exists(exe)) { StatusText = "Could not find Terraria executable"; return; }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = exe, WorkingDirectory = path, UseShellExecute = true
            });
            StatusText = $"Launching Terraria ({label})...";
        }
        catch (Exception ex) { StatusText = $"Failed to launch: {ex.Message}"; }
    }

    private async Task RegisterNxm()
    {
        if (IsNxmRegistered) return;
        try
        {
            _nxmRegistrar.Register();
            IsNxmRegistered = true;
            SettingsVm.IsNxmRegistered = true;
            _appSettings.NxmRegistered = true;
            _settingsService.Save(_appSettings);
            await DialogHelper.ShowDialog("Registered",
                "\"Download with Vortex\" links will now open in TerrariaModder Vault.\n\nYou can unregister in Settings if needed.",
                ButtonEnum.Ok, Icon.Info);
        }
        catch (Exception ex)
        {
            await DialogHelper.ShowDialog("Error",
                $"Failed to register nxm:// handler: {ex.Message}",
                ButtonEnum.Ok, Icon.Error);
        }
    }

    private void SyncNxmStatus() => IsNxmRegistered = _nxmRegistrar.IsRegistered();

    private void NotifyDownloadStatus()
    {
        OnPropertyChanged(nameof(HasActiveDownloads));
        OnPropertyChanged(nameof(ActiveDownloadCount));
        OnPropertyChanged(nameof(DownloadStatusText));
    }

    private void UpdateStatus()
    {
        var path = _appSettings.TerrariaPath;
        if (string.IsNullOrWhiteSpace(path)) { StatusText = "No Terraria path configured"; return; }
        var coreInfo = _modState.GetCoreInfo(path);
        var modCount = InstalledModsVm.EnabledCount + InstalledModsVm.DisabledCount;
        if (coreInfo.IsInstalled)
            StatusText = $"Core v{coreInfo.CoreVersion} | {modCount} mod(s) installed";
        else
            StatusText = $"TerrariaModder Core not installed | {modCount} mod(s) found";
    }
}
