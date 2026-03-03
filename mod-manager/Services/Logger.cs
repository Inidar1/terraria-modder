using System.IO;

namespace TerrariaModManager.Services;

public class Logger
{
    private readonly string _logPath;
    private readonly object _lock = new();

    public Logger(SettingsService settings)
    {
        _logPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TerrariaModManager", "app.log");
        
        // Ensure directory exists
        var dir = Path.GetDirectoryName(_logPath);
        if (dir != null) Directory.CreateDirectory(dir);
    }

    public void Info(string message) => Write("INFO", message);
    public void Warn(string message) => Write("WARN", message);
    public void Error(string message) => Write("ERROR", message);
    public void Error(string message, Exception ex) => Write("ERROR", $"{message}: {ex}");

    public string ReadTail(int lines = 100)
    {
        try
        {
            lock (_lock)
            {
                if (!File.Exists(_logPath)) return "(no log file)";
                var allLines = File.ReadAllLines(_logPath);
                var start = Math.Max(0, allLines.Length - lines);
                return string.Join(Environment.NewLine, allLines[start..]);
            }
        }
        catch (Exception ex)
        {
            return $"(could not read log: {ex.Message})";
        }
    }

    public void Clear()
    {
        try
        {
            lock (_lock)
            {
                if (File.Exists(_logPath))
                    File.WriteAllText(_logPath, "");
            }
        }
        catch { }
    }

    private void Write(string level, string message)
    {
        try
        {
            lock (_lock)
            {
                var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{level}] {message}";
                File.AppendAllText(_logPath, line + Environment.NewLine);
            }
        }
        catch { }
    }
}
