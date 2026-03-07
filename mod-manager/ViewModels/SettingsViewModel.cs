using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using MsBox.Avalonia.Enums;
using TerrariaModManager.Helpers;
using TerrariaModManager.Models;
using TerrariaModManager.Services;

namespace TerrariaModManager.ViewModels;

public class SettingsViewModel : ViewModelBase
{
    private string _terrariaPath = "";
    private string _apiKey = "";
    private bool _isNxmRegistered;
    private bool _isPremium;
    private string _coreVersion = "";
    private bool _isCoreInstalled;
    private string _loginStatus = "";
    private bool _isLoggedIn;
    private string _userName = "";
    private bool _isLoggingIn;
    private List<DetectedInstall> _detectedInstalls = new();
    private string _logText = "";
    private bool _autoCheckForUpdates = true;
    private bool _showLogs;

    private NexusSsoService? _ssoService;
    private readonly SettingsService _settings;
    private readonly ModInstallService _installer;
    private readonly ModStateService _modState;
    private readonly UpdateTracker _updateTracker;
    private readonly NexusApiService _nexusApi;
    private readonly TerrariaDetector _detector;
    private readonly NxmProtocolRegistrar _nxmRegistrar;
    private readonly Logger _logger;
    private AppSettings _appSettings = null!;

    public Func<string, string, string?, Task<InlineBrowserResult?>>? OpenBrowser { get; set; }
    public Action? CloseBrowser { get; set; }
    public Action? ClearBrowserCookies { get; set; }

    public string TerrariaPath
    {
        get => _terrariaPath;
        set
        {
            if (SetProperty(ref _terrariaPath, value))
            {
                _appSettings.TerrariaPath = value;
                _installer.SetTerrariaPath(value);
                _settings.Save(_appSettings);
                RefreshCoreInfo();
                OnPropertyChanged(nameof(IsPathValid));
            }
        }
    }

    private static string TerrariaExeName =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "Terraria.exe" : "Terraria";

    public bool IsPathValid =>
        string.IsNullOrWhiteSpace(_terrariaPath) ||
        (Directory.Exists(_terrariaPath) && File.Exists(Path.Combine(_terrariaPath, TerrariaExeName)));

    public string ApiKey
    {
        get => _apiKey;
        set => SetProperty(ref _apiKey, value);
    }

    public bool IsNxmRegistered
    {
        get => _isNxmRegistered;
        set
        {
            if (SetProperty(ref _isNxmRegistered, value))
            {
                OnPropertyChanged(nameof(NxmButtonText));
                OnPropertyChanged(nameof(NxmStatusText));
                OnPropertyChanged(nameof(NxmStatusColor));
            }
        }
    }

    public bool IsPremium
    {
        get => _isPremium;
        set => SetProperty(ref _isPremium, value);
    }

    public string CoreVersion
    {
        get => _coreVersion;
        set
        {
            if (SetProperty(ref _coreVersion, value))
            {
                OnPropertyChanged(nameof(CoreStatusText));
                OnPropertyChanged(nameof(CoreStatusColor));
            }
        }
    }

    public bool IsCoreInstalled
    {
        get => _isCoreInstalled;
        set
        {
            if (SetProperty(ref _isCoreInstalled, value))
            {
                OnPropertyChanged(nameof(CoreStatusText));
                OnPropertyChanged(nameof(CoreStatusColor));
            }
        }
    }

    public string LoginStatus
    {
        get => _loginStatus;
        set => SetProperty(ref _loginStatus, value);
    }

    public bool IsLoggedIn
    {
        get => _isLoggedIn;
        set => SetProperty(ref _isLoggedIn, value);
    }

    public string UserName
    {
        get => _userName;
        set => SetProperty(ref _userName, value);
    }

    public bool IsLoggingIn
    {
        get => _isLoggingIn;
        set => SetProperty(ref _isLoggingIn, value);
    }

    public List<DetectedInstall> DetectedInstalls
    {
        get => _detectedInstalls;
        set => SetProperty(ref _detectedInstalls, value);
    }

    public string LogText
    {
        get => _logText;
        set => SetProperty(ref _logText, value);
    }

    public bool AutoCheckForUpdates
    {
        get => _autoCheckForUpdates;
        set
        {
            if (SetProperty(ref _autoCheckForUpdates, value))
            {
                _appSettings.AutoCheckForUpdates = value;
                _settings.Save(_appSettings);
            }
        }
    }

    public bool ShowLogs
    {
        get => _showLogs;
        set
        {
            if (SetProperty(ref _showLogs, value))
                OnPropertyChanged(nameof(ShowSettings));
        }
    }

    public bool ShowSettings => !_showLogs;

    // Computed properties (replace WPF DataTriggers)
    public string CoreStatusText => IsCoreInstalled ? $"v{CoreVersion}" : "Not installed";
    public IBrush CoreStatusColor => IsCoreInstalled
        ? new SolidColorBrush(Color.Parse("#FF58EB1C"))
        : new SolidColorBrush(Color.Parse("#FFE05252"));

    public string NxmButtonText => IsNxmRegistered ? "Re-register Links" : "Register Nexus Links";
    public string NxmStatusText => IsNxmRegistered ? "Registered" : "Not registered";
    public IBrush NxmStatusColor => IsNxmRegistered
        ? new SolidColorBrush(Color.Parse("#FF58EB1C"))
        : new SolidColorBrush(Color.Parse("#FF7C828D"));

    public event Action? LoginSucceeded;

    public ICommand BrowsePathCommand { get; }
    public ICommand AutoDetectCommand { get; }
    public ICommand LoginWithNexusCommand { get; }
    public ICommand LogoutCommand { get; }
    public ICommand SaveManualKeyCommand { get; }
    public ICommand RegisterNxmCommand { get; }
    public ICommand UnregisterNxmCommand { get; }
    public ICommand RefreshLogsCommand { get; }
    public ICommand ClearLogsCommand { get; }
    public ICommand CopyLogsCommand { get; }
    public ICommand SaveLogsCommand { get; }
    public ICommand ShowSettingsTabCommand { get; }
    public ICommand ShowLogsTabCommand { get; }
    public ICommand ResetWebViewCommand { get; }

    public SettingsViewModel(
        SettingsService settings,
        ModInstallService installer,
        ModStateService modState,
        UpdateTracker updateTracker,
        NexusApiService nexusApi,
        TerrariaDetector detector,
        NxmProtocolRegistrar nxmRegistrar,
        Logger logger)
    {
        _settings = settings;
        _installer = installer;
        _modState = modState;
        _updateTracker = updateTracker;
        _nexusApi = nexusApi;
        _detector = detector;
        _nxmRegistrar = nxmRegistrar;
        _logger = logger;

        BrowsePathCommand = new AsyncRelayCommand(BrowsePath);
        AutoDetectCommand = new RelayCommand(AutoDetect);
        LoginWithNexusCommand = new AsyncRelayCommand(LoginWithNexus);
        LogoutCommand = new RelayCommand(Logout);
        SaveManualKeyCommand = new AsyncRelayCommand(SaveManualKey);
        RegisterNxmCommand = new AsyncRelayCommand(RegisterNxm);
        UnregisterNxmCommand = new RelayCommand(UnregisterNxm);
        RefreshLogsCommand = new RelayCommand(RefreshLogs);
        ClearLogsCommand = new RelayCommand(ClearLogs);
        CopyLogsCommand = new AsyncRelayCommand(CopyLogs);
        SaveLogsCommand = new AsyncRelayCommand(SaveLogs);
        ShowSettingsTabCommand = new RelayCommand(() => ShowLogs = false);
        ShowLogsTabCommand = new RelayCommand(() => { ShowLogs = true; RefreshLogs(); });
        ResetWebViewCommand = new RelayCommand(ResetWebView);

        _nxmRegistrar.RegistrationChanged += () =>
            Dispatcher.UIThread.InvokeAsync(() => IsNxmRegistered = _nxmRegistrar.IsRegistered());
    }

    public void LoadFromSettings()
    {
        _appSettings = _settings.Load();
        _terrariaPath = _appSettings.TerrariaPath ?? "";
        _apiKey = _appSettings.NexusApiKey ?? "";
        _isPremium = _appSettings.IsPremium;
        _isNxmRegistered = _nxmRegistrar.IsRegistered();
        _autoCheckForUpdates = _appSettings.AutoCheckForUpdates;

        OnPropertyChanged(nameof(TerrariaPath));
        OnPropertyChanged(nameof(ApiKey));
        OnPropertyChanged(nameof(IsPremium));
        OnPropertyChanged(nameof(IsNxmRegistered));
        OnPropertyChanged(nameof(IsPathValid));
        OnPropertyChanged(nameof(AutoCheckForUpdates));

        RefreshCoreInfo();
        RefreshLogs();

        // Validate from saved key, or from API service (which may have dev env key from startup)
        if (!string.IsNullOrWhiteSpace(_apiKey) || _nexusApi.HasApiKey)
            _ = ValidateExistingKey();
    }

    private async Task ValidateExistingKey()
    {
        // Only override the API service key if we have one from settings
        if (!string.IsNullOrWhiteSpace(_apiKey))
            _nexusApi.SetApiKey(_apiKey);

        var user = await _nexusApi.ValidateApiKeyAsync();
        if (user != null)
        {
            IsLoggedIn = true;
            UserName = user.Name;
            IsPremium = user.IsPremium;
            LoginStatus = "";

            _appSettings.IsPremium = user.IsPremium;
            _settings.Save(_appSettings);
        }
        else
        {
            IsLoggedIn = false;
            LoginStatus = "";
        }
    }

    public void RefreshCoreInfo()
    {
        if (string.IsNullOrWhiteSpace(_terrariaPath))
        {
            IsCoreInstalled = false;
            CoreVersion = "";
            return;
        }

        var info = _modState.GetCoreInfo(_terrariaPath);
        IsCoreInstalled = info.IsInstalled;
        // Prefer tracked Nexus version (stamped after download) over DLL version
        // to avoid format mismatches (e.g. DLL "1.2.0.0" vs Nexus "1.2.0")
        CoreVersion = _updateTracker.GetTrackedVersion("core")
                      ?? info.CoreVersion ?? "";
    }

    private async Task BrowsePath()
    {
        var topLevel = GetTopLevel();
        if (topLevel == null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = $"Select {TerrariaExeName}",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Terraria") { Patterns = new[] { TerrariaExeName } }
            }
        });

        if (files.Count > 0)
        {
            var filePath = files[0].Path.LocalPath;
            var dir = System.IO.Path.GetDirectoryName(filePath);
            if (dir != null)
                TerrariaPath = dir;
        }
    }

    public void AutoDetect()
    {
        var installs = _detector.FindAllInstalls();
        DetectedInstalls = installs;

        if (installs.Count > 0)
        {
            var preferred = installs.FirstOrDefault(i => i.HasTerrariaModder) ?? installs[0];
            TerrariaPath = preferred.Path;
        }
        else
        {
            LoginStatus = "Could not auto-detect Terraria. Use Browse to select manually.";
        }
    }

    private async Task LoginWithNexus()
    {
        IsLoggingIn = true;
        LoginStatus = "Opening browser...";

        _ssoService?.Dispose();
        _ssoService = new NexusSsoService();

        _ssoService.ApiKeyReceived += apiKey =>
        {
            Dispatcher.UIThread.InvokeAsync(async () =>
            {
                CloseBrowser?.Invoke();

                ApiKey = apiKey;
                _nexusApi.SetApiKey(apiKey);

                var user = await _nexusApi.ValidateApiKeyAsync();
                if (user != null)
                {
                    IsLoggedIn = true;
                    UserName = user.Name;
                    IsPremium = user.IsPremium;
                    LoginStatus = "";

                    _appSettings.NexusApiKey = apiKey;
                    _appSettings.IsPremium = user.IsPremium;
                    _settings.Save(_appSettings);

                    LoginSucceeded?.Invoke();
                }

                IsLoggingIn = false;
                _ssoService?.Dispose();
                _ssoService = null;
            });
        };

        _ssoService.ErrorOccurred += error =>
        {
            Dispatcher.UIThread.InvokeAsync(() =>
            {
                CloseBrowser?.Invoke();
                LoginStatus = $"Login failed: {error}. Use manual API key instead.";
                IsLoggingIn = false;
            });
        };

        try
        {
            var url = await _ssoService.StartLoginAsync();

            var browserTask = OpenBrowser?.Invoke(url, "Sign in to Nexus Mods", null);
            LoginStatus = "Waiting for authorization...";

            if (browserTask != null)
            {
                await browserTask;
                if (_isLoggingIn)
                {
                    IsLoggingIn = false;
                    LoginStatus = "";
                    _ssoService?.Dispose();
                    _ssoService = null;
                }
            }
        }
        catch
        {
            LoginStatus = "Could not connect to Nexus SSO. Use manual API key instead.";
            IsLoggingIn = false;
        }
    }

    private void Logout()
    {
        ApiKey = "";
        IsLoggedIn = false;
        UserName = "";
        IsPremium = false;
        LoginStatus = "";

        _nexusApi.SetApiKey("");
        _appSettings.NexusApiKey = "";
        _appSettings.IsPremium = false;

        // Clear cookies in the live WebView2 instance (works without restart)
        ClearBrowserCookies?.Invoke();

        // Also nuke the user-data folder so cached credentials don't survive a restart.
        // If the folder is locked by an active WebView2 process, flag it for next startup.
        var webViewDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TerrariaModManager", "WebView2");
        try
        {
            if (Directory.Exists(webViewDir))
                Directory.Delete(webViewDir, true);
            _appSettings.ClearWebViewDataOnNextStart = false;
        }
        catch
        {
            _appSettings.ClearWebViewDataOnNextStart = true;
        }

        _settings.Save(_appSettings);
    }

    private async Task SaveManualKey()
    {
        if (string.IsNullOrWhiteSpace(ApiKey))
        {
            LoginStatus = "Enter an API key first";
            return;
        }

        LoginStatus = "Validating...";
        _nexusApi.SetApiKey(ApiKey);

        var user = await _nexusApi.ValidateApiKeyAsync();
        if (user != null)
        {
            IsLoggedIn = true;
            UserName = user.Name;
            IsPremium = user.IsPremium;
            LoginStatus = "";

            _appSettings.NexusApiKey = ApiKey;
            _appSettings.IsPremium = user.IsPremium;
            _settings.Save(_appSettings);

            LoginSucceeded?.Invoke();
        }
        else
        {
            LoginStatus = "Invalid API key";
            _nexusApi.SetApiKey("");
        }
    }

    private async Task RegisterNxm()
    {
        try
        {
            _nxmRegistrar.Register();
            IsNxmRegistered = true;
            _appSettings.NxmRegistered = true;
            _settings.Save(_appSettings);
        }
        catch (Exception ex)
        {
            await DialogHelper.ShowDialog(
                "Error", $"Failed to register: {ex.Message}",
                ButtonEnum.Ok, Icon.Error);
        }
    }

    private void UnregisterNxm()
    {
        _nxmRegistrar.Unregister();
        IsNxmRegistered = false;
        _appSettings.NxmRegistered = false;
        _settings.Save(_appSettings);
    }

    private void RefreshLogs()
    {
        LogText = _logger.ReadTail(200);
    }

    private void ClearLogs()
    {
        _logger.Clear();
        LogText = "(log cleared)";
    }

    private async Task CopyLogs()
    {
        var topLevel = GetTopLevel();
        if (topLevel?.Clipboard == null) return;
        await topLevel.Clipboard.SetTextAsync(LogText);
    }

    private async Task SaveLogs()
    {
        var topLevel = GetTopLevel();
        if (topLevel == null) return;

        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save Logs",
            SuggestedFileName = $"terraria-modder-vault-logs-{DateTime.Now:yyyyMMdd-HHmmss}.txt",
            FileTypeChoices = new[]
            {
                new FilePickerFileType("Text Files") { Patterns = new[] { "*.txt" } }
            }
        });

        if (file == null) return;

        await using var stream = await file.OpenWriteAsync();
        await using var writer = new System.IO.StreamWriter(stream);
        await writer.WriteAsync(LogText);
    }

    private void ResetWebView()
    {
        _appSettings.ClearWebViewDataOnNextStart = true;
        _settings.Save(_appSettings);

        // Restart the app so the cleanup runs in RegisterServices() before WebView2 initializes
        var exe = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
        if (!string.IsNullOrEmpty(exe))
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe)
            {
                UseShellExecute = true
            });
        }

        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.Shutdown();
    }

    private static TopLevel? GetTopLevel()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            return desktop.MainWindow;
        return null;
    }
}
