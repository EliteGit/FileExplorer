using System.Text;
using FileExplorer.Core.Common;
using FileExplorer.Core.Fs;
using FileExplorer.Core.Search;

namespace FileExplorer.Core.Reports;

/// <summary>Large files, old files and empty folders. The first two are saved queries over QueryEngine predicates.</summary>
public static class Reports
{
    public static Query LargeFiles(long minBytes) =>
        new($"size:>={minBytes}", new IRowPredicate[] { new SizeRange(minBytes, long.MaxValue) });

    /// <summary>Files last modified before the cutoff, at least <paramref name="minBytes"/> big.</summary>
    public static Query OldFiles(DateTime cutoffLocal, long minBytes = 0) =>
        new($"dm:<{cutoffLocal:yyyy-MM-dd}", new IRowPredicate[]
        {
            new ModifiedRange(1, DateTime.SpecifyKind(cutoffLocal, DateTimeKind.Local).ToFileTimeUtc()), new SizeRange(minBytes, long.MaxValue),
        });

    /// <summary>Folders without any live child, found from the index arrays.</summary>
    public static int[] EmptyFolders(FileIndex index)
    {
        using var _ = index.ReadLock();
        var hasChild = new bool[index.Count];
        for (int i = 0; i < index.Count; i++)
            if (!index.IsDeleted(i) && index.Parent(i) is >= 0 and var p) hasChild[p] = true;
        var rows = new List<int>();
        for (int i = 0; i < index.Count; i++)
            if (!index.IsDeleted(i) && index.IsDir(i) && !index.IsReparse(i) && !hasChild[i]) rows.Add(i);
        return rows.ToArray();
    }
}

public interface IRow
{
    IReadOnlyList<string> Cells { get; }
}

public sealed record SimpleRow(params string[] Values) : IRow
{
    public IReadOnlyList<string> Cells => Values;
}

/// <summary>The only CSV writer: RFC 4180 quoting, UTF-8 with BOM so Excel opens it correctly.</summary>
public static class CsvExporter
{
    public static string Format(IReadOnlyList<string> header, IEnumerable<IRow> rows)
    {
        var sb = new StringBuilder();
        AppendLine(sb, header);
        foreach (var r in rows) AppendLine(sb, r.Cells);
        return sb.ToString();
    }

    public static Result<Unit> Export(string path, IReadOnlyList<string> header, IEnumerable<IRow> rows)
    {
        try
        {
            File.WriteAllText(path, Format(header, rows), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            return Result.Ok();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { return Result.Err(new AppError($"Cannot write the CSV file: {e.Message}", 0, path)); }
    }

    public static string Escape(string cell) =>
        cell.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0 ? "\"" + cell.Replace("\"", "\"\"") + "\"" : cell;

    private static void AppendLine(StringBuilder sb, IEnumerable<string> cells) =>
        sb.Append(string.Join(',', cells.Select(Escape))).Append("\r\n");
}
