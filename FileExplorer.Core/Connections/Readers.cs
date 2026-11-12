using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;
using FileExplorer.Core.Common;

namespace FileExplorer.Core.Connections;

/// <summary>Reads the target path out of a Windows shortcut (.lnk) without the shell: header, then the LinkInfo block.</summary>
public static class LnkParser
{
    private const uint HasLinkTargetIdList = 0x1, HasLinkInfo = 0x2, VolumeIdAndLocalBasePath = 0x1;

    public static Result<string> ReadTarget(string lnkPath)
    {
        try { return Parse(File.ReadAllBytes(lnkPath)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return new AppError($"Cannot read the shortcut: {e.Message}", 0, lnkPath); }
    }

    /// <summary>Only shortcuts to a local path are resolved; network and special-folder shortcuts report that they have no local target.</summary>
    public static Result<string> Parse(ReadOnlySpan<byte> b)
    {
        if (b.Length < 76 || BinaryPrimitives.ReadUInt32LittleEndian(b) != 0x4C) return new AppError("Not a shortcut file.");
        uint flags = BinaryPrimitives.ReadUInt32LittleEndian(b[0x14..]);
        int pos = 76;
        if ((flags & HasLinkTargetIdList) != 0)
        {
            if (pos + 2 > b.Length) return new AppError("Damaged shortcut.");
            pos += 2 + BinaryPrimitives.ReadUInt16LittleEndian(b[pos..]);
        }
        if ((flags & HasLinkInfo) == 0 || pos + 28 > b.Length) return new AppError("The shortcut has no local target path.");
        var info = b[pos..];
        uint size = BinaryPrimitives.ReadUInt32LittleEndian(info);
        uint headerSize = BinaryPrimitives.ReadUInt32LittleEndian(info[4..]);
        uint infoFlags = BinaryPrimitives.ReadUInt32LittleEndian(info[8..]);
        if (size > info.Length || (infoFlags & VolumeIdAndLocalBasePath) == 0) return new AppError("The shortcut points at a network or special location.");
        uint baseOffset = BinaryPrimitives.ReadUInt32LittleEndian(info[16..]);
        uint suffixOffset = BinaryPrimitives.ReadUInt32LittleEndian(info[24..]);
        string root, suffix;
        if (headerSize >= 0x24 && info.Length >= 36)
        {
            uint baseU = BinaryPrimitives.ReadUInt32LittleEndian(info[28..]);
            uint sufU = BinaryPrimitives.ReadUInt32LittleEndian(info[32..]);
            root = baseU != 0 ? ReadUnicode(info, baseU) : ReadAnsi(info, baseOffset);
            suffix = sufU != 0 ? ReadUnicode(info, sufU) : ReadAnsi(info, suffixOffset);
        }
        else { root = ReadAnsi(info, baseOffset); suffix = ReadAnsi(info, suffixOffset); }
        var target = root + suffix;
        return target.Length == 0 ? new AppError("The shortcut has an empty target.") : Result<string>.Ok(target);
    }

    private static string ReadAnsi(ReadOnlySpan<byte> s, uint offset)
    {
        if (offset == 0 || offset >= s.Length) return "";
        int end = s[(int)offset..].IndexOf((byte)0);
        return Encoding.Latin1.GetString(s.Slice((int)offset, end < 0 ? s.Length - (int)offset : end));
    }

    private static string ReadUnicode(ReadOnlySpan<byte> s, uint offset)
    {
        if (offset == 0 || offset >= s.Length) return "";
        var rest = s[(int)offset..];
        int len = 0;
        while (len + 1 < rest.Length && (rest[len] != 0 || rest[len + 1] != 0)) len += 2;
        return Encoding.Unicode.GetString(rest[..len]);
    }

    /// <summary>Builds a minimal valid shortcut (used by tests).</summary>
    public static byte[] Build(string target)
    {
        var path = Encoding.Latin1.GetBytes(target + "\0");
        int infoSize = 28 + 1 + path.Length + 1;           // header + (empty volume id placeholder) + base path + empty suffix
        var info = new byte[infoSize];
        BinaryPrimitives.WriteUInt32LittleEndian(info, (uint)infoSize);
        BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(4), 28);
        BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(8), VolumeIdAndLocalBasePath);
        BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(16), 29);                         // base path after the 1-byte placeholder
        BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(24), (uint)(29 + path.Length));   // suffix: a single 0 byte
        path.CopyTo(info, 29);
        var file = new byte[76 + infoSize];
        BinaryPrimitives.WriteUInt32LittleEndian(file, 0x4C);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(0x14), HasLinkInfo);
        info.CopyTo(file, 76);
        return file;
    }
}

public static class UrlFileParser
{
    /// <summary>The local file a .url (Internet Shortcut) points at; web addresses return an error because they are not on a drive.</summary>
    public static Result<string> ReadTarget(string urlPath)
    {
        try
        {
            foreach (var line in File.ReadLines(urlPath))
            {
                if (!line.StartsWith("URL=", StringComparison.OrdinalIgnoreCase)) continue;
                var value = line[4..].Trim();
                if (value.StartsWith("file:///", StringComparison.OrdinalIgnoreCase))
                    return Result<string>.Ok(Uri.UnescapeDataString(value[8..]).Replace('/', '\\'));
                return new AppError("The shortcut points at a web address.");
            }
            return new AppError("The .url file has no URL line.");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return new AppError($"Cannot read the shortcut: {e.Message}", 0, urlPath); }
    }
}

/// <summary>Imported DLL names of a Windows program, read straight from the PE headers (regular and delay-load imports).</summary>
public static class PeImportReader
{
    public static Result<IReadOnlyList<string>> ReadImports(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1);
            return Read(fs);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return new AppError($"Cannot read the program: {e.Message}", 0, path); }
    }

    public static Result<IReadOnlyList<string>> Read(Stream s)
    {
        var head = new byte[64];
        if (!ReadAt(s, 0, head) || head[0] != 'M' || head[1] != 'Z') return new AppError("Not a Windows program.");
        long pe = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(0x3C));
        var coff = new byte[24];
        if (!ReadAt(s, pe, coff) || coff[0] != 'P' || coff[1] != 'E') return new AppError("Not a Windows program.");
        int sections = BinaryPrimitives.ReadUInt16LittleEndian(coff.AsSpan(6));
        int optSize = BinaryPrimitives.ReadUInt16LittleEndian(coff.AsSpan(20));
        var opt = new byte[Math.Min(optSize, 240)];
        if (opt.Length < 96 || !ReadAt(s, pe + 24, opt)) return new AppError("Damaged program header.");
        ushort magic = BinaryPrimitives.ReadUInt16LittleEndian(opt);
        int dirStart = magic == 0x20B ? 112 : magic == 0x10B ? 96 : -1;
        if (dirStart < 0 || opt.Length < dirStart + 8 * 14) return Result<IReadOnlyList<string>>.Ok(Array.Empty<string>());
        var table = new byte[sections * 40];
        if (sections is < 1 or > 96 || !ReadAt(s, pe + 24 + optSize, table)) return new AppError("Damaged section table.");

        var names = new List<string>();
        uint importRva = BinaryPrimitives.ReadUInt32LittleEndian(opt.AsSpan(dirStart + 8));
        uint delayRva = BinaryPrimitives.ReadUInt32LittleEndian(opt.AsSpan(dirStart + 13 * 8));
        if (importRva != 0) ReadDescriptors(s, table, sections, importRva, 20, 12, names);
        if (delayRva != 0) ReadDescriptors(s, table, sections, delayRva, 32, 4, names);
        return Result<IReadOnlyList<string>>.Ok(names.Distinct(StringComparer.OrdinalIgnoreCase).ToList());
    }

    private static void ReadDescriptors(Stream s, byte[] table, int sections, uint rva, int descriptorSize, int nameField, List<string> names)
    {
        long off = RvaToOffset(table, sections, rva);
        if (off < 0) return;
        var d = new byte[descriptorSize];
        for (int i = 0; i < 512; i++)
        {
            if (!ReadAt(s, off + (long)i * descriptorSize, d)) return;
            uint nameRva = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(nameField));
            if (nameRva == 0) return;
            long nameOff = RvaToOffset(table, sections, nameRva);
            if (nameOff < 0) continue;
            var buf = new byte[260];
            int got = ReadSome(s, nameOff, buf);
            int end = Array.IndexOf(buf, (byte)0, 0, got);
            if (end > 0) names.Add(Encoding.ASCII.GetString(buf, 0, end));
        }
    }

    private static long RvaToOffset(byte[] table, int sections, uint rva)
    {
        for (int i = 0; i < sections; i++)
        {
            var sec = table.AsSpan(i * 40);
            uint virtualSize = BinaryPrimitives.ReadUInt32LittleEndian(sec[8..]), va = BinaryPrimitives.ReadUInt32LittleEndian(sec[12..]);
            uint raw = BinaryPrimitives.ReadUInt32LittleEndian(sec[16..]), ptr = BinaryPrimitives.ReadUInt32LittleEndian(sec[20..]);
            uint span = Math.Max(virtualSize, raw);
            if (rva >= va && rva < va + span) return ptr + (rva - va);
        }
        return -1;
    }

    private static bool ReadAt(Stream s, long offset, byte[] buffer) => ReadSome(s, offset, buffer) == buffer.Length;

    private static int ReadSome(Stream s, long offset, byte[] buffer)
    {
        if (offset < 0 || offset >= s.Length) return 0;
        s.Seek(offset, SeekOrigin.Begin);
        int total = 0;
        while (total < buffer.Length) { int n = s.Read(buffer, total, buffer.Length - total); if (n == 0) break; total += n; }
        return total;
    }
}

/// <summary>Finds absolute Windows paths written inside text files (configs, scripts, project files).</summary>
public static partial class PathMentions
{
    private static readonly Regex Quoted = new(@"[""'](?<p>[A-Za-z]:\\\\?[^""'<>|*?\r\n]{2,260})[""']", RegexOptions.Compiled);
    private static readonly Regex Bare = new(@"(?<![A-Za-z0-9])(?<p>[A-Za-z]:\\\\?[^\s""'<>|*?\r\n]{2,260})", RegexOptions.Compiled);

    /// <summary>Candidate paths (not checked against the disk), with doubled backslashes from JSON/escaped text folded and trailing punctuation trimmed.</summary>
    public static IReadOnlyList<string> Find(string text, int max = 64)
    {
        var found = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rx in new[] { Quoted, Bare })
            foreach (Match m in rx.Matches(text))
            {
                var p = Clean(m.Groups["p"].Value);
                if (p.Length > 3 && seen.Add(p)) found.Add(p);
                if (found.Count >= max) return found;
            }
        return found;
    }

    private static string Clean(string raw) => raw.Replace("\\\\", "\\").TrimEnd('.', ',', ';', ')', ']', '}', ':', ' ');
}
