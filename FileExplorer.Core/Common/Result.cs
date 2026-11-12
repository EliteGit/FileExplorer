namespace FileExplorer.Core.Common;

/// <summary>Describes a failed operation. Carries message, optional Win32 code and path.</summary>
public sealed record AppError(string Message, int Win32Code = 0, string? Path = null)
{
    public override string ToString() => Path is null ? Message : $"{Message} ({Path})";
}

/// <summary>Uniform return type: either a value or an AppError, never null/default.</summary>
public readonly struct Result<T>
{
    private readonly T? _value;
    private readonly AppError? _error;

    private Result(T? value, AppError? error) { _value = value; _error = error; }

    public bool IsOk => _error is null;
    public T Value => IsOk ? _value! : throw new InvalidOperationException($"Result is an error: {_error}");
    public AppError Error => _error ?? throw new InvalidOperationException("Result is Ok.");

    public static Result<T> Ok(T value) => new(value, null);
    public static Result<T> Err(AppError error) => new(default, error);
    public static implicit operator Result<T>(AppError error) => Err(error);

    public Result<TOut> Map<TOut>(Func<T, TOut> map) =>
        IsOk ? Result<TOut>.Ok(map(Value)) : Result<TOut>.Err(Error);
}

public static class Result
{
    /// <summary>Non-generic success/failure for operations with no value.</summary>
    public static Result<Unit> Ok() => Result<Unit>.Ok(Unit.Value);
    public static Result<Unit> Err(AppError error) => Result<Unit>.Err(error);
}

public readonly struct Unit { public static readonly Unit Value = default; }
