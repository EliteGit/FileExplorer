using FileExplorer.Core.Common;
using FileExplorer.Core.Models;

namespace FileExplorer.App.Tabs;

/// <summary>Row in a file list. Icon and type text are resolved lazily through the IconCache.</summary>
public sealed class FileEntryViewModel
{
    private readonly IIconProvider _icons;

    public FileEntryViewModel(FileEntry entry, IIconProvider icons, bool isDriveRow = false)
    {
        Entry = entry;
        _icons = icons;
        IsDriveRow = isDriveRow;
    }

    public FileEntry Entry { get; }
    public bool IsDriveRow { get; }
    public string Name => Entry.Name;
    public string FullPath => Entry.FullPath;
    public bool IsDir => Entry.IsDir;
    public string SizeText => Entry.IsDir && !IsDriveRow ? "" : SizeFormatter.Format(Entry.Size);
    public string ModifiedText => Entry.ModifiedLocal == DateTime.MinValue ? "" : DateFormatter.Format(Entry.ModifiedLocal);
    public string TypeText => _icons.GetTypeName(Entry);
    public object? Icon => _icons.GetIcon(Entry);
    public double Opacity => Entry.IsHidden ? 0.55 : 1.0;
}

/// <summary>Abstraction so view models stay free of WPF imaging types.</summary>
public interface IIconProvider
{
    object? GetIcon(FileEntry entry);
    string GetTypeName(FileEntry entry);
}
