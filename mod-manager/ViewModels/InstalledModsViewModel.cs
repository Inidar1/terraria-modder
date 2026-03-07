using System.Collections.ObjectModel;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using MsBox.Avalonia;
using MsBox.Avalonia.Enums;
using TerrariaModManager.Helpers;
using TerrariaModManager.Models;
using TerrariaModManager.Services;

namespace TerrariaModManager.ViewModels;

public class InstalledModsViewModel : ViewModelBase
{
    private bool _isCheckingUpdates;
    private int _updatesAvailable;
    private int _enabledCount;
    private int _disabledCount;
    private bool _isCoreInstalled;
    private bool _isDisabledExpanded = true;
    private bool _isUpdatesDismissed;
    private bool _isBulkRunning;
    private List<InstalledMod> _allMods = new();
    private CancellationTokenSource? _updateCts;
    private CancellationTokenSource? _bulkCts;

    private readonly SettingsService _settings;
    private readonly ModStateService _modState;
    private readonly UpdateTracker _updateTracker;
    private readonly NexusApiService _nexusApi;
    private readonly DownloadManager _downloadManager;
    private readonly ModInstallService _installer;
    private readonly NxmProtocolRegistrar _nxmRegistrar;

    /// <summary>
    /// Set by MainWindow to open the shared NexusBrowserPanel.
    /// Parameters: (url, modName, toastMessage?) → result or null if cancelled.
    /// </summary>
    public Func<string, string, string?, Task<InlineBrowserResult?>>? OpenBrowser { get; set; }

    public ObservableCollection<InstalledMod> Mods { get; } = new();
    public ObservableCollection<InstalledMod> EnabledMods { get; } = new();
    public ObservableCollection<InstalledMod> DisabledMods { get; } = new();
    public ObservableCollection<InstalledMod> UpdateMods { get; } = new();
    public List<InstalledMod> SelectedMods { get; } = new();

    public bool HasSelection => SelectedMods.Count > 0;
    public bool HasSingleSelection => SelectedMods.Count == 1;
    public bool AnySelectedHasUpdate => SelectedMods.Any(m => m.HasUpdate);
    public bool AnySelectedNonCore => SelectedMods.Any(m => !m.IsCore);

    public string ToggleButtonText
    {
        get
        {
            var nonCore = SelectedMods.Where(m => !m.IsCore).ToList();
            if (nonCore.Count == 0) return "Enable Selected";
            var enabledCount = nonCore.Count(m => m.IsEnabled);
            return enabledCount >= nonCore.Count - enabledCount
                ? "Disable Selected"
                : "Enable Selected";
        }
    }

    public void UpdateSelection(IList<object> selectedItems)
    {
        SelectedMods.Clear();
        foreach (var item in selectedItems)
        {
            if (item is InstalledMod mod)
                SelectedMods.Add(mod);
        }
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(HasSingleSelection));
        OnPropertyChanged(nameof(AnySelectedHasUpdate));
        OnPropertyChanged(nameof(AnySelectedNonCore));
        OnPropertyChanged(nameof(ToggleButtonText));
    }

    public bool IsCheckingUpdates
    {
        get => _isCheckingUpdates;
        set => SetProperty(ref _isCheckingUpdates, value);
    }

    public int UpdatesAvailable
    {
        get => _updatesAvailable;
        set
        {
            SetProperty(ref _updatesAvailable, value);
            OnPropertyChanged(nameof(HasUpdates));
            OnPropertyChanged(nameof(ShowUpdatesSection));
        }
    }

    public bool HasUpdates => _updatesAvailable > 0;

    public bool IsUpdatesDismissed
    {
        get => _isUpdatesDismissed;
        set
        {
            if (SetProperty(ref _isUpdatesDismissed, value))
                OnPropertyChanged(nameof(ShowUpdatesSection));
        }
    }

    public bool ShowUpdatesSection => HasUpdates && !IsUpdatesDismissed;

    public bool IsBulkRunning
    {
        get => _isBulkRunning;
        set => SetProperty(ref _isBulkRunning, value);
    }

    public bool IsDisabledExpanded
    {
        get => _isDisabledExpanded;
        set => SetProperty(ref _isDisabledExpanded, value);
    }

    public int EnabledCount
    {
        get => _enabledCount;
        set
        {
            SetProperty(ref _enabledCount, value);
            OnPropertyChanged(nameof(EnabledHeader));
        }
    }

    public int DisabledCount
    {
        get => _disabledCount;
        set
        {
            SetProperty(ref _disabledCount, value);
            OnPropertyChanged(nameof(DisabledHeader));
            OnPropertyChanged(nameof(HasDisabled));
        }
    }

    public string EnabledHeader => $"Enabled ({_enabledCount})";
    public string DisabledHeader => $"Disabled ({_disabledCount})";
    public bool HasDisabled => _disabledCount > 0;

    public bool IsCoreInstalled
    {
        get => _isCoreInstalled;
        set
        {
            SetProperty(ref _isCoreInstalled, value);
            OnPropertyChanged(nameof(IsCoreMissing));
        }
    }
    public bool IsCoreMissing => !_isCoreInstalled;


    public ICommand ToggleEnabledCommand { get; }
    public ICommand ToggleModCommand { get; }
    public ICommand ToggleDisabledExpandedCommand { get; }
    public ICommand DismissUpdatesCommand { get; }
    public ICommand? NavigateToBrowseCommand { get; set; }
    public ICommand RefreshCommand { get; }
    public ICommand CheckUpdatesCommand { get; }
    public ICommand UpdateModCommand { get; }
    public ICommand UpdateAllCommand { get; }
    public ICommand CancelBulkCommand { get; }
    public ICommand OpenModFolderCommand { get; }
    public ICommand OpenOnNexusCommand { get; }
    public ICommand UninstallCommand { get; }
    public ICommand InstallLocalCommand { get; }
    public ICommand UpdateSingleCommand { get; }
    public ICommand InstallCoreCommand { get; }

    public InstalledModsViewModel(
        SettingsService settings,
        ModStateService modState,
        UpdateTracker updateTracker,
        NexusApiService nexusApi,
        DownloadManager downloadManager,
        ModInstallService installer,
        NxmProtocolRegistrar nxmRegistrar)
    {
        _settings = settings;
        _modState = modState;
        _updateTracker = updateTracker;
        _nexusApi = nexusApi;
        _downloadManager = downloadManager;
        _installer = installer;
        _nxmRegistrar = nxmRegistrar;

        ToggleEnabledCommand = new AsyncRelayCommand(ToggleEnabled);
        ToggleModCommand = new RelayCommand<InstalledMod>(ToggleMod);
        ToggleDisabledExpandedCommand = new RelayCommand(() => IsDisabledExpanded = !IsDisabledExpanded);
        DismissUpdatesCommand = new RelayCommand(() => IsUpdatesDismissed = true);
        UninstallCommand = new AsyncRelayCommand(Uninstall);
        RefreshCommand = new RelayCommand(() => { Refresh(); StartUpdateCheck(); });
        CheckUpdatesCommand = new AsyncRelayCommand(CheckUpdates);
        UpdateModCommand = new AsyncRelayCommand(UpdateSelectedMods);
        UpdateAllCommand = new AsyncRelayCommand(UpdateAll);
        CancelBulkCommand = new RelayCommand(() => _bulkCts?.Cancel());
        OpenModFolderCommand = new RelayCommand(OpenModFolder);
        OpenOnNexusCommand = new AsyncRelayCommand(OpenOnNexus);
        InstallLocalCommand = new AsyncRelayCommand(InstallLocal);
        UpdateSingleCommand = new RelayCommand<InstalledMod>(mod => { if (mod != null) _ = DownloadUpdate(mod); });
        InstallCoreCommand = new AsyncRelayCommand(InstallCore);
    }

    public void Refresh()
    {
        var path = _settings.Load().TerrariaPath;
        if (string.IsNullOrWhiteSpace(path)) return;

        var coreInfo = _modState.GetCoreInfo(path);
        IsCoreInstalled = coreInfo.IsInstalled;

        var mods = _modState.ScanInstalledMods(path);
        foreach (var mod in mods)
            mod.NexusModId = _updateTracker.GetNexusModId(mod);

        _allMods = mods
            .OrderByDescending(m => m.IsCore)
            .ThenByDescending(m => m.IsEnabled)
            .ThenBy(m => m.Name)
            .ToList();

        ApplyFilter();

        SelectedMods.Clear();
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(HasSingleSelection));
        OnPropertyChanged(nameof(AnySelectedHasUpdate));

        // Cancel any in-progress update check before starting a new one
        _updateCts?.Cancel();
    }

    public void StartUpdateCheck()
    {
        if (_nexusApi.HasApiKey && !_isCheckingUpdates)
            _ = CheckUpdates();
    }

    private void ApplyFilter()
    {
        // Populate updates section
        UpdateMods.Clear();
        foreach (var mod in _allMods.Where(m => m.HasUpdate))
            UpdateMods.Add(mod);
        UpdatesAvailable = UpdateMods.Count;

        // Main list excludes mods that are in the updates section
        IEnumerable<InstalledMod> source = _allMods.Where(m => !m.HasUpdate);

        var filtered = source.ToList();

        Mods.Clear();
        EnabledMods.Clear();
        DisabledMods.Clear();
        foreach (var mod in filtered)
        {
            Mods.Add(mod);
            if (mod.IsEnabled)
                EnabledMods.Add(mod);
            else
                DisabledMods.Add(mod);
        }

        EnabledCount = _allMods.Count(m => m.IsEnabled && !m.IsCore);
        DisabledCount = _allMods.Count(m => !m.IsEnabled);
    }

    private async Task CheckUpdates()
    {
        if (!_nexusApi.HasApiKey) return;

        _updateCts?.Cancel();
        _updateCts = new CancellationTokenSource();
        var ct = _updateCts.Token;

        IsCheckingUpdates = true;
        try
        {
            var count = await _updateTracker.CheckForUpdatesAsync(_allMods, _nexusApi, ct);
            ct.ThrowIfCancellationRequested();
            UpdatesAvailable = count;
            ApplyFilter();
        }
        catch (OperationCanceledException) { }
        finally
        {
            IsCheckingUpdates = false;
        }
    }

    private async Task UpdateSelectedMods()
    {
        var toUpdate = SelectedMods.Where(m => m.HasUpdate).ToList();
        if (toUpdate.Count == 0) return;

        _bulkCts?.Cancel();
        _bulkCts = new CancellationTokenSource();
        var ct = _bulkCts.Token;

        IsBulkRunning = true;
        try
        {
            foreach (var mod in toUpdate)
            {
                if (ct.IsCancellationRequested) break;
                await DownloadUpdate(mod);
            }
        }
        finally
        {
            IsBulkRunning = false;
        }
    }

    private async Task UpdateAll()
    {
        var modsToUpdate = _allMods.Where(m => m.HasUpdate).ToList();
        if (modsToUpdate.Count == 0) return;

        _bulkCts?.Cancel();
        _bulkCts = new CancellationTokenSource();
        var ct = _bulkCts.Token;

        IsBulkRunning = true;
        try
        {
            foreach (var mod in modsToUpdate)
            {
                if (ct.IsCancellationRequested) break;
                await DownloadUpdate(mod);
            }
        }
        finally
        {
            IsBulkRunning = false;
        }
    }

    private async Task DownloadUpdate(InstalledMod mod)
    {
        if (mod.NexusModId <= 0 || mod.LatestFileId <= 0) return;

        if (_nexusApi.IsPremium)
        {
            await _downloadManager.EnqueueAsync(mod.NexusModId, mod.LatestFileId, forceKeepSettings: true);
        }
        else
        {
            var url = $"https://www.nexusmods.com/terraria/mods/{mod.NexusModId}?tab=files&file_id={mod.LatestFileId}";
            var result = await OpenBrowserAsync(url, $"Installing: {mod.Name}", "Click 'Manual Download' to update");
            await EnqueueFromBrowserResult(result, mod.NexusModId, keepSettings: true, version: mod.LatestVersion);
        }
    }

    public void SetModEnabled(InstalledMod mod, bool enabled)
    {
        if (mod.IsCore) return;
        var path = _settings.Load().TerrariaPath;
        if (string.IsNullOrWhiteSpace(path)) return;

        if (enabled)
            _modState.EnableMod(mod.Id, path);
        else
            _modState.DisableMod(mod.Id, path);

        // Defer refresh so collection changes happen after the click event finishes processing
        Avalonia.Threading.Dispatcher.UIThread.Post(Refresh);
    }

    private void ToggleMod(InstalledMod? mod)
    {
        if (mod == null) return;
        SetModEnabled(mod, !mod.IsEnabled);
    }

    private Task ToggleEnabled()
    {
        if (SelectedMods.Count == 0) return Task.CompletedTask;
        var path = _settings.Load().TerrariaPath;
        if (string.IsNullOrWhiteSpace(path)) return Task.CompletedTask;

        // Core cannot be enabled/disabled — skip it
        var mods = SelectedMods.Where(m => !m.IsCore).ToList();
        if (mods.Count == 0) return Task.CompletedTask;

        foreach (var mod in mods)
        {
            if (mod.IsEnabled)
                _modState.DisableMod(mod.Id, path);
            else
                _modState.EnableMod(mod.Id, path);
        }

        Refresh();
        return Task.CompletedTask;
    }

    private void OpenModFolder()
    {
        if (SelectedMods.Count != 1) return;
        var mod = SelectedMods[0];
        if (mod.FolderPath == null) return;
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = mod.FolderPath,
            UseShellExecute = true
        });
    }

    private async Task OpenOnNexus()
    {
        if (SelectedMods.Count != 1) return;
        var mod = SelectedMods[0];
        if (mod.NexusModId <= 0) return;
        var url = $"https://www.nexusmods.com/terraria/mods/{mod.NexusModId}";
        var result = await OpenBrowserAsync(url, $"Installing: {mod.Name}");
        await EnqueueFromBrowserResult(result, mod.NexusModId);
    }

    private async Task Uninstall()
    {
        if (SelectedMods.Count == 0) return;
        var path = _settings.Load().TerrariaPath;
        if (string.IsNullOrWhiteSpace(path)) return;

        var mods = SelectedMods.ToList();

        if (mods.Any(m => m.IsCore))
        {
            var result = await DialogHelper.ShowDialog(
                "Delete Core Framework?",
                "DANGER: Deleting TerrariaModder Core will completely remove the modding framework!\n\n" +
                "ALL mods will stop working. You will need to reinstall Core to use any mods again.\n\n" +
                "Are you absolutely sure you want to do this?",
                ButtonEnum.YesNo, Icon.Error);
            if (result != ButtonResult.Yes) return;
        }

        var names = mods.Count == 1 ? mods[0].Name : $"{mods.Count} mods";
        var anyHasConfig = mods.Any(m => m.HasConfigFiles && !m.IsCore);
        bool deleteSettings;

        if (!anyHasConfig)
        {
            var result = await DialogHelper.ShowDialog(
                "Delete Mod", $"Delete {names}?",
                ButtonEnum.YesNo, Icon.Question);
            if (result != ButtonResult.Yes) return;
            deleteSettings = true;
        }
        else
        {
            var box = MessageBoxManager.GetMessageBoxCustom(
                new MsBox.Avalonia.Dto.MessageBoxCustomParams
                {
                    ContentTitle = "Delete Mod",
                    ContentMessage = $"Delete {names}?\n\n" +
                        "You can keep your settings so they'll be restored if you reinstall later.",
                    Icon = Icon.Question,
                    WindowStartupLocation = Avalonia.Controls.WindowStartupLocation.CenterOwner,
                    ButtonDefinitions = new[]
                    {
                        new MsBox.Avalonia.Models.ButtonDefinition { Name = "Delete Mod Only", IsDefault = true },
                        new MsBox.Avalonia.Models.ButtonDefinition { Name = "Delete Mod & Settings" },
                        new MsBox.Avalonia.Models.ButtonDefinition { Name = "Cancel", IsCancel = true }
                    }
                });

            var mainWindow = GetTopLevel() as Window;
            var choice = mainWindow != null
                ? await box.ShowWindowDialogAsync(mainWindow)
                : await box.ShowAsync();

            if (choice == "Cancel" || string.IsNullOrEmpty(choice)) return;
            deleteSettings = choice == "Delete Mod & Settings";
        }

        foreach (var mod in mods)
            _modState.UninstallMod(mod.Id, path, deleteSettings);

        Refresh();
    }

    private async Task InstallCore()
    {
        var path = _settings.Load().TerrariaPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            await DialogHelper.ShowDialog("Error",
                "Set your Terraria path in Settings first.",
                ButtonEnum.Ok, Icon.Warning);
            return;
        }

        if (!_nexusApi.HasApiKey || !_nexusApi.IsPremium)
        {
            // Non-premium or no API key — try to resolve latest file for direct download page
            string url;
            string? coreVersion = null;
            try
            {
                var coreFiles = await _nexusApi.GetModFilesAsync(UpdateTracker.CoreNexusModId);
                // Pick the most recently uploaded file — user guarantees Core is always first
                var primary = coreFiles.OrderByDescending(f => f.UploadedTimestamp).FirstOrDefault();

                if (primary != null)
                {
                    url = $"https://www.nexusmods.com/terraria/mods/{UpdateTracker.CoreNexusModId}?tab=files&file_id={primary.FileId}";
                    coreVersion = primary.Version;
                }
                else
                {
                    url = $"https://www.nexusmods.com/terraria/mods/{UpdateTracker.CoreNexusModId}?tab=files";
                }
            }
            catch
            {
                url = $"https://www.nexusmods.com/terraria/mods/{UpdateTracker.CoreNexusModId}?tab=files";
            }

            var result = await OpenBrowserAsync(url, "Installing: TerrariaModder Core", "Click 'Manual Download' to install");
            await EnqueueFromBrowserResult(result, UpdateTracker.CoreNexusModId, version: coreVersion);
            return;
        }

        // Premium: direct download — picks latest file automatically
        var files = await _nexusApi.GetModFilesAsync(UpdateTracker.CoreNexusModId);
        var mainFile = files.OrderByDescending(f => f.UploadedTimestamp).FirstOrDefault();

        if (mainFile == null)
        {
            await DialogHelper.ShowDialog("Error",
                "Could not find any files for TerrariaModder Core on Nexus.",
                ButtonEnum.Ok, Icon.Error);
            return;
        }

        await _downloadManager.EnqueueAsync(UpdateTracker.CoreNexusModId, mainFile.FileId);
    }

    private async Task InstallLocal()
    {
        var path = _settings.Load().TerrariaPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            await DialogHelper.ShowDialog("Error",
                "Set your Terraria path in Settings first.",
                ButtonEnum.Ok, Icon.Warning);
            return;
        }

        // Ask user: zip file or mod folder?
        var choiceBox = MessageBoxManager.GetMessageBoxCustom(
            new MsBox.Avalonia.Dto.MessageBoxCustomParams
            {
                ContentTitle = "Install Local Mod",
                ContentMessage = "Select the mod source:",
                Icon = Icon.Question,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ButtonDefinitions = new[]
                {
                    new MsBox.Avalonia.Models.ButtonDefinition { Name = "Zip File", IsDefault = true },
                    new MsBox.Avalonia.Models.ButtonDefinition { Name = "Mod Folder" },
                    new MsBox.Avalonia.Models.ButtonDefinition { Name = "Cancel", IsCancel = true }
                }
            });

        var mainWindow = GetTopLevel() as Window;
        var choice = mainWindow != null
            ? await choiceBox.ShowWindowDialogAsync(mainWindow)
            : await choiceBox.ShowAsync();

        if (choice == "Cancel" || string.IsNullOrEmpty(choice)) return;

        var topLevel = GetTopLevel();
        if (topLevel == null) return;

        if (choice == "Zip File")
            await InstallFromZip(topLevel);
        else
            await InstallFromFolder(topLevel);
    }

    private async Task InstallFromZip(TopLevel topLevel)
    {
        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select Mod Archive",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Mod Archives") { Patterns = new[] { "*.zip" } },
                new FilePickerFileType("All Files") { Patterns = new[] { "*" } }
            }
        });

        if (files.Count == 0) return;

        var filePath = files[0].Path.LocalPath;
        var result = await _installer.InstallModAsync(filePath);

        if (result.Success)
        {
            Refresh();
            StartUpdateCheck();
            await DialogHelper.ShowDialog("Success",
                $"Mod '{result.InstalledModId}' installed successfully.",
                ButtonEnum.Ok, Icon.Success);
        }
        else
        {
            await DialogHelper.ShowDialog("Install Failed",
                result.Error ?? "The archive doesn't contain a valid TerrariaModder mod.",
                ButtonEnum.Ok, Icon.Error);
        }
    }

    private async Task InstallFromFolder(TopLevel topLevel)
    {
        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select Mod Folder",
            AllowMultiple = false
        });

        if (folders.Count == 0) return;

        var folderPath = folders[0].Path.LocalPath;
        var result = await _installer.InstallFromFolderAsync(folderPath);

        if (result.Success)
        {
            Refresh();
            StartUpdateCheck();
            await DialogHelper.ShowDialog("Success",
                $"Mod '{result.InstalledModId}' installed successfully.",
                ButtonEnum.Ok, Icon.Success);
        }
        else if (result.Error == "ALREADY_INSTALLED")
        {
            await DialogHelper.ShowDialog("Already Installed",
                $"This folder is already the install location for '{result.InstalledModId}'.",
                ButtonEnum.Ok, Icon.Info);
        }
        else if (result.Error == "NO_MOD_FOUND")
        {
            await DialogHelper.ShowDialog("No Mod Found",
                "The selected folder doesn't contain a valid TerrariaModder mod.\n\n" +
                "A mod folder needs a manifest.json or a .dll file.",
                ButtonEnum.Ok, Icon.Error);
        }
        else
        {
            await DialogHelper.ShowDialog("Install Failed",
                result.Error ?? "Unknown error",
                ButtonEnum.Ok, Icon.Error);
        }
    }

    private Task<InlineBrowserResult?> OpenBrowserAsync(string url, string modName, string? toast = null)
        => OpenBrowser?.Invoke(url, modName, toast) ?? Task.FromResult<InlineBrowserResult?>(null);

    private async Task EnqueueFromBrowserResult(InlineBrowserResult? result, int modId, bool keepSettings = false, string? version = null)
    {
        if (result?.DownloadedFilePath != null)
            await _downloadManager.EnqueueFromFileAsync(modId, result.DownloadedFilePath, forceKeepSettings: keepSettings, nexusVersion: version);
    }

    private static TopLevel? GetTopLevel()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            return desktop.MainWindow;
        return null;
    }
}
