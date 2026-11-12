using FileExplorer.Core.Models;

namespace FileExplorer.Core.Fs;

public enum SortColumn { Name, Size, Modified, Type }

/// <summary>Folders always come first; then the chosen column; name is the stable tiebreaker.</summary>
public static class EntrySorter
{
    public static List<FileEntry> Sort(IEnumerable<FileEntry> entries, SortColumn column, bool ascending)
    {
        var list = entries.ToList();
        list.Sort((a, b) =>
        {
            if (a.IsDir != b.IsDir) return a.IsDir ? -1 : 1;
            int c = CompareColumn(a, b, column);
            if (!ascending) c = -c;
            return c != 0 ? c : NameCompare(a, b);
        });
        return list;
    }

    private static int CompareColumn(FileEntry a, FileEntry b, SortColumn column) => column switch
    {
        SortColumn.Size => a.Size.CompareTo(b.Size),
        SortColumn.Modified => a.ModifiedLocal.CompareTo(b.ModifiedLocal),
        SortColumn.Type => string.Compare(Path.GetExtension(a.Name), Path.GetExtension(b.Name), StringComparison.OrdinalIgnoreCase),
        _ => NameCompare(a, b),
    };

    private static int NameCompare(FileEntry a, FileEntry b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);
}
