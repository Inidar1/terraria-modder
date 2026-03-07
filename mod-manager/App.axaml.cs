using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using AvaloniaWebView;
using Microsoft.Extensions.DependencyInjection;
using MsBox.Avalonia;
using MsBox.Avalonia.Enums;
using TerrariaModManager.Models;
using TerrariaModManager.Services;
using TerrariaModManager.Views;
using TerrariaModManager.ViewModels;

namespace TerrariaModManager;

public partial class App : Application
{
    private SingleInstance? _singleInstance;
    private IServiceProvider? _serviceProvider;
    private Logger? _logger;

    public static string? EnvApiKey { get; private set; }
    public static Logger? AppLogger { get; private set; }

    public override void RegisterServices()
    {
        base.RegisterServices();
        var webViewDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TerrariaModManager", "WebView2");

        // Clear WebView2 data if flagged from a previous logout
        try
        {
            var settingsPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "TerrariaModManager", "settings.json");
            if (File.Exists(settingsPath))
            {
                var json = File.ReadAllText(settingsPath);
                var settings = JsonSerializer.Deserialize<AppSettings>(json);
                if (settings?.ClearWebViewDataOnNextStart == true)
                {
                    if (Directory.Exists(webViewDataDir))
                        Directory.Delete(webViewDataDir, true);
                    settings.ClearWebViewDataOnNextStart = false;
                    File.WriteAllText(settingsPath, JsonSerializer.Serialize(settings,
                        new JsonSerializerOptions { WriteIndented = true }));
                }
            }
        }
        catch { /* best effort */ }

        // Ensure the directory exists — WebView2 CreateAsync may fail if it can't create it itself
        try { Directory.CreateDirectory(webViewDataDir); } catch { /* best effort */ }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // Force WebView2 user data folder to AppData — the Avalonia wrapper's config.UserDataFolder
            // doesn't always propagate, and WebView2 falls back to creating {exe}.WebView2 next to the exe.
            Environment.SetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER", webViewDataDir);

            // WebView2 SDK can't find WebView2Loader.dll in single-file publish because Assembly.Location
            // returns "". With IncludeNativeLibrariesForSelfExtract=false the DLL sits in deps/ next to
            // the exe, so we point the SDK there explicitly.
            var depsDir = Path.Combine(AppContext.BaseDirectory, "deps");
            try { Microsoft.Web.WebView2.Core.CoreWebView2Environment.SetLoaderDllFolderPath(depsDir); } catch { /* best effort */ }
        }

        AvaloniaWebViewBuilder.Initialize(config =>
        {
            config.UserDataFolder = webViewDataDir;
            config.AreDevToolEnabled = false;
            config.AreDefaultContextMenusEnabled = false;
        });
    }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // Global exception handlers
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
                _logger?.Error("Fatal unhandled exception", ex);
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            _logger?.Error("Unobserved task exception", args.Exception);
            args.SetObserved();
        };

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Single instance check
            _singleInstance = new SingleInstance();
            if (!_singleInstance.TryAcquire())
            {
                var nxmArg = FindNxmArg(desktop.Args ?? []);
                SingleInstance.SendToExisting(nxmArg ?? "ACTIVATE");
                desktop.Shutdown();
                return;
            }

            // Load .env file (dev API key)
            LoadEnvFile();

            // Set up DI
            var services = new ServiceCollection();
            ConfigureServices(services);
            _serviceProvider = services.BuildServiceProvider();

            // Initialize Logger
            _logger = _serviceProvider.GetRequiredService<Logger>();
            AppLogger = _logger;
            _logger.Info("TerrariaModder Vault starting");

            var settingsService = _serviceProvider.GetRequiredService<SettingsService>();
            settingsService.EnsureDirectories();

            var appSettings = settingsService.Load();
            var nexusApi = _serviceProvider.GetRequiredService<NexusApiService>();
            var installer = _serviceProvider.GetRequiredService<ModInstallService>();

            if (!string.IsNullOrWhiteSpace(appSettings.NexusApiKey))
                nexusApi.SetApiKey(appSettings.NexusApiKey);
            else if (!string.IsNullOrWhiteSpace(EnvApiKey))
                nexusApi.SetApiKey(EnvApiKey);

            if (!string.IsNullOrWhiteSpace(appSettings.TerrariaPath))
                installer.SetTerrariaPath(appSettings.TerrariaPath);

            // Create and show main window
            var mainWindow = new MainWindow();
            var vm = _serviceProvider.GetRequiredService<MainViewModel>();
            mainWindow.DataContext = vm;

            // Config found callback
            installer.OnExistingConfigFound = async (modId, configFiles) =>
            {
                var fileList = string.Join("\n", configFiles.Select(f => $"  • {f}"));
                var box = MessageBoxManager.GetMessageBoxCustom(
                    new MsBox.Avalonia.Dto.MessageBoxCustomParams
                    {
                        ContentTitle = "Existing Settings Found",
                        ContentMessage = $"The mod '{modId}' has existing configuration files:\n\n{fileList}\n\n" +
                            "Keep your current settings, or delete them for a clean install?",
                        Icon = Icon.Question,
                        WindowStartupLocation = Avalonia.Controls.WindowStartupLocation.CenterOwner,
                        ButtonDefinitions = new[]
                        {
                            new MsBox.Avalonia.Models.ButtonDefinition { Name = "Keep Settings", IsDefault = true },
                            new MsBox.Avalonia.Models.ButtonDefinition { Name = "Clean Install", IsCancel = true }
                        }
                    });
                var result = await box.ShowWindowDialogAsync(mainWindow);
                return result == "Clean Install" ? ConfigAction.Delete : ConfigAction.Keep;
            };

            // Auto-repair stale nxm:// registration (exe moved or updated to different path)
            _serviceProvider.GetRequiredService<NxmProtocolRegistrar>().AutoRepairIfStale();

            // Listen for nxm:// links from other instances
            var nxmHandler = _serviceProvider.GetRequiredService<NxmLinkHandler>();
            _singleInstance.MessageReceived += msg =>
            {
                Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (msg == "ACTIVATE")
                    {
                        mainWindow.Activate();
                        return;
                    }

                    var link = nxmHandler.Parse(msg);
                    if (link != null && nxmHandler.IsTerrariaLink(link))
                        vm.HandleNxmLink(link);
                });
            };
            _singleInstance.StartListening();

            // Check for nxm:// link in startup args
            var startupNxm = FindNxmArg(desktop.Args ?? []);
            if (startupNxm != null)
            {
                var link = nxmHandler.Parse(startupNxm);
                if (link != null && nxmHandler.IsTerrariaLink(link))
                {
                    mainWindow.Opened += (_, _) => vm.HandleNxmLink(link);
                }
            }

            desktop.MainWindow = mainWindow;
            desktop.ShutdownMode = Avalonia.Controls.ShutdownMode.OnMainWindowClose;
            desktop.ShutdownRequested += (_, _) =>
            {
                nexusApi.Dispose();
                _serviceProvider.GetRequiredService<DownloadManager>().Dispose();
                _singleInstance?.Dispose();
            };

            _logger.Info("Main window shown");
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void ConfigureServices(IServiceCollection services)
    {
        // Services
        services.AddSingleton<SettingsService>();
        services.AddSingleton<Logger>();
        services.AddSingleton<TerrariaDetector>();
        services.AddSingleton<ModStateService>();
        services.AddSingleton<NexusApiService>();
        services.AddSingleton<NxmLinkHandler>();
        services.AddSingleton<NxmProtocolRegistrar>();
        services.AddSingleton<ModInstallService>();
        services.AddSingleton<UpdateTracker>();
        services.AddSingleton<DownloadManager>();
        services.AddSingleton<NexusBrowserService>();

        // ViewModels
        services.AddTransient<MainViewModel>();
        services.AddTransient<InstalledModsViewModel>();
        services.AddTransient<BrowseViewModel>();
        services.AddTransient<DownloadsViewModel>();
        services.AddTransient<SettingsViewModel>();
    }

    private static void LoadEnvFile()
    {
        var exeDir = AppDomain.CurrentDomain.BaseDirectory;
        var envPath = Path.Combine(exeDir, ".env");

        if (!File.Exists(envPath)) return;

        foreach (var line in File.ReadAllLines(envPath))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith('#') || !trimmed.Contains('=')) continue;

            var eq = trimmed.IndexOf('=');
            var key = trimmed[..eq].Trim();
            var val = trimmed[(eq + 1)..].Trim();

            if (key == "NEXUS_API_KEY" && !string.IsNullOrWhiteSpace(val))
                EnvApiKey = val;
        }
    }

    private static string? FindNxmArg(string[] args)
    {
        return args.FirstOrDefault(a => a.StartsWith("nxm://", StringComparison.OrdinalIgnoreCase));
    }
}
