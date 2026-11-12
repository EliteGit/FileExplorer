using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FileExplorer.Core.Common;
using FileExplorer.Core.Models;

namespace FileExplorer.App.Features.Preview;

public interface IPreviewProvider
{
    string Name { get; }
    bool CanPreview(FileEntry entry);
    /// <summary>Builds the preview element. A failure is returned (and shown), never replaced by a fake preview.</summary>
    Result<FrameworkElement> CreateView(FileEntry entry);
}

/// <summary>Ordered set of providers; the first that accepts the file wins. Null means "no preview available".</summary>
public sealed class PreviewRegistry
{
    private readonly List<IPreviewProvider> _providers = new();
    public IReadOnlyList<IPreviewProvider> Providers => _providers;
    public void Add(IPreviewProvider p) => _providers.Add(p);
    public IPreviewProvider? Pick(FileEntry entry) => entry.IsDir ? null : _providers.FirstOrDefault(p => p.CanPreview(entry));
}

public sealed class TextPreviewProvider : IPreviewProvider
{
    private const int MaxBytes = 64 * 1024;
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".log", ".json", ".xml", ".csv", ".tsv", ".ini", ".cfg", ".conf", ".yml", ".yaml", ".toml", ".html", ".htm", ".css", ".js", ".ts",
        ".cs", ".vb", ".cpp", ".c", ".h", ".hpp", ".py", ".java", ".go", ".rs", ".rb", ".php", ".sql", ".sh", ".bat", ".cmd", ".ps1", ".xaml", ".csproj", ".sln", ".slnx", ".gitignore", ".props",
    };

    public string Name => "Text";
    public bool CanPreview(FileEntry e) => !e.IsDir && Extensions.Contains(Path.GetExtension(e.Name));

    public Result<FrameworkElement> CreateView(FileEntry entry)
    {
        try
        {
            var buffer = new byte[MaxBytes];
            int read;
            using (var fs = new FileStream(entry.FullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) read = fs.Read(buffer, 0, buffer.Length);
            if (Array.IndexOf(buffer, (byte)0, 0, Math.Min(read, 4096)) >= 0) return new AppError("This file looks binary, so it is not shown as text.", 0, entry.FullPath);
            string text;
            using (var sr = new StreamReader(new MemoryStream(buffer, 0, read), Encoding.UTF8, detectEncodingFromByteOrderMarks: true)) text = sr.ReadToEnd();
            if (entry.Size > MaxBytes) text += $"\n\n… showing the first {MaxBytes / 1024} KB of {SizeFormatter.Format(entry.Size)}";
            var box = new TextBox
            {
                Text = text, IsReadOnly = true, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 12, BorderThickness = new Thickness(0),
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Height = 260,
            };
            return Result<FrameworkElement>.Ok(box);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { return new AppError($"Cannot read the file: {e.Message}", 0, entry.FullPath); }
    }
}

public sealed class ImagePreviewProvider : IPreviewProvider
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".ico", ".tif", ".tiff" };

    public string Name => "Image";
    public bool CanPreview(FileEntry e) => !e.IsDir && Extensions.Contains(Path.GetExtension(e.Name));

    public Result<FrameworkElement> CreateView(FileEntry entry)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = new Uri(entry.FullPath);
            bmp.CacheOption = BitmapCacheOption.OnLoad;     // releases the file immediately
            bmp.DecodePixelWidth = 560;
            bmp.EndInit();
            bmp.Freeze();
            return Result<FrameworkElement>.Ok(new Image { Source = bmp, MaxHeight = 280, Stretch = Stretch.Uniform });
        }
        catch (Exception e) when (e is IOException or NotSupportedException or UnauthorizedAccessException or FileFormatException or InvalidOperationException)
        { return new AppError($"Cannot show this image: {e.Message}", 0, entry.FullPath); }
    }
}

/// <summary>PDF through WebView2. Without the runtime this provider reports why and the others keep working.</summary>
public sealed class PdfPreviewProvider : IPreviewProvider
{
    private static readonly Lazy<string?> Problem = new(CheckRuntime);

    public string Name => "PDF";
    public bool CanPreview(FileEntry e) => !e.IsDir && string.Equals(Path.GetExtension(e.Name), ".pdf", StringComparison.OrdinalIgnoreCase);

    private static string? CheckRuntime()
    {
        try { Microsoft.Web.WebView2.Core.CoreWebView2Environment.GetAvailableBrowserVersionString(); return null; }
        catch (Exception e) { return "PDF preview needs the Microsoft Edge WebView2 runtime, which is not installed (" + e.Message + ")."; }
    }

    public Result<FrameworkElement> CreateView(FileEntry entry)
    {
        if (Problem.Value is { } problem) return new AppError(problem, 0, entry.FullPath);
        var web = new Microsoft.Web.WebView2.Wpf.WebView2
        {
            Height = 360,
            CreationProperties = new Microsoft.Web.WebView2.Wpf.CoreWebView2CreationProperties
            {
                UserDataFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FileExplorer", "webview2"),
            },
            Source = new Uri(entry.FullPath),
        };
        return Result<FrameworkElement>.Ok(web);
    }
}
