using System.Text;
using FileExplorer.Core.Common;
using FileExplorer.Core.Native;
using Microsoft.Win32.SafeHandles;
using static FileExplorer.Core.Native.NativeMethods;

namespace FileExplorer.Core.Tags;

/// <summary>Stable identity of a file or folder: volume serial + file reference number. Survives renames and moves on one volume.</summary>
public readonly record struct FileKey(uint VolumeSerial, ulong Frn);

public static class FileIdentity
{
    private const uint ShareAll = FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE;

    public static Result<FileKey> Get(string path)
    {
        using var h = CreateFileW(PathUtil.ToLongPath(path), 0, ShareAll, IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
        if (h.IsInvalid) return Win32Error.FromLastError(path, "Cannot identify the file");
        if (!GetFileInformationByHandle(h, out var info)) return Win32Error.FromLastError(path, "Cannot identify the file");
        return Result<FileKey>.Ok(new FileKey(info.dwVolumeSerialNumber, ((ulong)info.nFileIndexHigh << 32) | info.nFileIndexLow));
    }

    /// <summary>Where the file with this identity is now (null when it no longer exists on a ready fixed/removable drive).</summary>
    public static Result<string?> CurrentPath(FileKey key)
    {
        foreach (var d in DriveInfo.GetDrives().Where(d => d.IsReady))
        {
            using var root = CreateFileW(d.Name, 0, ShareAll, IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
            if (root.IsInvalid || !GetFileInformationByHandle(root, out var ri) || ri.dwVolumeSerialNumber != key.VolumeSerial) continue;
            return Result<string?>.Ok(PathOnVolume(root, key.Frn));
        }
        return Result<string?>.Ok(null);
    }

    private static string? PathOnVolume(SafeFileHandle volumeHint, ulong frn)
    {
        var desc = new FILE_ID_DESCRIPTOR { dwSize = 24, Type = 0, FileId = (long)frn };
        using var h = OpenFileById(volumeHint, ref desc, 0, ShareAll, IntPtr.Zero, FILE_FLAG_BACKUP_SEMANTICS);
        if (h.IsInvalid) return null;
        var sb = new StringBuilder(1024);
        uint n = GetFinalPathNameByHandleW(h, sb, (uint)sb.Capacity, 0);
        return n == 0 || n >= sb.Capacity ? null : PathUtil.Normalize(sb.ToString());
    }
}
