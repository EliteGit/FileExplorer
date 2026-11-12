using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FileExplorer.App.Features.Tools;
using FileExplorer.App.Tabs;
using FileExplorer.Core.Common;
using FileExplorer.Core.Connections;
using FileExplorer.Core.Fs;
using FileExplorer.Core.Index;
using FileExplorer.Core.Reports;

namespace FileExplorer.App.Features.Connections;

/// <summary>One row of the connection tree: a file, and (when expanded) everything it points at or is pointed at by.</summary>
public sealed class ConnectionNodeViewModel
{
    private static readonly ConnectionNodeViewModel Placeholder = new();
    private readonly ConnectionsTabViewModel? _owner;
    private readonly HashSet<int> _ancestors = new();
    private bool _loaded;

    private ConnectionNodeViewModel() { Relation = ""; Name = "…"; Detail = ""; Badge = ""; }

    public ConnectionNodeViewModel(ConnectionsTabViewModel owner, int node, string relation, string badge, HashSet<int> ancestors, bool isCycle)
    {
        _owner = owner; NodeId = node; Relation = relation; Badge = badge;
        foreach (var a in ancestors) _ancestors.Add(a);
        _ancestors.Add(node);
        var path = owner.Graph!.PathOf(node);
        Name = Path.GetFileName(path.TrimEnd('\\')) is { Length: > 0 } n ? n : path;
        Detail = Path.GetDirectoryName(path) ?? path;
        IsMissing = owner.Graph.IsMissing(node);
        IsCycle = isCycle;
        if (!isCycle && owner.VisibleEdges(node).Any()) Children.Add(Placeholder);
    }

    public int NodeId { get; }
    public string Relation { get; }
    public string Name { get; }
    public string Detail { get; }
    public string Badge { get; }
    public bool IsMissing { get; }
    public bool IsCycle { get; }
    public string FullPath => _owner?.Graph?.PathOf(NodeId) ?? "";
    public ObservableCollection<ConnectionNodeViewModel> Children { get; } = new();
    public string Display => IsCycle ? Name + "  ↻ already shown above" : IsMissing ? Name + "  (missing)" : Name;

    private bool _expanded;
    public bool IsExpanded
    {
        get => _expanded;
        set { _expanded = value; if (value) LoadChildren(); }
    }

    private void LoadChildren()
    {
        if (_loaded || _owner is null || IsCycle) return;
        _loaded = true;
        Children.Clear();
        foreach (var (edge, outgoing) in _owner.VisibleEdges(NodeId))
        {
            int other = outgoing ? edge.To : edge.From;
            bool cycle = _ancestors.Contains(other);
            string badge = _owner.Graph!.CrossesDrives(edge) ? "⇄ " + (ConnectionGraph.DriveOf(_owner.Graph.PathOf(other)) is { } d ? d + ":" : "network") : "";
            Children.Add(new ConnectionNodeViewModel(_owner, other, ConnectionsTabViewModel.RelationText(edge.Kind, outgoing), badge, _ancestors, cycle));
        }
    }
}

public sealed partial class ConnectionsTabViewModel : TabViewModelBase
{
    public const string Type = "connections";
    private const int MaxRoots = 2000;
    private readonly ToolServices _s;
    private readonly IndexService? _indexes;
    private CancellationTokenSource? _cts;
    private ConnectionScanResult? _result;

    [ObservableProperty] private bool _shortcuts = true;
    [ObservableProperty] private bool _imports = true;
    [ObservableProperty] private bool _mentions = true;
    [ObservableProperty] private bool _includeWindows;
    [ObservableProperty] private bool _crossDriveOnly;
    [ObservableProperty] private string _filter = "";
    [ObservableProperty] private bool _running;
    [ObservableProperty] private string _progressText = "";
    [ObservableProperty] private string _summary = "Choose what to follow and press Scan. It reads every file on all fixed drives, so the first scan can take a few minutes.";

    public ConnectionsTabViewModel(ToolServices services, IndexService? indexes) { _s = services; _indexes = indexes; }

    public ConnectionGraph? Graph => _result?.Graph;
    public ObservableCollection<ConnectionNodeViewModel> Roots { get; } = new();
    public override string TypeId => Type;
    public override string Title => "Connections";

    public static string RelationText(ConnectionKind kind, bool outgoing) => (kind, outgoing) switch
    {
        (ConnectionKind.Shortcut, true) => "→ shortcut to", (ConnectionKind.Shortcut, false) => "← shortcut from",
        (ConnectionKind.Link, true) => "→ link to", (ConnectionKind.Link, false) => "← linked from",
        (ConnectionKind.Imports, true) => "→ loads", (ConnectionKind.Imports, false) => "← loaded by",
        (_, true) => "→ mentions", _ => "← mentioned by",
    };

    /// <summary>The edges of a node that pass the current filters (kind, cross-drive, Windows folder), with their direction.</summary>
    public IEnumerable<(ConnectionEdge Edge, bool Outgoing)> VisibleEdges(int node)
    {
        var g = Graph;
        if (g is null) yield break;
        foreach (var e in g.Outgoing(node)) if (Passes(e)) yield return (e, true);
        foreach (var e in g.Incoming(node)) if (Passes(e)) yield return (e, false);
    }

    private bool Passes(ConnectionEdge e) =>
        (!CrossDriveOnly || Graph!.CrossesDrives(e)) &&
        e.Kind switch { ConnectionKind.Shortcut or ConnectionKind.Link => Shortcuts, ConnectionKind.Imports => Imports, _ => Mentions };

    // ---- scan ----
    [RelayCommand]
    private void Scan()
    {
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        Running = true; Roots.Clear(); Summary = "Collecting files…"; ProgressText = "";
        var options = new ConnectionOptions(Shortcuts, Imports, Mentions, IncludeWindows);
        var progress = new Progress<Progress>(p => ProgressText = p.Total > 0 ? $"Reading files… {100.0 * p.Done / p.Total:0}%" : $"Collecting… {p.Done:N0}");
        _ = Task.Run(() =>
        {
            var indexes = Gather(progress, cts.Token);
            if (!indexes.IsOk) return Result<ConnectionScanResult>.Err(indexes.Error);
            return new ConnectionScanner().Scan(indexes.Value, options, progress, cts.Token);
        }).ContinueWith(t => _s.Post(() => Finish(t.Result, cts)));
    }

    /// <summary>All fixed drives: an index that already exists is reused, the others are walked now.</summary>
    private Result<IReadOnlyList<FileIndex>> Gather(IProgress<Progress> progress, CancellationToken ct)
    {
        var list = new List<FileIndex>();
        foreach (var volume in IndexService.FixedVolumes())
        {
            var have = _indexes?.Indexes.FirstOrDefault(i => string.Equals(i.Volume, PathUtil.Normalize(volume), StringComparison.OrdinalIgnoreCase));
            if (have is not null) { list.Add(have); continue; }
            var built = new EnumeratedIndexSource(_s.Files).Build(volume, progress, ct);
            if (!built.IsOk) { _s.Post(() => _s.Errors.Report(built.Error)); continue; }       // one unreadable drive does not stop the others
            list.Add(built.Value);
        }
        return list.Count == 0 ? new AppError("No drive could be read.") : Result<IReadOnlyList<FileIndex>>.Ok(list);
    }

    private void Finish(Result<ConnectionScanResult> r, CancellationTokenSource cts)
    {
        if (cts != _cts) return;
        Running = false; ProgressText = "";
        if (!r.IsOk) { Summary = r.Error.Message; _s.Errors.Report(r.Error); return; }
        _result = r.Value;
        OnPropertyChanged(nameof(Graph));
        var g = r.Value.Graph;
        int cross = g.Nodes.Sum(n => g.Outgoing(n).Count(g.CrossesDrives));
        var c = r.Value.Counts;
        Summary = $"{r.Value.FilesRead:N0} files read · {g.EdgeCount:N0} connections ({c[ConnectionKind.Shortcut]:N0} shortcuts, {c[ConnectionKind.Link]:N0} links, " +
                  $"{c[ConnectionKind.Imports]:N0} loads, {c[ConnectionKind.Mentions]:N0} mentions) · {cross:N0} cross drives" +
                  (r.Value.UnresolvedImports > 0 ? $" · {r.Value.UnresolvedImports:N0} DLLs not found" : "") +
                  (r.Value.Problems.Count > 0 ? $" · {r.Value.Problems.Count:N0} files could not be read" : "");
        Rebuild();
    }

    partial void OnCrossDriveOnlyChanged(bool value) => Rebuild();
    partial void OnShortcutsChanged(bool value) => Rebuild();
    partial void OnImportsChanged(bool value) => Rebuild();
    partial void OnMentionsChanged(bool value) => Rebuild();
    partial void OnFilterChanged(string value) => Rebuild();

    /// <summary>Roots: files that have at least one visible connection, busiest first.</summary>
    private void Rebuild()
    {
        Roots.Clear();
        var g = Graph;
        if (g is null) return;
        var f = Filter.Trim();
        var nodes = g.Nodes.Where(n => f.Length == 0 || g.PathOf(n).Contains(f, StringComparison.OrdinalIgnoreCase))
            .Select(n => (Node: n, Degree: VisibleEdges(n).Count())).Where(x => x.Degree > 0)
            .OrderByDescending(x => x.Degree).ThenBy(x => g.PathOf(x.Node), StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var x in nodes.Take(MaxRoots)) Roots.Add(new ConnectionNodeViewModel(this, x.Node, "", "", new HashSet<int>(), false));
        if (nodes.Count > MaxRoots) Summary = Summary.Split(" · showing")[0] + $" · showing the {MaxRoots:N0} busiest of {nodes.Count:N0} (use the filter to narrow)";
    }

    [RelayCommand] private void Cancel() { _cts?.Cancel(); Running = false; ProgressText = "Cancelled"; }

    [RelayCommand]
    private void ShowProblems()
    {
        var p = _result?.Problems;
        _s.Errors.Report(new AppError(p is null || p.Count == 0 ? "Every file was readable." : "Not read: " + string.Join(" | ", p.Take(5).Select(e => e.ToString()))));
    }

    [RelayCommand]
    private void ExportCsv()
    {
        if (Graph is not { } g) return;
        var path = ToolServices.AskCsvPath("connections.csv");
        if (path is null) return;
        var rows = g.Nodes.SelectMany(n => g.Outgoing(n)).Where(Passes)
            .Select(e => (IRow)new SimpleRow(g.PathOf(e.From), e.Kind.ToString(), g.PathOf(e.To), g.CrossesDrives(e) ? "yes" : "no", g.IsMissing(e.To) ? "missing" : ""));
        _s.Errors.Check(CsvExporter.Export(path, new[] { "From", "Kind", "To", "Crosses drives", "Target" }, rows));
    }

    [RelayCommand] private void Open(ConnectionNodeViewModel? n) { if (n is not null) _s.Errors.Check(_s.Shell.Open(n.FullPath)); }
    [RelayCommand] private void Reveal(ConnectionNodeViewModel? n) { if (n is not null) _s.Errors.Check(_s.Shell.Reveal(n.FullPath)); }
    [RelayCommand] private void FocusOn(ConnectionNodeViewModel? n) { if (n is not null) Filter = n.FullPath; }

    public override TabState Save() => new(Type, JsonSerializer.Serialize(""));
    public override ITabViewModel Duplicate() => new ConnectionsTabViewModel(_s, _indexes) { Shortcuts = Shortcuts, Imports = Imports, Mentions = Mentions, IncludeWindows = IncludeWindows };
    public override void Dispose() { _cts?.Cancel(); base.Dispose(); }
}
