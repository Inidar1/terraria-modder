using System;
using TerrariaModder.Core;
using TerrariaModder.Core.Debug;
using TerrariaModder.Core.Logging;
using TerrariaModder.Core.Net;
using TerrariaModder.Core.Reflection;

namespace DebugTools
{
    public class Mod : IMod, IModLifecycle
    {
        public string Id => "debug-tools";
        public string Name => "Debug Tools";
        public string Version => "2.0.0";

        private static Mod _instance;
        private ILogger _log;
        private DebugHttpServer _httpServer;
        private ConsoleUI _console;
        private bool _httpEnabled;
        private bool _serverMode;

        public void Initialize(ModContext context)
        {
            _log = context.Logger;
            _instance = this;

            var config = context.GetConfig<DebugToolsConfig>();
            if (config != null && !config.Enabled)
            {
                _log.Info("Debug Tools disabled in config");
                return;
            }

            if (Environment.GetEnvironmentVariable("TERRARIA_MODDER_DEDSERV") == "1")
            {
                _serverMode = true;
                RegisterNetCommands();
                _log.Info("Debug Tools: dedicated server mode — UI and HTTP skipped");
                return;
            }

            _httpEnabled = config != null ? config.HttpServer : true;
            int httpPort = config != null ? config.HttpPort : 7878;
            bool startHidden = config != null ? config.StartHidden : false;

            // Initialize main-thread dispatcher (captures thread ID for IsMainThread check)
            MainThreadDispatcher.Initialize();

            // Initialize window manager (grabs console handle, hides if startHidden)
            WindowManager.Initialize(_log, startHidden);

            // Initialize virtual input subsystems
            VirtualInputManager.Initialize(_log);
            VirtualInputActions.Initialize(_log);
            InputLogger.Initialize(_log);
            VirtualInputPatches.Initialize(_log);

            // Initialize console UI
            _console = new ConsoleUI();
            _console.Initialize(context);

            // Register menu navigation commands (backward-compatible names)
            RegisterMenuCommands();
            RegisterNetCommands();

            // Start HTTP server
            if (_httpEnabled)
            {
                try
                {
                    _httpServer = new DebugHttpServer(_log, httpPort);
                    _httpServer.Start();
                }
                catch (Exception ex)
                {
                    _log.Error($"[DebugHttpServer] Failed to initialize: {ex.Message}");
                }
            }
            else
            {
                _log.Info("[DebugHttpServer] Disabled via config");
            }

            _log.Info("Debug Tools initialized");
        }

        /// <summary>
        /// Called by injector lifecycle scan when Main.Initialize() completes.
        /// </summary>
        public static void OnGameReady()
        {
            var inst = _instance;
            if (inst == null || inst._serverMode) return;

            try
            {
                VirtualInputPatches.ApplyPatches();
            }
            catch (Exception ex)
            {
                inst._log?.Error($"[DebugTools] VirtualInputPatches failed: {ex.Message}");
            }

            try
            {
                WindowManager.AcquireGameWindowHandle();
            }
            catch (Exception ex)
            {
                inst._log?.Error($"[DebugTools] Window handle acquisition failed: {ex.Message}");
            }
        }

        public void OnContentReady(ModContext context) { }

        public void OnWorldLoad() { }

        public void OnWorldUnload()
        {
            if (_serverMode) return;

            try
            {
                VirtualInputManager.ReleaseAll();
            }
            catch (Exception ex)
            {
                _log?.Error($"[VirtualInput] Error releasing input on world unload: {ex.Message}");
            }

            _console?.Close();
        }

        public void Unload()
        {
            if (_serverMode)
            {
                _instance = null;
                _log?.Info("Debug Tools unloaded (server mode)");
                return;
            }

            // Release virtual input
            try
            {
                VirtualInputManager.ReleaseAll();
                VirtualInputPatches.Cleanup();
            }
            catch (Exception ex)
            {
                _log?.Error($"[VirtualInput] Error during shutdown: {ex.Message}");
            }

            // Stop HTTP server
            try
            {
                _httpServer?.Dispose();
                _httpServer = null;
            }
            catch (Exception ex)
            {
                _log?.Error($"[DebugHttpServer] Error during shutdown: {ex.Message}");
            }

            // Restore windows if hidden
            WindowManager.RestoreIfHidden();

            // Clean up console
            _console?.Cleanup();
            _console = null;

            _instance = null;
            _log?.Info("Debug Tools unloaded");
        }

        private void RegisterMenuCommands()
        {
            var menuNav = new MenuNavigator(_log);

            CommandRegistry.Register("menu.state", "Show current menu state, available characters and worlds", args =>
            {
                var state = menuNav.GetMenuState();
                if (state.InWorld)
                {
                    CommandRegistry.Write($"In world: {state.WorldName}");
                    return;
                }
                if (!state.InMenu)
                {
                    CommandRegistry.Write("Unknown state (not in menu or world)");
                    return;
                }
                CommandRegistry.Write($"Menu: {state.MenuDescription} (mode {state.MenuMode})");
                if (state.Players != null && state.Players.Count > 0)
                {
                    CommandRegistry.Write($"Characters ({state.PlayerCount}):");
                    foreach (var p in state.Players)
                        CommandRegistry.Write($"  [{p.Index}] {p.Name}");
                }
                if (state.Worlds != null && state.Worlds.Count > 0)
                {
                    CommandRegistry.Write($"Worlds ({state.WorldCount}):");
                    foreach (var w in state.Worlds)
                        CommandRegistry.Write($"  [{w.Index}] {w.Name}");
                }
            });

            CommandRegistry.Register("menu.select", "Navigate to a menu target. Usage: menu.select <target> (singleplayer|character_N|world_N|play|back)", args =>
            {
                if (args.Length == 0)
                {
                    CommandRegistry.Write("Usage: menu.select <target>");
                    CommandRegistry.Write("Targets: singleplayer, character_N, world_N, play, back, title");
                    return;
                }
                var result = menuNav.Navigate(args[0]);
                CommandRegistry.Write(result.Success ? $"OK: {result.Message}" : $"FAIL: {result.Message}");
            });

            CommandRegistry.Register("menu.back", "Go back to title screen (Escape equivalent)", args =>
            {
                var result = menuNav.Navigate("back");
                CommandRegistry.Write(result.Success ? $"OK: {result.Message}" : $"FAIL: {result.Message}");
            });

            CommandRegistry.Register("menu.enter", "Enter a world. Usage: menu.enter [character] [world]", args =>
            {
                int charIdx = args.Length > 0 && int.TryParse(args[0], out int c) ? c : 0;
                int worldIdx = args.Length > 1 && int.TryParse(args[1], out int w) ? w : 0;
                CommandRegistry.Write($"Entering world: character={charIdx}, world={worldIdx}...");
                var result = menuNav.EnterWorld(charIdx, worldIdx);
                CommandRegistry.Write(result.Success ? $"OK: {result.Message}" : $"FAIL: {result.Message}");
            });
        }

        private void RegisterNetCommands()
        {
            CommandRegistry.Register("net-ping", "M1 round-trip probe. Server: net-ping [clientIndex]. Client: net-ping", args =>
            {
                try
                {
                    bool isDedServ = Environment.GetEnvironmentVariable("TERRARIA_MODDER_DEDSERV") == "1";
                    bool isMultiplayer = isDedServ || Game.IsMultiplayer;

                    if (!isMultiplayer)
                    {
                        CommandRegistry.Write("Not in multiplayer (netMode=0)");
                        return;
                    }

                    if (isDedServ || Game.IsServer)
                    {
                        int target = 0;
                        if (args.Length > 0 && int.TryParse(args[0], out int t))
                            target = t;
                        CommandRegistry.Write($"[Server] Sending ping to client {target}...");
                        NetSync.SendPingToClient(target);
                    }
                    else
                    {
                        CommandRegistry.Write("[Client] Sending ping to server...");
                        NetSync.SendPingToServer();
                    }
                }
                catch (Exception ex)
                {
                    CommandRegistry.Write($"[net-ping] Error: {ex.Message}");
                }
            });
        }
    }
}
