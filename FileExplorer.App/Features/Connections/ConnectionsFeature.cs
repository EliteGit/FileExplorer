using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FileExplorer.App.Features.Tools;
using FileExplorer.App.Shell;
using FileExplorer.App.Tabs;
using FileExplorer.Core.Index;

namespace FileExplorer.App.Features.Connections;

public partial class ConnectionsTabView : UserControl
{
    public ConnectionsTabView() => InitializeComponent();

    private ConnectionsTabViewModel? Vm => DataContext as ConnectionsTabViewModel;

    private void Row_Click(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2 && ((FrameworkElement)sender).DataContext is ConnectionNodeViewModel n) Vm?.RevealCommand.Execute(n);
    }

    private void Row_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not ConnectionNodeViewModel n) return;
        var menu = new ContextMenu();
        void Add(string header, Action run) { var i = new MenuItem { Header = header }; i.Click += (_, _) => run(); menu.Items.Add(i); }
        Add("Reveal in Explorer", () => Vm?.RevealCommand.Execute(n));
        Add("Open", () => Vm?.OpenCommand.Execute(n));
        Add("Copy path", () => Clipboard.SetText(n.FullPath));
        Add("Show only this file's connections", () => Vm?.FocusOnCommand.Execute(n));
        menu.PlacementTarget = (UIElement)sender;
        menu.IsOpen = true;
        e.Handled = true;
    }
}

/// <summary>
/// Connections tool: scans every fixed drive and shows a tree of what points at what (shortcuts and links, DLL dependencies, paths written
/// inside config and script files), with a filter for connections that cross drives. Reuses the search index when one exists.
/// </summary>
public sealed class ConnectionsFeature : IFeature
{
    private readonly ToolServices _s;
    private readonly IndexService? _indexes;

    public ConnectionsFeature(ToolServices services, IndexService? indexes = null) { _s = services; _indexes = indexes; }
    public string Name => "Connections";

    public void Register(FeatureRegistry registry)
    {
        ViewTemplates.Register<ConnectionsTabViewModel, ConnectionsTabView>();
        registry.AddTabType(new TabType(ConnectionsTabViewModel.Type, "Connections", () => new ConnectionsTabViewModel(_s, _indexes), _ => new ConnectionsTabViewModel(_s, _indexes)));
        void Open(MainViewModel w) => w.OpenTool(new ConnectionsTabViewModel(_s, _indexes));
        registry.AddCommand(new FeatureCommand("Connections between files", null, Open));
        registry.AddSidebarEntry(new SidebarEntry("TOOLS", "⛓ Connections", Open));
    }
}
