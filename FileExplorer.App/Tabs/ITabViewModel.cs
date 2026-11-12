using CommunityToolkit.Mvvm.ComponentModel;

namespace FileExplorer.App.Tabs;

/// <summary>Serialized form of a tab for session restore; Data is owned by the tab type.</summary>
public sealed record TabState(string Type, string Data);

/// <summary>A tab is a plain object, never owned by a window, so it can move between windows with all state intact.</summary>
public interface ITabViewModel : IDisposable
{
    string TypeId { get; }
    string Title { get; }
    event Action? TitleChanged;
    TabState Save();
    /// <summary>A new, independent tab showing the same thing.</summary>
    ITabViewModel Duplicate();
}

public abstract class TabViewModelBase : ObservableObject, ITabViewModel
{
    public abstract string TypeId { get; }
    public abstract string Title { get; }
    public event Action? TitleChanged;
    public abstract TabState Save();
    public abstract ITabViewModel Duplicate();
    public virtual void Dispose() { }

    protected void RaiseTitleChanged()
    {
        OnPropertyChanged(nameof(Title));
        TitleChanged?.Invoke();
    }
}
