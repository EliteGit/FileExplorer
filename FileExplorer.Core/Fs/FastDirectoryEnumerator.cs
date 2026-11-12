using FileExplorer.Core.Common;
using FileExplorer.Core.Models;
using FileExplorer.Core.Native;

namespace FileExplorer.Core.Fs;

public interface IFileSource
{
    Result<IReadOnlyList<FileEntry>> EnumerateDirectory(string path);
}

/// <summary>The only code that calls FindFirstFileEx.</summary>
public sealed class FastDirectoryEnumerator : IFileSource
{
    public Result<IReadOnlyList<FileEntry>> EnumerateDirectory(string path)
    {
        var dir = PathUtil.Normalize(path);
        var h = NativeMethods.FindFirstFileExW(PathUtil.ToLongPath(PathUtil.Combine(dir, "*")), NativeMethods.FindExInfoBasic,
            out var data, NativeMethods.FindExSearchNameMatch, IntPtr.Zero, NativeMethods.FIND_FIRST_EX_LARGE_FETCH);
        if (h == NativeMethods.InvalidHandle)
            return Win32Error.FromLastError(dir, "Cannot open folder");
        try { return Result<IReadOnlyList<FileEntry>>.Ok(ReadAll(h, data, dir)); }
        finally { NativeMethods.FindClose(h); }
    }

    private static List<FileEntry> ReadAll(IntPtr handle, NativeMethods.WIN32_FIND_DATA first, string dir)
    {
        var list = new List<FileEntry>();
        var data = first;
        do
        {
            if (!IsDotEntry(data.cFileName)) list.Add(ToEntry(data, dir));
        } while (NativeMethods.FindNextFileW(handle, out data));
        return list;
    }

    private static bool IsDotEntry(string name) => name is "." or "..";

    private static FileEntry ToEntry(NativeMethods.WIN32_FIND_DATA d, string dir)
    {
        var attrs = (FileAttributes)d.dwFileAttributes;
        var isDir = (attrs & FileAttributes.Directory) != 0;
        long size = isDir ? 0 : ((long)d.nFileSizeHigh << 32) | d.nFileSizeLow;
        return new FileEntry(d.cFileName, PathUtil.Combine(dir, d.cFileName), size, ToLocal(d.ftLastWriteTime), attrs, isDir);
    }

    private static DateTime ToLocal(System.Runtime.InteropServices.ComTypes.FILETIME ft)
    {
        long t = ((long)(uint)ft.dwHighDateTime << 32) | (uint)ft.dwLowDateTime;
        return t <= 0 ? DateTime.MinValue : DateTime.FromFileTimeUtc(t).ToLocalTime();
    }
}
