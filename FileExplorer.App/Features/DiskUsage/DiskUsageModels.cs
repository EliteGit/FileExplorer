using System.Collections.ObjectModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using FileExplorer.Core.Common;
using FileExplorer.Core.Scan;

namespace FileExplorer.App.Features.DiskUsage;

/// <summary>One place for "what is selected": the tree, the treemap and the extension table all subscribe (node id and/or extension).</summary>
public sealed class SelectionHub
{
    public int? NodeId { get; private set; }
    public string? Extension { get; private set; }

    /// <summary>Raised with the originating object so subscribers can ignore their own selections.</summary>
    public event Action<object?>? Changed;

    public void SelectNode(int? id, object? source)
    {
        if (NodeId == id) return;
        NodeId = id;
        Changed?.Invoke(source);
    }

    public void SelectExtension(string? ext, object? source)
    {
        if (Extension == ext) return;
        Extension = ext;
        Changed?.Invoke(source);
    }
}

/// <summary>A row in the folder tree. Children are created when the node is first expanded.</summary>
public sealed partial class FolderNodeViewModel : ObservableObject
{
    private const int MaxShownChildren = 500;
    private static readonly FolderNodeViewModel Placeholder = new();
    private readonly FolderSizeTree? _tree;
    private readonly SelectionHub? _hub;
    private bool _loaded;

    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private bool _isSelected;

    private FolderNodeViewModel() { Name = "…"; }

    public FolderNodeViewModel(FolderSizeTree tree, int id, SelectionHub hub, FolderNodeViewModel? parent)
    {
        _tree = tree; _hub = hub; Id = id; Parent = parent;
        Name = tree.Name(id);
        long size = tree.Size(id), root = Math.Max(tree.Size(FolderSizeTree.RootId), 1);
        Fraction = Math.Min(1.0, (double)size / root);
        SizeText = SizeFormatter.Format(size);
        PercentText = (100.0 * size / root).ToString("0.0") + "%";
        FilesText = id >= 0 && !tree.IsDir(id) ? "" : id < 0 && id != FolderSizeTree.RootId ? "—" : tree.Files(id).ToString("N0");
        var m = tree.Modified(id);
        ModifiedText = m == DateTime.MinValue ? "" : DateFormatter.Format(m);
        BrushHex = id switch
        {
            FolderSizeTree.FreeId => "#9AA3B2", FolderSizeTree.UnknownId => "#C7CCD6",
            _ when tree.IsDir(id) => "#E0903A",
            _ => FileTypeCatalog.ColorOf(FileTypeCatalog.GroupOf(tree.Index.Extension(id))),
        };
        if (id >= 0 && tree.IsDir(id) && tree.Children(id).Length > 0) Children.Add(Placeholder);
        if (id == FolderSizeTree.RootId) Children.Add(Placeholder);
    }

    public int Id { get; }
    public FolderNodeViewModel? Parent { get; }
    public string Name { get; }
    public double Fraction { get; }
    public string SizeText { get; } = "";
    public string PercentText { get; } = "";
    public string FilesText { get; } = "";
    public string ModifiedText { get; } = "";
    public string BrushHex { get; } = "#8A94A6";
    public bool IsDir => _tree is not null && Id != FolderSizeTree.FreeId && Id != FolderSizeTree.UnknownId && _tree.IsDir(Id);
    public ObservableCollection<FolderNodeViewModel> Children { get; } = new();

    partial void OnIsExpandedChanged(bool value) { if (value) LoadChildren(); }

    partial void OnIsSelectedChanged(bool value)
    {
        if (value && _hub is not null && Id != FolderSizeTree.OtherId) _hub.SelectNode(Id, this);
    }

    public void LoadChildren()
    {
        if (_loaded || _tree is null) return;
        _loaded = true;
        Children.Clear();
        var kids = _tree.Children(Id);
        for (int i = 0; i < Math.Min(kids.Length, MaxShownChildren); i++) Children.Add(new FolderNodeViewModel(_tree, kids[i], _hub!, this));
        if (kids.Length > MaxShownChildren)
            Children.Add(new FolderNodeViewModel(_tree, FolderSizeTree.OtherId, _hub!, this) { });
    }

    /// <summary>Expands down to <paramref name="id"/> and selects it (used when the treemap or table drives the selection).</summary>
    public static FolderNodeViewModel? Reveal(FolderNodeViewModel root, FolderSizeTree tree, int id)
    {
        var chain = tree.PathFromRoot(id);
        var cur = root;
        foreach (var step in chain.Skip(1))
        {
            cur.IsExpanded = true;
            cur.LoadChildren();
            var next = cur.Children.FirstOrDefault(c => c.Id == step);
            if (next is null) return cur;      // beyond the 500 shown children
            cur = next;
        }
        return cur;
    }
}

public sealed record ExtensionRow(string Extension, string GroupName, string ColorHex, long Files, long Bytes, string SizeText, string PercentText);

public sealed record LegendEntry(string Name, string ColorHex, string SizeText);
