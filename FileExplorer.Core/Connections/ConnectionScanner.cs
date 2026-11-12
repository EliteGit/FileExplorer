using System.Collections.Concurrent;
using FileExplorer.Core.Common;
using FileExplorer.Core.Fs;

namespace FileExplorer.Core.Connections;

public sealed record ConnectionOptions(
    bool Shortcuts = true, bool Imports = true, bool Mentions = true,
    /// <summary>When false, files inside the Windows folder are not read and their imports are not followed (they only add noise).</summary>
    bool IncludeWindowsFolder = false,
    long MaxTextBytes = 512 * 1024);

public sealed record ConnectionScanResult(ConnectionGraph Graph, IReadOnlyDictionary<ConnectionKind, int> Counts, int FilesRead, IReadOnlyList<AppError> Problems, int UnresolvedImports);

/// <summary>
/// Reads the files of one or more indexes and records what points at what: shortcuts, symlinks/junctions, DLL imports (resolved to files
/// on disk) and paths written inside text files. Unreadable files are counted and listed, never skipped silently.
/// </summary>
public sealed class ConnectionScanner
{
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".json", ".xml", ".ini", ".cfg", ".conf", ".config", ".yml", ".yaml", ".toml", ".bat", ".cmd", ".ps1", ".txt", ".py", ".js", ".ts",
        ".sh", ".vdf", ".props", ".targets", ".csproj", ".vcxproj", ".sln", ".slnx", ".properties", ".env", ".lua", ".gradle", ".cmake", ".mk",
    };
    private static readonly HashSet<string> BinaryExtensions = new(StringComparer.OrdinalIgnoreCase) { ".exe", ".dll", ".ocx", ".sys", ".cpl", ".scr" };

    private readonly record struct Found(string From, string To, ConnectionKind Kind, bool TargetMissing);

    public Result<ConnectionScanResult> Scan(IReadOnlyList<FileIndex> indexes, ConnectionOptions options, IProgress<Progress>? progress, CancellationToken ct)
    {
        var windows = Path.TrimEndingDirectorySeparator(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        var dllDirs = new DllSearchPath();
        var found = new ConcurrentBag<Found>();
        var problems = new ConcurrentBag<AppError>();
        long processed = 0, total = indexes.Sum(i => (long)i.LiveCount), filesRead = 0, unresolved = 0;

        try
        {
            foreach (var index in indexes)
            {
                var rows = RowsToRead(index, options);
                Parallel.ForEach(rows, new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 2, 6) }, row =>
                {
                    var (path, ext, isReparse) = Describe(index, row);
                    if (!options.IncludeWindowsFolder && IsUnder(path, windows)) return;
                    Interlocked.Increment(ref filesRead);
                    Handle(path, ext, isReparse, options, windows, dllDirs, found, problems, ref unresolved);
                    long n = Interlocked.Increment(ref processed);
                    if ((n & 0x3FF) == 0) progress?.Report(new Progress(n, total, path));
                });
            }
        }
        catch (OperationCanceledException) { return new AppError("The connection scan was cancelled."); }

        var graph = new ConnectionGraph();
        foreach (var f in found)
        {
            int from = graph.Add(f.From), to = graph.Add(f.To, f.TargetMissing);
            graph.AddEdge(from, to, f.Kind);
        }
        var counts = Enum.GetValues<ConnectionKind>().ToDictionary(k => k, graph.Count);
        return Result<ConnectionScanResult>.Ok(new ConnectionScanResult(graph, counts, (int)filesRead, problems.Take(200).ToList(), (int)unresolved));
    }

    // ---- which rows matter ----
    private static List<int> RowsToRead(FileIndex index, ConnectionOptions o)
    {
        var rows = new List<int>();
        using var _ = index.ReadLock();
        for (int i = 0; i < index.Count; i++)
        {
            if (index.IsDeleted(i)) continue;
            var ext = Path.GetExtension(index.Name(i).ToString());
            if (o.Shortcuts && (index.IsReparse(i) || ext.Equals(".lnk", StringComparison.OrdinalIgnoreCase) || ext.Equals(".url", StringComparison.OrdinalIgnoreCase))) rows.Add(i);
            else if (index.IsDir(i)) continue;
            else if (o.Imports && BinaryExtensions.Contains(ext)) rows.Add(i);
            else if (o.Mentions && TextExtensions.Contains(ext)) rows.Add(i);
        }
        return rows;
    }

    private static (string Path, string Ext, bool Reparse) Describe(FileIndex index, int row)
    {
        using var _ = index.ReadLock();
        return (index.GetFullPath(row), Path.GetExtension(index.Name(row).ToString()), index.IsReparse(row));
    }

    private static bool IsUnder(string path, string folder) =>
        path.StartsWith(folder + "\\", StringComparison.OrdinalIgnoreCase) || path.Equals(folder, StringComparison.OrdinalIgnoreCase);

    // ---- one file ----
    private void Handle(string path, string ext, bool isReparse, ConnectionOptions o, string windows, DllSearchPath dlls,
        ConcurrentBag<Found> found, ConcurrentBag<AppError> problems, ref long unresolved)
    {
        if (isReparse && o.Shortcuts) { ReadLink(path, found, problems); return; }
        if (o.Shortcuts && ext.Equals(".lnk", StringComparison.OrdinalIgnoreCase)) { AddShortcut(path, LnkParser.ReadTarget(path), found, problems); return; }
        if (o.Shortcuts && ext.Equals(".url", StringComparison.OrdinalIgnoreCase)) { AddShortcut(path, UrlFileParser.ReadTarget(path), found, problems); return; }
        if (o.Imports && BinaryExtensions.Contains(ext)) { ReadImports(path, o, windows, dlls, found, problems, ref unresolved); return; }
        if (o.Mentions && TextExtensions.Contains(ext)) ReadMentions(path, o, windows, found, problems);
    }

    private static void AddShortcut(string path, Result<string> target, ConcurrentBag<Found> found, ConcurrentBag<AppError> problems)
    {
        // A shortcut to a web page, network share or special folder is simply not a drive connection; only real read failures are listed.
        if (!target.IsOk) { if (target.Error.Win32Code != 0 || target.Error.Message.StartsWith("Cannot")) problems.Add(target.Error); return; }
        found.Add(new Found(path, target.Value, ConnectionKind.Shortcut, !File.Exists(target.Value) && !Directory.Exists(target.Value)));
    }

    private static void ReadLink(string path, ConcurrentBag<Found> found, ConcurrentBag<AppError> problems)
    {
        try
        {
            FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
            var target = info.LinkTarget;
            if (string.IsNullOrEmpty(target)) return;
            var full = Path.GetFullPath(target, Path.GetDirectoryName(path) ?? "");
            found.Add(new Found(path, full, ConnectionKind.Link, !File.Exists(full) && !Directory.Exists(full)));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { problems.Add(new AppError($"Cannot read the link: {e.Message}", 0, path)); }
    }

    private static void ReadImports(string path, ConnectionOptions o, string windows, DllSearchPath dlls, ConcurrentBag<Found> found, ConcurrentBag<AppError> problems, ref long unresolved)
    {
        var imports = PeImportReader.ReadImports(path);
        if (!imports.IsOk)
        {
            if (!imports.Error.Message.StartsWith("Not a Windows") && !imports.Error.Message.StartsWith("Damaged")) problems.Add(imports.Error);
            return;
        }
        var folder = Path.GetDirectoryName(path) ?? "";
        foreach (var name in imports.Value)
        {
            var resolved = dlls.Resolve(name, folder);
            if (resolved is null) { if (!DllSearchPath.IsVirtual(name)) Interlocked.Increment(ref unresolved); continue; }
            if (!o.IncludeWindowsFolder && IsUnder(resolved, windows)) continue;          // ordinary Windows DLLs are noise unless asked for
            found.Add(new Found(path, resolved, ConnectionKind.Imports, false));
        }
    }

    private static void ReadMentions(string path, ConnectionOptions o, string windows, ConcurrentBag<Found> found, ConcurrentBag<AppError> problems)
    {
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists || fi.Length == 0 || fi.Length > o.MaxTextBytes) return;
            var text = File.ReadAllText(path);
            foreach (var mentioned in PathMentions.Find(text))
            {
                if (string.Equals(mentioned, path, StringComparison.OrdinalIgnoreCase)) continue;
                if (!File.Exists(mentioned) && !Directory.Exists(mentioned)) continue;       // only paths that really exist count as connections
                if (!o.IncludeWindowsFolder && IsUnder(mentioned, windows)) continue;
                found.Add(new Found(path, mentioned, ConnectionKind.Mentions, false));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { problems.Add(new AppError($"Cannot read the file: {e.Message}", 0, path)); }
    }
}

/// <summary>Where Windows looks for a DLL: the program's own folder, then System32, Windows, and PATH.</summary>
public sealed class DllSearchPath
{
    private readonly string[] _dirs;
    private readonly ConcurrentDictionary<string, HashSet<string>> _listings = new(StringComparer.OrdinalIgnoreCase);

    public DllSearchPath(IEnumerable<string>? extraDirs = null)
    {
        var dirs = new List<string> { Environment.SystemDirectory, Environment.GetFolderPath(Environment.SpecialFolder.Windows) };
        dirs.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries).Select(d => d.Trim().Trim('"')));
        if (extraDirs is not null) dirs.AddRange(extraDirs);
        _dirs = dirs.Where(d => d.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>API-set names (api-ms-win-*, ext-ms-*) are virtual: they are not files on any drive.</summary>
    public static bool IsVirtual(string dll) =>
        dll.StartsWith("api-ms-", StringComparison.OrdinalIgnoreCase) || dll.StartsWith("ext-ms-", StringComparison.OrdinalIgnoreCase);

    public string? Resolve(string dll, string programFolder)
    {
        if (IsVirtual(dll)) return null;
        if (Has(programFolder, dll)) return Path.Combine(programFolder, dll);
        foreach (var d in _dirs) if (Has(d, dll)) return Path.Combine(d, dll);
        return null;
    }

    private bool Has(string folder, string file)
    {
        var names = _listings.GetOrAdd(folder, f =>
        {
            try { return new HashSet<string>(Directory.EnumerateFiles(f).Select(Path.GetFileName)!, StringComparer.OrdinalIgnoreCase); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return new HashSet<string>(); }
        });
        return names.Contains(file);
    }
}
