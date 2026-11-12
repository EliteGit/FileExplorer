using FileExplorer.App.Shell;
using FileExplorer.App.Tabs;
using FileExplorer.Core.Fs;

namespace FileExplorer.App.Features;

/// <summary>Registers the built-in browser tab type. Every window needs at least this one.</summary>
public sealed class BrowserFeature : IFeature
{
    private readonly PaneFactory _panes;
    private readonly ErrorReporter _errors;

    public BrowserFeature(PaneFactory panes, ErrorReporter errors) { _panes = panes; _errors = errors; }

    public string Name => "Browser";

    public void Register(FeatureRegistry registry)
    {
        registry.AddTabType(new TabType(BrowserTabViewModel.Type, "Folder",
            () => new BrowserTabViewModel(_panes.Create, DefaultStart()),
            state => BrowserTabViewModel.Restore(state, _panes.Create, _errors)));
    }

    private static string DefaultStart() => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
}

/// <summary>Creates panes with the shared services.</summary>
public sealed class PaneFactory
{
    private readonly IFileSource _source;
    private readonly ErrorReporter _errors;
    private readonly IIconProvider _icons;
    private readonly Action<Action> _post;

    public PaneFactory(IFileSource source, ErrorReporter errors, IIconProvider icons, Action<Action> post)
    {
        _source = source; _errors = errors; _icons = icons; _post = post;
    }

    public PaneViewModel Create() => new(_source, _errors, _icons, _post);
}
