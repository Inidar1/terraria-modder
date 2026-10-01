using System.Text.Json.Serialization;

namespace TerrariaModManager.Models;

public class AppSettings
{
    [JsonPropertyName("terrariaPath")]
    public string? TerrariaPath { get; set; }

    /// <summary>
    /// Protected Nexus API key (DPAPI on Windows; stored in the user-private settings file elsewhere).
    /// Use SettingsService.GetApiKey() / SetApiKey() to read/write the plaintext value.
    /// </summary>
    [JsonPropertyName("nexusApiKeyEncrypted")]
    public string? NexusApiKeyEncrypted { get; set; }

    /// <summary>Legacy plaintext key — migrated to encrypted on first load.</summary>
    [JsonPropertyName("nexusApiKey")]
    public string? NexusApiKeyLegacy { get; set; }

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

    /// <summary>Tracks last app version to detect updates (triggers free-user re-login).</summary>
    [JsonPropertyName("lastRunVersion")]
    public string? LastRunVersion { get; set; }

    /// <summary>Whether the user has authenticated via browser this session/version.</summary>
    [JsonPropertyName("browserAuthenticated")]
    public bool BrowserAuthenticated { get; set; }
}
