using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Jellyfin.Plugin.Requests.Seerr;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Requests.Services;

/// <summary>
/// Title, year and poster for each requested TMDB id. Seerr's request list omits these,
/// so they are fetched once and kept on disk.
/// </summary>
public sealed class MetadataCache
{
    private const int MaxParallelFetches = 4;

    private readonly SeerrClient _seerr;
    private readonly ILogger<MetadataCache> _logger;
    private readonly SemaphoreSlim _saveLock = new(1, 1);
    private ConcurrentDictionary<string, SeerrTitleDetails>? _items;

    /// <summary>
    /// Initializes a new instance of the <see cref="MetadataCache"/> class.
    /// </summary>
    /// <param name="seerr">Seerr client.</param>
    /// <param name="logger">Logger.</param>
    public MetadataCache(SeerrClient seerr, ILogger<MetadataCache> logger)
    {
        _seerr = seerr;
        _logger = logger;
    }

    private static string? FilePath => Plugin.Instance is { } p ? Path.Combine(p.DataFolderPath, "titles.json") : null;

    /// <summary>Returns cached details, if any.</summary>
    /// <param name="mediaType">"movie" or "tv".</param>
    /// <param name="tmdbId">TMDB id.</param>
    /// <returns>Details or null.</returns>
    public SeerrTitleDetails? TryGet(string mediaType, int tmdbId)
        => Items.GetValueOrDefault(Key(mediaType, tmdbId));

    /// <summary>Fetches any titles not yet cached.</summary>
    /// <param name="wanted">Media type and TMDB id pairs.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task.</returns>
    public async Task EnsureAsync(IEnumerable<(string MediaType, int TmdbId)> wanted, CancellationToken cancellationToken)
    {
        var missing = wanted
            .Where(w => w.TmdbId > 0 && !Items.ContainsKey(Key(w.MediaType, w.TmdbId)))
            .Distinct()
            .ToList();

        if (missing.Count == 0)
        {
            return;
        }

        await Parallel.ForEachAsync(
            missing,
            new ParallelOptions { MaxDegreeOfParallelism = MaxParallelFetches, CancellationToken = cancellationToken },
            async (w, ct) =>
            {
                try
                {
                    // A 404 caches an empty record so a bad id is not refetched every refresh.
                    var details = await _seerr.GetTitleAsync(w.MediaType, w.TmdbId, ct).ConfigureAwait(false);
                    Items[Key(w.MediaType, w.TmdbId)] = details ?? new SeerrTitleDetails();
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
                {
                    _logger.LogDebug(ex, "Could not load Seerr details for {Type} {TmdbId}", w.MediaType, w.TmdbId);
                }
            }).ConfigureAwait(false);

        await SaveAsync(cancellationToken).ConfigureAwait(false);
    }

    private ConcurrentDictionary<string, SeerrTitleDetails> Items => _items ??= Load();

    private static string Key(string mediaType, int tmdbId)
        => string.Create(CultureInfo.InvariantCulture, $"{(mediaType == "tv" ? "tv" : "movie")}:{tmdbId}");

    private ConcurrentDictionary<string, SeerrTitleDetails> Load()
    {
        try
        {
            if (FilePath is { } path && File.Exists(path))
            {
                var data = JsonSerializer.Deserialize<Dictionary<string, SeerrTitleDetails>>(File.ReadAllText(path), SeerrClient.JsonOptions);
                if (data is not null)
                {
                    return new ConcurrentDictionary<string, SeerrTitleDetails>(data);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Ignoring unreadable title cache");
        }

        return new ConcurrentDictionary<string, SeerrTitleDetails>();
    }

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        if (FilePath is not { } path)
        {
            return;
        }

        await _saveLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(Items, SeerrClient.JsonOptions), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not save title cache");
        }
        finally
        {
            _saveLock.Release();
        }
    }
}
