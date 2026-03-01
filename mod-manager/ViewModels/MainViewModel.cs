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
    private ViewModelBase _currentView;
    private string _statusText = "Ready";
    private bool _needsSetup;
    private bool _isNxmRegistered;

    private static readonly IBrush NxmRegisteredBrush = new SolidColorBrush(Color.Parse("#FF58EB1C"));
    private static readonly IBrush NxmNotRegisteredBrush = new SolidColorBrush(Color.Parse("#FFE05252"));

    private readonly SettingsService _settingsService;
    private readonly ModStateService _modState;
    private readonly NxmProtocolRegistrar _nxmRegistrar;
    private readonly DownloadManager _downloadManager;
    private readonly NxmLinkHandler _nxmHandler;
    private AppSettings _appSettings = null!;

    public InstalledModsViewModel InstalledModsVm { get; }
    public BrowseViewModel BrowseVm { get; }
    public DownloadsViewModel DownloadsVm { get; }
    public SettingsViewModel SettingsVm { get; }

    public ViewModelBase CurrentView
    {
        get => _currentView;
        set => SetProperty(ref _currentView, value);
    }

    public string StatusText
    {
        get => _statusText;
        set => SetProperty(ref _statusText, value);
    }

    public bool NeedsSetup
    {
        get => _needsSetup;
        set => SetProperty(ref _needsSetup, value);
    }

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

    public IBrush NxmStatusColor => IsNxmRegistered
        ? NxmRegisteredBrush
        : NxmNotRegisteredBrush;

    public ICommand ShowInstalledCommand { get; }
    public ICommand ShowBrowseCommand { get; }
    public ICommand ShowDownloadsCommand { get; }
    public ICommand ShowSettingsCommand { get; }
    public ICommand LaunchModdedCommand { get; }
    public ICommand LaunchVanillaCommand { get; }
    public ICommand RegisterNxmCommand { get; }

    public MainViewModel(
        InstalledModsViewModel installedModsVm,
        BrowseViewModel browseVm,
        DownloadsViewModel downloadsVm,
        SettingsViewModel settingsVm,
        SettingsService settingsService,
        ModStateService modState,
        NxmProtocolRegistrar nxmRegistrar,
        DownloadManager downloadManager,
        NxmLinkHandler nxmHandler)
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

        _appSettings = _settingsService.Load();
        NeedsSetup = string.IsNullOrWhiteSpace(_appSettings.TerrariaPath);

        _currentView = NeedsSetup ? SettingsVm : InstalledModsVm;

        ShowInstalledCommand = new RelayCommand(() =>
        {
            SyncNxmStatus();
            CurrentView = InstalledModsVm;
            InstalledModsVm.Refresh();
        });
        ShowBrowseCommand = new RelayCommand(() =>
        {
            SyncNxmStatus();
            CurrentView = BrowseVm;
            if (BrowseVm.Mods.Count == 0)
                _ = BrowseVm.LoadFeedAsync();
            else
                BrowseVm.RefreshInstallStates();
        });
        ShowDownloadsCommand = new RelayCommand(() =>
        {
            SyncNxmStatus();
            CurrentView = DownloadsVm;
        });
        ShowSettingsCommand = new RelayCommand(() =>
        {
            SyncNxmStatus();
            CurrentView = SettingsVm;
            SettingsVm.RefreshCoreInfo();
        });
        LaunchModdedCommand = new RelayCommand(() => LaunchGame(modded: true));
        LaunchVanillaCommand = new RelayCommand(() => LaunchGame(modded: false));
        RegisterNxmCommand = new AsyncRelayCommand(RegisterNxm);

        _isNxmRegistered = _nxmRegistrar.IsRegistered();

        if (!NeedsSetup)
        {
            InstalledModsVm.Refresh();
            SettingsVm.LoadFromSettings();
            UpdateStatus();
        }
        else
        {
            StatusText = "Welcome! Set your Terraria path to get started.";
            SettingsVm.LoadFromSettings();
        }

        _downloadManager.DownloadCompleted += _ =>
        {
            try
            {
                Dispatcher.UIThread.InvokeAsync(() =>
                {
                    InstalledModsVm.Refresh();
                    BrowseVm.RefreshInstallStates();
                    if (BrowseVm.HideInstalled)
                        BrowseVm.ApplyCurrentSort();
                    UpdateStatus();
                });
            }
            catch (InvalidOperationException) { /* dispatcher shut down */ }
        };

        _downloadManager.DownloadFailed += (_, _) =>
        {
            try
            {
                Dispatcher.UIThread.InvokeAsync(() =>
                {
                    BrowseVm.RefreshInstallStates();
                    UpdateStatus();
                });
            }
            catch (InvalidOperationException) { /* dispatcher shut down */ }
        };
    }

    public void HandleNxmLink(NxmLink link)
    {
        CurrentView = DownloadsVm;
        _ = _downloadManager.EnqueueAsync(link.ModId, link.FileId, link.Key, link.Expires);
        StatusText = $"Downloading mod {link.ModId}...";
    }

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

        string exe;
        string label;
        if (modded)
        {
            exe = System.IO.File.Exists(injector) ? injector : terraria;
            label = System.IO.File.Exists(injector) ? "modded" : "vanilla (injector not found)";
        }
        else
        {
            exe = terraria;
            label = "vanilla";
        }

        if (!System.IO.File.Exists(exe))
        {
            StatusText = "Could not find Terraria executable";
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = exe,
                WorkingDirectory = path,
                UseShellExecute = true
            });
            StatusText = $"Launching Terraria ({label})...";
        }
        catch (Exception ex)
        {
            StatusText = $"Failed to launch: {ex.Message}";
        }
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

            await DialogHelper.ShowDialog(
                "Registered",
                "\"Download with Vortex\" links will now open in TerrariaModder Vault.\n\nYou can unregister in Settings if needed.",
                ButtonEnum.Ok, Icon.Info);
        }
        catch (Exception ex)
        {
            await DialogHelper.ShowDialog(
                "Error",
                $"Failed to register nxm:// handler: {ex.Message}",
                ButtonEnum.Ok, Icon.Error);
        }
    }

    private void SyncNxmStatus()
    {
        IsNxmRegistered = _nxmRegistrar.IsRegistered();
    }

    private void UpdateStatus()
    {
        var path = _appSettings.TerrariaPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            StatusText = "No Terraria path configured";
            return;
        }

        var coreInfo = _modState.GetCoreInfo(path);
        var modCount = InstalledModsVm.EnabledCount + InstalledModsVm.DisabledCount;

        if (coreInfo.IsInstalled)
            StatusText = $"Core v{coreInfo.CoreVersion} | {modCount} mod(s) installed";
        else
            StatusText = $"TerrariaModder Core not installed | {modCount} mod(s) found";
    }
}
