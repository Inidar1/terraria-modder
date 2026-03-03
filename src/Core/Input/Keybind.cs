using System;
using TerrariaModder.Core.Logging;

namespace TerrariaModder.Core.Input
{
    /// <summary>
    /// A registered keybind with callback.
    /// </summary>
    public class Keybind
    {
        /// <summary>Unique ID (mod_id.keybind_id format).</summary>
        public string Id { get; }

        /// <summary>Display label.</summary>
        public string Label { get; }

        /// <summary>Description for tooltip.</summary>
        public string Description { get; }

        /// <summary>Mod that registered this keybind.</summary>
        public string ModId { get; }

        /// <summary>Default key combination.</summary>
        public KeyCombo DefaultKey { get; }

        /// <summary>Current key combination.</summary>
        public KeyCombo CurrentKey { get; set; }

        /// <summary>Callback when keybind is pressed.</summary>
        public Action Callback { get; }

        /// <summary>Whether this keybind is enabled.</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>Whether this keybind works on the title/menu screen.</summary>
        public bool AllowInMenu { get; set; }

        private readonly ILogger _log;

        public Keybind(string id, string modId, string label, string description, KeyCombo defaultKey, Action callback, ILogger logger = null)
        {
            Id = id;
            ModId = modId;
            Label = label;
            Description = description;
            DefaultKey = defaultKey;
            CurrentKey = defaultKey?.Clone() ?? new KeyCombo();
            Callback = callback;
            _log = logger;
        }

        /// <summary>Reset to default key.</summary>
        public void ResetToDefault()
        {
            CurrentKey = DefaultKey?.Clone() ?? new KeyCombo();
        }

        /// <summary>Check and fire callback if pressed.</summary>
        internal bool CheckAndFire()
        {
            if (!Enabled) return false;
            if (Callback == null) return false;
            if (CurrentKey == null || CurrentKey.Key == KeyCode.None) return false;

            if (CurrentKey.JustPressed())
            {
                try
                {
                    Callback();
                }
                catch (Exception ex)
                {
                    if (_log != null)
                        _log.Error($"Error in keybind callback for {Id}: {ex.Message}");
                    else
                        Console.WriteLine($"[Keybind] Error in callback for {Id}: {ex.Message}");
                }
                return true;
            }

            return false;
        }
    }
}
