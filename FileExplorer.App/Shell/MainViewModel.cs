using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FileExplorer.App.Tabs;
using FileExplorer.Core.Common;

namespace FileExplorer.App.Shell;

/// <summary>State of one window. Every window is built identically by WindowFactory.</summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly WindowManager _manager;
    private readonly FileCommands _files;
    private readonly SplitCommands _split;
    private readonly IThemeService _theme;
    private PaneViewModel? _watchedPane;
    private BrowserTabViewModel? _watchedBrowser;

    [ObservableProperty] private ITabViewModel? _activeTab;
    [ObservableProperty] private string _statusLeft = "";
    [ObservableProperty] private string _statusSelection = "";
    [ObservableProperty] private string _statusError = "";

    public MainViewModel(WindowManager manager, FeatureRegistry features, ErrorReporter errors,
        FileCommands files, SplitCommands split, IThemeService theme)
    {
        _manager = manager; Features = features; Errors = errors; _files = files; _split = split; _theme = theme;
        errors.ErrorReported += OnError;
    }

    public WindowManager Manager => _manager;
    public FeatureRegistry Features { get; }
    public ErrorReporter Errors { get; }
    public ObservableCollection<ITabViewModel> Tabs { get; } = new();

    public BrowserTabViewModel? ActiveBrowser => ActiveTab as BrowserTabViewModel;
    public PaneViewModel? ActivePane => ActiveBrowser?.ActivePane;
    public bool IsSplit => ActiveBrowser?.IsSplit ?? false;

    /// <summary>Names of the features available in this window (identical for every window).</summary>
    public IReadOnlyList<string> FeatureNames => Features.Features.Select(f => f.Name).ToList();
    public IReadOnlyList<string> TabTypeIds => Features.TabTypes.Select(t => t.Id).OrderBy(i => i).ToList();

    // ---- tab membership (used by WindowManager) ----
    public void AddTab(ITabViewModel tab, bool select = true, int? index = null)
    {
        if (index is { } i) Tabs.Insert(Math.Clamp(i, 0, Tabs.Count), tab); else Tabs.Add(tab);
        if (select) ActiveTab = tab;
    }

    /// <summary>Removes a tab without disposing it (it is moving elsewhere).</summary>
    public void DetachTab(ITabViewModel tab)
    {
        int i = Tabs.IndexOf(tab);
        if (i < 0) return;
        Tabs.RemoveAt(i);
        if (ActiveTab == tab) ActiveTab = Tabs.Count == 0 ? null : Tabs[Math.Min(i, Tabs.Count - 1)];
    }

    public void DisposeAllTabs()
    {
        foreach (var t in Tabs) t.Dispose();
        Tabs.Clear();
        ActiveTab = null;
    }

    // ---- active-tab tracking ----
    partial void OnActiveTabChanged(ITabViewModel? value)
    {
        WatchBrowser(value as BrowserTabViewModel);
        OnPropertyChanged(nameof(ActiveBrowser));
        OnActivePaneChanged();
    }

    private void WatchBrowser(BrowserTabViewModel? browser)
    {
        if (_watchedBrowser is not null)
            _watchedBrowser.PropertyChanged -= OnBrowserChanged;
        _watchedBrowser = browser;
        if (browser is not null) browser.PropertyChanged += OnBrowserChanged;
    }

    private void OnBrowserChanged(object? s, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(BrowserTabViewModel.ActivePane)) OnActivePaneChanged();
        if (e.PropertyName == nameof(BrowserTabViewModel.IsSplit)) { OnPropertyChanged(nameof(IsSplit)); RefreshStatus(); }
    }

    private void OnActivePaneChanged()
    {
        if (_watchedPane is not null) { _watchedPane.SelectionChanged -= RefreshStatus; _watchedPane.Navigated -= RefreshStatus; _watchedPane.PropertyChanged -= OnPaneChanged; }
        _watchedPane = ActivePane;
        if (_watchedPane is not null) { _watchedPane.SelectionChanged += RefreshStatus; _watchedPane.Navigated += RefreshStatus; _watchedPane.PropertyChanged += OnPaneChanged; }
        OnPropertyChanged(nameof(ActivePane));
        OnPropertyChanged(nameof(IsSplit));
        RefreshStatus();
    }

    private void OnPaneChanged(object? s, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PaneViewModel.Items)) RefreshStatus();
    }

    // ---- status bar ----
    private void RefreshStatus()
    {
        var b = ActiveBrowser;
        StatusLeft = b is null ? "" : StatusService.Describe(b);
        StatusSelection = b is null ? "" : StatusService.DescribeSelection(b.ActivePane);
    }

    private void OnError(AppError e) => StatusError = e.ToString();

    [RelayCommand]
    private void ShowErrorDetails()
    {
        System.Windows.MessageBox.Show(Errors.History.Count == 0 ? "No errors." : Errors.Details(), "Errors");
        StatusError = "";
    }

    // ---- tab commands ----
    [RelayCommand]
    public void NewTab(string? typeId = null)
    {
        var type = Features.FindTabType(typeId ?? BrowserTabViewModel.Type);
        if (type is null) { Errors.Report(new AppError($"Unknown tab type '{typeId}'.")); return; }
        AddTab(type.Create());
    }

    [RelayCommand]
    private void CloseTab(ITabViewModel? tab)
    {
        tab ??= ActiveTab;
        if (tab is null) return;
        DetachTab(tab);
        tab.Dispose();
        _manager.CloseIfEmpty(this);
    }

    [RelayCommand]
    private void CloseOthers(ITabViewModel? keep)
    {
        keep ??= ActiveTab;
        foreach (var t in Tabs.Where(t => t != keep).ToList()) { DetachTab(t); t.Dispose(); }
        if (keep is not null) ActiveTab = keep;
    }

    [RelayCommand]
    private void DuplicateTab(ITabViewModel? tab)
    {
        tab ??= ActiveTab;
        if (tab is null) return;
        AddTab(tab.Duplicate(), true, Tabs.IndexOf(tab) + 1);
    }

    [RelayCommand]
    private void MoveTabToNewWindow(ITabViewModel? tab)
    {
        tab ??= ActiveTab;
        if (tab is null) return;
        if (!_manager.MoveTabToNewWindow(this, tab))
            Errors.Report(new AppError("A window's only tab cannot be moved to a new window."));
    }

    [RelayCommand] private void NewWindow() => _manager.CreateWindow();
    [RelayCommand] private void CloseWindow() => _manager.CloseWindow(this);
    [RelayCommand] private void NextTab() => StepTab(1);
    [RelayCommand] private void PreviousTab() => StepTab(-1);
    [RelayCommand] private void ToggleTheme() => _theme.Toggle();

    private void StepTab(int delta)
    {
        if (Tabs.Count == 0 || ActiveTab is null) return;
        ActiveTab = Tabs[(Tabs.IndexOf(ActiveTab) + delta + Tabs.Count) % Tabs.Count];
    }

    [RelayCommand]
    private void RunFeatureCommand(FeatureCommand command) => command.Execute(this);

    // ---- pane / navigation commands ----
    [RelayCommand] private void Back() { if (ActivePane is { } p) Errors.Check(p.Back()); }
    [RelayCommand] private void Forward() { if (ActivePane is { } p) Errors.Check(p.Forward()); }
    [RelayCommand] private void Up() { if (ActivePane is { } p) Errors.Check(p.Up()); }
    [RelayCommand] private void Refresh() { if (ActivePane is { } p) Errors.Check(p.Refresh()); }
    [RelayCommand] private void NavigateTo(string? path) { if (ActivePane is { } p && path is not null) Errors.Check(p.Navigate(path)); }

    // ---- file commands (active pane) ----
    [RelayCommand] private void OpenSelected() { if (ActivePane is { } p) _files.OpenSelected(p); }
    [RelayCommand] private void CopySelected() { if (ActivePane is { } p) _files.Copy(p); }
    [RelayCommand] private void CutSelected() { if (ActivePane is { } p) _files.Cut(p); }
    [RelayCommand] private void Paste() { if (ActivePane is { } p) _files.Paste(p); }
    [RelayCommand] private void DeleteSelected() { if (ActivePane is { } p) _files.DeleteSelected(p); }
    [RelayCommand] private void RenameSelected() { if (ActivePane is { } p) _files.RenameSelected(p); }
    [RelayCommand] private void NewFolder() { if (ActivePane is { } p) _files.NewFolder(p); }
    [RelayCommand] private void Undo() => _files.Undo(ActivePane);
    [RelayCommand] private async Task CalculateFolderSizes() { if (ActivePane is { } p) await p.CalculateFolderSizesAsync(); }
    [RelayCommand] private void RevealSelected() { if (ActivePane is { } p) _files.Reveal(p); }
    [RelayCommand] private void ShowProperties() { if (ActivePane is { } p) _files.Properties(p); }
    [RelayCommand] private void OpenTerminalHere() { if (ActivePane is { } p) _files.OpenTerminalHere(p); }

    // ---- split commands ----
    [RelayCommand] private void ToggleSplit() { if (ActiveBrowser is { } b) _split.ToggleSplit(b); }
    [RelayCommand] private void ToggleOrientation() { if (ActiveBrowser is { } b) _split.ToggleOrientation(b); }
    [RelayCommand] private void SwapPanes() { if (ActiveBrowser is { } b) _split.Swap(b); }
    [RelayCommand] private void OpenOtherPaneHere() { if (ActiveBrowser is { } b) _split.OpenOtherPaneHere(b); }
    [RelayCommand] private void ResetSplit() { if (ActiveBrowser is { } b) _split.ResetRatio(b); }
    [RelayCommand] private void CopyToOtherPane() { if (ActiveBrowser is { } b) _split.CopyToOther(b); }
    [RelayCommand] private void MoveToOtherPane() { if (ActiveBrowser is { } b) _split.MoveToOther(b); }
    [RelayCommand] private void FocusPane(string? index)
    {
        if (ActiveBrowser is { IsSplit: true } b && int.TryParse(index, out var i) && i >= 1 && i <= b.Panes.Count)
            b.Activate(b.Panes[i - 1]);
    }

    /// <summary>Raised when a view should focus the global search box (feature-driven).</summary>
    public event Action? FocusSearchRequested;
    [RelayCommand] private void FocusSearch()
    {
        if (Features.SearchAction is { } search) search(this); else FocusSearchRequested?.Invoke();
    }
}
