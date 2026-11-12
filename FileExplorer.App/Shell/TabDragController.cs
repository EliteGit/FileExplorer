namespace FileExplorer.App.Shell;

public readonly record struct Pt(double X, double Y);

public enum DragState { Idle, Pending, Reordering, TornOut }

/// <summary>Host of the tab strip the controller drives (WPF strip in the app, fake in tests).</summary>
public interface ITabDragHost
{
    double StripHeight { get; }
    int TabCount { get; }
    int IndexOf(object tab);
    int IndexAtX(double x);
    void MoveTab(object tab, int newIndex);
    bool CanTearOut(object tab);
    /// <summary>Creates a new window holding the tab, placed so the pointer is over its tab strip.</summary>
    void TearOut(object tab, Pt screen);
    void MoveTornWindow(Pt screen);
    /// <summary>Pointer released after a tear-out: the new window stays where it is and becomes active.</summary>
    void DropTornWindow();
    /// <summary>Esc after tear-out: put the tab back at its original index and discard the new window.</summary>
    void ReturnTornTab(object tab, int originalIndex);
}

/// <summary>The only code that handles tab drag: reorder within a strip and tear-out into a new window.</summary>
public sealed class TabDragController
{
    public const double TearOutDistance = 24;
    public const double StartDistance = 4;

    private readonly ITabDragHost _host;
    private object? _tab;
    private Pt _start;
    private int _originalIndex;

    public TabDragController(ITabDragHost host) => _host = host;

    public DragState State { get; private set; } = DragState.Idle;
    /// <summary>True when the last gesture moved the tab (so the click that follows must not select).</summary>
    public bool DidDrag { get; private set; }

    public void PointerDown(object tab, Pt strip)
    {
        _tab = tab; _start = strip; _originalIndex = _host.IndexOf(tab);
        State = DragState.Pending; DidDrag = false;
    }

    public void PointerMove(Pt strip, Pt screen)
    {
        switch (State)
        {
            case DragState.Pending when Distance(strip, _start) >= StartDistance:
                State = DragState.Reordering; DidDrag = true;
                Reorder(strip);
                TryTearOut(strip, screen);
                break;
            case DragState.Reordering:
                if (!TryTearOut(strip, screen)) Reorder(strip);
                break;
            case DragState.TornOut:
                _host.MoveTornWindow(screen);
                break;
        }
    }

    public void PointerUp()
    {
        if (State == DragState.TornOut) _host.DropTornWindow();
        Finish();
    }

    /// <summary>Esc: a reorder returns to the original slot; a tear-out is undone.</summary>
    public void Cancel()
    {
        if (_tab is null) return;
        if (State == DragState.TornOut) _host.ReturnTornTab(_tab, _originalIndex);
        else if (State == DragState.Reordering) _host.MoveTab(_tab, _originalIndex);
        Finish();
    }

    private void Finish() { State = DragState.Idle; _tab = null; }

    private void Reorder(Pt strip)
    {
        int index = _host.IndexAtX(strip.X);
        if (index >= 0 && index != _host.IndexOf(_tab!)) _host.MoveTab(_tab!, index);
    }

    private bool TryTearOut(Pt strip, Pt screen)
    {
        bool outside = strip.Y < -TearOutDistance || strip.Y > _host.StripHeight + TearOutDistance;
        if (!outside) return false;
        State = DragState.TornOut;
        _host.TearOut(_tab!, screen);
        return true;
    }

    private static double Distance(Pt a, Pt b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
}
