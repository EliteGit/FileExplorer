using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using FileExplorer.App.Shell;
using FileExplorer.App.Tabs;
using FileExplorer.Core.Tags;

namespace FileExplorer.App.Features.Tags;

/// <summary>Tags, favorite star and note for the selected item (or the open folder) of the active pane.</summary>
public sealed partial class TagsPanelViewModel : ObservableObject, IDisposable
{
    private readonly MainViewModel _window;
    private readonly ITagStore _store;
    private readonly ErrorReporter _errors;
    private readonly FeatureRegistry _registry;
    private PaneViewModel? _pane;

    [ObservableProperty] private string? _path;
    [ObservableProperty] private bool _isFavorite;
    [ObservableProperty] private string _note = "";
    [ObservableProperty] private string _caption = "";

    public TagsPanelViewModel(MainViewModel window, ITagStore store, ErrorReporter errors, FeatureRegistry registry)
    {
        _window = window; _store = store; _errors = errors; _registry = registry;
        window.PropertyChanged += OnWindowChanged;
        Attach(window.ActivePane);
    }

    public ObservableCollection<string> Tags { get; } = new();

    private void OnWindowChanged(object? s, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.ActivePane)) Attach(_window.ActivePane);
    }

    private void Attach(PaneViewModel? pane)
    {
        if (_pane is not null) { _pane.SelectionChanged -= Refresh; _pane.Navigated -= Refresh; }
        _pane = pane;
        if (_pane is not null) { _pane.SelectionChanged += Refresh; _pane.Navigated += Refresh; }
        Refresh();
    }

    public void Refresh()
    {
        Tags.Clear(); IsFavorite = false; Note = "";
        Path = TargetPath();
        Caption = Path is null ? "Select one item to tag it." : System.IO.Path.GetFileName(Path.TrimEnd('\\')) is { Length: > 0 } n ? n : Path;
        if (Path is null) return;
        var tags = _store.GetTags(Path);
        if (_errors.Check(tags)) foreach (var t in tags.Value) Tags.Add(t);
        var fav = _store.IsFavorite(Path);
        if (_errors.Check(fav)) IsFavorite = fav.Value;
        var note = _store.GetNote(Path);
        if (_errors.Check(note)) Note = note.Value;
    }

    /// <summary>One selected item, or the open folder when nothing is selected. Several items or This PC: nothing to tag.</summary>
    private string? TargetPath()
    {
        if (_pane is null) return null;
        if (_pane.Selection.Count == 1) return _pane.Selection[0].FullPath;
        return _pane.Selection.Count == 0 && !_pane.IsThisPc ? _pane.Path : null;
    }

    public void AddTag(string tag)
    {
        if (Path is null || string.IsNullOrWhiteSpace(tag)) return;
        if (_errors.Check(_store.AddTag(Path, tag))) { Refresh(); _registry.NotifySidebarChanged(); }
    }

    public void RemoveTag(string tag)
    {
        if (Path is null) return;
        if (_errors.Check(_store.RemoveTag(Path, tag))) { Refresh(); _registry.NotifySidebarChanged(); }
    }

    public void SetFavorite(bool value)
    {
        if (Path is null) return;
        if (_errors.Check(_store.SetFavorite(Path, value))) { IsFavorite = value; _registry.NotifySidebarChanged(); }
    }

    public void SaveNote(string text)
    {
        if (Path is null || text == Note) return;
        if (_errors.Check(_store.SetNote(Path, text))) Note = text;
    }

    public void Dispose()
    {
        _window.PropertyChanged -= OnWindowChanged;
        Attach(null);
    }
}

/// <summary>The panel itself, built in code.</summary>
public sealed class TagsPanelView : UserControl
{
    private readonly TagsPanelViewModel _vm;
    private readonly TextBlock _caption = new() { FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly WrapPanel _chips = new();
    private readonly TextBox _add = new() { ToolTip = "Type a tag and press Enter", Margin = new Thickness(0, 6, 0, 0) };
    private readonly ToggleButton _star = new() { Content = "☆ Favorite", Focusable = false, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0), Padding = new Thickness(8, 3, 8, 3) };
    private readonly TextBox _note = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 54, Margin = new Thickness(0, 6, 0, 0), ToolTip = "Note (saved when you leave the box)" };
    private bool _syncing;

    public TagsPanelView(TagsPanelViewModel vm)
    {
        _vm = vm; DataContext = vm;
        var header = new TextBlock { Text = "TAGS & NOTES", FontSize = 10.5, FontWeight = FontWeights.SemiBold };
        header.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        _caption.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        var stack = new StackPanel { Margin = new Thickness(14, 6, 14, 14) };
        stack.Children.Add(new Border { Height = 1, Margin = new Thickness(0, 0, 0, 10) }.WithBrush("Line"));
        foreach (var e in new UIElement[] { header, _caption, _chips, _add, _star, _note }) stack.Children.Add(e);
        Content = stack;
        _add.KeyDown += (_, e) => { if (e.Key == Key.Enter) { _vm.AddTag(_add.Text); _add.Clear(); e.Handled = true; } };
        _star.Click += (_, _) => { if (!_syncing) _vm.SetFavorite(_star.IsChecked == true); };
        _note.LostKeyboardFocus += (_, _) => _vm.SaveNote(_note.Text);
        vm.Tags.CollectionChanged += (_, _) => Render();
        vm.PropertyChanged += (_, _) => Render();
        Render();
    }

    private void Render()
    {
        _syncing = true;
        bool enabled = _vm.Path is not null;
        _caption.Text = _vm.Caption; _add.IsEnabled = enabled; _star.IsEnabled = enabled; _note.IsEnabled = enabled;
        _star.IsChecked = _vm.IsFavorite; _star.Content = _vm.IsFavorite ? "★ Favorite" : "☆ Favorite";
        if (_note.Text != _vm.Note) _note.Text = _vm.Note;
        _chips.Children.Clear();
        foreach (var t in _vm.Tags) _chips.Children.Add(Chip(t));
        _syncing = false;
    }

    private UIElement Chip(string tag)
    {
        var text = new TextBlock { Text = tag, FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
        text.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
        var close = new Button { Content = "✕", FontSize = 9, MinWidth = 16, MinHeight = 16, Padding = new Thickness(2, 0, 2, 0), Margin = new Thickness(4, 0, 0, 0), ToolTip = "Remove tag" };
        close.Click += (_, _) => _vm.RemoveTag(tag);
        var border = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(8, 1, 3, 1), Margin = new Thickness(0, 6, 5, 0), Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { text, close } } };
        border.SetResourceReference(Border.BackgroundProperty, "AccentSoft");
        return border;
    }
}

internal static class BorderExt
{
    public static Border WithBrush(this Border b, string key) { b.SetResourceReference(Border.BackgroundProperty, key); return b; }
}
