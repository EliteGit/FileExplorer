using FileExplorer.Core.Common;
using FileExplorer.Core.Native;
using FileExplorer.Core.Shell;
using static FileExplorer.Core.Native.NativeMethods;

namespace FileExplorer.Core.Ops;

/// <summary>File operations through the shell (progress UI, collision prompts, recycle bin).</summary>
public sealed class ShellFileOps : IFileOps
{
    public Result<OpOutcome> Copy(IReadOnlyList<string> sources, string targetFolder)
    {
        var expected = sources.Select(s => PathUtil.Combine(targetFolder, PathUtil.FileName(s))).ToList();
        var r = Run(FO_COPY, sources, targetFolder, FOF_ALLOWUNDO | FOF_NOCONFIRMMKDIR);
        if (!r.IsOk) return Result<OpOutcome>.Err(r.Error);
        var created = expected.Where(Exists).ToList();
        return Ok(new DelegateUndoable($"Copy {sources.Count} item(s)", () => DeleteCreated(created)));
    }

    public Result<OpOutcome> Move(IReadOnlyList<string> sources, string targetFolder)
    {
        var pairs = sources.Select(s => (From: s, To: PathUtil.Combine(targetFolder, PathUtil.FileName(s)))).ToList();
        var r = Run(FO_MOVE, sources, targetFolder, FOF_ALLOWUNDO | FOF_NOCONFIRMMKDIR);
        if (!r.IsOk) return Result<OpOutcome>.Err(r.Error);
        var moved = pairs.Where(p => Exists(p.To) && !Exists(p.From)).ToList();
        return Ok(new DelegateUndoable($"Move {moved.Count} item(s)", () => MoveBack(moved)));
    }

    public Result<OpOutcome> Delete(IReadOnlyList<string> sources)
    {
        var started = DateTime.Now.AddSeconds(-2);
        var r = Run(FO_DELETE, sources, null, FOF_ALLOWUNDO);
        if (!r.IsOk) return Result<OpOutcome>.Err(r.Error);
        var gone = sources.Where(p => !Exists(p)).ToList();
        // Items that vanished are in the Recycle Bin (or were permanently deleted, in which case Undo reports that honestly).
        return Ok(new DelegateUndoable($"Delete {gone.Count} item(s)", () => RecycleBin.Restore(gone, started)));
    }

    public Result<OpOutcome> Rename(string path, string newName)
    {
        if (newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return new AppError($"'{newName}' contains characters that are not allowed in a name.", 0, path);
        var parent = PathUtil.Parent(path);
        if (parent is null) return new AppError("A drive root cannot be renamed.", 0, path);
        var target = PathUtil.Combine(parent, newName);
        var r = MoveEntry(path, target);
        return !r.IsOk ? Result<OpOutcome>.Err(r.Error)
            : Ok(new DelegateUndoable($"Rename to {newName}", () => MoveEntry(target, path)));
    }

    public Result<OpOutcome> NewFolder(string parentFolder, string name)
    {
        var target = PathUtil.Combine(parentFolder, name);
        try { Directory.CreateDirectory(target); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { return new AppError(e.Message, 0, target); }
        return Ok(new DelegateUndoable($"New folder {name}", () => DeleteCreated(new[] { target })));
    }

    // ---- helpers ----
    private static Result<OpOutcome> Ok(IUndoable? u) => Result<OpOutcome>.Ok(new OpOutcome(u));

    private static bool Exists(string p) => File.Exists(p) || Directory.Exists(p);

    private static string DoubleNull(IEnumerable<string> paths) =>
        string.Join('\0', paths.Select(PathUtil.ToLongPath)) + "\0\0";

    private static Result<Unit> Run(uint func, IReadOnlyList<string> from, string? to, ushort flags)
    {
        var op = new SHFILEOPSTRUCT
        {
            wFunc = func, pFrom = DoubleNull(from),
            pTo = to is null ? null : DoubleNull(new[] { to }), fFlags = flags
        };
        int code = SHFileOperationW(ref op);
        if (code != 0) return Result.Err(Win32Error.ToAppError(code, from.FirstOrDefault(), "File operation failed"));
        if (op.fAnyOperationsAborted) return Result.Err(new AppError("The operation was cancelled.", 0, from.FirstOrDefault()));
        return Result.Ok();
    }

    private static Result<Unit> MoveEntry(string from, string to)
    {
        try
        {
            if (Directory.Exists(from)) Directory.Move(from, to); else File.Move(from, to);
            return Result.Ok();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { return Result.Err(new AppError(e.Message, 0, from)); }
    }

    private static Result<Unit> MoveBack(IEnumerable<(string From, string To)> moved)
    {
        foreach (var (from, to) in moved)
        {
            var r = MoveEntry(to, from);
            if (!r.IsOk) return r;
        }
        return Result.Ok();
    }

    /// <summary>Undo of a copy / new folder removes only what that operation created (to the Recycle Bin).</summary>
    private static Result<Unit> DeleteCreated(IReadOnlyList<string> paths) =>
        paths.Count == 0 ? Result.Ok() : Run(FO_DELETE, paths, null, FOF_ALLOWUNDO | FOF_NOCONFIRMATION);
}
