using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using TerrariaModder.Core.Config;

namespace TerrariaModder.Core.Logging
{
    /// <summary>
    /// A log entry for display in the UI.
    /// </summary>
    public class LogEntry
    {
        public string ModId { get; set; }
        public LogLevel Level { get; set; }
        public string Message { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Central log management. All mods write to a single shared log file.
    /// </summary>
    public static class LogManager
    {
        private static readonly Dictionary<string, ILogger> _loggers = new Dictionary<string, ILogger>();
        private static volatile string _logFilePath;
        private static SessionLogWriter _sessionLog;
        /// <summary>Canonical log for this process only; the combined log remains for compatibility.</summary>
        public static string SessionLogPath => _sessionLog?.FilePath;
        private static volatile ILogger _coreLogger;
        private static volatile bool _initialized;
        private static readonly object _initLock = new object();
        private const int MAX_OLD_LOGS = 5;
        private const int MAX_RECENT_LOGS = 100;
        private const long MAX_LOG_SIZE = 5 * 1024 * 1024; // 5MB

        // Ring buffer for recent logs (for UI display)
        private static readonly List<LogEntry> _recentLogs = new List<LogEntry>();
        private static readonly object _logLock = new object();
        private static readonly object _fileLock = new object();

        private static bool? _isDedicatedServerOverride;

        /// <summary>
        /// When true, log messages are written to file only (not Console).
        /// Used during dedicated server startup to avoid flooding the interactive world selection prompt.
        /// </summary>
        public static bool SuppressConsole { get; set; }

        /// <summary>
        /// True when running as a dedicated server.
        /// Causes logs to go to terrariamodder.server.log instead of terrariamodder.log.
        /// Auto-detects from TERRARIA_MODDER_DEDSERV env var (set by injector before mod loading).
        /// Can be overridden explicitly by PluginLoader.
        /// </summary>
        public static bool IsDedicatedServer
        {
            get => _isDedicatedServerOverride ?? (Environment.GetEnvironmentVariable("TERRARIA_MODDER_DEDSERV") == "1");
            set => _isDedicatedServerOverride = value;
        }

        /// <summary>
        /// Initialize the log manager. Call once at startup.
        /// </summary>
        public static void Initialize()
        {
            if (_initialized) return;

            lock (_initLock)
            {
                if (_initialized) return;

                try
                {
                    // Get log directory from CoreConfig
                    string logDirectory = CoreConfig.Instance.LogsPath;

                    if (!Directory.Exists(logDirectory))
                    {
                        Directory.CreateDirectory(logDirectory);
                    }

                    string logName = IsDedicatedServer ? "terrariamodder.server.log" : "terrariamodder.log";
                    _logFilePath = Path.Combine(logDirectory, logName);

                    // Rotate if log is too large
                    RotateLogIfNeeded();

                    try
                    {
                        _sessionLog = new SessionLogWriter(logDirectory, IsDedicatedServer);
                        AppDomain.CurrentDomain.ProcessExit += (_, __) =>
                        {
                            lock (_fileLock) _sessionLog?.Dispose();
                        };
                    }
                    catch (Exception ex)
                    {
                        // A user may be able to append the existing log but not create a new one.
                        // Optional session history must not prevent Core initialization.
                        Console.Error.WriteLine($"[TerrariaModder] Per-session log unavailable; using combined log: {ex.Message}");
                    }

                    // Create core logger
                    _coreLogger = new ModLogger("core");
                    _coreLogger.MinLevel = CoreConfig.Instance.GlobalLogLevel;
                    _initialized = true;

                    // Write session header
                    WriteToSharedFile($"=== TerrariaModder session started at {DateTime.Now:yyyy-MM-dd HH:mm:ss}; PID {System.Diagnostics.Process.GetCurrentProcess().Id}; log {SessionLogPath} ===");
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[TerrariaModder] LogManager.Initialize failed: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Get or create a logger for a mod.
        /// </summary>
        public static ILogger GetLogger(string modId)
        {
            if (string.IsNullOrEmpty(modId))
            {
                throw new ArgumentException("Mod ID cannot be null or empty", nameof(modId));
            }

            if (!_initialized)
            {
                Initialize();
            }

            lock (_initLock)
            {
                if (!_loggers.TryGetValue(modId, out var logger))
                {
                    logger = new ModLogger(modId);
                    logger.MinLevel = Config.CoreConfig.Instance.GlobalLogLevel;
                    _loggers[modId] = logger;
                }

                return logger;
            }
        }

        /// <summary>
        /// Get the core framework logger.
        /// </summary>
        public static ILogger Core
        {
            get
            {
                if (!_initialized)
                {
                    Initialize();
                }
                return _coreLogger;
            }
        }

        /// <summary>
        /// Write a message to the shared log file.
        /// </summary>
        internal static void WriteToSharedFile(string message)
        {
            if (string.IsNullOrEmpty(_logFilePath)) return;

            try
            {
                lock (_fileLock)
                {
                    // A compatibility-file sharing failure must not lose the per-session record.
                    _sessionLog?.Write(message);
                    File.AppendAllText(_logFilePath, message + Environment.NewLine);
                }
            }
            catch (Exception ex)
            {
                try { Console.Error.WriteLine($"[TerrariaModder] Log file write failed: {ex.Message}"); }
                catch { }
            }
        }

        /// <summary>
        /// Add a log entry to the recent logs buffer (for UI display).
        /// </summary>
        internal static void AddRecentLog(string modId, LogLevel level, string message)
        {
            lock (_logLock)
            {
                _recentLogs.Insert(0, new LogEntry
                {
                    ModId = modId,
                    Level = level,
                    Message = message,
                    Timestamp = DateTime.Now
                });

                // Trim to max size
                while (_recentLogs.Count > MAX_RECENT_LOGS)
                {
                    _recentLogs.RemoveAt(_recentLogs.Count - 1);
                }
            }
        }

        /// <summary>
        /// Get recent log entries for UI display.
        /// </summary>
        public static List<LogEntry> GetRecentLogs(int count = 50)
        {
            lock (_logLock)
            {
                return _recentLogs.Take(count).ToList();
            }
        }

        /// <summary>
        /// Rotate the log file if it exceeds the maximum size.
        /// </summary>
        private static void RotateLogIfNeeded()
        {
            try
            {
                if (!File.Exists(_logFilePath)) return;

                var fileInfo = new FileInfo(_logFilePath);
                if (fileInfo.Length < MAX_LOG_SIZE) return;

                // Rotate: rename current log with timestamp
                string logDirectory = Path.GetDirectoryName(_logFilePath);
                string backupName = $"{Path.GetFileNameWithoutExtension(_logFilePath)}_{DateTime.UtcNow:yyyyMMdd_HHmmss_fffffff}_{Guid.NewGuid():N}.log";
                string backupPath = Path.Combine(logDirectory, backupName);

                try
                {
                    File.Move(_logFilePath, backupPath);
                }
                catch (Exception ex)
                {
                    try { Console.Error.WriteLine($"[TerrariaModder] Log rotation move failed: {ex.Message}"); }
                    catch { }
                    // Preserve the old log if rotation fails; never delete the only copy.
                }

                // Clean up old backup logs (keep only most recent)
                CleanupOldLogs(logDirectory);
            }
            catch (Exception ex)
            {
                try { Console.Error.WriteLine($"[TerrariaModder] Log rotation failed: {ex.Message}"); }
                catch { }
            }
        }

        /// <summary>
        /// Remove old backup log files, keeping only the most recent ones.
        /// </summary>
        private static void CleanupOldLogs(string logDirectory)
        {
            try
            {
                var backupLogs = Directory.GetFiles(logDirectory, Path.GetFileNameWithoutExtension(_logFilePath) + "_*.log")
                    .Select(f => new FileInfo(f))
                    .OrderByDescending(f => f.LastWriteTime)
                    .Skip(MAX_OLD_LOGS)
                    .ToList();

                foreach (var oldLog in backupLogs)
                {
                    try { oldLog.Delete(); } catch { }
                }

            }
            catch
            {
                // Cleanup is non-critical
            }
        }
    }
}
