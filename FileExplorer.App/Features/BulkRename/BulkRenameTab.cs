using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FileExplorer.App.Features.Tools;
using FileExplorer.App.Shell;
using FileExplorer.App.Tabs;
using FileExplorer.Core.Common;
using FileExplorer.Core.Fs;
using FileExplorer.Core.Rename;

namespace FileExplorer.App.Features.BulkRename;

public sealed record PreviewRow(string OldName, string NewName, string Conflict, bool Changed)
{
    public bool HasConflict => Conflict.Length > 0;
}

public sealed partial class BulkRenameTabViewModel : TabViewModelBase
{
    public const string Type = "bulkrename";
    private readonly ToolServices _s;
    private readonly BulkRenamer _renamer = new();
    private readonly string _folder;
    private IReadOnlyList<RenameItem> _items;
    private IReadOnlyList<RenameEntry> _plan = Array.Empty<RenameEntry>();

    [ObservableProperty] private string _findText = "";
    [ObservableProperty] private string _replaceText = "";
    [ObservableProperty] private bool _caseSensitive;
    [ObservableProperty] private bool _useRegex;
    [ObservableProperty] private int _caseModeIndex;          // 0 none, 1 upper, 2 lower, 3 title, 4 sentence
    [ObservableProperty] private bool _useDate;
    [ObservableProperty] private string _dateFormat = "yyyy-MM-dd";
    [ObservableProperty] private bool _dateAsPrefix = true;
    [ObservableProperty] private bool _useCounter;
    [ObservableProperty] private string _counterTemplate = "{n}";
    [ObservableProperty] private int _counterStart = 1;
    [ObservableProperty] private int _counterPad = 2;
    [ObservableProperty] private int _counterModeIndex;       // 0 replace name, 1 append, 2 prepend
    [ObservableProperty] private int _extensionModeIndex;     // 0 keep, 1 lower, 2 upper, 3 set, 4 remove
    [ObservableProperty] private string _extensionValue = "";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _canApply;

    public BulkRenameTabViewModel(ToolServices services, string folder, IReadOnlyList<RenameItem> items)
    {
        _s = services; _folder = folder; _items = items;
        Recompute();
    }

    public ObservableCollection<PreviewRow> Rows { get; } = new();
    public override string TypeId => Type;
    public override string Title => "Bulk rename";
    public string Folder => _folder;

    // Every option change recomputes the preview.
    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName is nameof(Status) or nameof(CanApply) or nameof(Title)) return;
        Recompute();
    }

    private (List<IRenameRule>? Rules, string? Error) BuildRules()
    {
        var rules = new List<IRenameRule>();
        if (FindText.Length > 0)
        {
            if (UseRegex)
            {
                var rx = RegexRule.Create(FindText, ReplaceText, !CaseSensitive);
                if (!rx.IsOk) return (null, rx.Error.Message);
                rules.Add(rx.Value);
            }
            else rules.Add(new FindReplaceRule(FindText, ReplaceText, CaseSensitive));
        }
        if (CaseModeIndex > 0) rules.Add(new CaseRule((CaseMode)(CaseModeIndex - 1)));
        if (UseDate && DateFormat.Length > 0)
        {
            try { _ = DateTime.Now.ToString(DateFormat); } catch (FormatException) { return (null, "The date format is not valid."); }
            rules.Add(new DateRule(DateFormat, DateAsPrefix));
        }
        if (UseCounter) rules.Add(new CounterRule(CounterTemplate.Length == 0 ? "{n}" : CounterTemplate, CounterStart, 1, Math.Max(1, CounterPad), (CounterMode)Math.Max(0, CounterModeIndex)));
        if (ExtensionModeIndex > 0) rules.Add(new ExtensionRule((ExtensionMode)(ExtensionModeIndex - 1), ExtensionValue));
        return (rules, null);
    }

    private void Recompute()
    {
        var (rules, error) = BuildRules();
        Rows.Clear();
        if (rules is null) { Status = error!; CanApply = false; _plan = Array.Empty<RenameEntry>(); return; }
        _plan = _renamer.Preview(_items, rules);
        foreach (var e in _plan) Rows.Add(new PreviewRow(e.OldName, e.NewName, e.Conflict ?? "", e.Changed));
        int changed = _plan.Count(e => e.Changed), conflicts = _plan.Count(e => e.Conflict is not null);
        CanApply = changed > 0 && conflicts == 0;
        Status = _items.Count == 0 ? "Nothing selected: select files in a folder tab first." : conflicts > 0 ? $"{conflicts} conflict(s): fix them before renaming." : $"{changed} of {_items.Count} item(s) will be renamed.";
    }

    [RelayCommand]
    private void Apply()
    {
        var r = _renamer.Apply(_plan);
        if (!_s.Errors.Check(r)) return;
        _s.Undo.Push(r.Value);
        Status = $"Renamed {_plan.Count(e => e.Changed)} item(s). Ctrl+Z in a folder tab undoes it.";
        _items = _items.Select(i => i with { Path = _plan.FirstOrDefault(e => e.Item.Path == i.Path) is { Changed: true } e ? e.NewPath : i.Path }).ToList();
        FindText = ""; ReplaceText = "";   // back to a neutral preview of the new names
    }

    public override TabState Save() => new(Type, JsonSerializer.Serialize(_folder));
    public override ITabViewModel Duplicate() => new BulkRenameTabViewModel(_s, _folder, _items);
}
