using FileExplorer.App.Shell;

namespace FileExplorer.App.Features.Preview;

/// <summary>Preview panel at the right of every folder tab. Provider-based; unsupported types show an explicit "no preview" state.</summary>
public sealed class PreviewFeature : IFeature
{
    private readonly PreviewRegistry _registry = new();
    private readonly ErrorReporter _errors;

    public PreviewFeature(ErrorReporter errors)
    {
        _errors = errors;
        _registry.Add(new TextPreviewProvider());
        _registry.Add(new ImagePreviewProvider());
        _registry.Add(new PdfPreviewProvider());
    }

    public string Name => "Preview";

    public void Register(FeatureRegistry registry) =>
        registry.AddRightPanel(window => new PreviewPanelView(new PreviewPanelViewModel(window, _registry), _errors));
}
