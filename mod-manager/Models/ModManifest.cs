using System.Text.Json.Serialization;

namespace TerrariaModManager.Models;

public class ModManifest
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("version")]
    public string Version { get; set; } = "";

    [JsonPropertyName("author")]
    public string Author { get; set; } = "";

    [JsonPropertyName("description")]
    public string Description { get; set; } = "";

    [JsonPropertyName("entry_dll")]
    public string EntryDll { get; set; } = "";

    [JsonPropertyName("framework_version")]
    public string? FrameworkVersion { get; set; }

    [JsonPropertyName("terraria_version")]
    public string? TerrariaVersion { get; set; }

    [JsonPropertyName("dependencies")]
    public List<string>? Dependencies { get; set; }

    [JsonPropertyName("optional_dependencies")]
    public List<string>? OptionalDependencies { get; set; }

    [JsonPropertyName("incompatible_with")]
    public List<string>? IncompatibleWith { get; set; }

    [JsonPropertyName("load_after")]
    public List<string>? LoadAfter { get; set; }

    [JsonPropertyName("load_before")]
    public List<string>? LoadBefore { get; set; }

    /// <summary>"required", "optional", or "client-only". Used by server for ModListExchange.</summary>
    [JsonPropertyName("multiplayer")]
    public string? Multiplayer { get; set; }

    [JsonPropertyName("keybinds")]
    public List<KeybindDefinition>? Keybinds { get; set; }

    [JsonPropertyName("homepage")]
    public string? Homepage { get; set; }

    [JsonPropertyName("icon")]
    public string? Icon { get; set; }

    [JsonPropertyName("tags")]
    public List<string>? Tags { get; set; }

    [JsonPropertyName("nexus_id")]
    public int NexusId { get; set; }
}

public class KeybindDefinition
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("label")]
    public string Label { get; set; } = "";

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("default")]
    public string? Default { get; set; }
}
