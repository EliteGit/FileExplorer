using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FileExplorer.App.Shell;
using FileExplorer.App.Tabs;
using FileExplorer.Core.Fs;

namespace FileExplorer.App.Views;

public partial class PaneView : UserControl
{
    public static readonly DependencyProperty ShowChromeProperty = DependencyProperty.Register(
        nameof(ShowChrome), typeof(bool), typeof(PaneView), new PropertyMetadata(false, (d, _) => ((PaneView)d).UpdateChrome()));

    private PaneViewModel? _pane;

    public PaneView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    /// <summary>True in split view: shows the path header and the active-pane border.</summary>
    public bool ShowChrome { get => (bool)GetValue(ShowChromeProperty); set => SetValue(ShowChromeProperty, value); }

    private MainViewModel? Main => Window.GetWindow(this)?.DataContext as MainViewModel;

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_pane is not null) _pane.PropertyChanged -= OnPaneChanged;
        _pane = e.NewValue as PaneViewModel;
        if (_pane is not null) _pane.PropertyChanged += OnPaneChanged;
        UpdateChrome();
    }

    private void OnPaneChanged(object? s, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PaneViewModel.IsActive) or nameof(PaneViewModel.Path)) UpdateChrome();
    }

    private void UpdateChrome()
    {
        Header.Visibility = ShowChrome ? Visibility.Visible : Visibility.Collapsed;
        HeaderText.Text = _pane is null ? "" : _pane.IsThisPc ? "This PC" : _pane.Path;
        bool active = ShowChrome && _pane?.IsActive == true;
        Chrome.SetResourceReference(Border.BorderBrushProperty, ShowChrome ? (active ? "Accent" : "Line") : "Panel");
    }

    // ---- list events ----
    private void List_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        _pane?.SetSelection(List.SelectedItems.Cast<FileEntryViewModel>().ToList());

    private void List_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && FindAncestor<ListViewItem>(d) is not null) Main?.OpenSelectedCommand.Execute(null);
    }

    private void Header_Click(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is GridViewColumnHeader { Tag: string tag } && Enum.TryParse<SortColumn>(tag, out var col)) _pane?.SortBy(col);
    }

    private void OnFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_pane is not null) Main?.ActiveBrowser?.Activate(_pane);
    }

    // ---- keyboard ----
    private void List_KeyDown(object sender, KeyEventArgs e)
    {
        var main = Main;
        if (main is null) return;
        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        var alt = (Keyboard.Modifiers & ModifierKeys.Alt) != 0;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        e.Handled = Dispatch(main, key, ctrl, alt);
    }

    private static bool Dispatch(MainViewModel main, Key key, bool ctrl, bool alt)
    {
        if (alt) return DispatchNav(main, key);
        if (ctrl) return DispatchCtrl(main, key);
        switch (key)
        {
            case Key.Enter: main.OpenSelectedCommand.Execute(null); return true;
            case Key.F2: main.RenameSelectedCommand.Execute(null); return true;
            case Key.Delete: main.DeleteSelectedCommand.Execute(null); return true;
            case Key.Back: main.UpCommand.Execute(null); return true;
            case Key.F5: if (main.IsSplit) main.CopyToOtherPaneCommand.Execute(null); else main.RefreshCommand.Execute(null); return true;
            case Key.F6: main.MoveToOtherPaneCommand.Execute(null); return true;
            default: return false;
        }
    }

    private static bool DispatchNav(MainViewModel main, Key key)
    {
        switch (key)
        {
            case Key.Left: main.BackCommand.Execute(null); return true;
            case Key.Right: main.ForwardCommand.Execute(null); return true;
            case Key.Up: main.UpCommand.Execute(null); return true;
            default: return false;
        }
    }

    private static bool DispatchCtrl(MainViewModel main, Key key)
    {
        switch (key)
        {
            case Key.C: main.CopySelectedCommand.Execute(null); return true;
            case Key.X: main.CutSelectedCommand.Execute(null); return true;
            case Key.V: main.PasteCommand.Execute(null); return true;
            case Key.Z: main.UndoCommand.Execute(null); return true;
            default: return false;
        }
    }

    // ---- context menu ----
    private void Menu_Open(object s, RoutedEventArgs e) => Main?.OpenSelectedCommand.Execute(null);
    private void Menu_Reveal(object s, RoutedEventArgs e) => Main?.RevealSelectedCommand.Execute(null);
    private void Menu_Cut(object s, RoutedEventArgs e) => Main?.CutSelectedCommand.Execute(null);
    private void Menu_Copy(object s, RoutedEventArgs e) => Main?.CopySelectedCommand.Execute(null);
    private void Menu_Paste(object s, RoutedEventArgs e) => Main?.PasteCommand.Execute(null);
    private void Menu_Rename(object s, RoutedEventArgs e) => Main?.RenameSelectedCommand.Execute(null);
    private void Menu_Delete(object s, RoutedEventArgs e) => Main?.DeleteSelectedCommand.Execute(null);
    private void Menu_NewFolder(object s, RoutedEventArgs e) => Main?.NewFolderCommand.Execute(null);
    private void Menu_Terminal(object s, RoutedEventArgs e) => Main?.OpenTerminalHereCommand.Execute(null);
    private void Menu_Properties(object s, RoutedEventArgs e) => Main?.ShowPropertiesCommand.Execute(null);

    private static T? FindAncestor<T>(DependencyObject d) where T : DependencyObject
    {
        for (; d is not null; d = System.Windows.Media.VisualTreeHelper.GetParent(d)) if (d is T t) return t;
        return null;
    }
}
