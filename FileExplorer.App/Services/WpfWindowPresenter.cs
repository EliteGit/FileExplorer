using System.Windows;
using FileExplorer.App.Shell;
using FileExplorer.App.Views;

namespace FileExplorer.App.Services;

/// <summary>Creates and manages the real WPF windows for window view models.</summary>
public sealed class WpfWindowPresenter : IWindowPresenter
{
    private readonly Dictionary<MainViewModel, MainWindow> _windows = new();
    private Func<WindowManager>? _manager;
    private Action<MainViewModel>? _onClosingLast;

    /// <summary>Set once at start-up (the manager and presenter reference each other).</summary>
    public void Bind(Func<WindowManager> manager, Action<MainViewModel> onClosingLast)
    {
        _manager = manager; _onClosingLast = onClosingLast;
    }

    public void Show(MainViewModel vm, WindowBounds? bounds, bool activate)
    {
        var window = new MainWindow(vm, _manager!());
        window.ClosingLast += _onClosingLast!;
        if (bounds is { } b) ApplyBounds(window, b);
        window.ShowActivated = activate;
        _windows[vm] = window;
        window.Show();
    }

    public void Close(MainViewModel vm)
    {
        if (!_windows.Remove(vm, out var w)) return;
        w.Close();
    }

    public void MoveTo(MainViewModel vm, double screenX, double screenY)
    {
        if (!_windows.TryGetValue(vm, out var w)) return;
        w.Left = screenX; w.Top = screenY;
    }

    public void Bring(MainViewModel vm)
    {
        if (!_windows.TryGetValue(vm, out var w)) return;
        w.Activate();
    }

    public MainViewModel? StripWindowAt(double screenX, double screenY, MainViewModel exclude) =>
        _windows.Where(kv => kv.Key != exclude).Select(kv => kv.Key)
            .FirstOrDefault(vm => _windows[vm].StripContains(new Point(screenX, screenY)));

    public WindowBounds GetBounds(MainViewModel vm)
    {
        var w = _windows[vm];
        var r = w.WindowState == WindowState.Normal ? new Rect(w.Left, w.Top, w.Width, w.Height) : w.RestoreBounds;
        return new WindowBounds(r.Left, r.Top, r.Width, r.Height, w.WindowState == WindowState.Maximized);
    }

    private static void ApplyBounds(Window w, WindowBounds b)
    {
        w.WindowStartupLocation = WindowStartupLocation.Manual;
        w.Left = b.Left; w.Top = b.Top; w.Width = b.Width; w.Height = b.Height;
        if (b.Maximized) w.WindowState = WindowState.Maximized;
    }
}
