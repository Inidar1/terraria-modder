using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Terraria;
using TerrariaModder.Core.Assets;
using TerrariaModder.Core.Config;
using TerrariaModder.Core.Debug;
using TerrariaModder.Core.Events;
using TerrariaModder.Core.Input;
using TerrariaModder.Core.Logging;
using TerrariaModder.Core.Manifest;
using TerrariaModder.Core.Patches;
using TerrariaModder.Core.UI;

namespace TerrariaModder.Core
{
    /// <summary>
    /// Mod state tracking.
    /// </summary>
    public enum ModState
    {
        Discovered,
        DependencyError,
        Loading,
        Loaded,
        Errored,
        Disabled
    }

    /// <summary>
    /// Runtime information about a loaded mod.
    /// </summary>
    public class ModInfo
    {
        public ModManifest Manifest { get; set; }
        public IMod Instance { get; set; }
        public ModState State { get; set; }
        public ModContext Context { get; set; }
        public string ErrorMessage { get; set; }
        public Assembly Assembly { get; set; }
        public DateTime? LoadedAt { get; set; }

        /// <summary>
        /// Version compatibility warning (if mod requires newer Core).
        /// </summary>
        public string VersionWarning { get; set; }

        /// <summary>
        /// Loaded icon texture (Texture2D via reflection). Null if no icon.
        /// </summary>
        public object IconTexture { get; set; }
    }

    /// <summary>
    /// Harmony patch that triggers plugin loading when Terraria.Main initializes.
    /// </summary>
    [HarmonyPatch(typeof(Main))]
    [HarmonyPatch(MethodType.Constructor)]
    internal static class MainInitPatch
    {
        [HarmonyPostfix]
        public static void Postfix()
        {
            PluginLoader.LoadPlugins();
        }
    }

    /// <summary>
    /// Manages the keybind update patch. Applied via lifecycle hook (OnGameReady).
    /// </summary>
    internal static class KeybindUpdatePatch
    {
        private static bool _applied = false;
        private static ILogger _log;
        private static Harmony _harmony;

        public static void Initialize(ILogger log)
        {
            _log = log;
            _harmony = new Harmony("com.terrariamodder.keybinds");
        }

        internal static void ApplyPatch()
        {
            if (_applied) return;

            var doUpdateMethod = typeof(Main).GetMethod("DoUpdate",
                BindingFlags.NonPublic | BindingFlags.Instance);

            if (doUpdateMethod != null)
            {
                var postfix = typeof(KeybindUpdatePatch).GetMethod(nameof(DoUpdate_Postfix),
                    BindingFlags.Public | BindingFlags.Static);

                _harmony.Patch(doUpdateMethod, postfix: new HarmonyMethod(postfix));
                _applied = true;
                _log?.Info("[Keybind] Update patch applied");
            }
            else
            {
                _log?.Warn("[Keybind] DoUpdate method not found");
            }
        }

        private static int _keybindErrorCount = 0;

        public static void DoUpdate_Postfix(Main __instance)
        {
            try
            {
                KeybindManager.Update();
            }
            catch (Exception ex)
            {
                _keybindErrorCount++;
                if (_keybindErrorCount <= 3)
                {
                    _log?.Error($"[Keybind] Update error ({_keybindErrorCount}): {ex.Message}");
                }
            }
        }
    }

    /// <summary>
    /// Scans the plugins/ folder for mods, parses manifests, and loads them.
    /// </summary>
    public static class PluginLoader
    {
        public const string FrameworkVersion = "0.2.0";

        private static bool _initialized = false;
        private static readonly List<ModInfo> _mods = new List<ModInfo>();
        private static readonly Dictionary<string, ModInfo> _modsById = new Dictionary<string, ModInfo>();
        private static ILogger _log;
        private static bool _iconsLoaded;

        /// <summary>
        /// Get list of all discovered mods.
        /// </summary>
        public static IReadOnlyList<ModInfo> Mods => _mods.AsReadOnly();

        /// <summary>
        /// Default framework icon (Texture2D). Loaded lazily.
        /// </summary>
        public static object DefaultIcon { get; private set; }

        /// <summary>
        /// Get a mod by ID.
        /// </summary>
        public static ModInfo GetMod(string modId)
        {
            _modsById.TryGetValue(modId, out var mod);
            return mod;
        }

        /// <summary>
        /// Load icon textures for all mods. Called lazily on first UI draw.
        /// Safe to call multiple times; no-ops after first success.
        /// </summary>
        public static void LoadModIcons()
        {
            if (_iconsLoaded) return;

            // Load default Core icon
            var config = CoreConfig.Instance;
            string coreIconPath = Path.Combine(config.CorePath, "assets", "icon.png");
            DefaultIcon = UI.UIRenderer.LoadTexture(coreIconPath);
            if (DefaultIcon == null)
            {
                _log?.Debug($"[PluginLoader] No default icon at {coreIconPath}");
                return; // GraphicsDevice may not be ready yet, retry later
            }

            // Load per-mod icons
            foreach (var mod in _mods)
            {
                if (mod.Manifest?.FolderPath == null) continue;

                // Check manifest "icon" field, or default to icon.png
                string iconFile = mod.Manifest.Icon ?? "icon.png";
                string iconPath = Path.Combine(mod.Manifest.FolderPath, iconFile);

                if (File.Exists(iconPath))
                {
                    mod.IconTexture = UI.UIRenderer.LoadTexture(iconPath);
                }
            }

            _iconsLoaded = true;
            int modIcons = _mods.Count(m => m.IconTexture != null);
            _log?.Info($"[PluginLoader] Loaded icons: default={DefaultIcon != null}, {modIcons} mod icon(s)");
        }

        /// <summary>
        /// Load all plugins from the plugins/ folder.
        /// </summary>
        public static void LoadPlugins()
        {
            if (_initialized) return;

            try
            {
                // Initialize logging first
                LogManager.Initialize();
                _log = LogManager.Core;

                // Guard against CaptureManager crash (must be before Main.Initialize)
                CaptureManagerGuard.Apply(_log);

                // Initialize keybind system
                KeybindManager.Initialize(_log);
                KeybindUpdatePatch.Initialize(_log);

                // Initialize events system
                EventPatches.Initialize(_log);

                // Initialize custom assets system
                AssetSystem.Initialize(_log);

                // Initialize UI color system (must be before ModMenu)
                UIColors.Initialize(CoreConfig.Instance.CorePath, _log);

                // Initialize mod menu (UI)
                ModMenu.Initialize(_log);
                NativeModsMenu.Initialize(_log);

                // Initialize command registry
                CommandRegistry.Initialize(_log);
                RegisterBuiltInCommands();

                _log.Info("=== TerrariaModder Framework v" + FrameworkVersion + " ===");
                _log.Info("Starting plugin discovery...");

                // Get mods folder from CoreConfig
                var config = CoreConfig.Instance;
                string pluginsDir = config.ModsPath;

                _log.Info($"Config: root={config.RootFolder}, mods={config.ModsFolder}, logs={config.LogsFolder}");

                if (!Directory.Exists(pluginsDir))
                {
                    _log.Info($"Creating mods folder: {pluginsDir}");
                    Directory.CreateDirectory(pluginsDir);
                }

                // Phase 1: Discover mods
                DiscoverMods(pluginsDir);

                // Phase 2: Resolve dependencies
                ResolveDependencies();

                // Phase 3: Load mods in dependency order
                LoadMods();

                _initialized = true;

                // Summary
                int loaded = _mods.Count(m => m.State == ModState.Loaded);
                int depError = _mods.Count(m => m.State == ModState.DependencyError);
                int errored = _mods.Count(m => m.State == ModState.Errored);

                _log.Info($"=== Loaded {loaded} mod(s), {depError} dependency error(s), {errored} load error(s) ===");
            }
            catch (Exception ex)
            {
                _log?.Error("Fatal error during plugin loading", ex);
            }
        }

        /// <summary>
        /// Reserved folder names that should not be scanned as mods.
        /// </summary>
        private static readonly HashSet<string> ReservedFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Libs", "logs"
        };

        /// <summary>
        /// Discover all mods by scanning for manifest.json files in plugin folders.
        /// </summary>
        private static void DiscoverMods(string pluginsDir)
        {
            _log.Info($"Scanning: {pluginsDir}");

            // Scan mod folders (each should have manifest.json or a DLL)
            foreach (var dir in Directory.GetDirectories(pluginsDir))
            {
                string folderName = Path.GetFileName(dir);

                // Skip reserved folders
                if (ReservedFolders.Contains(folderName) || folderName.StartsWith("."))
                {
                    _log.Debug($"Skipping reserved folder: {folderName}");
                    continue;
                }

                DiscoverModFromFolder(dir);
            }

            _log.Info($"Discovered {_mods.Count} mod(s)");
        }

        /// <summary>
        /// Discover a mod from a folder, supporting nested subfolders.
        /// </summary>
        private static void DiscoverModFromFolder(string dir)
        {
            // Look for manifest.json in root or any subfolder (but only one allowed per mod)
            string manifestPath = FindManifest(dir);
            ModManifest manifest;

            if (manifestPath != null)
            {
                _log.Debug($"Found manifest: {manifestPath}");
                manifest = ManifestParser.Parse(manifestPath);
                if (manifest == null)
                {
                    _log.Error($"Failed to parse manifest: {manifestPath}");
                    return;
                }
            }
            else
            {
                // Look for DLLs recursively
                var dlls = FindDlls(dir);
                if (dlls.Count == 0)
                {
                    _log.Debug($"Skipping empty folder: {dir}");
                    return;
                }

                string dllName = Path.GetFileName(dlls[0]);
                _log.Warn($"No manifest.json in {Path.GetFileName(dir)}, using defaults");
                manifest = ManifestParser.CreateDefault(dir, dllName);
                if (manifest == null)
                {
                    _log.Error($"Failed to create default manifest for: {Path.GetFileName(dir)}");
                    return;
                }
            }

            RegisterMod(manifest, null);
        }

        /// <summary>
        /// Find manifest.json in folder or subfolders (only one allowed).
        /// </summary>
        private static string FindManifest(string dir)
        {
            // Check root first
            string rootJson = Path.Combine(dir, "manifest.json");
            if (File.Exists(rootJson)) return rootJson;

            // Check subfolders
            foreach (var subdir in Directory.GetDirectories(dir, "*", SearchOption.AllDirectories))
            {
                string subJson = Path.Combine(subdir, "manifest.json");
                if (File.Exists(subJson)) return subJson;
            }

            return null;
        }

        /// <summary>
        /// Find all DLLs in folder and subfolders.
        /// </summary>
        private static List<string> FindDlls(string dir)
        {
            return Directory.GetFiles(dir, "*.dll", SearchOption.AllDirectories).ToList();
        }

        /// <summary>
        /// Register a discovered mod.
        /// </summary>
        private static void RegisterMod(ModManifest manifest, string overrideDllPath)
        {
            if (!manifest.IsValid)
            {
                _log.Error($"Invalid manifest for {manifest.Id ?? "unknown"}:");
                foreach (var error in manifest.ValidationErrors)
                {
                    _log.Error($"  - {error}");
                }
                return;
            }

            // Check for duplicate IDs
            if (_modsById.ContainsKey(manifest.Id))
            {
                _log.Warn($"Duplicate mod ID '{manifest.Id}', skipping");
                return;
            }

            // Log any warnings
            foreach (var warning in manifest.ValidationErrors)
            {
                _log.Warn($"[{manifest.Id}] {warning}");
            }

            var modInfo = new ModInfo
            {
                Manifest = manifest,
                State = ModState.Discovered
            };

            _mods.Add(modInfo);
            _modsById[manifest.Id] = modInfo;

            _log.Info($"Discovered: {manifest.Name} v{manifest.Version} by {manifest.Author}");
        }

        /// <summary>
        /// Resolve dependencies and determine load order.
        /// </summary>
        private static void ResolveDependencies()
        {
            if (_mods.Count == 0) return;

            _log.Info("Resolving dependencies...");

            var resolver = new DependencyResolver(_log);
            var manifests = _mods
                .Where(m => m.State == ModState.Discovered)
                .Select(m => m.Manifest)
                .ToList();

            var result = resolver.Resolve(manifests);

            // Mark mods with missing dependencies
            foreach (var kvp in result.MissingDependencies)
            {
                if (_modsById.TryGetValue(kvp.Key, out var modInfo))
                {
                    modInfo.State = ModState.DependencyError;
                    modInfo.ErrorMessage = $"Missing dependencies: {string.Join(", ", kvp.Value)}";
                }
            }

            // Mark mods in circular dependencies
            foreach (var modId in result.CircularDependencies)
            {
                if (_modsById.TryGetValue(modId, out var modInfo))
                {
                    modInfo.State = ModState.DependencyError;
                    modInfo.ErrorMessage = "Circular dependency detected";
                }
            }

            // Reorder _mods list to match load order
            var orderedMods = new List<ModInfo>();

            // First, add mods in dependency order
            foreach (var manifest in result.LoadOrder)
            {
                if (_modsById.TryGetValue(manifest.Id, out var modInfo))
                {
                    orderedMods.Add(modInfo);
                }
            }

            // Then add mods with errors (they won't load but should still be tracked)
            foreach (var modInfo in _mods)
            {
                if (!orderedMods.Contains(modInfo))
                {
                    orderedMods.Add(modInfo);
                }
            }

            _mods.Clear();
            _mods.AddRange(orderedMods);

            if (result.Success)
            {
                _log.Info($"Dependencies resolved. Load order: {string.Join(" -> ", result.LoadOrder.Select(m => m.Id))}");
            }
            else
            {
                int errors = result.MissingDependencies.Count + result.CircularDependencies.Count;
                _log.Warn($"Dependency resolution completed with {errors} error(s)");
            }
        }

        /// <summary>
        /// Load all mods in dependency order.
        /// </summary>
        private static void LoadMods()
        {
            foreach (var modInfo in _mods)
            {
                // Only load mods that are still in Discovered state
                if (modInfo.State != ModState.Discovered) continue;

                LoadMod(modInfo);
            }
        }

        /// <summary>
        /// Load a single mod.
        /// </summary>
        private static void LoadMod(ModInfo modInfo)
        {
            var manifest = modInfo.Manifest;
            modInfo.State = ModState.Loading;

            // Check framework version compatibility
            CheckFrameworkVersion(modInfo);

            try
            {
                // Find DLL (search recursively in subfolders)
                string dllPath = manifest.DllPath;
                if (string.IsNullOrEmpty(dllPath) || !File.Exists(dllPath))
                {
                    // Try to find DLLs recursively in the folder
                    var dlls = FindDlls(manifest.FolderPath);
                    if (dlls.Count == 0)
                    {
                        throw new FileNotFoundException($"No DLL found in {manifest.FolderPath}");
                    }
                    dllPath = dlls[0];
                }

                _log.Debug($"Loading assembly: {Path.GetFileName(dllPath)}");
                Assembly assembly = Assembly.UnsafeLoadFrom(dllPath);
                modInfo.Assembly = assembly;

                // Find IMod implementation
                var modTypes = assembly.GetTypes()
                    .Where(t => t != null &&
                                t.IsClass &&
                                !t.IsAbstract &&
                                typeof(IMod).IsAssignableFrom(t))
                    .ToList();

                if (modTypes.Count == 0)
                {
                    throw new InvalidOperationException($"No IMod implementation found in {Path.GetFileName(dllPath)}");
                }

                if (modTypes.Count > 1)
                {
                    _log.Warn($"[{manifest.Id}] Multiple IMod implementations found, using first");
                }

                // Create instance
                var modType = modTypes[0];
                var mod = (IMod)Activator.CreateInstance(modType);
                if (mod == null)
                {
                    throw new InvalidOperationException($"Failed to create instance of {modType.FullName}");
                }

                // Verify ID matches
                if (mod.Id != manifest.Id)
                {
                    _log.Warn($"[{manifest.Id}] Mod ID mismatch: manifest says '{manifest.Id}', class says '{mod.Id}'");
                }

                // Create config if schema exists
                IModConfig config = null;
                if (!string.IsNullOrEmpty(manifest.ConfigSchemaJson))
                {
                    var logger2 = LogManager.GetLogger(manifest.Id);
                    var schema = ConfigSchema.Parse(manifest.ConfigSchemaJson, logger2);
                    var configPath = Path.Combine(manifest.FolderPath, "config.json");
                    config = new ModConfig(manifest.Id, configPath, schema, logger2);
                }

                // Create context
                var logger = LogManager.GetLogger(manifest.Id);
                var context = new ModContext(logger, manifest.FolderPath, manifest, config);

                // Initialize
                _log.Info($"Initializing: {manifest.Name}");
                mod.Initialize(context);

                modInfo.Instance = mod;
                modInfo.Context = context;
                modInfo.State = ModState.Loaded;
                modInfo.LoadedAt = DateTime.Now;

                _log.Info($"Loaded: {manifest.Name} v{manifest.Version}");
            }
            catch (ReflectionTypeLoadException rtle)
            {
                modInfo.State = ModState.Errored;
                modInfo.ErrorMessage = "Failed to load types";
                _log.Error($"[{manifest.Id}] Failed to load types:");
                foreach (var ex in rtle.LoaderExceptions.Where(e => e != null).Take(3))
                {
                    _log.Error($"  {ex.Message}");
                }
            }
            catch (Exception ex)
            {
                modInfo.State = ModState.Errored;
                modInfo.ErrorMessage = ex.Message;
                _log.Error($"[{manifest.Id}] Failed to load", ex);
            }
        }

        /// <summary>
        /// Call OnWorldLoad on all loaded mods.
        /// </summary>
        public static void NotifyWorldLoad()
        {
            foreach (var modInfo in _mods.Where(m => m.State == ModState.Loaded))
            {
                SafeCall(modInfo, () => modInfo.Instance.OnWorldLoad(), "OnWorldLoad");
            }
        }

        /// <summary>
        /// Call OnWorldUnload on all loaded mods.
        /// </summary>
        public static void NotifyWorldUnload()
        {
            foreach (var modInfo in _mods.Where(m => m.State == ModState.Loaded))
            {
                SafeCall(modInfo, () => modInfo.Instance.OnWorldUnload(), "OnWorldUnload");
            }
        }

        /// <summary>
        /// Unload all mods.
        /// </summary>
        public static void Unload()
        {
            // Unload in reverse order (reverse of dependency order)
            for (int i = _mods.Count - 1; i >= 0; i--)
            {
                var modInfo = _mods[i];
                if (modInfo.State == ModState.Loaded)
                {
                    // Unregister keybinds and commands for this mod
                    KeybindManager.UnregisterMod(modInfo.Manifest.Id);
                    CommandRegistry.UnregisterMod(modInfo.Manifest.Id);

                    SafeCall(modInfo, () => modInfo.Instance.Unload(), "Unload");

                    // Dispose config to release FileSystemWatcher
                    if (modInfo.Context?.Config is IDisposable disposableConfig)
                    {
                        try { disposableConfig.Dispose(); }
                        catch (Exception ex) { _log?.Debug($"Config dispose error for {modInfo.Manifest.Id}: {ex.Message}"); }
                    }
                }
            }

            _mods.Clear();
            _modsById.Clear();
            _initialized = false;
            _log?.Info("All mods unloaded");
        }

        // ---- Lifecycle hooks (called by injector via reflection) ----

        /// <summary>
        /// Called by injector when Main.Initialize() completes.
        /// Main.instance, GraphicsDevice, and Window.Handle are ready.
        /// </summary>
        public static void OnGameReady()
        {
            _log?.Info("Lifecycle: OnGameReady — applying deferred patches");

            try
            {
                KeybindUpdatePatch.ApplyPatch();
            }
            catch (Exception ex)
            {
                _log?.Error($"[Lifecycle] KeybindUpdatePatch failed: {ex.Message}");
            }

            try
            {
                EventPatches.ApplyPatches();
                AssetSystem.ApplyPatches();
                NativeModsMenu.ApplyPatches();
                FrameEvents.OnUIOverlay += DrawTitleScreenOverlay;
                FrameEvents.OnPostUpdate += AssetSystem.OnUpdate;
            }
            catch (Exception ex)
            {
                _log?.Error($"[Lifecycle] EventPatches/AssetSystem failed: {ex.Message}");
            }

            // Inject textures here — OnContentLoaded fires BEFORE OnGameReady because
            // XNA calls LoadContent() from within Initialize(). By this point ApplyPatches()
            // has set _patchesApplied=true and GraphicsDevice is ready (LoadContent completed).
            try
            {
                AssetSystem.OnContentLoaded();
            }
            catch (Exception ex)
            {
                _log?.Error($"[Lifecycle] Texture injection failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Called by injector when Main.LoadContent() completes.
        /// NOTE: Due to XNA calling LoadContent() from within Initialize(),
        /// this fires BEFORE OnGameReady. AssetSystem.OnContentLoaded() will
        /// no-op here (patches not applied yet) and run in OnGameReady instead.
        /// Kept as a hook point in case future subsystems need it.
        /// </summary>
        public static void OnContentLoaded()
        {
            _log?.Info("Lifecycle: OnContentLoaded");

            try
            {
                AssetSystem.OnContentLoaded();
            }
            catch (Exception ex)
            {
                _log?.Error($"[Lifecycle] AssetSystem.OnContentLoaded failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Called by injector on the first Main.Update() frame.
        /// Full game loop is active, all systems operational.
        /// </summary>
        public static void OnFirstUpdate()
        {
            _log?.Info("Lifecycle: OnFirstUpdate");
        }

        /// <summary>
        /// Called by injector when game is exiting (Main_Exiting prefix).
        /// Runs before Terraria disposes audio/systems — mods can save state.
        /// </summary>
        public static void OnShutdown()
        {
            _log?.Info("Lifecycle: OnShutdown — unloading mods");
            Unload();
        }

        /// <summary>
        /// Safely call a mod method, catching and logging any exceptions.
        /// </summary>
        private static void SafeCall(ModInfo modInfo, Action action, string methodName)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                modInfo.State = ModState.Errored;
                modInfo.ErrorMessage = $"{methodName} threw: {ex.Message}";
                _log.Error($"[{modInfo.Manifest.Id}] {methodName} error", ex);
            }
        }

        /// <summary>
        /// Check if a mod's framework_version is compatible with current Core.
        /// </summary>
        private static void CheckFrameworkVersion(ModInfo modInfo)
        {
            var manifest = modInfo.Manifest;
            if (string.IsNullOrEmpty(manifest.FrameworkVersion))
            {
                return; // No version specified, assume compatible
            }

            var constraint = VersionConstraint.Parse(manifest.FrameworkVersion);
            if (!constraint.IsValid)
            {
                _log.Warn($"[{manifest.Id}] Invalid framework_version format: {manifest.FrameworkVersion}");
                return;
            }

            if (!constraint.IsSatisfiedBy(FrameworkVersion))
            {
                string warning = $"Requires Core {manifest.FrameworkVersion}, running {FrameworkVersion}";
                modInfo.VersionWarning = warning;
                _log.Warn($"[{manifest.Id}] {warning} - mod may not work correctly");
            }
        }

        /// <summary>
        /// Register built-in debug commands (help, mods, config, clear).
        /// </summary>
        private static void RegisterBuiltInCommands()
        {
            CommandRegistry.Register("help", "List all commands, or show help for a specific command. Usage: help [command]", args =>
            {
                if (args.Length > 0)
                {
                    var desc = CommandRegistry.GetHelp(args[0]);
                    if (desc != null)
                        CommandRegistry.Write($"{args[0]}: {desc}");
                    else
                        CommandRegistry.Write($"Unknown command: {args[0]}");
                }
                else
                {
                    var commands = CommandRegistry.GetCommands();
                    CommandRegistry.Write($"{commands.Count} command(s) available:");
                    foreach (var cmd in commands)
                    {
                        string prefix = cmd.ModId != null ? $"[{cmd.ModId}] " : "";
                        CommandRegistry.Write($"  {cmd.Name} - {prefix}{cmd.Description}");
                    }
                }
            });

            CommandRegistry.Register("mods", "List loaded mods with status", args =>
            {
                CommandRegistry.Write($"{_mods.Count} mod(s) discovered:");
                foreach (var mod in _mods)
                {
                    string status = mod.State.ToString();
                    string version = mod.Manifest.Version ?? "?";
                    string extra = "";
                    if (mod.State == ModState.Errored)
                        extra = $" - {mod.ErrorMessage}";
                    else if (mod.State == ModState.DependencyError)
                        extra = $" - {mod.ErrorMessage}";
                    else if (!string.IsNullOrEmpty(mod.VersionWarning))
                        extra = $" - {mod.VersionWarning}";

                    CommandRegistry.Write($"  {mod.Manifest.Id} v{version} [{status}]{extra}");
                }
            });

            CommandRegistry.Register("config", "Show config values for a mod. Usage: config <mod-id>", args =>
            {
                if (args.Length == 0)
                {
                    CommandRegistry.Write("Usage: config <mod-id>");
                    return;
                }

                string modId = args[0];
                var modInfo = GetMod(modId);
                if (modInfo == null)
                {
                    CommandRegistry.Write($"Mod not found: {modId}");
                    return;
                }

                var config = modInfo.Context?.Config;
                if (config == null)
                {
                    CommandRegistry.Write($"{modId} has no configuration");
                    return;
                }

                CommandRegistry.Write($"{modId} configuration ({config.FilePath}):");
                foreach (var field in config.Schema)
                {
                    object value;
                    try { value = config.Get<object>(field.Key); }
                    catch (Exception ex) { value = $"(error: {ex.Message})"; }
                    CommandRegistry.Write($"  {field.Key} = {value} (default: {field.Value.Default})");
                }
            });

            CommandRegistry.Register("clear", "Clear console output", args =>
            {
                CommandRegistry.Clear();
            });

            CommandRegistry.Register("pending", "Show pending modded items (overflow from load). Toggle the pending items panel.", args =>
            {
                int count = Assets.AssetSystem.PendingItemCount;
                if (count == 0)
                {
                    CommandRegistry.Write("No pending items.");
                    return;
                }
                Assets.AssetSystem.TogglePendingItemsUI();
                CommandRegistry.Write($"{count} pending item{(count != 1 ? "s" : "")} — panel toggled.");
            });
        }

        /// <summary>
        /// Get list of mods with version warnings.
        /// </summary>
        public static IEnumerable<ModInfo> GetModsWithVersionWarnings()
        {
            return _mods.Where(m => !string.IsNullOrEmpty(m.VersionWarning));
        }

        // Scroll state for version warnings list
        private static int _warningScrollOffset = 0;

        // Update notification state
        private static bool _updateAvailable = false; // Set via GitHub API check
        private static string _updateVersion = ""; // Set via GitHub API check
        private static string _updateUrl = "https://www.nexusmods.com/terraria/mods/135";

        /// <summary>
        /// Draw title screen overlay showing framework version and warnings.
        /// </summary>
        private static void DrawTitleScreenOverlay()
        {
            try
            {
                // Only show on title screen (gameMenu = true)
                var gameMenuField = typeof(Main).GetField("gameMenu", BindingFlags.Public | BindingFlags.Static);
                if (gameMenuField == null) return;
                bool gameMenu = (bool)gameMenuField.GetValue(null);
                if (!gameMenu) return;

                // Get warnings
                var warnings = GetModsWithVersionWarnings().ToList();
                bool hasWarnings = warnings.Count > 0;

                // Position in top-left corner, below FPS counter
                int x = 12;
                int y = 34;

                // Layout constants
                int maxVisibleMods = 5;
                int lineHeight = 20;
                int padding = 10;

                // Calculate heights
                int headerY = y + padding;                    // "TerrariaModder v1.0.0 - X mods loaded"
                int updateY = headerY + 24;                   // "Update available" line (if applicable)
                int buttonY = updateY + 22;                   // Download button (if applicable)

                // Adjust subsequent Y positions based on whether update section exists
                int updateSectionHeight = _updateAvailable ? 58 : 0;
                int warningTitleY = headerY + 26 + updateSectionHeight;
                int listStartY = warningTitleY + 24;
                int visibleCount = hasWarnings ? Math.Min(warnings.Count, maxVisibleMods) : 0;
                int listHeight = visibleCount * lineHeight;
                int scrollHintY = listStartY + listHeight + 4;
                int footerStartY = scrollHintY + (warnings.Count > maxVisibleMods ? 22 : 0);

                // Panel dimensions
                int panelWidth = (hasWarnings || _updateAvailable) ? 360 : 340;
                int panelHeight;
                if (hasWarnings)
                {
                    panelHeight = footerStartY - y + 90; // extra 40px for download button below footer text
                }
                else if (_updateAvailable)
                {
                    panelHeight = buttonY - y + 42;
                }
                else
                {
                    panelHeight = 44;
                }

                // Draw panel background
                UIRenderer.DrawRect(x, y, panelWidth, panelHeight, UIColors.PanelBg);
                UIRenderer.DrawRectOutline(x, y, panelWidth, panelHeight, UIColors.Border, 1);

                // Draw version and mod count
                int loadedCount = _mods.Count(m => m.State == ModState.Loaded);
                string statusText = $"TerrariaModder v{FrameworkVersion} - {loadedCount} mods loaded";
                UIRenderer.DrawText(statusText, x + padding, headerY, UIColors.Info);

                // Draw update notification if available
                if (_updateAvailable)
                {
                    UIRenderer.DrawText($"Update available: v{_updateVersion}", x + padding, updateY, UIColors.Success);

                    // Draw download button
                    int btnX = x + padding;
                    int btnWidth = 110;
                    int btnHeight = 28;
                    bool btnHover = UIRenderer.IsMouseOver(btnX, buttonY, btnWidth, btnHeight);

                    // Button background
                    var btnBg = btnHover ? UIColors.ButtonHover : UIColors.Button;
                    UIRenderer.DrawRect(btnX, buttonY, btnWidth, btnHeight, btnBg);
                    UIRenderer.DrawRectOutline(btnX, buttonY, btnWidth, btnHeight, UIColors.Success, 1);

                    // Button text
                    UIRenderer.DrawText("Download", btnX + 12, buttonY + 6, UIColors.Text);

                    // Handle click
                    if (btnHover && UIRenderer.MouseLeftClick)
                    {
                        try
                        {
                            Process.Start(new ProcessStartInfo
                            {
                                FileName = _updateUrl,
                                UseShellExecute = true
                            });
                        }
                        catch (Exception ex)
                        {
                            _log?.Warn($"Failed to open browser: {ex.Message}");
                        }
                    }
                }

                // Draw warnings if any
                if (hasWarnings)
                {
                    int maxScroll = Math.Max(0, warnings.Count - maxVisibleMods);

                    string warningLine1 = warnings.Count == 1
                        ? "1 mod needs a newer Core version:"
                        : $"{warnings.Count} mods need a newer Core version:";
                    UIRenderer.DrawText(warningLine1, x + padding, warningTitleY, UIColors.Warning);

                    // Handle scrolling (same approach as ItemSpawner)
                    if (UIRenderer.IsMouseOver(x, y, panelWidth, panelHeight))
                    {
                        int scroll = UIRenderer.ScrollWheel;
                        if (scroll != 0)
                        {
                            _warningScrollOffset -= scroll / 30;
                            _warningScrollOffset = Math.Max(0, Math.Min(_warningScrollOffset, maxScroll));
                        }
                    }

                    // Draw mod list
                    for (int i = 0; i < visibleCount; i++)
                    {
                        int modIndex = i + _warningScrollOffset;
                        if (modIndex >= warnings.Count) break;

                        var mod = warnings[modIndex];
                        string modName = mod.Manifest.Name;
                        if (modName.Length > 30) modName = modName.Substring(0, 27) + "...";
                        UIRenderer.DrawText($"  - {modName}", x + padding, listStartY + i * lineHeight, UIColors.TextDim);
                    }

                    // Show scroll indicator if needed
                    if (warnings.Count > maxVisibleMods)
                    {
                        string scrollHint = $"({_warningScrollOffset + 1}-{_warningScrollOffset + visibleCount} of {warnings.Count}) scroll to see more";
                        UIRenderer.DrawText(scrollHint, x + padding, scrollHintY, UIColors.TextHint);
                    }

                    // Draw footer messages
                    UIRenderer.DrawText("Some features may not work.", x + padding, footerStartY, UIColors.TextDim);
                    UIRenderer.DrawText("Update TerrariaModder to fix.", x + padding, footerStartY + 22, UIColors.Text);

                    // Download button
                    int warnBtnX = x + padding;
                    int warnBtnWidth = 110;
                    int warnBtnHeight = 28;
                    int warnBtnY = footerStartY + 50;
                    bool warnBtnHover = UIRenderer.IsMouseOver(warnBtnX, warnBtnY, warnBtnWidth, warnBtnHeight);
                    var warnBtnBg = warnBtnHover ? UIColors.ButtonHover : UIColors.Button;
                    UIRenderer.DrawRect(warnBtnX, warnBtnY, warnBtnWidth, warnBtnHeight, warnBtnBg);
                    UIRenderer.DrawRectOutline(warnBtnX, warnBtnY, warnBtnWidth, warnBtnHeight, UIColors.Warning, 1);
                    UIRenderer.DrawText("Download", warnBtnX + 12, warnBtnY + 6, UIColors.Text);
                    if (warnBtnHover && UIRenderer.MouseLeftClick)
                    {
                        try
                        {
                            Process.Start(new ProcessStartInfo
                            {
                                FileName = _updateUrl,
                                UseShellExecute = true
                            });
                        }
                        catch (Exception ex)
                        {
                            _log?.Warn($"Failed to open browser: {ex.Message}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // Log once then stop trying to render
                _log?.Warn($"[TitleOverlay] Render error (will not retry): {ex.Message}");
                FrameEvents.OnUIOverlay -= DrawTitleScreenOverlay;
            }
        }
    }
}
