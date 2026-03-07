using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.WebView.Desktop;

namespace TerrariaModManager;

public class Program
{
    // Add an extra directory to the Windows DLL search path so native DLLs in
    // the deps/ subfolder (libSkiaSharp, libHarfBuzzSharp, av_libglesv2, etc.)
    // are found before Avalonia's P/Invoke loads them.
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetDllDirectory(string lpPathName);

    [STAThread]
    public static void Main(string[] args)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var depsPath = Path.Combine(AppContext.BaseDirectory, "deps");
            if (Directory.Exists(depsPath))
                SetDllDirectory(depsPath);
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace()
            .UseDesktopWebView();
}
