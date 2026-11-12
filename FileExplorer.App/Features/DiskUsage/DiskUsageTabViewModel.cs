using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FileExplorer.App.Shell;
using FileExplorer.App.Tabs;
using FileExplorer.Core.Common;
using FileExplorer.Core.Fs;
using FileExplorer.Core.Ops;
using FileExplorer.Core.Scan;
using FileExplorer.Core.Shell;

namespace FileExplorer.App.Features.DiskUsage;

public sealed class DiskUsageServices
{
    public DiskUsageServices(IFileSource files, ShellActions shell, IFileOps ops, UndoStack undo, ErrorReporter errors, IPrompt prompt, Action<Action> post)
    { Files = files; Shell = shell; Ops = ops; Undo = undo; Errors = errors; Prompt = prompt; Post = post; }
    public IFileSource Files { get; }
    public ShellActions Shell { get; }
    public IFileOps Ops { get; }
    public UndoStack Undo { get; }
    public ErrorReporter Errors { get; }
    public IPrompt Prompt { get; }
    public Action<Action> Post { get; }
}

public sealed partial class DiskUsageTabViewModel : TabViewModelBase
{
    public const string Type = "disk";

    private readonly DiskUsageServices _s;
    private CancellationTokenSource? _cts;
    private FolderNodeViewModel? _rootNode;

    [ObservableProperty] private string _rootPath;
    [ObservableProperty] private bool _scanning;
    [ObservableProperty] private string _progressText = "";
    [ObservableProperty] private string _scanChip = "";
    [ObservableProperty] private string _usedChip = "";
    [ObservableProperty] private string _warningChip = "";
    [ObservableProperty] private string _selectionText = "";
    [ObservableProperty] private FolderSizeTree? _tree;
    [ObservableProperty] private bool _allocatedMode;
    [ObservableProperty] private ExtensionRow? _selectedExtension;

    public DiskUsageTabViewModel(DiskUsageServices services, string rootPath)
    {
        _s = services;
        _rootPath = rootPath.Length == 0 ? "" : PathUtil.Normalize(rootPath);
        Hub.Changed += OnHubChanged;
        if (_rootPath.Length > 0) StartScan();
    }

    public SelectionHub Hub { get; } = new();
    public ObservableCollection<FolderNodeViewModel> TopNodes { get; } = new();
    public ObservableCollection<ExtensionRow> Extensions { get; } = new();
    public ObservableCollection<LegendEntry> Legend { get; } = new();
    public override string TypeId => Type;
    public override string Title => RootPath.Length == 0 ? "Disk Usage" : "Disk Usage: " + (PathUtil.IsRoot(RootPath) ? RootPath.TrimEnd('\\') : PathUtil.FileName(RootPath));
    public FolderNodeViewModel? SelectedNodeVm { get; private set; }
    public event Action<int>? RevealRequested;

    partial void OnRootPathChanged(string value) => RaiseTitleChanged();

    // ---- scan ----
    [RelayCommand]
    public void StartScan()
    {
        if (RootPath.Length == 0) return;
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        Scanning = true; ProgressText = "Scanning…"; WarningChip = "";
        var progress = new Progress<Progress>(p => ProgressText = $"Scanning… {p.Done:N0} items");
        var root = RootPath;
        _ = Task.Run(() => Scan(root, progress, cts.Token)).ContinueWith(t => _s.Post(() => Finish(t.Result, cts)));
    }

    private sealed record ScanOutcome(FolderSizeTree? Tree, List<ExtensionStat>? Stats, AppError? Error, double Seconds, int Skipped);

    private ScanOutcome Scan(string root, IProgress<Progress> progress, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var built = new EnumeratedIndexSource(_s.Files).Build(root, progress, ct);
        if (!built.IsOk) return new ScanOutcome(null, null, built.Error, 0, 0);
        var volume = VolumeInfo.Query(root);
        long cluster = volume.IsOk ? volume.Value.ClusterSize : 4096;
        bool wholeDrive = PathUtil.IsRoot(root);
        var tree = DiskScanner.Compute(built.Value, cluster, wholeDrive && volume.IsOk ? volume.Value.TotalBytes : 0, wholeDrive && volume.IsOk ? volume.Value.FreeBytes : 0);
        var stats = FileTypeCatalog.Aggregate(built.Value);
        return new ScanOutcome(tree, stats, null, sw.Elapsed.TotalSeconds, built.Value.SkippedFolders);
    }

    private void Finish(ScanOutcome o, CancellationTokenSource cts)
    {
        if (cts != _cts) return;            // superseded by a newer scan
        Scanning = false;
        if (o.Error is not null) { ProgressText = ""; _s.Errors.Report(o.Error); return; }
        Tree = o.Tree!;
        Tree.Mode = AllocatedMode ? SizeMode.Allocated : SizeMode.Logical;
        ScanChip = $"Scanned in {o.Seconds:0.0} s";
        UsedChip = Tree.HasVolumeInfo ? $"{SizeFormatter.Format(Tree.VolumeTotalBytes - Tree.VolumeFreeBytes)} used of {SizeFormatter.Format(Tree.VolumeTotalBytes)}" : $"{SizeFormatter.Format(Tree.RootLogical)} in {Tree.RootFiles:N0} files";
        WarningChip = o.Skipped > 0 ? $"{o.Skipped:N0} folders could not be read" : "";
        ProgressText = "";
        _extensionStats = o.Stats!;
        RebuildViews();
    }

    private List<ExtensionStat> _extensionStats = new();

    partial void OnAllocatedModeChanged(bool value)
    {
        if (Tree is null) return;
        Tree.Mode = value ? SizeMode.Allocated : SizeMode.Logical;
        RebuildViews();
        TreeChangedExternally?.Invoke();
    }

    public event Action? TreeChangedExternally;

    private void RebuildViews()
    {
        if (Tree is null) return;
        TopNodes.Clear();
        _rootNode = new FolderNodeViewModel(Tree, FolderSizeTree.RootId, Hub, null);
        _rootNode.LoadChildren();
        foreach (var c in _rootNode.Children) TopNodes.Add(c);
        if (Tree.HasVolumeInfo)
        {
            TopNodes.Add(new FolderNodeViewModel(Tree, FolderSizeTree.FreeId, Hub, null));
            if (Tree.UnknownBytes > 0) TopNodes.Add(new FolderNodeViewModel(Tree, FolderSizeTree.UnknownId, Hub, null));
        }
        BuildExtensionViews();
    }

    private void BuildExtensionViews()
    {
        Extensions.Clear(); Legend.Clear();
        long total = Math.Max(1, _extensionStats.Sum(s => s.Bytes));
        foreach (var s in _extensionStats.Take(200))
            Extensions.Add(new ExtensionRow(s.Extension.Length == 0 ? "(none)" : "." + s.Extension, s.Group.ToString(), FileTypeCatalog.ColorOf(s.Group), s.Files, s.Bytes,
                SizeFormatter.Format(s.Bytes), (100.0 * s.Bytes / total).ToString("0.0") + "%"));
        foreach (var g in FileTypeCatalog.ByGroup(_extensionStats).OrderByDescending(kv => kv.Value))
            Legend.Add(new LegendEntry(g.Key.ToString(), FileTypeCatalog.ColorOf(g.Key), SizeFormatter.Format(g.Value)));
    }

    [RelayCommand]
    private void CancelScan() { _cts?.Cancel(); Scanning = false; ProgressText = "Scan cancelled"; }

    [RelayCommand]
    private void ChooseFolder()
    {
        var path = _s.Prompt.Ask("Disk usage", "Folder or drive to analyse", RootPath);
        if (string.IsNullOrWhiteSpace(path)) return;
        RootPath = PathUtil.Normalize(path);
        StartScan();
    }

    [RelayCommand]
    private void ShowWarnings()
    {
        if (Tree is null) return;
        var w = Tree.Index.Warnings;
        _s.Errors.Report(new AppError(w.Count == 0 ? "No problems recorded." : $"{Tree.Index.SkippedFolders:N0} folders could not be read. First problems: " + string.Join(" | ", w.Take(5).Select(e => e.ToString()))));
    }

    // ---- selection sync: tree, treemap and table all go through the hub ----
    private void OnHubChanged(object? source)
    {
        if (Tree is null) return;
        if (Hub.NodeId is { } id && source is not FolderNodeViewModel && _rootNode is not null)
        {
            var node = FolderNodeViewModel.Reveal(_rootNode, Tree, id) ?? FindTop(id);
            if (id is FolderSizeTree.FreeId or FolderSizeTree.UnknownId) node = FindTop(id);
            if (node is not null) node.IsSelected = true;
            RevealRequested?.Invoke(id);
        }
        UpdateSelectionText();
        if (source is not null && source is not ExtensionRow && SelectedExtension is not null && Hub.Extension is null) SelectedExtension = null;
    }

    private FolderNodeViewModel? FindTop(int id) => TopNodes.FirstOrDefault(n => n.Id == id);

    partial void OnSelectedExtensionChanged(ExtensionRow? value) =>
        Hub.SelectExtension(value is null ? null : value.Extension == "(none)" ? "" : value.Extension.TrimStart('.'), value);

    private void UpdateSelectionText()
    {
        if (Tree is null || Hub.NodeId is not { } id) { SelectionText = ""; return; }
        SelectionText = id >= 0 ? $"{Tree.FullPath(id)} · {SizeFormatter.Format(Tree.Size(id))}" : $"{Tree.Name(id)} · {SizeFormatter.Format(Tree.Size(id))}";
    }

    public void NodeSelectedInTree(FolderNodeViewModel node) { SelectedNodeVm = node; UpdateSelectionText(); }

    // ---- actions on the selected node ----
    private string? SelectedPath => Tree is not null && Hub.NodeId is { } id && id >= 0 ? Tree.FullPath(id) : null;

    [RelayCommand] private void Open() { if (SelectedPath is { } p) _s.Errors.Check(_s.Shell.Open(p)); }
    [RelayCommand] private void Reveal() { if (SelectedPath is { } p) _s.Errors.Check(_s.Shell.Reveal(p)); }
    [RelayCommand] private void Properties() { if (SelectedPath is { } p) _s.Errors.Check(_s.Shell.ShowProperties(p)); }
    [RelayCommand] private void CopyPath() { if (SelectedPath is { } p) System.Windows.Clipboard.SetText(p); }

    [RelayCommand]
    private void DeleteSelected()
    {
        if (SelectedPath is not { } p) return;
        var r = _s.Ops.Delete(new[] { p });
        if (!_s.Errors.Check(r)) return;
        if (r.Value.Undo is { } u) _s.Undo.Push(u);
        ProgressText = "Deleted. Rescan to update the sizes.";
    }

    // ---- persistence ----
    public override TabState Save() => new(Type, JsonSerializer.Serialize(RootPath));
    public override ITabViewModel Duplicate() => new DiskUsageTabViewModel(_s, RootPath);

    public override void Dispose()
    {
        _cts?.Cancel();
        base.Dispose();
    }
}
