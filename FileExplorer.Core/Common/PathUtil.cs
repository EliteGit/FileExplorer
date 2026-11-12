namespace FileExplorer.Core.Common;

public static class PathUtil
{
    public const string LongPrefix = @"\\?\";
    private const char Sep = '\\';

    /// <summary>Full path with backslashes and no trailing separator (except drive roots "C:\").</summary>
    public static string Normalize(string path)
    {
        var p = path.Replace('/', Sep).Trim();
        if (p.StartsWith(LongPrefix, StringComparison.Ordinal)) p = p[LongPrefix.Length..];
        if (p.Length == 2 && p[1] == ':') return p + Sep;
        if (p.Length > 3) p = p.TrimEnd(Sep);
        return p;
    }

    public static bool IsRoot(string path)
    {
        var p = Normalize(path);
        return p.Length == 3 && p[1] == ':';
    }

    public static string? Parent(string path)
    {
        var p = Normalize(path);
        if (IsRoot(p)) return null;
        var i = p.LastIndexOf(Sep);
        if (i < 0) return null;
        return i == 2 && p[1] == ':' ? p[..3] : p[..i];
    }

    public static string Combine(string folder, string name)
    {
        var f = Normalize(folder);
        return f.EndsWith(Sep) ? f + name : f + Sep + name;
    }

    public static string FileName(string path)
    {
        var p = Normalize(path);
        return IsRoot(p) ? p : p[(p.LastIndexOf(Sep) + 1)..];
    }

    /// <summary>Cumulative segments: "C:\a\b" gives C:\, C:\a, C:\a\b.</summary>
    public static IReadOnlyList<string> Segments(string path)
    {
        var result = new List<string>();
        for (string? p = Normalize(path); p is not null; p = Parent(p)) result.Insert(0, p);
        return result;
    }

    /// <summary>Adds the long-path prefix for paths near MAX_PATH.</summary>
    public static string ToLongPath(string path)
    {
        var p = Normalize(path);
        if (p.Length < 240 || p.StartsWith(LongPrefix, StringComparison.Ordinal)) return p;
        return p.StartsWith(@"\\") ? LongPrefix + "UNC" + p[1..] : LongPrefix + p;
    }
}
