using FileExplorer.App.Services;
using FileExplorer.App.Shell;
using FileExplorer.App.Tabs;
using FileExplorer.Core.Index;
using FileExplorer.Core.Search;
using FileExplorer.Core.Shell;

namespace FileExplorer.App.Features.Search;

/// <summary>Search tab, Ctrl+K / Ctrl+F, index status. Removing this feature leaves the rest of the app untouched.</summary>
public sealed class SearchFeature : IFeature
{
    private readonly IndexService _index;
    private readonly QueryEngine _engine;
    private readonly QueryParser _parser;
    private readonly SavedSearchStore _saved;
    private readonly ErrorReporter _errors;
    private readonly IIconProvider _icons;
    private readonly IPrompt _prompt;
    private readonly ShellActions _shell;
    private readonly ElevationService _elevation;
    private readonly Action<Action> _post;

    public SearchFeature(IndexService index, QueryEngine engine, QueryParser parser, SavedSearchStore saved, ErrorReporter errors,
        IIconProvider icons, IPrompt prompt, ShellActions shell, ElevationService elevation, Action<Action> post)
    {
        _index = index; _engine = engine; _parser = parser; _saved = saved; _errors = errors; _icons = icons;
        _prompt = prompt; _shell = shell; _elevation = elevation; _post = post;
    }

    public string Name => "Search";

    public void Register(FeatureRegistry registry)
    {
        ViewTemplates.Register<SearchTabViewModel, SearchTabView>();
        registry.AddTabType(new TabType(SearchTabViewModel.Type, "Search", () => NewTab(""), s => SearchTabViewModel.Restore(s, NewTab)));
        registry.AddCommand(new FeatureCommand("Search…", "Ctrl+K", Open));
        registry.AddSidebarEntry(new SidebarEntry("TOOLS", "⌕ Search", Open));
        registry.SearchAction = Open;
        _index.ErrorRaised += e => _post(() => _errors.Report(e));
        _post(LoadCachesInBackground);
    }

    private SearchTabViewModel NewTab(string query)
    {
        EnsureIndexing();
        return new SearchTabViewModel(_index, _engine, _parser, _saved, _errors, _icons, _prompt, _shell, _elevation, _post, query);
    }

    /// <summary>Opens a search tab, or focuses the one this window already has.</summary>
    private void Open(MainViewModel window)
    {
        if (window.Manager.FindTab<SearchTabViewModel>() is not var (host, existing)) { window.OpenTool(NewTab("")); return; }
        host.ActiveTab = existing;
        window.Manager.BringToFront(host);
        existing.RequestFocus();
    }

    private void LoadCachesInBackground() =>
        _ = Task.Run(() => _index.LoadCaches(IndexService.FixedVolumes()));

    /// <summary>The first search tab starts indexing if nothing is indexed (cache load is cheap; a build is not).</summary>
    private void EnsureIndexing()
    {
        if (_index.Indexes.Count == 0 && !_index.AnyBuilding) _ = _index.BuildAllAsync();
    }
}
