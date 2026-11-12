using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using FileExplorer.App.Tabs;

namespace FileExplorer.App.Views;

/// <summary>Hosts one or two PaneViews with a draggable divider. Views are cached per pane so scroll state survives swaps.</summary>
public partial class BrowserTabView : UserControl
{
    private readonly Dictionary<PaneViewModel, PaneView> _views = new();
    private BrowserTabViewModel? _tab;

    public BrowserTabView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_tab is not null)
        {
            _tab.Panes.CollectionChanged -= OnPanesChanged;
            _tab.PropertyChanged -= OnTabChanged;
        }
        _tab = e.NewValue as BrowserTabViewModel;
        if (_tab is not null)
        {
            _tab.Panes.CollectionChanged += OnPanesChanged;
            _tab.PropertyChanged += OnTabChanged;
        }
        Rebuild();
    }

    private void OnPanesChanged(object? s, NotifyCollectionChangedEventArgs e) => Rebuild();

    private void OnTabChanged(object? s, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(BrowserTabViewModel.Orientation)) Rebuild();
        if (e.PropertyName is nameof(BrowserTabViewModel.SplitRatio)) ApplyRatio();
    }

    private PaneView ViewFor(PaneViewModel pane)
    {
        if (_views.TryGetValue(pane, out var v)) return v;
        v = new PaneView { DataContext = pane };
        _views[pane] = v;
        return v;
    }

    private void Rebuild()
    {
        Root.Children.Clear();
        Root.RowDefinitions.Clear();
        Root.ColumnDefinitions.Clear();
        if (_tab is null) return;
        foreach (var gone in _views.Keys.Where(p => !_tab.Panes.Contains(p)).ToList()) _views.Remove(gone);

        bool split = _tab.IsSplit;
        foreach (var pane in _tab.Panes) ViewFor(pane).ShowChrome = split;
        if (!split) { Place(ViewFor(_tab.Panes[0]), 0); return; }

        bool side = _tab.Orientation == SplitOrientation.Horizontal;
        AddDefinitions(side);
        Place(ViewFor(_tab.Panes[0]), 0, side);
        Place(Splitter(side), 1, side);
        Place(ViewFor(_tab.Panes[1]), 2, side);
        ApplyRatio();
    }

    private void AddDefinitions(bool side)
    {
        for (int i = 0; i < 3; i++)
        {
            var size = i == 1 ? GridLength.Auto : new GridLength(1, GridUnitType.Star);
            if (side) Root.ColumnDefinitions.Add(new ColumnDefinition { Width = size, MinWidth = i == 1 ? 0 : 120 });
            else Root.RowDefinitions.Add(new RowDefinition { Height = size, MinHeight = i == 1 ? 0 : 80 });
        }
    }

    private void Place(UIElement el, int slot, bool side = true)
    {
        Grid.SetColumn(el, side ? slot : 0);
        Grid.SetRow(el, side ? 0 : slot);
        Root.Children.Add(el);
    }

    private GridSplitter Splitter(bool side)
    {
        var s = new GridSplitter
        {
            HorizontalAlignment = side ? HorizontalAlignment.Center : HorizontalAlignment.Stretch,
            VerticalAlignment = side ? VerticalAlignment.Stretch : VerticalAlignment.Center,
            Width = side ? 6 : double.NaN, Height = side ? double.NaN : 6,
            ToolTip = "Drag to resize · double-click to reset 50/50",
            ResizeBehavior = GridResizeBehavior.PreviousAndNext
        };
        s.SetResourceReference(BackgroundProperty, "Panel2");
        s.DragCompleted += (_, _) => StoreRatio();
        s.MouseDoubleClick += (_, _) => (Window.GetWindow(this)?.DataContext as Shell.MainViewModel)?.ResetSplitCommand.Execute(null);
        return s;
    }

    private void ApplyRatio()
    {
        if (_tab is null || !_tab.IsSplit) return;
        var a = new GridLength(_tab.SplitRatio, GridUnitType.Star);
        var b = new GridLength(1 - _tab.SplitRatio, GridUnitType.Star);
        if (_tab.Orientation == SplitOrientation.Horizontal && Root.ColumnDefinitions.Count == 3)
        { Root.ColumnDefinitions[0].Width = a; Root.ColumnDefinitions[2].Width = b; }
        else if (Root.RowDefinitions.Count == 3)
        { Root.RowDefinitions[0].Height = a; Root.RowDefinitions[2].Height = b; }
    }

    private void StoreRatio()
    {
        if (_tab is null) return;
        bool side = _tab.Orientation == SplitOrientation.Horizontal;
        double first = side ? Root.ColumnDefinitions[0].ActualWidth : Root.RowDefinitions[0].ActualHeight;
        double second = side ? Root.ColumnDefinitions[2].ActualWidth : Root.RowDefinitions[2].ActualHeight;
        if (first + second > 0) _tab.SplitRatio = Math.Clamp(first / (first + second), 0.1, 0.9);
    }
}
