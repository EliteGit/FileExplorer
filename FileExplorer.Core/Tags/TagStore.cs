using FileExplorer.Core.Common;
using Microsoft.Data.Sqlite;

namespace FileExplorer.Core.Tags;

public sealed record TagInfo(string Name, int Files);

/// <summary>Tags, favorites and notes keyed by file identity (volume serial + FRN) with the last known path.</summary>
public interface ITagStore
{
    Result<IReadOnlyList<string>> GetTags(string path);
    Result<Unit> AddTag(string path, string tag);
    Result<Unit> RemoveTag(string path, string tag);
    Result<IReadOnlyList<TagInfo>> AllTags();
    Result<Unit> RenameTag(string oldName, string newName);
    Result<Unit> DeleteTag(string name);
    Result<IReadOnlyList<string>> PathsWithTag(string tag);

    Result<bool> IsFavorite(string path);
    Result<Unit> SetFavorite(string path, bool favorite);
    Result<IReadOnlyList<string>> Favorites();

    Result<string> GetNote(string path);
    Result<Unit> SetNote(string path, string note);

    /// <summary>Looks every stored file up by identity and rewrites paths that changed. Returns how many were repaired.</summary>
    Result<int> RepairPaths();
}

public sealed class TagStore : ITagStore
{
    public const int SchemaVersion = 1;
    private readonly string _connectionString;

    public TagStore(string? databasePath = null)
    {
        var path = databasePath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FileExplorer", "tags.db");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString();
    }

    /// <summary>Creates or upgrades the schema. Call once at start; a failure is reported, never hidden.</summary>
    public Result<Unit> Initialize() => Run(c => { Migrate(c); return Unit.Value; });

    // ---- tags ----
    public Result<IReadOnlyList<string>> GetTags(string path) => WithFile(path, create: false, (c, id) =>
        id is null ? Array.Empty<string>() : (IReadOnlyList<string>)Query(c,
            "SELECT t.name FROM file_tag ft JOIN tag t ON t.id=ft.tag_id WHERE ft.file_id=$f ORDER BY t.name", r => r.GetString(0), ("$f", id)));

    public Result<Unit> AddTag(string path, string tag)
    {
        tag = tag.Trim();
        if (tag.Length == 0) return Result.Err(new AppError("A tag needs a name."));
        return WithFileDo(path, (c, id) =>
        {
            Exec(c, "INSERT OR IGNORE INTO tag(name) VALUES($n)", ("$n", tag));
            Exec(c, "INSERT OR IGNORE INTO file_tag(file_id, tag_id) SELECT $f, id FROM tag WHERE name=$n", ("$f", id!), ("$n", tag));
        });
    }

    public Result<Unit> RemoveTag(string path, string tag) => WithFileDo(path, (c, id) =>
        Exec(c, "DELETE FROM file_tag WHERE file_id=$f AND tag_id=(SELECT id FROM tag WHERE name=$n)", ("$f", id!), ("$n", tag)));

    public Result<IReadOnlyList<TagInfo>> AllTags() => Run(c => (IReadOnlyList<TagInfo>)Query(c,
        "SELECT t.name, COUNT(ft.file_id) FROM tag t LEFT JOIN file_tag ft ON ft.tag_id=t.id GROUP BY t.id ORDER BY t.name",
        r => new TagInfo(r.GetString(0), r.GetInt32(1))));

    public Result<Unit> RenameTag(string oldName, string newName)
    {
        newName = newName.Trim();
        if (newName.Length == 0) return Result.Err(new AppError("A tag needs a name."));
        return Run(c =>
        {
            bool exists = Scalar(c, "SELECT COUNT(*) FROM tag WHERE name=$n", ("$n", newName)) > 0;
            if (!exists) { Exec(c, "UPDATE tag SET name=$n WHERE name=$o", ("$n", newName), ("$o", oldName)); return Unit.Value; }
            // Merge into the existing tag.
            Exec(c, "INSERT OR IGNORE INTO file_tag(file_id, tag_id) SELECT file_id, (SELECT id FROM tag WHERE name=$n) FROM file_tag WHERE tag_id=(SELECT id FROM tag WHERE name=$o)", ("$n", newName), ("$o", oldName));
            Exec(c, "DELETE FROM tag WHERE name=$o", ("$o", oldName));
            return Unit.Value;
        });
    }

    public Result<Unit> DeleteTag(string name) => Run(c => { Exec(c, "DELETE FROM tag WHERE name=$n", ("$n", name)); return Unit.Value; });

    public Result<IReadOnlyList<string>> PathsWithTag(string tag) => Run(c => (IReadOnlyList<string>)Query(c,
        "SELECT f.path FROM file f JOIN file_tag ft ON ft.file_id=f.id JOIN tag t ON t.id=ft.tag_id WHERE t.name=$n ORDER BY f.path", r => r.GetString(0), ("$n", tag)));

    // ---- favorites ----
    public Result<bool> IsFavorite(string path) => WithFile(path, create: false, (c, id) =>
        id is not null && Scalar(c, "SELECT COUNT(*) FROM favorite WHERE file_id=$f", ("$f", id)) > 0);

    public Result<Unit> SetFavorite(string path, bool favorite) => WithFileDo(path, (c, id) =>
    {
        if (favorite) Exec(c, "INSERT OR IGNORE INTO favorite(file_id) VALUES($f)", ("$f", id!));
        else Exec(c, "DELETE FROM favorite WHERE file_id=$f", ("$f", id!));
    });

    public Result<IReadOnlyList<string>> Favorites() => Run(c => (IReadOnlyList<string>)Query(c,
        "SELECT f.path FROM file f JOIN favorite fv ON fv.file_id=f.id ORDER BY f.path", r => r.GetString(0)));

    // ---- notes ----
    public Result<string> GetNote(string path) => WithFile(path, create: false, (c, id) =>
        id is null ? "" : Query(c, "SELECT text FROM note WHERE file_id=$f", r => r.GetString(0), ("$f", id)).FirstOrDefault() ?? "");

    public Result<Unit> SetNote(string path, string note) => WithFileDo(path, (c, id) =>
    {
        if (string.IsNullOrWhiteSpace(note)) Exec(c, "DELETE FROM note WHERE file_id=$f", ("$f", id!));
        else Exec(c, "INSERT INTO note(file_id, text) VALUES($f,$t) ON CONFLICT(file_id) DO UPDATE SET text=$t", ("$f", id!), ("$t", note));
    });

    // ---- path repair ----
    public Result<int> RepairPaths() => Run(c =>
    {
        var rows = Query(c, "SELECT id, volume_serial, frn, path FROM file", r => (Id: r.GetInt64(0), Serial: (uint)r.GetInt64(1), Frn: (ulong)r.GetInt64(2), Path: r.GetString(3)));
        int fixedCount = 0;
        foreach (var row in rows)
        {
            var now = FileIdentity.CurrentPath(new FileKey(row.Serial, row.Frn));
            if (!now.IsOk || now.Value is null || string.Equals(now.Value, row.Path, StringComparison.OrdinalIgnoreCase)) continue;
            Exec(c, "UPDATE file SET path=$p WHERE id=$i", ("$p", now.Value), ("$i", row.Id));
            fixedCount++;
        }
        return fixedCount;
    });

    // ---- plumbing ----
    private Result<T> Run<T>(Func<SqliteConnection, T> body)
    {
        try
        {
            using var c = Open();
            return Result<T>.Ok(body(c));
        }
        catch (SqliteException e) { return new AppError($"Tag database error: {e.Message}"); }
        catch (IOException e) { return new AppError($"Tag database error: {e.Message}"); }
    }

    private Result<Unit> WithFileDo(string path, Action<SqliteConnection, object?> body) =>
        WithFile(path, create: true, (c, id) => { body(c, id); return Unit.Value; });

    /// <summary>Resolves the file's identity, creates/updates its row (repairing a changed path) and runs the body with its row id.</summary>
    private Result<T> WithFile<T>(string path, bool create, Func<SqliteConnection, object?, T> body)
    {
        var key = FileIdentity.Get(path);
        if (!key.IsOk) return Result<T>.Err(key.Error);
        var full = PathUtil.Normalize(path);
        return Run(c =>
        {
            object? id = Query(c, "SELECT id, path FROM file WHERE volume_serial=$s AND frn=$n", r => (r.GetInt64(0), r.GetString(1)),
                ("$s", (long)key.Value.VolumeSerial), ("$n", (long)key.Value.Frn)).Select(x => ((long, string)?)x).FirstOrDefault() is { } row
                ? Repair(c, row, full) : null;
            if (id is null && create)
            {
                Exec(c, "INSERT INTO file(volume_serial, frn, path) VALUES($s,$n,$p)", ("$s", (long)key.Value.VolumeSerial), ("$n", (long)key.Value.Frn), ("$p", full));
                id = Scalar(c, "SELECT last_insert_rowid()");
            }
            return body(c, id);
        });
    }

    private static object Repair(SqliteConnection c, (long Id, string Path) row, string currentPath)
    {
        if (!string.Equals(row.Path, currentPath, StringComparison.OrdinalIgnoreCase))
            Exec(c, "UPDATE file SET path=$p WHERE id=$i", ("$p", currentPath), ("$i", row.Id));
        return row.Id;
    }

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        Exec(c, "PRAGMA foreign_keys=ON");
        Migrate(c);
        return c;
    }

    private static void Migrate(SqliteConnection c)
    {
        Exec(c, "CREATE TABLE IF NOT EXISTS schema_version(version INTEGER NOT NULL)");
        long v = Scalar(c, "SELECT COALESCE(MAX(version),0) FROM schema_version");
        if (v >= SchemaVersion) return;
        using var tx = c.BeginTransaction();
        if (v < 1)
        {
            Exec(c, "CREATE TABLE file(id INTEGER PRIMARY KEY, volume_serial INTEGER NOT NULL, frn INTEGER NOT NULL, path TEXT NOT NULL, UNIQUE(volume_serial, frn))");
            Exec(c, "CREATE TABLE tag(id INTEGER PRIMARY KEY, name TEXT NOT NULL UNIQUE COLLATE NOCASE)");
            Exec(c, "CREATE TABLE file_tag(file_id INTEGER NOT NULL REFERENCES file(id) ON DELETE CASCADE, tag_id INTEGER NOT NULL REFERENCES tag(id) ON DELETE CASCADE, PRIMARY KEY(file_id, tag_id))");
            Exec(c, "CREATE TABLE favorite(file_id INTEGER PRIMARY KEY REFERENCES file(id) ON DELETE CASCADE)");
            Exec(c, "CREATE TABLE note(file_id INTEGER PRIMARY KEY REFERENCES file(id) ON DELETE CASCADE, text TEXT NOT NULL)");
        }
        Exec(c, "DELETE FROM schema_version");
        Exec(c, "INSERT INTO schema_version(version) VALUES($v)", ("$v", (long)SchemaVersion));
        tx.Commit();
    }

    private static void Exec(SqliteConnection c, string sql, params (string, object)[] args)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v);
        cmd.ExecuteNonQuery();
    }

    private static long Scalar(SqliteConnection c, string sql, params (string, object)[] args)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v);
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    private static List<T> Query<T>(SqliteConnection c, string sql, Func<SqliteDataReader, T> read, params (string, object)[] args)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v);
        using var r = cmd.ExecuteReader();
        var list = new List<T>();
        while (r.Read()) list.Add(read(r));
        return list;
    }
}
