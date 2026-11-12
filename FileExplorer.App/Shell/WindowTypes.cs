using FileExplorer.App.Tabs;

namespace FileExplorer.App.Shell;

public readonly record struct WindowBounds(double Left, double Top, double Width, double Height, bool Maximized);

public sealed record WindowSnapshot(WindowBounds Bounds, List<TabState> Tabs, int ActiveTab);

public sealed record SessionSnapshot(List<WindowSnapshot> Windows);

/// <summary>The only way the WindowManager touches real windows; WPF implements it, tests fake it.</summary>
public interface IWindowPresenter
{
    void Show(MainViewModel window, WindowBounds? bounds, bool activate);
    void Close(MainViewModel window);
    void MoveTo(MainViewModel window, double screenX, double screenY);
    WindowBounds GetBounds(MainViewModel window);
    void Bring(MainViewModel window);
    /// <summary>The window (other than <paramref name="exclude"/>) whose tab strip is under the screen point, if any.</summary>
    MainViewModel? StripWindowAt(double screenX, double screenY, MainViewModel exclude);
}
