using FileExplorer.App.Tabs;
using FileExplorer.Core.Common;
using FileExplorer.Core.Fs;
using FileExplorer.Core.Scan;

namespace FileExplorer.App.Services;

/// <summary>Remembers the folder-size trees of finished disk-usage scans so file lists can reuse them.</summary>
public sealed class FolderSizeService : IFolderSizeProvider
{
    private readonly IFileSource _files;
    private readonly List<FolderSizeTree> _trees = new();
    private readonly object _gate = new();

    public FolderSizeService(IFileSource files) => _files = files;

    /// <summary>Called by the disk-usage tab when a scan finishes (the newest scan wins for the same root).</summary>
    public void Publish(FolderSizeTree tree)
    {
        lock (_gate)
        {
            _trees.RemoveAll(t => string.Equals(t.RootPath, tree.RootPath, StringComparison.OrdinalIgnoreCase));
            _trees.Add(tree);
        }
    }

    public Result<FolderSizeResult> Lookup(string folder)
    {
        var path = PathUtil.Normalize(folder);
        FolderSizeTree[] trees;
        lock (_gate) trees = _trees.ToArray();
        foreach (var tree in trees.OrderByDescending(t => t.RootPath.Length))
        {
            var root = tree.RootPath;
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;
            int node = Find(tree, path[root.Length..].Trim('\\'));
            if (node != int.MinValue) return Result<FolderSizeResult>.Ok(new FolderSizeResult(tree.Size(node), "from disk-usage scan"));
        }
        return new AppError("No scan covers this folder.", 0, folder);
    }

    private static int Find(FolderSizeTree tree, string relative)
    {
        int node = FolderSizeTree.RootId;
        if (relative.Length == 0) return node;
        foreach (var part in relative.Split('\\'))
        {
            int next = int.MinValue;
            foreach (var c in tree.Children(node))
                if (tree.IsDir(c) && string.Equals(tree.Name(c), part, StringComparison.OrdinalIgnoreCase)) { next = c; break; }
            if (next == int.MinValue) return int.MinValue;
            node = next;
        }
        return node;
    }

    public Task<Result<FolderSizeResult>> ComputeAsync(string folder, CancellationToken ct) => Task.Run(() =>
    {
        var built = new EnumeratedIndexSource(_files).Build(folder, null, ct);
        if (!built.IsOk) return Result<FolderSizeResult>.Err(built.Error);
        var vol = VolumeInfo.Query(folder);
        var tree = DiskScanner.Compute(built.Value, vol.IsOk ? vol.Value.ClusterSize : 4096);
        return Result<FolderSizeResult>.Ok(new FolderSizeResult(tree.RootLogical, "calculated now"));
    }, ct);
}
