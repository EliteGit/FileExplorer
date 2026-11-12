using System.Globalization;
using System.Text.RegularExpressions;
using FileExplorer.Core.Common;

namespace FileExplorer.Core.Rename;

public readonly record struct NameParts(string Stem, string Ext);

public sealed record RenameContext(int Index, DateTime Modified);

public interface IRenameRule
{
    NameParts Apply(NameParts name, RenameContext context);
}

public sealed record FindReplaceRule(string Find, string Replace, bool CaseSensitive = false) : IRenameRule
{
    public NameParts Apply(NameParts n, RenameContext c) =>
        Find.Length == 0 ? n : n with { Stem = n.Stem.Replace(Find, Replace, CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase) };
}

public sealed class RegexRule : IRenameRule
{
    private readonly Regex _regex;
    private readonly string _replacement;
    private RegexRule(Regex regex, string replacement) { _regex = regex; _replacement = replacement; }

    public static Result<RegexRule> Create(string pattern, string replacement, bool ignoreCase = true)
    {
        try { return Result<RegexRule>.Ok(new RegexRule(new Regex(pattern, ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None, TimeSpan.FromMilliseconds(200)), replacement)); }
        catch (ArgumentException e) { return new AppError($"Invalid regular expression: {e.Message}"); }
    }

    public NameParts Apply(NameParts n, RenameContext c) => n with { Stem = _regex.Replace(n.Stem, _replacement) };
}

public enum CounterMode { Append, Prepend, ReplaceName }

/// <summary>Numbers files in order. Template uses {n} for the counter, e.g. "Holiday_{n}".</summary>
public sealed record CounterRule(string Template = "{n}", int Start = 1, int Step = 1, int Pad = 2, CounterMode Mode = CounterMode.ReplaceName) : IRenameRule
{
    public NameParts Apply(NameParts n, RenameContext c)
    {
        string number = (Start + c.Index * Step).ToString(CultureInfo.InvariantCulture).PadLeft(Pad, '0');
        string text = Template.Replace("{n}", number);
        return Mode switch
        {
            CounterMode.Append => n with { Stem = n.Stem + text },
            CounterMode.Prepend => n with { Stem = text + n.Stem },
            _ => n with { Stem = text },
        };
    }
}

public enum CaseMode { Upper, Lower, Title, Sentence }

public sealed record CaseRule(CaseMode Mode) : IRenameRule
{
    public NameParts Apply(NameParts n, RenameContext c) => n with { Stem = Mode switch
    {
        CaseMode.Upper => n.Stem.ToUpperInvariant(),
        CaseMode.Lower => n.Stem.ToLowerInvariant(),
        CaseMode.Title => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(n.Stem.ToLowerInvariant()),
        _ => n.Stem.Length == 0 ? n.Stem : char.ToUpperInvariant(n.Stem[0]) + n.Stem[1..].ToLowerInvariant(),
    } };
}

/// <summary>Inserts the file's modified date, formatted with a .NET date format (e.g. yyyy-MM-dd).</summary>
public sealed record DateRule(string Format = "yyyy-MM-dd", bool Prefix = true, string Separator = "_") : IRenameRule
{
    public NameParts Apply(NameParts n, RenameContext c)
    {
        string date = c.Modified.ToString(Format, CultureInfo.InvariantCulture);
        return n with { Stem = Prefix ? date + Separator + n.Stem : n.Stem + Separator + date };
    }
}

public enum ExtensionMode { Lower, Upper, Set, Remove }

public sealed record ExtensionRule(ExtensionMode Mode, string Value = "") : IRenameRule
{
    public NameParts Apply(NameParts n, RenameContext c) => n with { Ext = Mode switch
    {
        ExtensionMode.Lower => n.Ext.ToLowerInvariant(),
        ExtensionMode.Upper => n.Ext.ToUpperInvariant(),
        ExtensionMode.Set => Value.TrimStart('.'),
        _ => "",
    } };
}

public sealed record RenameItem(string Path, bool IsDir, DateTime Modified);

public sealed record RenameEntry(RenameItem Item, string OldName, string NewName, string? Conflict)
{
    public bool Changed => !string.Equals(OldName, NewName, StringComparison.Ordinal);
    public string Folder => PathUtil.Parent(Item.Path) ?? "";
    public string NewPath => PathUtil.Combine(Folder, NewName);
}

/// <summary>Rule pipeline with a preview that detects conflicts, and an apply step that renames in two phases (so swaps work) as one undoable batch.</summary>
public sealed class BulkRenamer
{
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
        { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" };

    public IReadOnlyList<RenameEntry> Preview(IReadOnlyList<RenameItem> items, IReadOnlyList<IRenameRule> rules)
    {
        var entries = new List<RenameEntry>();
        for (int i = 0; i < items.Count; i++)
        {
            string old = PathUtil.FileName(items[i].Path);
            var parts = Split(old, items[i].IsDir);
            var ctx = new RenameContext(i, items[i].Modified);
            foreach (var rule in rules) parts = rule.Apply(parts, ctx);
            entries.Add(new RenameEntry(items[i], old, Join(parts), null));
        }
        return FindConflicts(entries);
    }

    private static NameParts Split(string name, bool isDir)
    {
        if (isDir) return new NameParts(name, "");
        int dot = name.LastIndexOf('.');
        return dot <= 0 ? new NameParts(name, "") : new NameParts(name[..dot], name[(dot + 1)..]);
    }

    private static string Join(NameParts p) => p.Ext.Length == 0 ? p.Stem : p.Stem + "." + p.Ext;

    private static List<RenameEntry> FindConflicts(List<RenameEntry> entries)
    {
        var batchPaths = entries.Select(e => e.Item.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var byTarget = entries.Where(e => e.Changed).GroupBy(e => e.NewPath, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).SelectMany(g => g).ToHashSet();
        var result = new List<RenameEntry>(entries.Count);
        foreach (var e in entries)
        {
            string? problem = !e.Changed ? null : NameProblem(e.NewName)
                ?? (byTarget.Contains(e) ? $"Two items would both be named '{e.NewName}'." : null)
                ?? TargetExists(e, batchPaths, entries);
            result.Add(e with { Conflict = problem });
        }
        return result;
    }

    private static string? NameProblem(string name)
    {
        if (name.Length == 0) return "The new name is empty.";
        if (name.Length > 255) return "The new name is longer than 255 characters.";
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return "The new name contains characters Windows does not allow.";
        if (name.EndsWith('.') || name.EndsWith(' ')) return "Names cannot end with a dot or a space.";
        if (Reserved.Contains(name.Split('.')[0])) return $"'{name.Split('.')[0]}' is a reserved Windows name.";
        return null;
    }

    private static string? TargetExists(RenameEntry e, HashSet<string> batchPaths, List<RenameEntry> all)
    {
        var target = e.NewPath;
        if (!File.Exists(target) && !Directory.Exists(target)) return null;
        if (!batchPaths.Contains(target)) return $"'{e.NewName}' already exists in this folder.";
        var other = all.First(x => string.Equals(x.Item.Path, target, StringComparison.OrdinalIgnoreCase));
        return other.Changed ? null : $"'{e.NewName}' already exists in this folder.";   // swapped or moved out of the way by the same batch
    }

    // ---- apply ----
    public Result<IUndoable> Apply(IReadOnlyList<RenameEntry> plan)
    {
        var first = plan.FirstOrDefault(e => e.Conflict is not null);
        if (first is not null) return new AppError($"Fix the conflicts first: {first.Conflict}", 0, first.Item.Path);
        var changes = plan.Where(e => e.Changed).ToList();
        if (changes.Count == 0) return new AppError("Nothing to rename: no name changes.");

        var steps = changes.Select(e => new Step(e.Item.Path, e.NewPath, TempName(e.Item.Path), e.Item.IsDir)).ToList();
        var r = Run(steps);
        return r.IsOk
            ? Result<IUndoable>.Ok(new DelegateUndoable($"Rename {steps.Count} item(s)", () => Run(steps.Select(s => new Step(s.To, s.From, TempName(s.To), s.IsDir)).ToList())))
            : Result<IUndoable>.Err(r.Error);
    }

    private sealed record Step(string From, string To, string Temp, bool IsDir);

    private static string TempName(string path) => PathUtil.Combine(PathUtil.Parent(path)!, "~fe-" + Guid.NewGuid().ToString("N"));

    /// <summary>Phase 1: everything to a temporary name. Phase 2: temporary to final. A failure rolls back what was done.</summary>
    private static Result<Unit> Run(List<Step> steps)
    {
        var done = new List<(string From, string To, bool IsDir)>();
        foreach (var s in steps)
            if (!Move(s.From, s.Temp, s.IsDir, done)) return Rollback(done, s.From);
        foreach (var s in steps)
            if (!Move(s.Temp, s.To, s.IsDir, done)) return Rollback(done, s.To);
        return Result.Ok();
    }

    private static bool Move(string from, string to, bool isDir, List<(string, string, bool)> done)
    {
        try
        {
            if (isDir) Directory.Move(from, to); else File.Move(from, to);
            done.Add((from, to, isDir));
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { LastError = e.Message; return false; }
    }

    [ThreadStatic] private static string? LastError;

    private static Result<Unit> Rollback(List<(string From, string To, bool IsDir)> done, string failedPath)
    {
        string cause = LastError ?? "unknown error";
        var stuck = new List<string>();
        for (int i = done.Count - 1; i >= 0; i--)
        {
            try { if (done[i].IsDir) Directory.Move(done[i].To, done[i].From); else File.Move(done[i].To, done[i].From); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { stuck.Add(done[i].To); }
        }
        string tail = stuck.Count == 0 ? "Everything was put back." : $"{stuck.Count} item(s) could not be put back: {string.Join(", ", stuck.Take(3))}";
        return Result.Err(new AppError($"Rename failed ({cause}). {tail}", 0, failedPath));
    }
}
