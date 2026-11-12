using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using FileExplorer.App.Controls;
using FileExplorer.App.Shell;
using FileExplorer.App.Tabs;

namespace FileExplorer.App.Views;

/// <summary>Shows a tab's view only while it is the active tab (all tab views stay alive to keep scroll/selection).</summary>
public sealed class ActiveTabVisibilityConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) =>
        ReferenceEquals(values[0], values[1]) ? Visibility.Visible : Visibility.Collapsed;

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly WindowManager _manager;

    public MainWindow(MainViewModel vm, WindowManager manager)
    {
        _vm = vm; _manager = manager;
        DataContext = vm;
        InitializeComponent();
        Sidebar.ItemsSource = SidebarBuilder.Build(vm.Features);
        FeatureMenuButton.Visibility = vm.Features.Commands.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        Activated += (_, _) => _manager.Activate(_vm);
        Closing += OnClosing;
        Closed += (_, _) => _manager.NotifyWindowClosed(_vm);
        PreviewKeyDown += OnWindowKey;
        Strip.ItemContainerStyle = BuildTabStyle();
        vm.Features.SidebarChanged += () => Dispatcher.BeginInvoke(new Action(() => Sidebar.ItemsSource = SidebarBuilder.Build(vm.Features)));
        BuildRightPanels();
    }

    /// <summary>Feature-provided right-hand panels (preview). Shown only while a folder tab is active.</summary>
    private void BuildRightPanels()
    {
        if (_vm.Features.RightPanels.Count == 0) return;
        var stack = new Grid();   // the first panel (preview) takes the free height, later ones their own height
        int row = 0;
        foreach (var factory in _vm.Features.RightPanels)
        {
            stack.RowDefinitions.Add(new RowDefinition { Height = row == 0 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
            var panel = factory(_vm);
            Grid.SetRow(panel, row++);
            stack.Children.Add(panel);
        }
        RightHost.Child = stack;
        void Sync() => RightHost.Visibility = _vm.ActiveBrowser is null ? Visibility.Collapsed : Visibility.Visible;
        _vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(MainViewModel.ActiveBrowser)) Sync(); };
        Sync();
    }

    /// <summary>Tab context menu is built in code: handlers declared inside a XAML style break generated connection ids.</summary>
    private Style BuildTabStyle()
    {
        var menu = new ContextMenu();
        AddItem(menu, "Move to new window", TabMenu_MoveToNewWindow);
        AddItem(menu, "Duplicate tab", TabMenu_Duplicate);
        AddItem(menu, "Close others", TabMenu_CloseOthers);
        var style = new Style(typeof(TabStripItem), (Style?)FindResource(typeof(TabStripItem)));
        style.Setters.Add(new Setter(ContextMenuProperty, menu));
        return style;
    }

    private static void AddItem(ContextMenu menu, string header, RoutedEventHandler click)
    {
        var item = new MenuItem { Header = header };
        item.Click += click;
        menu.Items.Add(item);
    }

    private void Min_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Max_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    /// <summary>The window draws its own title bar: keep the maximize glyph and the frame in step with the window state.</summary>
    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        bool max = WindowState == WindowState.Maximized;
        MaxBtn.Content = max ? "\uE923" : "\uE922";
        MaxBtn.ToolTip = max ? "Restore" : "Maximize";
        var t = SystemParameters.WindowResizeBorderThickness;
        Frame.Margin = max ? new Thickness(t.Left, t.Top, t.Right, t.Bottom) : new Thickness(0);
        Frame.BorderThickness = max ? new Thickness(0) : new Thickness(1);
    }

    public event Action<MainViewModel>? ClosingLast;

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_manager.Windows.Count == 1) ClosingLast?.Invoke(_vm);
    }

    private void OnWindowKey(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.L) { Crumbs.BeginEdit(); e.Handled = true; }
    }

    /// <summary>True when a screen point (in DIPs) is over this window's tab strip row.</summary>
    public bool StripContains(Point screenDips)
    {
        if (!IsVisible || WindowState == WindowState.Minimized) return false;
        var source = PresentationSource.FromVisual(Strip);
        var px = source?.CompositionTarget is { } t ? t.TransformToDevice.Transform(screenDips) : screenDips;
        var local = Strip.PointFromScreen(px);
        var row = (FrameworkElement)Strip.Parent;
        var rowTop = row.TranslatePoint(new Point(0, 0), Strip).Y;
        return local.Y >= rowTop && local.Y <= rowTop + row.ActualHeight && local.X >= 0 && local.X <= ActualWidth;
    }

    private void SidebarItem_Click(object sender, MouseButtonEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is SidebarItem item) item.Activate(_vm);
    }

    private void Error_Click(object sender, MouseButtonEventArgs e) => _vm.ShowErrorDetailsCommand.Execute(null);

    private void FeatureMenu_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        foreach (var cmd in _vm.Features.Commands)
        {
            var item = new MenuItem { Header = cmd.Title, InputGestureText = cmd.Gesture ?? "" };
            var captured = cmd;
            item.Click += (_, _) => _vm.RunFeatureCommandCommand.Execute(captured);
            menu.Items.Add(item);
        }
        menu.PlacementTarget = (UIElement)sender;
        menu.IsOpen = true;
    }

    private ITabViewModel? MenuTab(object sender) => ((FrameworkElement)sender).DataContext as ITabViewModel;

    private void TabMenu_MoveToNewWindow(object sender, RoutedEventArgs e) => _vm.MoveTabToNewWindowCommand.Execute(MenuTab(sender));
    private void TabMenu_Duplicate(object sender, RoutedEventArgs e) => _vm.DuplicateTabCommand.Execute(MenuTab(sender));
    private void TabMenu_CloseOthers(object sender, RoutedEventArgs e) => _vm.CloseOthersCommand.Execute(MenuTab(sender));
}
