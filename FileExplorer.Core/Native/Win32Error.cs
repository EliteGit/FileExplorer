using System.ComponentModel;
using FileExplorer.Core.Common;

namespace FileExplorer.Core.Native;

/// <summary>The single place a Win32 error code becomes an AppError.</summary>
public static class Win32Error
{
    public static AppError ToAppError(int code, string? path = null, string? context = null)
    {
        var text = new Win32Exception(code).Message;
        var message = context is null ? text : $"{context}: {text}";
        return new AppError(message, code, path);
    }

    public static AppError FromLastError(string? path = null, string? context = null) =>
        ToAppError(System.Runtime.InteropServices.Marshal.GetLastWin32Error(), path, context);
}
