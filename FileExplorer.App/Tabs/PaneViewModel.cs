using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using FileExplorer.App.Shell;
using FileExplorer.Core.Common;
using FileExplorer.Core.Fs;
using FileExplorer.Core.Models;

namespace FileExplorer.App.Tabs;

/// <summary>One folder pane: own path, history, selection, sort and watcher. Knows nothing about its tab or sibling pane.</summary>
public sealed partial class PaneViewModel : ObservableObject, IDisposable
{
    private readonly IFileSource _source;
    private readonly ErrorReporter _errors;
    private readonly IIconProvider _icons;
    private readonly Action<Action> _post;
    private readonly DirectoryWatcher? _watcher;
    private readonly Stack<string> _back = new();
    private readonly Stack<string> _forward = new();
    private List<FileEntry> _entries = new();

    [ObservableProperty] private string _path = "";
    [ObservableProperty] private ObservableCollection<FileEntryViewModel> _items = new();
    [ObservableProperty] private SortColumn _sortColumn = SortColumn.Name;
    [ObservableProperty] private bool _sortAscending = true;
    [ObservableProperty] private bool _showHidden;
    [ObservableProperty] private bool _isActive;

    public PaneViewModel(IFileSource source, ErrorReporter errors, IIconProvider icons, Action<Action> post, bool watch = true)
    {
        _source = source; _errors = errors; _icons = icons; _post = post;
        if (!watch) return;
        _watcher = new DirectoryWatcher(TimeSpan.FromMilliseconds(250));
        _watcher.Changed += () => _post(() => Refresh());
    }

    public IReadOnlyList<FileEntryViewModel> Selection { get; private set; } = Array.Empty<FileEntryViewModel>();
    public event Action? SelectionChanged;
    public event Action? Navigated;

    public bool CanGoBack => _back.Count > 0;
    public bool CanGoForward => _forward.Count > 0;
    public bool CanGoUp => Path.Length > 0;
    public bool IsThisPc => Path.Length == 0;

    /// <summary>Folder name shown on a tab.</summary>
    public string Title => IsThisPc ? "This PC" : PathUtil.FileName(Path);

    public long SelectionBytes => Selection.Where(s => !s.IsDir).Sum(s => s.Entry.Size);
    public int ItemCount => Items.Count;

    // ---- navigation ----
    public Result<Unit> Navigate(string path) => NavigateCore(path, record: true);

    /// <summary>Opens a start location; if it cannot be opened the error is reported and the pane shows This PC.</summary>
    public void Open(string path)
    {
        if (!Navigate(path).IsOk) Navigate("");
    }

    public Result<Unit> Back() => Step(_back, _forward);

    public Result<Unit> Forward() => Step(_forward, _back);

    public Result<Unit> Up() => !CanGoUp ? Result.Ok() : Navigate(PathUtil.Parent(Path) ?? "");

    public Result<Unit> Refresh()
    {
        var r = Load(Path);
        if (!r.IsOk) return Result.Err(r.Error);
        Apply(r.Value, Path);
        return Result.Ok();
    }

    private Result<Unit> Step(Stack<string> from, Stack<string> to)
    {
        if (from.Count == 0) return Result.Ok();
        var target = from.Peek();
        var r = Load(target);
        if (!r.IsOk) return Result.Err(r.Error);
        from.Pop();
        to.Push(Path);
        Apply(r.Value, target);
        return Result.Ok();
    }

    private Result<Unit> NavigateCore(string path, bool record)
    {
        var target = path.Length == 0 ? "" : PathUtil.Normalize(path);
        var r = Load(target);
        if (!r.IsOk) return Result.Err(r.Error);
        if (record && target != Path) { _back.Push(Path); _forward.Clear(); }
        Apply(r.Value, target);
        return Result.Ok();
    }

    /// <summary>Reads the folder without touching pane state; a failure is reported and leaves the pane as it was.</summary>
    private Result<List<FileEntry>> Load(string target)
    {
        var r = target.Length == 0 ? DriveLister.ListDrives() : _source.EnumerateDirectory(target);
        if (!r.IsOk) { _errors.Report(r.Error); return Result<List<FileEntry>>.Err(r.Error); }
        return Result<List<FileEntry>>.Ok(r.Value.ToList());
    }

    private void Apply(List<FileEntry> entries, string target)
    {
        _entries = entries;
        bool pathChanged = target != Path;
        Path = target;
        Rebuild();
        var watchError = _watcher?.Watch(target);
        if (watchError is not null) _errors.Report(new AppError(watchError, 0, target));
        if (pathChanged) Navigated?.Invoke();
        NotifyNavState();
    }

    private void NotifyNavState()
    {
        OnPropertyChanged(nameof(CanGoBack)); OnPropertyChanged(nameof(CanGoForward));
        OnPropertyChanged(nameof(CanGoUp)); OnPropertyChanged(nameof(IsThisPc)); OnPropertyChanged(nameof(Title));
    }

    // ---- sort / filter ----
    public void SortBy(SortColumn column)
    {
        if (column == SortColumn) SortAscending = !SortAscending;
        else { SortColumn = column; SortAscending = true; }
        Rebuild();
    }

    partial void OnShowHiddenChanged(bool value) => Rebuild();

    private void Rebuild()
    {
        bool drives = IsThisPc;
        var visible = _entries.Where(e => drives || ShowHidden || !e.IsHidden);
        var sorted = drives ? visible.ToList() : EntrySorter.Sort(visible, SortColumn, SortAscending);
        Items = new ObservableCollection<FileEntryViewModel>(sorted.Select(e => new FileEntryViewModel(e, _icons, drives)));
        SetSelection(Array.Empty<FileEntryViewModel>());
        OnPropertyChanged(nameof(ItemCount));
    }

    // ---- selection ----
    public void SetSelection(IReadOnlyList<FileEntryViewModel> selection)
    {
        Selection = selection;
        OnPropertyChanged(nameof(Selection)); OnPropertyChanged(nameof(SelectionBytes));
        SelectionChanged?.Invoke();
    }

    public IReadOnlyList<string> SelectedPaths => Selection.Select(s => s.FullPath).ToList();

    public void Dispose() => _watcher?.Dispose();
}
