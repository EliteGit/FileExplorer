using System.IO.Hashing;
using System.Security.Cryptography;
using FileExplorer.Core.Common;

namespace FileExplorer.Core.Hashing;

public enum HashKind { XxHash64, Sha256 }

/// <summary>Partial and full file hashes. Used by the duplicate finder and the checksum viewer.</summary>
public sealed class FileHasher
{
    public const int PartialBytes = 64 * 1024;
    private const int BufferSize = 1 << 20;

    /// <summary>xxHash64 over the first and last 64 KB (the whole file when it is no bigger than both together).</summary>
    public Result<string> HashPartial(string path)
    {
        try
        {
            using var fs = Open(path);
            var hash = new XxHash64();
            var buf = new byte[PartialBytes];
            if (fs.Length <= 2L * PartialBytes) { Feed(fs, hash, buf, fs.Length); return Hex(hash.GetCurrentHash()); }
            Feed(fs, hash, buf, PartialBytes);
            fs.Seek(-PartialBytes, SeekOrigin.End);
            Feed(fs, hash, buf, PartialBytes);
            return Hex(hash.GetCurrentHash());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return Fail(e, path); }
    }

    public Result<string> HashFull(string path, HashKind kind, IProgress<Progress>? progress = null, CancellationToken ct = default)
    {
        try
        {
            using var fs = Open(path);
            var buf = new byte[BufferSize];
            long done = 0;
            if (kind == HashKind.XxHash64)
            {
                var h = new XxHash64();
                int n;
                while ((n = fs.Read(buf, 0, buf.Length)) > 0) { ct.ThrowIfCancellationRequested(); h.Append(buf.AsSpan(0, n)); progress?.Report(new Progress(done += n, fs.Length, path)); }
                return Hex(h.GetCurrentHash());
            }
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            int read;
            while ((read = fs.Read(buf, 0, buf.Length)) > 0) { ct.ThrowIfCancellationRequested(); sha.AppendData(buf, 0, read); progress?.Report(new Progress(done += read, fs.Length, path)); }
            return Hex(sha.GetHashAndReset());
        }
        catch (OperationCanceledException) { return new AppError("Hashing was cancelled.", 0, path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return Fail(e, path); }
    }

    private static FileStream Open(string path) =>
        new(PathUtil.ToLongPath(path), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan);

    private static void Feed(FileStream fs, XxHash64 hash, byte[] buf, long count)
    {
        long left = count;
        while (left > 0)
        {
            int n = fs.Read(buf, 0, (int)Math.Min(buf.Length, left));
            if (n == 0) break;
            hash.Append(buf.AsSpan(0, n));
            left -= n;
        }
    }

    private static Result<string> Hex(ReadOnlySpan<byte> bytes) => Result<string>.Ok(Convert.ToHexString(bytes).ToLowerInvariant());

    private static AppError Fail(Exception e, string path) => new($"Cannot read the file: {e.Message}", 0, path);
}
