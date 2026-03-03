using System;
using System.IO;
using System.Text;
using Terraria;
using TerrariaModder.Core.Logging;

namespace StorageHub.Debug
{
    /// <summary>
    /// Captures debug state to a single session log file.
    ///
    /// Design for user bug reports:
    /// - Single file per game session (overwrites previous)
    /// - Records important events with timestamps
    /// - Captures errors/failures prominently
    /// - User can send this file when reporting issues
    ///
    /// File location: TerrariaModder/mods/storage-hub/debug/session.log
    /// </summary>
    public class DebugDumper
    {
        private readonly ILogger _log;
        private readonly string _logPath;
        private readonly StringBuilder _sessionLog = new StringBuilder();
        private DateTime _sessionStart;
        private int _errorCount = 0;
        private int _warningCount = 0;

        public DebugDumper(ILogger log, string modFolder)
        {
            _log = log;
            _sessionStart = DateTime.Now;

            // Ensure debug folder exists
            var debugFolder = Path.Combine(modFolder, "debug");
            if (!Directory.Exists(debugFolder))
                Directory.CreateDirectory(debugFolder);

            _logPath = Path.Combine(debugFolder, "session.log");

            // Start fresh session log
            _sessionLog.Clear();
            _sessionLog.AppendLine("================================================================================");
            _sessionLog.AppendLine("STORAGE HUB DEBUG SESSION");
            _sessionLog.AppendLine($"Started: {_sessionStart:yyyy-MM-dd HH:mm:ss}");
            _sessionLog.AppendLine($"Framework: TerrariaModder");
            _sessionLog.AppendLine("================================================================================");
            _sessionLog.AppendLine();

            WriteToFile();
        }

/// <summary>
        /// Log an event (no state dump, just a timestamped message).
        /// </summary>
        public void LogEvent(string message)
        {
            var timestamp = GetTimestamp();
            _sessionLog.AppendLine($"[{timestamp}] {message}");
            WriteToFile();
        }

        /// <summary>
        /// Log a world load event with config summary.
        /// </summary>
        public void DumpState(string actionLabel)
        {
            var timestamp = GetTimestamp();
            _sessionLog.AppendLine();
            _sessionLog.AppendLine($"[{timestamp}] === {actionLabel} ===");

            // Just log the event, not full inventory spam
            var cursorInfo = GetCursorItemSummary();
            if (!string.IsNullOrEmpty(cursorInfo))
            {
                _sessionLog.AppendLine($"  Cursor: {cursorInfo}");
            }

            WriteToFile();
        }

        /// <summary>
        /// Log a shimmer operation with before/after context.
        /// Only logs summary, not full inventory dumps.
        /// </summary>
        public void DumpShimmerOperation(string phase, int inputItemId, int inputStack, int outputItemId, int outputStack, int sourceChest, int sourceSlot)
        {
            var timestamp = GetTimestamp();

            if (phase == "BEFORE")
            {
                _sessionLog.AppendLine();
                _sessionLog.AppendLine($"[{timestamp}] SHIMMER: Taking {inputStack}x ID:{inputItemId} from chest {sourceChest} slot {sourceSlot}");
                _sessionLog.AppendLine($"           Expected output: {outputStack}x ID:{outputItemId}");
            }
            else if (phase == "AFTER")
            {
                _sessionLog.AppendLine($"[{timestamp}] SHIMMER: Complete");
            }

            WriteToFile();
        }

        /// <summary>
        /// Log a state dump with chest context (for errors).
        /// </summary>
        public void DumpStateWithChest(string actionLabel, int chestIndex)
        {
            var timestamp = GetTimestamp();

            bool isError = actionLabel.Contains("FAILED");
            if (isError) _errorCount++;

            _sessionLog.AppendLine();
            _sessionLog.AppendLine($"[{timestamp}] {(isError ? "ERROR" : "EVENT")}: {actionLabel}");

            // Only dump detailed state on errors
            if (isError)
            {
                _sessionLog.AppendLine($"  Chest {chestIndex}: {GetChestSummary(chestIndex)}");

                var cursorInfo = GetCursorItemSummary();
                if (!string.IsNullOrEmpty(cursorInfo))
                {
                    _sessionLog.AppendLine($"  Cursor: {cursorInfo}");
                }
            }

            WriteToFile();
        }

        /// <summary>
        /// Log an error with full context for debugging.
        /// </summary>
        public void LogError(string operation, string details, Exception ex = null)
        {
            _errorCount++;
            var timestamp = GetTimestamp();

            _sessionLog.AppendLine();
            _sessionLog.AppendLine($"[{timestamp}] *** ERROR in {operation} ***");
            _sessionLog.AppendLine($"  Details: {details}");

            if (ex != null)
            {
                _sessionLog.AppendLine($"  Exception: {ex.Message}");
            }

            // Capture cursor state on error
            var cursorInfo = GetCursorItemSummary();
            if (!string.IsNullOrEmpty(cursorInfo))
            {
                _sessionLog.AppendLine($"  Cursor: {cursorInfo}");
            }

            WriteToFile();
        }

        /// <summary>
        /// Write session summary (call on world unload).
        /// </summary>
        public void WriteSessionSummary()
        {
            var duration = DateTime.Now - _sessionStart;

            _sessionLog.AppendLine();
            _sessionLog.AppendLine("================================================================================");
            _sessionLog.AppendLine("SESSION SUMMARY");
            _sessionLog.AppendLine($"Duration: {duration.TotalMinutes:F1} minutes");
            _sessionLog.AppendLine($"Errors: {_errorCount}");
            _sessionLog.AppendLine($"Warnings: {_warningCount}");
            _sessionLog.AppendLine("================================================================================");

            WriteToFile();
        }

        private string GetTimestamp()
        {
            var elapsed = DateTime.Now - _sessionStart;
            return $"{elapsed.TotalMinutes:00}:{elapsed.Seconds:00}";
        }

        private string GetCursorItemSummary()
        {
            try
            {
                var mouseItem = Main.mouseItem;
                if (mouseItem == null || mouseItem.type == 0) return null;
                return $"{mouseItem.stack}x {mouseItem.Name} (ID:{mouseItem.type})";
            }
            catch
            {
                return null;
            }
        }

        private string GetChestSummary(int chestIndex)
        {
            try
            {
                var chests = Main.chest;
                if (chests == null || chestIndex < 0 || chestIndex >= chests.Length)
                    return "(invalid)";

                var chest = chests[chestIndex];
                if (chest == null) return "(null)";

                int itemCount = 0;
                if (chest.item != null)
                {
                    foreach (var item in chest.item)
                    {
                        if (item != null && item.type > 0) itemCount++;
                    }
                }

                return $"pos=({chest.x},{chest.y}), items={itemCount}/40";
            }
            catch
            {
                return "(error reading)";
            }
        }

        private void WriteToFile()
        {
            try
            {
                File.WriteAllText(_logPath, _sessionLog.ToString());
            }
            catch
            {
                // Silent fail - don't break gameplay for logging
            }
        }
    }
}
