using FileExplorer.Core.Common;
using FileExplorer.Core.Fs;

namespace FileExplorer.Core.Index;

/// <summary>What the UI shows for one volume (mode is explicit, never hidden).</summary>
public sealed class VolumeIndexState
{
    public required string Volume { get; init; }
    public FileIndex? Index { get; set; }
    public bool Building { get; set; }
    public long Progress { get; set; }
    public bool FromCache { get; set; }
    public bool Live { get; set; }
    /// <summary>True while sizes and dates are being read in the background (MFT mode).</summary>
    public bool Filling => Index?.Metadata == MetadataState.Filling;
    public AppError? Error { get; set; }
}

/// <summary>Owns the per-volume indexes: cache load, USN catch-up or rebuild, live updates.</summary>
public sealed class IndexService : IDisposable
{
    private readonly IFileSource _files;
    private readonly IndexCache _cache;
    private readonly Func<bool> _isElevated;
    private readonly Dictionary<string, VolumeIndexState> _states = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, UsnWatcher> _watchers = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private readonly CancellationTokenSource _fillCts = new();
    private readonly MetadataFiller _filler;

    public IndexService(IFileSource files, IndexCache cache, Func<bool> isElevated)
    {
        _files = files; _cache = cache; _isElevated = isElevated; _filler = new MetadataFiller(files);
    }

    public event Action? Changed;
    public event Action<AppError>? ErrorRaised;

    public bool IsElevated => _isElevated();
    public IReadOnlyList<VolumeIndexState> Volumes { get { lock (_gate) return _states.Values.ToList(); } }
    public IReadOnlyList<FileIndex> Indexes { get { lock (_gate) return _states.Values.Select(s => s.Index).OfType<FileIndex>().ToList(); } }
    public bool AnyBuilding { get { lock (_gate) return _states.Values.Any(s => s.Building); } }
    public bool AnyFilling { get { lock (_gate) return _states.Values.Any(s => s.Filling); } }

    public static IReadOnlyList<string> FixedVolumes() =>
        DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady).Select(d => d.Name).ToList();

    public static bool IsNtfs(string volume) =>
        string.Equals(new DriveInfo(volume).DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase);

    /// <summary>The mode this process can use for a volume: MFT only when elevated and the volume is NTFS.</summary>
    public IndexMode PreferredMode(string volume) => IsElevated && IsNtfs(volume) ? IndexMode.Mft : IndexMode.Enumerated;

    private VolumeIndexState State(string volume)
    {
        var key = PathUtil.Normalize(volume);
        lock (_gate)
        {
            if (!_states.TryGetValue(key, out var s)) _states[key] = s = new VolumeIndexState { Volume = key };
            return s;
        }
    }

    private void Raise() => Changed?.Invoke();

    private void RaiseError(VolumeIndexState s, AppError e)
    {
        s.Error = e;
        ErrorRaised?.Invoke(e);
    }

    // ---- start-up: cheap, from cache only ----
    public void LoadCaches(IEnumerable<string> volumes)
    {
        foreach (var v in volumes)
        {
            var s = State(v);
            if (!_cache.Exists(v)) continue;
            var loaded = _cache.Load(v);
            if (!loaded.IsOk) { RaiseError(s, loaded.Error); continue; }
            s.Index = loaded.Value; s.FromCache = true;
            if (loaded.Value.Mode == IndexMode.Mft) { TryCatchUp(s); StartFill(s); }
        }
        Raise();
    }

    /// <summary>A cached MFT index is made current by replaying the journal; if that is impossible the caller must rebuild.</summary>
    private void TryCatchUp(VolumeIndexState s)
    {
        if (!IsElevated) return;   // cannot read the journal: the cache stays visible but is marked as not live
        var w = UsnWatcher.Create(s.Index!);
        if (!w.IsOk) { RaiseError(s, w.Error); return; }
        var caught = w.Value.CatchUp();
        if (!caught.IsOk) { w.Value.Dispose(); s.Index = null; RaiseError(s, caught.Error); return; }
        AttachWatcher(s, w.Value);
        s.FromCache = false;
    }

    private void AttachWatcher(VolumeIndexState s, UsnWatcher w)
    {
        w.Changed += Raise;
        w.Failed += e => { s.Live = false; RaiseError(s, e); Raise(); };
        lock (_gate) { if (_watchers.Remove(s.Volume, out var old)) old.Dispose(); _watchers[s.Volume] = w; }
        w.Start();
        s.Live = true;
    }

    // ---- full build ----
    public Task BuildAsync(string volume, IndexMode mode, CancellationToken ct = default) => Task.Run(() => Build(volume, mode, ct));

    public Task BuildAllAsync(CancellationToken ct = default) =>
        Task.Run(() => { foreach (var v in FixedVolumes()) Build(v, PreferredMode(v), ct); });

    private void Build(string volume, IndexMode mode, CancellationToken ct)
    {
        var s = State(volume);
        s.Building = true; s.Error = null; s.Progress = 0; Raise();
        var progress = new Progress<Progress>(p => { s.Progress = p.Done; });
        IIndexSource source = mode == IndexMode.Mft ? new MftIndexSource() : new EnumeratedIndexSource(_files);
        var built = SafeBuild(source, volume, progress, ct);
        if (!built.IsOk && mode == IndexMode.Mft)
        {
            // Visible fallback: the error is raised to the user, then the enumerated index is built instead.
            RaiseError(s, built.Error);
            built = SafeBuild(new EnumeratedIndexSource(_files), volume, progress, ct);
        }
        if (built.IsOk) Publish(s, built.Value);
        else RaiseError(s, built.Error);
        s.Building = false; Raise();
    }

    private static Result<FileIndex> SafeBuild(IIndexSource source, string volume, IProgress<Progress> progress, CancellationToken ct)
    {
        try { return source.Build(volume, progress, ct); }
        catch (OperationCanceledException) { return new AppError("Indexing was cancelled.", 0, volume); }
    }

    private void Publish(VolumeIndexState s, FileIndex index)
    {
        s.Index = index; s.FromCache = false; s.Live = false;
        var saved = _cache.Save(index);
        if (!saved.IsOk) RaiseError(s, saved.Error);
        if (index.Mode != IndexMode.Mft) return;
        var w = UsnWatcher.Create(index);
        if (w.IsOk) AttachWatcher(s, w.Value); else RaiseError(s, w.Error);
        StartFill(s);
    }

    /// <summary>MFT indexes have names only; read sizes and dates in the background, then save so the next start already has them.</summary>
    private void StartFill(VolumeIndexState s)
    {
        var index = s.Index;
        if (index is null || index.Mode != IndexMode.Mft || index.HasMetadata || index.Metadata == MetadataState.Filling) return;
        index.BeginMetadataFill();
        var last = DateTime.MinValue;
        var progress = new Progress<double>(_ =>
        {
            if (DateTime.UtcNow - last < TimeSpan.FromMilliseconds(500)) return;
            last = DateTime.UtcNow; Raise();
        });
        _ = Task.Run(() =>
        {
            var r = _filler.Fill(index, _fillCts.Token, progress);
            if (r.IsOk)
            {
                if (r.Value.Unreadable > 0) index.AddWarning(new AppError($"{r.Value.Unreadable} folders could not be read, so their files have no size or date."));
                var saved = _cache.Save(index);
                if (!saved.IsOk) RaiseError(s, saved.Error);
            }
            else RaiseError(s, r.Error);
            Raise();
        });
        Raise();
    }

    /// <summary>Writes the current indexes so the next start is instant.</summary>
    public void SaveAll()
    {
        foreach (var i in Indexes) _cache.Save(i);
    }

    public void Dispose()
    {
        _fillCts.Cancel();
        lock (_gate) { foreach (var w in _watchers.Values) w.Dispose(); _watchers.Clear(); }
    }
}
