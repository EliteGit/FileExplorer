namespace FileExplorer.App.Tabs;

/// <summary>Watches one folder and raises a single debounced Changed event per burst of changes.</summary>
public sealed class DirectoryWatcher : IDisposable
{
    private readonly TimeSpan _debounce;
    private readonly Timer _timer;
    private FileSystemWatcher? _watcher;

    public DirectoryWatcher(TimeSpan debounce)
    {
        _debounce = debounce;
        _timer = new Timer(_ => Changed?.Invoke(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>Raised on a thread-pool thread; the owner marshals to the UI thread.</summary>
    public event Action? Changed;

    /// <summary>Starts watching <paramref name="path"/> (empty path = nothing to watch). Returns an error text, or null.</summary>
    public string? Watch(string path)
    {
        Stop();
        if (path.Length == 0) return null;
        try
        {
            _watcher = new FileSystemWatcher(path) { NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size | NotifyFilters.LastWrite | NotifyFilters.Attributes };
            _watcher.Created += OnEvent; _watcher.Deleted += OnEvent; _watcher.Changed += OnEvent; _watcher.Renamed += OnEvent;
            _watcher.EnableRaisingEvents = true;
            return null;
        }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException)
        {
            _watcher = null;
            return $"Live updates unavailable for this folder: {e.Message}";
        }
    }

    private void OnEvent(object sender, FileSystemEventArgs e) => _timer.Change(_debounce, Timeout.InfiniteTimeSpan);

    private void Stop()
    {
        if (_watcher is null) return;
        _watcher.EnableRaisingEvents = false;
        _watcher.Dispose();
        _watcher = null;
    }

    public void Dispose()
    {
        Stop();
        _timer.Dispose();
    }
}
