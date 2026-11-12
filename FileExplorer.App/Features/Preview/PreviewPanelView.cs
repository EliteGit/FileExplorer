using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using FileExplorer.App.Shell;

namespace FileExplorer.App.Features.Preview;

/// <summary>The preview panel, built in code: header, preview content, key/value info.</summary>
public sealed class PreviewPanelView : UserControl
{
    private readonly PreviewPanelViewModel _vm;
    private readonly ErrorReporter _errors;
    private readonly TextBlock _title = new() { FontSize = 14, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _subtitle = new() { FontSize = 12, Margin = new Thickness(0, 0, 0, 10) };
    private readonly ContentControl _content = new() { Margin = new Thickness(0, 0, 0, 10) };
    private readonly TextBlock _message = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) };
    private readonly Grid _info = new();

    public PreviewPanelView(PreviewPanelViewModel vm, ErrorReporter errors)
    {
        _vm = vm; _errors = errors;
        DataContext = vm;
        _subtitle.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        _message.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        _info.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _info.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var stack = new StackPanel { Margin = new Thickness(14) };
        foreach (var c in new UIElement[] { _title, _subtitle, _content, _message, _info }) stack.Children.Add(c);
        Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = stack };
        vm.PropertyChanged += OnChanged;
        vm.Rows.CollectionChanged += (_, _) => RebuildInfo();
        Render();
    }

    private void OnChanged(object? s, PropertyChangedEventArgs e) => Render();

    private void Render()
    {
        _title.Text = _vm.Title;
        _subtitle.Text = _vm.Subtitle;
        _content.Content = null;
        _message.Text = _vm.Message;
        if (_vm.Entry is { } entry && _vm.Provider is { } provider)
        {
            var view = provider.CreateView(entry);
            if (view.IsOk) _content.Content = view.Value;
            else { _message.Text = view.Error.Message; _errors.Report(view.Error); }
        }
        RebuildInfo();
    }

    private void RebuildInfo()
    {
        _info.Children.Clear(); _info.RowDefinitions.Clear();
        int row = 0;
        foreach (var r in _vm.Rows)
        {
            _info.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var k = new TextBlock { Text = r.Key, FontSize = 12, Margin = new Thickness(0, 2, 12, 2) };
            k.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            var v = new TextBlock { Text = r.Value, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 2) };
            Grid.SetRow(k, row); Grid.SetRow(v, row); Grid.SetColumn(v, 1);
            _info.Children.Add(k); _info.Children.Add(v);
            row++;
        }
    }
}
