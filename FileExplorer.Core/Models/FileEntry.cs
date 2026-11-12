namespace FileExplorer.Core.Models;

public readonly record struct FileEntry(string Name, string FullPath, long Size, DateTime ModifiedLocal, FileAttributes Attributes, bool IsDir)
{
    public bool IsHidden => (Attributes & FileAttributes.Hidden) != 0;
    public bool IsReparse => (Attributes & FileAttributes.ReparsePoint) != 0;
}
