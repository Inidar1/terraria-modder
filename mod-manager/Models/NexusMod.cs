using System.ComponentModel;
using System.Text.Json.Serialization;

namespace TerrariaModManager.Models;

public class NexusMod : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    [JsonPropertyName("mod_id")]
    public int ModId { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("summary")]
    public string Summary { get; set; } = "";

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("version")]
    public string Version { get; set; } = "";

    [JsonPropertyName("author")]
    public string Author { get; set; } = "";

    [JsonPropertyName("picture_url")]
    public string? PictureUrl { get; set; }

    [JsonPropertyName("mod_downloads")]
    public int Downloads { get; set; }

    [JsonPropertyName("mod_unique_downloads")]
    public int UniqueDownloads { get; set; }

    [JsonPropertyName("endorsement_count")]
    public int EndorsementCount { get; set; }

    [JsonPropertyName("updated_timestamp")]
    public long UpdatedTimestamp { get; set; }

    [JsonPropertyName("created_timestamp")]
    public long CreatedTimestamp { get; set; }

    [JsonPropertyName("available")]
    public bool Available { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }

    // Computed locally
    [JsonIgnore]
    public bool IsTerrariaModder { get; set; }

    [JsonIgnore]
    public bool HasNoImage => string.IsNullOrEmpty(PictureUrl);

    // Install state (set by BrowseViewModel)
    [JsonIgnore]
    public bool IsInstalled { get; set; }

    [JsonIgnore]
    public string? InstalledVersion { get; set; }

    [JsonIgnore]
    public bool HasNewerVersion { get; set; }

    [JsonIgnore]
    public bool IsDownloading { get; set; }

    // Selection state for multi-select install
    private bool _isSelectedForInstall;
    [JsonIgnore]
    public bool IsSelectedForInstall
    {
        get => _isSelectedForInstall;
        set
        {
            if (_isSelectedForInstall != value)
            {
                _isSelectedForInstall = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelectedForInstall)));
            }
        }
    }

    [JsonIgnore]
    public string InstallButtonText =>
        IsDownloading ? "Downloading..." :
        IsInstalled
            ? (HasNewerVersion ? "Update" : "Reinstall")
            : "Install";

    [JsonIgnore]
    public string InstalledBadgeText =>
        !IsInstalled ? ""
        : HasNewerVersion ? "Update Available"
        : $"Installed v{InstalledVersion}";

    [JsonIgnore]
    public string CardBorderColor =>
        !IsInstalled ? "Transparent"
        : HasNewerVersion ? "#FFEE8800"
        : "#FF58EB1C";

    [JsonIgnore]
    public string BadgeBackground =>
        HasNewerVersion ? "#FF3D2E00" : "#FF1E3316";

    [JsonIgnore]
    public string BadgeForeground =>
        HasNewerVersion ? "#FFEE8800" : "#FF58EB1C";
}
