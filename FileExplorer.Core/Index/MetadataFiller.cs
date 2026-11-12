using FileExplorer.Core.Common;
using System.Collections.Concurrent;
using FileExplorer.Core.Fs;

namespace FileExplorer.Core.Index;

/// <summary>
/// Adds sizes and modified times to an MFT index (which only has names and attributes). It reads each folder once, matches the
/// entries by name to rows that are already in the index, and writes them in small batches, so searching is never blocked.
/// </summary>
public sealed class MetadataFiller
{
    private const int BatchDirectories = 64;
    private readonly IFileSource _files;

    public MetadataFiller(IFileSource files) => _files = files;

    /// <summary>Runs to completion (or cancellation). Folders that cannot be read keep size 0 and are counted in <see cref="FillResult.Unreadable"/>.</summary>
    public Result<FillResult> Fill(FileIndex index, CancellationToken ct, IProgress<double>? progress = null)
    {
        if (index.HasMetadata) return Result<FillResult>.Ok(new FillResult(0, 0));
        index.BeginMetadataFill();
        var (children, dirs) = Snapshot(index);
        int total = dirs.Count + 1, done = 0, unreadable = 0;
        long filled = 0;
        try
        {
            var work = new List<int> { -1 };       // -1 = the volume root itself
            work.AddRange(dirs);
            Parallel.ForEach(Partitioner.Create(0, work.Count, BatchDirectories),
                new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 2, 8) },
                range =>
                {
                    var batch = new List<(int, long, long)>();
                    for (int i = range.Item1; i < range.Item2; i++)
                    {
                        ct.ThrowIfCancellationRequested();
                        if (!FillDirectory(index, work[i], children, batch)) Interlocked.Increment(ref unreadable);
                    }
                    index.ApplyMetadata(batch);
                    Interlocked.Add(ref filled, batch.Count);
                    int d = Interlocked.Add(ref done, range.Item2 - range.Item1);
                    double p = (double)d / total;
                    index.MetadataProgress = p;
                    progress?.Report(p);
                });
        }
        catch (OperationCanceledException)
        {
            index.AbandonMetadataFill();
            return new AppError("Reading sizes and dates was cancelled.", 0, index.Volume);
        }
        index.CompleteMetadata();
        return Result<FillResult>.Ok(new FillResult(filled, unreadable));
    }

    /// <summary>Child rows per directory (row -1 = volume root) and the directories to read, taken under one read lock.</summary>
    private static (Dictionary<int, List<int>> Children, List<int> Dirs) Snapshot(FileIndex index)
    {
        var children = new Dictionary<int, List<int>>();
        var dirs = new List<int>();
        using var _ = index.ReadLock();
        for (int i = 0; i < index.Count; i++)
        {
            if (index.IsDeleted(i)) continue;
            int parent = index.Parent(i);
            if (!children.TryGetValue(parent, out var list)) children[parent] = list = new List<int>();
            list.Add(i);
            if (index.IsDir(i) && !index.IsReparse(i)) dirs.Add(i);
        }
        return (children, dirs);
    }

    private bool FillDirectory(FileIndex index, int dirRow, Dictionary<int, List<int>> children, List<(int, long, long)> batch)
    {
        if (!children.TryGetValue(dirRow, out var rows)) return true;
        string path; var names = new (int Row, string Name)[rows.Count];
        using (index.ReadLock())
        {
            path = dirRow < 0 ? index.Volume : index.GetFullPath(dirRow);
            for (int i = 0; i < rows.Count; i++) names[i] = (rows[i], index.Name(rows[i]).ToString());
        }
        var listing = _files.EnumerateDirectory(path);
        if (!listing.IsOk) return false;
        var byName = new Dictionary<string, Models.FileEntry>(listing.Value.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var e in listing.Value) byName[e.Name] = e;
        foreach (var (row, name) in names)
        {
            if (!byName.TryGetValue(name, out var e)) continue;      // renamed or deleted since the index was built
            long modified = e.ModifiedLocal == DateTime.MinValue ? 0 : e.ModifiedLocal.ToUniversalTime().ToFileTimeUtc();
            batch.Add((row, e.IsDir ? 0 : e.Size, modified));
        }
        return true;
    }
}

public readonly record struct FillResult(long Filled, int Unreadable);
