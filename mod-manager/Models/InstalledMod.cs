using Avalonia.Media;

namespace TerrariaModManager.Models;

public class InstalledMod
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public string Author { get; set; } = "";
    public string Description { get; set; } = "";
    public string EntryDll { get; set; } = "";
    public string FolderPath { get; set; } = "";
    public bool IsEnabled { get; set; } = true;
    public bool IsCore { get; set; }
    public ModManifest? Manifest { get; set; }

    // Settings detection
    public bool HasConfigFiles { get; set; }

    // Update tracking
    public int NexusModId { get; set; }
    public bool HasUpdate { get; set; }
    public string LatestVersion { get; set; } = "";
    public int LatestFileId { get; set; }

    // Section grouping
    public bool IsFirstDisabled { get; set; }

    // Computed for Avalonia UI (replaces WPF DataTrigger)
    private static readonly IBrush CoreBrush = new SolidColorBrush(Color.Parse("#FF6CB4EE"));
    private static readonly IBrush UpdateBrush = new SolidColorBrush(Color.Parse("#FFE8B93C"));
    private static readonly IBrush EnabledBrush = new SolidColorBrush(Color.Parse("#FF58EB1C"));
    private static readonly IBrush DisabledBrush = new SolidColorBrush(Color.Parse("#FF7C828D"));
    private static readonly IBrush DefaultTextBrush = new SolidColorBrush(Color.Parse("#FFE2E4E8"));

    public string StatusText => IsCore ? "Core"
        : HasUpdate ? "Update Available"
        : IsEnabled ? "Enabled" : "Disabled";
    public IBrush StatusColor => IsCore ? CoreBrush
        : HasUpdate ? UpdateBrush
        : IsEnabled ? EnabledBrush : DisabledBrush;
    public IBrush VersionColor => IsCore ? CoreBrush
        : HasUpdate ? UpdateBrush : EnabledBrush;
    public IBrush NameColor => IsCore ? CoreBrush
        : HasUpdate ? UpdateBrush : DefaultTextBrush;
}
