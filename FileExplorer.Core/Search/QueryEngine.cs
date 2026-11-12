using System.Text.RegularExpressions;
using FileExplorer.Core.Common;
using FileExplorer.Core.Fs;

namespace FileExplorer.Core.Search;

/// <summary>Runs a Query over the index arrays in parallel and returns matching row ids (not objects), in index order.</summary>
public sealed class QueryEngine
{
    private const int ChunkSize = 1 << 16;

    public Result<int[]> Search(FileIndex index, Query query, CancellationToken ct)
    {
        if (query.IsEmpty) return Result<int[]>.Ok(Array.Empty<int>());
        var preds = query.Predicates.OrderBy(p => p.Cost).ToArray();
        if (!index.HasMetadata && preds.Any(p => p.NeedsMetadata))
            return new AppError($"{index.Volume} uses the fast MFT index, which has no sizes or dates. size: and dm: need the enumerated index (relaunch without elevation, or rebuild in enumerated mode).");

        using var _ = index.ReadLock();
        int count = index.Count, chunks = (count + ChunkSize - 1) / ChunkSize;
        var results = new int[chunks][];
        try
        {
            Parallel.For(0, chunks, new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Environment.ProcessorCount },
                c => results[c] = ScanChunk(index, preds, c * ChunkSize, Math.Min(count, (c + 1) * ChunkSize), ct));
        }
        catch (OperationCanceledException) { return new AppError("Search cancelled."); }
        catch (RegexMatchTimeoutException) { return new AppError("The regular expression took too long to evaluate."); }
        catch (AggregateException e) when (e.InnerException is RegexMatchTimeoutException) { return new AppError("The regular expression took too long to evaluate."); }
        return Result<int[]>.Ok(Merge(results));
    }

    private static int[] ScanChunk(FileIndex index, IRowPredicate[] preds, int from, int to, CancellationToken ct)
    {
        var hits = new List<int>();
        for (int row = from; row < to; row++)
        {
            if ((row & 0x3FFF) == 0) ct.ThrowIfCancellationRequested();
            if (index.IsDeleted(row)) continue;
            if (MatchesAll(index, preds, row)) hits.Add(row);
        }
        return hits.ToArray();
    }

    private static bool MatchesAll(FileIndex index, IRowPredicate[] preds, int row)
    {
        foreach (var p in preds) if (!p.Match(index, row)) return false;
        return true;
    }

    private static int[] Merge(int[][] parts)
    {
        var all = new int[parts.Sum(p => p.Length)];
        int pos = 0;
        foreach (var p in parts) { p.CopyTo(all, pos); pos += p.Length; }
        return all;
    }
}
