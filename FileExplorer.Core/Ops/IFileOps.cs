using FileExplorer.Core.Common;

namespace FileExplorer.Core.Ops;

/// <summary>Outcome of a successful operation; Undo is null where the operation cannot be reversed.</summary>
public sealed record OpOutcome(IUndoable? Undo);

public interface IFileOps
{
    Result<OpOutcome> Copy(IReadOnlyList<string> sources, string targetFolder);
    Result<OpOutcome> Move(IReadOnlyList<string> sources, string targetFolder);
    Result<OpOutcome> Delete(IReadOnlyList<string> sources);
    Result<OpOutcome> Rename(string path, string newName);
    Result<OpOutcome> NewFolder(string parentFolder, string name);
}
