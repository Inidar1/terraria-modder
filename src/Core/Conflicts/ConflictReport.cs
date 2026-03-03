using System.Collections.Generic;
using TerrariaModder.Core.Input;

namespace TerrariaModder.Core.Conflicts
{
    public enum ConflictSeverity
    {
        Low,
        Medium,
        High
    }

    /// <summary>
    /// A Harmony patch owner attributed to a specific mod or core.
    /// </summary>
    public class PatchOwner
    {
        public string ModId { get; set; }
        public string ModName { get; set; }
        public string HarmonyOwner { get; set; }
        public string PatchType { get; set; }
        public bool ReturnsBool { get; set; }
    }

    /// <summary>
    /// A conflict where two or more mods patch the same Terraria method.
    /// </summary>
    public class PatchConflict
    {
        public string TargetType { get; set; }
        public string TargetMethod { get; set; }
        public ConflictSeverity Severity { get; set; }
        public List<PatchOwner> Patches { get; set; } = new List<PatchOwner>();
    }

    /// <summary>
    /// Aggregates all detected conflicts across keybinds, patches, and load order.
    /// </summary>
    public class ConflictReport
    {
        public List<PatchConflict> PatchConflicts { get; set; } = new List<PatchConflict>();
        public List<KeybindConflict> KeybindConflicts { get; set; } = new List<KeybindConflict>();
        public List<string> LoadOrder { get; set; } = new List<string>();

        public int HighCount { get; set; }
        public int MediumCount { get; set; }
        public int LowCount { get; set; }

        /// <summary>
        /// Get the number of conflicts (patch + keybind) involving a specific mod.
        /// </summary>
        public int GetConflictCountForMod(string modId)
        {
            int count = 0;

            foreach (var pc in PatchConflicts)
            {
                bool involvesMod = false;
                foreach (var p in pc.Patches)
                {
                    if (p.ModId == modId) { involvesMod = true; break; }
                }
                if (involvesMod) count++;
            }

            foreach (var kc in KeybindConflicts)
            {
                if (kc.Keybind1.ModId == modId || kc.Keybind2.ModId == modId)
                    count++;
            }

            return count;
        }
    }
}
