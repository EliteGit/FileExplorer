using System.IO.Hashing;
using System.Runtime.InteropServices;
using System.Text;
using FileExplorer.Core.Common;
using FileExplorer.Core.Fs;

namespace FileExplorer.Core.Index;

/// <summary>Binary cache of one volume's index plus its USN cursor. A damaged cache is an error, never silently ignored.</summary>
public sealed class IndexCache
{
    private const uint Magic = 0x58444946;   // "FIDX"
    private const int Version = 1;
    private readonly string _folder;

    public IndexCache(string? folder = null) =>
        _folder = folder ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FileExplorer", "index");

    public string FileFor(string volume) => Path.Combine(_folder, PathUtil.Normalize(volume).TrimEnd('\\', ':') + ".idx");

    public bool Exists(string volume) => File.Exists(FileFor(volume));

    public Result<Unit> Save(FileIndex index)
    {
        try
        {
            Directory.CreateDirectory(_folder);
            var tmp = FileFor(index.Volume) + ".tmp";
            using (var fs = File.Create(tmp))
            using (var w = new BinaryWriter(fs, Encoding.UTF8))
                Write(w, index);
            File.Move(tmp, FileFor(index.Volume), overwrite: true);
            return Result.Ok();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { return Result.Err(new AppError($"Cannot write the index cache: {e.Message}", 0, FileFor(index.Volume))); }
    }

    public Result<FileIndex> Load(string volume)
    {
        var file = FileFor(volume);
        try
        {
            var bytes = File.ReadAllBytes(file);
            return Read(bytes, volume, file);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { return new AppError($"Cannot read the index cache: {e.Message}", 0, file); }
    }

    private static void Write(BinaryWriter w, FileIndex idx)
    {
        using var _ = idx.ReadLock();
        var raw = idx.Raw();
        var body = new MemoryStream();
        using (var bw = new BinaryWriter(body, Encoding.UTF8, leaveOpen: true))
        {
            bw.Write((int)idx.Mode); bw.Write(idx.HasMetadata); bw.Write(idx.BuiltUtc.ToBinary());
            bw.Write(idx.JournalId); bw.Write(idx.NextUsn);
            bw.Write(idx.Count); bw.Write(raw.PoolLen);
            WriteArray(bw, raw.Pool.AsSpan(0, raw.PoolLen));
            WriteArray(bw, raw.Start.AsSpan(0, idx.Count)); WriteArray(bw, raw.Len.AsSpan(0, idx.Count));
            WriteArray(bw, raw.Parent.AsSpan(0, idx.Count)); WriteArray(bw, raw.Frn.AsSpan(0, idx.Count));
            WriteArray(bw, raw.Size.AsSpan(0, idx.Count)); WriteArray(bw, raw.Mtime.AsSpan(0, idx.Count));
            WriteArray(bw, raw.Attrs.AsSpan(0, idx.Count));
        }
        var data = body.ToArray();
        w.Write(Magic); w.Write(Version); w.Write(data.Length); w.Write(Crc32.HashToUInt32(data));
        w.Write(data);
    }

    private static Result<FileIndex> Read(byte[] file, string volume, string path)
    {
        if (file.Length < 16) return new AppError("The index cache is truncated; it must be rebuilt.", 0, path);
        using var head = new BinaryReader(new MemoryStream(file));
        if (head.ReadUInt32() != Magic) return new AppError("The index cache is not a valid index file; it must be rebuilt.", 0, path);
        if (head.ReadInt32() != Version) return new AppError("The index cache was written by a different version; it must be rebuilt.", 0, path);
        int length = head.ReadInt32(); uint crc = head.ReadUInt32();
        if (file.Length - 16 != length) return new AppError("The index cache is truncated; it must be rebuilt.", 0, path);
        var data = file.AsSpan(16, length);
        if (Crc32.HashToUInt32(data) != crc) return new AppError("The index cache failed its checksum; it must be rebuilt.", 0, path);
        using var r = new BinaryReader(new MemoryStream(data.ToArray()));
        var mode = (IndexMode)r.ReadInt32(); bool meta = r.ReadBoolean(); var built = DateTime.FromBinary(r.ReadInt64());
        long journal = r.ReadInt64(), next = r.ReadInt64();
        int count = r.ReadInt32(), poolLen = r.ReadInt32();
        var pool = ReadArray<char>(r, poolLen);
        var idx = FileIndex.FromRaw(volume, mode, meta, count, pool, poolLen,
            ReadArray<int>(r, count), ReadArray<int>(r, count), ReadArray<int>(r, count), ReadArray<ulong>(r, count),
            ReadArray<long>(r, count), ReadArray<long>(r, count), ReadArray<uint>(r, count));
        idx.BuiltUtc = built; idx.JournalId = journal; idx.NextUsn = next;
        return Result<FileIndex>.Ok(idx);
    }

    private static void WriteArray<T>(BinaryWriter w, ReadOnlySpan<T> items) where T : unmanaged =>
        w.Write(MemoryMarshal.AsBytes(items));

    private static T[] ReadArray<T>(BinaryReader r, int count) where T : unmanaged
    {
        var arr = new T[Math.Max(count, 1)];
        var bytes = MemoryMarshal.AsBytes(arr.AsSpan(0, count));
        int read = 0;
        while (read < bytes.Length) { int n = r.Read(bytes[read..]); if (n == 0) throw new EndOfStreamException(); read += n; }
        return arr;
    }
}
