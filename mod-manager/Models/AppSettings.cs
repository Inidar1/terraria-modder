using System.Text.Json.Serialization;

namespace TerrariaModManager.Models;

public class AppSettings
{
    [JsonPropertyName("terrariaPath")]
    public string? TerrariaPath { get; set; }

    [JsonPropertyName("nexusApiKey")]
    public string? NexusApiKey { get; set; }

    [JsonPropertyName("isPremium")]
    public bool IsPremium { get; set; }

    [JsonPropertyName("nxmRegistered")]
    public bool NxmRegistered { get; set; }

    [JsonPropertyName("windowLeft")]
    public double WindowLeft { get; set; } = 100;

    [JsonPropertyName("windowTop")]
    public double WindowTop { get; set; } = 100;

    [JsonPropertyName("windowWidth")]
    public double WindowWidth { get; set; } = 1100;

    [JsonPropertyName("windowHeight")]
    public double WindowHeight { get; set; } = 700;

    [JsonPropertyName("windowMaximized")]
    public bool WindowMaximized { get; set; }

    [JsonPropertyName("clearWebViewData")]
    public bool ClearWebViewDataOnNextStart { get; set; }

    [JsonPropertyName("autoCheckForUpdates")]
    public bool AutoCheckForUpdates { get; set; } = true;
}
