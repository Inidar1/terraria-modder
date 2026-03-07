using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TerrariaModManager.Services;

public class SsoResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("data")]
    public JsonElement? Data { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }
}

public class NexusSsoService : IDisposable
{
    private const string SsoWebSocketUrl = "wss://sso.nexusmods.com";

    private const string ApplicationSlug = "inidar-terrariamodder";

    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;
    private string? _connectionToken;

    public event Action<string>? ApiKeyReceived;
    public event Action<string>? ErrorOccurred;

    public async Task<string> StartLoginAsync()
    {
        _connectionToken = null;
        var uuid = Guid.NewGuid().ToString();

        _cts = new CancellationTokenSource();
        _ws = new ClientWebSocket();

        try
        {
            await _ws.ConnectAsync(new Uri(SsoWebSocketUrl), _cts.Token);

            // Send handshake
            var handshake = JsonSerializer.Serialize(new
            {
                id = uuid,
                token = _connectionToken,
                protocol = 2
            });
            await SendAsync(handshake);

            // Start listening for responses in background
            _ = ListenLoopAsync(_cts.Token);

            // Return the browser URL for the user to authorize
            return $"https://www.nexusmods.com/sso?id={uuid}&application={ApplicationSlug}";
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke($"SSO connection failed: {ex.Message}");
            throw;
        }
    }

    private async Task ListenLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[4096];
        var messageBuffer = new StringBuilder();

        try
        {
            while (!ct.IsCancellationRequested && _ws?.State == WebSocketState.Open)
            {
                var result = await _ws.ReceiveAsync(buffer, ct);

                if (result.MessageType == WebSocketMessageType.Close)
                    break;

                messageBuffer.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));

                if (result.EndOfMessage)
                {
                    var json = messageBuffer.ToString();
                    messageBuffer.Clear();
                    HandleMessage(json);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke($"SSO error: {ex.Message}");
        }
    }

    private void HandleMessage(string json)
    {
        try
        {
            var response = JsonSerializer.Deserialize<SsoResponse>(json);
            if (response == null) return;

            if (!response.Success)
            {
                ErrorOccurred?.Invoke(response.Error ?? "SSO failed");
                return;
            }

            if (response.Data == null) return;

            var data = response.Data.Value;

            // Check if this is a connection token response
            if (data.TryGetProperty("connection_token", out var tokenEl))
            {
                _connectionToken = tokenEl.GetString();
                return;
            }

            // Check if this is an API key response
            if (data.TryGetProperty("api_key", out var keyEl))
            {
                var apiKey = keyEl.GetString();
                if (!string.IsNullOrEmpty(apiKey))
                    ApiKeyReceived?.Invoke(apiKey);
                return;
            }
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke($"Failed to parse SSO response: {ex.Message}");
        }
    }

    private async Task SendAsync(string message)
    {
        if (_ws?.State != WebSocketState.Open) return;
        var bytes = Encoding.UTF8.GetBytes(message);
        await _ws.SendAsync(bytes, WebSocketMessageType.Text, true, _cts?.Token ?? default);
    }

    public void Cancel()
    {
        _cts?.Cancel();
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _ws?.Dispose();
    }
}
