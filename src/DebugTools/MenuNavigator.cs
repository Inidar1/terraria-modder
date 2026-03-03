using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Terraria;
using Terraria.IO;
using TerrariaModder.Core.Logging;

namespace DebugTools
{
    /// <summary>
    /// Programmatic menu navigation for automating the title screen → world entry flow.
    /// Uses direct state manipulation (same approach as Terraria's own QuickLoad testing class)
    /// rather than simulated mouse clicks, for maximum reliability.
    /// </summary>
    public sealed class MenuNavigator
    {
        private readonly ILogger _log;

        /// <summary>Maximum allowed timeout for any blocking operation (2 minutes).</summary>
        private const int MaxTimeoutMs = 120_000;

        /// <summary>Lock to prevent concurrent navigation operations from corrupting game state.
        /// Static so all MenuNavigator instances (console commands + HTTP API) share the same lock.</summary>
        private static readonly object _navigationLock = new object();

        public MenuNavigator(ILogger logger)
        {
            _log = logger;
        }

        /// <summary>
        /// Get current menu state including mode, available characters/worlds, and selection indices.
        /// </summary>
        public MenuState GetMenuState()
        {
            var state = new MenuState
            {
                InMenu = Main.gameMenu,
                MenuMode = Main.menuMode,
                MenuDescription = DescribeMenuMode(Main.menuMode),
                InWorld = !Main.gameMenu && Main.LocalPlayer != null
            };

            if (state.InWorld)
            {
                state.WorldName = Main.worldName ?? "";
            }

            if (state.InMenu)
            {
                try
                {
                    state.PlayerCount = Main.PlayerList?.Count ?? 0;
                    state.WorldCount = Main.WorldList?.Count ?? 0;

                    var players = new List<CharacterInfo>();
                    if (Main.PlayerList != null)
                    {
                        for (int i = 0; i < Main.PlayerList.Count; i++)
                        {
                            var pfd = Main.PlayerList[i];
                            players.Add(new CharacterInfo
                            {
                                Index = i,
                                Name = pfd.Player?.name ?? "Unknown",
                                Difficulty = pfd.Player?.difficulty ?? 0
                            });
                        }
                    }
                    state.Players = players;

                    var worlds = new List<WorldInfo>();
                    if (Main.WorldList != null)
                    {
                        for (int i = 0; i < Main.WorldList.Count; i++)
                        {
                            var wfd = Main.WorldList[i];
                            worlds.Add(new WorldInfo
                            {
                                Index = i,
                                Name = wfd.Name ?? "Unknown",
                                Seed = wfd.SeedText ?? "",
                                IsHardMode = wfd.IsHardMode,
                                GameMode = wfd.GameMode
                            });
                        }
                    }
                    state.Worlds = worlds;
                }
                catch (Exception ex)
                {
                    _log.Debug($"[MenuNavigator] Error reading player/world lists: {ex.Message}");
                }
            }

            return state;
        }

        /// <summary>
        /// Navigate to a specific menu target.
        /// </summary>
        public NavigationResult Navigate(string target)
        {
            if (!Main.gameMenu)
                return NavigationResult.Fail("Not in menu - already in game");

            // Prevent concurrent navigation operations from corrupting game state
            if (!Monitor.TryEnter(_navigationLock))
                return NavigationResult.Fail("Another navigation operation is already in progress");

            try
            {
                switch (target.ToLowerInvariant())
                {
                    case "singleplayer":
                        return NavigateToSingleplayer();
                    case "back":
                    case "title":
                        return NavigateBack();
                    default:
                        if (target.StartsWith("character_", StringComparison.OrdinalIgnoreCase))
                        {
                            if (int.TryParse(target.Substring(10), out int charIdx))
                                return SelectCharacter(charIdx);
                            return NavigationResult.Fail($"Invalid character index in: {target}");
                        }
                        if (target.StartsWith("world_", StringComparison.OrdinalIgnoreCase))
                        {
                            if (int.TryParse(target.Substring(6), out int worldIdx))
                                return SelectWorld(worldIdx);
                            return NavigationResult.Fail($"Invalid world index in: {target}");
                        }
                        if (target == "play")
                            return PlaySelectedWorld();

                        return NavigationResult.Fail($"Unknown navigation target: {target}. Use: singleplayer, character_N, world_N, play, back, title");
                }
            }
            finally
            {
                Monitor.Exit(_navigationLock);
            }
        }

        /// <summary>
        /// Full sequence: enter a world from any state. Handles current state detection.
        /// </summary>
        public NavigationResult EnterWorld(int characterIndex = 0, int worldIndex = 0, int timeoutMs = 30000)
        {
            // Clamp timeout to prevent indefinite blocking
            if (timeoutMs <= 0) timeoutMs = 30000;
            if (timeoutMs > MaxTimeoutMs) timeoutMs = MaxTimeoutMs;

            // Already in world?
            if (!Main.gameMenu)
            {
                string currentWorld = Main.worldName ?? "";
                return NavigationResult.Ok($"Already in world: {currentWorld}");
            }

            // Prevent concurrent navigation operations from corrupting game state
            if (!Monitor.TryEnter(_navigationLock))
                return NavigationResult.Fail("Another navigation operation is already in progress");

            try
            {
                return EnterWorldInternal(characterIndex, worldIndex, timeoutMs);
            }
            finally
            {
                Monitor.Exit(_navigationLock);
            }
        }

        private NavigationResult EnterWorldInternal(int characterIndex, int worldIndex, int timeoutMs)
        {
            _log.Info($"[MenuNavigator] EnterWorld: character={characterIndex}, world={worldIndex}, timeout={timeoutMs}ms");

            // Step 1: Load players and select character
            try
            {
                Main.LoadPlayers();
            }
            catch (Exception ex)
            {
                return NavigationResult.Fail($"Failed to load players: {ex.Message}");
            }

            if (Main.PlayerList == null || Main.PlayerList.Count == 0)
                return NavigationResult.Fail("No characters available");

            if (characterIndex < 0 || characterIndex >= Main.PlayerList.Count)
                return NavigationResult.Fail($"Character index {characterIndex} out of range (0-{Main.PlayerList.Count - 1})");

            var playerData = Main.PlayerList[characterIndex];
            string characterName = playerData.Player?.name ?? "Unknown";

            // Validate player loaded successfully - SelectPlayer throws if loadStatus != Ok
            if (playerData.Player == null)
                return NavigationResult.Fail($"Character at index {characterIndex} has no player data");

            int loadStatus = playerData.Player.loadStatus;
            if (loadStatus != 0) // StatusID.Ok == 0
                return NavigationResult.Fail($"Character '{characterName}' failed to load (loadStatus={loadStatus}). File may be corrupt or from a newer version.");

            _log.Info($"[MenuNavigator] Selecting character: {characterName} (index {characterIndex})");

            // Ensure singleplayer path - SelectPlayer checks this flag
            Main.menuMultiplayer = false;

            try
            {
                Main.SelectPlayer(playerData);
            }
            catch (Exception ex)
            {
                return NavigationResult.Fail($"Failed to select character: {ex.Message}");
            }

            // SelectPlayer calls LoadWorlds() synchronously and sets menuMode = 6 (world select)
            // Verify we're in the expected state
            int postSelectMode = Main.menuMode;
            if (postSelectMode != 6)
            {
                _log.Warn($"[MenuNavigator] Unexpected menuMode after SelectPlayer: {postSelectMode} ({DescribeMenuMode(postSelectMode)}), expected 6 (world_select)");
                return NavigationResult.Fail($"Character selection ended in unexpected state: menuMode={postSelectMode} ({DescribeMenuMode(postSelectMode)}). Expected world_select (6).");
            }

            // Step 2: Select and enter world
            if (Main.WorldList == null || Main.WorldList.Count == 0)
                return NavigationResult.Fail("No worlds available");

            if (worldIndex < 0 || worldIndex >= Main.WorldList.Count)
                return NavigationResult.Fail($"World index {worldIndex} out of range (0-{Main.WorldList.Count - 1})");

            var worldData = Main.WorldList[worldIndex];
            string worldName = worldData.Name ?? "Unknown";
            _log.Info($"[MenuNavigator] Selecting world: {worldName} (index {worldIndex})");

            try
            {
                worldData.SetAsActive();
            }
            catch (Exception ex)
            {
                return NavigationResult.Fail($"Failed to set world active: {ex.Message}");
            }

            // Step 3: Start world loading (same as Terraria's QuickLoad)
            try
            {
                WorldGen.playWorld();
                Main.menuMode = 10; // loading screen
            }
            catch (Exception ex)
            {
                return NavigationResult.Fail($"Failed to start world loading: {ex.Message}");
            }

            _log.Info($"[MenuNavigator] World loading started, waiting up to {timeoutMs}ms...");

            // Step 4: Wait for world to load
            // playWorld() queues work on ThreadPool. On success, sets gameMenu=false.
            // On failure, sets menuMode to 200 (load failed, backup available) or 201 (load failed, no backup).
            var sw = Stopwatch.StartNew();
            int lastLoggedMode = 10;
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                if (!Main.gameMenu && Main.LocalPlayer != null)
                {
                    _log.Info($"[MenuNavigator] Successfully entered world: {worldName} ({sw.ElapsedMilliseconds}ms)");
                    return NavigationResult.Ok($"Entered world: {worldName}", worldName);
                }

                // Detect world load failure - background thread sets these on corrupt/missing world files
                int currentMode = Main.menuMode;
                if (currentMode == 200)
                {
                    _log.Error($"[MenuNavigator] World load failed for '{worldName}' - backup file available");
                    return NavigationResult.Fail($"World '{worldName}' failed to load (corrupt). A backup file exists.");
                }
                if (currentMode == 201)
                {
                    _log.Error($"[MenuNavigator] World load failed for '{worldName}' - no backup available");
                    return NavigationResult.Fail($"World '{worldName}' failed to load (corrupt). No backup file available.");
                }

                // Log unexpected menuMode transitions during loading
                if (currentMode != lastLoggedMode && currentMode != 10)
                {
                    _log.Warn($"[MenuNavigator] Unexpected menuMode during world load: {currentMode} ({DescribeMenuMode(currentMode)})");
                    lastLoggedMode = currentMode;
                }

                Thread.Sleep(250);
            }

            return NavigationResult.Fail($"Timeout waiting for world to load after {timeoutMs}ms (menuMode={Main.menuMode}, gameMenu={Main.gameMenu})");
        }

        /// <summary>
        /// Wait for a condition to become true.
        /// </summary>
        public NavigationResult WaitForState(string condition, int timeoutMs = 15000)
        {
            // Clamp timeout to prevent indefinite blocking
            if (timeoutMs <= 0) timeoutMs = 15000;
            if (timeoutMs > MaxTimeoutMs) timeoutMs = MaxTimeoutMs;

            // Validate condition before starting the wait loop
            string normalizedCondition = condition.ToLowerInvariant();
            if (normalizedCondition != "in_world" && normalizedCondition != "in_menu" &&
                normalizedCondition != "title_screen" && normalizedCondition != "loading")
            {
                return NavigationResult.Fail($"Unknown condition: {condition}. Use: in_world, in_menu, title_screen, loading");
            }

            var sw = Stopwatch.StartNew();

            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                bool met = false;
                switch (normalizedCondition)
                {
                    case "in_world":
                        met = !Main.gameMenu && Main.LocalPlayer != null;
                        break;
                    case "in_menu":
                        met = Main.gameMenu;
                        break;
                    case "title_screen":
                        met = Main.gameMenu && Main.menuMode == 0;
                        break;
                    case "loading":
                        met = Main.gameMenu && Main.menuMode == 10;
                        break;
                }

                if (met)
                    return NavigationResult.Ok($"Condition met: {condition}");

                Thread.Sleep(200);
            }

            return NavigationResult.Fail($"Timeout waiting for {condition} after {timeoutMs}ms (menuMode={Main.menuMode}, gameMenu={Main.gameMenu})");
        }

        #region Navigation Helpers

        private NavigationResult NavigateToSingleplayer()
        {
            if (Main.menuMode != 0 && Main.menuMode != 888)
                return NavigationResult.Fail($"Cannot navigate to singleplayer from menuMode {Main.menuMode} ({DescribeMenuMode(Main.menuMode)})");

            try
            {
                Main.LoadPlayers();
                Main.menuMode = 1;
                return NavigationResult.Ok("Navigated to singleplayer/character select");
            }
            catch (Exception ex)
            {
                return NavigationResult.Fail($"Failed to navigate to singleplayer: {ex.Message}");
            }
        }

        private NavigationResult NavigateBack()
        {
            Main.menuMode = 0;
            Main.menuMultiplayer = false;
            return NavigationResult.Ok("Returned to title screen");
        }

        private NavigationResult SelectCharacter(int index)
        {
            if (Main.PlayerList == null || Main.PlayerList.Count == 0)
            {
                try
                {
                    Main.LoadPlayers();
                }
                catch (Exception ex)
                {
                    return NavigationResult.Fail($"Failed to load players: {ex.Message}");
                }
                if (Main.PlayerList == null || Main.PlayerList.Count == 0)
                    return NavigationResult.Fail("No characters available");
            }

            if (index < 0 || index >= Main.PlayerList.Count)
                return NavigationResult.Fail($"Character index {index} out of range (0-{Main.PlayerList.Count - 1})");

            var playerData = Main.PlayerList[index];

            // Validate player loaded successfully - SelectPlayer throws if loadStatus != Ok
            if (playerData.Player == null)
                return NavigationResult.Fail($"Character at index {index} has no player data");

            int loadStatus = playerData.Player.loadStatus;
            if (loadStatus != 0) // StatusID.Ok == 0
                return NavigationResult.Fail($"Character '{playerData.Player.name ?? "Unknown"}' failed to load (loadStatus={loadStatus})");

            // Ensure singleplayer path
            Main.menuMultiplayer = false;

            try
            {
                Main.SelectPlayer(playerData);
                string name = playerData.Player?.name ?? "Unknown";
                return NavigationResult.Ok($"Selected character: {name} (index {index})");
            }
            catch (Exception ex)
            {
                return NavigationResult.Fail($"Failed to select character: {ex.Message}");
            }
        }

        private NavigationResult SelectWorld(int index)
        {
            if (Main.WorldList == null || Main.WorldList.Count == 0)
                return NavigationResult.Fail("No worlds available. Select a character first.");

            if (index < 0 || index >= Main.WorldList.Count)
                return NavigationResult.Fail($"World index {index} out of range (0-{Main.WorldList.Count - 1})");

            try
            {
                Main.WorldList[index].SetAsActive();
                string name = Main.WorldList[index].Name ?? "Unknown";
                return NavigationResult.Ok($"Selected world: {name} (index {index})");
            }
            catch (Exception ex)
            {
                return NavigationResult.Fail($"Failed to select world: {ex.Message}");
            }
        }

        private NavigationResult PlaySelectedWorld()
        {
            if (Main.ActiveWorldFileData == null)
                return NavigationResult.Fail("No world selected. Select a world first.");

            // Validate we're on the world select screen
            int currentMode = Main.menuMode;
            if (currentMode != 6)
            {
                _log.Warn($"[MenuNavigator] PlaySelectedWorld called from menuMode {currentMode} ({DescribeMenuMode(currentMode)}), expected 6 (world_select)");
                return NavigationResult.Fail($"Cannot play world from menuMode {currentMode} ({DescribeMenuMode(currentMode)}). Navigate to world select (menuMode 6) first.");
            }

            try
            {
                WorldGen.playWorld();
                Main.menuMode = 10;
                return NavigationResult.Ok($"Loading world: {Main.ActiveWorldFileData.Name}");
            }
            catch (Exception ex)
            {
                return NavigationResult.Fail($"Failed to play world: {ex.Message}");
            }
        }

        #endregion

        private static string DescribeMenuMode(int mode)
        {
            switch (mode)
            {
                case 0: return "title_screen";
                case 1: return "character_select";
                case 2: return "new_character";
                case 3: return "character_name";
                case 5: return "character_deletion_confirm";
                case 6: return "world_select";
                case 7: return "world_name";
                case 10: return "loading";
                case 11: return "settings";
                case 12: return "multiplayer";
                case 13: return "server_ip";
                case 14: return "multiplayer_connecting";
                case 15: return "disconnected";
                case 16: return "world_size_select";
                case 200: return "world_load_failed_backup_available";
                case 201: return "world_load_failed_no_backup";
                case 888: return "fancy_ui";
                default: return "unknown_" + mode;
            }
        }

        #region Data Types

        public class MenuState
        {
            public bool InMenu;
            public bool InWorld;
            public int MenuMode;
            public string MenuDescription;
            public string WorldName;
            public int PlayerCount;
            public int WorldCount;
            public List<CharacterInfo> Players;
            public List<WorldInfo> Worlds;
        }

        public class CharacterInfo
        {
            public int Index;
            public string Name;
            public int Difficulty;
        }

        public class WorldInfo
        {
            public int Index;
            public string Name;
            public string Seed;
            public bool IsHardMode;
            public int GameMode;
        }

        public class NavigationResult
        {
            public bool Success;
            public string Message;
            public string WorldName;

            public static NavigationResult Ok(string message, string worldName = null)
                => new NavigationResult { Success = true, Message = message, WorldName = worldName };

            public static NavigationResult Fail(string message)
                => new NavigationResult { Success = false, Message = message };
        }

        #endregion
    }
}
