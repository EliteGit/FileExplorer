using System.Diagnostics;
using System.Security.Principal;
using FileExplorer.Core.Common;

namespace FileExplorer.App.Services;

/// <summary>Elevation is only ever entered through an explicit user action ("Enable fast indexing").</summary>
public sealed class ElevationService
{
    public bool IsElevated()
    {
        using var id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>Starts a second, elevated copy of the app. The caller exits this copy when this returns Ok.</summary>
    public Result<Unit> RelaunchElevated()
    {
        try
        {
            var exe = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot find the application path.");
            Process.Start(new ProcessStartInfo(exe, "--mft") { UseShellExecute = true, Verb = "runas" });
            return Result.Ok();
        }
        catch (System.ComponentModel.Win32Exception e) when (e.NativeErrorCode == 1223)
        { return Result.Err(new AppError("Elevation was cancelled, so fast indexing stays off.", e.NativeErrorCode)); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        { return Result.Err(new AppError($"Cannot relaunch elevated: {e.Message}")); }
    }
}
