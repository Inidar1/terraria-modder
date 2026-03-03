using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using TerrariaModder.Core.Logging;

namespace DebugTools
{
    /// <summary>
    /// High-level gameplay action API that maps named actions to Terraria's trigger system.
    /// Uses trigger names directly since we inject at the TriggersSet.KeyStatus level.
    /// </summary>
    public static class VirtualInputActions
    {
        private static ILogger _log;

        // Action name -> trigger name mapping
        private static readonly Dictionary<string, string> _actionMap =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                // Movement
                { "move_left", "Left" },
                { "move_right", "Right" },
                { "move_up", "Up" },
                { "move_down", "Down" },
                { "jump", "Jump" },

                // Combat
                { "attack", "MouseLeft" },
                { "use_item", "MouseLeft" },
                { "interact", "MouseRight" },

                // Inventory
                { "open_inventory", "Inventory" },
                { "throw", "Throw" },

                // Equipment
                { "grapple", "Grapple" },
                { "quick_mount", "QuickMount" },
                { "smart_cursor", "SmartCursor" },
                { "smart_select", "SmartSelect" },

                // Consumables
                { "quick_heal", "QuickHeal" },
                { "quick_mana", "QuickMana" },
                { "quick_buff", "QuickBuff" },

                // Hotbar
                { "hotbar_1", "Hotbar1" },
                { "hotbar_2", "Hotbar2" },
                { "hotbar_3", "Hotbar3" },
                { "hotbar_4", "Hotbar4" },
                { "hotbar_5", "Hotbar5" },
                { "hotbar_6", "Hotbar6" },
                { "hotbar_7", "Hotbar7" },
                { "hotbar_8", "Hotbar8" },
                { "hotbar_9", "Hotbar9" },
                { "hotbar_10", "Hotbar10" },

                // Map
                { "map_full", "MapFull" },
                { "map_style", "MapStyle" },
                { "map_zoom_in", "MapZoomIn" },
                { "map_zoom_out", "MapZoomOut" },

                // View
                { "view_zoom_in", "ViewZoomIn" },
                { "view_zoom_out", "ViewZoomOut" },

                // Loadouts
                { "loadout_1", "Loadout1" },
                { "loadout_2", "Loadout2" },
                { "loadout_3", "Loadout3" },

                // Lock-on
                { "lock_on", "LockOn" },
            };

        // Actions currently held (ConcurrentDictionary for thread safety - accessed from HTTP threads)
        // Tracks action name -> trigger name for started (indefinite) actions
        private static readonly ConcurrentDictionary<string, string> _activeActions =
            new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Reference count per trigger: how many actions are using it
        // Prevents one StopAction from clearing a trigger that another action still needs
        private static readonly ConcurrentDictionary<string, int> _triggerRefCount =
            new ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        public static void Initialize(ILogger logger)
        {
            _log = logger;
        }

        /// <summary>
        /// Execute a named action for a duration. The action maps to a trigger name
        /// which is set directly on TriggersSet.KeyStatus.
        /// Timed actions are managed entirely by VirtualInputManager and don't use refcounting.
        /// </summary>
        public static bool ExecuteAction(string actionName, int durationMs = 100)
        {
            if (!_actionMap.TryGetValue(actionName, out string triggerName))
            {
                _log?.Warn($"[VirtualInput] Unknown action: {actionName}");
                return false;
            }

            VirtualInputManager.HoldTrigger(triggerName, durationMs);
            return true;
        }

        /// <summary>
        /// Start an action (held until StopAction is called).
        /// Uses reference counting so multiple actions sharing a trigger don't conflict.
        /// </summary>
        public static bool StartAction(string actionName)
        {
            if (!_actionMap.TryGetValue(actionName, out string triggerName))
            {
                _log?.Warn($"[VirtualInput] Unknown action: {actionName}");
                return false;
            }

            VirtualInputManager.SetTrigger(triggerName);
            _activeActions[actionName] = triggerName;
            _triggerRefCount.AddOrUpdate(triggerName, 1, (_, count) => count + 1);
            return true;
        }

        /// <summary>
        /// Stop a running action. Only clears the underlying trigger when no other
        /// actions are still using it (reference counted).
        /// </summary>
        public static bool StopAction(string actionName)
        {
            if (!_activeActions.TryRemove(actionName, out string triggerName))
            {
                // Not actively held - try the action map to clear trigger directly
                if (!_actionMap.TryGetValue(actionName, out triggerName))
                    return false;
                VirtualInputManager.ClearTrigger(triggerName);
                return true;
            }

            // Decrement ref count; only clear trigger when no other actions use it
            int newCount = _triggerRefCount.AddOrUpdate(triggerName, 0, (_, count) => Math.Max(0, count - 1));
            if (newCount <= 0)
            {
                _triggerRefCount.TryRemove(triggerName, out _);
                VirtualInputManager.ClearTrigger(triggerName);
            }
            return true;
        }

        /// <summary>
        /// Clear all active actions and ref counts. Called when VirtualInputManager.ReleaseAll() fires.
        /// </summary>
        public static void ReleaseAll()
        {
            _activeActions.Clear();
            _triggerRefCount.Clear();
        }

        /// <summary>
        /// Get all available action names.
        /// </summary>
        public static IEnumerable<string> GetAvailableActions()
        {
            return _actionMap.Keys;
        }

        /// <summary>
        /// Check if an action name is valid.
        /// </summary>
        public static bool IsValidAction(string actionName)
        {
            return _actionMap.ContainsKey(actionName);
        }

        /// <summary>
        /// Get the trigger name for an action.
        /// </summary>
        public static string GetTriggerName(string actionName)
        {
            _actionMap.TryGetValue(actionName, out string triggerName);
            return triggerName;
        }
    }
}
