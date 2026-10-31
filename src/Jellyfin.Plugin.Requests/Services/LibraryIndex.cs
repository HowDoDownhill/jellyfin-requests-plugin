using System.Globalization;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Requests.Seerr;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.Requests.Services;

/// <summary>
/// Lookup of Jellyfin movies and series by TMDB/TVDB id, rebuilt lazily after library changes.
/// </summary>
public sealed class LibraryIndex
{
    private static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan MinRebuildInterval = TimeSpan.FromSeconds(5);

    private readonly ILibraryManager _libraryManager;
    private readonly Lock _lock = new();
    private Snapshot? _snapshot;
    private bool _dirty = true;

    /// <summary>
    /// Initializes a new instance of the <see cref="LibraryIndex"/> class.
    /// </summary>
    /// <param name="libraryManager">Library manager.</param>
    public LibraryIndex(ILibraryManager libraryManager)
    {
        _libraryManager = libraryManager;
    }

    /// <summary>Marks the index stale. The next read rebuilds it.</summary>
    public void Invalidate() => _dirty = true;

    /// <summary>Finds the Jellyfin item id for a Seerr media record.</summary>
    /// <param name="media">Seerr media.</param>
    /// <param name="forceFresh">Rebuild now even if rebuilt seconds ago.</param>
    /// <returns>The item id (N format), or null when absent.</returns>
    public string? Find(SeerrMedia media, bool forceFresh = false)
    {
        var s = Get(forceFresh);
        if (media.MediaType == "tv")
        {
            if (media.TvdbId is > 0 && s.SeriesByTvdb.TryGetValue(media.TvdbId.Value, out var byTvdb))
            {
                return byTvdb;
            }

            return s.SeriesByTmdb.GetValueOrDefault(media.TmdbId);
        }

        return s.MoviesByTmdb.GetValueOrDefault(media.TmdbId);
    }

    private Snapshot Get(bool forceFresh)
    {
        lock (_lock)
        {
            var now = DateTime.UtcNow;
            var stale = _snapshot is null
                || now - _snapshot.BuiltAt > MaxAge
                || (_dirty && (forceFresh || now - _snapshot.BuiltAt > MinRebuildInterval));

            if (stale)
            {
                _dirty = false;
                _snapshot = Build();
            }

            return _snapshot!;
        }
    }

    private Snapshot Build()
    {
        var items = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Series],
            Recursive = true,
            IsVirtualItem = false,
        });

        var snapshot = new Snapshot(DateTime.UtcNow);
        foreach (var item in items)
        {
            var id = item.Id.ToString("N", CultureInfo.InvariantCulture);
            var tmdb = ParseId(item.GetProviderId(MetadataProvider.Tmdb));
            if (item.GetBaseItemKind() == BaseItemKind.Movie)
            {
                if (tmdb is not null)
                {
                    snapshot.MoviesByTmdb.TryAdd(tmdb.Value, id);
                }
            }
            else
            {
                if (tmdb is not null)
                {
                    snapshot.SeriesByTmdb.TryAdd(tmdb.Value, id);
                }

                var tvdb = ParseId(item.GetProviderId(MetadataProvider.Tvdb));
                if (tvdb is not null)
                {
                    snapshot.SeriesByTvdb.TryAdd(tvdb.Value, id);
                }
            }
        }

        return snapshot;
    }

    internal static int? ParseId(string? value)
        => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) && id > 0 ? id : null;

    private sealed record Snapshot(DateTime BuiltAt)
    {
        public Dictionary<int, string> MoviesByTmdb { get; } = [];

        public Dictionary<int, string> SeriesByTmdb { get; } = [];

        public Dictionary<int, string> SeriesByTvdb { get; } = [];
    }
}
