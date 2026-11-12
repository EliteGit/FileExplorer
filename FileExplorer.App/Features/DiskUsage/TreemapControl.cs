using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using FileExplorer.Core.Common;
using FileExplorer.Core.Scan;
using FileExplorer.Core.Treemap;

namespace FileExplorer.App.Features.DiskUsage;

/// <summary>Draws the treemap. Re-layouts only on resize / data change and redraws on selection change.</summary>
public sealed class TreemapControl : FrameworkElement
{
    private static readonly Dictionary<TypeGroup, Brush> GroupBrushes = Enum.GetValues<TypeGroup>().ToDictionary(g => g, g => Frozen(FileTypeCatalog.ColorOf(g)));
    private static readonly Brush FreeBrush = Frozen("#9AA3B2"), UnknownBrush = Frozen("#C7CCD6"), DirBrush = Frozen("#E0903A"), OtherBrush = Frozen("#A9B0BD");
    private static readonly Typeface Face = new("Segoe UI");

    private FolderSizeTree? _tree;
    private SelectionHub? _hub;
    private TreemapModel? _model;
    private int _viewRoot = FolderSizeTree.RootId;

    public TreemapControl()
    {
        ClipToBounds = true;
        SizeChanged += (_, _) => Relayout();
        MouseMove += OnMouseMove;
        MouseLeftButtonUp += OnClick;
        MouseDown += OnMouseDown;
        System.Windows.Controls.ToolTipService.SetInitialShowDelay(this, 300);
    }

    public event Action<int>? NodeActivated;     // double-click on a directory: zoom in
    public event Action<int>? ContextRequested;

    public int ViewRoot => _viewRoot;

    public void Bind(FolderSizeTree? tree, SelectionHub hub)
    {
        _tree = tree; _viewRoot = FolderSizeTree.RootId;
        if (_hub is not null) _hub.Changed -= OnSelectionChanged;
        _hub = hub; hub.Changed += OnSelectionChanged;
        Relayout();
    }

    public void Relayout()
    {
        _model = _tree is null || ActualWidth < 4 || ActualHeight < 4 ? null
            : TreemapModel.Build(_tree, _viewRoot, new RectD(0, 0, ActualWidth, ActualHeight));
        InvalidateVisual();
    }

    public void ZoomTo(int node) { _viewRoot = node; Relayout(); }
    public void ZoomOut() { if (_tree is not null && _viewRoot != FolderSizeTree.RootId) ZoomTo(_tree.Parent(_viewRoot)); }

    private void OnSelectionChanged(object? source) => InvalidateVisual();

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle((Brush)FindResource("Panel2"), null, new Rect(0, 0, ActualWidth, ActualHeight));
        if (_model is null || _tree is null) return;
        var line = (Brush)FindResource("Panel");
        var border = new Pen(line, 1); border.Freeze();
        string? ext = _hub?.Extension;
        foreach (var c in _model.Cells)
        {
            var r = new Rect(c.Rect.X, c.Rect.Y, c.Rect.W, c.Rect.H);
            if (c.Expanded) { dc.DrawRectangle(null, border, r); continue; }
            var brush = BrushFor(c);
            bool dim = ext is not null && c.NodeId >= 0 && !c.IsDir && !_tree.Index.Extension(c.NodeId).Equals(ext, StringComparison.OrdinalIgnoreCase);
            if (dim) dc.PushOpacity(0.25);
            dc.DrawRectangle(brush, border, r);
            if (dim) dc.Pop();
            DrawLabel(dc, c, r);
        }
        DrawSelection(dc);
    }

    private Brush BrushFor(TreemapCell c) => c.NodeId switch
    {
        FolderSizeTree.FreeId => FreeBrush, FolderSizeTree.UnknownId => UnknownBrush, FolderSizeTree.OtherId => OtherBrush,
        _ => c.IsDir ? DirBrush : GroupBrushes[c.Group],
    };

    private void DrawLabel(DrawingContext dc, TreemapCell c, Rect r)
    {
        if (r.Width < 64 || r.Height < 30) return;
        var name = _tree!.Name(c.NodeId);
        var brush = c.NodeId is FolderSizeTree.FreeId or FolderSizeTree.UnknownId ? (Brush)FindResource("Muted") : Brushes.White;
        var text = new FormattedText($"{name}\n{SizeFormatter.Format(_tree.Size(c.NodeId))}", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Face, 11, brush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip)
        { MaxTextWidth = Math.Max(1, r.Width - 8), MaxTextHeight = Math.Max(1, r.Height - 6), Trimming = TextTrimming.CharacterEllipsis };
        dc.DrawText(text, new Point(r.X + 4, r.Y + 3));
    }

    private void DrawSelection(DrawingContext dc)
    {
        if (_hub?.NodeId is not { } id || _model?.CellOf(id) is not { } cell) return;
        var pen = new Pen((Brush)FindResource("Fg"), 2); pen.Freeze();
        dc.DrawRectangle(null, pen, new Rect(cell.Rect.X + 1, cell.Rect.Y + 1, Math.Max(0, cell.Rect.W - 2), Math.Max(0, cell.Rect.H - 2)));
    }

    // ---- mouse ----
    private int? Hit(MouseEventArgs e)
    {
        var p = e.GetPosition(this);
        return _model?.HitTest(p.X, p.Y);
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (Hit(e) is not { } id || _tree is null) { ToolTip = null; return; }
        long size = _tree.Size(id);
        double pct = 100.0 * size / Math.Max(1, _tree.Size(_viewRoot));
        ToolTip = $"{_tree.Name(id)}\n{SizeFormatter.Format(size)} · {pct:0.0}%" + (id >= 0 ? $"\n{_tree.FullPath(id)}" : "");
    }

    private void OnClick(object sender, MouseButtonEventArgs e)
    {
        if (Hit(e) is not { } id) return;
        if (e.ClickCount == 2 && id >= 0 && _tree!.IsDir(id)) { NodeActivated?.Invoke(id); return; }
        _hub?.SelectNode(id, this);
        InvalidateVisual();
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Right || Hit(e) is not { } id) return;
        _hub?.SelectNode(id, this);
        InvalidateVisual();
        ContextRequested?.Invoke(id);
    }

    private static Brush Frozen(string hex)
    {
        var b = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
        b.Freeze();
        return b;
    }
}
