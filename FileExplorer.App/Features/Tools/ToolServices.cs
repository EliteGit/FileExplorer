using FileExplorer.App.Shell;
using FileExplorer.App.Tabs;
using FileExplorer.Core.Common;
using FileExplorer.Core.Fs;
using FileExplorer.Core.Ops;
using FileExplorer.Core.Shell;

namespace FileExplorer.App.Features.Tools;

/// <summary>Services shared by the Phase 5 tools (duplicates, bulk rename, reports, checksum).</summary>
public sealed class ToolServices
{
    public ToolServices(IFileSource files, IFileOps ops, UndoStack undo, ErrorReporter errors, IPrompt prompt, ShellActions shell, Action<Action> post)
    { Files = files; Ops = ops; Undo = undo; Errors = errors; Prompt = prompt; Shell = shell; Post = post; }

    public IFileSource Files { get; }
    public IFileOps Ops { get; }
    public UndoStack Undo { get; }
    public ErrorReporter Errors { get; }
    public IPrompt Prompt { get; }
    public ShellActions Shell { get; }
    public Action<Action> Post { get; }

    /// <summary>Folder a tool should start in: the active pane's folder, or the first fixed drive.</summary>
    public static string StartFolder(MainViewModel window) =>
        window.ActivePane is { IsThisPc: false } p ? p.Path : DriveInfo.GetDrives().FirstOrDefault(d => d.DriveType == DriveType.Fixed)?.Name ?? @"C:\";

    /// <summary>Asks for a CSV path using the standard save dialog.</summary>
    public static string? AskCsvPath(string suggestedName)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog { FileName = suggestedName, Filter = "CSV file (*.csv)|*.csv", DefaultExt = ".csv" };
        return dlg.ShowDialog() == true ? dlg.FileName : null;
    }
}
