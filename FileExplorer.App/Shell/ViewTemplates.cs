using System.Windows;

namespace FileExplorer.App.Shell;

/// <summary>Lets a feature attach a view to its tab view model without the shell knowing either type.</summary>
public static class ViewTemplates
{
    public static void Register<TViewModel, TView>() where TView : FrameworkElement
    {
        var template = new DataTemplate(typeof(TViewModel)) { VisualTree = new FrameworkElementFactory(typeof(TView)) };
        Application.Current.Resources[template.DataTemplateKey!] = template;
    }
}
