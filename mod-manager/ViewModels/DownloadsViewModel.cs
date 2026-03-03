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

    public int ModId { get; set; }
    public int FileId { get; set; }

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
            }
        }
    }

    public bool IsInstalled
    {
        get => _isInstalled;
        set
        {
            if (SetProperty(ref _isInstalled, value))
                OnPropertyChanged(nameof(IsDownloading));
        }
    }

    public bool IsDownloading => !HasError && !IsInstalled;
    public bool IsFailed => HasError;

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
                OnPropertyChanged(nameof(ProgressText));
        }
    }

    public string ProgressText => TotalBytes > 0
        ? $"{DownloadedBytes / 1024.0 / 1024.0:F1} / {TotalBytes / 1024.0 / 1024.0:F1} MB"
        : $"{DownloadedBytes / 1024.0 / 1024.0:F1} MB";
}

public class DownloadsViewModel : ViewModelBase
{
    private readonly DownloadManager _downloadManager;

    public ObservableCollection<DownloadItem> Downloads => _downloadManager.Downloads;

    public ICommand OpenOnNexusCommand { get; }

    public DownloadsViewModel(DownloadManager downloadManager)
    {
        _downloadManager = downloadManager;
        OpenOnNexusCommand = new RelayCommand<DownloadItem>(OpenOnNexus);
    }

    private void OpenOnNexus(DownloadItem? item)
    {
        if (item == null || item.ModId <= 0) return;
        var url = $"https://www.nexusmods.com/terraria/mods/{item.ModId}";
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
    }
}
