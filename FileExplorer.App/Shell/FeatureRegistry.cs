using FileExplorer.App.Tabs;

namespace FileExplorer.App.Shell;

/// <summary>A tab kind. Create makes a fresh one; Restore rebuilds one from saved state.</summary>
public sealed record TabType(string Id, string DisplayName, Func<ITabViewModel> Create, Func<TabState, ITabViewModel> Restore);

/// <summary>A command a feature adds to the toolbar menu / keyboard map.</summary>
public sealed record FeatureCommand(string Title, string? Gesture, Action<MainViewModel> Execute);

/// <summary>A sidebar entry added by a feature (e.g. tools, tags).</summary>
public sealed record SidebarEntry(string Section, string Title, Action<MainViewModel> Activate);

public interface IFeature
{
    string Name { get; }
    void Register(FeatureRegistry registry);
}

/// <summary>Features add tab types, commands and sidebar items here; the shell only iterates the registry.</summary>
public sealed class FeatureRegistry
{
    private readonly List<IFeature> _features = new();
    private readonly Dictionary<string, TabType> _tabTypes = new();
    private readonly List<FeatureCommand> _commands = new();
    private readonly List<SidebarEntry> _sidebar = new();

    /// <summary>What Ctrl+K / Ctrl+F does; set by the search feature (nothing happens when it is absent).</summary>
    public Action<MainViewModel>? SearchAction { get; set; }

    private readonly List<Func<MainViewModel, System.Windows.FrameworkElement>> _rightPanels = new();
    /// <summary>Panels shown at the right of every window (e.g. preview). Each window builds its own instance.</summary>
    public IReadOnlyList<Func<MainViewModel, System.Windows.FrameworkElement>> RightPanels => _rightPanels;
    public void AddRightPanel(Func<MainViewModel, System.Windows.FrameworkElement> factory) => _rightPanels.Add(factory);

    private readonly List<Func<IReadOnlyList<SidebarEntry>>> _sidebarProviders = new();
    /// <summary>Entries that change at run time (favorites, tags); evaluated each time a sidebar is built.</summary>
    public IReadOnlyList<Func<IReadOnlyList<SidebarEntry>>> SidebarProviders => _sidebarProviders;
    public void AddSidebarProvider(Func<IReadOnlyList<SidebarEntry>> provider) => _sidebarProviders.Add(provider);
    public event Action? SidebarChanged;
    public void NotifySidebarChanged() => SidebarChanged?.Invoke();

    public IReadOnlyList<IFeature> Features => _features;
    public IReadOnlyCollection<TabType> TabTypes => _tabTypes.Values;
    public IReadOnlyList<FeatureCommand> Commands => _commands;
    public IReadOnlyList<SidebarEntry> Sidebar => _sidebar;

    public void Add(IFeature feature)
    {
        _features.Add(feature);
        feature.Register(this);
    }

    public void AddTabType(TabType type) => _tabTypes[type.Id] = type;
    public void AddCommand(FeatureCommand command) => _commands.Add(command);
    public void AddSidebarEntry(SidebarEntry entry) => _sidebar.Add(entry);

    public TabType? FindTabType(string id) => _tabTypes.GetValueOrDefault(id);
}
