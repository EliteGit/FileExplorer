using System.Text.Json;
using FileExplorer.Core.Common;

namespace FileExplorer.App.Shell;

/// <summary>Persists window layout and tabs to %LocalAppData%\FileExplorer\session.json.</summary>
public sealed class SessionStore
{
    private readonly string _file;

    public SessionStore(string? folder = null)
    {
        var dir = folder ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FileExplorer");
        _file = Path.Combine(dir, "session.json");
    }

    public Result<Unit> Save(SessionSnapshot snapshot)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            File.WriteAllText(_file, JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true }));
            return Result.Ok();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { return Result.Err(new AppError($"Cannot save session: {e.Message}", 0, _file)); }
    }

    /// <summary>Ok(null) = no saved session yet. A damaged file is an error the caller reports.</summary>
    public Result<SessionSnapshot?> Load()
    {
        if (!File.Exists(_file)) return Result<SessionSnapshot?>.Ok(null);
        try { return Result<SessionSnapshot?>.Ok(JsonSerializer.Deserialize<SessionSnapshot>(File.ReadAllText(_file))); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        { return new AppError($"Cannot read saved session: {e.Message}", 0, _file); }
    }
}
