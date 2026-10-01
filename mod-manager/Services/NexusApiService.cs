using System.Net.Http;
using System.Net.Http.Headers;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using TerrariaModManager.Models;

namespace TerrariaModManager.Services;

public class NexusUser
{
    [JsonPropertyName("user_id")]
    public int UserId { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("is_premium")]
    public bool IsPremium { get; set; }

    [JsonPropertyName("is_supporter")]
    public bool IsSupporter { get; set; }

    [JsonPropertyName("profile_url")]
    public string? ProfileUrl { get; set; }
}

public class NexusApiService : IDisposable
{
    private const string BaseUrl = "https://api.nexusmods.com/v1";
    private const string DependencyBatchUrl = "https://api.nexusmods.com/v3/mod-file-versions/dependencies/ranges/materialized/batch";
    private const string GameDomain = "terraria";

    private readonly HttpClient _http;
    private string? _apiKey;

    public bool IsPremium { get; private set; }
    public int DailyRemaining { get; private set; } = -1;
    public int HourlyRemaining { get; private set; } = -1;

    public NexusApiService()
        : this(new HttpClient()) { }

    internal NexusApiService(HttpClient http)
    {
        _http = http;
        _http.DefaultRequestHeaders.Add("Application-Name", "TerrariaModder Vault");
        _http.DefaultRequestHeaders.Add("Application-Version", GetAppVersion());
    }

    private static string GetAppVersion()
    {
        var v = typeof(NexusApiService).Assembly.GetName().Version;
        return v != null ? $"{v.Major}.{v.Minor}.{v.Build}" : "0.1.0";
    }

    public void SetApiKey(string apiKey)
    {
        _apiKey = apiKey;
        _http.DefaultRequestHeaders.Remove("apikey");
        if (!string.IsNullOrWhiteSpace(apiKey))
            _http.DefaultRequestHeaders.Add("apikey", apiKey);
    }

    public bool HasApiKey => !string.IsNullOrWhiteSpace(_apiKey);

    /// <summary>Resolves effective file-to-file dependencies using the signed-in user's key.</summary>
    public async Task<HashSet<string>> GetDependentFileVersionIdsAsync(
        IReadOnlyCollection<string> sourceVersionIds, string targetModUid,
        CancellationToken cancellationToken = default)
    {
        if (!HasApiKey)
            throw new NexusApiException("Sign in to Nexus to load file requirements.", HttpStatusCode.Unauthorized);

        var matching = new HashSet<string>(StringComparer.Ordinal);
        foreach (var ids in sourceVersionIds.Distinct(StringComparer.Ordinal).Chunk(5000))
        {
            var requested = ids.ToHashSet(StringComparer.Ordinal);
            var page = 1;
            int? expectedCount = null;
            var receivedCount = 0;
            while (true)
            {
                using var response = await _http.PostAsJsonAsync(DependencyBatchUrl,
                    new { version_ids = ids, page, page_size = 5000 }, cancellationToken);
                ReadRateLimits(response);
                if (!response.IsSuccessStatusCode)
                    throw new NexusApiException(
                        response.StatusCode == HttpStatusCode.TooManyRequests
                            ? "Nexus API rate limit reached. Try again after the reset shown by Nexus."
                            : $"Nexus API returned HTTP {(int)response.StatusCode} ({response.ReasonPhrase}).",
                        response.StatusCode);

                DependencyBatchResponse? payload;
                try
                {
                    payload = await response.Content.ReadFromJsonAsync<DependencyBatchResponse>(cancellationToken);
                }
                catch (JsonException ex)
                {
                    throw new NexusApiException("Nexus API returned malformed file dependencies.", response.StatusCode, ex);
                }
                if (payload?.Data?.Candidates == null || payload.Meta == null)
                    throw new NexusApiException("Nexus API returned incomplete file dependencies.", response.StatusCode);
                if (payload.Meta.TotalCount < 0 || payload.Data.Candidates.Any(candidate =>
                        string.IsNullOrWhiteSpace(candidate.SourceVersionId) ||
                        string.IsNullOrWhiteSpace(candidate.ModId) ||
                        !requested.Contains(candidate.SourceVersionId)))
                    throw new NexusApiException("Nexus API returned invalid file dependencies.", response.StatusCode);
                if ((expectedCount is int priorCount && payload.Meta.TotalCount != priorCount) ||
                    (payload.Data.Candidates.Length == 0 && (payload.Meta.TotalCount > 0 || expectedCount != null)))
                    throw new NexusApiException("Nexus API returned incomplete file dependency pages.", response.StatusCode);

                expectedCount ??= payload.Meta.TotalCount;
                receivedCount += payload.Data.Candidates.Length;
                if (receivedCount > expectedCount.Value)
                    throw new NexusApiException("Nexus API returned invalid file dependency counts.", response.StatusCode);

                foreach (var candidate in payload.Data.Candidates)
                {
                    if (string.Equals(candidate.ModId, targetModUid, StringComparison.Ordinal))
                        matching.Add(candidate.SourceVersionId);
                }
                if (receivedCount == expectedCount.Value)
                    break;
                page++;
            }
        }
        return matching;
    }

    public async Task<NexusUser?> ValidateApiKeyAsync()
    {
        NexusUser? user;
        try { user = await GetAsync<NexusUser>("users/validate.json"); }
        catch (NexusApiException ex) when (ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return null;
        }
        if (user != null)
            IsPremium = user.IsPremium;
        return user;
    }

    // Mod listings
    // All mods updated in period (1d, 1w, 1m) — returns IDs only
    // Mod details
    public Task<NexusMod?> GetModInfoAsync(int modId, CancellationToken cancellationToken = default)
        => GetAsync<NexusMod>($"games/{GameDomain}/mods/{modId}.json", cancellationToken);

    // Files
    public async Task<List<NexusModFile>> GetModFilesAsync(int modId, CancellationToken cancellationToken = default)
    {
        var result = await GetAsync<NexusModFiles>($"games/{GameDomain}/mods/{modId}/files.json", cancellationToken);
        return result?.Files ?? new List<NexusModFile>();
    }

    // Download links
    public async Task<List<NexusDownloadLink>> GetDownloadLinksAsync(
        int modId, int fileId, string? key = null, long? expires = null,
        CancellationToken cancellationToken = default)
    {
        var url = $"games/{GameDomain}/mods/{modId}/files/{fileId}/download_link.json";

        if (!string.IsNullOrEmpty(key) && expires.HasValue)
            url += $"?key={Uri.EscapeDataString(key)}&expires={expires.Value}";

        return await GetAsync<List<NexusDownloadLink>>(url, cancellationToken) ?? new List<NexusDownloadLink>();
    }

    private async Task<T?> GetAsync<T>(string path, CancellationToken cancellationToken = default) where T : class
    {
        HttpResponseMessage response;
        try
        {
            response = await _http.GetAsync(BaseUrl + "/" + path.TrimStart('/'), cancellationToken);
            ReadRateLimits(response);
        }
        catch (OperationCanceledException) { throw; }
        catch (HttpRequestException ex)
        {
            throw new NexusApiException("Nexus API could not be reached.", null, ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var message = response.StatusCode == HttpStatusCode.TooManyRequests
                    ? "Nexus API rate limit reached. Try again after the reset shown by Nexus."
                    : $"Nexus API returned HTTP {(int)response.StatusCode} ({response.ReasonPhrase}).";
                throw new NexusApiException(message, response.StatusCode);
            }

            try
            {
                var json = await response.Content.ReadAsStringAsync(cancellationToken);
                return JsonSerializer.Deserialize<T>(json)
                    ?? throw new NexusApiException("Nexus API returned an empty response.", response.StatusCode);
            }
            catch (JsonException ex)
            {
                throw new NexusApiException("Nexus API returned malformed JSON.", response.StatusCode, ex);
            }
        }
    }

    private void ReadRateLimits(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues("X-RL-Daily-Remaining", out var daily))
        {
            if (int.TryParse(daily.FirstOrDefault(), out var d))
                DailyRemaining = d;
        }
        if (response.Headers.TryGetValues("X-RL-Hourly-Remaining", out var hourly))
        {
            if (int.TryParse(hourly.FirstOrDefault(), out var h))
                HourlyRemaining = h;
        }
    }

    public void Dispose() => _http.Dispose();

    private sealed record DependencyBatchResponse(
        [property: JsonPropertyName("data")] DependencyBatchData? Data,
        [property: JsonPropertyName("meta")] DependencyBatchMeta? Meta);
    private sealed record DependencyBatchData(
        [property: JsonPropertyName("candidates")] DependencyCandidate[]? Candidates);
    private sealed record DependencyBatchMeta(
        [property: JsonPropertyName("total_count")] int TotalCount);
    private sealed record DependencyCandidate(
        [property: JsonPropertyName("source_version_id")] string SourceVersionId,
        [property: JsonPropertyName("mod_id")] string ModId);
}

public sealed class NexusApiException : Exception
{
    public NexusApiException(string message, HttpStatusCode? statusCode, Exception? inner = null)
        : base(message, inner) => StatusCode = statusCode;

    public HttpStatusCode? StatusCode { get; }
    public bool IsRateLimited => StatusCode == HttpStatusCode.TooManyRequests;
}
