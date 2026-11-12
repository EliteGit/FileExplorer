using FileExplorer.App.Tabs;

namespace FileExplorer.App.Shell;

/// <summary>Connects the drag state machine to one window's tab list and the WindowManager.</summary>
public sealed class WindowTabDragHost : ITabDragHost
{
    /// <summary>Where the pointer sits inside a new window so it is over that window's tab strip.</summary>
    public static readonly Pt GrabOffset = new(90, 20);

    private readonly MainViewModel _window;
    private readonly Func<double, int> _indexAtX;
    private readonly Func<double> _stripHeight;
    private MainViewModel? _torn;
    private ITabViewModel? _tornTab;
    private Pt _offset = GrabOffset;
    private Pt _origin;
    private Pt _lastScreen;

    public WindowTabDragHost(MainViewModel window, Func<double, int> indexAtX, Func<double> stripHeight)
    {
        _window = window; _indexAtX = indexAtX; _stripHeight = stripHeight;
    }

    public double StripHeight => _stripHeight();
    public int TabCount => _window.Tabs.Count;
    public int IndexOf(object tab) => _window.Tabs.IndexOf((ITabViewModel)tab);
    public int IndexAtX(double x) => _indexAtX(x);

    public void MoveTab(object tab, int newIndex) => _window.Tabs.Move(IndexOf(tab), Math.Clamp(newIndex, 0, _window.Tabs.Count - 1));

    public bool CanTearOut(object tab) => _window.Manager.CanTearOut(_window, (ITabViewModel)tab);

    /// <summary>With several tabs this creates a new window; with one tab the window itself is picked up.</summary>
    public void TearOut(object tab, Pt screen)
    {
        var manager = _window.Manager;
        _tornTab = (ITabViewModel)tab;
        _lastScreen = screen;
        if (!CanTearOut(tab))
        {
            var b = manager.BoundsOf(_window);
            _torn = _window; _origin = new Pt(b.Left, b.Top);
            _offset = new Pt(screen.X - b.Left, screen.Y - b.Top);
            return;
        }
        _offset = GrabOffset;
        // Not activated, so the strip keeps mouse capture while the new window follows the pointer.
        if (!manager.MoveTabToNewWindow(_window, _tornTab, screen.X - _offset.X, screen.Y - _offset.Y, activate: false)) return;
        _torn = manager.Windows[^1];
        _origin = default;
    }

    public void MoveTornWindow(Pt screen)
    {
        _lastScreen = screen;
        if (_torn is not null) _window.Manager.MoveWindow(_torn, screen.X - _offset.X, screen.Y - _offset.Y);
    }

    /// <summary>Released over another window's tab strip: the tab joins that window. Otherwise the window stays put.</summary>
    public void DropTornWindow()
    {
        var manager = _window.Manager;
        if (_torn is not null && _tornTab is not null)
        {
            var target = manager.StripWindowAt(_lastScreen.X, _lastScreen.Y, _torn);
            if (target is not null) manager.MergeTab(_torn, _tornTab, target);
            else manager.BringToFront(_torn);
        }
        _torn = null; _tornTab = null;
    }

    public void ReturnTornTab(object tab, int originalIndex)
    {
        if (_torn is null) return;
        if (_torn == _window) _window.Manager.MoveWindow(_window, _origin.X, _origin.Y);
        else _window.Manager.ReturnTab(_torn, (ITabViewModel)tab, _window, originalIndex);
        _torn = null; _tornTab = null;
    }
}
