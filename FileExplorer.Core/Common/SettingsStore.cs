using System.Text.Json;

namespace FileExplorer.Core.Common;

public sealed record AppSettings(bool ShowHidden = false);

/// <summary>User settings as JSON in %LocalAppData%\FileExplorer\settings.json.</summary>
public sealed class SettingsStore
{
    private readonly string _file;

    public SettingsStore(string? folder = null)
    {
        var dir = folder ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FileExplorer");
        _file = Path.Combine(dir, "settings.json");
    }

    /// <summary>Missing file = defaults. A damaged file is an error the caller reports (defaults are then used knowingly).</summary>
    public Result<AppSettings> Load()
    {
        if (!File.Exists(_file)) return Result<AppSettings>.Ok(new AppSettings());
        try { return Result<AppSettings>.Ok(JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_file)) ?? new AppSettings()); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        { return new AppError($"Cannot read settings: {e.Message}", 0, _file); }
    }

    public Result<Unit> Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            File.WriteAllText(_file, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
            return Result.Ok();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { return Result.Err(new AppError($"Cannot save settings: {e.Message}", 0, _file)); }
    }
}
