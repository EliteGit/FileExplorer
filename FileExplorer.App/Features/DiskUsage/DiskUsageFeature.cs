using FileExplorer.App.Shell;
using FileExplorer.App.Tabs;
using FileExplorer.Core.Index;

namespace FileExplorer.App.Features.DiskUsage;

/// <summary>Disk Usage tab (folder tree, treemap, extension table). Depends only on Core/Fs and Core/Scan.</summary>
public sealed class DiskUsageFeature : IFeature
{
    private readonly DiskUsageServices _services;

    public DiskUsageFeature(DiskUsageServices services) => _services = services;

    public string Name => "Disk Usage";

    public void Register(FeatureRegistry registry)
    {
        ViewTemplates.Register<DiskUsageTabViewModel, DiskUsageTabView>();
        registry.AddTabType(new TabType(DiskUsageTabViewModel.Type, "Disk Usage", () => new DiskUsageTabViewModel(_services, ""),
            s => new DiskUsageTabViewModel(_services, System.Text.Json.JsonSerializer.Deserialize<string>(s.Data) ?? "")));
        registry.AddCommand(new FeatureCommand("Disk usage of this folder", null, vm => OpenFor(vm, vm.ActivePane is { IsThisPc: false } p ? p.Path : "")));
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady))
        {
            var root = drive.Name;
            registry.AddSidebarEntry(new SidebarEntry("DISK USAGE", "◔ " + root.TrimEnd('\\'), vm => OpenFor(vm, root)));
        }
    }

    private void OpenFor(MainViewModel window, string path) => window.AddTab(new DiskUsageTabViewModel(_services, path));
}
