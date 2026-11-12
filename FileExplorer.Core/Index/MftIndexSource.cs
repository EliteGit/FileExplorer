using System.Buffers.Binary;
using FileExplorer.Core.Common;
using FileExplorer.Core.Fs;
using FileExplorer.Core.Native;
using Microsoft.Win32.SafeHandles;

namespace FileExplorer.Core.Index;

/// <summary>Opens a volume for journal access. Failure (not elevated, not NTFS) is returned to the caller, never swallowed.</summary>
public static class VolumeHandle
{
    public static Result<SafeFileHandle> Open(string volumeRoot)
    {
        var letter = PathUtil.Normalize(volumeRoot)[..2];
        var h = NativeMethods.CreateFileW($@"\\.\{letter}", NativeMethods.GENERIC_READ,
            NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE | NativeMethods.FILE_SHARE_DELETE,
            IntPtr.Zero, NativeMethods.OPEN_EXISTING, 0, IntPtr.Zero);
        if (h.IsInvalid)
        {
            var err = Native.Win32Error.FromLastError(letter, "Cannot open the volume for fast indexing");
            return err.Win32Code == NativeMethods.ERROR_ACCESS_DENIED
                ? new AppError("Fast indexing needs administrator rights. Relaunch elevated, or continue with the slower enumerated index.", err.Win32Code, letter)
                : err;
        }
        return Result<SafeFileHandle>.Ok(h);
    }

    public static Result<(long JournalId, long NextUsn)> QueryJournal(SafeFileHandle h)
    {
        var buf = new byte[64];
        if (!NativeMethods.DeviceIoControl(h, NativeMethods.FSCTL_QUERY_USN_JOURNAL, null, 0, buf, buf.Length, out _, IntPtr.Zero))
            return Native.Win32Error.FromLastError(null, "Cannot query the USN journal");
        return Result<(long, long)>.Ok((BinaryPrimitives.ReadInt64LittleEndian(buf), BinaryPrimitives.ReadInt64LittleEndian(buf.AsSpan(16))));
    }
}

/// <summary>Enumerates every NTFS file via FSCTL_ENUM_USN_DATA. Names and attributes only: size and time are not in USN records.</summary>
public sealed class MftIndexSource : IIndexSource
{
    public IndexMode Mode => IndexMode.Mft;

    public Result<FileIndex> Build(string root, IProgress<Progress>? progress, CancellationToken ct)
    {
        var opened = VolumeHandle.Open(root);
        if (!opened.IsOk) return Result<FileIndex>.Err(opened.Error);
        using var h = opened.Value;
        var journal = VolumeHandle.QueryJournal(h);
        if (!journal.IsOk) return Result<FileIndex>.Err(journal.Error);

        var builder = new IndexBuilder(root, IndexMode.Mft, hasMetadata: false);
        var r = EnumerateAll(h, builder, progress, ct);
        if (!r.IsOk) return Result<FileIndex>.Err(r.Error);
        var index = builder.Build();
        index.JournalId = journal.Value.JournalId;
        index.NextUsn = journal.Value.NextUsn;
        return Result<FileIndex>.Ok(index);
    }

    private static Result<Unit> EnumerateAll(SafeFileHandle h, IndexBuilder builder, IProgress<Progress>? progress, CancellationToken ct)
    {
        var input = new byte[24];
        BinaryPrimitives.WriteInt64LittleEndian(input.AsSpan(16), long.MaxValue);   // HighUsn; Low = 0, start FRN = 0
        var output = new byte[1 << 20];
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (!NativeMethods.DeviceIoControl(h, NativeMethods.FSCTL_ENUM_USN_DATA, input, input.Length, output, output.Length, out int got, IntPtr.Zero))
            {
                int code = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
                if (code == NativeMethods.ERROR_HANDLE_EOF) return Result.Ok();
                return Native.Win32Error.ToAppError(code, null, "Reading the file table failed");
            }
            if (got <= 8) return Result.Ok();
            var parsed = UsnRecordParser.Parse(output.AsSpan(8, got - 8));
            if (!parsed.IsOk) return Result.Err(parsed.Error);
            foreach (var r in parsed.Value)
                builder.Add(new RawRecord(r.Frn, r.ParentFrn, r.Name, 0, 0, r.Attributes));
            progress?.Report(new Progress(builder.Count, 0));
            Array.Copy(output, 0, input, 0, 8);   // next start FRN
        }
    }
}
