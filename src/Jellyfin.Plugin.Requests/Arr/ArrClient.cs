using System.Net.Http.Json;
using System.Text.Json;
using Jellyfin.Plugin.Requests.Seerr;

namespace Jellyfin.Plugin.Requests.Arr;

/// <summary>Which *arr application a call targets.</summary>
public enum ArrKind
{
    /// <summary>Radarr (movies, keyed by TMDB id).</summary>
    Radarr,

    /// <summary>Sonarr (series, keyed by TVDB id).</summary>
    Sonarr,
}

/// <summary>One download in an *arr queue, normalized across Radarr and Sonarr.</summary>
/// <param name="Kind">Source application.</param>
/// <param name="ExternalId">TMDB id for Radarr, TVDB id for Sonarr.</param>
/// <param name="Size">Total bytes.</param>
/// <param name="SizeLeft">Remaining bytes.</param>
/// <param name="Status">Download client status (downloading, paused, queued, completed, warning...).</param>
/// <param name="TrackedState">*arr tracked state (downloading, importPending, importing, failedPending...).</param>
/// <param name="EstimatedCompletion">ETA, when known.</param>
/// <param name="SeasonNumber">Season for Sonarr entries.</param>
/// <param name="EpisodeNumber">Episode for Sonarr entries.</param>
public sealed record ArrQueueEntry(
    ArrKind Kind,
    int ExternalId,
    double Size,
    double SizeLeft,
    string? Status,
    string? TrackedState,
    DateTime? EstimatedCompletion,
    int? SeasonNumber,
    int? EpisodeNumber);

/// <summary>Whether an *arr instance still holds a title and has files for it.</summary>
public enum ArrPresence
{
    /// <summary>Not checked (instance not configured or unreachable).</summary>
    Unknown,

    /// <summary>The title is no longer in the *arr library.</summary>
    Missing,

    /// <summary>In the library with no files on disk.</summary>
    NoFile,

    /// <summary>In the library with files on disk.</summary>
    HasFile,
}

/// <summary>
/// Minimal Radarr/Sonarr v3 API client: download queue, library presence and a connection test.
/// </summary>
public class ArrClient
{
    private readonly IHttpClientFactory _httpClientFactory;

    /// <summary>
    /// Initializes a new instance of the <see cref="ArrClient"/> class.
    /// </summary>
    /// <param name="httpClientFactory">HTTP client factory.</param>
    public ArrClient(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    /// <summary>Gets a value indicating whether the given app has a URL and key configured.</summary>
    /// <param name="kind">Which app.</param>
    /// <returns>True when configured.</returns>
    public static bool IsConfigured(ArrKind kind)
    {
        var (url, key) = Settings(kind);
        return !string.IsNullOrWhiteSpace(url) && !string.IsNullOrWhiteSpace(key);
    }

    /// <summary>Reads the full download queue.</summary>
    /// <param name="kind">Which app.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Queue entries; empty when not configured.</returns>
    public async Task<List<ArrQueueEntry>> GetQueueAsync(ArrKind kind, CancellationToken cancellationToken)
    {
        if (!IsConfigured(kind))
        {
            return [];
        }

        var path = kind == ArrKind.Radarr
            ? "/api/v3/queue?page=1&pageSize=1000&includeMovie=true"
            : "/api/v3/queue?page=1&pageSize=1000&includeSeries=true&includeEpisode=true";

        var page = await GetAsync<QueuePage>(kind, path, cancellationToken).ConfigureAwait(false);
        var result = new List<ArrQueueEntry>();
        foreach (var r in page?.Records ?? [])
        {
            var externalId = kind == ArrKind.Radarr ? r.Movie?.TmdbId : r.Series?.TvdbId;
            if (externalId is null or 0)
            {
                continue;
            }

            result.Add(new ArrQueueEntry(
                kind,
                externalId.Value,
                r.Size,
                r.SizeLeft,
                r.Status,
                r.TrackedDownloadState,
                r.EstimatedCompletionTime,
                r.Episode?.SeasonNumber,
                r.Episode?.EpisodeNumber));
        }

        return result;
    }

    /// <summary>
    /// Reads the whole library and reports presence per external id
    /// (TMDB for Radarr, TVDB for Sonarr). Returns null when not configured.
    /// </summary>
    /// <param name="kind">Which app.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Map of external id to presence, or null.</returns>
    public async Task<Dictionary<int, ArrPresence>?> GetLibraryAsync(ArrKind kind, CancellationToken cancellationToken)
    {
        if (!IsConfigured(kind))
        {
            return null;
        }

        var map = new Dictionary<int, ArrPresence>();
        if (kind == ArrKind.Radarr)
        {
            var movies = await GetAsync<List<LibraryMovie>>(kind, "/api/v3/movie", cancellationToken).ConfigureAwait(false);
            foreach (var m in movies ?? [])
            {
                map[m.TmdbId] = m.HasFile ? ArrPresence.HasFile : ArrPresence.NoFile;
            }
        }
        else
        {
            var series = await GetAsync<List<LibrarySeries>>(kind, "/api/v3/series", cancellationToken).ConfigureAwait(false);
            foreach (var s in series ?? [])
            {
                map[s.TvdbId] = (s.Statistics?.EpisodeFileCount ?? 0) > 0 ? ArrPresence.HasFile : ArrPresence.NoFile;
            }
        }

        return map;
    }

    /// <summary>Checks URL and API key.</summary>
    /// <param name="kind">Which app.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Null on success, otherwise an error message.</returns>
    public async Task<string?> TestAsync(ArrKind kind, CancellationToken cancellationToken)
    {
        if (!IsConfigured(kind))
        {
            return "Not configured.";
        }

        try
        {
            await GetAsync<JsonElement>(kind, "/api/v3/system/status", cancellationToken).ConfigureAwait(false);
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return ex.Message;
        }
    }

    private static (string Url, string Key) Settings(ArrKind kind)
    {
        var c = Plugin.Instance?.Configuration;
        return kind == ArrKind.Radarr
            ? (c?.RadarrUrl ?? string.Empty, c?.RadarrApiKey ?? string.Empty)
            : (c?.SonarrUrl ?? string.Empty, c?.SonarrApiKey ?? string.Empty);
    }

    private async Task<T?> GetAsync<T>(ArrKind kind, string path, CancellationToken cancellationToken)
    {
        var (url, key) = Settings(kind);
        using var request = new HttpRequestMessage(HttpMethod.Get, url.TrimEnd('/') + path);
        request.Headers.Add("X-Api-Key", key);
        using var response = await _httpClientFactory.CreateClient(SeerrClient.HttpClientName)
            .SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(SeerrClient.JsonOptions, cancellationToken).ConfigureAwait(false);
    }

    private sealed class QueuePage
    {
        public List<QueueRecord> Records { get; set; } = [];
    }

    private sealed class QueueRecord
    {
        public double Size { get; set; }

        public double SizeLeft { get; set; }

        public string? Status { get; set; }

        public string? TrackedDownloadState { get; set; }

        public DateTime? EstimatedCompletionTime { get; set; }

        public IdHolder? Movie { get; set; }

        public IdHolder? Series { get; set; }

        public EpisodeRef? Episode { get; set; }
    }

    private sealed class IdHolder
    {
        public int TmdbId { get; set; }

        public int TvdbId { get; set; }
    }

    private sealed class EpisodeRef
    {
        public int SeasonNumber { get; set; }

        public int EpisodeNumber { get; set; }
    }

    private sealed class LibraryMovie
    {
        public int TmdbId { get; set; }

        public bool HasFile { get; set; }
    }

    private sealed class LibrarySeries
    {
        public int TvdbId { get; set; }

        public SeriesStatistics? Statistics { get; set; }
    }

    private sealed class SeriesStatistics
    {
        public int EpisodeFileCount { get; set; }
    }
}
