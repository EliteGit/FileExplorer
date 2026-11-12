using System.Windows;
using FileExplorer.App.Features;
using FileExplorer.App.Services;
using FileExplorer.App.Shell;
using FileExplorer.App.Tabs;
using FileExplorer.Core.Common;
using FileExplorer.Core.Fs;
using FileExplorer.Core.Index;
using FileExplorer.Core.Search;
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
        Exit += (_, _) => _services.GetRequiredService<IndexService>().SaveAll();

        _services.GetRequiredService<WpfWindowPresenter>().Bind(
            () => _services.GetRequiredService<WindowManager>(), _ => SaveSession(manager, session, errors));
        DispatcherUnhandledException += (_, e) => OnUnexpectedError(e, errors);
        manager.AllWindowsClosed += () => Dispatcher.BeginInvoke(new Action(Shutdown));
        SessionEnding += (_, _) => SaveSession(manager, session, errors);

        RegisterFeatures(_services.GetRequiredService<FeatureRegistry>());
        StartWindows(manager, session, errors);
    }

    private int _recentFailures;
    private DateTime _lastFailure = DateTime.MinValue;

    /// <summary>
    /// An unexpected error is written to the error log and shown in the status bar instead of closing the app. If the same failure
    /// repeats in quick succession (for example inside a redraw loop) the app is allowed to stop, because carrying on would be a lie.
    /// </summary>
    private void OnUnexpectedError(System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e, ErrorReporter errors)
    {
        var now = DateTime.UtcNow;
        _recentFailures = now - _lastFailure < TimeSpan.FromSeconds(2) ? _recentFailures + 1 : 1;
        _lastFailure = now;
        try
        {
            var dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FileExplorer");
            System.IO.Directory.CreateDirectory(dir);
            System.IO.File.AppendAllText(System.IO.Path.Combine(dir, "errors.log"), $"{DateTime.Now:s} {e.Exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (System.IO.IOException) { /* the status bar below still tells the user */ }
        if (_recentFailures > 3) return;               // not handled: the app stops with the original exception
        errors.Report(new Core.Common.AppError($"Unexpected error: {e.Exception.Message} (details in %LocalAppData%\\FileExplorer\\errors.log)"));
        e.Handled = true;
    }

    private static void RegisterFeatures(FeatureRegistry registry)
    {
        // Each phase adds one line here. Removing a line (and its folder) must leave the app building and running.
        registry.Add(ActivatorUtilities.CreateInstance<Features.Settings.SettingsFeature>(((App)Current)._services));   // Phase 5: saved settings
        registry.Add(ActivatorUtilities.CreateInstance<BrowserFeature>(((App)Current)._services));
        registry.Add(ActivatorUtilities.CreateInstance<Features.Search.SearchFeature>(((App)Current)._services));   // Phase 2
        registry.Add(new Features.DiskUsage.DiskUsageFeature(ActivatorUtilities.CreateInstance<Features.DiskUsage.DiskUsageServices>(((App)Current)._services)));   // Phase 3 (builds its own services, so this one line is all that registers it)
        registry.Add(ActivatorUtilities.CreateInstance<Features.Preview.PreviewFeature>(((App)Current)._services));   // Phase 4: preview
        registry.Add(ActivatorUtilities.CreateInstance<Features.Tags.TagsFeature>(((App)Current)._services));   // Phase 4: tags
        registry.Add(ActivatorUtilities.CreateInstance<Features.Tags.TagSearchIntegration>(((App)Current)._services));   // Phase 4: tag: search term (needs Phase 2)
        registry.Add(ActivatorUtilities.CreateInstance<Features.Duplicates.DuplicatesFeature>(((App)Current)._services));   // Phase 5
        registry.Add(ActivatorUtilities.CreateInstance<Features.BulkRename.BulkRenameFeature>(((App)Current)._services));   // Phase 5
        registry.Add(ActivatorUtilities.CreateInstance<Features.Reports.ReportsFeature>(((App)Current)._services));   // Phase 5
        registry.Add(ActivatorUtilities.CreateInstance<Features.Checksum.ChecksumFeature>(((App)Current)._services));   // Phase 5
        registry.Add(ActivatorUtilities.CreateInstance<Features.Connections.ConnectionsFeature>(((App)Current)._services));   // Connections tool
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
        // Phase 2
        s.AddSingleton<ElevationService>();
        s.AddSingleton<IndexCache>();
        s.AddSingleton(p => new IndexService(p.GetRequiredService<IFileSource>(), p.GetRequiredService<IndexCache>(), p.GetRequiredService<ElevationService>().IsElevated));
        s.AddSingleton<QueryEngine>();
        s.AddSingleton<QueryTermProviders>();
        s.AddSingleton(p => new QueryParser(p.GetRequiredService<QueryTermProviders>()));
        s.AddSingleton<SavedSearchStore>();
        // Phase 4
        s.AddSingleton<FileExplorer.Core.Tags.ITagStore, FileExplorer.Core.Tags.TagStore>();
        // Phase 5
        s.AddSingleton<FolderSizeService>();
        s.AddSingleton<IFolderSizeProvider>(p => p.GetRequiredService<FolderSizeService>());
        s.AddSingleton<SettingsStore>();
        s.AddSingleton<Features.Tools.ToolServices>();
        return s.BuildServiceProvider();
    }
}
