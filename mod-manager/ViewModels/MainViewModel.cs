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
        NexusApiService nexusApi)
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

        _appSettings = _settingsService.Load();
        NeedsSetup = string.IsNullOrWhiteSpace(_appSettings.TerrariaPath);
        _currentView = NeedsSetup ? SettingsVm : InstalledModsVm;

        InstalledModsVm.NavigateToBrowseCommand = new RelayCommand(() => ShowBrowseCommand.Execute(null));
        BrowseVm.NavigateToSettingsCommand = new RelayCommand(() => ShowSettingsCommand.Execute(null));
        SettingsVm.LoginSucceeded += () =>
            Dispatcher.UIThread.InvokeAsync(() =>
            {
                _ = BrowseVm.LoadFeedAsync();
                _ = CheckVaultUpdateAsync();
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

            var mainFile = files.FirstOrDefault(f => f.IsPrimary)
                ?? files.OrderByDescending(f => f.UploadedTimestamp).First();

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

    private static string UpdateLogPath =>
        Path.Combine(Path.GetTempPath(), "TerrariaModderVault_updater.log");

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
        var tempDir = Path.Combine(Path.GetTempPath(), $"TerrariaModderVault_Update_{Guid.NewGuid():N}");
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
        var tempDir = Path.Combine(Path.GetTempPath(), $"TerrariaModderVault_Update_{Guid.NewGuid():N}");
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
        ZipFile.ExtractToDirectory(zipPath, extractDir);

        var (sourceDir, foundExeName) = FindExtractedAppDir(extractDir);
        if (foundExeName == null)
        {
            var found = Directory.GetFiles(extractDir, "*", SearchOption.AllDirectories)
                .Select(Path.GetFileName).Distinct().Take(6);
            StatusText = $"Update failed: no executable found in zip. Contents: {string.Join(", ", found)}";
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

        WriteAndLaunchUpdaterScript(sourceDir, destDir, foundExeName);

        await Dispatcher.UIThread.InvokeAsync(() => StatusText = "Restarting to apply update...");
        await Task.Delay(800);
        Environment.Exit(0);
    }

    // Returns (directory, exeFileName) where the app exe was found, or (root, null) if not found.
    // Matches exact name first, then falls back to any .exe (Windows) or any non-extension file (Linux).
    private static (string dir, string? exeName) FindExtractedAppDir(string extractRoot)
    {
        static string? FindExeIn(string dir)
        {
            if (File.Exists(Path.Combine(dir, AppExeName))) return AppExeName;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // Pick the largest .exe — most likely to be the app, not a helper
                var exes = Directory.GetFiles(dir, "*.exe");
                return exes.Length > 0
                    ? Path.GetFileName(exes.OrderByDescending(f => new FileInfo(f).Length).First())
                    : null;
            }
            else
            {
                // On Linux look for a file with no extension that is large enough to be the app
                var candidates = Directory.GetFiles(dir)
                    .Where(f => !Path.HasExtension(f) && new FileInfo(f).Length > 1_000_000)
                    .ToArray();
                return candidates.Length > 0
                    ? Path.GetFileName(candidates.OrderByDescending(f => new FileInfo(f).Length).First())
                    : null;
            }
        }

        var name = FindExeIn(extractRoot);
        if (name != null) return (extractRoot, name);

        foreach (var sub in Directory.GetDirectories(extractRoot))
        {
            name = FindExeIn(sub);
            if (name != null) return (sub, name);
        }

        foreach (var sub in Directory.GetDirectories(extractRoot))
            foreach (var sub2 in Directory.GetDirectories(sub))
            {
                name = FindExeIn(sub2);
                if (name != null) return (sub2, name);
            }

        return (extractRoot, null);
    }

    private static void WriteAndLaunchUpdaterScript(string sourceDir, string destDir, string exeName)
    {
        var pid = Environment.ProcessId;
        var logPath = UpdateLogPath;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var scriptPath = Path.Combine(Path.GetTempPath(), $"vault_update_{pid}.ps1");
            var exePath = Path.Combine(destDir, exeName);
            // Use variable assignment so paths with spaces and apostrophes are safe.
            // robocopy /E /IS /IT: mirror all files, overwrite same/newer/tweaked.
            // Exit codes 0-7 are success (bit flags for what was copied); >=8 means error.
            var script = string.Join("\r\n",
                $"$appPid = {pid}",
                $"$src = \"{EscapeForDoubleQuotedPs(sourceDir)}\"",
                $"$dst = \"{EscapeForDoubleQuotedPs(destDir)}\"",
                $"$exe = \"{EscapeForDoubleQuotedPs(exePath)}\"",
                $"$log = \"{EscapeForDoubleQuotedPs(logPath)}\"",
                "\"Updater started, waiting for PID $appPid\" | Set-Content $log",
                "while (Get-Process -Id $appPid -ErrorAction SilentlyContinue) { Start-Sleep 1 }",
                "\"Process exited, copying files...\" | Add-Content $log",
                "& robocopy $src $dst /E /IS /IT /NFL /NDL /NJH /NJS | Out-Null",
                "if ($LASTEXITCODE -ge 8) { \"FAILED: robocopy exit $LASTEXITCODE\" | Add-Content $log }",
                "else { \"Copy succeeded (robocopy $LASTEXITCODE)\" | Add-Content $log }",
                "Start-Process $exe",
                "Remove-Item $src -Recurse -Force -ErrorAction SilentlyContinue",
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
            var scriptPath = Path.Combine(Path.GetTempPath(), $"vault_update_{pid}.sh");
            var exePath = Path.Combine(destDir, exeName);
            // Use double-quoted variables so paths with spaces are safe.
            // Apostrophes in paths are also fine inside "..." in bash.
            var script = string.Join("\n",
                "#!/bin/bash",
                $"app_pid={pid}",
                $"src=\"{EscapeForDoubleQuotedBash(sourceDir)}\"",
                $"dst=\"{EscapeForDoubleQuotedBash(destDir)}\"",
                $"exe=\"{EscapeForDoubleQuotedBash(exePath)}\"",
                $"log=\"{EscapeForDoubleQuotedBash(logPath)}\"",
                "echo \"Updater started\" > \"$log\"",
                "while kill -0 \"$app_pid\" 2>/dev/null; do sleep 1; done",
                "echo \"Copying files...\" >> \"$log\"",
                "cp -rf \"$src/.\" \"$dst/\" && echo \"Copy succeeded\" >> \"$log\" || echo \"FAILED: cp error $?\" >> \"$log\"",
                "chmod +x \"$exe\"",
                "\"$exe\" &",
                "rm -rf \"$src\"",
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
