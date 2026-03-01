using System.IO;
using System.Runtime.InteropServices;

namespace TerrariaModManager.Services;

public class NxmProtocolRegistrar
{
    private const string ProtocolKey = @"Software\Classes\nxm";
    private const string DesktopFileName = "terraria-mod-manager-nxm.desktop";

    public bool IsRegistered()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return IsRegisteredWindows();
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return IsRegisteredLinux();
        return false;
    }

    public void Register()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            RegisterWindows();
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            RegisterLinux();
    }

    public void Unregister()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            UnregisterWindows();
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            UnregisterLinux();
    }

    // --- Windows (Registry) ---

    private static bool IsRegisteredWindows()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(ProtocolKey + @"\shell\open\command");
            var val = key?.GetValue(null) as string;
            if (string.IsNullOrEmpty(val)) return false;
            return val.Contains("TerrariaModManager", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static void RegisterWindows()
    {
        var exePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Cannot determine executable path");

        try
        {
            using var nxm = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(ProtocolKey);
            nxm.SetValue(null, "URL:NXM Protocol");
            nxm.SetValue("URL Protocol", "");

            using var icon = nxm.CreateSubKey(@"DefaultIcon");
            icon.SetValue(null, $"\"{exePath}\",1");

            using var command = nxm.CreateSubKey(@"shell\open\command");
            command.SetValue(null, $"\"{exePath}\" \"%1\"");
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to register nxm:// handler: {ex.Message}", ex);
        }
    }

    private static void UnregisterWindows()
    {
        try
        {
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(ProtocolKey, false);
        }
        catch { }
    }

    // --- Linux (.desktop file + xdg-mime) ---

    private static string GetDesktopFilePath()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".local", "share", "applications", DesktopFileName);
    }

    private static bool IsRegisteredLinux()
    {
        return File.Exists(GetDesktopFilePath());
    }

    private static void RegisterLinux()
    {
        var exePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Cannot determine executable path");

        var desktopPath = GetDesktopFilePath();
        Directory.CreateDirectory(Path.GetDirectoryName(desktopPath)!);

        var content = $"""
            [Desktop Entry]
            Type=Application
            Name=TerrariaModder Vault
            Exec={exePath} %u
            MimeType=x-scheme-handler/nxm;
            NoDisplay=true
            Terminal=false
            """;

        // Remove leading whitespace from heredoc-style indentation
        var lines = content.Split('\n').Select(l => l.TrimStart()).ToArray();
        File.WriteAllText(desktopPath, string.Join('\n', lines) + "\n");

        // Register with xdg-mime
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "xdg-mime",
                Arguments = $"default {DesktopFileName} x-scheme-handler/nxm",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            proc?.WaitForExit(5000);
        }
        catch { }
    }

    private static void UnregisterLinux()
    {
        try
        {
            var desktopPath = GetDesktopFilePath();
            if (File.Exists(desktopPath))
                File.Delete(desktopPath);
        }
        catch { }
    }
}
