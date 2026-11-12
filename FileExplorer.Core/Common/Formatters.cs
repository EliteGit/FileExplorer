using System.Globalization;

namespace FileExplorer.Core.Common;

public static class SizeFormatter
{
    private static readonly string[] Units = { "B", "KB", "MB", "GB", "TB", "PB" };

    public static string Format(long bytes)
    {
        double v = bytes; int u = 0;
        while (v >= 1024 && u < Units.Length - 1) { v /= 1024; u++; }
        return u == 0 ? $"{bytes} B" : string.Create(CultureInfo.CurrentCulture, $"{v:0.#} {Units[u]}");
    }
}

public static class DateFormatter
{
    public static string Format(DateTime local) => local.ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture);
    public static string FormatUtcTicks(long fileTimeUtc) =>
        fileTimeUtc <= 0 ? "" : Format(DateTime.FromFileTimeUtc(fileTimeUtc).ToLocalTime());
}
