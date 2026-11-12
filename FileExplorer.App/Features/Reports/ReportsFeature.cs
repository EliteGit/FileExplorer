using FileExplorer.App.Features.Tools;
using FileExplorer.App.Shell;
using FileExplorer.App.Tabs;

namespace FileExplorer.App.Features.Reports;

/// <summary>Large files, old files and empty folders reports.</summary>
public sealed class ReportsFeature : IFeature
{
    private readonly ToolServices _s;
    private readonly IIconProvider _icons;

    public ReportsFeature(ToolServices services, IIconProvider icons) { _s = services; _icons = icons; }
    public string Name => "Reports";

    public void Register(FeatureRegistry registry)
    {
        ViewTemplates.Register<ReportTabViewModel, ReportTabView>();
        registry.AddTabType(new TabType(ReportTabViewModel.Type, "Report", () => new ReportTabViewModel(_s, _icons, ReportKind.LargeFiles, ""),
            s => ReportTabViewModel.Restore(s, _s, _icons)));
        Add(registry, "Large files", "⚖ Large / old files", ReportKind.LargeFiles);
        Add(registry, "Old files", null, ReportKind.OldFiles);
        Add(registry, "Empty folders", "∅ Empty folders", ReportKind.EmptyFolders);
    }

    private void Add(FeatureRegistry registry, string command, string? sidebar, ReportKind kind)
    {
        void Open(MainViewModel w) => w.OpenTool(new ReportTabViewModel(_s, _icons, kind, ToolServices.StartFolder(w)));
        registry.AddCommand(new FeatureCommand(command + " report", null, Open));
        if (sidebar is not null) registry.AddSidebarEntry(new SidebarEntry("TOOLS", sidebar, Open));
    }
}
