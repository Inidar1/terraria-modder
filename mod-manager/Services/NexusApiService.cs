using System.Net.Http;
using System.Net.Http.Headers;
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
    private const string GameDomain = "terraria";

    private readonly HttpClient _http;
    private string? _apiKey;

    public bool IsPremium { get; private set; }
    public int DailyRemaining { get; private set; } = -1;
    public int HourlyRemaining { get; private set; } = -1;

    public NexusApiService()
    {
        _http = new HttpClient();
        _http.DefaultRequestHeaders.Add("Application-Name", "TerrariaModder Vault");
        _http.DefaultRequestHeaders.Add("Application-Version", "0.1.0");
    }

    public void SetApiKey(string apiKey)
    {
        _apiKey = apiKey;
        _http.DefaultRequestHeaders.Remove("apikey");
        if (!string.IsNullOrWhiteSpace(apiKey))
            _http.DefaultRequestHeaders.Add("apikey", apiKey);
    }

    public bool HasApiKey => !string.IsNullOrWhiteSpace(_apiKey);

    public async Task<NexusUser?> ValidateApiKeyAsync()
    {
        var user = await GetAsync<NexusUser>("users/validate.json");
        if (user != null)
            IsPremium = user.IsPremium;
        return user;
    }

    // Mod listings
    public Task<List<NexusMod>?> GetLatestAddedAsync()
        => GetAsync<List<NexusMod>>($"games/{GameDomain}/mods/latest_added.json");

    public Task<List<NexusMod>?> GetLatestUpdatedAsync()
        => GetAsync<List<NexusMod>>($"games/{GameDomain}/mods/latest_updated.json");

    public Task<List<NexusMod>?> GetTrendingAsync()
        => GetAsync<List<NexusMod>>($"games/{GameDomain}/mods/trending.json");

    // All mods updated in period (1d, 1w, 1m) — returns IDs only
    public async Task<List<UpdatedModEntry>> GetUpdatedModIdsAsync(string period = "1m")
    {
        return await GetAsync<List<UpdatedModEntry>>(
            $"games/{GameDomain}/mods/updated.json?period={period}") ?? new();
    }

    // Mod details
    public Task<NexusMod?> GetModInfoAsync(int modId)
        => GetAsync<NexusMod>($"games/{GameDomain}/mods/{modId}.json");

    // Files
    public async Task<List<NexusModFile>> GetModFilesAsync(int modId)
    {
        var result = await GetAsync<NexusModFiles>($"games/{GameDomain}/mods/{modId}/files.json");
        return result?.Files ?? new List<NexusModFile>();
    }

    // Download links
    public async Task<List<NexusDownloadLink>> GetDownloadLinksAsync(
        int modId, int fileId, string? key = null, long? expires = null)
    {
        var url = $"games/{GameDomain}/mods/{modId}/files/{fileId}/download_link.json";

        if (!string.IsNullOrEmpty(key) && expires.HasValue)
            url += $"?key={Uri.EscapeDataString(key)}&expires={expires.Value}";

        return await GetAsync<List<NexusDownloadLink>>(url) ?? new List<NexusDownloadLink>();
    }

    private async Task<T?> GetAsync<T>(string path) where T : class
    {
        try
        {
            var response = await _http.GetAsync(BaseUrl + "/" + path.TrimStart('/'));
            ReadRateLimits(response);

            if (!response.IsSuccessStatusCode)
                return null;

            var json = await response.Content.ReadAsStringAsync();
            return JsonSerializer.Deserialize<T>(json);
        }
        catch
        {
            return null;
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
}
