using System.Buffers.Binary;
using System.Text;
using FileExplorer.Core.Common;
using FileExplorer.Core.Fs;

namespace FileExplorer.Core.Index;

public readonly record struct UsnRecord(ulong Frn, ulong ParentFrn, long Usn, uint Reason, uint Attributes, string Name);

/// <summary>Parses USN_RECORD_V2 structures (NTFS). Pure function over bytes, unit-tested with synthetic buffers.</summary>
public static class UsnRecordParser
{
    public const int HeaderSize = 60;           // up to the file name
    public const uint ReasonCreate = 0x100, ReasonDelete = 0x200, ReasonRenameNew = 0x2000, ReasonClose = 0x80000000;

    /// <summary>Parses consecutive records. Version 3 (ReFS 128-bit ids) is reported as an error, never guessed.</summary>
    public static Result<List<UsnRecord>> Parse(ReadOnlySpan<byte> buffer)
    {
        var list = new List<UsnRecord>();
        int pos = 0;
        while (pos + 8 <= buffer.Length)
        {
            uint length = BinaryPrimitives.ReadUInt32LittleEndian(buffer[pos..]);
            if (length == 0) break;
            if (length < HeaderSize || pos + length > buffer.Length)
                return new AppError($"Corrupt USN record at offset {pos} (length {length}).");
            ushort major = BinaryPrimitives.ReadUInt16LittleEndian(buffer[(pos + 4)..]);
            if (major != 2) return new AppError($"USN record version {major} is not supported (NTFS volumes use version 2).");
            list.Add(ReadV2(buffer.Slice(pos, (int)length)));
            pos += (int)length;
        }
        return Result<List<UsnRecord>>.Ok(list);
    }

    private static UsnRecord ReadV2(ReadOnlySpan<byte> r)
    {
        ulong frn = BinaryPrimitives.ReadUInt64LittleEndian(r[8..]);
        ulong parent = BinaryPrimitives.ReadUInt64LittleEndian(r[16..]);
        long usn = BinaryPrimitives.ReadInt64LittleEndian(r[24..]);
        uint reason = BinaryPrimitives.ReadUInt32LittleEndian(r[40..]);
        uint attrs = BinaryPrimitives.ReadUInt32LittleEndian(r[52..]);
        int nameLen = BinaryPrimitives.ReadUInt16LittleEndian(r[56..]);
        int nameOff = BinaryPrimitives.ReadUInt16LittleEndian(r[58..]);
        string name = Encoding.Unicode.GetString(r.Slice(nameOff, nameLen));
        return new UsnRecord(frn, parent, usn, reason, attrs, name);
    }

    /// <summary>Test/helper: builds one V2 record.</summary>
    public static byte[] Build(ulong frn, ulong parent, long usn, uint reason, uint attrs, string name)
    {
        var nameBytes = Encoding.Unicode.GetBytes(name);
        int length = (HeaderSize + nameBytes.Length + 7) & ~7;
        var b = new byte[length];
        BinaryPrimitives.WriteUInt32LittleEndian(b, (uint)length);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(4), 2);
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(8), frn);
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(16), parent);
        BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(24), usn);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(40), reason);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(52), attrs);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(56), (ushort)nameBytes.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(58), HeaderSize);
        nameBytes.CopyTo(b, HeaderSize);
        return b;
    }
}

/// <summary>Turns journal records into index changes. Journal records are per-reason, so one file may yield several.</summary>
public static class UsnChangeTranslator
{
    public static IEnumerable<IndexChange> ToChanges(IEnumerable<UsnRecord> records)
    {
        foreach (var r in records)
        {
            if ((r.Reason & UsnRecordParser.ReasonDelete) != 0)
                yield return new IndexChange(IndexChangeKind.Deleted, r.Frn, r.ParentFrn, r.Name, r.Attributes);
            else if ((r.Reason & UsnRecordParser.ReasonCreate) != 0)
                yield return new IndexChange(IndexChangeKind.Created, r.Frn, r.ParentFrn, r.Name, r.Attributes);
            else if ((r.Reason & UsnRecordParser.ReasonRenameNew) != 0)
                yield return new IndexChange(IndexChangeKind.Renamed, r.Frn, r.ParentFrn, r.Name, r.Attributes);
        }
    }
}
