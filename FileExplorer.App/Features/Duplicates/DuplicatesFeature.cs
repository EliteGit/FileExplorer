using FileExplorer.App.Features.Tools;
using FileExplorer.App.Shell;
using FileExplorer.App.Tabs;

namespace FileExplorer.App.Features.Duplicates;

/// <summary>Duplicate finder: size, partial hash, full hash. Delete goes to the Recycle Bin and keeps one copy per group.</summary>
public sealed class DuplicatesFeature : IFeature
{
    private readonly ToolServices _s;
    public DuplicatesFeature(ToolServices services) => _s = services;
    public string Name => "Duplicate finder";

    public void Register(FeatureRegistry registry)
    {
        ViewTemplates.Register<DuplicatesTabViewModel, DuplicatesTabView>();
        registry.AddTabType(new TabType(DuplicatesTabViewModel.Type, "Duplicates", () => new DuplicatesTabViewModel(_s, ""),
            s => new DuplicatesTabViewModel(_s, System.Text.Json.JsonSerializer.Deserialize<string>(s.Data) ?? "")));
        void Open(MainViewModel w) { var tab = new DuplicatesTabViewModel(_s, ToolServices.StartFolder(w)); w.AddTab(tab); }
        registry.AddCommand(new FeatureCommand("Duplicate finder", null, Open));
        registry.AddSidebarEntry(new SidebarEntry("TOOLS", "⧉ Duplicate finder", Open));
    }
}
