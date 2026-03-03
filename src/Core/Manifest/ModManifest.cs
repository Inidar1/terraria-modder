using System.Collections.Generic;

namespace TerrariaModder.Core.Manifest
{
    /// <summary>
    /// Represents the parsed mod.json manifest.
    /// </summary>
    public class ModManifest
    {
        // Required fields
        public string Id { get; set; }
        public string Name { get; set; }
        public string Version { get; set; }
        public string Author { get; set; }
        public string Description { get; set; }

        // Optional version constraints
        public string TerrariaVersion { get; set; }
        public string FrameworkVersion { get; set; }

        // Optional fields
        public string EntryDll { get; set; }
        public List<string> Dependencies { get; set; } = new List<string>();
        public List<string> OptionalDependencies { get; set; } = new List<string>();
        public List<string> IncompatibleWith { get; set; } = new List<string>();
        public List<string> LoadAfter { get; set; } = new List<string>();
        public List<string> LoadBefore { get; set; } = new List<string>();
        public string ConfigSchemaJson { get; set; }
        public List<KeybindDefinition> Keybinds { get; set; } = new List<KeybindDefinition>();
        public string Homepage { get; set; }
        public string Icon { get; set; }
        public List<string> Tags { get; set; } = new List<string>();

        // Runtime properties (set by loader, not from JSON)
        public string FolderPath { get; set; }
        public string DllPath { get; set; }
        public bool IsValid { get; set; }
        public List<string> ValidationErrors { get; set; } = new List<string>();
    }

    /// <summary>
    /// Keybind definition from manifest.
    /// </summary>
    public class KeybindDefinition
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public string DefaultKey { get; set; }
    }
}
