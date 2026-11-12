using System.Text;
using FileExplorer.Core.Common;

namespace FileExplorer.Core.Fs;

public enum IndexMode { Enumerated, Mft }

/// <summary>
/// Struct-of-arrays index of one volume: a shared char pool plus parallel arrays, no object per file.
/// Readers take <see cref="ReadLock"/>; the USN watcher mutates through <see cref="Apply"/>.
/// </summary>
public sealed class FileIndex
{
    public const uint DeletedFlag = 0x8000_0000;
    public const ulong FrnMask = 0x0000_FFFF_FFFF_FFFF;

    private readonly ReaderWriterLockSlim _lock = new();
    private char[] _pool;
    private int _poolLen;
    private int[] _nameStart, _nameLen, _parent;
    private ulong[] _frn;
    private long[] _size, _mtime;
    private uint[] _attrs;
    private Dictionary<ulong, int>? _byFrn;
    private readonly List<AppError> _warnings = new();

    internal FileIndex(string volume, IndexMode mode, bool hasMetadata, int capacity = 1024)
    {
        Volume = PathUtil.Normalize(volume);
        Mode = mode; HasMetadata = hasMetadata;
        capacity = Math.Max(capacity, 16);
        _pool = new char[capacity * 12];
        _nameStart = new int[capacity]; _nameLen = new int[capacity]; _parent = new int[capacity];
        _frn = new ulong[capacity]; _size = new long[capacity]; _mtime = new long[capacity]; _attrs = new uint[capacity];
    }

    public string Volume { get; }
    public IndexMode Mode { get; }
    /// <summary>False for MFT enumeration: USN records carry names and attributes only, no size or time.</summary>
    public bool HasMetadata { get; }
    public int Count { get; private set; }
    public int DeletedCount { get; private set; }
    public int LiveCount => Count - DeletedCount;
    public DateTime BuiltUtc { get; internal set; } = DateTime.UtcNow;
    public DateTime LastUpdateUtc { get; private set; } = DateTime.UtcNow;
    public long JournalId { get; internal set; }
    public long NextUsn { get; internal set; }
    /// <summary>Folders that could not be read while building (shown to the user, never swallowed).</summary>
    public IReadOnlyList<AppError> Warnings => _warnings;
    public int SkippedFolders { get; internal set; }
    internal void AddWarning(AppError e) { if (_warnings.Count < 100) _warnings.Add(e); }

    // ---- locking ----
    public IDisposable ReadLock() => new Releaser(_lock, write: false);
    private IDisposable WriteLock() => new Releaser(_lock, write: true);

    private sealed class Releaser : IDisposable
    {
        private readonly ReaderWriterLockSlim _l; private readonly bool _w;
        public Releaser(ReaderWriterLockSlim l, bool write) { _l = l; _w = write; if (write) l.EnterWriteLock(); else l.EnterReadLock(); }
        public void Dispose() { if (_w) _l.ExitWriteLock(); else _l.ExitReadLock(); }
    }

    // ---- row access (callers hold ReadLock for consistency during scans) ----
    public ReadOnlySpan<char> Name(int i) => new(_pool, _nameStart[i], _nameLen[i]);
    public long Size(int i) => _size[i];
    /// <summary>Last-write time as FILETIME (UTC ticks since 1601); 0 when unknown.</summary>
    public long Modified(int i) => _mtime[i];
    public uint Attributes(int i) => _attrs[i] & ~DeletedFlag;
    public ulong Frn(int i) => _frn[i];
    public int Parent(int i) => _parent[i];
    public bool IsDeleted(int i) => (_attrs[i] & DeletedFlag) != 0;
    public bool IsDir(int i) => (_attrs[i] & (uint)FileAttributes.Directory) != 0;
    public bool IsReparse(int i) => (_attrs[i] & (uint)FileAttributes.ReparsePoint) != 0;

    public ReadOnlySpan<char> Extension(int i)
    {
        var n = Name(i);
        int dot = n.LastIndexOf('.');
        return dot <= 0 || dot == n.Length - 1 ? ReadOnlySpan<char>.Empty : n[(dot + 1)..];
    }

    /// <summary>The only way to build a full path from the parent chain.</summary>
    public string GetFullPath(int i)
    {
        var parts = new List<int>(8);
        for (int cur = i, guard = 0; cur >= 0 && guard < 4096; cur = _parent[cur], guard++) parts.Add(cur);
        var sb = new StringBuilder(Volume.TrimEnd('\\'), 64);
        for (int k = parts.Count - 1; k >= 0; k--) { sb.Append('\\'); sb.Append(Name(parts[k])); }
        if (parts.Count == 0) sb.Append('\\');
        return sb.ToString();
    }

    // ---- construction (IndexBuilder / cache) ----
    internal int Append(ulong frn, int parent, ReadOnlySpan<char> name, long size, long mtime, uint attrs)
    {
        if (Count == _frn.Length) Grow();
        int i = Count++;
        _frn[i] = frn; _parent[i] = parent; _size[i] = size; _mtime[i] = mtime; _attrs[i] = attrs;
        StoreName(i, name);
        _byFrn?.TryAdd(frn & FrnMask, i);
        return i;
    }

    private void StoreName(int i, ReadOnlySpan<char> name)
    {
        if (_poolLen + name.Length > _pool.Length) Array.Resize(ref _pool, Math.Max(_pool.Length * 2, _poolLen + name.Length));
        name.CopyTo(_pool.AsSpan(_poolLen));
        _nameStart[i] = _poolLen; _nameLen[i] = name.Length;
        _poolLen += name.Length;
    }

    private void Grow()
    {
        int n = _frn.Length * 2;
        Array.Resize(ref _nameStart, n); Array.Resize(ref _nameLen, n); Array.Resize(ref _parent, n);
        Array.Resize(ref _frn, n); Array.Resize(ref _size, n); Array.Resize(ref _mtime, n); Array.Resize(ref _attrs, n);
    }

    internal void SetParent(int i, int parent) => _parent[i] = parent;

    // ---- live updates ----
    public void Apply(IndexChange change)
    {
        using var _ = WriteLock();
        _byFrn ??= BuildFrnMap();
        switch (change.Kind)
        {
            case IndexChangeKind.Created: ApplyCreated(change); break;
            case IndexChangeKind.Deleted: ApplyDeleted(change); break;
            case IndexChangeKind.Renamed: ApplyRenamed(change); break;
        }
        LastUpdateUtc = DateTime.UtcNow;
    }

    private Dictionary<ulong, int> BuildFrnMap()
    {
        var map = new Dictionary<ulong, int>(Count);
        for (int i = 0; i < Count; i++) map.TryAdd(_frn[i] & FrnMask, i);
        return map;
    }

    private int ParentIndex(ulong parentFrn) =>
        _byFrn!.TryGetValue(parentFrn & FrnMask, out var p) ? p : -1;

    private void ApplyCreated(IndexChange c)
    {
        if (_byFrn!.TryGetValue(c.Frn & FrnMask, out var existing)) { Revive(existing, c); return; }
        Append(c.Frn, ParentIndex(c.ParentFrn), c.Name, 0, 0, c.Attributes);
    }

    private void Revive(int i, IndexChange c)
    {
        if (IsDeleted(i)) DeletedCount--;
        _attrs[i] = c.Attributes; _parent[i] = ParentIndex(c.ParentFrn);
        StoreName(i, c.Name);
    }

    private void ApplyDeleted(IndexChange c)
    {
        if (!_byFrn!.TryGetValue(c.Frn & FrnMask, out var i) || IsDeleted(i)) return;
        _attrs[i] |= DeletedFlag; DeletedCount++;
    }

    private void ApplyRenamed(IndexChange c)
    {
        if (!_byFrn!.TryGetValue(c.Frn & FrnMask, out var i)) { ApplyCreated(c); return; }
        _parent[i] = ParentIndex(c.ParentFrn);
        StoreName(i, c.Name);    // appended to the pool; the old text becomes garbage until Compact()
    }

    /// <summary>Rebuilds without deleted rows and unused pool text.</summary>
    public FileIndex Compact()
    {
        using var _ = WriteLock();
        var map = new int[Count];
        var fresh = new FileIndex(Volume, Mode, HasMetadata, Math.Max(LiveCount, 16))
        { BuiltUtc = BuiltUtc, JournalId = JournalId, NextUsn = NextUsn };
        for (int i = 0; i < Count; i++)
            map[i] = IsDeleted(i) ? -1 : fresh.Append(_frn[i], -1, Name(i), _size[i], _mtime[i], _attrs[i]);
        for (int i = 0; i < Count; i++)
            if (map[i] >= 0) fresh.SetParent(map[i], _parent[i] >= 0 ? map[_parent[i]] : -1);
        foreach (var w in _warnings) fresh.AddWarning(w);
        fresh.SkippedFolders = SkippedFolders;
        return fresh;
    }

    // ---- cache support ----
    internal (char[] Pool, int PoolLen, int[] Start, int[] Len, int[] Parent, ulong[] Frn, long[] Size, long[] Mtime, uint[] Attrs) Raw() =>
        (_pool, _poolLen, _nameStart, _nameLen, _parent, _frn, _size, _mtime, _attrs);

    internal static FileIndex FromRaw(string volume, IndexMode mode, bool hasMeta, int count, char[] pool, int poolLen,
        int[] start, int[] len, int[] parent, ulong[] frn, long[] size, long[] mtime, uint[] attrs)
    {
        var idx = new FileIndex(volume, mode, hasMeta, 16)
        {
            _pool = pool, _poolLen = poolLen, _nameStart = start, _nameLen = len, _parent = parent,
            _frn = frn, _size = size, _mtime = mtime, _attrs = attrs, Count = count
        };
        for (int i = 0; i < count; i++) if ((attrs[i] & DeletedFlag) != 0) idx.DeletedCount++;
        return idx;
    }
}

public enum IndexChangeKind { Created, Deleted, Renamed }

/// <summary>A live change from the USN journal (or a fake stream in tests).</summary>
public sealed record IndexChange(IndexChangeKind Kind, ulong Frn, ulong ParentFrn, string Name, uint Attributes);
