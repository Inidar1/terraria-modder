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
using TerrariaModder.Core.Net;
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
    /// Harmony patch that triggers plugin loading when Terraria.Main is constructed.
    /// Uses TargetMethods() instead of [HarmonyPatch(typeof(Main))] because typeof(Main)
    /// resolves to Terraria.exe's Main at compile time. When running TerrariaServer.exe
    /// (loaded via Assembly.UnsafeLoadFrom into the LoadFrom context), that is a different
    /// type identity and the attribute-based patch never fires. Scanning AppDomain at
    /// PatchAll() time finds the actual loaded game type regardless of load context.
    /// IsDedicatedServer is read from the TERRARIA_MODDER_DEDSERV env var (set by injector
    /// before mod loading), so it is reliable even before Main.dedServ is set by DedServ().
    /// </summary>
    [HarmonyPatch]
    internal static class MainInitPatch
    {
        static IEnumerable<MethodBase> TargetMethods()
        {
            var result = new List<MethodBase>();
            var seen = new System.Collections.Generic.HashSet<MethodBase>();
            var dbg = new System.Text.StringBuilder("TargetMethods scan:\n");

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type mainType = null;
                try { mainType = assembly.GetType("Terraria.Main"); }
                catch (Exception ex) { dbg.AppendLine($"  {assembly.GetName().Name}: GetType threw {ex.GetType().Name}: {ex.Message}"); }
                if (mainType == null) continue;
                dbg.AppendLine($"  Found Terraria.Main in {assembly.GetName().Name}");
                ConstructorInfo ctor = null;
                try { ctor = AccessTools.Constructor(mainType); } catch { }
                if (ctor != null && seen.Add(ctor))
                {
                    dbg.AppendLine($"    ctor added");
                    result.Add(ctor);
                }
            }

            // Fallback: compile-time typeof(Main) reference (covers case where GetType fails above)
            try
            {
                var ctor = AccessTools.Constructor(typeof(Main));
                if (ctor != null && seen.Add(ctor))
                {
                    dbg.AppendLine($"  Fallback typeof(Main) ctor added");
                    result.Add(ctor);
                }
            }
            catch (Exception ex) { dbg.AppendLine($"  Fallback typeof(Main) threw: {ex.GetType().Name}: {ex.Message}"); }

            dbg.AppendLine($"Total: {result.Count} ctors");
            return result;
        }

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
        public const string FrameworkVersion = "0.4.1";

        // Static constructor — runs once, before any other PluginLoader code.
        // Installs global unhandled exception handler so Windows error dialogs
        // are captured in the log file (they're otherwise invisible to automation).
        static PluginLoader()
        {
            AppDomain.CurrentDomain.UnhandledException += (sender, args) =>
            {
                try
                {
                    var ex = args.ExceptionObject as Exception;
                    var msg = ex != null
                        ? $"UNHANDLED EXCEPTION (isTerminating={args.IsTerminating}): {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}"
                        : $"UNHANDLED EXCEPTION (isTerminating={args.IsTerminating}): {args.ExceptionObject}";
                    _log?.Error(msg);
                    // Also write to a crash file in case the logger is dead
                    try
                    {
                        var crashPath = Path.Combine(
                            Path.GetDirectoryName(typeof(PluginLoader).Assembly.Location) ?? ".",
                            "crash.log");
                        File.AppendAllText(crashPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {msg}\n");
                    }
                    catch { }
                }
                catch { }
            };
        }

        private static bool _initialized = false;
        private static readonly List<ModInfo> _mods = new List<ModInfo>();
        private static readonly Dictionary<string, ModInfo> _modsById = new Dictionary<string, ModInfo>();
        private static ILogger _log;
        private static bool _iconsLoaded;

        /// <summary>
        /// True if running as TerrariaServer.exe (dedicated server).
        /// Reads the TERRARIA_MODDER_DEDSERV env var set by TerrariaInjector at startup.
        /// Does NOT fall back to Main.dedServ — accessing Main on TerrariaServer.exe can
        /// trigger TypeInitializationException if Main's cctor hasn't completed.
        /// </summary>
        public static bool IsDedicatedServer =>
            Environment.GetEnvironmentVariable("TERRARIA_MODDER_DEDSERV") == "1";

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
                // Initialize logging first (set dedicated-server flag before Initialize)
                LogManager.IsDedicatedServer = IsDedicatedServer;
                LogManager.Initialize();
                _log = LogManager.Core;

                // Guard against CaptureManager crash (must be before Main.Initialize)
                CaptureManagerGuard.Apply(_log);

                // Note: libPath is no longer cleared here. The HostAndPlay_Prefix patch in EventPatches
                // replaces the server spawn to use TerrariaInjector.exe directly, bypassing -loadlib entirely.
                // The server process loads mods through the injector's normal bootstrap, ensuring tileContainer
                // and all mod state is correctly initialized on the server.

                // Initialize asset system on both client and server — internal gating applies (F1)
                AssetSystem.Initialize(_log);

                if (!IsDedicatedServer)
                {
                    // Client / Host & Play: initialize input, UI, and render systems
                    KeybindManager.Initialize(_log);
                    KeybindUpdatePatch.Initialize(_log);
                    EventPatches.Initialize(_log);
                    UIColors.Initialize(CoreConfig.Instance.CorePath, _log);
                    ModMenu.Initialize(_log);
                }
                else
                {
                    // Dedicated server: load server config and activate console command intercept
                    _log.Info("[Server] Running as dedicated server — UI systems skipped");
                    Server.ServerConfig.Load(CoreConfig.Instance.CorePath);
                }

                // Initialize command registry (always — used by both server console and client debug console)
                CommandRegistry.Initialize(_log);
                RegisterBuiltInCommands();

                // Initialize identity and permission systems
                var coreConfig = CoreConfig.Instance;
                Identity.IdentityService.Initialize(coreConfig.CorePath, _log);
                Permissions.PermissionService.Initialize(coreConfig.CorePath, _log);
                Net.ServerCommandDispatch.Initialize(_log);
                Server.DedServProxy.Initialize(_log);

                if (IsDedicatedServer)
                {
                    // Start server console command intercept after CommandRegistry is ready
                    Server.ServerConsole.Initialize(_log);
                    Server.PlayerDataStore.Initialize(_log, coreConfig.CorePath);
                    Server.ServerModdataStore.Initialize(_log, coreConfig.CorePath);

                    // Start HTTP management API early so mods can be monitored during load
                    var srvConfig = Server.ServerConfig.Instance;
                    if (srvConfig != null && !string.IsNullOrEmpty(srvConfig.ManagementApiKey))
                    {
                        try
                        {
                            _managementApi = new Server.ServerManagementApi(_log, srvConfig);
                            _managementApi.Start();
                        }
                        catch (Exception ex)
                        {
                            _log?.Error($"[ServerManagementApi] Failed to start: {ex.Message}");
                        }
                    }
                    else
                    {
                        _log?.Info("[ServerManagementApi] Not started — ManagementApiKey not set in server-config.json");
                    }
                }

                // Initialize net sync (packet intercept for multiplayer config sync)
                NetSync.Initialize(_log);
                var netHarmony = new Harmony("com.terrariamodder.netsync");
                NetSyncPatches.Apply(netHarmony, _log);

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

                // Suppress console output on dedicated server during mod loading
                // to avoid flooding the interactive world selection prompt
                if (IsDedicatedServer)
                    LogManager.SuppressConsole = true;

                // Phase 1: Discover mods
                DiscoverMods(pluginsDir);

                // Phase 2: Resolve dependencies
                ResolveDependencies();

                // Phase 3: Load mods in dependency order
                LoadMods();

                if (IsDedicatedServer)
                {
                    // Apply server-safe asset patches AFTER mods load — mods register custom items
                    // in their Initialize(), so ApplyPatches() (which assigns type IDs) must run
                    // after all mod Initialize() calls complete. OnGameReady is not called for
                    // TerrariaServer.exe, so we call both here manually.
                    AssetSystem.ApplyPatches();
                    DispatchOnContentReady();
                }

                _initialized = true;

                // On dedicated server, keep console suppressed — mod logs go to file only.
                // ServerConsole writes directly to Console and is not affected by this flag.
                // This prevents mod log spam from flooding the interactive world selection prompt.

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
        /// Read the per-instance enabled-mods file from the core folder.
        /// Returns null if no file exists (meaning all mods are enabled).
        /// Checks enabled-mods.server.json or enabled-mods.client.json first, then enabled-mods.json.
        /// File format: JSON array of mod IDs, e.g. ["skip-intro", "quick-keys"]
        /// </summary>
        private static HashSet<string> GetEnabledModIds(string corePath)
        {
            bool isServer = IsDedicatedServer;
            string scopedFile = Path.Combine(corePath, isServer ? "enabled-mods.server.json" : "enabled-mods.client.json");
            string unifiedFile = Path.Combine(corePath, "enabled-mods.json");

            string filePath = File.Exists(scopedFile) ? scopedFile
                            : File.Exists(unifiedFile) ? unifiedFile
                            : null;

            if (filePath == null) return null; // no file = all mods enabled

            try
            {
                string json = File.ReadAllText(filePath);
                var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                // Parse simple JSON string array: ["id1", "id2", ...]
                int i = 0;
                while (i < json.Length)
                {
                    // Find opening quote
                    while (i < json.Length && json[i] != '"') i++;
                    if (i >= json.Length) break;
                    i++; // skip opening quote

                    // Read until closing quote
                    var sb = new System.Text.StringBuilder();
                    while (i < json.Length && json[i] != '"')
                    {
                        if (json[i] == '\\' && i + 1 < json.Length)
                            i++; // skip escape
                        sb.Append(json[i++]);
                    }
                    i++; // skip closing quote

                    string id = sb.ToString();
                    if (!string.IsNullOrEmpty(id))
                        ids.Add(id);
                }

                _log.Info($"[PluginLoader] Instance mod filter: {filePath} ({ids.Count} enabled)");
                return ids;
            }
            catch (Exception ex)
            {
                _log.Warn($"[PluginLoader] Failed to read enabled-mods file {filePath}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Load all mods in dependency order.
        /// Skips mods not present in the per-instance enabled-mods file (if it exists).
        /// </summary>
        private static void LoadMods()
        {
            var config = CoreConfig.Instance;
            HashSet<string> enabledIds = GetEnabledModIds(config.CorePath);

            foreach (var modInfo in _mods)
            {
                // Only load mods that are still in Discovered state
                if (modInfo.State != ModState.Discovered) continue;

                // Per-instance activation filter
                if (enabledIds != null && !enabledIds.Contains(modInfo.Manifest.Id))
                {
                    modInfo.State = ModState.Disabled;
                    _log.Info($"[PluginLoader] Mod disabled by instance filter: {modInfo.Manifest.Id}");
                    continue;
                }

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

                // Create config from ModConfig subclass in assembly (if any)
                var logger = LogManager.GetLogger(manifest.Id);
                var coreConfig = CoreConfig.Instance;
                var modConfig = ConfigManager.LoadForMod(manifest.Id, assembly, coreConfig.ConfigsPath, manifest.FolderPath, logger);

                // Create context
                var context = new ModContext(logger, manifest.FolderPath, manifest, modConfig);

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
        /// Returns true if a mod with the given ID is currently loaded.
        /// Used by ModContext.IsModLoaded() for cross-mod capability checks.
        /// </summary>
        public static bool IsModLoaded(string modId)
        {
            if (string.IsNullOrEmpty(modId)) return false;
            return _mods.Any(m => m.State == ModState.Loaded &&
                string.Equals(m.Manifest?.Id, modId, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Call OnWorldLoad on all loaded mods.
        /// </summary>
        public static void NotifyWorldLoad()
        {
            foreach (var modInfo in _mods.Where(m => m.State == ModState.Loaded))
            {
                if (modInfo.Instance is IModLifecycle lc)
                    SafeCall(modInfo, () => lc.OnWorldLoad(), "OnWorldLoad");
            }

            // Show one-time notification if any mods failed to load
            int errorCount = _mods.Count(m => m.State == ModState.Errored);
            if (errorCount > 0)
            {
                try { Terraria.Main.NewText($"[TerrariaModder] {errorCount} mod(s) failed to load \u2014 press F6 for details.", 255, 80, 80); } catch { }
            }
        }

        /// <summary>
        /// Call OnWorldUnload on all loaded mods.
        /// </summary>
        public static void NotifyWorldUnload()
        {
            // I6: Clear KnownUnknown type IDs on world exit — they are per-server session
            Assets.ItemRegistry.ClearKnownUnknowns();

            foreach (var modInfo in _mods.Where(m => m.State == ModState.Loaded))
            {
                if (modInfo.Instance is IModLifecycle lc)
                    SafeCall(modInfo, () => lc.OnWorldUnload(), "OnWorldUnload");
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

                    // Config is no longer disposable (no FileSystemWatcher)
                }
            }

            _mods.Clear();
            _modsById.Clear();
            _initialized = false;

            // Stop management API if running
            try { _managementApi?.Dispose(); _managementApi = null; } catch { }

            _log?.Info("All mods unloaded");
        }

        // ---- Lifecycle hooks (called by injector via reflection) ----

        private static Server.ServerManagementApi _managementApi;

        /// <summary>
        /// Called by injector when Main.Initialize() completes.
        /// Main.instance, GraphicsDevice, and Window.Handle are ready.
        /// </summary>
        public static void OnGameReady()
        {
            _log?.Info("Lifecycle: OnGameReady — applying deferred patches");

            // Start HTTP management API for dedicated server (if key configured)
            // Skip if already started during LoadPlugins()
            if (IsDedicatedServer)
            {
                if (_managementApi != null)
                {
                    _log?.Info("[ServerManagementApi] Already running — skipping duplicate start");
                    return;
                }

                var srvConfig = Server.ServerConfig.Instance;
                if (srvConfig != null && !string.IsNullOrEmpty(srvConfig.ManagementApiKey))
                {
                    try
                    {
                        _managementApi = new Server.ServerManagementApi(_log, srvConfig);
                        _managementApi.Start();
                    }
                    catch (Exception ex)
                    {
                        _log?.Error($"[ServerManagementApi] Failed to start: {ex.Message}");
                    }
                }
                else
                {
                    _log?.Info("[ServerManagementApi] Not started — ManagementApiKey not set in server-config.json");
                }
                return;
            }

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
                FrameEvents.OnUIOverlay += DrawTitleScreenOverlay;
                FrameEvents.OnPostUpdate += AssetSystem.OnUpdate;
            }
            catch (Exception ex)
            {
                _log?.Error($"[Lifecycle] EventPatches/AssetSystem failed: {ex.Message}");
            }

            // Dispatch OnContentReady — runtime type IDs are assigned, cross-mod lookups are valid.
            DispatchOnContentReady();

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
        /// Dispatch IModLifecycle.OnContentReady to all loaded mods that implement it.
        /// Called from Initialize() for dedicated server and from OnGameReady() for client.
        /// </summary>
        private static void DispatchOnContentReady()
        {
            foreach (var modInfo in _mods.Where(m => m.State == ModState.Loaded))
            {
                if (modInfo.Instance is IModLifecycle lc)
                    SafeCall(modInfo, () => lc.OnContentReady(modInfo.Context), "OnContentReady");
            }
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
            // On dedicated server, skip "help", "mods", and "config" — ServerConsole registers its own
            // versions that write to Console.WriteLine instead of CommandRegistry.Write.
            if (!IsDedicatedServer)
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

                    CommandRegistry.Write($"{modId} configuration v{config.Version} ({config.FilePath}):");
                    foreach (var meta in config.GetPropertyMetadata())
                    {
                        object value = meta.GetValue(config);
                        string scope = meta.Scope == Config.ConfigScope.Server ? "[Server]" : "[Client]";
                        CommandRegistry.Write($"  {scope} {meta.Key} = {value}");
                    }
                });
            }

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

        // Optional warning display: show in-world chat once after connecting
        private static bool _optionalWarningShown = false;

        /// <summary>
        /// Draw title screen overlay showing framework version and warnings.
        /// Also handles the mod mismatch block screen and optional warning display.
        /// </summary>
        private static void DrawTitleScreenOverlay()
        {
            try
            {
                // Only show on title screen (gameMenu = true)
                var gameMenuField = typeof(Main).GetField("gameMenu", BindingFlags.Public | BindingFlags.Static);
                if (gameMenuField == null) return;
                bool gameMenu = (bool)gameMenuField.GetValue(null);

                // Show optional warning in chat once when we first enter a world
                if (!gameMenu && !_optionalWarningShown && ModListMismatch.OptionalWarning != null)
                {
                    _optionalWarningShown = true;
                    try
                    {
                        var newText = typeof(Main).GetMethod("NewText",
                            BindingFlags.Public | BindingFlags.Static, null,
                            new[] { typeof(string), typeof(byte), typeof(byte), typeof(byte) }, null);
                        newText?.Invoke(null, new object[] { ModListMismatch.OptionalWarning, (byte)255, (byte)165, (byte)0 });
                    }
                    catch { }
                }

                // Reset optional warning shown flag when returning to menu
                if (gameMenu && _optionalWarningShown)
                {
                    _optionalWarningShown = false;
                }

                if (!gameMenu) return;

                // Show mod mismatch block overlay when client was disconnected due to missing required mods
                if (ModListMismatch.IsBlocked)
                {
                    DrawModMismatchOverlay();
                    return; // Don't draw the normal version panel on top
                }

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
                int errorCount = _mods.Count(m => m.State == ModState.Errored || m.State == ModState.DependencyError);
                string statusText = errorCount > 0
                    ? $"TerrariaModder v{FrameworkVersion} - {loadedCount} mods loaded ({errorCount} error)"
                    : $"TerrariaModder v{FrameworkVersion} - {loadedCount} mods loaded";
                UIRenderer.DrawText(statusText, x + padding, headerY, errorCount > 0 ? UIColors.Error : UIColors.Info);
                UIRenderer.DrawTextSmall("Press F6 for Mod Menu", x + padding, headerY + 18, UIColors.TextDim);

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

        /// <summary>
        /// Draw the mod mismatch error overlay on the title screen.
        /// Shown after a client is disconnected due to missing required mods.
        /// </summary>
        private static void DrawModMismatchOverlay()
        {
            const int x = 12;
            const int y = 34;
            const int panelWidth = 440;
            const int padding = 12;

            string reason = ModListMismatch.BlockedReason ?? "Missing required mod(s)";

            // Wrap reason text at ~50 chars per line
            var lines = WrapText(reason, 50);
            int panelHeight = 80 + lines.Count * 20 + 44;

            UIRenderer.DrawRect(x, y, panelWidth, panelHeight, UIColors.PanelBg);
            UIRenderer.DrawRectOutline(x, y, panelWidth, panelHeight, UIColors.Error, 2);

            int textY = y + padding;
            UIRenderer.DrawText("Connection blocked — mod mismatch", x + padding, textY, UIColors.Error);
            textY += 24;

            foreach (string line in lines)
            {
                UIRenderer.DrawText(line, x + padding, textY, UIColors.TextDim);
                textY += 20;
            }

            textY += 8;
            UIRenderer.DrawText("Get mods at nexusmods.com/terraria/mods/159", x + padding, textY, UIColors.TextHint);
            textY += 22;

            // "Get Mods" button
            int btnWidth = 110;
            int btnHeight = 28;
            bool btnHover = UIRenderer.IsMouseOver(x + padding, textY, btnWidth, btnHeight);
            UIRenderer.DrawRect(x + padding, textY, btnWidth, btnHeight, btnHover ? UIColors.ButtonHover : UIColors.Button);
            UIRenderer.DrawRectOutline(x + padding, textY, btnWidth, btnHeight, UIColors.Error, 1);
            UIRenderer.DrawText("Get Mods", x + padding + 12, textY + 6, UIColors.Text);

            if (btnHover && UIRenderer.MouseLeftClick)
            {
                ModListMismatch.Clear();
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "https://www.nexusmods.com/terraria/mods/159",
                        UseShellExecute = true
                    });
                }
                catch { }
            }

            // "Dismiss" button
            int dismissX = x + padding + btnWidth + 8;
            bool dismissHover = UIRenderer.IsMouseOver(dismissX, textY, btnWidth, btnHeight);
            UIRenderer.DrawRect(dismissX, textY, btnWidth, btnHeight, dismissHover ? UIColors.ButtonHover : UIColors.Button);
            UIRenderer.DrawRectOutline(dismissX, textY, btnWidth, btnHeight, UIColors.Border, 1);
            UIRenderer.DrawText("Dismiss", dismissX + 18, textY + 6, UIColors.Text);

            if (dismissHover && UIRenderer.MouseLeftClick)
                ModListMismatch.Clear();
        }

        private static List<string> WrapText(string text, int maxChars)
        {
            var result = new List<string>();
            if (string.IsNullOrEmpty(text)) return result;

            while (text.Length > maxChars)
            {
                int cut = text.LastIndexOf(' ', maxChars);
                if (cut <= 0) cut = maxChars;
                result.Add(text.Substring(0, cut));
                text = text.Substring(cut).TrimStart();
            }
            if (text.Length > 0) result.Add(text);
            return result;
        }
    }
}
