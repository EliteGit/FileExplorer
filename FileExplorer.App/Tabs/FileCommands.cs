using FileExplorer.App.Shell;
using FileExplorer.Core.Common;
using FileExplorer.Core.Ops;
using FileExplorer.Core.Shell;

namespace FileExplorer.App.Tabs;

public interface IClipboardService
{
    void SetFiles(IReadOnlyList<string> paths, bool cut);
    (IReadOnlyList<string> Paths, bool Cut)? GetFiles();
}

public interface IPrompt
{
    /// <summary>Asks for a line of text; null when cancelled.</summary>
    string? Ask(string title, string label, string initial);
}

/// <summary>File commands on one pane: open, copy/cut/paste, delete, rename, new folder, undo, shell verbs.</summary>
public sealed class FileCommands
{
    private readonly IFileOps _ops;
    private readonly UndoStack _undo;
    private readonly ErrorReporter _errors;
    private readonly ShellActions _shell;
    private readonly IClipboardService _clipboard;
    private readonly IPrompt _prompt;

    public FileCommands(IFileOps ops, UndoStack undo, ErrorReporter errors, ShellActions shell, IClipboardService clipboard, IPrompt prompt)
    {
        _ops = ops; _undo = undo; _errors = errors; _shell = shell; _clipboard = clipboard; _prompt = prompt;
    }

    public void OpenSelected(PaneViewModel pane)
    {
        if (pane.Selection.Count != 1) return;
        var item = pane.Selection[0];
        if (item.IsDir) _errors.Check(pane.Navigate(item.FullPath));
        else _errors.Check(_shell.Open(item.FullPath));
    }

    public void Copy(PaneViewModel pane) => ToClipboard(pane, cut: false);

    public void Cut(PaneViewModel pane) => ToClipboard(pane, cut: true);

    public void Paste(PaneViewModel pane)
    {
        if (pane.IsThisPc) { _errors.Report(new AppError("Open a folder to paste into.")); return; }
        var clip = _clipboard.GetFiles();
        if (clip is null) return;
        var r = clip.Value.Cut ? _ops.Move(clip.Value.Paths, pane.Path) : _ops.Copy(clip.Value.Paths, pane.Path);
        Finish(r, pane);
    }

    public void DeleteSelected(PaneViewModel pane)
    {
        if (pane.Selection.Count == 0) return;
        Finish(_ops.Delete(pane.SelectedPaths), pane);
    }

    public void RenameSelected(PaneViewModel pane)
    {
        if (pane.Selection.Count != 1) return;
        var item = pane.Selection[0];
        var name = _prompt.Ask("Rename", "New name", item.Name);
        if (string.IsNullOrWhiteSpace(name) || name == item.Name) return;
        Finish(_ops.Rename(item.FullPath, name), pane);
    }

    public void NewFolder(PaneViewModel pane)
    {
        if (pane.IsThisPc) { _errors.Report(new AppError("Open a folder to create a new folder in.")); return; }
        var name = _prompt.Ask("New folder", "Folder name", "New folder");
        if (string.IsNullOrWhiteSpace(name)) return;
        Finish(_ops.NewFolder(pane.Path, name), pane);
    }

    public void Undo(PaneViewModel? refresh)
    {
        _errors.Check(_undo.UndoLast());
        refresh?.Refresh();
    }

    // ---- shell verbs ----
    public void Reveal(PaneViewModel pane) { if (pane.Selection.Count == 1) _errors.Check(_shell.Reveal(pane.Selection[0].FullPath)); }

    public void Properties(PaneViewModel pane) => _errors.Check(_shell.ShowProperties(pane.Selection.Count == 1 ? pane.Selection[0].FullPath : pane.Path));

    public void OpenTerminalHere(PaneViewModel pane)
    {
        if (!pane.IsThisPc) _errors.Check(_shell.OpenTerminalHere(pane.Path));
    }

    private void ToClipboard(PaneViewModel pane, bool cut)
    {
        if (pane.Selection.Count > 0) _clipboard.SetFiles(pane.SelectedPaths, cut);
    }

    private void Finish(Result<OpOutcome> result, PaneViewModel pane)
    {
        if (_errors.Check(result) && result.Value.Undo is { } undo) _undo.Push(undo);
        pane.Refresh();
    }
}
