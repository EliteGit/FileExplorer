using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace FileExplorer.App.Features.DiskUsage;

public sealed class FractionToWidth : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is double d ? Math.Max(1, d * double.Parse((string)parameter, CultureInfo.InvariantCulture)) : 0.0;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class HexToBrush : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is string s ? new SolidColorBrush((Color)ColorConverter.ConvertFromString(s)) : Brushes.Gray;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class CountVisibility : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is int n && n > 0 ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public partial class DiskUsageTabView : UserControl
{
    private DiskUsageTabViewModel? _vm;

    public DiskUsageTabView()
    {
        Resources.Add("CountVis", new CountVisibility());
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Map.NodeActivated += id => Map.ZoomTo(id);
        Map.ContextRequested += _ => ShowNodeMenu(Map);
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_vm is not null) { _vm.PropertyChanged -= OnVmChanged; _vm.RevealRequested -= OnReveal; _vm.TreeChangedExternally -= Relayout; }
        _vm = e.NewValue as DiskUsageTabViewModel;
        if (_vm is null) return;
        _vm.PropertyChanged += OnVmChanged;
        _vm.RevealRequested += OnReveal;
        _vm.TreeChangedExternally += Relayout;
        Map.Bind(_vm.Tree, _vm.Hub);
    }

    private void OnVmChanged(object? s, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DiskUsageTabViewModel.Tree)) Map.Bind(_vm!.Tree, _vm.Hub);
    }

    private void Relayout() => Map.Relayout();

    /// <summary>Scrolls the tree to the node the treemap or table selected.</summary>
    private void OnReveal(int id) =>
        Dispatcher.BeginInvoke(new Action(() => (Tree.ItemContainerGenerator.ContainerFromItem(_vm!.TopNodes.FirstOrDefault(n => n.Id == id)) as TreeViewItem)?.BringIntoView()),
            System.Windows.Threading.DispatcherPriority.Background);

    private void ZoomOut_Click(object sender, RoutedEventArgs e) => Map.ZoomOut();

    private void ShowNodeMenu(FrameworkElement target)
    {
        var menu = (ContextMenu)FindResource("NodeMenu");
        menu.PlacementTarget = target;
        menu.IsOpen = true;
    }

    private void Menu_Open(object s, RoutedEventArgs e) => _vm?.OpenCommand.Execute(null);
    private void Menu_Reveal(object s, RoutedEventArgs e) => _vm?.RevealCommand.Execute(null);
    private void Menu_CopyPath(object s, RoutedEventArgs e) => _vm?.CopyPathCommand.Execute(null);
    private void Menu_Properties(object s, RoutedEventArgs e) => _vm?.PropertiesCommand.Execute(null);
    private void Menu_Delete(object s, RoutedEventArgs e) => _vm?.DeleteSelectedCommand.Execute(null);
}
