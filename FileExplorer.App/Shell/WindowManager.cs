using FileExplorer.App.Tabs;
using FileExplorer.Core.Common;

namespace FileExplorer.App.Shell;

/// <summary>Builds a complete window view model. The first window and every later one use this single path.</summary>
public sealed class WindowFactory
{
    private readonly FeatureRegistry _features;
    private readonly ErrorReporter _errors;
    private readonly FileCommands _files;
    private readonly SplitCommands _split;

    public WindowFactory(FeatureRegistry features, ErrorReporter errors, FileCommands files, SplitCommands split)
    {
        _features = features; _errors = errors; _files = files; _split = split;
    }

    public MainViewModel Create(WindowManager owner) => new(owner, _features, _errors, _files, _split);
}

/// <summary>Owns every open window. Tabs only ever move to a NEW window (no merge-back).</summary>
public sealed class WindowManager
{
    private readonly WindowFactory _factory;
    private readonly IWindowPresenter _presenter;
    private readonly FeatureRegistry _features;
    private readonly ErrorReporter _errors;
    private readonly List<MainViewModel> _windows = new();

    public WindowManager(WindowFactory factory, IWindowPresenter presenter, FeatureRegistry features, ErrorReporter errors)
    {
        _factory = factory; _presenter = presenter; _features = features; _errors = errors;
    }

    public IReadOnlyList<MainViewModel> Windows => _windows;
    public MainViewModel? ActiveWindow { get; private set; }

    /// <summary>Raised after the last window closed; the application exits.</summary>
    public event Action? AllWindowsClosed;

    // ---- creation ----
    public MainViewModel CreateWindow(ITabViewModel? tab = null, WindowBounds? bounds = null, bool activate = true)
    {
        var vm = _factory.Create(this);
        vm.AddTab(tab ?? NewDefaultTab());
        _windows.Add(vm);
        _presenter.Show(vm, bounds, activate);
        Activate(vm);
        return vm;
    }

    private ITabViewModel NewDefaultTab() =>
        (_features.FindTabType(BrowserTabViewModel.Type) ?? throw new InvalidOperationException("The browser tab type is not registered.")).Create();

    // ---- tab moves ----
    /// <summary>Moves a tab into a brand-new window. A window's only tab cannot be moved.</summary>
    public bool MoveTabToNewWindow(MainViewModel from, ITabViewModel tab, double? screenX = null, double? screenY = null, bool activate = true)
    {
        if (!CanTearOut(from, tab)) return false;
        from.DetachTab(tab);
        var bounds = screenX is { } x && screenY is { } y ? new WindowBounds(x, y, 1000, 640, false) : (WindowBounds?)null;
        CreateWindow(tab, bounds, activate);
        return true;
    }

    public bool CanTearOut(MainViewModel from, ITabViewModel tab) => from.Tabs.Count > 1 && from.Tabs.Contains(tab);

    /// <summary>Esc during a tear-out: closes the new window and puts the tab back where it was.</summary>
    public void ReturnTab(MainViewModel target, ITabViewModel tab, MainViewModel from, int index)
    {
        target.DetachTab(tab);
        from.AddTab(tab, true, index);
        RemoveWindow(target);
        Activate(from);
    }

    // ---- closing ----
    public void CloseIfEmpty(MainViewModel window)
    {
        if (window.Tabs.Count == 0) CloseWindow(window);
    }

    /// <summary>Closes a window (and its tabs). Exits the app when it was the last one.</summary>
    public void CloseWindow(MainViewModel window)
    {
        window.DisposeAllTabs();
        RemoveWindow(window);
    }

    /// <summary>Called when the real window was closed by the user (title bar X, Alt+F4).</summary>
    public void NotifyWindowClosed(MainViewModel window)
    {
        if (!_windows.Contains(window)) return;
        window.DisposeAllTabs();
        _windows.Remove(window);
        AfterRemove(window);
    }

    private void RemoveWindow(MainViewModel window)
    {
        if (!_windows.Remove(window)) return;
        _presenter.Close(window);
        AfterRemove(window);
    }

    private void AfterRemove(MainViewModel window)
    {
        if (ActiveWindow == window) ActiveWindow = _windows.LastOrDefault();
        if (_windows.Count == 0) AllWindowsClosed?.Invoke();
    }

    public void Activate(MainViewModel window)
    {
        if (_windows.Contains(window)) ActiveWindow = window;
    }

    public WindowBounds BoundsOf(MainViewModel window) => _presenter.GetBounds(window);

    public MainViewModel? StripWindowAt(double x, double y, MainViewModel exclude) => _presenter.StripWindowAt(x, y, exclude);

    /// <summary>Drops a tab onto another window's strip; the source window closes if it is left empty.</summary>
    public void MergeTab(MainViewModel from, ITabViewModel tab, MainViewModel target)
    {
        from.DetachTab(tab);
        target.AddTab(tab);
        CloseIfEmpty(from);
        BringToFront(target);
    }

    public void BringToFront(MainViewModel window) { _presenter.Bring(window); Activate(window); }

    public void MoveWindow(MainViewModel window, double screenX, double screenY) => _presenter.MoveTo(window, screenX, screenY);

    // ---- session ----
    public SessionSnapshot Snapshot() => new(_windows.Select(w => new WindowSnapshot(
        _presenter.GetBounds(w), w.Tabs.Select(t => t.Save()).ToList(), Math.Max(0, w.ActiveTab is null ? 0 : w.Tabs.IndexOf(w.ActiveTab)))).ToList());

    /// <summary>Reopens saved windows. A tab that cannot be restored is reported and skipped; if nothing restores, one fresh window opens.</summary>
    public void Restore(SessionSnapshot snapshot)
    {
        foreach (var ws in snapshot.Windows)
        {
            var tabs = ws.Tabs.Select(RestoreTab).OfType<ITabViewModel>().ToList();
            if (tabs.Count == 0) continue;
            var vm = CreateWindow(tabs[0], ws.Bounds);
            foreach (var t in tabs.Skip(1)) vm.AddTab(t, false);
            vm.ActiveTab = vm.Tabs[Math.Clamp(ws.ActiveTab, 0, vm.Tabs.Count - 1)];
        }
        if (_windows.Count == 0) CreateWindow();
    }

    private ITabViewModel? RestoreTab(TabState state)
    {
        var type = _features.FindTabType(state.Type);
        if (type is null) { _errors.Report(new AppError($"Cannot restore tab of unknown type '{state.Type}'.")); return null; }
        try { return type.Restore(state); }
        catch (Exception e) when (e is System.Text.Json.JsonException or InvalidDataException)
        { _errors.Report(new AppError($"Cannot restore a {type.DisplayName} tab: {e.Message}")); return null; }
    }
}
