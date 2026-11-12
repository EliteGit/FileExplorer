using System.Windows.Controls;
using FileExplorer.App.Features.Tools;
using FileExplorer.App.Shell;
using FileExplorer.App.Tabs;
using FileExplorer.Core.Rename;

namespace FileExplorer.App.Features.BulkRename;

public partial class BulkRenameTabView : UserControl
{
    public BulkRenameTabView() => InitializeComponent();
}

/// <summary>Bulk rename of the active pane's selection (or all its files when nothing is selected), with live preview and undo.</summary>
public sealed class BulkRenameFeature : IFeature
{
    private readonly ToolServices _s;
    public BulkRenameFeature(ToolServices services) => _s = services;
    public string Name => "Bulk rename";

    public void Register(FeatureRegistry registry)
    {
        ViewTemplates.Register<BulkRenameTabViewModel, BulkRenameTabView>();
        registry.AddTabType(new TabType(BulkRenameTabViewModel.Type, "Bulk rename", () => new BulkRenameTabViewModel(_s, "", Array.Empty<RenameItem>()),
            s => new BulkRenameTabViewModel(_s, System.Text.Json.JsonSerializer.Deserialize<string>(s.Data) ?? "", Array.Empty<RenameItem>())));   // a restored tab has no selection to work on
        registry.AddCommand(new FeatureCommand("Bulk rename", null, Open));
        registry.AddSidebarEntry(new SidebarEntry("TOOLS", "✎ Bulk rename", Open));
    }

    private void Open(MainViewModel window)
    {
        var pane = window.ActivePane;
        if (pane is null || pane.IsThisPc) { _s.Errors.Report(new Core.Common.AppError("Open a folder tab and select the files to rename.")); return; }
        var source = pane.Selection.Count > 0 ? pane.Selection : pane.Items.ToList();
        var items = source.Select(v => new RenameItem(v.FullPath, v.IsDir, v.Entry.ModifiedLocal)).ToList();
        window.AddTab(new BulkRenameTabViewModel(_s, pane.Path, items));
    }
}
