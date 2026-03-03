using Microsoft.Xna.Framework.Input;
using Terraria;
using TerrariaModder.Core.Reflection;

namespace TerrariaModder.Core.Input
{
    /// <summary>
    /// Tracks keyboard and mouse state per frame.
    /// Call Update() once per frame.
    /// </summary>
    public static class InputState
    {
        private static KeyboardState _previousKeyState;
        private static KeyboardState _currentKeyState;
        private static MouseState _previousMouseState;
        private static MouseState _currentMouseState;
        private static bool _initialized;
        private static bool _firstFrame = true;
        private static int _previousScrollWheel;
        private static int _currentScrollWheel;

        /// <summary>
        /// Update input state. Called each frame by the framework.
        /// </summary>
        public static void Update()
        {
            // Skip on dedicated server - XNA input APIs don't exist
            if (Game.IsServer) return;

            try
            {
                // Keyboard
                _previousKeyState = _currentKeyState;
                _currentKeyState = Keyboard.GetState();

                // Mouse
                _previousMouseState = _currentMouseState;
                _currentMouseState = Mouse.GetState();
                _previousScrollWheel = _currentScrollWheel;
                _currentScrollWheel = _currentMouseState.ScrollWheelValue;

                // First frame - set previous = current to avoid false positives
                if (_firstFrame)
                {
                    _previousKeyState = _currentKeyState;
                    _previousMouseState = _currentMouseState;
                    _previousScrollWheel = _currentScrollWheel;
                    _firstFrame = false;
                }

                _initialized = true;
            }
            catch
            {
                // Silently fail
            }
        }

        /// <summary>Check if key is currently down.</summary>
        public static bool IsKeyDown(int keyCode)
        {
            if (Game.IsServer) return false;
            if (keyCode == KeyCode.None) return false;

            // Mouse buttons
            if (keyCode >= KeyCode.MouseLeft)
                return IsMouseButtonDown(keyCode);

            if (!_initialized) return false;

            try
            {
                return _currentKeyState.IsKeyDown((Keys)keyCode);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Check if key was just pressed this frame.</summary>
        public static bool IsKeyJustPressed(int keyCode)
        {
            if (Game.IsServer) return false;
            if (keyCode == KeyCode.None) return false;

            // Mouse buttons
            if (keyCode >= KeyCode.MouseLeft)
                return IsMouseButtonJustPressed(keyCode);

            if (!_initialized) return false;

            try
            {
                return _currentKeyState.IsKeyDown((Keys)keyCode) && _previousKeyState.IsKeyUp((Keys)keyCode);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Check if key was just released this frame.</summary>
        public static bool IsKeyJustReleased(int keyCode)
        {
            if (Game.IsServer) return false;
            if (keyCode == KeyCode.None) return false;

            // Mouse buttons
            if (keyCode >= KeyCode.MouseLeft)
                return IsMouseButtonJustReleased(keyCode);

            if (!_initialized) return false;

            try
            {
                return _currentKeyState.IsKeyUp((Keys)keyCode) && _previousKeyState.IsKeyDown((Keys)keyCode);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Check if Ctrl is held.</summary>
        public static bool IsCtrlDown() =>
            IsKeyDown(KeyCode.LeftControl) || IsKeyDown(KeyCode.RightControl);

        /// <summary>Check if Shift is held.</summary>
        public static bool IsShiftDown() =>
            IsKeyDown(KeyCode.LeftShift) || IsKeyDown(KeyCode.RightShift);

        /// <summary>Check if Alt is held.</summary>
        public static bool IsAltDown() =>
            IsKeyDown(KeyCode.LeftAlt) || IsKeyDown(KeyCode.RightAlt);

        /// <summary>Get scroll wheel delta since last frame.</summary>
        public static int ScrollWheelDelta => _currentScrollWheel - _previousScrollWheel;

        private static bool IsMouseButtonDown(int button)
        {
            if (!_initialized) return false;

            switch (button)
            {
                case KeyCode.MouseLeft: return _currentMouseState.LeftButton == ButtonState.Pressed;
                case KeyCode.MouseRight: return _currentMouseState.RightButton == ButtonState.Pressed;
                case KeyCode.MouseMiddle: return _currentMouseState.MiddleButton == ButtonState.Pressed;
                default: return false;
            }
        }

        private static bool IsMouseButtonJustPressed(int button)
        {
            if (!_initialized) return false;

            switch (button)
            {
                case KeyCode.MouseLeft:
                    return _currentMouseState.LeftButton == ButtonState.Pressed && _previousMouseState.LeftButton == ButtonState.Released;
                case KeyCode.MouseRight:
                    return _currentMouseState.RightButton == ButtonState.Pressed && _previousMouseState.RightButton == ButtonState.Released;
                case KeyCode.MouseMiddle:
                    return _currentMouseState.MiddleButton == ButtonState.Pressed && _previousMouseState.MiddleButton == ButtonState.Released;
                default: return false;
            }
        }

        private static bool IsMouseButtonJustReleased(int button)
        {
            if (!_initialized) return false;

            switch (button)
            {
                case KeyCode.MouseLeft:
                    return _currentMouseState.LeftButton == ButtonState.Released && _previousMouseState.LeftButton == ButtonState.Pressed;
                case KeyCode.MouseRight:
                    return _currentMouseState.RightButton == ButtonState.Released && _previousMouseState.RightButton == ButtonState.Pressed;
                case KeyCode.MouseMiddle:
                    return _currentMouseState.MiddleButton == ButtonState.Released && _previousMouseState.MiddleButton == ButtonState.Pressed;
                default: return false;
            }
        }

        /// <summary>Check if input should be blocked (chat open, menu, etc).</summary>
        public static bool ShouldBlockInput()
        {
            return Main.drawingPlayerChat || Main.editSign || Main.editChest || Main.gameMenu;
        }
    }
}
