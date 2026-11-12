using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using FileExplorer.Core.Common;
using FileExplorer.Core.Fs;

namespace FileExplorer.App.Shell;

/// <summary>A right-click action on a sidebar item (for example "Remove from favorites").</summary>
public sealed record SidebarAction(string Header, Action<MainViewModel> Run);

/// <summary>
/// One row of the sidebar. A row with a loader (a drive or folder) shows an expander and reads its sub-folders the first time
/// it is opened, like Explorer's navigation pane.
/// </summary>
public sealed partial class SidebarNode : ObservableObject
{
    private static readonly SidebarNode Placeholder = new("…", null);
    private readonly Func<IReadOnlyList<SidebarNode>>? _loader;
    private bool _loaded;

    [ObservableProperty] private bool _isExpanded;

    public SidebarNode(string title, Action<MainViewModel>? activate, IReadOnlyList<SidebarAction>? actions = null, Func<IReadOnlyList<SidebarNode>>? loader = null)
    {
        Title = title; Activate = activate; Actions = actions ?? Array.Empty<SidebarAction>(); _loader = loader;
        if (loader is not null) Children.Add(Placeholder);
    }

    public string Title { get; }
    public Action<MainViewModel>? Activate { get; }
    public IReadOnlyList<SidebarAction> Actions { get; }
    public bool HasLoader => _loader is not null;
    public ObservableCollection<SidebarNode> Children { get; } = new();

    partial void OnIsExpandedChanged(bool value) { if (value) Load(); }

    public void Load()
    {
        if (_loaded || _loader is null) return;
        _loaded = true;
        Children.Clear();
        foreach (var c in _loader()) Children.Add(c);
    }
}

public sealed record SidebarSection(string Title, IReadOnlyList<SidebarNode> Items);

/// <summary>Quick access (built-in folders, or the user's favorites when the favorites feature is present), drives with their folders, and feature sections.</summary>
public static class SidebarBuilder
{
    public const string QuickAccessTitle = "QUICK ACCESS";
    public const string DrivesTitle = "DRIVES";

    public static IReadOnlyList<SidebarSection> Build(FeatureRegistry features, IFileSource? files = null)
    {
        files ??= new FastDirectoryEnumerator();
        var order = new List<string>();
        var items = new Dictionary<string, List<SidebarNode>>();
        void Add(string section, SidebarNode node)
        {
            if (!items.TryGetValue(section, out var list)) { items[section] = list = new List<SidebarNode>(); order.Add(section); }
            list.Add(node);
        }

        // The two built-in sections always come first, in this order (they are dropped below if they stay empty).
        order.Add(QuickAccessTitle); items[QuickAccessTitle] = new List<SidebarNode>();
        order.Add(DrivesTitle); items[DrivesTitle] = new List<SidebarNode>();
        if (!features.OwnsQuickAccess) foreach (var n in BuiltInQuickAccess()) Add(QuickAccessTitle, n);
        foreach (var n in Drives(files)) Add(DrivesTitle, n);
        foreach (var e in features.Sidebar.Concat(features.SidebarProviders.SelectMany(p => p())))
            Add(e.Section, new SidebarNode(e.Title, e.Activate, e.Actions));
        return order.Where(s => items[s].Count > 0).Select(s => new SidebarSection(s, items[s])).ToList();
    }

    /// <summary>Used only when no favorites feature is registered.</summary>
    private static IEnumerable<SidebarNode> BuiltInQuickAccess() =>
        DefaultFolders().Select(f => new SidebarNode("★ " + Path.GetFileName(f), vm => vm.NavigateToCommand.Execute(f)));

    /// <summary>Folders everyone expects at the top (also what the favorites list starts with).</summary>
    public static IReadOnlyList<string> DefaultFolders()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var all = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Path.Combine(profile, "Downloads"),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
        };
        return all.Where(p => p.Length > 0 && Directory.Exists(p)).ToList();
    }

    private static IEnumerable<SidebarNode> Drives(IFileSource files)
    {
        yield return new SidebarNode("▣ This PC", vm => vm.NavigateToCommand.Execute(""));
        foreach (var d in DriveInfo.GetDrives())
        {
            string root = d.Name;
            yield return FolderNode("▣ " + root.TrimEnd('\\'), root, files);
        }
    }

    /// <summary>A folder only gets an expander when it has a visible sub-folder (same rules as the listing: no hidden folders, no links).</summary>
    private static SidebarNode FolderNode(string title, string path, IFileSource files) =>
        new(title, vm => vm.NavigateToCommand.Execute(path), null, HasSubfolders(path) ? () => FolderChildren(path, files) : null);

    /// <summary>
    /// Stops at the first sub-folder, so it is cheap. If the folder cannot be read at all (access denied, drive not ready) it counts as
    /// "may have folders", so the expander is there and opening it shows the reason instead of hiding the problem.
    /// </summary>
    public static bool HasSubfolders(string path)
    {
        try
        {
            var options = new EnumerationOptions { IgnoreInaccessible = false, AttributesToSkip = FileAttributes.Hidden | FileAttributes.ReparsePoint, RecurseSubdirectories = false };
            using var e = new DirectoryInfo(path).EnumerateDirectories("*", options).GetEnumerator();
            return e.MoveNext();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return true; }
    }

    /// <summary>The visible sub-folders of a folder, sorted by name. A folder that cannot be read shows why, in the tree, instead of looking empty.</summary>
    public static IReadOnlyList<SidebarNode> FolderChildren(string path, IFileSource files)
    {
        var listing = files.EnumerateDirectory(path);
        if (!listing.IsOk) return new[] { new SidebarNode("⚠ " + listing.Error.Message, null) };
        return listing.Value.Where(e => e.IsDir && !e.IsHidden && !e.IsReparse)
            .OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(e => FolderNode(e.Name, e.FullPath, files)).ToList();
    }
}
