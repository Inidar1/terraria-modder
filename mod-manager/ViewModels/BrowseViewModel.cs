using System.Collections.ObjectModel;
using System.Diagnostics;
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
    private string _selectedFeed = "All";
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

    private readonly NexusApiService _nexusApi;
    private readonly SettingsService _settings;
    private readonly ModStateService _modState;
    private readonly UpdateTracker _updateTracker;
    private readonly DownloadManager _downloadManager;
    private readonly Logger _logger;

    public ObservableCollection<NexusMod> Mods { get; } = new();

    public string SelectedFeed
    {
        get => _selectedFeed;
        set
        {
            if (SetProperty(ref _selectedFeed, value))
                _ = LoadFeedAsync();
        }
    }

    public bool IsLoading
    {
        get => _isLoading;
        set => SetProperty(ref _isLoading, value);
    }

    public bool HasApiKey
    {
        get => _hasApiKey;
        set => SetProperty(ref _hasApiKey, value);
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
                ApplySort();
        }
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
                ApplyFilter();
        }
    }

    public ICommand LoadAllCommand { get; }
    public ICommand LoadLatestCommand { get; }
    public ICommand LoadTrendingCommand { get; }
    public ICommand LoadUpdatedCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand ToggleLayoutCommand { get; }
    public ICommand ToggleHideInstalledCommand { get; }
    public ICommand SortByNameCommand { get; }
    public ICommand SortByDownloadsCommand { get; }
    public ICommand SortByUpdatedCommand { get; }
    public ICommand SortByEndorsementsCommand { get; }
    public ICommand DownloadModCommand { get; }
    public ICommand OpenNexusPageCommand { get; }
    public ICommand SearchCommand { get; }
    public ICommand OpenDetailCommand { get; }
    public ICommand CloseDetailCommand { get; }

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

    public BrowseViewModel(
        NexusApiService nexusApi,
        SettingsService settings,
        ModStateService modState,
        UpdateTracker updateTracker,
        DownloadManager downloadManager,
        Logger logger)
    {
        _nexusApi = nexusApi;
        _settings = settings;
        _modState = modState;
        _updateTracker = updateTracker;
        _downloadManager = downloadManager;
        _logger = logger;

        DownloadModCommand = new RelayCommand(param => _ = DownloadMod(param as NexusMod));
        OpenNexusPageCommand = new RelayCommand(param =>
        {
            if (param is NexusMod mod)
                Process.Start(new ProcessStartInfo($"https://www.nexusmods.com/terraria/mods/{mod.ModId}") { UseShellExecute = true });
        });
        HasApiKey = _nexusApi.HasApiKey;

        LoadAllCommand = new RelayCommand(() => SelectedFeed = "All");
        LoadLatestCommand = new RelayCommand(() => SelectedFeed = "Latest");
        LoadTrendingCommand = new RelayCommand(() => SelectedFeed = "Trending");
        LoadUpdatedCommand = new RelayCommand(() => SelectedFeed = "Updated");
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
    }

    private async Task OpenDetailAsync(NexusMod mod)
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
        if (!HasApiKey) return;
        if (_isLoadingFeed) return;
        _isLoadingFeed = true;

        IsLoading = true;
        RebuildInstallStateCache();
        Mods.Clear();
        lock (_allModsLock) { _allMods.Clear(); }

        try
        {
            if (SelectedFeed == "All")
            {
                await LoadAllModsAsync();
            }
            else
            {
                var mods = SelectedFeed switch
                {
                    "Trending" => await _nexusApi.GetTrendingAsync(),
                    "Updated" => await _nexusApi.GetLatestUpdatedAsync(),
                    _ => await _nexusApi.GetLatestAddedAsync()
                };
                var candidates = (mods ?? new List<NexusMod>()).Where(m => m.Available).ToList();

                // Quick-check name/summary, show immediately
                foreach (var mod in candidates)
                {
                    if (IsTerrariaModder(mod.Name, mod.Summary))
                    {
                        mod.IsTerrariaModder = true;
                        lock (_allModsLock) { _allMods.Add(mod); }
                    }
                }
                ApplySort();

                // Deep-check remaining via full description
                await DeepCheckModsAsync(candidates.Where(m => !m.IsTerrariaModder).ToList());
                ApplySort();
            }
        }
        catch (Exception ex)
        {
            _logger.Error("Failed to load feed", ex);
        }
        finally
        {
            IsLoading = false;
            _isLoadingFeed = false;
        }
    }

    private async Task LoadAllModsAsync()
    {
        // Phase 1: Fetch feeds + updated mod IDs in parallel
        var feedTask = Task.WhenAll(
            _nexusApi.GetLatestAddedAsync(),
            _nexusApi.GetTrendingAsync(),
            _nexusApi.GetLatestUpdatedAsync());
        var updatedTask = _nexusApi.GetUpdatedModIdsAsync("1m");

        await Task.WhenAll(feedTask, updatedTask);

        var feedResults = feedTask.Result;
        var updatedEntries = updatedTask.Result;

        // Collect feed mods (these have full info already)
        var knownMods = new Dictionary<int, NexusMod>();
        foreach (var feed in feedResults)
        {
            if (feed == null) continue;
            foreach (var mod in feed)
            {
                if (mod.Available && !knownMods.ContainsKey(mod.ModId))
                    knownMods[mod.ModId] = mod;
            }
        }

        // Quick-check feed mods and show immediately
        foreach (var mod in knownMods.Values)
        {
            if (IsTerrariaModder(mod.Name, mod.Summary))
            {
                mod.IsTerrariaModder = true;
                lock (_allModsLock) { _allMods.Add(mod); }
            }
        }
        ApplySort();

        // Phase 2: Deep-check feed mods that didn't match on name/summary
        var feedUnchecked = knownMods.Values.Where(m => !m.IsTerrariaModder).ToList();
        await DeepCheckModsAsync(feedUnchecked);
        ApplySort();

        // Phase 3: Fetch full info for updated mods not in feeds (5 concurrent)
        var unknownIds = updatedEntries
            .Select(e => e.ModId)
            .Where(id => !knownMods.ContainsKey(id))
            .ToList();

        if (unknownIds.Count > 0)
        {
            var semaphore = new SemaphoreSlim(5);
            var fetchTasks = unknownIds.Select(async modId =>
            {
                await semaphore.WaitAsync();
                try
                {
                    var mod = await _nexusApi.GetModInfoAsync(modId);
                    if (mod != null && mod.Available &&
                        IsTerrariaModder(mod.Name, mod.Summary ?? mod.Description))
                    {
                        mod.IsTerrariaModder = true;
                        lock (_allModsLock) _allMods.Add(mod);
                    }
                }
                catch { }
                finally { semaphore.Release(); }
            });
            await Task.WhenAll(fetchTasks);
            ApplySort();
        }
    }

    private async Task DeepCheckModsAsync(List<NexusMod> unchecked_)
    {
        if (unchecked_.Count == 0) return;

        var semaphore = new SemaphoreSlim(5);
        var tasks = unchecked_.Select(async mod =>
        {
            await semaphore.WaitAsync();
            try
            {
                var full = await _nexusApi.GetModInfoAsync(mod.ModId);
                if (full?.Description != null && IsTerrariaModder(full.Name, full.Description))
                {
                    mod.IsTerrariaModder = true;
                    lock (_allModsLock) _allMods.Add(mod);
                }
            }
            catch { }
            finally { semaphore.Release(); }
        });
        await Task.WhenAll(tasks);
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

        IEnumerable<NexusMod> source = snapshot;

        if (isTextFilter)
            source = source.Where(m => m.Name.Contains(filter!, StringComparison.OrdinalIgnoreCase)
                || m.Author.Contains(filter!, StringComparison.OrdinalIgnoreCase));

        if (_hideInstalled)
            source = source.Where(m => !m.IsInstalled);

        var sorted = SortBy switch
        {
            "Downloads" => source.OrderByDescending(m => m.Downloads).ToList(),
            "Updated" => source.OrderByDescending(m => m.UpdatedTimestamp).ToList(),
            "Endorsements" => source.OrderByDescending(m => m.EndorsementCount).ToList(),
            _ => source.OrderBy(m => m.Name).ToList()
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
        foreach (var mod in mods)
        {
            if (_installStateCache.TryGetValue(mod.ModId, out var localMod))
            {
                mod.IsInstalled = true;
                mod.InstalledVersion = localMod.Version;

                // Compare versions
                var nexusVer = (mod.Version ?? "").TrimStart('v', 'V');
                var localVer = (localMod.Version ?? "").TrimStart('v', 'V');
                mod.HasNewerVersion = !string.IsNullOrEmpty(nexusVer)
                    && !string.IsNullOrEmpty(localVer)
                    && nexusVer != localVer
                    && (Version.TryParse(nexusVer, out var nv) && Version.TryParse(localVer, out var lv)
                        ? nv > lv
                        : string.Compare(nexusVer, localVer, StringComparison.OrdinalIgnoreCase) > 0);
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
        if (mod == null) return;

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

        var files = await _nexusApi.GetModFilesAsync(mod.ModId);
        if (files.Count == 0)
        {
            Process.Start(new ProcessStartInfo($"https://www.nexusmods.com/terraria/mods/{mod.ModId}?tab=files") { UseShellExecute = true });
            return;
        }

        var mainFile = files.FirstOrDefault(f => f.IsPrimary)
            ?? files.OrderByDescending(f => f.UploadedTimestamp).First();

        if (_nexusApi.IsPremium)
        {
            var keepSettings = mod.IsInstalled && mod.HasNewerVersion;
            _ = ShowToastAsync($"Downloading {mod.Name}...");
            await _downloadManager.EnqueueAsync(mod.ModId, mainFile.FileId, forceKeepSettings: keepSettings);
        }
        else
        {
            Process.Start(new ProcessStartInfo($"https://www.nexusmods.com/terraria/mods/{mod.ModId}?tab=files") { UseShellExecute = true });
        }
    }

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

    private static bool IsTerrariaModder(string? name, string? text)
    {
        return (name?.Contains("TerrariaModder", StringComparison.OrdinalIgnoreCase) ?? false)
            || (text?.Contains("TerrariaModder", StringComparison.OrdinalIgnoreCase) ?? false);
    }
}
