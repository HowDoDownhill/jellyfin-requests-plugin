namespace Jellyfin.Plugin.Requests.Model;

/// <summary>One request as the web client renders it.</summary>
public sealed class RequestView
{
    /// <summary>Gets or sets the Seerr request id.</summary>
    public int Id { get; set; }

    /// <summary>Gets or sets the Seerr media id.</summary>
    public int MediaId { get; set; }

    /// <summary>Gets or sets "movie" or "tv".</summary>
    public string Type { get; set; } = "movie";

    /// <summary>Gets or sets a value indicating whether this is a 4K request.</summary>
    public bool Is4k { get; set; }

    /// <summary>Gets or sets the TMDB id.</summary>
    public int TmdbId { get; set; }

    /// <summary>Gets or sets the title.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Gets or sets the release year.</summary>
    public int? Year { get; set; }

    /// <summary>Gets or sets the TMDB poster path (e.g. /abc.jpg).</summary>
    public string? PosterPath { get; set; }

    /// <summary>Gets or sets the Jellyfin item id when the title is in the library.</summary>
    public string? JellyfinItemId { get; set; }

    /// <summary>
    /// Gets or sets the display state: pending, processing, downloading, importing,
    /// partial, available, declined, failed, blocklisted or removed.
    /// </summary>
    public string State { get; set; } = "processing";

    /// <summary>Gets or sets the requested season numbers (TV only).</summary>
    public List<int> Seasons { get; set; } = [];

    /// <summary>Gets or sets live download info, when something is in a download queue.</summary>
    public DownloadView? Download { get; set; }

    /// <summary>Gets or sets who asked for it.</summary>
    public RequesterView RequestedBy { get; set; } = new();

    /// <summary>Gets or sets when the request was made.</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>Gets or sets when the request last changed.</summary>
    public DateTime UpdatedAt { get; set; }
}

/// <summary>Aggregated download progress for one request.</summary>
public sealed class DownloadView
{
    /// <summary>Gets or sets progress from 0 to 1.</summary>
    public double Progress { get; set; }

    /// <summary>Gets or sets the latest ETA across the items.</summary>
    public DateTime? Eta { get; set; }

    /// <summary>Gets or sets the summarized status: downloading, queued, paused, importing, warning or failed.</summary>
    public string Status { get; set; } = "downloading";

    /// <summary>Gets or sets the number of queue items (episodes for TV).</summary>
    public int Items { get; set; }
}

/// <summary>Requesting user.</summary>
public sealed class RequesterView
{
    /// <summary>Gets or sets a stable grouping key: the Jellyfin user id (N format) or "seerr-{id}".</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>Gets or sets the display name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the Jellyfin user id (N format) when linked.</summary>
    public string? JellyfinUserId { get; set; }
}
