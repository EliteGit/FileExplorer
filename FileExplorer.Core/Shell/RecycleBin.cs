using System.Runtime.InteropServices;
using FileExplorer.Core.Common;

namespace FileExplorer.Core.Shell;

/// <summary>Finds items in the Recycle Bin (shell namespace 10) and restores them to where they were deleted from.</summary>
public static class RecycleBin
{
    private const int BitBucket = 10;

    /// <summary>
    /// Restores each original path. Must run on an STA thread (the UI thread). Only items deleted after
    /// <paramref name="deletedAfter"/> are considered, so older bin entries with the same name are left alone.
    /// </summary>
    public static Result<Unit> Restore(IReadOnlyList<string> originalPaths, DateTime deletedAfter)
    {
        try
        {
            var type = Type.GetTypeFromProgID("Shell.Application");
            if (type is null) return Result.Err(new AppError("The Windows shell is not available, so the Recycle Bin cannot be read."));
            dynamic shell = Activator.CreateInstance(type)!;
            dynamic bin = shell.NameSpace(BitBucket);
            var wanted = originalPaths.Select(PathUtil.Normalize).ToList();
            foreach (var path in wanted)
            {
                var item = FindItem(bin, path, deletedAfter);
                if (item is null) return Result.Err(new AppError("Not found in the Recycle Bin (it may have been emptied).", 0, path));
                item.InvokeVerb("undelete");
                if (!File.Exists(path) && !Directory.Exists(path))
                    return Result.Err(new AppError("Windows did not restore the item from the Recycle Bin.", 0, path));
            }
            return Result.Ok();
        }
        catch (Exception e) when (e is COMException or InvalidCastException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            return Result.Err(new AppError($"Cannot read the Recycle Bin: {e.Message}"));
        }
    }

    private static dynamic? FindItem(dynamic bin, string originalPath, DateTime deletedAfter)
    {
        dynamic? best = null;
        DateTime bestTime = DateTime.MinValue;
        foreach (dynamic item in bin.Items())
        {
            if (!Matches(item, originalPath)) continue;
            DateTime deleted = item.ExtendedProperty("System.Recycle.DateDeleted");
            if (deleted.ToLocalTime() < deletedAfter && deleted < deletedAfter) continue;
            if (deleted >= bestTime) { best = item; bestTime = deleted; }
        }
        return best;
    }

    private static bool Matches(dynamic item, string originalPath)
    {
        string from = item.ExtendedProperty("System.Recycle.DeletedFrom") ?? "";
        string name = item.Name;
        string binExt = Path.GetExtension((string)item.Path);   // display name may hide the extension
        var dir = PathUtil.Normalize(from);
        return string.Equals(PathUtil.Combine(dir, name), originalPath, StringComparison.OrdinalIgnoreCase)
            || string.Equals(PathUtil.Combine(dir, name + binExt), originalPath, StringComparison.OrdinalIgnoreCase);
    }
}
