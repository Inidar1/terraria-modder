using System;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using TerrariaModder.Core.Logging;

namespace TerrariaModder.Core.Config
{
    /// <summary>
    /// Configuration for TerrariaModder folder structure.
    /// Reads from config.json in the core folder.
    /// </summary>
    public class CoreConfig
    {
        private static CoreConfig _instance;
        private static readonly object _lock = new object();

        /// <summary>
        /// Root folder name (e.g., "TerrariaModder" or "Mods").
        /// </summary>
        public string RootFolder { get; private set; } = "Mods";

        /// <summary>
        /// Core folder path relative to root (e.g., "core" or "").
        /// </summary>
        public string CoreFolder { get; private set; } = "";

        /// <summary>
        /// Dependencies folder path relative to root (e.g., "core/deps" or "Libs").
        /// </summary>
        public string DepsFolder { get; private set; } = "Libs";

        /// <summary>
        /// Mods folder path relative to root (e.g., "mods" or "plugins").
        /// </summary>
        public string ModsFolder { get; private set; } = "plugins";

        /// <summary>
        /// Logs folder path relative to root (e.g., "core/logs" or "logs").
        /// </summary>
        public string LogsFolder { get; private set; } = "logs";

        /// <summary>
        /// Global minimum log level for all mod loggers.
        /// Set in config.json: "logLevel": "debug" | "info" | "warn" | "error"
        /// Default is Info. Set to Debug for troubleshooting.
        /// </summary>
        public LogLevel GlobalLogLevel { get; private set; } = LogLevel.Info;

        /// <summary>
        /// Absolute path to the Terraria game folder.
        /// </summary>
        public string GameFolder { get; private set; }

        /// <summary>
        /// Enables experimental custom tile runtime IDs and related patches.
        /// Disabled by default due deep world/render/save hooks.
        /// </summary>
        public bool ExperimentalCustomTiles { get; private set; } = false;

        /// <summary>
        /// Absolute path to the root folder (e.g., Terraria/TerrariaModder/).
        /// </summary>
        public string RootPath { get; private set; }

        /// <summary>
        /// Absolute path to the core folder (e.g., Terraria/TerrariaModder/core/).
        /// </summary>
        public string CorePath { get; private set; }

        /// <summary>
        /// Absolute path to the mods folder (e.g., Terraria/TerrariaModder/mods/).
        /// </summary>
        public string ModsPath { get; private set; }

        /// <summary>
        /// Absolute path to the logs folder (e.g., Terraria/TerrariaModder/core/logs/).
        /// </summary>
        public string LogsPath { get; private set; }

        /// <summary>
        /// Get the singleton instance. Loads config on first access.
        /// </summary>
        public static CoreConfig Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_lock)
                    {
                        if (_instance == null)
                        {
                            _instance = Load();
                        }
                    }
                }
                return _instance;
            }
        }

        private CoreConfig() { }

        /// <summary>
        /// Load configuration from config.json.
        /// </summary>
        private static CoreConfig Load()
        {
            var config = new CoreConfig();

            // Core.dll is at: Terraria/TerrariaModder/core/TerrariaModder.Core.dll
            // or at: Terraria/Mods/TerrariaModder.Core.dll (legacy)
            string coreLocation = Assembly.GetExecutingAssembly().Location;
            string coreDir = Path.GetDirectoryName(coreLocation);

            // Look for config.json in the same directory as Core.dll
            string configPath = Path.Combine(coreDir, "config.json");

            if (File.Exists(configPath))
            {
                try
                {
                    string json = File.ReadAllText(configPath);
                    ParseConfig(config, json);
                }
                catch
                {
                    // Fall through to defaults if parse fails
                }
            }

            // Calculate absolute paths
            // The Core.dll location tells us where we are in the structure
            // If coreFolder is set, Core.dll is in {root}/{coreFolder}/
            // Otherwise, Core.dll is in {root}/

            if (!string.IsNullOrEmpty(config.CoreFolder))
            {
                // Core.dll is in {game}/{root}/{coreFolder}/
                // So {game}/{root} is parent of coreDir
                config.CorePath = coreDir;
                config.RootPath = Path.GetDirectoryName(coreDir);
                config.GameFolder = Path.GetDirectoryName(config.RootPath);
            }
            else
            {
                // Core.dll is in {game}/{root}/
                config.CorePath = coreDir;
                config.RootPath = coreDir;
                config.GameFolder = Path.GetDirectoryName(coreDir);
            }

            // Calculate mods and logs paths
            config.ModsPath = string.IsNullOrEmpty(config.ModsFolder)
                ? config.RootPath
                : Path.Combine(config.RootPath, config.ModsFolder);

            config.LogsPath = string.IsNullOrEmpty(config.LogsFolder)
                ? config.CorePath
                : Path.Combine(config.RootPath, config.LogsFolder);

            return config;
        }

        /// <summary>
        /// Parse config.json content using regex (no JSON dependency).
        /// </summary>
        private static void ParseConfig(CoreConfig config, string json)
        {
            var rootMatch = Regex.Match(json, @"""rootFolder""\s*:\s*""([^""]*)""", RegexOptions.IgnoreCase);
            if (rootMatch.Success) config.RootFolder = rootMatch.Groups[1].Value;

            var coreMatch = Regex.Match(json, @"""coreFolder""\s*:\s*""([^""]*)""", RegexOptions.IgnoreCase);
            if (coreMatch.Success) config.CoreFolder = coreMatch.Groups[1].Value;

            var depsMatch = Regex.Match(json, @"""depsFolder""\s*:\s*""([^""]*)""", RegexOptions.IgnoreCase);
            if (depsMatch.Success) config.DepsFolder = depsMatch.Groups[1].Value;

            var modsMatch = Regex.Match(json, @"""modsFolder""\s*:\s*""([^""]*)""", RegexOptions.IgnoreCase);
            if (modsMatch.Success) config.ModsFolder = modsMatch.Groups[1].Value;

            var logsMatch = Regex.Match(json, @"""logsFolder""\s*:\s*""([^""]*)""", RegexOptions.IgnoreCase);
            if (logsMatch.Success) config.LogsFolder = logsMatch.Groups[1].Value;

            var logLevelMatch = Regex.Match(json, @"""logLevel""\s*:\s*""([^""]*)""", RegexOptions.IgnoreCase);
            if (logLevelMatch.Success)
            {
                switch (logLevelMatch.Groups[1].Value.ToLowerInvariant())
                {
                    case "debug": config.GlobalLogLevel = LogLevel.Debug; break;
                    case "warn": config.GlobalLogLevel = LogLevel.Warn; break;
                    case "error": config.GlobalLogLevel = LogLevel.Error; break;
                    default: config.GlobalLogLevel = LogLevel.Info; break;
                }
            }

            var experimentalTilesMatch = Regex.Match(json, @"""experimental_custom_tiles""\s*:\s*(true|false)", RegexOptions.IgnoreCase);
            if (experimentalTilesMatch.Success)
            {
                config.ExperimentalCustomTiles = experimentalTilesMatch.Groups[1].Value.Equals("true", StringComparison.OrdinalIgnoreCase);
            }
        }

        /// <summary>
        /// Force reload of configuration (useful for testing).
        /// </summary>
        public static void Reload()
        {
            lock (_lock)
            {
                _instance = null;
            }
        }
    }
}
