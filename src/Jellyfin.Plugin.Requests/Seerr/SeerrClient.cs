using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Jellyfin.Plugin.Requests.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Requests.Seerr;

/// <summary>
/// Thin wrapper over the Seerr v1 API, authenticated with the admin API key.
/// </summary>
public class SeerrClient
{
    /// <summary>Named HttpClient used for every outbound call from this plugin.</summary>
    public const string HttpClientName = "JellyfinRequests";

    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private const int PageSize = 100;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<SeerrClient> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SeerrClient"/> class.
    /// </summary>
    /// <param name="httpClientFactory">HTTP client factory.</param>
    /// <param name="logger">Logger.</param>
    public SeerrClient(IHttpClientFactory httpClientFactory, ILogger<SeerrClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    private static PluginConfiguration Config => Plugin.Instance?.Configuration ?? new PluginConfiguration();

    /// <summary>Gets a value indicating whether Seerr is configured.</summary>
    public static bool IsConfigured => !string.IsNullOrWhiteSpace(Config.SeerrUrl) && !string.IsNullOrWhiteSpace(Config.SeerrApiKey);

    /// <summary>Reads every request, newest first.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>All requests.</returns>
    public async Task<List<SeerrRequest>> GetAllRequestsAsync(CancellationToken cancellationToken)
    {
        var all = new List<SeerrRequest>();
        for (var skip = 0; ; skip += PageSize)
        {
            var path = string.Create(
                CultureInfo.InvariantCulture,
                $"/api/v1/request?take={PageSize}&skip={skip}&filter=all&sort=added&sortDirection=desc");
            var page = await GetAsync<SeerrPage<SeerrRequest>>(path, cancellationToken).ConfigureAwait(false);
            if (page is null || page.Results.Count == 0)
            {
                break;
            }

            all.AddRange(page.Results);
            if (page.Results.Count < PageSize || all.Count >= (page.PageInfo?.Results ?? int.MaxValue))
            {
                break;
            }
        }

        return all;
    }

    /// <summary>Reads title metadata for a movie or series.</summary>
    /// <param name="mediaType">"movie" or "tv".</param>
    /// <param name="tmdbId">TMDB id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The details, or null when Seerr has none.</returns>
    public Task<SeerrTitleDetails?> GetTitleAsync(string mediaType, int tmdbId, CancellationToken cancellationToken)
    {
        var segment = mediaType == "tv" ? "tv" : "movie";
        return GetAsync<SeerrTitleDetails>(
            string.Create(CultureInfo.InvariantCulture, $"/api/v1/{segment}/{tmdbId}"),
            cancellationToken);
    }

    /// <summary>
    /// Deletes a Seerr media record. Its requests go with it and the title becomes requestable again.
    /// This never touches files in Radarr/Sonarr.
    /// </summary>
    /// <param name="mediaId">Seerr media id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task.</returns>
    public Task DeleteMediaAsync(int mediaId, CancellationToken cancellationToken)
        => SendAsync(HttpMethod.Delete, string.Create(CultureInfo.InvariantCulture, $"/api/v1/media/{mediaId}"), cancellationToken);

    /// <summary>
    /// Deletes the title from Radarr/Sonarr <b>including its files</b>, using Seerr's own routing to the
    /// right instance (4K aware). Seerr then marks the media as deleted.
    /// </summary>
    /// <param name="mediaId">Seerr media id.</param>
    /// <param name="is4k">Target the 4K instance.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task.</returns>
    public Task DeleteMediaFilesAsync(int mediaId, bool is4k, CancellationToken cancellationToken)
        => SendAsync(
            HttpMethod.Delete,
            string.Create(CultureInfo.InvariantCulture, $"/api/v1/media/{mediaId}/file?is4k={(is4k ? "true" : "false")}"),
            cancellationToken);

    /// <summary>Deletes a single request.</summary>
    /// <param name="requestId">Request id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task.</returns>
    public Task DeleteRequestAsync(int requestId, CancellationToken cancellationToken)
        => SendAsync(HttpMethod.Delete, string.Create(CultureInfo.InvariantCulture, $"/api/v1/request/{requestId}"), cancellationToken);

    /// <summary>Approves or declines a pending request.</summary>
    /// <param name="requestId">Request id.</param>
    /// <param name="approve">True to approve, false to decline.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task.</returns>
    public Task SetRequestApprovalAsync(int requestId, bool approve, CancellationToken cancellationToken)
        => SendAsync(
            HttpMethod.Post,
            string.Create(CultureInfo.InvariantCulture, $"/api/v1/request/{requestId}/{(approve ? "approve" : "decline")}"),
            cancellationToken);

    /// <summary>Checks the URL and API key by reading one request.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Null on success, otherwise an error message.</returns>
    public async Task<string?> TestAsync(CancellationToken cancellationToken)
    {
        try
        {
            await GetAsync<SeerrPage<SeerrRequest>>("/api/v1/request?take=1", cancellationToken).ConfigureAwait(false);
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException or JsonException)
        {
            return ex.Message;
        }
    }

    private async Task<T?> GetAsync<T>(string path, CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Get, path);
        using var response = await _httpClientFactory.CreateClient(HttpClientName)
            .SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return default;
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken).ConfigureAwait(false);
    }

    private async Task SendAsync(HttpMethod method, string path, CancellationToken cancellationToken)
    {
        using var request = CreateRequest(method, path);
        using var response = await _httpClientFactory.CreateClient(HttpClientName)
            .SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogWarning("Seerr {Method} {Path} returned {Status}: {Body}", method, path, (int)response.StatusCode, body);
            response.EnsureSuccessStatusCode();
        }
    }

    private static HttpRequestMessage CreateRequest(HttpMethod method, string path)
    {
        var config = Config;
        if (string.IsNullOrWhiteSpace(config.SeerrUrl) || string.IsNullOrWhiteSpace(config.SeerrApiKey))
        {
            throw new InvalidOperationException("Seerr URL and API key are not configured.");
        }

        var request = new HttpRequestMessage(method, config.SeerrUrl.TrimEnd('/') + path);
        request.Headers.Add("X-Api-Key", config.SeerrApiKey);
        request.Headers.Accept.ParseAdd("application/json");
        return request;
    }
}
