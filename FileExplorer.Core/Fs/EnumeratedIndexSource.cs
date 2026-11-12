using System.Collections.Concurrent;
using FileExplorer.Core.Common;
using FileExplorer.Core.Models;

namespace FileExplorer.Core.Fs;

/// <summary>Builds an index by walking folders through IFileSource, in parallel by top-level folder. Mode = Enumerated.</summary>
public sealed class EnumeratedIndexSource : IIndexSource
{
    private const ulong SyntheticRoot = 1;
    private readonly IFileSource _source;
    private long _nextId = 1;

    public EnumeratedIndexSource(IFileSource source) => _source = source;

    public IndexMode Mode => IndexMode.Enumerated;

    public Result<FileIndex> Build(string root, IProgress<Progress>? progress, CancellationToken ct)
    {
        root = PathUtil.Normalize(root);
        var errors = new ConcurrentQueue<AppError>();
        long skipped = 0, seen = 0;
        var top = _source.EnumerateDirectory(root);
        if (!top.IsOk) return Result<FileIndex>.Err(top.Error);

        var builder = new IndexBuilder(root, IndexMode.Enumerated, hasMetadata: true).WithRootFrn(SyntheticRoot);
        var topFolders = new List<(FileEntry Entry, ulong Id)>();
        foreach (var e in top.Value)
        {
            ulong id = (ulong)Interlocked.Increment(ref _nextId);
            builder.Add(ToRecord(e, id, SyntheticRoot));
            if (ShouldDescend(e)) topFolders.Add((e, id));
        }

        var chunks = new ConcurrentBag<List<RawRecord>>();
        try
        {
            Parallel.ForEach(topFolders,
                new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 2, 8) },
                f =>
                {
                    var local = new List<RawRecord>();
                    Walk(f.Entry.FullPath, f.Id, local, errors, ref skipped, ct);
                    chunks.Add(local);
                    progress?.Report(new Progress(Interlocked.Add(ref seen, local.Count), 0, f.Entry.FullPath));
                });
        }
        catch (OperationCanceledException) { return new AppError("The scan was cancelled.", 0, root); }

        foreach (var c in chunks) builder.AddRange(c);
        var index = builder.Build();
        index.SkippedFolders = (int)skipped;
        foreach (var err in errors.Take(50)) index.AddWarning(err);
        return Result<FileIndex>.Ok(index);
    }

    private void Walk(string folder, ulong folderId, List<RawRecord> output, ConcurrentQueue<AppError> errors, ref long skipped, CancellationToken ct)
    {
        var stack = new Stack<(string Path, ulong Id)>();
        stack.Push((folder, folderId));
        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var (path, id) = stack.Pop();
            var listing = _source.EnumerateDirectory(path);
            if (!listing.IsOk) { Interlocked.Increment(ref skipped); if (errors.Count < 50) errors.Enqueue(listing.Error); continue; }
            foreach (var e in listing.Value)
            {
                ulong childId = (ulong)Interlocked.Increment(ref _nextId);
                output.Add(ToRecord(e, childId, id));
                if (ShouldDescend(e)) stack.Push((e.FullPath, childId));
            }
        }
    }

    /// <summary>Junctions and symlinks are listed but not followed (avoids loops and double counting).</summary>
    private static bool ShouldDescend(FileEntry e) => e.IsDir && !e.IsReparse;

    private static RawRecord ToRecord(FileEntry e, ulong id, ulong parent) =>
        new(id, parent, e.Name, e.Size, e.ModifiedLocal == DateTime.MinValue ? 0 : e.ModifiedLocal.ToUniversalTime().ToFileTimeUtc(), (uint)e.Attributes);
}
