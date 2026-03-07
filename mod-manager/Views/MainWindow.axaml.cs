using System.IO;
using Avalonia.Controls;
using TerrariaModManager.Models;
using TerrariaModManager.ViewModels;

namespace TerrariaModManager.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        try
        {
            InitializeComponent();
        }
        catch (Exception ex)
        {
            var msg = UnwrapException(ex);
            File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "crash.log"), msg);
            throw;
        }
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is MainViewModel vm)
        {
            Func<string, string, string?, Task<InlineBrowserResult?>> openBrowser =
                (url, modName, toast) => BrowserPanel.OpenAsync(url, modName, toast);

            vm.OpenBrowser = openBrowser;
            vm.BrowseVm.OpenBrowser = openBrowser;
            vm.InstalledModsVm.OpenBrowser = openBrowser;
            vm.SettingsVm.OpenBrowser = openBrowser;
            vm.SettingsVm.CloseBrowser = () => BrowserPanel.Close();
            vm.SettingsVm.ClearBrowserCookies = () => BrowserPanel.ClearCookies();
            vm.SetBrowserDownloadCallback = cb =>
            {
                BrowserPanel.DownloadStartedCallback = () =>
                {
                    // Hide the browser immediately — blocks native WebView2 input during update
                    BrowserPanel.IsVisible = false;
                    cb();
                };
            };
        }
    }

    private static string UnwrapException(Exception ex)
    {
        var sb = new System.Text.StringBuilder();
        var current = ex;
        while (current != null)
        {
            sb.AppendLine($"[{current.GetType().Name}] {current.Message}");
            sb.AppendLine(current.StackTrace);
            sb.AppendLine("---");
            current = current.InnerException;
        }
        return sb.ToString();
    }
}
