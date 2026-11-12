using FileExplorer.Core.Common;
using FileExplorer.Core.Models;

namespace FileExplorer.App.Tabs;

/// <summary>Row in a file list. Icon and type text are resolved lazily through the IconCache.</summary>
public sealed class FileEntryViewModel : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
{
    private string _folderSize = "";
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
    public string SizeText => Entry.IsDir && !IsDriveRow ? _folderSize : SizeFormatter.Format(Entry.Size);

    /// <summary>Shown in the Size column for folders once calculated; always labelled with where it came from.</summary>
    public void SetFolderSize(string text) { _folderSize = text; OnPropertyChanged(nameof(SizeText)); }
    public string ModifiedText => Entry.ModifiedLocal == DateTime.MinValue ? "" : DateFormatter.Format(Entry.ModifiedLocal);
    public string TypeText => _icons.GetTypeName(Entry);
    public object? Icon => _icons.GetIcon(Entry);
    public double Opacity => Entry.IsHidden ? 0.55 : 1.0;
}

public readonly record struct FolderSizeResult(long Bytes, string Source);

/// <summary>Folder sizes on demand: from an existing disk-usage scan if there is one, otherwise by scanning that folder.</summary>
public interface IFolderSizeProvider
{
    Result<FolderSizeResult> Lookup(string folder);                                  // cached only, no scanning
    Task<Result<FolderSizeResult>> ComputeAsync(string folder, CancellationToken ct);
}

/// <summary>Abstraction so view models stay free of WPF imaging types.</summary>
public interface IIconProvider
{
    object? GetIcon(FileEntry entry);
    string GetTypeName(FileEntry entry);
}
