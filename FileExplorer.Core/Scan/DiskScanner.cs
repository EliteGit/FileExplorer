using FileExplorer.Core.Common;
using FileExplorer.Core.Fs;
using FileExplorer.Core.Native;

namespace FileExplorer.Core.Scan;

/// <summary>Volume geometry used for allocated sizes and the free-space block.</summary>
public readonly record struct VolumeInfo(long ClusterSize, long TotalBytes, long FreeBytes)
{
    public static Result<VolumeInfo> Query(string path)
    {
        var root = Path.GetPathRoot(PathUtil.Normalize(path)) ?? path;
        if (!NativeMethods.GetDiskFreeSpaceW(root, out var spc, out var bps, out _, out _))
            return Win32Error.FromLastError(root, "Cannot read the volume geometry");
        long cluster = (long)spc * bps;
        if (!NativeMethods.GetDiskFreeSpaceExW(root, out var avail, out var total, out var free))
            return Win32Error.FromLastError(root, "Cannot read the volume size");
        return Result<VolumeInfo>.Ok(new VolumeInfo(cluster, (long)total, (long)free));
    }
}

/// <summary>
/// Computes folder sizes from a FileIndex bottom-up over the arrays (it never walks the disk itself).
/// Allocated size rounds each file up to whole clusters; files sharing a file id (hard links) count once.
/// </summary>
public static class DiskScanner
{
    public static FolderSizeTree Compute(FileIndex index, long clusterSize, long volumeTotal = 0, long volumeFree = 0)
    {
        using var _ = index.ReadLock();
        int n = index.Count;
        var (childStart, childRows, rootChildren) = BuildChildren(index);
        var logical = new long[n]; var allocated = new long[n]; var files = new long[n];

        var seen = new HashSet<ulong>();
        for (int i = 0; i < n; i++)
        {
            if (index.IsDeleted(i) || index.IsDir(i)) continue;
            ulong frn = index.Frn(i) & FileIndex.FrnMask;
            if (frn != 0 && !seen.Add(frn)) continue;            // another hard link to the same data
            long size = index.Size(i);
            logical[i] = size; allocated[i] = RoundUp(size, clusterSize); files[i] = 1;
        }

        var order = TopDownOrder(index, childStart, childRows, rootChildren);
        long rl = 0, ra = 0, rf = 0;
        for (int k = order.Count - 1; k >= 0; k--)       // children before parents
        {
            int i = order[k];
            int p = index.Parent(i);
            if (p >= 0) { logical[p] += logical[i]; allocated[p] += allocated[i]; files[p] += files[i]; }
            else { rl += logical[i]; ra += allocated[i]; rf += files[i]; }
        }

        SortBySize(childRows, childStart, allocated);
        Array.Sort(rootChildren, (a, b) => allocated[b].CompareTo(allocated[a]));
        return new FolderSizeTree(index, logical, allocated, files, childStart, childRows, rl, ra, rf, rootChildren, volumeTotal, volumeFree, clusterSize);
    }

    public static long RoundUp(long size, long cluster) =>
        size <= 0 || cluster <= 1 ? Math.Max(size, 0) : (size + cluster - 1) / cluster * cluster;

    private static (int[] Start, int[] Rows, int[] RootChildren) BuildChildren(FileIndex index)
    {
        int n = index.Count;
        var counts = new int[n + 1];
        var roots = new List<int>();
        for (int i = 0; i < n; i++)
        {
            if (index.IsDeleted(i)) continue;
            int p = index.Parent(i);
            if (p >= 0 && !index.IsDeleted(p)) counts[p + 1]++; else roots.Add(i);
        }
        for (int i = 0; i < n; i++) counts[i + 1] += counts[i];
        var start = (int[])counts.Clone();
        var rows = new int[counts[n]];
        var fill = (int[])counts.Clone();
        for (int i = 0; i < n; i++)
        {
            if (index.IsDeleted(i)) continue;
            int p = index.Parent(i);
            if (p >= 0 && !index.IsDeleted(p)) rows[fill[p]++] = i;
        }
        return (start, rows, roots.ToArray());
    }

    private static List<int> TopDownOrder(FileIndex index, int[] start, int[] rows, int[] roots)
    {
        var order = new List<int>(index.LiveCount);
        var queue = new Queue<int>(roots);
        while (queue.Count > 0)
        {
            int i = queue.Dequeue();
            order.Add(i);
            if (!index.IsDir(i)) continue;
            for (int c = start[i]; c < start[i + 1]; c++) queue.Enqueue(rows[c]);
        }
        return order;
    }

    private static void SortBySize(int[] rows, int[] start, long[] size)
    {
        for (int i = 0; i + 1 < start.Length; i++)
        {
            int len = start[i + 1] - start[i];
            if (len > 1) Array.Sort(rows, start[i], len, Comparer<int>.Create((a, b) => size[b].CompareTo(size[a])));
        }
    }
}
