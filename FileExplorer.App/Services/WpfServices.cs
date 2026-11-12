using System.Collections.Specialized;
using System.Windows;
using FileExplorer.App.Shell;
using FileExplorer.App.Tabs;

namespace FileExplorer.App.Services;

/// <summary>Clipboard with the shell's "cut" marker (Preferred DropEffect).</summary>
public sealed class ClipboardService : IClipboardService
{
    private const string DropEffect = "Preferred DropEffect";

    public void SetFiles(IReadOnlyList<string> paths, bool cut)
    {
        var list = new StringCollection();
        list.AddRange(paths.ToArray());
        var data = new DataObject();
        data.SetFileDropList(list);
        data.SetData(DropEffect, new MemoryStream(BitConverter.GetBytes(cut ? 2 : 5)));
        Clipboard.SetDataObject(data, true);
    }

    public (IReadOnlyList<string> Paths, bool Cut)? GetFiles()
    {
        if (!Clipboard.ContainsFileDropList()) return null;
        var paths = Clipboard.GetFileDropList().Cast<string>().ToList();
        bool cut = false;
        if (Clipboard.GetData(DropEffect) is MemoryStream ms && ms.Length >= 4)
            cut = (BitConverter.ToInt32(ms.ToArray(), 0) & 2) != 0 && (BitConverter.ToInt32(ms.ToArray(), 0) & 1) == 0;
        return (paths, cut);
    }
}

public sealed class PromptService : IPrompt
{
    public string? Ask(string title, string label, string initial) => Views.PromptWindow.Show(title, label, initial);
}

/// <summary>Swaps the light/dark resource dictionary (always the first merged dictionary).</summary>
public sealed class ThemeService : IThemeService
{
    public bool IsDark { get; private set; }

    public void Toggle() => Apply(!IsDark);

    public void Apply(bool dark)
    {
        IsDark = dark;
        var uri = new Uri($"Themes/{(dark ? "Dark" : "Light")}.xaml", UriKind.Relative);
        Application.Current.Resources.MergedDictionaries[0] = new ResourceDictionary { Source = uri };
    }
}
