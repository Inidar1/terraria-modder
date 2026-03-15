using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using TerrariaModder.Core.Logging;

namespace TerrariaModder.Core.Nexus
{
    internal sealed class NexusApiService : IDisposable
    {
        private const string BaseUrl = "https://api.nexusmods.com/v1";
        private const string GameDomain = "terraria";

        private readonly HttpClient _http;
        private readonly JsonSerializerOptions _jsonOptions;
        private readonly ILogger _log;
        private string _apiKey;

        public NexusApiService(ILogger log)
        {
            _log = log;
            _http = new HttpClient();
            _http.DefaultRequestHeaders.Add("Application-Name", "TerrariaModder");
            _http.DefaultRequestHeaders.Add("Application-Version", PluginLoader.FrameworkVersion);
            _jsonOptions = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            };
        }

        public bool HasApiKey => !string.IsNullOrWhiteSpace(_apiKey);
        public bool IsPremium { get; private set; }
        public int DailyRemaining { get; private set; } = -1;
        public int HourlyRemaining { get; private set; } = -1;

        public void SetApiKey(string apiKey)
        {
            _apiKey = apiKey ?? string.Empty;
            _http.DefaultRequestHeaders.Remove("apikey");
            if (!string.IsNullOrWhiteSpace(_apiKey))
                _http.DefaultRequestHeaders.Add("apikey", _apiKey);
        }

        public async Task<NexusUser> ValidateApiKeyAsync()
        {
            var user = await GetAsync<NexusUser>("users/validate.json").ConfigureAwait(false);
            if (user != null)
                IsPremium = user.IsPremium;
            return user;
        }

        public Task<List<NexusMod>> GetLatestAddedAsync()
        {
            return GetAsync<List<NexusMod>>("games/" + GameDomain + "/mods/latest_added.json");
        }

        public Task<List<NexusMod>> GetLatestUpdatedAsync()
        {
            return GetAsync<List<NexusMod>>("games/" + GameDomain + "/mods/latest_updated.json");
        }

        public Task<List<NexusMod>> GetTrendingAsync()
        {
            return GetAsync<List<NexusMod>>("games/" + GameDomain + "/mods/trending.json");
        }

        public async Task<List<UpdatedModEntry>> GetUpdatedModIdsAsync(string period = "1m")
        {
            return await GetAsync<List<UpdatedModEntry>>("games/" + GameDomain + "/mods/updated.json?period=" + period).ConfigureAwait(false)
                ?? new List<UpdatedModEntry>();
        }

        public Task<NexusMod> GetModInfoAsync(int modId)
        {
            return GetAsync<NexusMod>("games/" + GameDomain + "/mods/" + modId + ".json");
        }

        public async Task<List<NexusModFile>> GetModFilesAsync(int modId)
        {
            var response = await GetAsync<NexusModFiles>("games/" + GameDomain + "/mods/" + modId + "/files.json").ConfigureAwait(false);
            return response?.Files ?? new List<NexusModFile>();
        }

        public async Task<List<NexusDownloadLink>> GetDownloadLinksAsync(int modId, int fileId, string key = null, long? expires = null)
        {
            string path = "games/" + GameDomain + "/mods/" + modId + "/files/" + fileId + "/download_link.json";
            if (!string.IsNullOrEmpty(key) && expires.HasValue)
                path += "?key=" + Uri.EscapeDataString(key) + "&expires=" + expires.Value;

            return await GetAsync<List<NexusDownloadLink>>(path).ConfigureAwait(false) ?? new List<NexusDownloadLink>();
        }

        private async Task<T> GetAsync<T>(string path) where T : class
        {
            try
            {
                using (var response = await _http.GetAsync(BaseUrl + "/" + path.TrimStart('/')).ConfigureAwait(false))
                {
                    ReadRateLimits(response);
                    if (!response.IsSuccessStatusCode)
                    {
                        _log?.Warn("[Nexus] Request failed: " + response.StatusCode + " " + path);
                        return null;
                    }

                    string json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    return JsonSerializer.Deserialize<T>(json, _jsonOptions);
                }
            }
            catch (Exception ex)
            {
                _log?.Warn("[Nexus] Request exception for " + path + ": " + ex.Message);
                return null;
            }
        }

        private void ReadRateLimits(HttpResponseMessage response)
        {
            if (response.Headers.TryGetValues("X-RL-Daily-Remaining", out IEnumerable<string> daily))
            {
                if (int.TryParse(daily.FirstOrDefault(), out int dailyValue))
                    DailyRemaining = dailyValue;
            }

            if (response.Headers.TryGetValues("X-RL-Hourly-Remaining", out IEnumerable<string> hourly))
            {
                if (int.TryParse(hourly.FirstOrDefault(), out int hourlyValue))
                    HourlyRemaining = hourlyValue;
            }
        }

        public void Dispose()
        {
            _http.Dispose();
        }
    }
}
