using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using TerrariaModManager.Views;

namespace TerrariaModManager.Services;

public class NexusBrowserService
{
    private readonly NxmLinkHandler _nxmHandler;
    private readonly Logger _logger;

    public NexusBrowserService(NxmLinkHandler nxmHandler, Logger logger)
    {
        _nxmHandler = nxmHandler;
        _logger = logger;
    }

    /// <summary>
    /// Opens a NexusBrowserWindow for SSO login. Returns the window so the caller
    /// can close it when authorization completes.
    /// </summary>
    public Window OpenSsoWindow(string url)
    {
        _logger.Info($"NexusBrowser: opening SSO window for {url}");

        var window = new NexusBrowserWindow(_nxmHandler);
        window.Title = "Login to Nexus Mods";

        var owner = GetMainWindow();
        if (owner != null)
            window.Show(owner);
        else
            window.Show();

        // Navigate after Show so WebView2 has a chance to initialize
        window.NavigateTo(url);

        return window;
    }

    private static Window? GetMainWindow()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            return desktop.MainWindow;
        return null;
    }
}
