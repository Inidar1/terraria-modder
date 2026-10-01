using System.IO;
using System.Runtime.InteropServices;

namespace TerrariaModManager.Services;

public class DetectedInstall
{
    public string Path { get; set; } = "";
    public string Source { get; set; } = "";
    public bool HasTerrariaModder { get; set; }
}

public class TerrariaDetector
{
    public List<DetectedInstall> FindAllInstalls()
    {
        var results = new List<DetectedInstall>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            FindWindowsInstalls(results, seen);
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            FindLinuxInstalls(results, seen);
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            FindMacInstalls(results, seen);

        return results;
    }

    public bool Validate(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        // Windows: Terraria.exe; Linux/macOS: Terraria (no extension)
        return File.Exists(System.IO.Path.Combine(path, "Terraria.exe"))
            || File.Exists(System.IO.Path.Combine(path, "Terraria"));
    }

    public bool HasTerrariaModder(string path)
    {
        return Directory.Exists(System.IO.Path.Combine(path, "TerrariaModder", "core"))
            && File.Exists(System.IO.Path.Combine(path, "TerrariaModder", "core", "TerrariaModder.Core.dll"));
    }

    private void FindWindowsInstalls(List<DetectedInstall> results, HashSet<string> seen)
    {
        // 1. Steam registry — direct install location
        TryAddFromRegistry(results, seen,
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Steam App 105600",
            "InstallLocation", "Steam (registry)");

        // 2. Steam library folders — parse libraryfolders.vdf
        TryAddFromSteamLibrariesWindows(results, seen);

        // 3. Common Windows paths
        string[] commonPaths =
        {
            @"C:\Program Files (x86)\Steam\steamapps\common\Terraria",
            @"C:\Program Files\Steam\steamapps\common\Terraria",
            @"D:\Steam\steamapps\common\Terraria",
            @"D:\SteamLibrary\steamapps\common\Terraria",
            @"E:\Steam\steamapps\common\Terraria",
            @"E:\SteamLibrary\steamapps\common\Terraria",
        };

        foreach (var path in commonPaths)
            TryAdd(results, seen, path, "Common path");
    }

    private void FindLinuxInstalls(List<DetectedInstall> results, HashSet<string> seen)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        // Steam Proton prefix (most common for modded Terraria on Linux)
        var protonPrefixes = new[]
        {
            Path.Combine(home, ".steam", "steam", "steamapps", "common", "Terraria"),
            Path.Combine(home, ".local", "share", "Steam", "steamapps", "common", "Terraria"),
            Path.Combine(home, ".var", "app", "com.valvesoftware.Steam", ".local", "share", "Steam", "steamapps", "common", "Terraria"), // Flatpak Steam
        };

        foreach (var path in protonPrefixes)
            TryAdd(results, seen, path, "Steam (Linux)");

        // Parse libraryfolders.vdf for additional Steam libraries
        TryAddFromSteamLibrariesLinux(results, seen, home);
    }

    private void FindMacInstalls(List<DetectedInstall> results, HashSet<string> seen)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var paths = new[]
        {
            Path.Combine(home, "Library", "Application Support", "Steam", "steamapps", "common", "Terraria"),
        };

        foreach (var path in paths)
            TryAdd(results, seen, path, "Steam (macOS)");
    }

    private void TryAddFromRegistry(List<DetectedInstall> results, HashSet<string> seen,
        string keyPath, string valueName, string source)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(keyPath);
            var val = key?.GetValue(valueName) as string;
            if (!string.IsNullOrEmpty(val))
                TryAdd(results, seen, val, source);
        }
        catch { }
    }

    private void TryAddFromSteamLibrariesWindows(List<DetectedInstall> results, HashSet<string> seen)
    {
        try
        {
            string? steamPath = null;

            using (var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Wow6432Node\Valve\Steam"))
                steamPath = key?.GetValue("InstallPath") as string;

            if (steamPath == null)
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Valve\Steam");
                steamPath = key?.GetValue("InstallPath") as string;
            }

            if (steamPath == null) return;

            TryAdd(results, seen,
                Path.Combine(steamPath, "steamapps", "common", "Terraria"),
                "Steam library");

            ParseLibraryFolders(results, seen,
                Path.Combine(steamPath, "steamapps", "libraryfolders.vdf"));
        }
        catch { }
    }

    private void TryAddFromSteamLibrariesLinux(List<DetectedInstall> results, HashSet<string> seen, string home)
    {
        var vdfPaths = new[]
        {
            Path.Combine(home, ".steam", "steam", "steamapps", "libraryfolders.vdf"),
            Path.Combine(home, ".local", "share", "Steam", "steamapps", "libraryfolders.vdf"),
            Path.Combine(home, ".var", "app", "com.valvesoftware.Steam", ".local", "share", "Steam", "steamapps", "libraryfolders.vdf"),
        };

        foreach (var vdfPath in vdfPaths)
            ParseLibraryFolders(results, seen, vdfPath);
    }

    private void ParseLibraryFolders(List<DetectedInstall> results, HashSet<string> seen, string vdfPath)
    {
        try
        {
            if (!File.Exists(vdfPath)) return;

            var lines = File.ReadAllLines(vdfPath);
            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("\"path\""))
                {
                    var parts = trimmed.Split('"');
                    if (parts.Length >= 4)
                    {
                        var libPath = parts[3];
                        TryAdd(results, seen,
                            Path.Combine(libPath, "steamapps", "common", "Terraria"),
                            "Steam library");
                    }
                }
            }
        }
        catch { }
    }

    private void TryAdd(List<DetectedInstall> results, HashSet<string> seen, string path, string source)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        try
        {
            var normalized = Path.GetFullPath(path);
            if (!seen.Add(normalized)) return;
            if (!Validate(normalized)) return;

            results.Add(new DetectedInstall
            {
                Path = normalized,
                Source = source,
                HasTerrariaModder = HasTerrariaModder(normalized)
            });
        }
        catch { }
    }
}
