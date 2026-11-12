using FileExplorer.App.Shell;
using FileExplorer.Core.Common;
using FileExplorer.Core.Search;
using FileExplorer.Core.Tags;

namespace FileExplorer.App.Features.Tags;

/// <summary>
/// The only place Tags touches Phase 2: registers the <c>tag:</c> search term. Delete this file and its registration line
/// together with Phase 2; without it search simply has no tag: filter.
/// </summary>
public sealed class TagSearchIntegration : IFeature
{
    private readonly ITagStore _store;
    private readonly QueryTermProviders _providers;

    public TagSearchIntegration(ITagStore store, QueryTermProviders providers) { _store = store; _providers = providers; }

    public string Name => "Tag search";

    public void Register(FeatureRegistry registry) => _providers.Add(new TagTerm(_store));

    private sealed class TagTerm(ITagStore store) : IQueryTermProvider
    {
        public string Prefix => "tag";

        public Result<IRowPredicate> Create(string value)
        {
            if (value.Length == 0) return new AppError("tag: needs a tag name, e.g. tag:work");
            var paths = store.PathsWithTag(value);
            return paths.IsOk ? Result<IRowPredicate>.Ok(new PathInSet(paths.Value)) : Result<IRowPredicate>.Err(paths.Error);
        }
    }
}
