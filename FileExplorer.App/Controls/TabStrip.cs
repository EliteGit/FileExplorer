using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using FileExplorer.App.Shell;
using FileExplorer.App.Tabs;

namespace FileExplorer.App.Controls;

public sealed class TabStripItem : ContentControl
{
    public static readonly DependencyProperty IsSelectedProperty =
        DependencyProperty.Register(nameof(IsSelected), typeof(bool), typeof(TabStripItem));

    public bool IsSelected { get => (bool)GetValue(IsSelectedProperty); set => SetValue(IsSelectedProperty, value); }
}

/// <summary>Custom tab strip (not a TabControl). All drag logic is delegated to TabDragController.</summary>
public sealed class TabStrip : ItemsControl
{
    public static readonly DependencyProperty SelectedTabProperty = DependencyProperty.Register(
        nameof(SelectedTab), typeof(object), typeof(TabStrip),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, _) => ((TabStrip)d).RefreshSelection()));

    private TabDragController? _drag;
    private WindowTabDragHost? _host;

    public TabStrip()
    {
        Focusable = true;
        ItemContainerGenerator.StatusChanged += (_, _) => RefreshSelection();
    }

    public object? SelectedTab { get => GetValue(SelectedTabProperty); set => SetValue(SelectedTabProperty, value); }

    protected override bool IsItemItsOwnContainerOverride(object item) => item is TabStripItem;
    protected override DependencyObject GetContainerForItemOverride() => new TabStripItem();

    private MainViewModel? Window => DataContext as MainViewModel;

    private void RefreshSelection()
    {
        foreach (var item in Items)
            if (ItemContainerGenerator.ContainerFromItem(item) is TabStripItem c) c.IsSelected = ReferenceEquals(item, SelectedTab);
    }

    // ---- pointer handling: forward to the controller ----
    private TabDragController Controller()
    {
        if (_drag is not null) return _drag;
        _host = new WindowTabDragHost(Window!, IndexAtX, () => ActualHeight);
        return _drag = new TabDragController(_host);
    }

    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseLeftButtonDown(e);
        if (Window is null || IsInsideButton(e.OriginalSource as DependencyObject)) return;
        var item = FindItem(e.OriginalSource as DependencyObject);
        if (item is null) return;
        SelectedTab = item.DataContext;
        Focus();
        CaptureMouse();
        Controller().PointerDown(item.DataContext, ToPt(e.GetPosition(this)));
    }

    protected override void OnPreviewMouseMove(MouseEventArgs e)
    {
        base.OnPreviewMouseMove(e);
        if (_drag is null || _drag.State == DragState.Idle || !IsMouseCaptured) return;
        var pos = e.GetPosition(this);
        _drag.PointerMove(ToPt(pos), ToPt(ToScreenDips(pos)));
    }

    protected override void OnPreviewMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseLeftButtonUp(e);
        if (_drag is null || _drag.State == DragState.Idle) return;
        _drag.PointerUp();
        ReleaseMouseCapture();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Key != Key.Escape || _drag is null || _drag.State == DragState.Idle) return;
        _drag.Cancel();
        ReleaseMouseCapture();
        e.Handled = true;
    }

    // ---- geometry ----
    private int IndexAtX(double x)
    {
        int last = Items.Count - 1;
        for (int i = 0; i <= last; i++)
        {
            if (ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement c) continue;
            var left = c.TranslatePoint(new Point(0, 0), this).X;
            if (x < left + c.ActualWidth) return i;
        }
        return last;
    }

    private Point ToScreenDips(Point local)
    {
        var px = PointToScreen(local);
        var source = PresentationSource.FromVisual(this);
        return source?.CompositionTarget is { } t ? t.TransformFromDevice.Transform(px) : px;
    }

    private static Pt ToPt(Point p) => new(p.X, p.Y);

    private static TabStripItem? FindItem(DependencyObject? d)
    {
        while (d is not null && d is not TabStripItem) d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        return d as TabStripItem;
    }

    private static bool IsInsideButton(DependencyObject? d)
    {
        while (d is not null && d is not TabStripItem)
        {
            if (d is System.Windows.Controls.Primitives.ButtonBase) return true;
            d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }
        return false;
    }
}
