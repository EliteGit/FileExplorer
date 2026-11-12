using FileExplorer.Core.Fs;

namespace FileExplorer.Core.Scan;

public enum SizeMode { Logical, Allocated }

/// <summary>
/// Folder totals computed from a FileIndex. Node ids are index row ids; <see cref="RootId"/> is the scanned root,
/// <see cref="FreeId"/> and <see cref="UnknownId"/> are synthetic blocks for the treemap.
/// </summary>
public sealed class FolderSizeTree
{
    public const int RootId = -1, FreeId = -2, UnknownId = -3, OtherId = -4;

    private readonly FileIndex _index;
    private readonly long[] _logical, _allocated, _files;
    private readonly int[] _childStart, _childRows;   // CSR, children sorted by size (descending)

    internal FolderSizeTree(FileIndex index, long[] logical, long[] allocated, long[] files, int[] childStart, int[] childRows,
        long rootLogical, long rootAllocated, long rootFiles, int[] rootChildren, long volumeTotal, long volumeFree, long clusterSize)
    {
        _index = index; _logical = logical; _allocated = allocated; _files = files; _childStart = childStart; _childRows = childRows;
        RootLogical = rootLogical; RootAllocated = rootAllocated; RootFiles = rootFiles; _rootChildren = rootChildren;
        VolumeTotalBytes = volumeTotal; VolumeFreeBytes = volumeFree; ClusterSize = clusterSize;
    }

    private readonly int[] _rootChildren;
    public FileIndex Index => _index;
    public SizeMode Mode { get; set; } = SizeMode.Logical;
    public long ClusterSize { get; }
    public long RootLogical { get; }
    public long RootAllocated { get; }
    public long RootFiles { get; }
    public long VolumeTotalBytes { get; }
    public long VolumeFreeBytes { get; }
    public bool HasVolumeInfo => VolumeTotalBytes > 0;
    public string RootPath => _index.Volume;

    public long Size(int id) => id switch
    {
        RootId => Mode == SizeMode.Logical ? RootLogical : RootAllocated,
        FreeId => VolumeFreeBytes,
        UnknownId => UnknownBytes,
        _ => Mode == SizeMode.Logical ? _logical[id] : _allocated[id],
    };

    /// <summary>Used space the scan could not account for (permission-denied folders, system areas).</summary>
    public long UnknownBytes => HasVolumeInfo ? Math.Max(0, VolumeTotalBytes - VolumeFreeBytes - RootAllocated) : 0;

    public long Files(int id) => id == RootId ? RootFiles : id < 0 ? 0 : _files[id];
    public bool IsDir(int id) => id == RootId || (id >= 0 && _index.IsDir(id));
    public string Name(int id) => id switch
    {
        RootId => RootPath,
        FreeId => "Free space",
        UnknownId => "Unknown",
        OtherId => "Other",
        _ => _index.Name(id).ToString(),
    };

    public string FullPath(int id) => id == RootId ? RootPath : id >= 0 ? _index.GetFullPath(id) : "";
    public DateTime Modified(int id)
    {
        if (id < 0 || _index.Modified(id) <= 0) return DateTime.MinValue;
        return DateTime.FromFileTimeUtc(_index.Modified(id)).ToLocalTime();
    }

    public int Parent(int id) => id == RootId ? RootId : _index.Parent(id) < 0 ? RootId : _index.Parent(id);

    public ReadOnlySpan<int> Children(int id) =>
        id == RootId ? _rootChildren : id < 0 || !_index.IsDir(id) ? ReadOnlySpan<int>.Empty : _childRows.AsSpan(_childStart[id], _childStart[id + 1] - _childStart[id]);

    /// <summary>Ids from the root down to (and including) the node.</summary>
    public List<int> PathFromRoot(int id)
    {
        var chain = new List<int>();
        for (int cur = id; cur != RootId && cur >= 0 && chain.Count < 4096; cur = Parent(cur)) chain.Add(cur);
        chain.Add(RootId);
        chain.Reverse();
        return chain;
    }
}
