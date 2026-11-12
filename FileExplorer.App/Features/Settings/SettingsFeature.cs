using FileExplorer.App.Services;
using FileExplorer.App.Shell;
using FileExplorer.Core.Common;

namespace FileExplorer.App.Features.Settings;

/// <summary>Remembers the theme between runs and adds a "hidden files" toggle.</summary>
public sealed class SettingsFeature : IFeature
{
    private readonly SettingsStore _store;
    private readonly ThemeService _theme;
    private readonly ErrorReporter _errors;
    private AppSettings _current = new();

    public SettingsFeature(SettingsStore store, ThemeService theme, ErrorReporter errors) { _store = store; _theme = theme; _errors = errors; }
    public string Name => "Settings";

    public void Register(FeatureRegistry registry)
    {
        var loaded = _store.Load();
        if (loaded.IsOk) _current = loaded.Value; else _errors.Report(loaded.Error);      // defaults are used knowingly, and the user is told
        _theme.Apply(_current.DarkTheme);
        _theme.Changed += () => { _current = _current with { DarkTheme = _theme.IsDark }; _errors.Check(_store.Save(_current)); };
        registry.AddCommand(new FeatureCommand("Show / hide hidden files", null, w => { if (w.ActivePane is { } p) p.ShowHidden = !p.ShowHidden; }));
    }
}
