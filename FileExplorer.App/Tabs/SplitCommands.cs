using FileExplorer.App.Shell;
using FileExplorer.Core.Common;
using FileExplorer.Core.Ops;

namespace FileExplorer.App.Tabs;

/// <summary>All split-view commands: toggle, orientation, swap, open-other-here, reset, F5/F6 transfers.</summary>
public sealed class SplitCommands
{
    private readonly IFileOps _ops;
    private readonly UndoStack _undo;
    private readonly ErrorReporter _errors;

    public SplitCommands(IFileOps ops, UndoStack undo, ErrorReporter errors)
    {
        _ops = ops; _undo = undo; _errors = errors;
    }

    public void ToggleSplit(BrowserTabViewModel tab)
    {
        if (tab.IsSplit) tab.RemoveSecondPane();
        else tab.AddPane();
    }

    /// <summary>Flips side-by-side / stacked; turns split on first if it is off.</summary>
    public void ToggleOrientation(BrowserTabViewModel tab)
    {
        if (!tab.IsSplit) tab.AddPane();
        tab.Orientation = tab.Orientation == SplitOrientation.Horizontal ? SplitOrientation.Vertical : SplitOrientation.Horizontal;
    }

    public void Swap(BrowserTabViewModel tab) => tab.SwapPanes();

    public void OpenOtherPaneHere(BrowserTabViewModel tab)
    {
        if (!tab.IsSplit) tab.AddPane();
        _errors.Check(tab.OtherPane!.Navigate(tab.ActivePane.Path));
    }

    public void ResetRatio(BrowserTabViewModel tab) => tab.SplitRatio = 0.5;

    public void CopyToOther(BrowserTabViewModel tab) => Transfer(tab, _ops.Copy);

    public void MoveToOther(BrowserTabViewModel tab) => Transfer(tab, _ops.Move);

    private void Transfer(BrowserTabViewModel tab, Func<IReadOnlyList<string>, string, Result<OpOutcome>> op)
    {
        if (!tab.IsSplit) { _errors.Report(new AppError("Open the second pane (Ctrl+\\) to copy or move between panes.")); return; }
        var from = tab.ActivePane; var to = tab.OtherPane!;
        if (from.Selection.Count == 0) return;
        if (to.IsThisPc) { _errors.Report(new AppError("Choose a folder in the other pane first; This PC cannot receive files.")); return; }
        var r = op(from.SelectedPaths, to.Path);
        if (_errors.Check(r) && r.Value.Undo is { } u) _undo.Push(u);
        from.Refresh(); to.Refresh();
    }
}
