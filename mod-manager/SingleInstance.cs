using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Threading;
using TerrariaModManager.Services;

namespace TerrariaModManager;

public class SingleInstance : IDisposable
{
    private const string MutexBaseName = "TerrariaModManager_SingleInstance";
    private const string PipeBaseName = "TerrariaModManager_Pipe";

    private readonly AppPaths _paths;
    private readonly string _mutexName;
    private readonly string _pipeName;
    private Mutex? _mutex;
    private FileStream? _lockFile;
    private CancellationTokenSource? _cts;
    private bool _isFirstInstance;

    public event Action<string>? MessageReceived;

    public SingleInstance(AppPaths paths)
    {
        _paths = paths;
        _mutexName = paths.SingleInstanceName(MutexBaseName);
        _pipeName = paths.SingleInstanceName(PipeBaseName);
    }

    public bool TryAcquire()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            _mutex = new Mutex(true, _mutexName, out _isFirstInstance);
        }
        else
        {
            // On Linux/macOS, Mutex is process-local. Use a lock file instead.
            var lockPath = Path.Combine(_paths.AppDataDirectory, ".instance.lock");
            Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
            try
            {
                _lockFile = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite,
                    FileShare.None, 1, FileOptions.DeleteOnClose);
                _isFirstInstance = true;
            }
            catch (IOException)
            {
                _isFirstInstance = false;
            }
        }
        return _isFirstInstance;
    }

    public void StartListening()
    {
        if (!_isFirstInstance) return;

        _cts = new CancellationTokenSource();
        Task.Run(() => ListenLoop(_cts.Token));
    }

    private async Task ListenLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(_pipeName,
                    PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

                await server.WaitForConnectionAsync(ct);

                using var reader = new StreamReader(server);
                var message = await reader.ReadToEndAsync(ct);

                if (!string.IsNullOrWhiteSpace(message))
                    MessageReceived?.Invoke(message);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // Brief delay before retrying on unexpected errors
                try { await Task.Delay(500, ct); } catch { break; }
            }
        }
    }

    public bool SendToExisting(string message)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.Out);
            client.Connect(2000); // 2 second timeout

            using var writer = new StreamWriter(client) { AutoFlush = true };
            writer.Write(message);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        if (_isFirstInstance)
            _mutex?.ReleaseMutex();
        _mutex?.Dispose();
        _lockFile?.Dispose();
    }
}
