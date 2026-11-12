using FileExplorer.App.Tabs;
using FileExplorer.Core.Common;

namespace FileExplorer.App.Shell;

/// <summary>Builds the status bar text (item counts, selection size) for the active pane.</summary>
public static class StatusService
{
    public static string Describe(BrowserTabViewModel tab) =>
        tab.IsSplit
            ? $"Left {Count(tab.Panes[0])} · Right {Count(tab.Panes[1])}"
            : Count(tab.Panes[0]);

    public static string DescribeSelection(PaneViewModel pane)
    {
        if (pane.Selection.Count == 0) return "";
        var bytes = pane.SelectionBytes;
        return bytes > 0 ? $"{pane.Selection.Count} selected · {SizeFormatter.Format(bytes)}" : $"{pane.Selection.Count} selected";
    }

    private static string Count(PaneViewModel pane) => $"{pane.ItemCount} items";
}
