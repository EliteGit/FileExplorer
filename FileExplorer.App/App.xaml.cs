using System.Windows;
using FileExplorer.App.Features;
using FileExplorer.App.Services;
using FileExplorer.App.Shell;
using FileExplorer.App.Tabs;
using FileExplorer.Core.Common;
using FileExplorer.Core.Fs;
using FileExplorer.Core.Ops;
using FileExplorer.Core.Shell;
using Microsoft.Extensions.DependencyInjection;

namespace FileExplorer.App;

public partial class App : Application
{
    private ServiceProvider _services = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _services = BuildServices();
        var manager = _services.GetRequiredService<WindowManager>();
        var errors = _services.GetRequiredService<ErrorReporter>();
        var session = _services.GetRequiredService<SessionStore>();

        _services.GetRequiredService<WpfWindowPresenter>().Bind(
            () => _services.GetRequiredService<WindowManager>(), _ => SaveSession(manager, session, errors));
        manager.AllWindowsClosed += () => Dispatcher.BeginInvoke(new Action(Shutdown));
        SessionEnding += (_, _) => SaveSession(manager, session, errors);

        RegisterFeatures(_services.GetRequiredService<FeatureRegistry>());
        StartWindows(manager, session, errors);
    }

    private static void RegisterFeatures(FeatureRegistry registry)
    {
        // Each phase adds one line here. Removing a line (and its folder) must leave the app building and running.
        registry.Add(ActivatorUtilities.CreateInstance<BrowserFeature>(((App)Current)._services));
    }

    private static void StartWindows(WindowManager manager, SessionStore session, ErrorReporter errors)
    {
        var loaded = session.Load();
        if (!loaded.IsOk) errors.Report(loaded.Error);
        if (loaded.IsOk && loaded.Value is { Windows.Count: > 0 } snapshot) manager.Restore(snapshot);
        else manager.CreateWindow();
    }

    private static void SaveSession(WindowManager manager, SessionStore session, ErrorReporter errors)
    {
        if (manager.Windows.Count == 0) return;
        var saved = session.Save(manager.Snapshot());
        if (!saved.IsOk) errors.Report(saved.Error);
    }

    private static ServiceProvider BuildServices()
    {
        var s = new ServiceCollection();
        s.AddSingleton<IFileSource, FastDirectoryEnumerator>();
        s.AddSingleton<IFileOps, ShellFileOps>();
        s.AddSingleton<UndoStack>();
        s.AddSingleton<ShellActions>();
        s.AddSingleton<ErrorReporter>();
        s.AddSingleton<IconCache>();
        s.AddSingleton<IIconProvider>(p => p.GetRequiredService<IconCache>());
        s.AddSingleton<IClipboardService, ClipboardService>();
        s.AddSingleton<IPrompt, PromptService>();
        s.AddSingleton<ThemeService>();
        s.AddSingleton<IThemeService>(p => p.GetRequiredService<ThemeService>());
        s.AddSingleton<FeatureRegistry>();
        s.AddSingleton<SessionStore>();
        s.AddSingleton<FileCommands>();
        s.AddSingleton<SplitCommands>();
        s.AddSingleton<Action<Action>>(_ => a => Current.Dispatcher.BeginInvoke(a));
        s.AddSingleton<PaneFactory>();
        s.AddSingleton<WindowFactory>();
        s.AddSingleton<WpfWindowPresenter>();
        s.AddSingleton<IWindowPresenter>(p => p.GetRequiredService<WpfWindowPresenter>());
        s.AddSingleton<WindowManager>();
        return s.BuildServiceProvider();
    }
}
