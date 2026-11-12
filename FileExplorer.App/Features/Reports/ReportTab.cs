using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FileExplorer.App.Controls;
using FileExplorer.App.Features.Search;
using FileExplorer.App.Features.Tools;
using FileExplorer.App.Shell;
using FileExplorer.App.Tabs;
using FileExplorer.Core.Common;
using FileExplorer.Core.Fs;
using FileExplorer.Core.Reports;
using FileExplorer.Core.Search;

namespace FileExplorer.App.Features.Reports;

public enum ReportKind { LargeFiles, OldFiles, EmptyFolders }

/// <summary>One report tab: scans a folder, runs a saved query (or the empty-folder pass), lists matches, exports CSV.</summary>
public sealed partial class ReportTabViewModel : TabViewModelBase
{
    public const string Type = "report";
    private readonly ToolServices _s;
    private readonly IIconProvider _icons;
    private readonly QueryEngine _engine = new();
    private CancellationTokenSource? _cts;

    [ObservableProperty] private string _rootPath;
    [ObservableProperty] private double _parameter;
    [ObservableProperty] private bool _running;
    [ObservableProperty] private string _summary = "Press Run.";
    [ObservableProperty] private VirtualList<SearchRowViewModel> _rows = new(0, _ => null!);

    public ReportTabViewModel(ToolServices services, IIconProvider icons, ReportKind kind, string root)
    {
        _s = services; _icons = icons; Kind = kind; _rootPath = root;
        _parameter = kind == ReportKind.LargeFiles ? 100 : 365;
    }

    public ReportKind Kind { get; }
    public SearchResults Current { get; private set; } = SearchResults.Empty;
    public override string TypeId => Type;
    public override string Title => Kind switch { ReportKind.LargeFiles => "Large files", ReportKind.OldFiles => "Old files", _ => "Empty folders" };
    public bool HasParameter => Kind != ReportKind.EmptyFolders;
    public string ParameterLabel => Kind == ReportKind.LargeFiles ? "At least (MB)" : "Not changed for (days)";

    [RelayCommand]
    private void ChooseFolder()
    {
        var p = _s.Prompt.Ask(Title, "Folder or drive to scan", RootPath);
        if (!string.IsNullOrWhiteSpace(p)) { RootPath = PathUtil.Normalize(p); Run(); }
    }

    [RelayCommand]
    private void Run()
    {
        if (RootPath.Length == 0) return;
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        Running = true; Summary = "Scanning…";
        string root = RootPath; double param = Parameter; var kind = Kind;
        _ = Task.Run(() => Execute(root, kind, param, cts.Token)).ContinueWith(t => _s.Post(() => Finish(t.Result, cts)));
    }

    private Result<SearchResults> Execute(string root, ReportKind kind, double param, CancellationToken ct)
    {
        var built = new EnumeratedIndexSource(_s.Files).Build(root, null, ct);
        if (!built.IsOk) return Result<SearchResults>.Err(built.Error);
        var index = built.Value;
        int[] rows;
        if (kind == ReportKind.EmptyFolders) rows = Reports_EmptyFolders(index);
        else
        {
            var query = kind == ReportKind.LargeFiles ? Core.Reports.Reports.LargeFiles((long)(param * 1024 * 1024)) : Core.Reports.Reports.OldFiles(DateTime.Now.AddDays(-param));
            var found = _engine.Search(index, query, ct);
            if (!found.IsOk) return Result<SearchResults>.Err(found.Error);
            rows = found.Value;
            Array.Sort(rows, (a, b) => kind == ReportKind.LargeFiles ? index.Size(b).CompareTo(index.Size(a)) : index.Modified(a).CompareTo(index.Modified(b)));
        }
        return Result<SearchResults>.Ok(new SearchResults(new[] { (index, rows) }));
    }

    private static int[] Reports_EmptyFolders(FileIndex index) => Core.Reports.Reports.EmptyFolders(index);

    private void Finish(Result<SearchResults> r, CancellationTokenSource cts)
    {
        if (cts != _cts) return;
        Running = false;
        if (!r.IsOk) { Summary = r.Error.Message; _s.Errors.Report(r.Error); return; }
        Current = r.Value;
        var results = r.Value;
        Rows = new VirtualList<SearchRowViewModel>(results.Count, i => new SearchRowViewModel(results.At(i), _icons));
        Summary = $"{results.Count:N0} results";
    }

    [RelayCommand]
    private void ExportCsv()
    {
        var path = ToolServices.AskCsvPath(Title.Replace(' ', '-').ToLowerInvariant() + ".csv");
        if (path is null) return;
        var rows = Enumerable.Range(0, Current.Count).Select(i => Current.At(i))
            .Select(e => (IRow)new SimpleRow(e.Name, e.FullPath, e.IsDir ? "" : e.Size.ToString(), e.ModifiedLocal == DateTime.MinValue ? "" : e.ModifiedLocal.ToString("yyyy-MM-dd HH:mm")));
        _s.Errors.Check(CsvExporter.Export(path, new[] { "Name", "Path", "Size (bytes)", "Modified" }, rows));
    }

    [RelayCommand] private void Open(string? path) { if (path is not null) _s.Errors.Check(_s.Shell.Open(path)); }
    [RelayCommand] private void Reveal(string? path) { if (path is not null) _s.Errors.Check(_s.Shell.Reveal(path)); }

    public override TabState Save() => new(Type, JsonSerializer.Serialize(new Saved(Kind, RootPath, Parameter)));
    public override ITabViewModel Duplicate() => new ReportTabViewModel(_s, _icons, Kind, RootPath) { Parameter = Parameter };
    private sealed record Saved(ReportKind Kind, string Root, double Parameter);

    public static ReportTabViewModel Restore(TabState s, ToolServices services, IIconProvider icons)
    {
        var saved = JsonSerializer.Deserialize<Saved>(s.Data) ?? throw new InvalidDataException("Saved report is empty.");
        return new ReportTabViewModel(services, icons, saved.Kind, saved.Root) { Parameter = saved.Parameter };
    }

    public override void Dispose() { _cts?.Cancel(); base.Dispose(); }
}

/// <summary>Report view, built in code (parameter row, result list, export).</summary>
public sealed class ReportTabView : UserControl
{
    public ReportTabView()
    {
        var root = new DockPanel();
        root.SetResourceReference(Panel.BackgroundProperty, "Panel");

        var bar = new DockPanel { Margin = new Thickness(12, 8, 12, 8) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(Cmd("Choose…", "ChooseFolderCommand")); buttons.Children.Add(Cmd("Run", "RunCommand")); buttons.Children.Add(Cmd("Export CSV…", "ExportCsvCommand"));
        DockPanel.SetDock(buttons, Dock.Right);
        var path = new TextBlock { FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 14, 0) };
        path.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("RootPath"));
        var label = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
        label.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("ParameterLabel"));
        var value = new TextBox { Width = 70, VerticalAlignment = VerticalAlignment.Center };
        value.SetBinding(TextBox.TextProperty, new System.Windows.Data.Binding("Parameter") { UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged });
        var visible = new System.Windows.Data.Binding("HasParameter") { Converter = new System.Windows.Controls.BooleanToVisibilityConverter() };
        label.SetBinding(VisibilityProperty, visible); value.SetBinding(VisibilityProperty, visible);
        label.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        foreach (UIElement e in new UIElement[] { buttons, path, label, value }) bar.Children.Add(e);
        DockPanel.SetDock(bar, Dock.Top);

        var summary = new TextBlock { Margin = new Thickness(12, 0, 12, 6) };
        summary.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        summary.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("Summary"));
        DockPanel.SetDock(summary, Dock.Top);

        var list = new ListView();
        list.SetBinding(ItemsControl.ItemsSourceProperty, new System.Windows.Data.Binding("Rows"));
        VirtualizingPanel.SetIsVirtualizing(list, true);
        VirtualizingPanel.SetVirtualizationMode(list, VirtualizationMode.Recycling);
        var grid = new GridView();
        grid.Columns.Add(Col("Name", 280, "Name")); grid.Columns.Add(Col("Folder", 420, "Folder")); grid.Columns.Add(Col("Size", 90, "SizeText")); grid.Columns.Add(Col("Modified", 130, "ModifiedText"));
        list.View = grid;
        list.MouseDoubleClick += (_, _) => { if (list.SelectedItem is SearchRowViewModel r) (DataContext as ReportTabViewModel)?.OpenCommand.Execute(r.FullPath); };
        var menu = new ContextMenu();
        var open = new MenuItem { Header = "Open" }; open.Click += (_, _) => { if (list.SelectedItem is SearchRowViewModel r) (DataContext as ReportTabViewModel)?.OpenCommand.Execute(r.FullPath); };
        var reveal = new MenuItem { Header = "Reveal in Explorer" }; reveal.Click += (_, _) => { if (list.SelectedItem is SearchRowViewModel r) (DataContext as ReportTabViewModel)?.RevealCommand.Execute(r.FullPath); };
        var copy = new MenuItem { Header = "Copy path" }; copy.Click += (_, _) => { if (list.SelectedItem is SearchRowViewModel r) Clipboard.SetText(r.FullPath); };
        menu.Items.Add(open); menu.Items.Add(reveal); menu.Items.Add(copy);
        list.ContextMenu = menu;

        foreach (UIElement e in new UIElement[] { bar, summary, list }) root.Children.Add(e);
        Content = root;
    }

    private static Button Cmd(string text, string command)
    {
        var b = new Button { Content = text };
        b.SetBinding(System.Windows.Controls.Primitives.ButtonBase.CommandProperty, new System.Windows.Data.Binding(command));
        return b;
    }

    private static GridViewColumn Col(string header, double width, string path) =>
        new() { Header = header, Width = width, DisplayMemberBinding = new System.Windows.Data.Binding(path) };
}
