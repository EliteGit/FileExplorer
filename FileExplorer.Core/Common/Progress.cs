namespace FileExplorer.Core.Common;

/// <summary>Convention for every long operation: (IProgress&lt;Progress&gt;, CancellationToken).</summary>
public readonly record struct Progress(long Done, long Total, string? Current = null);

public interface IUndoable
{
    string Description { get; }
    Result<Unit> Undo();
}

public sealed class UndoStack
{
    private readonly Stack<IUndoable> _stack = new();
    public event Action? Changed;

    public int Count => _stack.Count;
    public string? NextDescription => _stack.TryPeek(out var u) ? u.Description : null;

    public void Push(IUndoable undoable) { _stack.Push(undoable); Changed?.Invoke(); }

    /// <summary>Undoes the latest action. On failure the action stays on the stack and the error is returned.</summary>
    public Result<Unit> UndoLast()
    {
        if (!_stack.TryPeek(out var u)) return Result.Err(new AppError("Nothing to undo."));
        var r = u.Undo();
        if (r.IsOk) { _stack.Pop(); Changed?.Invoke(); }
        return r;
    }
}

public sealed class DelegateUndoable(string description, Func<Result<Unit>> undo) : IUndoable
{
    public string Description => description;
    public Result<Unit> Undo() => undo();
}
