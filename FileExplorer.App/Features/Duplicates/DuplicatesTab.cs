using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FileExplorer.App.Features.Tools;
using FileExplorer.App.Tabs;
using FileExplorer.Core.Common;
using FileExplorer.Core.Duplicates;
using FileExplorer.Core.Fs;
using FileExplorer.Core.Hashing;
using FileExplorer.Core.Reports;

namespace FileExplorer.App.Features.Duplicates;

public sealed class DupFileViewModel : ObservableObject
{
    private readonly DupGroupViewModel _group;
    private bool _isMarked;

    public DupFileViewModel(DupGroupViewModel group, DupCandidate file) { _group = group; File = file; }
    public DupCandidate File { get; }
    public string Name => Path.GetFileName(File.Path);
    public string Folder => Path.GetDirectoryName(File.Path) ?? "";
    public string ModifiedText => File.Modified == DateTime.MinValue ? "" : DateFormatter.Format(File.Modified);

    /// <summary>Marking the last unmarked copy of a group is refused (at least one copy always stays).</summary>
    public bool IsMarked
    {
        get => _isMarked;
        set
        {
            if (value && !_isMarked && _group.WouldMarkEverything(this)) { _group.Refused?.Invoke(); OnPropertyChanged(); return; }
            if (SetProperty(ref _isMarked, value)) _group.MarkChanged?.Invoke();
        }
    }

    internal void SetMarkedQuietly(bool v) { _isMarked = v; OnPropertyChanged(nameof(IsMarked)); }
}

public sealed class DupGroupViewModel
{
    public DupGroupViewModel(DuplicateGroup group)
    {
        Group = group;
        Files = new ObservableCollection<DupFileViewModel>(group.Files.Select(f => new DupFileViewModel(this, f)));
        Header = $"{group.Files.Count} copies · {SizeFormatter.Format(group.Size)} each · {SizeFormatter.Format(group.WastedBytes)} wasted";
    }

    public DuplicateGroup Group { get; }
    public string Header { get; }
    public ObservableCollection<DupFileViewModel> Files { get; }
    public Action? Refused { get; set; }
    public Action? MarkChanged { get; set; }

    public bool WouldMarkEverything(DupFileViewModel candidate) => Files.All(f => f == candidate || f.IsMarked);
    public IEnumerable<string> MarkedPaths => Files.Where(f => f.IsMarked).Select(f => f.File.Path);

    public void MarkAllButNewest()
    {
        var keep = DuplicateSelection.AllButOne(Group, keepNewest: true).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var f in Files) f.SetMarkedQuietly(keep.Contains(f.File.Path));
    }
}

public sealed partial class DuplicatesTabViewModel : TabViewModelBase
{
    public const string Type = "duplicates";
    private readonly ToolServices _s;
    private readonly DupFinder _finder = new(new FileHasher());
    private CancellationTokenSource? _cts;
    private IReadOnlyList<AppError> _skipped = Array.Empty<AppError>();

    [ObservableProperty] private string _rootPath;
    [ObservableProperty] private long _minSizeKb = 1;
    [ObservableProperty] private bool _includeEmpty;
    [ObservableProperty] private bool _running;
    [ObservableProperty] private string _progressText = "";
    [ObservableProperty] private string _summary = "Choose a folder and press Find.";
    [ObservableProperty] private string _markedText = "";

    public DuplicatesTabViewModel(ToolServices services, string rootPath) { _s = services; _rootPath = rootPath; }

    public ObservableCollection<DupGroupViewModel> Groups { get; } = new();
    public override string TypeId => Type;
    public override string Title => "Duplicates";

    [RelayCommand]
    private void ChooseFolder()
    {
        var p = _s.Prompt.Ask("Duplicate finder", "Folder or drive to search", RootPath);
        if (!string.IsNullOrWhiteSpace(p)) { RootPath = PathUtil.Normalize(p); Find(); }
    }

    [RelayCommand]
    private void Find()
    {
        if (RootPath.Length == 0) return;
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        Running = true; Groups.Clear(); Summary = ""; ProgressText = "Scanning folders…";
        var root = RootPath; long minSize = Math.Max(0, MinSizeKb) * 1024; bool empty = IncludeEmpty;
        var progress = new Progress<Progress>(p => ProgressText = p.Total > 0 ? $"Hashing… {p.Done:N0} of {p.Total:N0} candidate files" : $"Scanning… {p.Done:N0} items");
        _ = Task.Run(() =>
        {
            var built = new EnumeratedIndexSource(_s.Files).Build(root, progress, cts.Token);
            if (!built.IsOk) return Result<DupResult>.Err(built.Error);
            return _finder.Find(DupFinder.CandidatesFrom(built.Value), minSize, empty, progress, cts.Token);
        }).ContinueWith(t => _s.Post(() => Finish(t.Result, cts)));
    }

    private void Finish(Result<DupResult> r, CancellationTokenSource cts)
    {
        if (cts != _cts) return;
        Running = false; ProgressText = "";
        if (!r.IsOk) { _s.Errors.Report(r.Error); Summary = r.Error.Message; return; }
        _skipped = r.Value.Skipped;
        foreach (var g in r.Value.Groups)
        {
            var vm = new DupGroupViewModel(g) { Refused = () => _s.Errors.Report(new AppError("Keep at least one copy in each group.")), MarkChanged = UpdateMarked };
            Groups.Add(vm);
        }
        long wasted = r.Value.Groups.Sum(g => g.WastedBytes);
        Summary = $"{Groups.Count:N0} groups · {SizeFormatter.Format(wasted)} could be freed" + (_skipped.Count > 0 ? $" · {_skipped.Count} files could not be read" : "");
        UpdateMarked();
    }

    [RelayCommand] private void Cancel() { _cts?.Cancel(); Running = false; ProgressText = "Cancelled"; }

    [RelayCommand]
    private void AutoSelect()
    {
        foreach (var g in Groups) g.MarkAllButNewest();
        UpdateMarked();
    }

    [RelayCommand]
    private void ClearSelection()
    {
        foreach (var g in Groups) foreach (var f in g.Files) f.SetMarkedQuietly(false);
        UpdateMarked();
    }

    private void UpdateMarked()
    {
        int n = Groups.Sum(g => g.MarkedPaths.Count());
        long bytes = Groups.Sum(g => g.MarkedPaths.Count() * g.Group.Size);
        MarkedText = n == 0 ? "" : $"{n:N0} files marked · {SizeFormatter.Format(bytes)}";
    }

    [RelayCommand]
    private void DeleteMarked()
    {
        var all = Groups.SelectMany(g => g.MarkedPaths).ToList();
        if (all.Count == 0) return;
        foreach (var g in Groups)
        {
            var ok = DuplicateSelection.Validate(g.Group, g.MarkedPaths.ToList());
            if (!ok.IsOk) { _s.Errors.Report(ok.Error); return; }
        }
        if (System.Windows.MessageBox.Show($"Move {all.Count:N0} files to the Recycle Bin?", "Delete duplicates", System.Windows.MessageBoxButton.OKCancel) != System.Windows.MessageBoxResult.OK) return;
        var r = _s.Ops.Delete(all);
        if (!_s.Errors.Check(r)) return;
        if (r.Value.Undo is { } u) _s.Undo.Push(u);
        var gone = all.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var g in Groups.ToList())
        {
            foreach (var f in g.Files.Where(f => gone.Contains(f.File.Path) && !File.Exists(f.File.Path)).ToList()) g.Files.Remove(f);
            if (g.Files.Count < 2) Groups.Remove(g);
        }
        UpdateMarked();
        Summary = $"{Groups.Count:N0} groups left";
    }

    [RelayCommand]
    private void ExportCsv()
    {
        var path = ToolServices.AskCsvPath("duplicates.csv");
        if (path is null) return;
        var rows = Groups.SelectMany((g, i) => g.Group.Files.Select(f => (IRow)new SimpleRow((i + 1).ToString(), g.Group.Size.ToString(), g.Group.Hash, f.Path, f.Modified.ToString("yyyy-MM-dd HH:mm"))));
        _s.Errors.Check(CsvExporter.Export(path, new[] { "Group", "Size (bytes)", "Hash", "Path", "Modified" }, rows));
    }

    [RelayCommand] private void ShowSkipped() =>
        _s.Errors.Report(new AppError(_skipped.Count == 0 ? "Every candidate file was readable." : "Not read: " + string.Join(" | ", _skipped.Take(5).Select(e => e.ToString()))));
    [RelayCommand] private void Reveal(DupFileViewModel? f) { if (f is not null) _s.Errors.Check(_s.Shell.Reveal(f.File.Path)); }
    [RelayCommand] private void Open(DupFileViewModel? f) { if (f is not null) _s.Errors.Check(_s.Shell.Open(f.File.Path)); }

    public override TabState Save() => new(Type, JsonSerializer.Serialize(RootPath));
    public override ITabViewModel Duplicate() => new DuplicatesTabViewModel(_s, RootPath);
    public override void Dispose() { _cts?.Cancel(); base.Dispose(); }
}
