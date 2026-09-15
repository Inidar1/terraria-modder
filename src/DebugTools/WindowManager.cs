using System;
using System.Runtime.InteropServices;
using Terraria;
using TerrariaModder.Core.Logging;

namespace DebugTools
{
    /// <summary>
    /// Manages game and console window visibility via P/Invoke.
    /// Extracted from the RunHidden mod.
    /// </summary>
    internal static class WindowManager
    {
        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetConsoleWindow();

        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        private const uint WM_KEYDOWN = 0x0100;
        private const uint WM_KEYUP   = 0x0101;
        private const uint WM_CHAR    = 0x0102;

        private const int SW_HIDE = 0;
        private const int SW_SHOW = 5;

        private static ILogger _log;
        private static volatile IntPtr _gameWindowHandle;
        private static volatile IntPtr _consoleWindowHandle;
        private static volatile bool _isHidden;
        private static bool _startHidden;
        private static bool _startupVisibilityPending;

        public static bool IsHidden => _gameWindowHandle != IntPtr.Zero ? !IsWindowVisible(_gameWindowHandle) : _isHidden;

        public static void Initialize(ILogger log, bool startHidden)
        {
            _log = log;
            _startHidden = startHidden;
            _startupVisibilityPending = startHidden;

            // Grab console window handle immediately (always available)
            _consoleWindowHandle = GetConsoleWindow();
            if (_consoleWindowHandle != IntPtr.Zero)
                _log.Debug("[WindowManager] Console window handle acquired");

            // If startHidden, hide console immediately
            if (_startHidden && _consoleWindowHandle != IntPtr.Zero)
            {
                ShowWindow(_consoleWindowHandle, SW_HIDE);
                _log.Info("[WindowManager] Console hidden (startHidden=true)");
            }
        }

        /// <summary>
        /// Called from OnGameReady lifecycle hook when Main.Initialize() completes.
        /// Game window handle is available at this point.
        /// </summary>
        public static void AcquireGameWindowHandle()
        {
            if (_gameWindowHandle != IntPtr.Zero) return;

            var handle = FindGameWindowHandle();
            if (handle != IntPtr.Zero)
            {
                _gameWindowHandle = handle;
                _log.Info("[WindowManager] Game window handle acquired");

                if (_startHidden)
                {
                    ShowWindow(_gameWindowHandle, SW_HIDE);
                    _isHidden = true;
                    _log.Info("[WindowManager] Game window hidden (startHidden=true)");
                }
            }
        }

        public static void Show()
        {
            _startupVisibilityPending = false;
            if (_gameWindowHandle != IntPtr.Zero)
            {
                ShowWindow(_gameWindowHandle, SW_SHOW);
                SetForegroundWindow(_gameWindowHandle);
            }

            if (_consoleWindowHandle != IntPtr.Zero)
                ShowWindow(_consoleWindowHandle, SW_SHOW);

            _isHidden = false;
            _log?.Info("[WindowManager] Windows shown");
        }

        public static void Hide()
        {
            if (_gameWindowHandle != IntPtr.Zero)
                ShowWindow(_gameWindowHandle, SW_HIDE);

            if (_consoleWindowHandle != IntPtr.Zero)
                ShowWindow(_consoleWindowHandle, SW_HIDE);

            _isHidden = true;
            _log?.Info("[WindowManager] Windows hidden");
        }

        internal static void ApplyStartupVisibilityAfterDraw()
        {
            if (!_startupVisibilityPending) return;
            AcquireGameWindowHandle();
            if (_gameWindowHandle == IntPtr.Zero) return;
            // The first actual draw follows XNA's startup window show, including in menus.
            Hide();
            _startupVisibilityPending = false;
        }

        /// <summary>
        /// Restore windows if hidden (called during Unload).
        /// </summary>
        public static void RestoreIfHidden()
        {
            if (_isHidden)
            {
                try
                {
                    if (_gameWindowHandle != IntPtr.Zero)
                        ShowWindow(_gameWindowHandle, SW_SHOW);
                    if (_consoleWindowHandle != IntPtr.Zero)
                        ShowWindow(_consoleWindowHandle, SW_SHOW);
                }
                catch { }
            }
        }

        /// <summary>
        /// Post a raw Enter key press to the game window via the Windows message queue.
        /// Works in WritingText mode where trigger injection does not.
        /// </summary>
        public static bool PostEnterKey()
        {
            var hwnd = _gameWindowHandle != IntPtr.Zero ? _gameWindowHandle : FindGameWindowHandle();
            if (hwnd == IntPtr.Zero) return false;

            const int VK_RETURN = 0x0D;
            const int SCAN_RETURN = 0x1C;
            IntPtr vk = new IntPtr(VK_RETURN);
            IntPtr lParamDown = new IntPtr((SCAN_RETURN << 16) | 1);
            IntPtr lParamUp   = new IntPtr((1 << 31) | (1 << 30) | (SCAN_RETURN << 16) | 1);

            PostMessage(hwnd, WM_KEYDOWN, vk, lParamDown);
            PostMessage(hwnd, WM_CHAR,    vk, lParamDown);
            PostMessage(hwnd, WM_KEYUP,   vk, lParamUp);
            return true;
        }

        private static IntPtr FindGameWindowHandle()
        {
            try
            {
                if (Main.instance == null) return IntPtr.Zero;

                var window = Main.instance.Window;
                if (window == null) return IntPtr.Zero;

                return window.Handle;
            }
            catch { }

            return IntPtr.Zero;
        }
    }
}
