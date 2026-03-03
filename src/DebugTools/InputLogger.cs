using System;
using System.Collections.Generic;
using System.Text;
using Terraria;
using TerrariaModder.Core.Logging;
using TerrariaModder.Core.Reflection;

namespace DebugTools
{
    /// <summary>
    /// Logs real mouse click events with screen coordinates.
    /// Toggled on/off via HTTP API. Off by default.
    /// Polled each frame from the input patch postfix (game thread).
    /// </summary>
    public static class InputLogger
    {
        private static ILogger _log;
        private static volatile bool _enabled;
        private static readonly object _lock = new object();
        private static readonly List<ClickEntry> _entries = new List<ClickEntry>();
        private const int MaxEntries = 200;

        // Previous frame mouse state for edge detection
        private static bool _prevMouseLeft;
        private static bool _prevMouseRight;

        public static bool Enabled
        {
            get => _enabled;
            set => _enabled = value;
        }

        public static void Initialize(ILogger logger)
        {
            _log = logger;
        }

        /// <summary>
        /// Called each frame from the input postfix BEFORE virtual input injection,
        /// so we capture real hardware mouse state.
        /// </summary>
        public static void PollFrame()
        {
            if (!_enabled) return;

            try
            {
                bool left = Game.MouseLeft;
                bool right = Game.MouseRight;
                int x = Game.MouseX;
                int y = Game.MouseY;

                // Detect leading edge (press start)
                if (left && !_prevMouseLeft)
                    RecordClick(x, y, "left");

                if (right && !_prevMouseRight)
                    RecordClick(x, y, "right");

                _prevMouseLeft = left;
                _prevMouseRight = right;
            }
            catch
            {
                // Non-critical, game state may not be ready
            }
        }

        private static void RecordClick(int x, int y, string button)
        {
            bool inWorld = Game.InWorld;
            int menuMode = -1;
            if (!inWorld)
            {
                try { menuMode = Main.menuMode; }
                catch { }
            }

            var entry = new ClickEntry
            {
                Timestamp = DateTime.UtcNow,
                X = x,
                Y = y,
                Button = button,
                InWorld = inWorld,
                MenuMode = menuMode
            };

            lock (_lock)
            {
                if (_entries.Count >= MaxEntries)
                    _entries.RemoveAt(0);
                _entries.Add(entry);
            }

            _log?.Info($"[InputLog] {button} click at ({x}, {y})" +
                       (inWorld ? " [in-world]" : $" [menu={menuMode}]"));
        }

        public static List<ClickEntry> GetEntries()
        {
            lock (_lock)
            {
                return new List<ClickEntry>(_entries);
            }
        }

        public static void Clear()
        {
            lock (_lock)
            {
                _entries.Clear();
            }
        }

        public struct ClickEntry
        {
            public DateTime Timestamp;
            public int X;
            public int Y;
            public string Button;
            public bool InWorld;
            public int MenuMode;
        }
    }
}
