using System.ComponentModel;
using System.Diagnostics;
using FileExplorer.Core.Common;
using FileExplorer.Core.Native;

namespace FileExplorer.Core.Shell;

/// <summary>Open, reveal, properties, terminal here.</summary>
public sealed class ShellActions
{
    public Result<Unit> Open(string path) => Start(new ProcessStartInfo(path) { UseShellExecute = true }, path);

    public Result<Unit> Reveal(string path) =>
        Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true }, path);

    public Result<Unit> OpenTerminalHere(string folder)
    {
        var wt = Start(new ProcessStartInfo("wt.exe", $"-d \"{folder}\"") { UseShellExecute = true }, folder);
        if (wt.IsOk) return wt;
        // Windows Terminal not installed: explicit choice of cmd.exe, not a hidden swap of results.
        return Start(new ProcessStartInfo("cmd.exe") { WorkingDirectory = folder, UseShellExecute = true }, folder);
    }

    public Result<Unit> ShowProperties(string path)
    {
        var info = new NativeMethods.SHELLEXECUTEINFO
        {
            cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.SHELLEXECUTEINFO>(),
            fMask = NativeMethods.SEE_MASK_INVOKEIDLIST, lpVerb = "properties", lpFile = path, nShow = 1
        };
        return NativeMethods.ShellExecuteExW(ref info) ? Result.Ok() : Win32Error.FromLastError(path, "Cannot show properties");
    }

    private static Result<Unit> Start(ProcessStartInfo psi, string path)
    {
        try { Process.Start(psi); return Result.Ok(); }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException)
        { return Result.Err(new AppError(e.Message, (e as Win32Exception)?.NativeErrorCode ?? 0, path)); }
    }
}
