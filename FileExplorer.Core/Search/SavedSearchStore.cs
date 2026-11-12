using System.Text.Json;
using FileExplorer.Core.Common;

namespace FileExplorer.Core.Search;

/// <summary>Named queries kept in a plain JSON file.</summary>
public sealed class SavedSearchStore
{
    private readonly string _file;

    public SavedSearchStore(string? folder = null)
    {
        var dir = folder ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FileExplorer");
        _file = Path.Combine(dir, "saved-searches.json");
    }

    public Result<Dictionary<string, string>> Load()
    {
        if (!File.Exists(_file)) return Result<Dictionary<string, string>>.Ok(new());
        try { return Result<Dictionary<string, string>>.Ok(JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_file)) ?? new()); }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        { return new AppError($"Cannot read saved searches: {e.Message}", 0, _file); }
    }

    public Result<Unit> Save(string name, string query)
    {
        var all = Load();
        if (!all.IsOk) return Result.Err(all.Error);
        all.Value[name] = query;
        return Write(all.Value);
    }

    public Result<Unit> Delete(string name)
    {
        var all = Load();
        if (!all.IsOk) return Result.Err(all.Error);
        all.Value.Remove(name);
        return Write(all.Value);
    }

    private Result<Unit> Write(Dictionary<string, string> data)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            File.WriteAllText(_file, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
            return Result.Ok();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { return Result.Err(new AppError($"Cannot save searches: {e.Message}", 0, _file)); }
    }
}
