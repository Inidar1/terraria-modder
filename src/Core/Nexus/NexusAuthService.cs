using System;
using System.Diagnostics;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using TerrariaModder.Core.Config;
using TerrariaModder.Core.Logging;

namespace TerrariaModder.Core.Nexus
{
    internal sealed class NexusAuthService : IDisposable
    {
        private const string SsoWebSocketUrl = "wss://sso.nexusmods.com";
        private const string ApplicationSlug = "terraria-modder-vault";

        private readonly ILogger _log;
        private readonly NexusApiService _api;
        private readonly string _authFilePath;
        private readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions { WriteIndented = true };

        private ClientWebSocket _webSocket;
        private CancellationTokenSource _loginCts;
        private string _connectionToken;

        public NexusAuthService(ILogger log, NexusApiService api)
        {
            _log = log;
            _api = api;
            _authFilePath = Path.Combine(CoreConfig.Instance.CorePath, "nexus-auth.json");
            Load();
        }

        public NexusAuthState State { get; private set; } = new NexusAuthState();
        public bool IsLoginInProgress { get; private set; }
        public string LoginStatus { get; private set; }
        public bool HasApiKey => !string.IsNullOrWhiteSpace(State.ApiKey);

        public async Task<NexusUser> ValidateStoredKeyAsync()
        {
            if (!HasApiKey)
                return null;

            _api.SetApiKey(State.ApiKey);
            var user = await _api.ValidateApiKeyAsync().ConfigureAwait(false);
            if (user != null)
            {
                ApplyUser(user);
                Save();
                LoginStatus = "Connected as " + user.Name;
            }
            else
            {
                LoginStatus = "Stored API key is invalid.";
            }

            return user;
        }

        public async Task<bool> SetManualApiKeyAsync(string apiKey)
        {
            State.ApiKey = apiKey ?? string.Empty;
            _api.SetApiKey(State.ApiKey);
            var user = await _api.ValidateApiKeyAsync().ConfigureAwait(false);
            if (user == null)
            {
                LoginStatus = "Invalid API key.";
                return false;
            }

            ApplyUser(user);
            Save();
            LoginStatus = "Connected as " + user.Name;
            return true;
        }

        public void ClearApiKey()
        {
            State = new NexusAuthState();
            _api.SetApiKey(string.Empty);
            LoginStatus = "API key cleared.";
            Save();
        }

        public async Task<string> StartBrowserLoginAsync()
        {
            CancelLogin();
            _connectionToken = null;
            string uuid = Guid.NewGuid().ToString();

            _loginCts = new CancellationTokenSource();
            _webSocket = new ClientWebSocket();
            LoginStatus = "Connecting to Nexus SSO...";
            IsLoginInProgress = true;

            try
            {
                await _webSocket.ConnectAsync(new Uri(SsoWebSocketUrl), _loginCts.Token).ConfigureAwait(false);
                string handshake = JsonSerializer.Serialize(new { id = uuid, token = _connectionToken, protocol = 2 });
                await SendAsync(handshake).ConfigureAwait(false);
                _ = ListenLoopAsync(_loginCts.Token);
                string url = "https://www.nexusmods.com/sso?id=" + uuid + "&application=" + ApplicationSlug;
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                LoginStatus = "Waiting for authorization in browser...";
                return url;
            }
            catch (Exception ex)
            {
                IsLoginInProgress = false;
                LoginStatus = "SSO connection failed: " + ex.Message;
                _log?.Warn("[Nexus] SSO start failed: " + ex.Message);
                return null;
            }
        }

        public void CancelLogin()
        {
            if (_loginCts != null)
            {
                try { _loginCts.Cancel(); } catch { }
                try { _loginCts.Dispose(); } catch { }
                _loginCts = null;
            }

            if (_webSocket != null)
            {
                try { _webSocket.Dispose(); } catch { }
                _webSocket = null;
            }

            IsLoginInProgress = false;
        }

        private async Task ListenLoopAsync(CancellationToken ct)
        {
            var buffer = new byte[4096];
            var builder = new StringBuilder();

            try
            {
                while (!ct.IsCancellationRequested && _webSocket != null && _webSocket.State == WebSocketState.Open)
                {
                    var result = await _webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), ct).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                        break;

                    builder.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                    if (!result.EndOfMessage)
                        continue;

                    string json = builder.ToString();
                    builder.Clear();
                    await HandleMessageAsync(json).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                LoginStatus = "SSO error: " + ex.Message;
                _log?.Warn("[Nexus] SSO listen failed: " + ex.Message);
            }
            finally
            {
                IsLoginInProgress = false;
            }
        }

        private async Task HandleMessageAsync(string json)
        {
            try
            {
                var response = JsonSerializer.Deserialize<SsoResponse>(json);
                if (response == null)
                    return;

                if (!response.Success)
                {
                    LoginStatus = response.Error ?? "SSO failed.";
                    IsLoginInProgress = false;
                    return;
                }

                if (!response.Data.HasValue)
                    return;

                JsonElement data = response.Data.Value;
                if (data.TryGetProperty("connection_token", out JsonElement tokenElement))
                {
                    _connectionToken = tokenElement.GetString();
                    return;
                }

                if (data.TryGetProperty("api_key", out JsonElement keyElement))
                {
                    string apiKey = keyElement.GetString();
                    if (!string.IsNullOrWhiteSpace(apiKey))
                    {
                        await SetManualApiKeyAsync(apiKey).ConfigureAwait(false);
                        IsLoginInProgress = false;
                    }
                }
            }
            catch (Exception ex)
            {
                LoginStatus = "Failed to parse Nexus SSO response: " + ex.Message;
            }
        }

        private async Task SendAsync(string message)
        {
            if (_webSocket == null || _webSocket.State != WebSocketState.Open)
                return;

            byte[] payload = Encoding.UTF8.GetBytes(message);
            await _webSocket.SendAsync(new ArraySegment<byte>(payload), WebSocketMessageType.Text, true, _loginCts.Token).ConfigureAwait(false);
        }

        private void ApplyUser(NexusUser user)
        {
            State.UserId = user.UserId;
            State.UserName = user.Name;
            State.IsPremium = user.IsPremium;
            State.IsSupporter = user.IsSupporter;
            State.ProfileUrl = user.ProfileUrl;
            State.LastValidatedUtc = DateTime.UtcNow;
        }

        private void Load()
        {
            try
            {
                if (!File.Exists(_authFilePath))
                    return;

                State = JsonSerializer.Deserialize<NexusAuthState>(File.ReadAllText(_authFilePath)) ?? new NexusAuthState();
                if (!string.IsNullOrWhiteSpace(State.ApiKey))
                    _api.SetApiKey(State.ApiKey);
            }
            catch (Exception ex)
            {
                _log?.Warn("[Nexus] Failed to load auth state: " + ex.Message);
                State = new NexusAuthState();
            }
        }

        private void Save()
        {
            try
            {
                File.WriteAllText(_authFilePath, JsonSerializer.Serialize(State, _jsonOptions));
            }
            catch (Exception ex)
            {
                _log?.Warn("[Nexus] Failed to save auth state: " + ex.Message);
            }
        }

        public void Dispose()
        {
            CancelLogin();
        }

        private sealed class SsoResponse
        {
            [JsonPropertyName("success")]
            public bool Success { get; set; }

            [JsonPropertyName("data")]
            public JsonElement? Data { get; set; }

            [JsonPropertyName("error")]
            public string Error { get; set; }
        }
    }
}
