using System.IO;
using System.Runtime.InteropServices;
using MsBox.Avalonia.Enums;
using TerrariaModManager.Helpers;

namespace TerrariaModManager.Services;

public class NxmProtocolRegistrar
{
    private const string ProtocolKey = @"Software\Classes\nxm";
    private const string DesktopFileName = "terrariamodder-vault-nxm.desktop";

    private readonly Logger _logger;

    public NxmProtocolRegistrar(Logger logger) => _logger = logger;

    public event Action? RegistrationChanged;

    /// <summary>
    /// If not already registered, shows a confirmation dialog and registers if the user agrees.
    /// Returns true if registered (either already was, or just registered now).
    /// </summary>
    public async Task<bool> RegisterIfNeededAsync()
    {
        if (IsRegistered()) return true;

        var answer = await DialogHelper.ShowDialog(
            "Register Nexus Links",
            "You must register Nexus Links to download mods.\n\nRegister now?",
            ButtonEnum.YesNo, Icon.Question);

        if (answer != ButtonResult.Yes) return false;

        Register();
        return true;
    }

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
        _logger.Info("NXM: registering protocol handler");
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            RegisterWindows();
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            RegisterLinux();
        _logger.Info("NXM: registration complete");
        RegistrationChanged?.Invoke();
    }

    public void Unregister()
    {
        _logger.Info("NXM: unregistering protocol handler");
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            UnregisterWindows();
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            UnregisterLinux();
        RegistrationChanged?.Invoke();
    }

    /// <summary>
    /// If the nxm:// handler is registered but points to a stale/different exe path,
    /// silently re-registers to the current exe. Call this on startup.
    /// </summary>
    public void AutoRepairIfStale()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            AutoRepairIfStaleWindows();
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            AutoRepairIfStaleLinux();
    }

    // --- Windows (Registry) ---

    /// <summary>
    /// Returns the exe path currently registered for the nxm:// protocol,
    /// or null if not registered or the command can't be parsed.
    /// </summary>
    private static string? GetRegisteredExePathWindows()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(ProtocolKey + @"\shell\open\command");
            var val = key?.GetValue(null) as string;
            if (string.IsNullOrEmpty(val)) return null;

            // Command format: "C:\path\to\exe.exe" "%1"
            val = val.Trim();
            if (val.StartsWith('"'))
            {
                var end = val.IndexOf('"', 1);
                if (end > 1)
                    return val[1..end];
            }

            // No quotes — take everything before first space
            var space = val.IndexOf(' ');
            return space > 0 ? val[..space] : val;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsRegisteredWindows()
    {
        var registeredPath = GetRegisteredExePathWindows();
        if (registeredPath == null) return false;

        var currentExe = Environment.ProcessPath;
        if (currentExe == null)
        {
            // Can't determine current path — fall back to name check
            return registeredPath.Contains("TerrariaModderVault", StringComparison.OrdinalIgnoreCase)
                   && File.Exists(registeredPath);
        }

        return string.Equals(registeredPath, currentExe, StringComparison.OrdinalIgnoreCase);
    }

    private void AutoRepairIfStaleWindows()
    {
        try
        {
            var registeredPath = GetRegisteredExePathWindows();
            if (registeredPath == null) return; // not registered at all — nothing to repair

            var currentExe = Environment.ProcessPath;
            if (currentExe == null) return;

            if (!string.Equals(registeredPath, currentExe, StringComparison.OrdinalIgnoreCase))
            {
                _logger.Info($"NXM: stale registration detected (was: \"{registeredPath}\", now: \"{currentExe}\") — repairing");
                RegisterWindows();
                _logger.Info("NXM: auto-repair complete");
                RegistrationChanged?.Invoke();
            }
        }
        catch (Exception ex)
        {
            _logger.Warn($"NXM: auto-repair failed: {ex.Message}");
        }
    }

    private void RegisterWindows()
    {
        var exePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Cannot determine executable path");

        _logger.Info($"NXM: writing registry for \"{exePath}\"");
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
            throw new InvalidOperationException($"Failed to write to registry: {ex.Message}", ex);
        }

        // Verify the write actually stuck
        var written = GetRegisteredExePathWindows();
        if (written == null || !string.Equals(written, exePath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Registry write appeared to succeed but the value didn't stick. " +
                $"Written: \"{written ?? "(none)"}\", expected: \"{exePath}\"");

        _logger.Info("NXM: registry write verified");
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

    private static string? GetRegisteredExePathLinux()
    {
        var desktopPath = GetDesktopFilePath();
        if (!File.Exists(desktopPath)) return null;

        try
        {
            foreach (var line in File.ReadAllLines(desktopPath))
            {
                if (!line.StartsWith("Exec=", StringComparison.Ordinal)) continue;
                // Exec=/path/to/exe %u
                var rest = line["Exec=".Length..].Trim();
                var space = rest.IndexOf(' ');
                return space > 0 ? rest[..space] : rest;
            }
        }
        catch { }

        return null;
    }

    private static bool IsRegisteredLinux()
    {
        var registeredPath = GetRegisteredExePathLinux();
        if (registeredPath == null) return false;

        var currentExe = Environment.ProcessPath;
        if (currentExe == null)
            return File.Exists(registeredPath);

        return string.Equals(registeredPath, currentExe, StringComparison.Ordinal);
    }

    private void AutoRepairIfStaleLinux()
    {
        try
        {
            var registeredPath = GetRegisteredExePathLinux();
            if (registeredPath == null) return;

            var currentExe = Environment.ProcessPath;
            if (currentExe == null) return;

            if (!string.Equals(registeredPath, currentExe, StringComparison.Ordinal))
            {
                _logger.Info($"NXM: stale .desktop registration detected — repairing");
                RegisterLinux();
                _logger.Info("NXM: auto-repair complete");
                RegistrationChanged?.Invoke();
            }
        }
        catch (Exception ex)
        {
            _logger.Warn($"NXM: auto-repair failed: {ex.Message}");
        }
    }

    private void RegisterLinux()
    {
        var exePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Cannot determine executable path");

        _logger.Info($"NXM: writing .desktop file for \"{exePath}\"");
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
