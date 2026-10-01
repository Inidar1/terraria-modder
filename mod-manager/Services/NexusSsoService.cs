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
    private const int MaxReconnectAttempts = 3;

    private readonly object _sync = new();
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;
    private Task? _runner;
    private string? _connectionToken;
    private string? _loginId;
    private bool _completed;

    public event Action<string>? ApiKeyReceived;
    public event Action<string>? ErrorOccurred;

    public async Task<string> StartLoginAsync()
    {
        Cancel();
        _connectionToken = null;
        _loginId = Guid.NewGuid().ToString();
        _completed = false;
        _cts = new CancellationTokenSource();

        try
        {
            var first = await ConnectAsync(_cts.Token);
            lock (_sync) _ws = first;
            _runner = RunSessionAsync(first, _cts.Token);
            return $"https://www.nexusmods.com/sso?id={_loginId}&application={ApplicationSlug}";
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke($"SSO connection failed: {ex.Message}");
            Cancel();
            throw;
        }
    }

    private async Task<ClientWebSocket> ConnectAsync(CancellationToken cancellationToken)
    {
        var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(25);
        try
        {
            await socket.ConnectAsync(new Uri(SsoWebSocketUrl), cancellationToken);
            var handshake = JsonSerializer.Serialize(new
            {
                id = _loginId,
                token = _connectionToken,
                protocol = 2
            });
            var bytes = Encoding.UTF8.GetBytes(handshake);
            await socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private async Task RunSessionAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        var reconnectAttempt = 0;
        try
        {
            while (!cancellationToken.IsCancellationRequested && !_completed)
            {
                try
                {
                    await ListenUntilClosedAsync(socket, cancellationToken);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception) when (reconnectAttempt < MaxReconnectAttempts) { }

                if (cancellationToken.IsCancellationRequested || _completed) break;
                socket.Dispose();
                reconnectAttempt++;
                if (reconnectAttempt > MaxReconnectAttempts)
                    throw new WebSocketException("Nexus closed the SSO connection repeatedly.");
                await Task.Delay(TimeSpan.FromSeconds(reconnectAttempt), cancellationToken);
                socket = await ConnectAsync(cancellationToken);
                lock (_sync) _ws = socket;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!_completed && !cancellationToken.IsCancellationRequested)
                ErrorOccurred?.Invoke($"SSO connection ended: {ex.Message}");
        }
        finally
        {
            socket.Dispose();
        }
    }

    private async Task ListenUntilClosedAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        using var message = new MemoryStream();
        while (!cancellationToken.IsCancellationRequested && socket.State == WebSocketState.Open && !_completed)
        {
            var result = await socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close) return;
            if (result.MessageType != WebSocketMessageType.Text) continue;
            message.Write(buffer, 0, result.Count);
            if (message.Length > 64 * 1024)
                throw new InvalidDataException("Nexus SSO response exceeded 64KB.");
            if (!result.EndOfMessage) continue;

            HandleMessage(Encoding.UTF8.GetString(message.ToArray()));
            message.SetLength(0);
        }
    }

    internal void HandleMessage(string json)
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
            if (data.TryGetProperty("connection_token", out var tokenElement))
            {
                _connectionToken = tokenElement.GetString();
                return;
            }
            if (data.TryGetProperty("api_key", out var keyElement))
            {
                var apiKey = keyElement.GetString();
                if (string.IsNullOrWhiteSpace(apiKey)) return;
                _completed = true;
                ApiKeyReceived?.Invoke(apiKey);
            }
        }
        catch (JsonException ex)
        {
            ErrorOccurred?.Invoke($"Failed to parse SSO response: {ex.Message}");
        }
    }

    public void Cancel()
    {
        CancellationTokenSource? cancellation;
        ClientWebSocket? socket;
        lock (_sync)
        {
            cancellation = _cts;
            socket = _ws;
            _cts = null;
            _ws = null;
        }
        try { cancellation?.Cancel(); } catch { }
        try { socket?.Abort(); } catch { }
        cancellation?.Dispose();
    }

    public void Dispose()
    {
        Cancel();
        _runner = null;
    }
}
