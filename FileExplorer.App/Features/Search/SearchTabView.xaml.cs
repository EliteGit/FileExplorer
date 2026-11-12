using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FileExplorer.App.Shell;

namespace FileExplorer.App.Features.Search;

public partial class SearchTabView : UserControl
{
    private SearchTabViewModel? _vm;

    public SearchTabView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (_vm is not null) _vm.FocusRequested -= FocusQuery;
            _vm = e.NewValue as SearchTabViewModel;
            if (_vm is not null) _vm.FocusRequested += FocusQuery;
        };
        Loaded += (_, _) => FocusQuery();
    }

    private void FocusQuery()
    {
        QueryBox.Focus();
        QueryBox.SelectAll();
    }

    private MainViewModel? Main => Window.GetWindow(this)?.DataContext as MainViewModel;
    private SearchRowViewModel? Selected => List.SelectedItem as SearchRowViewModel;

    private void List_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && System.Windows.Media.VisualTreeHelper.GetParent(d) is not null) Menu_Open(sender, e);
    }

    private void List_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { Menu_Open(sender, e); e.Handled = true; }
    }

    private void Menu_Open(object sender, RoutedEventArgs e)
    {
        if (Selected is { } r) _vm?.OpenCommand.Execute(r.FullPath);
    }

    private void Menu_Reveal(object sender, RoutedEventArgs e)
    {
        if (Selected is { } r) _vm?.RevealCommand.Execute(r.FullPath);
    }

    private void Menu_CopyPath(object sender, RoutedEventArgs e)
    {
        if (Selected is { } r) Clipboard.SetText(r.FullPath);
    }

    private void Menu_ShowInFolder(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } r || Main is not { } main) return;
        main.NewTab();
        main.NavigateToCommand.Execute(r.Folder);
    }

    private void Saved_Click(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        var menu = new ContextMenu();
        foreach (var name in _vm.SavedNames)
        {
            var item = new MenuItem { Header = name };
            var captured = name;
            item.Click += (_, _) => _vm.LoadSavedCommand.Execute(captured);
            menu.Items.Add(item);
        }
        if (menu.Items.Count == 0) menu.Items.Add(new MenuItem { Header = "No saved searches", IsEnabled = false });
        menu.PlacementTarget = SavedButton;
        menu.IsOpen = true;
    }
}

/// <summary>Visible when the bound number is greater than zero.</summary>
public sealed class CountToVisibility : System.Windows.Data.IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) =>
        value is int n && n > 0 ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) =>
        throw new NotSupportedException();
}
