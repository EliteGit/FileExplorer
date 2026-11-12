namespace FileExplorer.Core.Connections;

public enum ConnectionKind { Shortcut, Link, Imports, Mentions }

public readonly record struct ConnectionEdge(int From, int To, ConnectionKind Kind);

/// <summary>Files and folders as nodes, "A points at / loads / mentions B" as directed edges. Built once, then read-only.</summary>
public sealed class ConnectionGraph
{
    private readonly Dictionary<string, int> _ids = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _paths = new();
    private readonly List<bool> _missing = new();
    private readonly List<List<ConnectionEdge>> _out = new();
    private readonly List<List<ConnectionEdge>> _in = new();
    private readonly HashSet<(int, int, ConnectionKind)> _seen = new();

    public int NodeCount => _paths.Count;
    public int EdgeCount { get; private set; }

    public string PathOf(int id) => _paths[id];
    /// <summary>True when the thing a shortcut or link points at does not exist any more.</summary>
    public bool IsMissing(int id) => _missing[id];
    public IReadOnlyList<ConnectionEdge> Outgoing(int id) => _out[id];
    public IReadOnlyList<ConnectionEdge> Incoming(int id) => _in[id];
    public int Degree(int id) => _out[id].Count + _in[id].Count;
    public IEnumerable<int> Nodes => Enumerable.Range(0, NodeCount);

    public int? Find(string path) => _ids.TryGetValue(Normalize(path), out var id) ? id : null;

    public int Add(string path, bool missing = false)
    {
        var key = Normalize(path);
        if (_ids.TryGetValue(key, out var id)) { if (missing) _missing[id] = true; return id; }
        id = _paths.Count;
        _ids[key] = id; _paths.Add(key); _missing.Add(missing); _out.Add(new List<ConnectionEdge>()); _in.Add(new List<ConnectionEdge>());
        return id;
    }

    /// <summary>Adds an edge once (the same pair and kind is not repeated). Returns false for a duplicate or a self-reference.</summary>
    public bool AddEdge(int from, int to, ConnectionKind kind)
    {
        if (from == to || !_seen.Add((from, to, kind))) return false;
        var e = new ConnectionEdge(from, to, kind);
        _out[from].Add(e); _in[to].Add(e); EdgeCount++;
        return true;
    }

    public int Count(ConnectionKind kind) => _out.Sum(l => l.Count(e => e.Kind == kind));

    /// <summary>Drive letter of a path, or null for network (UNC) paths.</summary>
    public static char? DriveOf(string path) => path.Length >= 2 && path[1] == ':' ? char.ToUpperInvariant(path[0]) : null;

    public bool CrossesDrives(ConnectionEdge e)
    {
        var a = DriveOf(_paths[e.From]); var b = DriveOf(_paths[e.To]);
        return a != b;
    }

    private static string Normalize(string path) => path.Replace('/', '\\').TrimEnd('\\') is { Length: > 0 } p ? (p.Length == 2 && p[1] == ':' ? p + "\\" : p) : path;
}
