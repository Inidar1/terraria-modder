using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Microsoft.Xna.Framework.Input;
using Terraria.GameInput;
using TerrariaModder.Core.Logging;

namespace DebugTools
{
    /// <summary>
    /// Harmony patches for injecting virtual keyboard and mouse input into Terraria's input pipeline.
    ///
    /// Strategy: Postfix on PlayerInput.UpdateInput() to inject virtual trigger states
    /// after all real input processing is complete. This merges virtual with real input
    /// (never suppresses real input) and bypasses WritingText blocking.
    ///
    /// Input flow:
    ///   Keyboard.GetState() / Mouse.GetState()
    ///     -> PlayerInput.UpdateInput()
    ///       -> MouseInput() / KeyboardInput()
    ///         -> Triggers.Current (TriggersSet.KeyStatus)
    ///           -> [OUR POSTFIX INJECTS HERE]
    ///             -> Player controls consumed by game
    ///
    /// Note: Raw keyboard injection (Main.keyState) is set in the postfix but gets
    /// overwritten by Keyboard.GetState() in DoUpdate_HandleInput before the next frame.
    /// For reliable input, use trigger-based actions (/api/input/action) rather than
    /// raw key presses (/api/input/key). Triggers like "Inventory" cover Escape's function.
    /// </summary>
    internal static class VirtualInputPatches
    {
        private static Harmony _harmony;
        private static ILogger _log;
        private static bool _patchesApplied;

        // Cached reflection for TriggersSet.KeyStatus
        private static FieldInfo _triggersField;   // PlayerInput.Triggers field
        private static FieldInfo _currentField;    // TriggersPack.Current field
        private static FieldInfo _keyStatusField;  // TriggersSet.KeyStatus field

        // Cached reflection for mouse position
        private static FieldInfo _mouseXField;     // PlayerInput.MouseX
        private static FieldInfo _mouseYField;     // PlayerInput.MouseY

        // Cached reflection for Main.keyState (for raw keyboard state patching)
        private static FieldInfo _keyStateField;   // Main.keyState
        private static Type _keysType;             // XNA Keys enum type
        private static ConstructorInfo _keyboardStateCtor; // KeyboardState(params Keys[])

        // Cached reflection for Main mouse fields (avoid per-frame GetField)
        private static FieldInfo _mainMouseXField;
        private static FieldInfo _mainMouseYField;
        private static FieldInfo _mainMouseLeftField;
        private static FieldInfo _mainMouseRightField;
        private static FieldInfo _mainMouseLeftReleaseField;
        private static FieldInfo _mainMouseRightReleaseField;

        public static void Initialize(ILogger logger)
        {
            _log = logger;
            _harmony = new Harmony("com.terrariamodder.virtualinput");
        }

        internal static void ApplyPatches()
        {
            if (_patchesApplied) return;

            var playerInputType = typeof(PlayerInput);
            if (playerInputType == null)
            {
                _log?.Error("[VirtualInput] PlayerInput type not found");
                return;
            }

            // Cache reflection for trigger injection
            if (!CacheReflection(playerInputType))
            {
                _log?.Error("[VirtualInput] Failed to cache reflection types");
                return;
            }

            // Patch PlayerInput.UpdateInput() with a postfix for trigger injection
            var updateInput = playerInputType.GetMethod("UpdateInput",
                BindingFlags.Public | BindingFlags.Static);

            if (updateInput == null)
            {
                _log?.Error("[VirtualInput] PlayerInput.UpdateInput not found");
                return;
            }

            var postfix = typeof(VirtualInputPatches).GetMethod(nameof(UpdateInput_Postfix),
                BindingFlags.NonPublic | BindingFlags.Static);

            _harmony.Patch(updateInput, postfix: new HarmonyMethod(postfix));
            _patchesApplied = true;
            _log?.Info("[VirtualInput] Input patch applied (postfix on PlayerInput.UpdateInput)");
        }

        private static bool CacheReflection(Type playerInputType)
        {
            try
            {
                // PlayerInput.Triggers (TriggersPack)
                _triggersField = playerInputType.GetField("Triggers",
                    BindingFlags.Public | BindingFlags.Static);
                if (_triggersField == null)
                {
                    _log?.Warn("[VirtualInput] PlayerInput.Triggers field not found");
                    return false;
                }

                var triggersPackType = _triggersField.FieldType;

                // TriggersPack.Current (TriggersSet)
                _currentField = triggersPackType.GetField("Current",
                    BindingFlags.Public | BindingFlags.Instance);
                if (_currentField == null)
                {
                    _log?.Warn("[VirtualInput] TriggersPack.Current field not found");
                    return false;
                }

                var triggersSetType = _currentField.FieldType;

                // TriggersSet.KeyStatus (Dictionary<string, bool>)
                _keyStatusField = triggersSetType.GetField("KeyStatus",
                    BindingFlags.Public | BindingFlags.Instance);
                if (_keyStatusField == null)
                {
                    _log?.Warn("[VirtualInput] TriggersSet.KeyStatus field not found");
                    return false;
                }

                // PlayerInput.MouseX and MouseY
                _mouseXField = playerInputType.GetField("MouseX",
                    BindingFlags.Public | BindingFlags.Static);
                _mouseYField = playerInputType.GetField("MouseY",
                    BindingFlags.Public | BindingFlags.Static);

                // Main.keyState for raw keyboard state
                _keyStateField = typeof(Terraria.Main).GetField("keyState",
                    BindingFlags.Public | BindingFlags.Static);

                // XNA Keys type and KeyboardState constructor
                _keysType = typeof(Keys);
                if (_keysType != null && _keyStateField != null)
                {
                    var keyboardStateType = _keyStateField.FieldType;
                    _keyboardStateCtor = keyboardStateType.GetConstructor(
                        new[] { _keysType.MakeArrayType() });
                }

                // Cache Main mouse fields (avoids per-frame GetField in InjectMouse)
                var mainType = typeof(Terraria.Main);
                var pubStatic = BindingFlags.Public | BindingFlags.Static;
                _mainMouseXField = mainType.GetField("mouseX", pubStatic);
                _mainMouseYField = mainType.GetField("mouseY", pubStatic);
                _mainMouseLeftField = mainType.GetField("mouseLeft", pubStatic);
                _mainMouseRightField = mainType.GetField("mouseRight", pubStatic);
                _mainMouseLeftReleaseField = mainType.GetField("mouseLeftRelease", pubStatic);
                _mainMouseRightReleaseField = mainType.GetField("mouseRightRelease", pubStatic);

                _log?.Info("[VirtualInput] Reflection cached successfully");
                return true;
            }
            catch (Exception ex)
            {
                _log?.Error($"[VirtualInput] Reflection cache failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Postfix on PlayerInput.UpdateInput() - injects virtual input after real input is processed.
        /// Runs on the game thread.
        /// </summary>
        private static int _errorCount;
        private static int _lastErrorLogTick;

        // Track previous virtual mouse button state for release gate edge detection
        private static bool _prevVirtualLeft;
        private static bool _prevVirtualRight;

        private static void UpdateInput_Postfix()
        {
            try
            {
                // Poll real mouse state for input logging BEFORE virtual injection
                InputLogger.PollFrame();

                // Process timed releases
                VirtualInputManager.Update();

                // Skip if no virtual input is active
                if (!VirtualInputManager.HasActiveInput) return;

                // Inject trigger states
                InjectTriggers();

                // Inject mouse state
                InjectMouse();

                // Inject raw keyboard state (for Main.keyState consumers)
                InjectKeyboardState();

                // Reset error count on successful frame
                _errorCount = 0;
            }
            catch (Exception ex)
            {
                _errorCount++;
                // Log errors with rate limiting: first error, then every 60 seconds
                int now = Environment.TickCount;
                if (_errorCount == 1 || unchecked(now - _lastErrorLogTick) >= 60_000)
                {
                    _log?.Error($"[VirtualInput] Postfix error (count={_errorCount}): {ex.Message}");
                    _lastErrorLogTick = now;
                }

                // If errors persist for many frames, release all input as safety measure
                if (_errorCount >= 300) // ~5 seconds at 60fps
                {
                    _log?.Error("[VirtualInput] Persistent errors detected, releasing all virtual input");
                    VirtualInputManager.ReleaseAll();
                    _errorCount = 0;
                }
            }
        }

        private static void InjectTriggers()
        {
            if (_triggersField == null || _currentField == null || _keyStatusField == null)
                return;

            var triggersPack = _triggersField.GetValue(null);
            if (triggersPack == null) return;

            var currentSet = _currentField.GetValue(triggersPack);
            if (currentSet == null) return;

            var keyStatus = _keyStatusField.GetValue(currentSet) as Dictionary<string, bool>;
            if (keyStatus == null) return;

            // Apply direct trigger overrides
            foreach (var triggerName in VirtualInputManager.GetActiveTriggers())
            {
                if (keyStatus.ContainsKey(triggerName))
                {
                    keyStatus[triggerName] = true;
                }
            }

            // Apply mouse button triggers from virtual mouse
            var (active, x, y, left, right, middle, scroll) = VirtualInputManager.GetMouseState();
            if (left && keyStatus.ContainsKey("MouseLeft"))
                keyStatus["MouseLeft"] = true;
            if (right && keyStatus.ContainsKey("MouseRight"))
                keyStatus["MouseRight"] = true;
        }

        private static void InjectMouse()
        {
            var (active, x, y, left, right, middle, scroll) = VirtualInputManager.GetMouseState();

            if (active && _mouseXField != null && _mouseYField != null)
            {
                _mouseXField.SetValue(null, x);
                _mouseYField.SetValue(null, y);

                // Also update Main.mouseX/Y so the game uses the virtual position
                _mainMouseXField?.SetValue(null, x);
                _mainMouseYField?.SetValue(null, y);
            }

            // Set Main.mouseLeft/mouseRight and release gates so inventory/UI code sees virtual clicks.
            // Terraria uses mouseLeft && mouseLeftRelease for click edge detection.
            // mouseLeftRelease = true means "button was released last frame" (ready for new click).
            try
            {
                if (left)
                {
                    _mainMouseLeftField?.SetValue(null, true);
                    // Set release gate so the click registers as a new press
                    if (!_prevVirtualLeft)
                        _mainMouseLeftReleaseField?.SetValue(null, true);
                }
                if (right)
                {
                    _mainMouseRightField?.SetValue(null, true);
                    if (!_prevVirtualRight)
                        _mainMouseRightReleaseField?.SetValue(null, true);
                }
                _prevVirtualLeft = left;
                _prevVirtualRight = right;
            }
            catch
            {
                // Non-critical
            }
        }

        private static void InjectKeyboardState()
        {
            if (_keyStateField == null || _keysType == null || _keyboardStateCtor == null)
                return;

            var virtualKeys = VirtualInputManager.GetPressedKeys();
            var keyEnums = new List<object>();

            // Collect virtual key enums
            foreach (var keyName in virtualKeys)
            {
                try
                {
                    if (Enum.IsDefined(_keysType, keyName))
                    {
                        keyEnums.Add(Enum.Parse(_keysType, keyName, true));
                    }
                }
                catch
                {
                    // Skip invalid key names
                }
            }

            if (keyEnums.Count == 0) return;

            try
            {
                // Get current real keyboard state and merge with virtual keys
                var currentState = _keyStateField.GetValue(null);
                if (currentState == null) return;

                // Get pressed keys from current state
                var getPressedKeys = currentState.GetType().GetMethod("GetPressedKeys");
                if (getPressedKeys == null) return;

                var realKeys = getPressedKeys.Invoke(currentState, null) as Array;
                var mergedSet = new HashSet<object>();

                // Add real keys
                if (realKeys != null)
                {
                    foreach (var key in realKeys)
                    {
                        mergedSet.Add(key);
                    }
                }

                // Add virtual keys
                foreach (var key in keyEnums)
                {
                    mergedSet.Add(key);
                }

                // Create array of Keys enum type
                var keysArray = Array.CreateInstance(_keysType, mergedSet.Count);
                int i = 0;
                foreach (var key in mergedSet)
                {
                    keysArray.SetValue(key, i++);
                }

                // Construct new KeyboardState with merged keys
                var newState = _keyboardStateCtor.Invoke(new object[] { keysArray });
                _keyStateField.SetValue(null, newState);
            }
            catch
            {
                // Non-critical - trigger-level injection still works
            }
        }

        /// <summary>
        /// Cleanup patches.
        /// </summary>
        public static void Cleanup()
        {
            _harmony?.UnpatchAll("com.terrariamodder.virtualinput");
            _patchesApplied = false;
        }
    }
}
