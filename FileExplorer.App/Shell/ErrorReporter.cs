using FileExplorer.Core.Common;

namespace FileExplorer.App.Shell;

/// <summary>Single sink that every UI layer uses to surface an AppError.</summary>
public sealed class ErrorReporter
{
    private readonly List<AppError> _history = new();

    public event Action<AppError>? ErrorReported;

    public AppError? Last => _history.Count > 0 ? _history[^1] : null;
    public IReadOnlyList<AppError> History => _history;

    public void Report(AppError error)
    {
        _history.Add(error);
        ErrorReported?.Invoke(error);
    }

    /// <summary>Reports the error if the result failed; returns true when the result is Ok.</summary>
    public bool Check<T>(Result<T> result)
    {
        if (!result.IsOk) Report(result.Error);
        return result.IsOk;
    }

    public string Details() => string.Join(Environment.NewLine + Environment.NewLine, _history.AsEnumerable().Reverse().Select(e => e.ToString()));
}
