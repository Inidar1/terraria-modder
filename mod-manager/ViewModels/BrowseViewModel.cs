using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using System.Windows.Input;
using Avalonia.Threading;
using MsBox.Avalonia.Enums;
using TerrariaModManager.Helpers;
using TerrariaModManager.Models;
using TerrariaModManager.Services;

namespace TerrariaModManager.ViewModels;

public class BrowseViewModel : ViewModelBase
{
    private bool _isLoading;
    private bool _isLoadingFeed;
    private NexusMod? _selectedMod;
    private bool _hasApiKey;
    private bool _isGridLayout = true;
    private string _sortBy = "Name";
    private string _searchText = "";
    private List<NexusMod> _allMods = new();
    private readonly object _allModsLock = new();
    private Dictionary<int, InstalledMod> _installStateCache = new();
    private bool _isDetailOpen;
    private NexusMod? _detailMod;
    private string _detailDescription = "";
    private bool _isDetailLoading;
    private string _toastMessage = "";
    private bool _isToastVisible;
    private bool _hideInstalled = true;
    private CancellationTokenSource? _toastCts;
    private bool _isSelectMode;
    private int _selectedForInstallCount;
    private bool _isBulkRunning;
    private string _catalogStatusText = "";
    private CancellationTokenSource? _bulkCts;
    private readonly NexusApiService _nexusApi;
    private readonly NexusCatalogService _catalog;
    private readonly SettingsService _settings;
    private readonly ModStateService _modState;
    private readonly UpdateTracker _updateTracker;
    private readonly DownloadManager _downloadManager;
    private readonly NxmProtocolRegistrar _nxmRegistrar;
    private readonly Logger _logger;

    public ObservableCollection<NexusMod> Mods { get; } = new();

    public bool IsLoading
    {
        get => _isLoading;
        set { if (SetProperty(ref _isLoading, value)) OnPropertyChanged(nameof(ShowEmptyMessage)); }
    }

    public bool HasApiKey
    {
        get => _hasApiKey;
        set { if (SetProperty(ref _hasApiKey, value)) OnPropertyChanged(nameof(ShowEmptyMessage)); }
    }

    public NexusMod? SelectedMod
    {
        get => _selectedMod;
        set => SetProperty(ref _selectedMod, value);
    }

    public bool IsGridLayout
    {
        get => _isGridLayout;
        set
        {
            if (SetProperty(ref _isGridLayout, value))
            {
                OnPropertyChanged(nameof(IsListLayout));
                OnPropertyChanged(nameof(LayoutToggleText));
            }
        }
    }

    public bool IsListLayout => !_isGridLayout;
    public string LayoutToggleText => _isGridLayout ? "List View" : "Grid View";

    public bool HideInstalled
    {
        get => _hideInstalled;
        set
        {
            if (SetProperty(ref _hideInstalled, value))
            {
                OnPropertyChanged(nameof(HideInstalledText));
                ApplySort();
            }
        }
    }

    public string HideInstalledText => _hideInstalled ? "Show Installed" : "Hide Installed";

    public string SortBy
    {
        get => _sortBy;
        set
        {
            if (SetProperty(ref _sortBy, value))
            {
                OnPropertyChanged(nameof(IsSortName));
                OnPropertyChanged(nameof(IsSortDownloads));
                OnPropertyChanged(nameof(IsSortUpdated));
                OnPropertyChanged(nameof(IsSortEndorsements));
                ApplySort();
            }
        }
    }

    public bool IsSortName => _sortBy == "Name";
    public bool IsSortDownloads => _sortBy == "Downloads";
    public bool IsSortUpdated => _sortBy == "Updated";
    public bool IsSortEndorsements => _sortBy == "Endorsements";

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
                ApplyFilter();
        }
    }

    public ICommand RefreshCommand { get; }
    public ICommand ToggleLayoutCommand { get; }
    public ICommand ToggleHideInstalledCommand { get; }
    public ICommand SortByNameCommand { get; }
    public ICommand SortByDownloadsCommand { get; }
    public ICommand SortByUpdatedCommand { get; }
    public ICommand SortByEndorsementsCommand { get; }
    public ICommand DownloadModCommand { get; }
    public ICommand SearchCommand { get; }
    public ICommand OpenDetailCommand { get; }
    public ICommand CloseDetailCommand { get; }
    public ICommand ToggleSelectModeCommand { get; }
    public ICommand InstallSelectedCommand { get; }
    public ICommand CancelBulkCommand { get; }

    public bool IsDetailOpen
    {
        get => _isDetailOpen;
        set => SetProperty(ref _isDetailOpen, value);
    }

    public NexusMod? DetailMod
    {
        get => _detailMod;
        set => SetProperty(ref _detailMod, value);
    }

    public string DetailDescription
    {
        get => _detailDescription;
        set => SetProperty(ref _detailDescription, value);
    }

    public bool IsDetailLoading
    {
        get => _isDetailLoading;
        set => SetProperty(ref _isDetailLoading, value);
    }

    public string ToastMessage
    {
        get => _toastMessage;
        set => SetProperty(ref _toastMessage, value);
    }

    public bool IsToastVisible
    {
        get => _isToastVisible;
        set => SetProperty(ref _isToastVisible, value);
    }

    public bool ShowEmptyMessage => !IsLoading && Mods.Count == 0;

    public string CatalogStatusText
    {
        get => _catalogStatusText;
        private set
        {
            if (SetProperty(ref _catalogStatusText, value))
                OnPropertyChanged(nameof(HasCatalogStatus));
        }
    }

    public bool HasCatalogStatus => !string.IsNullOrWhiteSpace(CatalogStatusText);

    public bool IsSelectMode
    {
        get => _isSelectMode;
        set
        {
            if (SetProperty(ref _isSelectMode, value))
            {
                OnPropertyChanged(nameof(SelectModeButtonText));
                if (!value) ClearSelections();
            }
        }
    }

    public int SelectedForInstallCount
    {
        get => _selectedForInstallCount;
        set
        {
            if (SetProperty(ref _selectedForInstallCount, value))
            {
                OnPropertyChanged(nameof(HasSelectionForInstall));
                OnPropertyChanged(nameof(SelectCountText));
            }
        }
    }

    public bool HasSelectionForInstall => _selectedForInstallCount > 0;
    public string SelectCountText => _selectedForInstallCount == 1 ? "1 mod selected" : $"{_selectedForInstallCount} mods selected";
    public string SelectModeButtonText => _isSelectMode ? "Done" : "Select Multiple";

    public bool IsBulkRunning
    {
        get => _isBulkRunning;
        set => SetProperty(ref _isBulkRunning, value);
    }

    /// <summary>
    /// Set by MainWindow to open the shared NexusBrowserPanel.
    /// Parameters: (url, modName, toastMessage?) → result or null if cancelled.
    /// </summary>
    public Func<string, string, string?, Task<Models.InlineBrowserResult?>>? OpenBrowser { get; set; }

    /// <summary>Set by MainViewModel to navigate to the Settings tab.</summary>
    public ICommand? NavigateToSettingsCommand { get; set; }

    public BrowseViewModel(
        NexusApiService nexusApi,
        NexusCatalogService catalog,
        SettingsService settings,
        ModStateService modState,
        UpdateTracker updateTracker,
        DownloadManager downloadManager,
        NxmProtocolRegistrar nxmRegistrar,
        Logger logger)
    {
        _nexusApi = nexusApi;
        _catalog = catalog;
        _settings = settings;
        _modState = modState;
        _updateTracker = updateTracker;
        _downloadManager = downloadManager;
        _nxmRegistrar = nxmRegistrar;
        _logger = logger;

        DownloadModCommand = new AsyncRelayCommand<NexusMod>(DownloadMod);
        HasApiKey = _nexusApi.HasApiKey;
        Mods.CollectionChanged += (_, _) => OnPropertyChanged(nameof(ShowEmptyMessage));

        // Refresh browse cards when a download starts or completes so IsDownloading updates
        _downloadManager.Downloads.CollectionChanged += (_, _) =>
            Dispatcher.UIThread.InvokeAsync(() => ApplySort());

        RefreshCommand = new AsyncRelayCommand(LoadFeedAsync);
        ToggleLayoutCommand = new RelayCommand(() => IsGridLayout = !IsGridLayout);
        ToggleHideInstalledCommand = new RelayCommand(() => HideInstalled = !HideInstalled);
        SortByNameCommand = new RelayCommand(() => SortBy = "Name");
        SortByDownloadsCommand = new RelayCommand(() => SortBy = "Downloads");
        SortByUpdatedCommand = new RelayCommand(() => SortBy = "Updated");
        SortByEndorsementsCommand = new RelayCommand(() => SortBy = "Endorsements");
        SearchCommand = new AsyncRelayCommand(SearchMod);
        OpenDetailCommand = new RelayCommand(param =>
        {
            if (param is NexusMod mod)
                _ = OpenDetailAsync(mod);
        });
        CloseDetailCommand = new RelayCommand(() =>
        {
            IsDetailOpen = false;
            DetailMod = null;
            DetailDescription = "";
        });
        ToggleSelectModeCommand = new RelayCommand(() => IsSelectMode = !IsSelectMode);
        InstallSelectedCommand = new AsyncRelayCommand(InstallSelected);
        CancelBulkCommand = new RelayCommand(() => _bulkCts?.Cancel());
    }

    internal async Task OpenDetailAsync(NexusMod mod)
    {
        DetailMod = mod;
        IsDetailOpen = true;
        DetailDescription = "";

        // Fetch full description if not already loaded
        if (string.IsNullOrEmpty(mod.Description))
        {
            IsDetailLoading = true;
            try
            {
                var full = await _nexusApi.GetModInfoAsync(mod.ModId);
                if (full?.Description != null)
                    mod.Description = full.Description;
            }
            catch { }
            finally { IsDetailLoading = false; }
        }

        DetailDescription = BbCodeToHtml(mod.Description ?? mod.Summary);
    }

    internal static string BbCodeToHtml(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";

        var result = text;

        // Basic formatting
        result = Regex.Replace(result, @"\[b\](.*?)\[/b\]", "<b>$1</b>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        result = Regex.Replace(result, @"\[i\](.*?)\[/i\]", "<i>$1</i>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        result = Regex.Replace(result, @"\[u\](.*?)\[/u\]", "<u>$1</u>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        result = Regex.Replace(result, @"\[s\](.*?)\[/s\]", "<s>$1</s>", RegexOptions.IgnoreCase | RegexOptions.Singleline);

        // Size → heading-like
        result = Regex.Replace(result, @"\[size=([^\]]*)\](.*?)\[/size\]", "<span style=\"font-size:$1\">$2</span>", RegexOptions.IgnoreCase | RegexOptions.Singleline);

        // Color/font
        result = Regex.Replace(result, @"\[color=([^\]]*)\](.*?)\[/color\]", "<span style=\"color:$1\">$2</span>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        result = Regex.Replace(result, @"\[font=([^\]]*)\](.*?)\[/font\]", "<span style=\"font-family:$1\">$2</span>", RegexOptions.IgnoreCase | RegexOptions.Singleline);

        // Links (sanitize javascript: scheme)
        result = Regex.Replace(result, @"\[url=([^\]]*)\](.*?)\[/url\]", m =>
        {
            var href = m.Groups[1].Value;
            if (href.TrimStart().StartsWith("javascript:", StringComparison.OrdinalIgnoreCase))
                return m.Groups[2].Value;
            return $"<a href=\"{href}\">{m.Groups[2].Value}</a>";
        }, RegexOptions.IgnoreCase | RegexOptions.Singleline);
        result = Regex.Replace(result, @"\[url\](.*?)\[/url\]", m =>
        {
            var href = m.Groups[1].Value;
            if (href.TrimStart().StartsWith("javascript:", StringComparison.OrdinalIgnoreCase))
                return href;
            return $"<a href=\"{href}\">{href}</a>";
        }, RegexOptions.IgnoreCase | RegexOptions.Singleline);

        // Images — skip (too heavy for sidebar)
        result = Regex.Replace(result, @"\[img\].*?\[/img\]", "", RegexOptions.IgnoreCase | RegexOptions.Singleline);

        // Lists — process [*] items first, then wrap with correct list tags
        // Nexus emits explicit [/*] terminators, while older descriptions may
        // rely on the next item or list end to close an entry.
        result = Regex.Replace(result, @"\[/\*\]", "", RegexOptions.IgnoreCase);
        result = Regex.Replace(result, @"\[\*\](.*?)(?=\[\*\]|\[/list\]|$)", "<li>$1</li>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        result = Regex.Replace(result, @"\[list=1\](.*?)\[/list\]", "<ol>$1</ol>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        result = Regex.Replace(result, @"\[list\](.*?)\[/list\]", "<ul>$1</ul>", RegexOptions.IgnoreCase | RegexOptions.Singleline);

        // Quote
        result = Regex.Replace(result, @"\[quote[^\]]*\](.*?)\[/quote\]", "<blockquote>$1</blockquote>", RegexOptions.IgnoreCase | RegexOptions.Singleline);

        // Code
        result = Regex.Replace(result, @"\[code\](.*?)\[/code\]", "<pre>$1</pre>", RegexOptions.IgnoreCase | RegexOptions.Singleline);

        // Spoiler → just show the content
        result = Regex.Replace(result, @"\[spoiler\](.*?)\[/spoiler\]", "<i>(Spoiler)</i> $1", RegexOptions.IgnoreCase | RegexOptions.Singleline);

        // Center/align
        result = Regex.Replace(result, @"\[center\](.*?)\[/center\]", "<div style=\"text-align:center\">$1</div>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        result = Regex.Replace(result, @"\[/?(?:right|left|justify)\]", "", RegexOptions.IgnoreCase);

        // Line/hr
        result = Regex.Replace(result, @"\[line\]", "<hr/>", RegexOptions.IgnoreCase);

        // Remove any remaining BBCode tags
        result = Regex.Replace(result, @"\[/?[a-zA-Z][^\]]*\]", "");

        // Convert newlines to <br/> for HTML display
        result = result.Replace("\r\n", "\n").Replace("\n", "<br/>");

        // Wrap in a styled body for dark theme
        return $"<body style=\"font-family: Inter, Segoe UI, sans-serif; font-size: 12px; color: #9ca3af; background: transparent; margin: 0; padding: 0;\">{result}</body>";
    }

    public async Task LoadFeedAsync()
    {
        HasApiKey = _nexusApi.HasApiKey;
        if (_isLoadingFeed) return;
        _isLoadingFeed = true;

        IsLoading = true;
        RebuildInstallStateCache();
        Mods.Clear();
        lock (_allModsLock) { _allMods.Clear(); }

        try
        {
            var catalog = await _catalog.GetCatalogAsync();
            lock (_allModsLock) { _allMods = catalog.Mods.ToList(); }
            CatalogStatusText = catalog.IsCached
                ? $"Showing {catalog.Mods.Count} TerrariaModder mods from cache ({catalog.FetchedAtUtc.LocalDateTime:g}); Nexus refresh was incomplete."
                : catalog.IsComplete
                    ? $"{catalog.Mods.Count} TerrariaModder mods · refreshed {catalog.FetchedAtUtc.LocalDateTime:g}"
                    : $"Showing {catalog.Mods.Count} TerrariaModder mods (partial) · refreshed {catalog.FetchedAtUtc.LocalDateTime:g}";
            if (catalog.Warning != null)
                CatalogStatusText += $" - {catalog.Warning}";
            if (catalog.IsCached && catalog.LiveError != null)
                CatalogStatusText += $" - {catalog.LiveError}";
            ApplySort();
        }
        catch (Exception ex)
        {
            CatalogStatusText = ex.Message;
            _logger.Error("Failed to load feed", ex);
        }
        finally
        {
            IsLoading = false;
            _isLoadingFeed = false;
        }
    }

    private async Task SearchMod()
    {
        if (string.IsNullOrWhiteSpace(SearchText)) return;
        HasApiKey = _nexusApi.HasApiKey;
        if (!HasApiKey) return;

        int modId = 0;
        var text = SearchText.Trim();

        // Only do URL/ID lookup if it looks like a URL or pure number
        if (int.TryParse(text, out var id))
        {
            modId = id;
        }
        else if (text.Contains("nexusmods.com/terraria/mods/"))
        {
            var match = System.Text.RegularExpressions.Regex.Match(text, @"mods/(\d+)");
            if (match.Success)
                modId = int.Parse(match.Groups[1].Value);
        }

        if (modId <= 0) return; // Not a URL/ID — text filtering is live via ApplyFilter

        IsLoading = true;
        try
        {
            var mod = await _nexusApi.GetModInfoAsync(modId);
            if (mod != null && mod.Available)
            {
                bool added;
                lock (_allModsLock)
                {
                    added = !_allMods.Any(m => m.ModId == mod.ModId);
                    if (added)
                    {
                        mod.IsTerrariaModder = IsTerrariaModder(mod.Name, mod.Description);
                        _allMods.Add(mod);
                    }
                }
                if (added) ApplySort();
            }
        }
        catch (Exception ex)
        {
            _logger.Error("Failed to search mod", ex);
        }
        finally
        {
            IsLoading = false;
            SearchText = "";
        }
    }

    private void ApplyFilter()
    {
        var filter = SearchText?.Trim();

        // Don't text-filter on URLs or IDs — Enter key triggers lookup via SearchMod
        if (!string.IsNullOrEmpty(filter) &&
            (int.TryParse(filter, out _) || filter.Contains("nexusmods.com")))
            return;

        ApplySort();
    }

    public void ApplyCurrentSort() => ApplySort();

    /// <summary>
    /// Rebuild the displayed Mods collection from the canonical catalog with current filter/sort.
    /// </summary>
    private void ApplySort()
    {
        List<NexusMod> snapshot;
        lock (_allModsLock) { snapshot = _allMods.ToList(); }

        // Apply cached install states so filtering works correctly
        ApplyInstallStates(snapshot);

        var filter = SearchText?.Trim();
        var isTextFilter = !string.IsNullOrEmpty(filter)
            && !int.TryParse(filter, out _)
            && !filter.Contains("nexusmods.com");

        IEnumerable<NexusMod> source = snapshot.Where(m => m.ModId != 159); // exclude the Vault itself

        if (isTextFilter)
            source = source.Where(m => m.Name.Contains(filter!, StringComparison.OrdinalIgnoreCase)
                || m.Author.Contains(filter!, StringComparison.OrdinalIgnoreCase));

        if (_hideInstalled)
            source = source.Where(m => !m.IsInstalled);

        var sorted = SortBy switch
        {
            "Downloads" => source.OrderByDescending(m => m.Downloads).ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ThenBy(m => m.ModId).ToList(),
            "Updated" => source.OrderByDescending(m => m.UpdatedTimestamp).ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ThenBy(m => m.ModId).ToList(),
            "Endorsements" => source.OrderByDescending(m => m.EndorsementCount).ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ThenBy(m => m.ModId).ToList(),
            _ => source.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ThenBy(m => m.ModId).ToList()
        };

        Mods.Clear();
        foreach (var mod in sorted)
            Mods.Add(mod);
    }

    public void RefreshInstallStates()
    {
        RebuildInstallStateCache();
        ApplyInstallStates(Mods);
    }

    private void RebuildInstallStateCache()
    {
        var path = _settings.Load().TerrariaPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            _installStateCache = new();
            return;
        }

        var installed = _modState.ScanInstalledMods(path);
        var cache = new Dictionary<int, InstalledMod>();
        foreach (var mod in installed)
        {
            var nexusId = _updateTracker.GetNexusModId(mod);
            if (nexusId > 0)
                cache[nexusId] = mod;
        }
        _installStateCache = cache;
    }

    private void ApplyInstallStates(IEnumerable<NexusMod> mods)
    {
        var activeIds = new HashSet<int>(_downloadManager.Downloads
            .Where(d => d.IsDownloading)
            .Select(d => d.ModId));

        foreach (var mod in mods)
        {
            mod.IsDownloading = activeIds.Contains(mod.ModId);

            if (_installStateCache.TryGetValue(mod.ModId, out var localMod))
            {
                mod.IsInstalled = true;
                mod.InstalledVersion = localMod.Version;

                // Use the same version comparison as the update checker for consistency.
                // This correctly handles pre-release suffixes (-beta, -rc < release < -hotfix).
                mod.HasNewerVersion = !string.IsNullOrEmpty(mod.Version)
                    && !string.IsNullOrEmpty(localMod.Version)
                    && UpdateTracker.IsNewerVersion(mod.Version, localMod.Version);
            }
            else
            {
                mod.IsInstalled = false;
                mod.InstalledVersion = null;
                mod.HasNewerVersion = false;
            }
        }
    }

    private async Task DownloadMod(NexusMod? mod)
    {
        _logger.Info($"DownloadMod ENTERED: mod={mod?.Name ?? "NULL"} (id={mod?.ModId})");
        if (mod == null) return;

        _logger.Info($"DownloadMod: {mod.Name} (id={mod.ModId}), premium={_nexusApi.IsPremium}, hasKey={_nexusApi.HasApiKey}");

        // If already installed and up to date, confirm before redownloading
        if (mod.IsInstalled && !mod.HasNewerVersion)
        {
            var answer = await DialogHelper.ShowDialog(
                "Mod Already Installed",
                $"{mod.Name} is already installed (v{mod.InstalledVersion}).\n\n" +
                "Do you want to redownload and overwrite your current version?",
                ButtonEnum.YesNo, Icon.Question);
            if (answer != ButtonResult.Yes) return;
        }

        // Close detail sidebar when browser opens
        IsDetailOpen = false;

        // For non-premium users, try to resolve primary file and open download page directly
        if (!_nexusApi.IsPremium)
        {
            _logger.Info("DownloadMod: non-premium, resolving primary file for inline browser");
            string url;
            string? resolvedVersion = null;
            bool showFilesToast = false;
            try
            {
                var modFiles = await _nexusApi.GetModFilesAsync(mod.ModId);
                var primary = NexusFileSelector.SelectCurrentMain(modFiles);

                if (primary != null)
                {
                    // Navigate directly to the file's download page
                    url = $"https://www.nexusmods.com/terraria/mods/{mod.ModId}?tab=files&file_id={primary.FileId}";
                    resolvedVersion = primary.Version;
                    _logger.Info($"DownloadMod: resolved primary file {primary.FileId} v{resolvedVersion}");
                }
                else
                {
                    url = $"https://www.nexusmods.com/terraria/mods/{mod.ModId}?tab=files";
                    showFilesToast = true;
                }
            }
            catch
            {
                url = $"https://www.nexusmods.com/terraria/mods/{mod.ModId}?tab=files";
                showFilesToast = true;
            }

            var keepSettings = mod.IsInstalled && mod.HasNewerVersion;
            var toast = showFilesToast
                ? "Choose a file version below, then click 'Manual Download'"
                : "Click 'Manual Download' to install";

            var result = await OpenBrowserAsync(url, $"Installing: {mod.Name}", toast);
            await EnqueueFromBrowserResult(result, mod.ModId, keepSettings, resolvedVersion);
            return;
        }

        // Premium: direct download
        var files = await _nexusApi.GetModFilesAsync(mod.ModId);
        var mainFile = NexusFileSelector.SelectCurrentMain(files);
        if (mainFile == null)
        {
            _logger.Info("DownloadMod: no current main file found, opening in-app browser");
            var result = await OpenBrowserAsync($"https://www.nexusmods.com/terraria/mods/{mod.ModId}?tab=files", $"Installing: {mod.Name}",
                "Choose a file version below, then click 'Manual Download'");
            await EnqueueFromBrowserResult(result, mod.ModId);
            return;
        }

        var keep = mod.IsInstalled && mod.HasNewerVersion;
        _ = ShowToastAsync($"Downloading {mod.Name}...");
        await _downloadManager.EnqueueAsync(mod.ModId, mainFile.FileId, forceKeepSettings: keep);
    }

    private Task<Models.InlineBrowserResult?> OpenBrowserAsync(string url, string modName, string? toast = null)
        => OpenBrowser?.Invoke(url, modName, toast) ?? Task.FromResult<Models.InlineBrowserResult?>(null);

    private async Task ShowToastAsync(string message, int durationMs = 3000)
    {
        _toastCts?.Cancel();
        _toastCts = new CancellationTokenSource();
        var token = _toastCts.Token;

        ToastMessage = message;
        IsToastVisible = true;
        try { await Task.Delay(durationMs, token); }
        catch (OperationCanceledException) { return; }
        IsToastVisible = false;
    }

    /// <summary>Fires when a browser-based download succeeds (proves browser auth is valid).</summary>
    public event Action? BrowserDownloadSucceeded;

    private async Task EnqueueFromBrowserResult(Models.InlineBrowserResult? result, int modId, bool keepSettings = false, string? version = null)
    {
        if (result?.DownloadedFilePath != null)
        {
            await _downloadManager.EnqueueFromFileAsync(modId, result.DownloadedFilePath, forceKeepSettings: keepSettings, nexusVersion: version);
            BrowserDownloadSucceeded?.Invoke();
        }
    }

    public void ToggleSelectMod(NexusMod? mod)
    {
        if (mod == null) return;
        mod.IsSelectedForInstall = !mod.IsSelectedForInstall;
        SelectedForInstallCount = Mods.Count(m => m.IsSelectedForInstall);
    }

    private void ClearSelections()
    {
        foreach (var mod in Mods)
            mod.IsSelectedForInstall = false;
        SelectedForInstallCount = 0;
    }

    private async Task InstallSelected()
    {
        var selected = Mods.Where(m => m.IsSelectedForInstall).ToList();
        if (selected.Count == 0) return;

        _bulkCts?.Cancel();
        _bulkCts = new CancellationTokenSource();
        var ct = _bulkCts.Token;

        IsSelectMode = false; // exits select mode and clears selections
        IsBulkRunning = true;
        try
        {
            foreach (var mod in selected)
            {
                if (ct.IsCancellationRequested) break;
                await DownloadMod(mod);
            }
        }
        finally
        {
            IsBulkRunning = false;
        }
    }

    private static bool IsTerrariaModder(string? name, string? text)
    {
        return (name?.Contains("TerrariaModder", StringComparison.OrdinalIgnoreCase) ?? false)
            || (text?.Contains("TerrariaModder", StringComparison.OrdinalIgnoreCase) ?? false);
    }
}
