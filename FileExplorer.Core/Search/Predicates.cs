using System.Text.RegularExpressions;
using FileExplorer.Core.Fs;

namespace FileExplorer.Core.Search;

/// <summary>A condition on one index row. Reused by search and by the large/old files reports.</summary>
public interface IRowPredicate
{
    /// <summary>Cheaper predicates run first (name = 1, size/date = 2, path = 5).</summary>
    int Cost { get; }
    bool NeedsMetadata { get; }
    bool Match(FileIndex index, int row);
}

public sealed class NameContains(string needle, bool caseSensitive) : IRowPredicate
{
    private readonly StringComparison _cmp = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
    public int Cost => 1;
    public bool NeedsMetadata => false;
    public bool Match(FileIndex index, int row) => index.Name(row).IndexOf(needle, _cmp) >= 0;
}

/// <summary>Glob over the whole name: * any run, ? one char.</summary>
public sealed class NameGlob(string pattern, bool caseSensitive) : IRowPredicate
{
    public int Cost => 1;
    public bool NeedsMetadata => false;
    public bool Match(FileIndex index, int row) => GlobMatch(pattern, index.Name(row), caseSensitive);

    public static bool GlobMatch(ReadOnlySpan<char> pattern, ReadOnlySpan<char> text, bool caseSensitive)
    {
        int p = 0, t = 0, star = -1, mark = 0;
        while (t < text.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || Eq(pattern[p], text[t], caseSensitive))) { p++; t++; }
            else if (p < pattern.Length && pattern[p] == '*') { star = p++; mark = t; }
            else if (star >= 0) { p = star + 1; t = ++mark; }
            else return false;
        }
        while (p < pattern.Length && pattern[p] == '*') p++;
        return p == pattern.Length;
    }

    private static bool Eq(char a, char b, bool cs) => cs ? a == b : char.ToUpperInvariant(a) == char.ToUpperInvariant(b);
}

public sealed class NameRegex(Regex regex) : IRowPredicate
{
    public int Cost => 3;
    public bool NeedsMetadata => false;
    public bool Match(FileIndex index, int row) => regex.IsMatch(index.Name(row));
}

public sealed class ExtensionIn(IReadOnlyCollection<string> extensions) : IRowPredicate
{
    private readonly string[] _set = extensions.Select(e => e.TrimStart('.')).ToArray();
    public int Cost => 1;
    public bool NeedsMetadata => false;
    public bool Match(FileIndex index, int row) =>
        !index.IsDir(row) && Has(index.Extension(row));

    private bool Has(ReadOnlySpan<char> ext)
    {
        foreach (var e in _set) if (ext.Equals(e, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}

public sealed class SizeRange(long min, long max) : IRowPredicate
{
    public int Cost => 2;
    public bool NeedsMetadata => true;
    public bool Match(FileIndex index, int row) => !index.IsDir(row) && index.Size(row) >= min && index.Size(row) <= max;
}

/// <summary>Last-write time between two FILETIME values (UTC ticks). Rows with unknown time never match.</summary>
public sealed class ModifiedRange(long fromFileTime, long toFileTime) : IRowPredicate
{
    public int Cost => 2;
    public bool NeedsMetadata => true;
    public bool Match(FileIndex index, int row)
    {
        long m = index.Modified(row);
        return m > 0 && m >= fromFileTime && m <= toFileTime;
    }
}

public sealed class PathContains(string needle, bool caseSensitive) : IRowPredicate
{
    private readonly StringComparison _cmp = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
    public int Cost => 5;
    public bool NeedsMetadata => false;
    public bool Match(FileIndex index, int row) => index.GetFullPath(row).Contains(needle, _cmp);
}

public sealed class KindIs(bool folders) : IRowPredicate
{
    public int Cost => 1;
    public bool NeedsMetadata => false;
    public bool Match(FileIndex index, int row) => index.IsDir(row) == folders;
}

public sealed class Not(IRowPredicate inner) : IRowPredicate
{
    public int Cost => inner.Cost;
    public bool NeedsMetadata => inner.NeedsMetadata;
    public bool Match(FileIndex index, int row) => !inner.Match(index, row);
}

/// <summary>Rows whose full path is in a fixed set (used by tag: and other feature-provided terms).</summary>
public sealed class PathInSet(IEnumerable<string> paths) : IRowPredicate
{
    private readonly HashSet<string> _paths = new(paths, StringComparer.OrdinalIgnoreCase);
    public int Cost => 5;
    public bool NeedsMetadata => false;
    public bool Match(FileIndex index, int row) => _paths.Contains(index.GetFullPath(row));
}
