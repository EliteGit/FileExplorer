using FileExplorer.Core.Fs;

namespace FileExplorer.Core.Scan;

public enum TypeGroup { Documents, Images, Video, Audio, Archives, Executables, Code, Other }

public sealed record ExtensionStat(string Extension, TypeGroup Group, long Files, long Bytes);

/// <summary>Extension to group and colour, shared by the treemap, legend, table and icons.</summary>
public static class FileTypeCatalog
{
    private static readonly Dictionary<string, TypeGroup> Map = Build();

    private static Dictionary<string, TypeGroup> Build()
    {
        var d = new Dictionary<string, TypeGroup>(StringComparer.OrdinalIgnoreCase);
        void Add(TypeGroup g, string list) { foreach (var e in list.Split(' ')) d[e] = g; }
        Add(TypeGroup.Documents, "doc docx xls xlsx ppt pptx pdf txt rtf odt ods md csv epub");
        Add(TypeGroup.Images, "jpg jpeg png gif bmp tif tiff webp psd svg ico heic raw cr2 nef");
        Add(TypeGroup.Video, "mp4 mkv avi mov wmv flv webm m4v mpg mpeg");
        Add(TypeGroup.Audio, "mp3 wav flac aac ogg m4a wma opus");
        Add(TypeGroup.Archives, "zip rar 7z tar gz bz2 xz iso cab img vhd vhdx wim");
        Add(TypeGroup.Executables, "exe dll sys msi bat cmd com scr lib obj pdb");
        Add(TypeGroup.Code, "cs vb cpp c h hpp js ts py java go rs rb php html css json xml yaml yml xaml sln csproj");
        return d;
    }

    public static TypeGroup GroupOf(ReadOnlySpan<char> extension) =>
        extension.IsEmpty ? TypeGroup.Other : Map.TryGetValue(extension.TrimStart('.').ToString(), out var g) ? g : TypeGroup.Other;

    public static string ColorOf(TypeGroup g) => g switch
    {
        TypeGroup.Documents => "#4F7CD9", TypeGroup.Images => "#4AA98A", TypeGroup.Video => "#B565C9", TypeGroup.Audio => "#3FA7C4",
        TypeGroup.Archives => "#C9534F", TypeGroup.Executables => "#E0903A", TypeGroup.Code => "#8B7BD8", _ => "#8A94A6",
    };

    /// <summary>Per-extension totals over every live file in the index (allocated size when requested).</summary>
    public static List<ExtensionStat> Aggregate(FileIndex index, FolderSizeTree? tree = null)
    {
        var bytes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var count = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        using var _ = index.ReadLock();
        for (int i = 0; i < index.Count; i++)
        {
            if (index.IsDeleted(i) || index.IsDir(i)) continue;
            var ext = index.Extension(i).ToString().ToLowerInvariant();
            long size = tree is null ? index.Size(i) : tree.Size(i);
            bytes[ext] = bytes.GetValueOrDefault(ext) + size;
            count[ext] = count.GetValueOrDefault(ext) + 1;
        }
        return bytes.Select(kv => new ExtensionStat(kv.Key, GroupOf(kv.Key), count[kv.Key], kv.Value)).OrderByDescending(s => s.Bytes).ToList();
    }

    public static Dictionary<TypeGroup, long> ByGroup(IEnumerable<ExtensionStat> stats) =>
        stats.GroupBy(s => s.Group).ToDictionary(g => g.Key, g => g.Sum(s => s.Bytes));
}
