using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using FileExplorer.Core.Common;

namespace FileExplorer.App.Tabs;

public enum SplitOrientation { Horizontal, Vertical }

/// <summary>A browser tab: one pane, or two panes side by side / stacked.</summary>
public sealed partial class BrowserTabViewModel : TabViewModelBase
{
    public const string Type = "browser";

    private readonly Func<PaneViewModel> _newPane;
    private PaneViewModel? _subscribedTo;

    [ObservableProperty] private SplitOrientation _orientation = SplitOrientation.Horizontal;
    [ObservableProperty] private PaneViewModel _activePane;
    [ObservableProperty] private double _splitRatio = 0.5;

    public BrowserTabViewModel(Func<PaneViewModel> newPane, string startPath)
    {
        _newPane = newPane;
        var first = newPane();
        first.Open(startPath);
        Panes.Add(first);
        _activePane = first;
        Subscribe(first);
        first.IsActive = true;
    }

    public ObservableCollection<PaneViewModel> Panes { get; } = new();
    public bool IsSplit => Panes.Count == 2;
    public PaneViewModel? OtherPane => IsSplit ? Panes.First(p => p != ActivePane) : null;

    public override string TypeId => Type;
    public override string Title => ActivePane.Title;

    public PaneViewModel AddPane()
    {
        var pane = _newPane();
        pane.Open(ActivePane.Path);
        Panes.Add(pane);
        OnPropertyChanged(nameof(IsSplit));
        return pane;
    }

    public void RemoveSecondPane()
    {
        if (!IsSplit) return;
        var removed = Panes[1];
        Panes.RemoveAt(1);
        removed.Dispose();
        if (ActivePane == removed) ActivePane = Panes[0];
        OnPropertyChanged(nameof(IsSplit)); OnPropertyChanged(nameof(OtherPane));
    }

    public void SwapPanes()
    {
        if (!IsSplit) return;
        Panes.Move(0, 1);
    }

    partial void OnActivePaneChanged(PaneViewModel? oldValue, PaneViewModel newValue)
    {
        if (oldValue is not null) oldValue.IsActive = false;
        newValue.IsActive = true;
        Subscribe(newValue);
        OnPropertyChanged(nameof(OtherPane));
        RaiseTitleChanged();
    }

    private void Subscribe(PaneViewModel pane)
    {
        if (_subscribedTo is not null) _subscribedTo.Navigated -= RaiseTitleChanged;
        _subscribedTo = pane;
        pane.Navigated += RaiseTitleChanged;
    }

    public void Activate(PaneViewModel pane)
    {
        if (Panes.Contains(pane)) ActivePane = pane;
    }

    // ---- persistence / duplication ----
    private sealed record Saved(List<string> Paths, SplitOrientation Orientation, int Active, double Ratio);

    public override TabState Save() => new(Type, JsonSerializer.Serialize(
        new Saved(Panes.Select(p => p.Path).ToList(), Orientation, Panes.IndexOf(ActivePane), SplitRatio)));

    public override ITabViewModel Duplicate() => Restore(Save(), _newPane, reporter: null);

    /// <summary>Rebuilds a tab from saved state. A path that no longer opens leaves that pane on This PC (error is reported).</summary>
    public static BrowserTabViewModel Restore(TabState state, Func<PaneViewModel> newPane, Shell.ErrorReporter? reporter)
    {
        var saved = JsonSerializer.Deserialize<Saved>(state.Data)
                    ?? throw new InvalidDataException("Saved browser tab is empty.");
        var tab = new BrowserTabViewModel(newPane, saved.Paths.FirstOrDefault() ?? "");
        tab.Orientation = saved.Orientation;
        tab.SplitRatio = saved.Ratio;
        if (saved.Paths.Count > 1)
        {
            var second = tab.AddPane();
            second.Open(saved.Paths[1]);
        }
        tab.ActivePane = tab.Panes[Math.Clamp(saved.Active, 0, tab.Panes.Count - 1)];
        return tab;
    }

    public override void Dispose()
    {
        foreach (var p in Panes) p.Dispose();
        base.Dispose();
    }
}
