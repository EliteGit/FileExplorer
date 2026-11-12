using System.Windows;

namespace FileExplorer.App.Views;

public partial class PromptWindow : Window
{
    public PromptWindow(string title, string label, string initial)
    {
        InitializeComponent();
        Title = title;
        Label.Text = label;
        Input.Text = initial;
        Loaded += (_, _) => { Input.Focus(); Input.SelectAll(); };
    }

    public string Result => Input.Text;

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    public static string? Show(string title, string label, string initial)
    {
        var w = new PromptWindow(title, label, initial) { Owner = Application.Current.Windows.OfType<Window>().FirstOrDefault(x => x.IsActive) };
        return w.ShowDialog() == true ? w.Result : null;
    }
}
