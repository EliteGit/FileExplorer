using System.Windows;
using System.Windows.Controls;
using FileExplorer.App.Shell;
using FileExplorer.Core.Common;
using FileExplorer.Core.Hashing;

namespace FileExplorer.App.Features.Checksum;

/// <summary>Checksum viewer for the selected file (SHA-256 and xxHash64, computed in the background).</summary>
public sealed class ChecksumFeature : IFeature
{
    private readonly ErrorReporter _errors;
    public ChecksumFeature(ErrorReporter errors) => _errors = errors;
    public string Name => "Checksum";

    public void Register(FeatureRegistry registry) =>
        registry.AddCommand(new FeatureCommand("Checksum of selected file…", null, Open));

    private void Open(MainViewModel window)
    {
        if (window.ActivePane is not { Selection.Count: 1 } pane || pane.Selection[0].IsDir)
        { _errors.Report(new AppError("Select one file to see its checksums.")); return; }
        var w = new ChecksumWindow(pane.Selection[0].FullPath, _errors) { Owner = Application.Current.Windows.OfType<Window>().FirstOrDefault(x => x.IsActive) };
        w.Show();
    }
}

public sealed class ChecksumWindow : Window
{
    private readonly CancellationTokenSource _cts = new();

    public ChecksumWindow(string path, ErrorReporter errors)
    {
        Title = "Checksum"; Width = 640; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "Panel");
        var progress = new ProgressBar { Height = 6, Margin = new Thickness(0, 0, 0, 10), Maximum = 1 };
        var stack = new StackPanel { Margin = new Thickness(16) };
        stack.Children.Add(new TextBlock { Text = path, TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 10) });
        stack.Children.Add(progress);
        var sha = Row(stack, "SHA-256"); var xx = Row(stack, "xxHash64");
        Closed += (_, _) => _cts.Cancel();
        Loaded += async (_, _) =>
        {
            var hasher = new FileHasher(); var token = _cts.Token;
            var p = new Progress<Progress>(x => progress.Value = x.Total > 0 ? (double)x.Done / x.Total / 2 : 0);
            var r1 = await Task.Run(() => hasher.HashFull(path, HashKind.Sha256, p, token));
            Show(sha, r1, errors);
            var p2 = new Progress<Progress>(x => progress.Value = 0.5 + (x.Total > 0 ? (double)x.Done / x.Total / 2 : 0));
            var r2 = await Task.Run(() => hasher.HashFull(path, HashKind.XxHash64, p2, token));
            Show(xx, r2, errors);
            progress.Value = 1;
        };
        Content = stack;
    }

    private static void Show(TextBox box, Result<string> r, ErrorReporter errors)
    {
        if (r.IsOk) box.Text = r.Value; else { box.Text = r.Error.Message; errors.Report(r.Error); }
    }

    private static TextBox Row(StackPanel parent, string label)
    {
        var caption = new TextBlock { Text = label, Margin = new Thickness(0, 6, 0, 2) };
        var box = new TextBox { IsReadOnly = true, FontFamily = new System.Windows.Media.FontFamily("Cascadia Mono, Consolas"), Text = "computing…" };
        var copy = new Button { Content = "Copy", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 0) };
        copy.Click += (_, _) => { if (box.Text != "computing…") Clipboard.SetText(box.Text); };
        parent.Children.Add(caption); parent.Children.Add(box); parent.Children.Add(copy);
        return box;
    }
}
