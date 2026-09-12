using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace TerrariaModder.Core
{
    /// <summary>Optional client-only console visibility; never hide a shared terminal.</summary>
    internal static class ConsoleVisibility
    {
        private static IntPtr _hiddenConsole;
        public static string Status { get; private set; } = "disabled";

        [DllImport("kernel32.dll")] private static extern IntPtr GetConsoleWindow();
        [DllImport("kernel32.dll")] private static extern uint GetConsoleProcessList(uint[] processes, uint count);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);

        static ConsoleVisibility()
        {
            AppDomain.CurrentDomain.UnhandledException += (_, __) => Restore();
            AppDomain.CurrentDomain.ProcessExit += (_, __) => Restore();
        }

        private static bool OwnsConsole(IntPtr window)
        {
            if (window == IntPtr.Zero || GetConsoleWindow() != window) return false;
            var processes = new uint[2];
            return GetConsoleProcessList(processes, 2) == 1 && processes[0] == (uint)Process.GetCurrentProcess().Id;
        }

        internal static string Apply(bool hide, bool server)
        {
            if (server) return Status = "server console retained";
            if (!hide) return Status = "disabled";
            if (Environment.OSVersion.Platform != PlatformID.Win32NT) return Status = "unsupported platform";
            var window = GetConsoleWindow();
            if (window == IntPtr.Zero) return Status = "no console window";
            if (!OwnsConsole(window)) return Status = "shared console retained";
            if (!IsWindowVisible(window)) return Status = "already hidden";
            ShowWindow(window, 0);
            if (IsWindowVisible(window)) return Status = "hide failed";
            _hiddenConsole = window;
            return Status = "hidden";
        }

        internal static void Restore()
        {
            if (_hiddenConsole == IntPtr.Zero) return;
            try
            {
                // The frozen injector may display an error prompt after the game exits.
                if (OwnsConsole(_hiddenConsole)) ShowWindow(_hiddenConsole, 4); // no activation
            }
            finally { _hiddenConsole = IntPtr.Zero; }
        }
    }
}
