using System.Diagnostics;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FileExplorer.App.Controls;
using FileExplorer.App.Shell;
using FileExplorer.App.Tabs;
using FileExplorer.Core.Common;
using FileExplorer.Core.Fs;
using FileExplorer.Core.Index;
using FileExplorer.Core.Models;
using FileExplorer.Core.Search;
using FileExplorer.Core.Shell;

namespace FileExplorer.App.Features.Search;

/// <summary>One result line. Created lazily from (index, row id) when the list asks for it.</summary>
public sealed class SearchRowViewModel
{
    private readonly IIconProvider _icons;
    public SearchRowViewModel(FileEntry entry, IIconProvider icons) { Entry = entry; _icons = icons; }
    public FileEntry Entry { get; }
    public string Name => Entry.Name;
    public string FullPath => Entry.FullPath;
    public string Folder => PathUtil.Parent(Entry.FullPath) ?? Entry.FullPath;
    public string SizeText => Entry.IsDir ? "" : Entry.Size > 0 || Entry.ModifiedLocal != DateTime.MinValue ? SizeFormatter.Format(Entry.Size) : "";
    public string ModifiedText => Entry.ModifiedLocal == DateTime.MinValue ? "" : DateFormatter.Format(Entry.ModifiedLocal);
    public object? Icon => _icons.GetIcon(Entry);
}

/// <summary>Matches from several volumes, addressed by one running position.</summary>
public sealed class SearchResults
{
    private readonly (FileIndex Index, int[] Rows)[] _parts;
    private readonly int[] _starts;

    public SearchResults(IEnumerable<(FileIndex, int[])> parts)
    {
        _parts = parts.ToArray();
        _starts = new int[_parts.Length];
        int total = 0;
        for (int i = 0; i < _parts.Length; i++) { _starts[i] = total; total += _parts[i].Rows.Length; }
        Count = total;
    }

    public static SearchResults Empty { get; } = new(Array.Empty<(FileIndex, int[])>());
    public int Count { get; }

    public FileEntry At(int position)
    {
        int p = 0;
        while (p < _parts.Length - 1 && position >= _starts[p] + _parts[p].Rows.Length) p++;
        var (index, rows) = _parts[p];
        int row = rows[position - _starts[p]];
        using var _ = index.ReadLock();
        if (row >= index.Count) return new FileEntry("(changed)", "", 0, DateTime.MinValue, 0, false);
        var attrs = (FileAttributes)index.Attributes(row);
        long m = index.Modified(row);
        var modified = m > 0 ? DateTime.FromFileTimeUtc(m).ToLocalTime() : DateTime.MinValue;
        return new FileEntry(index.Name(row).ToString(), index.GetFullPath(row), index.Size(row), modified, attrs, index.IsDir(row));
    }
}

public sealed partial class SearchTabViewModel : TabViewModelBase
{
    public const string Type = "search";

    private readonly IndexService _index;
    private readonly QueryEngine _engine;
    private readonly QueryParser _parser;
    private readonly SavedSearchStore _saved;
    private readonly ErrorReporter _errors;
    private readonly IIconProvider _icons;
    private readonly IPrompt _prompt;
    private readonly ShellActions _shell;
    private readonly FileExplorer.App.Services.ElevationService _elevation;
    private readonly Action<Action> _post;
    private CancellationTokenSource? _cts;
    private System.Threading.Timer? _liveTimer;

    [ObservableProperty] private string _query = "";
    [ObservableProperty] private VirtualList<SearchRowViewModel> _rows = new(0, _ => null!);
    [ObservableProperty] private string _queryError = "";
    [ObservableProperty] private string _resultText = "Type to search";
    [ObservableProperty] private string _chipText = "";
    [ObservableProperty] private bool _chipWarn;
    [ObservableProperty] private bool _canEnableFast;
    [ObservableProperty] private IReadOnlyList<string> _savedNames = Array.Empty<string>();

    public SearchTabViewModel(IndexService index, QueryEngine engine, QueryParser parser, SavedSearchStore saved, ErrorReporter errors,
        IIconProvider icons, IPrompt prompt, ShellActions shell, FileExplorer.App.Services.ElevationService elevation, Action<Action> post, string initialQuery = "")
    {
        _index = index; _engine = engine; _parser = parser; _saved = saved; _errors = errors; _icons = icons;
        _prompt = prompt; _shell = shell; _elevation = elevation; _post = post;
        _query = initialQuery;
        _index.Changed += OnIndexChanged;
        RefreshChip();
        RefreshSaved();
        if (initialQuery.Length > 0) _ = ScheduleSearch();
    }

    public override string TypeId => Type;
    public override string Title => Query.Trim().Length == 0 ? "Search" : "Search: " + Query.Trim();
    public SearchResults Current { get; private set; } = SearchResults.Empty;
    public event Action? FocusRequested;
    public void RequestFocus() => FocusRequested?.Invoke();

    partial void OnQueryChanged(string value)
    {
        RaiseTitleChanged();
        _ = ScheduleSearch();
    }

    // ---- search with 30 ms debounce; a new keystroke cancels the running query ----
    private async Task ScheduleSearch()
    {
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        var text = Query;
        try { await Task.Delay(30, cts.Token); } catch (TaskCanceledException) { return; }
        var outcome = await Task.Run(() => Execute(text, cts.Token));
        if (cts.IsCancellationRequested) return;
        _post(() => Apply(outcome));
    }

    private sealed record Outcome(SearchResults? Results, string Error, long Ms);

    private Outcome Execute(string text, CancellationToken ct)
    {
        var parsed = _parser.Parse(text);
        if (!parsed.IsOk) return new Outcome(null, parsed.Error.Message, 0);
        if (parsed.Value.IsEmpty) return new Outcome(SearchResults.Empty, "", 0);
        var sw = Stopwatch.StartNew();
        var parts = new List<(FileIndex, int[])>();
        foreach (var idx in _index.Indexes)
        {
            var r = _engine.Search(idx, parsed.Value, ct);
            if (!r.IsOk) return new Outcome(null, r.Error.Message, 0);
            parts.Add((idx, r.Value));
        }
        return new Outcome(new SearchResults(parts), "", sw.ElapsedMilliseconds);
    }

    private void Apply(Outcome o)
    {
        QueryError = o.Error;
        if (o.Results is null) return;       // keep the previous results while the query is invalid
        Current = o.Results;
        var results = o.Results;
        Rows = new VirtualList<SearchRowViewModel>(results.Count, i => new SearchRowViewModel(results.At(i), _icons));
        ResultText = Query.Trim().Length == 0 ? "Type to search"
            : _index.Indexes.Count == 0 ? "No index yet" : $"{results.Count:N0} results · {o.Ms} ms";
    }

    // ---- index chip ----
    private void OnIndexChanged()
    {
        _post(() =>
        {
            RefreshChip();
            _liveTimer?.Dispose();
            _liveTimer = new System.Threading.Timer(_ => _post(() => { if (Query.Trim().Length > 0) _ = ScheduleSearch(); }), null, 400, Timeout.Infinite);
        });
    }

    private void RefreshChip()
    {
        var volumes = _index.Volumes;
        long files = volumes.Sum(v => (long)(v.Index?.LiveCount ?? 0));
        if (volumes.Any(v => v.Building))
        {
            ChipText = $"Indexing… {volumes.Sum(v => v.Progress):N0} files"; ChipWarn = true;
        }
        else if (files == 0) { ChipText = "Not indexed yet"; ChipWarn = true; }
        else
        {
            var modes = volumes.Where(v => v.Index is not null).Select(v => v.Index!.Mode).Distinct().ToList();
            string mode = modes.Count > 1 ? "Mixed index" : modes[0] == IndexMode.Mft ? "MFT index" : "Enumerated index";
            string live = volumes.All(v => v.Index is null || v.Live) ? "updated live" : volumes.Any(v => v.FromCache) ? "from cache" : "built " + Ago(volumes.Where(v => v.Index is not null).Min(v => v.Index!.BuiltUtc));
            var filling = volumes.Where(x => x.Filling).Select(x => x.Index!.MetadataProgress).ToList();
            string sizes = filling.Count > 0 ? $" · reading sizes {filling.Average():P0}" : "";
            ChipText = $"{mode} · {files:N0} files · {live}{sizes}";
            ChipWarn = modes.Contains(IndexMode.Enumerated);
        }
        CanEnableFast = !_index.IsElevated && _index.Indexes.Concat(Array.Empty<FileIndex>()).All(i => i.Mode != IndexMode.Mft);
    }

    private static string Ago(DateTime utc)
    {
        var span = DateTime.UtcNow - utc;
        return span.TotalMinutes < 1 ? "just now" : span.TotalHours < 1 ? $"{(int)span.TotalMinutes} min ago" : span.TotalDays < 1 ? $"{(int)span.TotalHours} h ago" : $"{(int)span.TotalDays} d ago";
    }

    // ---- commands ----
    [RelayCommand] private void Open(string? path) { if (path is not null) _errors.Check(_shell.Open(path)); }
    [RelayCommand] private void Reveal(string? path) { if (path is not null) _errors.Check(_shell.Reveal(path)); }

    [RelayCommand]
    private void RebuildIndex() { _ = _index.BuildAllAsync(); }

    [RelayCommand]
    private void EnableFastIndexing()
    {
        var r = _elevation.RelaunchElevated();
        if (!_errors.Check(r)) return;
        System.Windows.Application.Current.Shutdown();
    }

    [RelayCommand]
    private void SaveSearch()
    {
        var name = _prompt.Ask("Save search", "Name", Query.Trim());
        if (string.IsNullOrWhiteSpace(name)) return;
        _errors.Check(_saved.Save(name, Query));
        RefreshSaved();
    }

    [RelayCommand]
    private void LoadSaved(string? name)
    {
        var all = _saved.Load();
        if (_errors.Check(all) && name is not null && all.Value.TryGetValue(name, out var q)) Query = q;
    }

    private void RefreshSaved()
    {
        var all = _saved.Load();
        if (_errors.Check(all)) SavedNames = all.Value.Keys.OrderBy(k => k).ToList();
    }

    // ---- persistence ----
    public override TabState Save() => new(Type, JsonSerializer.Serialize(Query));

    public override ITabViewModel Duplicate() =>
        new SearchTabViewModel(_index, _engine, _parser, _saved, _errors, _icons, _prompt, _shell, _elevation, _post, Query);

    public static SearchTabViewModel Restore(TabState s, Func<string, SearchTabViewModel> make) =>
        make(JsonSerializer.Deserialize<string>(s.Data) ?? "");

    public override void Dispose()
    {
        _index.Changed -= OnIndexChanged;
        _cts?.Cancel(); _liveTimer?.Dispose();
        base.Dispose();
    }
}
