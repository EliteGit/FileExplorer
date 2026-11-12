using System.Buffers.Binary;
using FileExplorer.Core.Common;
using FileExplorer.Core.Fs;
using FileExplorer.Core.Native;
using Microsoft.Win32.SafeHandles;

namespace FileExplorer.Core.Index;

/// <summary>Reads the USN journal from a cursor and applies the changes to a FileIndex. Mft mode only.</summary>
public sealed class UsnWatcher : IDisposable
{
    private readonly FileIndex _index;
    private readonly SafeFileHandle _handle;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    private UsnWatcher(FileIndex index, SafeFileHandle handle) { _index = index; _handle = handle; }

    public event Action<AppError>? Failed;
    public event Action? Changed;

    public static Result<UsnWatcher> Create(FileIndex index)
    {
        if (index.Mode != IndexMode.Mft) return new AppError("Live updates through the USN journal need an MFT index.");
        var h = VolumeHandle.Open(index.Volume);
        return h.IsOk ? Result<UsnWatcher>.Ok(new UsnWatcher(index, h.Value)) : Result<UsnWatcher>.Err(h.Error);
    }

    /// <summary>Applies everything since the cursor once (used at start-up after loading the cache).</summary>
    public Result<int> CatchUp()
    {
        var info = VolumeHandle.QueryJournal(_handle);
        if (!info.IsOk) return Result<int>.Err(info.Error);
        if (info.Value.JournalId != _index.JournalId)
            return new AppError("The USN journal was recreated since the cache was written; the index must be rebuilt.");
        int applied = 0;
        while (_index.NextUsn < info.Value.NextUsn)
        {
            long before = _index.NextUsn;
            var step = ReadOnce(timeoutSeconds: 0);
            if (!step.IsOk) return Result<int>.Err(step.Error);
            applied += step.Value;
            if (_index.NextUsn == before) break;   // no progress: nothing more to read
        }
        return Result<int>.Ok(applied);
    }

    public void Start() => _loop = Task.Run(Loop);

    private void Loop()
    {
        while (!_cts.IsCancellationRequested)
        {
            var r = ReadOnce(timeoutSeconds: 1);
            if (!r.IsOk) { Failed?.Invoke(r.Error); return; }
            if (r.Value > 0) Changed?.Invoke();
        }
    }

    /// <summary>One FSCTL_READ_USN_JOURNAL call. Returns the number of changes applied.</summary>
    private Result<int> ReadOnce(int timeoutSeconds)
    {
        var input = new byte[40];
        BinaryPrimitives.WriteInt64LittleEndian(input, _index.NextUsn);
        BinaryPrimitives.WriteUInt32LittleEndian(input.AsSpan(8), 0xFFFFFFFF);                     // all reasons
        BinaryPrimitives.WriteUInt32LittleEndian(input.AsSpan(12), 0);                             // not only on close
        BinaryPrimitives.WriteUInt64LittleEndian(input.AsSpan(16), (ulong)timeoutSeconds);
        BinaryPrimitives.WriteUInt64LittleEndian(input.AsSpan(24), timeoutSeconds == 0 ? 0UL : 1UL);
        BinaryPrimitives.WriteUInt64LittleEndian(input.AsSpan(32), (ulong)_index.JournalId);
        var output = new byte[1 << 16];
        if (!NativeMethods.DeviceIoControl(_handle, NativeMethods.FSCTL_READ_USN_JOURNAL, input, input.Length, output, output.Length, out int got, IntPtr.Zero))
            return Win32Error.FromLastError(null, "Reading the USN journal failed");
        if (got < 8) return Result<int>.Ok(0);
        long next = BinaryPrimitives.ReadInt64LittleEndian(output);
        var parsed = UsnRecordParser.Parse(output.AsSpan(8, got - 8));
        if (!parsed.IsOk) return Result<int>.Err(parsed.Error);
        int n = 0;
        foreach (var change in UsnChangeTranslator.ToChanges(parsed.Value))
        {
            _index.Apply(change); n++;
            if (change.Kind != IndexChangeKind.Deleted && _index.HasMetadata) ReadMetadata(change.Frn);
        }
        _index.NextUsn = next;
        return Result<int>.Ok(n);
    }

    /// <summary>A file created or renamed while the index is live: read its size and date so size:/dm: stay correct.</summary>
    private void ReadMetadata(ulong frn)
    {
        int row = _index.RowOfFrn(frn);
        if (row < 0) return;
        string path; bool isDir;
        using (_index.ReadLock()) { path = _index.GetFullPath(row); isDir = _index.IsDir(row); }
        try
        {
            if (isDir) return;
            var fi = new FileInfo(path);
            if (!fi.Exists) return;
            _index.ApplyMetadata(new[] { (row, fi.Length, fi.LastWriteTimeUtc.ToFileTimeUtc()) });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* gone or locked: the next rebuild will have it */ }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _loop?.Wait(TimeSpan.FromSeconds(3)); } catch (AggregateException) { }
        _handle.Dispose();
        _cts.Dispose();
    }
}
