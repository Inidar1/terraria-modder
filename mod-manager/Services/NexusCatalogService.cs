using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TerrariaModManager.Models;

namespace TerrariaModManager.Services;

public sealed record NexusCatalogResult(
    IReadOnlyList<NexusMod> Mods,
    string Source,
    DateTimeOffset FetchedAtUtc,
    int PublishedCount,
    bool IsComplete,
    string? LiveError = null,
    string? Warning = null)
{
    public bool IsCached => Source == "cache";
}

public sealed class NexusCatalogException : Exception
{
    public NexusCatalogException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>
/// Owns the Vault Browse catalog. Active Core requirements and the TerrariaModder
/// tag independently qualify published mods; description text is not a membership signal.
/// </summary>
public sealed class NexusCatalogService : IDisposable
{
    internal const int TerrariaGameId = 549;
    internal const int CoreModId = 135;
    internal const int VaultModId = 159;
    private const int PageSize = 100;
    private const string Endpoint = "https://api.nexusmods.com/v2/graphql";

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly NexusApiService _nexusApi;
    private readonly AppPaths _paths;
    private readonly Logger _logger;
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    public NexusCatalogService(AppPaths paths, Logger logger, NexusApiService nexusApi)
        : this(paths, logger, nexusApi, new HttpClient(), ownsHttp: true) { }

    internal NexusCatalogService(AppPaths paths, Logger logger, NexusApiService nexusApi,
        HttpClient http, bool ownsHttp = false)
    {
        _paths = paths;
        _logger = logger;
        _nexusApi = nexusApi;
        _http = http;
        _ownsHttp = ownsHttp;
        if (!_http.DefaultRequestHeaders.Contains("Application-Name"))
            _http.DefaultRequestHeaders.Add("Application-Name", "TerrariaModder Vault");
        if (!_http.DefaultRequestHeaders.Contains("Application-Version"))
            _http.DefaultRequestHeaders.Add("Application-Version", GetAppVersion());
    }

    internal string CachePath => Path.Combine(_paths.CacheDirectory, "nexus-terrariamodder-catalog-v3.json");

    public async Task<NexusCatalogResult> GetCatalogAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var live = await FetchLiveAsync(cancellationToken);
            if (live.IsComplete)
                SaveCache(live);
            else
            {
                var cached = LoadCache();
                if (cached != null)
                    return cached with { Source = "cache", LiveError = live.Warning, Warning = null };
            }
            return live;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.Warn($"Nexus catalog live refresh failed: {ex.Message}");
            var cached = LoadCache();
            if (cached != null)
                return cached with { Source = "cache", LiveError = ex.Message };
            throw new NexusCatalogException("Nexus could not provide the TerrariaModder catalog and no cached catalog is available.", ex);
        }
    }

    private async Task<NexusCatalogResult> FetchLiveAsync(CancellationToken cancellationToken)
    {
        var requirements = new List<RequirementNode>();
        string? coreUid = null;
        var offset = 0;
        var total = int.MaxValue;
        while (offset < total)
        {
            var variables = new { gameId = TerrariaGameId.ToString(), modId = CoreModId.ToString(), offset, count = PageSize };
            var root = await PostAsync<RequirementsData>(RequirementsQuery, variables, cancellationToken);
            var page = root.Mod?.ModRequirements.ModsRequiringThisMod
                ?? throw new NexusCatalogException("Nexus returned no reverse-requirements page for TerrariaModder Core.");
            coreUid ??= root.Mod?.Uid;
            total = page.TotalCount;
            requirements.AddRange(page.Nodes);
            if (page.Nodes.Length == 0)
            {
                if (total == 0) break;
                throw new NexusCatalogException("Nexus returned an empty reverse-requirements page.");
            }
            offset += page.Nodes.Length;
        }
        if (string.IsNullOrWhiteSpace(coreUid))
            throw new NexusCatalogException("Nexus returned no identifier for TerrariaModder Core.");
        if (requirements.Count != total)
            throw new NexusCatalogException("Nexus reverse-requirements pages changed during refresh.");

        var reverseIds = requirements
            .Select(node => int.TryParse(node.ModId, out var id) ? id : 0)
            .Where(id => id > 0)
            .ToHashSet();

        var published = new List<ModNode>();
        offset = 0;
        total = int.MaxValue;
        while (offset < total)
        {
            var root = await PostAsync<PublishedModsData>(PublishedModsQuery,
                new { gameId = TerrariaGameId.ToString(), offset, count = PageSize }, cancellationToken);
            var page = root.Mods
                ?? throw new NexusCatalogException("Nexus returned no published Terraria mods page.");
            total = page.TotalCount;
            published.AddRange(page.Nodes);
            if (page.Nodes.Length == 0)
            {
                if (total == 0) break;
                throw new NexusCatalogException("Nexus returned an empty published-mods page.");
            }
            offset += page.Nodes.Length;
        }
        if (published.Count == 0 || published.Select(node => node.ModId).Distinct().Count() != total)
            throw new NexusCatalogException("Nexus published-mods pages changed during refresh.");

        var details = published
            .Where(node => node.GameId == TerrariaGameId &&
                string.Equals(node.Status, "published", StringComparison.OrdinalIgnoreCase) &&
                node.ModId != CoreModId && node.ModId != VaultModId)
            .GroupBy(node => node.ModId)
            .Select(group => group.First())
            .ToArray();
        if (details.Any(node => node.LegacyModRequirementsEnabled == null || node.Tags == null))
            throw new NexusCatalogException("Nexus omitted catalog tags or requirement modes.");

        FileInventory? files = null;
        string? warning = null;
        try
        {
            files = await FetchFileInventoryAsync(details.Select(node => node.ModId).ToArray(), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            warning = "current file versions unavailable";
            _logger.Warn($"Nexus catalog file-version enrichment failed: {ex.Message}");
        }

        HashSet<string> dependentVersions = [];
        if (files != null)
        {
            if (!_nexusApi.HasApiKey)
                warning = "Sign in to Nexus to include file requirements.";
            else
            {
                try
                {
                    dependentVersions = await _nexusApi.GetDependentFileVersionIdsAsync(
                        files.ActiveVersionIdsByMod.Values.SelectMany(ids => ids).ToArray(),
                        coreUid, cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    warning = "file requirements unavailable";
                    _logger.Warn($"Nexus catalog file-requirement lookup failed: {ex.Message}");
                }
            }
        }

        var mods = details
            .Where(node => HasTerrariaModderTag(node) ||
                (node.LegacyModRequirementsEnabled == true && reverseIds.Contains(node.ModId)) ||
                (node.LegacyModRequirementsEnabled == false && files != null &&
                    files.ActiveVersionIdsByMod.TryGetValue(node.ModId, out var ids) &&
                    ids.Any(dependentVersions.Contains)))
            .Select(node => Map(node, files?.CurrentMainByMod.GetValueOrDefault(node.ModId)))
            .OrderBy(mod => mod.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(mod => mod.ModId)
            .ToArray();

        return new NexusCatalogResult(
            mods, "live", DateTimeOffset.UtcNow, published.Count,
            IsComplete: warning == null, Warning: warning);
    }

    private static bool HasTerrariaModderTag(ModNode node) =>
        node.Tags?.Any(tag => string.Equals(tag.Name?.Trim(), "TerrariaModder",
            StringComparison.OrdinalIgnoreCase)) == true;

    private async Task<FileInventory> FetchFileInventoryAsync(
        int[] modIds, CancellationToken cancellationToken)
    {
        var selected = new Dictionary<int, NexusModFile>();
        var activeVersionIds = new Dictionary<int, string[]>();
        foreach (var chunk in modIds.Chunk(50))
        {
            var query = new StringBuilder("query VaultCurrentFiles {");
            foreach (var modId in chunk)
            {
                query.Append($" m{modId}: modFiles(modId: \"{modId}\", gameId: \"{TerrariaGameId}\") ");
                query.Append("{ uid fileId version category primary date }");
            }
            query.Append(" }");

            var data = await PostAsync<Dictionary<string, ModFileNode[]>>(
                query.ToString(), new { }, cancellationToken);
            foreach (var modId in chunk)
            {
                if (!data.TryGetValue($"m{modId}", out var files) || files == null)
                    throw new NexusCatalogException($"Nexus omitted files for Terraria mod {modId}.");
                var active = files.Where(file => file.Category is "MAIN" or "OPTIONAL" or
                    "UPDATE" or "MISCELLANEOUS").ToArray();
                if (active.Any(file => string.IsNullOrWhiteSpace(file.Uid)))
                    throw new NexusCatalogException($"Nexus omitted an active file identifier for Terraria mod {modId}.");
                activeVersionIds[modId] = active.Select(file => file.Uid).Distinct().ToArray();
                var current = NexusFileSelector.SelectCurrentMain(files.Select(file => new NexusModFile
                {
                    FileId = file.FileId,
                    Version = file.Version,
                    CategoryName = file.Category,
                    IsPrimary = file.Primary > 0,
                    UploadedTimestamp = file.Date
                }));
                if (current != null) selected[modId] = current;
            }
        }
        return new FileInventory(selected, activeVersionIds);
    }

    private async Task<T> PostAsync<T>(string query, object variables, CancellationToken cancellationToken) where T : class
    {
        using var response = await _http.PostAsJsonAsync(Endpoint, new { query, variables }, _json, cancellationToken);
        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new NexusCatalogException($"Nexus GraphQL returned HTTP {(int)response.StatusCode}.");

        GraphQlEnvelope<T>? envelope;
        try { envelope = JsonSerializer.Deserialize<GraphQlEnvelope<T>>(payload, _json); }
        catch (JsonException ex) { throw new NexusCatalogException("Nexus GraphQL returned malformed JSON.", ex); }

        if (envelope?.Errors is { Length: > 0 })
            throw new NexusCatalogException($"Nexus GraphQL rejected the catalog query: {string.Join("; ", envelope.Errors.Select(e => e.Message))}");
        return envelope?.Data ?? throw new NexusCatalogException("Nexus GraphQL returned no catalog data.");
    }

    private NexusMod Map(ModNode node, NexusModFile? currentFile)
    {
        return new NexusMod
        {
            ModId = node.ModId,
            Name = node.Name,
            Summary = node.Summary,
            Version = string.IsNullOrWhiteSpace(currentFile?.Version) ? node.Version : currentFile.Version,
            Author = string.IsNullOrWhiteSpace(node.Author) ? node.Uploader?.Name ?? "Unknown" : node.Author,
            PictureUrl = node.PictureUrl ?? node.ThumbnailUrl,
            Downloads = node.Downloads,
            UniqueDownloads = node.Downloads,
            EndorsementCount = node.Endorsements,
            CreatedTimestamp = ToUnixTime(node.CreatedAt),
            UpdatedTimestamp = ToUnixTime(node.UpdatedAt),
            Available = true,
            Status = node.Status,
            IsTerrariaModder = true
        };
    }

    private static long ToUnixTime(string value) =>
        DateTimeOffset.TryParse(value, out var parsed) ? parsed.ToUnixTimeSeconds() : 0;

    private void SaveCache(NexusCatalogResult result)
    {
        try
        {
            Directory.CreateDirectory(_paths.CacheDirectory);
            var temp = CachePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(result, _json));
            File.Move(temp, CachePath, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.Warn($"Nexus catalog cache write failed: {ex.Message}");
        }
    }

    private NexusCatalogResult? LoadCache()
    {
        try
        {
            if (!File.Exists(CachePath)) return null;
            var result = JsonSerializer.Deserialize<NexusCatalogResult>(File.ReadAllText(CachePath), _json);
            if (result == null || !result.IsComplete || result.Mods.Count == 0) return null;
            foreach (var mod in result.Mods)
                mod.IsTerrariaModder = true;
            return result;
        }
        catch (Exception ex)
        {
            _logger.Warn($"Nexus catalog cache read failed: {ex.Message}");
            return null;
        }
    }

    private static string GetAppVersion()
    {
        var version = typeof(NexusCatalogService).Assembly.GetName().Version;
        return version == null ? "0.0.0" : $"{version.Major}.{version.Minor}.{version.Build}";
    }

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }

    private const string RequirementsQuery = """
        query VaultRequirements($gameId: ID!, $modId: ID!, $offset: Int!, $count: Int!) {
          mod(gameId: $gameId, modId: $modId) {
            uid
            modRequirements {
              modsRequiringThisMod(offset: $offset, count: $count) {
                totalCount
                nodesCount
                nodes { modId modName notes url }
              }
            }
          }
        }
        """;

    private const string PublishedModsQuery = """
        query VaultPublishedMods($gameId: String!, $offset: Int!, $count: Int!) {
          mods(filter: {gameId: [{value: $gameId}], status: [{value: "published"}]},
               offset: $offset, count: $count) {
            nodesCount
            totalCount
            nodes {
              modId gameId name summary version pictureUrl thumbnailUrl downloads endorsements
              createdAt updatedAt status author uploader { name }
              legacyModRequirementsEnabled tags { name }
            }
          }
        }
        """;

    private sealed record GraphQlEnvelope<T>(T? Data, GraphQlError[]? Errors) where T : class;
    private sealed record GraphQlError(string Message);
    private sealed record RequirementsData(ModNodeWithRequirements? Mod);
    private sealed record ModNodeWithRequirements(string Uid, ModRequirements ModRequirements);
    private sealed record ModRequirements(ModRequiringPage ModsRequiringThisMod);
    private sealed record ModRequiringPage(int TotalCount, int NodesCount, RequirementNode[] Nodes);
    private sealed record RequirementNode(string ModId, string ModName, string? Notes, string? Url);
    private sealed record PublishedModsData(ModPage? Mods);
    private sealed record ModPage(int TotalCount, int NodesCount, ModNode[] Nodes);
    private sealed record ModNode(
        int ModId, int GameId, string Name, string Summary, string Version,
        string? PictureUrl, string? ThumbnailUrl, int Downloads, int Endorsements,
        string CreatedAt, string UpdatedAt, string Status, string? Author, UploaderNode? Uploader,
        bool? LegacyModRequirementsEnabled, TagNode[]? Tags);
    private sealed record ModFileNode(string Uid, int FileId, string Version, string Category, int Primary, long Date);
    private sealed record TagNode(string? Name);
    private sealed record UploaderNode(string Name);
    private sealed record FileInventory(
        Dictionary<int, NexusModFile> CurrentMainByMod,
        Dictionary<int, string[]> ActiveVersionIdsByMod);
}
