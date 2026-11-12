using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using FileExplorer.App.Shell;
using FileExplorer.App.Tabs;
using FileExplorer.Core.Common;
using FileExplorer.Core.Models;

namespace FileExplorer.App.Features.Preview;

public sealed record InfoRow(string Key, string Value);

/// <summary>Follows the active pane's selection of one window and describes it (preview content is built by the view).</summary>
public sealed partial class PreviewPanelViewModel : ObservableObject, IDisposable
{
    private readonly MainViewModel _window;
    private readonly PreviewRegistry _registry;
    private PaneViewModel? _pane;

    [ObservableProperty] private string _title = "Nothing selected";
    [ObservableProperty] private string _subtitle = "";
    [ObservableProperty] private FileEntry? _entry;
    [ObservableProperty] private IPreviewProvider? _provider;
    [ObservableProperty] private string _message = "";

    public PreviewPanelViewModel(MainViewModel window, PreviewRegistry registry)
    {
        _window = window; _registry = registry;
        window.PropertyChanged += OnWindowChanged;
        Attach(window.ActivePane);
    }

    public System.Collections.ObjectModel.ObservableCollection<InfoRow> Rows { get; } = new();

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
        Rows.Clear();
        Entry = null; Provider = null; Message = "";
        if (_pane is null) { Title = "Nothing selected"; Subtitle = ""; return; }
        var sel = _pane.Selection;
        if (sel.Count == 0) { ShowFolder(_pane); return; }
        if (sel.Count > 1) { ShowMany(sel); return; }
        ShowOne(sel[0].Entry);
    }

    private void ShowFolder(PaneViewModel pane)
    {
        Title = pane.IsThisPc ? "This PC" : pane.Title;
        Subtitle = "Folder";
        Rows.Add(new InfoRow("Items", pane.ItemCount.ToString("N0")));
        if (!pane.IsThisPc) Rows.Add(new InfoRow("Path", pane.Path));
        Message = "Select a file to preview it.";
    }

    private void ShowMany(IReadOnlyList<FileEntryViewModel> sel)
    {
        Title = $"{sel.Count} items selected";
        Subtitle = "";
        Rows.Add(new InfoRow("Total size", SizeFormatter.Format(sel.Where(s => !s.IsDir).Sum(s => s.Entry.Size))));
        Rows.Add(new InfoRow("Folders", sel.Count(s => s.IsDir).ToString()));
        Message = "Select one item to preview it.";
    }

    private void ShowOne(FileEntry e)
    {
        Title = e.Name;
        Entry = e;
        Subtitle = e.IsDir ? "Folder" : string.IsNullOrEmpty(Path.GetExtension(e.Name)) ? "File" : Path.GetExtension(e.Name).TrimStart('.').ToUpperInvariant() + " file";
        if (!e.IsDir) Rows.Add(new InfoRow("Size", SizeFormatter.Format(e.Size)));
        if (e.ModifiedLocal != DateTime.MinValue) Rows.Add(new InfoRow("Modified", DateFormatter.Format(e.ModifiedLocal)));
        Rows.Add(new InfoRow("Path", e.FullPath));
        Provider = _registry.Pick(e);
        if (!e.IsDir && Provider is null) Message = "No preview available for this type.";
    }

    public void Dispose()
    {
        _window.PropertyChanged -= OnWindowChanged;
        Attach(null);
    }
}
