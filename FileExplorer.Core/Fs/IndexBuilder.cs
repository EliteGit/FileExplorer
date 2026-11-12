using FileExplorer.Core.Common;

namespace FileExplorer.Core.Fs;

/// <summary>One raw record, from the MFT or from a directory walk.</summary>
public readonly record struct RawRecord(ulong Frn, ulong ParentFrn, string Name, long Size, long Modified, uint Attributes);

/// <summary>Single construction path for FileIndex: accepts raw records, resolves parents, produces the index.</summary>
public sealed class IndexBuilder
{
    private const ulong RootFrn = 5;
    private readonly List<RawRecord> _records = new();
    private readonly string _volume;
    private readonly IndexMode _mode;
    private readonly bool _hasMetadata;
    private ulong? _syntheticRoot;

    public IndexBuilder(string volumeRoot, IndexMode mode, bool hasMetadata)
    {
        _volume = volumeRoot; _mode = mode; _hasMetadata = hasMetadata;
    }

    public int Count => _records.Count;

    /// <summary>Declares which parent id means "directly under the volume root" (walks use a synthetic id).</summary>
    public IndexBuilder WithRootFrn(ulong frn) { _syntheticRoot = frn; return this; }

    public void Add(in RawRecord record) => _records.Add(record);

    public void AddRange(IEnumerable<RawRecord> records) => _records.AddRange(records);

    public FileIndex Build()
    {
        var index = new FileIndex(_volume, _mode, _hasMetadata, _records.Count + 16);
        var byFrn = new Dictionary<ulong, int>(_records.Count);
        foreach (var r in _records)
        {
            int i = index.Append(r.Frn, -1, r.Name, r.Size, r.Modified, r.Attributes);
            byFrn.TryAdd(r.Frn & FileIndex.FrnMask, i);
        }
        int orphans = ResolveParents(index, byFrn);
        if (orphans > 0)
            index.AddWarning(new AppError($"{orphans} entries had a parent that is not in the index; they are listed directly under {_volume}."));
        return index;
    }

    private int ResolveParents(FileIndex index, Dictionary<ulong, int> byFrn)
    {
        int orphans = 0;
        for (int i = 0; i < _records.Count; i++)
        {
            ulong parent = _records[i].ParentFrn & FileIndex.FrnMask;
            if (byFrn.TryGetValue(parent, out var p) && p != i) index.SetParent(i, p);
            else if (!IsVolumeRoot(parent)) orphans++;
        }
        return orphans;
    }

    private bool IsVolumeRoot(ulong parentFrn) =>
        _syntheticRoot is { } root ? parentFrn == (root & FileIndex.FrnMask) : parentFrn == RootFrn;
}

/// <summary>Produces an index for one volume or folder tree.</summary>
public interface IIndexSource
{
    IndexMode Mode { get; }
    Result<FileIndex> Build(string root, IProgress<Progress>? progress, CancellationToken ct);
}
