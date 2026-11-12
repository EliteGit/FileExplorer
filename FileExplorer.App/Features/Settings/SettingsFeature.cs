using FileExplorer.App.Services;
using FileExplorer.App.Shell;
using FileExplorer.App.Tabs;
using FileExplorer.Core.Common;

namespace FileExplorer.App.Features.Settings;

/// <summary>Remembers the hidden-files choice between runs.</summary>
public sealed class SettingsFeature : IFeature
{
    private readonly SettingsStore _store;
    private readonly ErrorReporter _errors;
    private readonly PaneFactory _panes;
    private AppSettings _current = new();

    public SettingsFeature(SettingsStore store, ErrorReporter errors, PaneFactory panes)
    {
        _store = store; _errors = errors; _panes = panes;
    }

    public string Name => "Settings";

    public void Register(FeatureRegistry registry)
    {
        var loaded = _store.Load();
        if (loaded.IsOk) _current = loaded.Value; else _errors.Report(loaded.Error);      // defaults are used knowingly, and the user is told
        _panes.DefaultShowHidden = _current.ShowHidden;
        registry.AddCommand(new FeatureCommand("Show / hide hidden files", null, ToggleHidden));
    }

    /// <summary>Flips hidden files for every pane in the window, for new panes, and for the next start.</summary>
    private void ToggleHidden(MainViewModel window)
    {
        bool show = !_panes.DefaultShowHidden;
        _panes.DefaultShowHidden = show;
        foreach (var pane in window.Tabs.OfType<BrowserTabViewModel>().SelectMany(t => t.Panes)) pane.ShowHidden = show;
        Save(_current with { ShowHidden = show });
    }

    private void Save(AppSettings settings)
    {
        _current = settings;
        _errors.Check(_store.Save(_current));
    }
}
