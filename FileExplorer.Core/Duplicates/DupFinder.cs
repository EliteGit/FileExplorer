using System.Collections.Concurrent;
using FileExplorer.Core.Common;
using FileExplorer.Core.Fs;
using FileExplorer.Core.Hashing;

namespace FileExplorer.Core.Duplicates;

public readonly record struct DupCandidate(string Path, long Size, DateTime Modified);

public sealed record DuplicateGroup(long Size, string Hash, IReadOnlyList<DupCandidate> Files)
{
    public long WastedBytes => Size * (Files.Count - 1);
}

public sealed record DupResult(IReadOnlyList<DuplicateGroup> Groups, IReadOnlyList<AppError> Skipped);

/// <summary>Size groups, then partial hash (first+last 64 KB), then full hash. Parallel with a bounded degree; cancellable.</summary>
public sealed class DupFinder
{
    private readonly FileHasher _hasher;
    private readonly int _degree;

    public DupFinder(FileHasher hasher, int? degree = null) { _hasher = hasher; _degree = degree ?? Math.Clamp(Environment.ProcessorCount / 2, 1, 4); }

    /// <summary>Files from an index (the single source of candidate lists).</summary>
    public static List<DupCandidate> CandidatesFrom(FileIndex index)
    {
        var list = new List<DupCandidate>();
        using var _ = index.ReadLock();
        for (int i = 0; i < index.Count; i++)
        {
            if (index.IsDeleted(i) || index.IsDir(i) || index.IsReparse(i)) continue;
            long m = index.Modified(i);
            list.Add(new DupCandidate(index.GetFullPath(i), index.Size(i), m > 0 ? DateTime.FromFileTimeUtc(m).ToLocalTime() : DateTime.MinValue));
        }
        return list;
    }

    public Result<DupResult> Find(IReadOnlyList<DupCandidate> candidates, long minSize, bool includeEmpty, IProgress<Progress>? progress, CancellationToken ct)
    {
        var errors = new ConcurrentBag<AppError>();
        try
        {
            var sameSize = candidates.Where(c => c.Size >= minSize && (c.Size > 0 || includeEmpty)).GroupBy(c => c.Size).Where(g => g.Count() > 1).ToList();
            long total = sameSize.Sum(g => g.Count()), done = 0;
            var groups = new ConcurrentBag<DuplicateGroup>();
            var opts = new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = _degree };
            Parallel.ForEach(sameSize, opts, sizeGroup =>
            {
                foreach (var g in Refine(sizeGroup.Key, sizeGroup.ToList(), errors, ct)) groups.Add(g);
                progress?.Report(new Progress(Interlocked.Add(ref done, sizeGroup.Count()), total));
            });
            var ordered = groups.OrderByDescending(g => g.WastedBytes).ThenBy(g => g.Files[0].Path, StringComparer.OrdinalIgnoreCase).ToList();
            return Result<DupResult>.Ok(new DupResult(ordered, errors.ToList()));
        }
        catch (OperationCanceledException) { return new AppError("The duplicate search was cancelled."); }
    }

    private IEnumerable<DuplicateGroup> Refine(long size, List<DupCandidate> files, ConcurrentBag<AppError> errors, CancellationToken ct)
    {
        if (size == 0) { yield return new DuplicateGroup(0, "empty", files); yield break; }
        foreach (var partialGroup in GroupByHash(files, f => _hasher.HashPartial(f.Path), errors, ct))
        {
            if (size <= 2L * FileHasher.PartialBytes)       // the partial hash already covered the whole file
            {
                yield return new DuplicateGroup(size, partialGroup.Key, partialGroup.ToList());
                continue;
            }
            foreach (var full in GroupByHash(partialGroup.ToList(), f => _hasher.HashFull(f.Path, HashKind.XxHash64, null, ct), errors, ct))
                yield return new DuplicateGroup(size, full.Key, full.ToList());
        }
    }

    private static IEnumerable<IGrouping<string, DupCandidate>> GroupByHash(List<DupCandidate> files, Func<DupCandidate, Result<string>> hash,
        ConcurrentBag<AppError> errors, CancellationToken ct)
    {
        var hashed = new List<(string Hash, DupCandidate File)>();
        foreach (var f in files)
        {
            ct.ThrowIfCancellationRequested();
            var h = hash(f);
            if (h.IsOk) hashed.Add((h.Value, f)); else errors.Add(h.Error);
        }
        return hashed.GroupBy(x => x.Hash, x => x.File).Where(g => g.Count() > 1);
    }
}

/// <summary>Safety rule for deleting duplicates: at least one copy in each group must stay.</summary>
public static class DuplicateSelection
{
    public static Result<Unit> Validate(DuplicateGroup group, IReadOnlyCollection<string> selectedForDelete)
    {
        int selected = group.Files.Count(f => selectedForDelete.Contains(f.Path, StringComparer.OrdinalIgnoreCase));
        return selected >= group.Files.Count
            ? Result.Err(new AppError("Keep at least one copy: every file of this group is selected for deletion."))
            : Result.Ok();
    }

    /// <summary>Marks every copy except the one to keep (newest by default).</summary>
    public static IReadOnlyList<string> AllButOne(DuplicateGroup group, bool keepNewest = true)
    {
        var keep = (keepNewest ? group.Files.OrderByDescending(f => f.Modified) : group.Files.OrderBy(f => f.Modified)).First();
        return group.Files.Where(f => f.Path != keep.Path).Select(f => f.Path).ToList();
    }
}
