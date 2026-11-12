using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
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
    private TabStripItem? _dragItem;
    private double _grabX;
    private DragGhost? _ghost;
    private AdornerLayer? _layer;
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
        _dragItem = item;
        _grabX = e.GetPosition(item).X;
        Controller().PointerDown(item.DataContext, ToPt(e.GetPosition(this)));
    }

    protected override void OnPreviewMouseMove(MouseEventArgs e)
    {
        base.OnPreviewMouseMove(e);
        if (_drag is null || _drag.State == DragState.Idle || !IsMouseCaptured) return;
        var pos = e.GetPosition(this);
        var before = SnapshotPositions();
        _drag.PointerMove(ToPt(pos), ToPt(ToScreenDips(pos)));
        if (_drag.State == DragState.Reordering) { UpdateLayout(); AnimateShifts(before); ShowGhost(pos.X); }
        else RemoveGhost(animateTo: null);
    }

    protected override void OnPreviewMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseLeftButtonUp(e);
        if (_drag is null || _drag.State == DragState.Idle) return;
        var state = _drag.State;
        _drag.PointerUp();
        ReleaseMouseCapture();
        EndGhost(snap: state == DragState.Reordering);
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Key != Key.Escape || _drag is null || _drag.State == DragState.Idle) return;
        _drag.Cancel();
        ReleaseMouseCapture();
        EndGhost(snap: false);
        e.Handled = true;
    }

    // ---- drag polish: ghost of the dragged tab and sliding neighbours ----
    private Dictionary<TabStripItem, double> SnapshotPositions()
    {
        var map = new Dictionary<TabStripItem, double>();
        foreach (var item in Items)
            if (ItemContainerGenerator.ContainerFromItem(item) is TabStripItem c) map[c] = c.TranslatePoint(new Point(0, 0), this).X;
        return map;
    }

    /// <summary>After a reorder, neighbours start from where they were and slide into their new slot.</summary>
    private void AnimateShifts(Dictionary<TabStripItem, double> before)
    {
        foreach (var (container, oldX) in before)
        {
            double delta = oldX - container.TranslatePoint(new Point(0, 0), this).X;
            if (Math.Abs(delta) < 0.5) continue;
            var t = container.RenderTransform as TranslateTransform ?? new TranslateTransform();
            container.RenderTransform = t;
            t.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(delta, 0, TimeSpan.FromMilliseconds(140)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } });
        }
    }

    private void ShowGhost(double pointerX)
    {
        if (_dragItem is null) return;
        if (_ghost is null)
        {
            _layer ??= AdornerLayer.GetAdornerLayer(this);
            if (_layer is null) return;               // no adorner layer: the drag still works, just without the thumbnail
            _ghost = new DragGhost(this, Render(_dragItem));
            _layer.Add(_ghost);
            _dragItem.Opacity = 0.3;
        }
        _ghost.SetX(Math.Clamp(pointerX - _grabX, 0, Math.Max(0, ActualWidth - _ghost.Thumbnail.Width)));
    }

    private static RenderTargetBitmap Render(FrameworkElement el)
    {
        var dpi = VisualTreeHelper.GetDpi(el);
        var bmp = new RenderTargetBitmap(Math.Max(1, (int)(el.ActualWidth * dpi.DpiScaleX)), Math.Max(1, (int)(el.ActualHeight * dpi.DpiScaleY)), 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
        bmp.Render(el);
        return bmp;
    }

    /// <summary>On drop the thumbnail slides into the tab's final slot, then disappears.</summary>
    private void EndGhost(bool snap)
    {
        if (_ghost is null) { RestoreDragItem(); return; }
        double? target = snap && _dragItem is not null ? _dragItem.TranslatePoint(new Point(0, 0), this).X : null;
        RemoveGhost(target);
    }

    private void RemoveGhost(double? animateTo)
    {
        var ghost = _ghost; _ghost = null;
        RestoreDragItem();
        if (ghost is null) return;
        if (animateTo is { } x)
        {
            var anim = new DoubleAnimation(ghost.X, x, TimeSpan.FromMilliseconds(110)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
            anim.Completed += (_, _) => _layer?.Remove(ghost);
            ghost.BeginAnimation(DragGhost.XProperty, anim);
        }
        else _layer?.Remove(ghost);
    }

    private void RestoreDragItem()
    {
        if (_dragItem is not null) _dragItem.Opacity = 1;
        _dragItem = null;
    }

    private sealed class DragGhost : Adorner
    {
        public static readonly DependencyProperty XProperty = DependencyProperty.Register(nameof(X), typeof(double), typeof(DragGhost),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

        public DragGhost(UIElement owner, BitmapSource thumbnail) : base(owner) { Thumbnail = thumbnail; IsHitTestVisible = false; }

        public BitmapSource Thumbnail { get; }
        public double X { get => (double)GetValue(XProperty); set => SetValue(XProperty, value); }
        public void SetX(double x) => X = x;

        protected override void OnRender(DrawingContext dc)
        {
            double w = Thumbnail.Width * 96 / Thumbnail.DpiX, h = Thumbnail.Height * 96 / Thumbnail.DpiY;
            dc.PushOpacity(0.8);
            dc.DrawImage(Thumbnail, new Rect(X, 0, w, h));
            dc.Pop();
        }
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
