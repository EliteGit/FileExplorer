using System.Collections.Concurrent;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using FileExplorer.App.Tabs;
using FileExplorer.Core.Models;
using FileExplorer.Core.Native;

namespace FileExplorer.App.Services;

/// <summary>Shell icons and type names, cached per extension (per path for exe/ico/lnk and drives).</summary>
public sealed class IconCache : IIconProvider
{
    private static readonly HashSet<string> PerFile = new(StringComparer.OrdinalIgnoreCase) { ".exe", ".ico", ".lnk", ".msi", ".scr" };
    private readonly ConcurrentDictionary<string, BitmapSource?> _icons = new();
    private readonly ConcurrentDictionary<string, string> _types = new();

    public object? GetIcon(FileEntry entry) => _icons.GetOrAdd(Key(entry), _ => Load(entry));

    public string GetTypeName(FileEntry entry) => _types.GetOrAdd(Key(entry), _ => Query(entry).TypeName);

    private static string Key(FileEntry e)
    {
        if (e.IsDir) return e.FullPath.Length == 3 ? "drive:" + e.FullPath : "<dir>";
        var ext = Path.GetExtension(e.Name);
        return PerFile.Contains(ext) ? e.FullPath : ext.ToLowerInvariant();
    }

    private static BitmapSource? Load(FileEntry entry)
    {
        var (icon, _) = Query(entry, wantIcon: true);
        if (icon == IntPtr.Zero) return null;
        try
        {
            var src = Imaging.CreateBitmapSourceFromHIcon(icon, System.Windows.Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            return src;
        }
        finally { NativeMethods.DestroyIcon(icon); }
    }

    private static (IntPtr Icon, string TypeName) Query(FileEntry entry, bool wantIcon = false)
    {
        bool real = entry.FullPath.Length == 3 || PerFile.Contains(Path.GetExtension(entry.Name));
        var flags = NativeMethods.SHGFI_SMALLICON | NativeMethods.SHGFI_TYPENAME | (wantIcon ? NativeMethods.SHGFI_ICON : 0)
                    | (real ? 0u : NativeMethods.SHGFI_USEFILEATTRIBUTES);
        var info = new NativeMethods.SHFILEINFO();
        var path = real ? entry.FullPath : entry.Name;
        uint attrs = entry.IsDir ? (uint)FileAttributes.Directory : (uint)FileAttributes.Normal;
        NativeMethods.SHGetFileInfoW(path, attrs, ref info, (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.SHFILEINFO>(), flags);
        return (info.hIcon, info.szTypeName ?? "");
    }
}
