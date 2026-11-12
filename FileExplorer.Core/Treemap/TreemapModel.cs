using FileExplorer.Core.Scan;

namespace FileExplorer.Core.Treemap;

/// <summary>One drawn rectangle. Directories that were expanded are frames; the rest are filled blocks.</summary>
/// <summary>Weight is the size the cell stands for (the "other" block has no node of its own).</summary>
public readonly record struct TreemapCell(int NodeId, RectD Rect, int Depth, bool IsDir, bool Expanded, TypeGroup Group, double Weight = 0);

public sealed record TreemapOptions(int MaxDepth = 8, double MinExpandSize = 28, double Padding = 2, int MaxChildren = 250, double MinCellSize = 1.5);

/// <summary>Recursive layout down to a depth / minimum pixel cutoff, with hit testing that returns a node id.</summary>
public sealed class TreemapModel
{
    private readonly List<TreemapCell> _cells = new();

    public IReadOnlyList<TreemapCell> Cells => _cells;

    public static TreemapModel Build(FolderSizeTree tree, int rootNode, RectD bounds, TreemapOptions? options = null)
    {
        var m = new TreemapModel();
        var o = options ?? new TreemapOptions();
        m.Fill(tree, rootNode, bounds, o, 0, withVolumeBlocks: rootNode == FolderSizeTree.RootId && tree.HasVolumeInfo);
        return m;
    }

    private void Fill(FolderSizeTree tree, int node, RectD area, TreemapOptions o, int depth, bool withVolumeBlocks)
    {
        var ids = new List<int>(); var weights = new List<double>();
        var kids = tree.Children(node);
        int take = Math.Min(kids.Length, o.MaxChildren);
        double shown = 0;
        for (int i = 0; i < take; i++)
        {
            long s = tree.Size(kids[i]);
            if (s <= 0) continue;
            ids.Add(kids[i]); weights.Add(s); shown += s;
        }
        double rest = tree.Size(node) - shown;
        if (rest > 0 && kids.Length > take) { ids.Add(FolderSizeTree.OtherId); weights.Add(rest); }
        if (withVolumeBlocks)
        {
            if (tree.VolumeFreeBytes > 0) { ids.Add(FolderSizeTree.FreeId); weights.Add(tree.VolumeFreeBytes); }
            if (tree.UnknownBytes > 0) { ids.Add(FolderSizeTree.UnknownId); weights.Add(tree.UnknownBytes); }
        }

        var rects = SquarifiedLayout.Layout(weights, area);
        for (int i = 0; i < ids.Count; i++)
        {
            var r = rects[i];
            if (r.W < o.MinCellSize || r.H < o.MinCellSize) continue;
            int id = ids[i];
            bool isDir = tree.IsDir(id) && id >= 0;
            bool expand = isDir && depth + 1 < o.MaxDepth && r.W >= o.MinExpandSize && r.H >= o.MinExpandSize && tree.Children(id).Length > 0;
            var group = isDir || id < 0 ? TypeGroup.Other : FileTypeCatalog.GroupOf(ExtensionOf(tree, id));
            _cells.Add(new TreemapCell(id, r, depth, isDir, expand, group, weights[i]));
            if (expand) Fill(tree, id, r.Deflate(o.Padding), o, depth + 1, false);
        }
    }

    private static ReadOnlySpan<char> ExtensionOf(FolderSizeTree tree, int id)
    {
        var idx = tree.Index;
        return idx.Extension(id);
    }

    /// <summary>The deepest cell under the point (children are added after their parents).</summary>
    public int? HitTest(double x, double y)
    {
        for (int i = _cells.Count - 1; i >= 0; i--)
            if (_cells[i].Rect.Contains(x, y)) return _cells[i].NodeId;
        return null;
    }

    public TreemapCell? CellOf(int nodeId)
    {
        foreach (var c in _cells) if (c.NodeId == nodeId) return c;
        return null;
    }
}
