using System.Runtime.InteropServices;
using FileExplorer.Core.Common;

namespace FileExplorer.Core.Shell;

/// <summary>The real Explorer right-click menu (Open with, Send to handlers, 7-Zip, Git, ...) for one item, through IContextMenu.</summary>
public static class ShellContextMenu
{
    private const uint CMF_CANRENAME = 0x10, TPM_RETURNCMD = 0x100, TPM_RIGHTBUTTON = 2, CMIC_MASK_UNICODE = 0x4000, CMIC_MASK_PTINVOKE = 0x20000000;
    private static Guid IidShellItem = new("43826d1e-e718-42ee-bc55-a1e261c37bfe");
    private static Guid BhidSfuiObject = new("3981e225-f559-11d3-8e3a-00c04f6837d5");
    private static Guid IidContextMenu = new("000214e4-0000-0000-c000-000000000046");

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
        void GetParent(out IShellItem ppsi);
        void GetDisplayName(uint sigdn, out IntPtr name);
        void GetAttributes(uint mask, out uint attribs);
        void Compare(IShellItem psi, uint hint, out int order);
    }

    [ComImport, Guid("000214e4-0000-0000-c000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu
    {
        [PreserveSig] int QueryContextMenu(IntPtr hmenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint flags);
        void InvokeCommand(ref CMINVOKECOMMANDINFOEX info);
        void GetCommandString(UIntPtr idCmd, uint type, IntPtr reserved, IntPtr name, uint max);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CMINVOKECOMMANDINFOEX
    {
        public int cbSize; public uint fMask; public IntPtr hwnd; public IntPtr lpVerb; public IntPtr lpParameters; public IntPtr lpDirectory;
        public int nShow; public uint dwHotKey; public IntPtr hIcon; public IntPtr lpTitle; public IntPtr lpVerbW; public IntPtr lpParametersW;
        public IntPtr lpDirectoryW; public IntPtr lpTitleW; public int ptX, ptY;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(string path, IntPtr pbc, ref Guid riid, out IShellItem item);

    [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr h);
    [DllImport("user32.dll")] private static extern int TrackPopupMenuEx(IntPtr hmenu, uint flags, int x, int y, IntPtr hwnd, IntPtr tpm);

    /// <summary>Shows the menu at a screen position (device pixels) and runs the chosen command. Must be called on the UI (STA) thread.</summary>
    public static Result<Unit> Show(string path, int screenX, int screenY, IntPtr owner)
    {
        IntPtr menu = IntPtr.Zero, ptr = IntPtr.Zero;
        try
        {
            SHCreateItemFromParsingName(path, IntPtr.Zero, ref IidShellItem, out var item);
            item.BindToHandler(IntPtr.Zero, ref BhidSfuiObject, ref IidContextMenu, out ptr);
            var cm = (IContextMenu)Marshal.GetObjectForIUnknown(ptr);
            menu = CreatePopupMenu();
            int hr = cm.QueryContextMenu(menu, 0, 1, 0x7FFF, CMF_CANRENAME);
            if (hr < 0) return Result.Err(new AppError($"The shell menu could not be built (0x{hr:X8}).", 0, path));
            int id = TrackPopupMenuEx(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON, screenX, screenY, owner, IntPtr.Zero);
            if (id <= 0) return Result.Ok();    // dismissed
            var info = new CMINVOKECOMMANDINFOEX
            {
                cbSize = Marshal.SizeOf<CMINVOKECOMMANDINFOEX>(), fMask = CMIC_MASK_UNICODE | CMIC_MASK_PTINVOKE, hwnd = owner,
                lpVerb = (IntPtr)(id - 1), lpVerbW = (IntPtr)(id - 1), nShow = 1, ptX = screenX, ptY = screenY,
            };
            cm.InvokeCommand(ref info);
            return Result.Ok();
        }
        catch (Exception e) when (e is COMException or InvalidCastException or ArgumentException)
        { return Result.Err(new AppError($"The shell menu failed: {e.Message}", 0, path)); }
        finally
        {
            if (menu != IntPtr.Zero) DestroyMenu(menu);
            if (ptr != IntPtr.Zero) Marshal.Release(ptr);
        }
    }
}
