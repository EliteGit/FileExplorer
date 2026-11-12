using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FileExplorer.Core.Common;

namespace FileExplorer.App.Controls;

/// <summary>Breadcrumbs that turn into an editable path box when the empty area is clicked (one control, toggled).</summary>
public sealed class BreadcrumbBar : Grid
{
    public static readonly DependencyProperty PathProperty = DependencyProperty.Register(
        nameof(Path), typeof(string), typeof(BreadcrumbBar), new PropertyMetadata("", (d, _) => ((BreadcrumbBar)d).Rebuild()));

    public static readonly DependencyProperty NavigateCommandProperty =
        DependencyProperty.Register(nameof(NavigateCommand), typeof(ICommand), typeof(BreadcrumbBar));

    private readonly StackPanel _crumbs = new() { Orientation = Orientation.Horizontal };
    private readonly Border _crumbHost = new() { CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1), Padding = new Thickness(4, 2, 4, 2), Background = System.Windows.Media.Brushes.Transparent };
    private readonly TextBox _edit = new() { Visibility = Visibility.Collapsed, VerticalContentAlignment = VerticalAlignment.Center };

    public BreadcrumbBar()
    {
        _crumbHost.SetResourceReference(Border.BorderBrushProperty, "Line");
        _crumbHost.SetResourceReference(Border.BackgroundProperty, "Bg");
        _crumbHost.Child = _crumbs;
        _crumbHost.MouseLeftButtonDown += (_, e) => { if (e.OriginalSource == _crumbHost || e.OriginalSource == _crumbs) BeginEdit(); };
        Children.Add(_crumbHost);
        Children.Add(_edit);
        _edit.KeyDown += OnEditKey;
        _edit.LostKeyboardFocus += (_, _) => EndEdit();
        ClipToBounds = true;
    }

    public string Path { get => (string)GetValue(PathProperty); set => SetValue(PathProperty, value); }
    public ICommand? NavigateCommand { get => (ICommand?)GetValue(NavigateCommandProperty); set => SetValue(NavigateCommandProperty, value); }

    public void BeginEdit()
    {
        _edit.Text = Path;
        _edit.Visibility = Visibility.Visible;
        _crumbHost.Visibility = Visibility.Collapsed;
        _edit.Focus();
        _edit.SelectAll();
    }

    private void EndEdit()
    {
        _edit.Visibility = Visibility.Collapsed;
        _crumbHost.Visibility = Visibility.Visible;
    }

    private void OnEditKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { EndEdit(); e.Handled = true; }
        else if (e.Key == Key.Enter) { var text = _edit.Text; EndEdit(); Go(text); e.Handled = true; }
    }

    private void Go(string path) => NavigateCommand?.Execute(path);

    private void Rebuild()
    {
        _crumbs.Children.Clear();
        _crumbs.Children.Add(Crumb("This PC", ""));
        if (Path.Length == 0) return;
        foreach (var seg in PathUtil.Segments(Path))
        {
            _crumbs.Children.Add(Separator());
            _crumbs.Children.Add(Crumb(PathUtil.IsRoot(seg) ? seg.TrimEnd('\\') : PathUtil.FileName(seg), seg));
        }
    }

    private Button Crumb(string text, string target)
    {
        var b = new Button { Content = text, Padding = new Thickness(6, 1, 6, 1), MinHeight = 22, MinWidth = 0 };
        b.Click += (_, _) => Go(target);
        return b;
    }

    private static TextBlock Separator()
    {
        var t = new TextBlock { Text = "›", VerticalAlignment = VerticalAlignment.Center };
        t.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        return t;
    }
}
