using System.Collections.ObjectModel;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FileExplorer.App.Shell;
using FileExplorer.App.Tabs;
using FileExplorer.Core.Shell;
using FileExplorer.Core.Tags;

namespace FileExplorer.App.Features.Tags;

/// <summary>Tags manager: every tag with its file count, the files carrying the selected tag, rename / delete.</summary>
public sealed partial class TagsTabViewModel : TabViewModelBase
{
    public const string Type = "tags";
    private readonly ITagStore _store;
    private readonly ErrorReporter _errors;
    private readonly ShellActions _shell;
    private readonly IPrompt _prompt;
    private readonly FeatureRegistry _registry;

    [ObservableProperty] private TagInfo? _selectedTag;
    [ObservableProperty] private string? _selectedFile;
    [ObservableProperty] private string _status = "";

    public TagsTabViewModel(ITagStore store, ErrorReporter errors, ShellActions shell, IPrompt prompt, FeatureRegistry registry, string? initialTag = null)
    {
        _store = store; _errors = errors; _shell = shell; _prompt = prompt; _registry = registry;
        Reload();
        if (initialTag is not null) SelectedTag = Tags.FirstOrDefault(t => string.Equals(t.Name, initialTag, StringComparison.OrdinalIgnoreCase));
    }

    public ObservableCollection<TagInfo> Tags { get; } = new();
    public ObservableCollection<string> Files { get; } = new();
    public override string TypeId => Type;
    public override string Title => SelectedTag is null ? "Tags" : "Tag: " + SelectedTag.Name;

    partial void OnSelectedTagChanged(TagInfo? value)
    {
        RaiseTitleChanged();
        Files.Clear();
        if (value is null) return;
        var paths = _store.PathsWithTag(value.Name);
        if (_errors.Check(paths)) foreach (var p in paths.Value) Files.Add(p);
    }

    [RelayCommand]
    private void Reload()
    {
        var keep = SelectedTag?.Name;
        var repaired = _store.RepairPaths();
        if (_errors.Check(repaired) && repaired.Value > 0) Status = $"Updated {repaired.Value} moved or renamed file(s).";
        Tags.Clear();
        var all = _store.AllTags();
        if (_errors.Check(all)) foreach (var t in all.Value) Tags.Add(t);
        if (keep is not null) SelectedTag = Tags.FirstOrDefault(t => t.Name == keep);
    }

    [RelayCommand]
    private void RenameTag()
    {
        if (SelectedTag is null) return;
        var name = _prompt.Ask("Rename tag", "New name (an existing name merges the two)", SelectedTag.Name);
        if (string.IsNullOrWhiteSpace(name) || name == SelectedTag.Name) return;
        if (_errors.Check(_store.RenameTag(SelectedTag.Name, name))) { SelectedTag = null; Reload(); _registry.NotifySidebarChanged(); }
    }

    [RelayCommand]
    private void DeleteTag()
    {
        if (SelectedTag is null) return;
        if (System.Windows.MessageBox.Show($"Remove the tag '{SelectedTag.Name}' from {SelectedTag.Files} file(s)? The files are not touched.", "Delete tag", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
        if (_errors.Check(_store.DeleteTag(SelectedTag.Name))) { SelectedTag = null; Reload(); _registry.NotifySidebarChanged(); }
    }

    [RelayCommand] private void OpenFile() { if (SelectedFile is not null) _errors.Check(_shell.Open(SelectedFile)); }
    [RelayCommand] private void RevealFile() { if (SelectedFile is not null) _errors.Check(_shell.Reveal(SelectedFile)); }

    public override TabState Save() => new(Type, JsonSerializer.Serialize(SelectedTag?.Name ?? ""));
    public override ITabViewModel Duplicate() => new TagsTabViewModel(_store, _errors, _shell, _prompt, _registry, SelectedTag?.Name);
}

public sealed class TagsTabView : UserControl
{
    public TagsTabView()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(280) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.SetResourceReference(Panel.BackgroundProperty, "Panel");

        var tags = new ListView { SelectionMode = SelectionMode.Single, DisplayMemberPath = "Name" };
        tags.SetBinding(ItemsControl.ItemsSourceProperty, new System.Windows.Data.Binding("Tags"));
        tags.SetBinding(System.Windows.Controls.Primitives.Selector.SelectedItemProperty, new System.Windows.Data.Binding("SelectedTag") { Mode = System.Windows.Data.BindingMode.TwoWay });
        var left = new DockPanel();
        var leftButtons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8) };
        leftButtons.Children.Add(Button("Rename", "RenameTagCommand")); leftButtons.Children.Add(Button("Delete", "DeleteTagCommand")); leftButtons.Children.Add(Button("Refresh", "ReloadCommand"));
        DockPanel.SetDock(leftButtons, Dock.Top);
        left.Children.Add(leftButtons); left.Children.Add(tags);

        var files = new ListView { SelectionMode = SelectionMode.Single };
        files.SetBinding(ItemsControl.ItemsSourceProperty, new System.Windows.Data.Binding("Files"));
        files.SetBinding(System.Windows.Controls.Primitives.Selector.SelectedItemProperty, new System.Windows.Data.Binding("SelectedFile") { Mode = System.Windows.Data.BindingMode.TwoWay });
        files.MouseDoubleClick += (_, _) => (DataContext as TagsTabViewModel)?.OpenFileCommand.Execute(null);
        var right = new DockPanel();
        var rightButtons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8) };
        rightButtons.Children.Add(Button("Open", "OpenFileCommand")); rightButtons.Children.Add(Button("Reveal in Explorer", "RevealFileCommand"));
        var status = new TextBlock { Margin = new Thickness(12, 0, 0, 6) };
        status.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        status.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("Status"));
        DockPanel.SetDock(rightButtons, Dock.Top); DockPanel.SetDock(status, Dock.Bottom);
        right.Children.Add(rightButtons); right.Children.Add(status); right.Children.Add(files);

        Grid.SetColumn(right, 1);
        grid.Children.Add(left); grid.Children.Add(right);
        Content = grid;
    }

    private static Button Button(string text, string command)
    {
        var b = new Button { Content = text };
        b.SetBinding(System.Windows.Controls.Primitives.ButtonBase.CommandProperty, new System.Windows.Data.Binding(command));
        return b;
    }
}
