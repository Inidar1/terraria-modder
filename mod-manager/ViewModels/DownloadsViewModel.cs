using System.Collections.ObjectModel;
using System.Windows.Input;
using TerrariaModManager.Helpers;
using TerrariaModManager.Services;

namespace TerrariaModManager.ViewModels;

public class DownloadItem : ViewModelBase
{
    private string _name = "";
    private string _status = "Pending";
    private string _errorMessage = "";
    private bool _hasError;
    private bool _isInstalled;
    private double _progress;
    private long _totalBytes;
    private long _downloadedBytes;
    private string _speedText = "";
    private string _etaText = "";

    private readonly Queue<(DateTime time, long bytes)> _speedSamples = new();
    private const int SpeedWindowSeconds = 5;

    public int ModId { get; set; }
    public int FileId { get; set; }

    // Stored for retry after failure (key/expires may be expired but ModId+FileId allow a fresh attempt)
    public string? RetryKey { get; set; }
    public long? RetryExpires { get; set; }

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    public string Status
    {
        get => _status;
        set => SetProperty(ref _status, value);
    }

    public string ErrorMessage
    {
        get => _errorMessage;
        set => SetProperty(ref _errorMessage, value);
    }

    public bool HasError
    {
        get => _hasError;
        set
        {
            if (SetProperty(ref _hasError, value))
            {
                OnPropertyChanged(nameof(IsDownloading));
                OnPropertyChanged(nameof(IsFailed));
                OnPropertyChanged(nameof(IsDone));
            }
        }
    }

    public bool IsInstalled
    {
        get => _isInstalled;
        set
        {
            if (SetProperty(ref _isInstalled, value))
            {
                OnPropertyChanged(nameof(IsDownloading));
                OnPropertyChanged(nameof(IsDone));
            }
        }
    }

    public bool IsDownloading => !HasError && !IsInstalled;
    public bool IsFailed => HasError;
    public bool IsDone => IsInstalled || HasError;

    public double Progress
    {
        get => _progress;
        set => SetProperty(ref _progress, value);
    }

    public long TotalBytes
    {
        get => _totalBytes;
        set
        {
            if (SetProperty(ref _totalBytes, value))
                OnPropertyChanged(nameof(ProgressText));
        }
    }

    public long DownloadedBytes
    {
        get => _downloadedBytes;
        set
        {
            if (SetProperty(ref _downloadedBytes, value))
            {
                OnPropertyChanged(nameof(ProgressText));
                UpdateSpeed(value);
            }
        }
    }

    public string ProgressText => TotalBytes > 0
        ? $"{DownloadedBytes / 1024.0 / 1024.0:F1} / {TotalBytes / 1024.0 / 1024.0:F1} MB"
        : $"{DownloadedBytes / 1024.0 / 1024.0:F1} MB";

    public string SpeedText
    {
        get => _speedText;
        private set => SetProperty(ref _speedText, value);
    }

    public string EtaText
    {
        get => _etaText;
        private set => SetProperty(ref _etaText, value);
    }

    private void UpdateSpeed(long currentBytes)
    {
        if (!IsDownloading || currentBytes <= 0) return;

        var now = DateTime.UtcNow;
        _speedSamples.Enqueue((now, currentBytes));

        // Keep only samples within the rolling window
        while (_speedSamples.Count > 1 && (now - _speedSamples.Peek().time).TotalSeconds > SpeedWindowSeconds)
            _speedSamples.Dequeue();

        if (_speedSamples.Count < 2)
        {
            SpeedText = "";
            EtaText = "";
            return;
        }

        var oldest = _speedSamples.Peek();
        var elapsed = (now - oldest.time).TotalSeconds;
        if (elapsed <= 0) { SpeedText = ""; EtaText = ""; return; }

        var bytesPerSec = (currentBytes - oldest.bytes) / elapsed;
        if (bytesPerSec <= 0) { SpeedText = ""; EtaText = ""; return; }

        SpeedText = bytesPerSec >= 1_000_000
            ? $"{bytesPerSec / 1_000_000:F1} MB/s"
            : $"{bytesPerSec / 1000:F0} KB/s";

        if (TotalBytes > 0 && currentBytes < TotalBytes)
        {
            var remainingBytes = TotalBytes - currentBytes;
            var etaSec = remainingBytes / bytesPerSec;
            EtaText = etaSec >= 60
                ? $"{(int)(etaSec / 60)}m {(int)(etaSec % 60)}s left"
                : $"{(int)etaSec}s left";
        }
        else
        {
            EtaText = "";
        }
    }

    public void ClearSpeedSamples()
    {
        _speedSamples.Clear();
        SpeedText = "";
        EtaText = "";
    }
}

public class DownloadsViewModel : ViewModelBase
{
    private readonly DownloadManager _downloadManager;

    public ObservableCollection<DownloadItem> Downloads => _downloadManager.Downloads;

    public ICommand OpenOnNexusCommand { get; }
    public ICommand RetryCommand { get; }
    public ICommand DismissCommand { get; }
    public ICommand ClearCompletedCommand { get; }

    public DownloadsViewModel(DownloadManager downloadManager)
    {
        _downloadManager = downloadManager;
        OpenOnNexusCommand = new RelayCommand<DownloadItem>(OpenOnNexus);
        RetryCommand = new AsyncRelayCommand<DownloadItem>(RetryDownload);
        DismissCommand = new RelayCommand<DownloadItem>(Dismiss);
        ClearCompletedCommand = new RelayCommand(ClearCompleted);
    }

    private void OpenOnNexus(DownloadItem? item)
    {
        if (item == null || item.ModId <= 0) return;
        var url = $"https://www.nexusmods.com/terraria/mods/{item.ModId}";
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
    }

    private async Task RetryDownload(DownloadItem? item)
    {
        if (item == null || !item.HasError) return;
        var modId = item.ModId;
        var fileId = item.FileId;
        var key = item.RetryKey;
        var expires = item.RetryExpires;
        _downloadManager.Remove(item);
        await _downloadManager.EnqueueAsync(modId, fileId, key, expires);
    }

    private void Dismiss(DownloadItem? item)
    {
        if (item == null) return;
        _downloadManager.Remove(item);
    }

    private void ClearCompleted()
    {
        var done = Downloads.Where(d => d.IsDone).ToList();
        foreach (var item in done)
            _downloadManager.Remove(item);
    }
}
