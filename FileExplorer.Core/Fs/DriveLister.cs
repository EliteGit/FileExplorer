using FileExplorer.Core.Common;
using FileExplorer.Core.Models;

namespace FileExplorer.Core.Fs;

/// <summary>Lists drives for the "This PC" view.</summary>
public static class DriveLister
{
    public static Result<IReadOnlyList<FileEntry>> ListDrives()
    {
        try
        {
            var list = DriveInfo.GetDrives().Select(ToEntry).ToList();
            return Result<IReadOnlyList<FileEntry>>.Ok(list);
        }
        catch (IOException e) { return new AppError(e.Message); }
    }

    private static FileEntry ToEntry(DriveInfo d)
    {
        var root = d.Name;
        if (!d.IsReady) return new FileEntry(root.TrimEnd('\\') + " (not ready)", root, 0, DateTime.MinValue, FileAttributes.Directory, true);
        var label = string.IsNullOrEmpty(d.VolumeLabel) ? "Local Disk" : d.VolumeLabel;
        return new FileEntry($"{label} ({root.TrimEnd('\\')})", root, d.TotalSize, DateTime.MinValue, FileAttributes.Directory, true);
    }
}
