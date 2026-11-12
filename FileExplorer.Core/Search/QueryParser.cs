using System.Globalization;
using System.Text.RegularExpressions;
using FileExplorer.Core.Common;

namespace FileExplorer.Core.Search;

/// <summary>A parsed query: every predicate must match (AND).</summary>
public sealed record Query(string Text, IReadOnlyList<IRowPredicate> Predicates)
{
    public bool IsEmpty => Predicates.Count == 0;
}

/// <summary>Lets a feature add a <c>prefix:value</c> term (e.g. tag:) without the parser knowing about it.</summary>
public interface IQueryTermProvider
{
    string Prefix { get; }
    Result<IRowPredicate> Create(string value);
}

public sealed class QueryTermProviders
{
    private readonly List<IQueryTermProvider> _providers = new();
    public IReadOnlyList<IQueryTermProvider> All => _providers;
    public void Add(IQueryTermProvider p) => _providers.Add(p);
}

/// <summary>
/// Syntax: words (substring), wildcards (* ?), "quoted phrases", !negation, ext:a;b, size:&gt;100mb, size:1kb..1mb,
/// dm:today|yesterday|thisweek|thismonth|thisyear|lastNdays|&gt;2025-01-01|2025-01-01..2025-02-01, path:text, regex:pattern,
/// file: / folder:, case: (case-sensitive). Parse errors are returned, never ignored.
/// </summary>
public sealed class QueryParser
{
    private readonly QueryTermProviders? _providers;
    private readonly Func<DateTime> _now;

    public QueryParser(QueryTermProviders? providers = null, Func<DateTime>? now = null)
    {
        _providers = providers; _now = now ?? (() => DateTime.Now);
    }

    public Result<Query> Parse(string text)
    {
        var tokens = Tokenize(text);
        if (!tokens.IsOk) return Result<Query>.Err(tokens.Error);
        bool cs = tokens.Value.Any(t => t.Key == "case");
        var preds = new List<IRowPredicate>();
        foreach (var t in tokens.Value.Where(t => t.Key != "case"))
        {
            var p = BuildTerm(t, cs);
            if (!p.IsOk) return Result<Query>.Err(p.Error);
            preds.Add(t.Negated ? new Not(p.Value) : p.Value);
        }
        return Result<Query>.Ok(new Query(text, preds));
    }

    private readonly record struct Token(string? Key, string Value, bool Negated, bool Quoted);

    private Result<List<Token>> Tokenize(string text)
    {
        var list = new List<Token>();
        int i = 0;
        while (i < text.Length)
        {
            while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
            if (i >= text.Length) break;
            bool neg = text[i] == '!' && i + 1 < text.Length; if (neg) i++;
            int start = i; string? key = null; bool quoted = false;
            var sb = new System.Text.StringBuilder();
            while (i < text.Length && !char.IsWhiteSpace(text[i]))
            {
                if (text[i] == '"')
                {
                    int end = text.IndexOf('"', i + 1);
                    if (end < 0) return new AppError($"Unclosed quote starting at position {i + 1}.");
                    sb.Append(text, i + 1, end - i - 1); quoted = true; i = end + 1;
                }
                else if (text[i] == ':' && key is null && !quoted && sb.Length > 0 && IsKey(sb.ToString())) { key = sb.ToString().ToLowerInvariant(); sb.Clear(); i++; }
                else sb.Append(text[i++]);
            }
            list.Add(new Token(key, sb.ToString(), neg, quoted));
        }
        return Result<List<Token>>.Ok(list);
    }

    private bool IsKey(string s) =>
        s.Length > 0 && s.All(char.IsLetter) &&
        (s.ToLowerInvariant() is "ext" or "size" or "dm" or "path" or "regex" or "case" or "file" or "folder"
         || (_providers?.All.Any(p => string.Equals(p.Prefix, s, StringComparison.OrdinalIgnoreCase)) ?? false));

    private Result<IRowPredicate> BuildTerm(Token t, bool cs)
    {
        if (t.Key is null) return Result<IRowPredicate>.Ok(NameTerm(t.Value, cs));
        switch (t.Key)
        {
            case "ext": return t.Value.Length == 0 ? Err("ext: needs a value, e.g. ext:psd;png") : Result<IRowPredicate>.Ok(new ExtensionIn(t.Value.Split(';', ',')));
            case "path": return t.Value.Length == 0 ? Err("path: needs a value") : Result<IRowPredicate>.Ok(new PathContains(t.Value, cs));
            case "file": return Result<IRowPredicate>.Ok(new KindIs(false));
            case "folder": return Result<IRowPredicate>.Ok(new KindIs(true));
            case "regex": return BuildRegex(t.Value, cs);
            case "size": return ParseSize(t.Value);
            case "dm": return ParseDate(t.Value);
        }
        var provider = _providers?.All.FirstOrDefault(p => string.Equals(p.Prefix, t.Key, StringComparison.OrdinalIgnoreCase));
        return provider is null ? Err($"Unknown term '{t.Key}:'.") : provider.Create(t.Value);
    }

    private static IRowPredicate NameTerm(string value, bool cs) =>
        value.IndexOfAny(new[] { '*', '?' }) >= 0 ? new NameGlob(value, cs) : new NameContains(value, cs);

    private static Result<IRowPredicate> BuildRegex(string pattern, bool cs)
    {
        try { return Result<IRowPredicate>.Ok(new NameRegex(new Regex(pattern, (cs ? RegexOptions.None : RegexOptions.IgnoreCase) | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200)))); }
        catch (ArgumentException e) { return Err($"Invalid regular expression: {e.Message}"); }
    }

    private static Result<IRowPredicate> Err(string message) => Result<IRowPredicate>.Err(new AppError(message));

    // ---- size ----
    private static readonly Regex SizeRx = new(@"^(?<op>>=|<=|>|<|=)?(?<n>\d+(?:\.\d+)?)\s*(?<u>[kmgt]?b?)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static Result<IRowPredicate> ParseSize(string v)
    {
        int dots = v.IndexOf("..", StringComparison.Ordinal);
        if (dots >= 0)
        {
            var a = ParseBytes(v[..dots]); var b = ParseBytes(v[(dots + 2)..]);
            if (a is null || b is null) return Err($"Cannot read size range '{v}'. Example: size:1kb..5mb");
            return Result<IRowPredicate>.Ok(new SizeRange(a.Value, b.Value));
        }
        var m = SizeRx.Match(v.Trim());
        if (!m.Success) return Err($"Cannot read size '{v}'. Examples: size:>100mb  size:<=5kb  size:1gb");
        long n = ParseBytes(m.Groups["n"].Value + m.Groups["u"].Value)!.Value;
        return Result<IRowPredicate>.Ok(m.Groups["op"].Value switch
        {
            ">" => new SizeRange(n + 1, long.MaxValue),
            ">=" => new SizeRange(n, long.MaxValue),
            "<" => new SizeRange(0, n - 1),
            "<=" => new SizeRange(0, n),
            _ => new SizeRange(n, n),
        });
    }

    private static long? ParseBytes(string s)
    {
        var m = SizeRx.Match(s.Trim());
        if (!m.Success || m.Groups["op"].Success && m.Groups["op"].Value.Length > 0) return null;
        double n = double.Parse(m.Groups["n"].Value, CultureInfo.InvariantCulture);
        double mult = m.Groups["u"].Value.ToLowerInvariant() switch { "k" or "kb" => 1L << 10, "m" or "mb" => 1L << 20, "g" or "gb" => 1L << 30, "t" or "tb" => 1L << 40, _ => 1 };
        return (long)(n * mult);
    }

    // ---- date ----
    private Result<IRowPredicate> ParseDate(string v)
    {
        var today = _now().Date;
        switch (v.ToLowerInvariant())
        {
            case "today": return Range(today, today.AddDays(1));
            case "yesterday": return Range(today.AddDays(-1), today);
            case "thisweek": return Range(today.AddDays(-(((int)today.DayOfWeek + 6) % 7)), today.AddDays(1));
            case "thismonth": return Range(new DateTime(today.Year, today.Month, 1), today.AddDays(1));
            case "thisyear": return Range(new DateTime(today.Year, 1, 1), today.AddDays(1));
        }
        var last = Regex.Match(v, @"^last(\d+)days?$", RegexOptions.IgnoreCase);
        if (last.Success) return Range(today.AddDays(-int.Parse(last.Groups[1].Value)), today.AddDays(1));
        int dots = v.IndexOf("..", StringComparison.Ordinal);
        if (dots >= 0 && TryDate(v[..dots], out var f) && TryDate(v[(dots + 2)..], out var t)) return Range(f, t.AddDays(1));
        if (v.StartsWith(">=") && TryDate(v[2..], out var ge)) return Range(ge, DateTime.MaxValue);
        if (v.StartsWith('>') && TryDate(v[1..], out var gt)) return Range(gt.AddDays(1), DateTime.MaxValue);
        if (v.StartsWith("<=") && TryDate(v[2..], out var le)) return Range(DateTime.MinValue, le.AddDays(1));
        if (v.StartsWith('<') && TryDate(v[1..], out var lt)) return Range(DateTime.MinValue, lt);
        if (TryDate(v, out var d)) return Range(d, d.AddDays(1));
        return Err($"Cannot read date '{v}'. Examples: dm:today  dm:last7days  dm:>2025-01-01  dm:2025-01-01..2025-02-01");
    }

    private static bool TryDate(string s, out DateTime d) =>
        DateTime.TryParse(s.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out d);

    private static Result<IRowPredicate> Range(DateTime fromLocal, DateTime toLocalExclusive)
    {
        long from = fromLocal == DateTime.MinValue ? 1 : ToFileTime(fromLocal);
        long to = toLocalExclusive == DateTime.MaxValue ? long.MaxValue : ToFileTime(toLocalExclusive) - 1;
        return Result<IRowPredicate>.Ok(new ModifiedRange(from, to));
    }

    private static long ToFileTime(DateTime local) => DateTime.SpecifyKind(local, DateTimeKind.Local).ToFileTimeUtc();
}
