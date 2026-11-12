using FileExplorer.App.Shell;
using FileExplorer.App.Tabs;
using FileExplorer.Core.Common;
using FileExplorer.Core.Shell;
using FileExplorer.Core.Tags;

namespace FileExplorer.App.Features.Tags;

/// <summary>Favorites and tags: sidebar sections, the tags/notes panel, and the Tags manager tab.</summary>
public sealed class TagsFeature : IFeature
{
    private readonly ITagStore _store;
    private readonly ErrorReporter _errors;
    private readonly ShellActions _shell;
    private readonly IPrompt _prompt;

    public TagsFeature(ITagStore store, ErrorReporter errors, ShellActions shell, IPrompt prompt)
    {
        _store = store; _errors = errors; _shell = shell; _prompt = prompt;
    }

    public string Name => "Tags";

    public void Register(FeatureRegistry registry)
    {
        if (_store is TagStore concrete) _errors.Check(concrete.Initialize());
        ViewTemplates.Register<TagsTabViewModel, TagsTabView>();
        registry.AddTabType(new TabType(TagsTabViewModel.Type, "Tags", () => NewTab(registry, null),
            s => NewTab(registry, System.Text.Json.JsonSerializer.Deserialize<string>(s.Data) is { Length: > 0 } t ? t : null)));
        registry.AddCommand(new FeatureCommand("Tags manager", null, w => w.OpenTool(NewTab(registry, null))));
        registry.AddRightPanel(w => new TagsPanelView(new TagsPanelViewModel(w, _store, _errors, registry)));
        registry.AddSidebarProvider(() => FavoriteEntries());
        registry.AddSidebarProvider(() => TagEntries(registry));
    }

    private TagsTabViewModel NewTab(FeatureRegistry registry, string? tag) => new(_store, _errors, _shell, _prompt, registry, tag);

    private IReadOnlyList<SidebarEntry> FavoriteEntries()
    {
        var favs = _store.Favorites();
        if (!favs.IsOk) { _errors.Report(favs.Error); return Array.Empty<SidebarEntry>(); }
        return favs.Value.Where(p => Directory.Exists(p) || File.Exists(p))
            .Select(p => new SidebarEntry("FAVORITES", "★ " + (PathUtil.FileName(p)), w => w.NavigateToCommand.Execute(Directory.Exists(p) ? p : PathUtil.Parent(p) ?? "")))
            .ToList();
    }

    private IReadOnlyList<SidebarEntry> TagEntries(FeatureRegistry registry)
    {
        var tags = _store.AllTags();
        if (!tags.IsOk) { _errors.Report(tags.Error); return Array.Empty<SidebarEntry>(); }
        return tags.Value.Select(t => new SidebarEntry("TAGS", $"# {t.Name} ({t.Files})", w => w.OpenTool(NewTab(registry, t.Name)))).ToList();
    }
}
