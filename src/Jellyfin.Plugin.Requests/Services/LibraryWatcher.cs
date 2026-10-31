using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Hosting;

namespace Jellyfin.Plugin.Requests.Services;

/// <summary>
/// Listens to Jellyfin library changes directly, so new content shows as available right away
/// and removed content can be cleaned up without any webhook.
/// </summary>
public sealed class LibraryWatcher : IHostedService
{
    private readonly ILibraryManager _libraryManager;
    private readonly LibraryIndex _index;
    private readonly RequestStore _store;
    private readonly CleanupService _cleanup;

    /// <summary>
    /// Initializes a new instance of the <see cref="LibraryWatcher"/> class.
    /// </summary>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="index">Library index.</param>
    /// <param name="store">Request store.</param>
    /// <param name="cleanup">Cleanup service.</param>
    public LibraryWatcher(ILibraryManager libraryManager, LibraryIndex index, RequestStore store, CleanupService cleanup)
    {
        _libraryManager = libraryManager;
        _index = index;
        _store = store;
        _cleanup = cleanup;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded += OnItemAdded;
        _libraryManager.ItemRemoved += OnItemRemoved;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded -= OnItemAdded;
        _libraryManager.ItemRemoved -= OnItemRemoved;
        return Task.CompletedTask;
    }

    private void OnItemAdded(object? sender, ItemChangeEventArgs e)
    {
        if (e.Item is Movie or Series or Episode)
        {
            _index.Invalidate();

            // Pick up Seerr's availability change too, which follows shortly after its own scan.
            _store.RequestRefresh(e.Item is Episode ? RefreshParts.Queue : RefreshParts.All);
        }
    }

    private void OnItemRemoved(object? sender, ItemChangeEventArgs e)
    {
        if (e.Item is not (Movie or Series))
        {
            return;
        }

        _index.Invalidate();
        _store.RequestRefresh(RefreshParts.None);
        _cleanup.OnJellyfinItemRemoved(
            e.Item is Series,
            LibraryIndex.ParseId(e.Item.GetProviderId(MetadataProvider.Tmdb)),
            LibraryIndex.ParseId(e.Item.GetProviderId(MetadataProvider.Tvdb)));
    }
}
