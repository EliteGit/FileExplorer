namespace FileExplorer.App.Shell;

public sealed record SidebarItem(string Title, Action<MainViewModel> Activate);

public sealed record SidebarSection(string Title, IReadOnlyList<SidebarItem> Items);

/// <summary>Quick-access folders and drives, plus whatever sections features registered.</summary>
public static class SidebarBuilder
{
    public static IReadOnlyList<SidebarSection> Build(FeatureRegistry features)
    {
        var sections = new List<SidebarSection> { QuickAccess(), Drives() };
        foreach (var group in features.Sidebar.Concat(features.SidebarProviders.SelectMany(p => p())).GroupBy(e => e.Section))
            sections.Add(new SidebarSection(group.Key, group.Select(e => new SidebarItem(e.Title, e.Activate)).ToList()));
        return sections.Where(s => s.Items.Count > 0).ToList();
    }

    private static SidebarSection QuickAccess()
    {
        var folders = new (string Name, Environment.SpecialFolder Folder)[]
        {
            ("Desktop", Environment.SpecialFolder.DesktopDirectory), ("Documents", Environment.SpecialFolder.MyDocuments),
            ("Pictures", Environment.SpecialFolder.MyPictures), ("Music", Environment.SpecialFolder.MyMusic),
            ("Videos", Environment.SpecialFolder.MyVideos),
        };
        var items = folders.Select(f => (f.Name, Path: Environment.GetFolderPath(f.Folder))).Where(f => Directory.Exists(f.Path))
            .Select(f => new SidebarItem("★ " + f.Name, vm => vm.NavigateToCommand.Execute(f.Path))).ToList();
        var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        if (Directory.Exists(downloads)) items.Insert(1, new SidebarItem("★ Downloads", vm => vm.NavigateToCommand.Execute(downloads)));
        return new SidebarSection("QUICK ACCESS", items);
    }

    private static SidebarSection Drives()
    {
        var items = new List<SidebarItem> { new("▣ This PC", vm => vm.NavigateToCommand.Execute("")) };
        items.AddRange(DriveInfo.GetDrives().Select(d => new SidebarItem("▣ " + d.Name.TrimEnd('\\'), vm => vm.NavigateToCommand.Execute(d.Name))));
        return new SidebarSection("DRIVES", items);
    }
}
