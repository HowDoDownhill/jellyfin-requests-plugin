namespace Jellyfin.Plugin.Requests.Seerr;

/// <summary>Seerr request status (server/constants/media.ts).</summary>
public enum SeerrRequestStatus
{
    /// <summary>Waiting for approval.</summary>
    Pending = 1,

    /// <summary>Approved and sent to Radarr/Sonarr.</summary>
    Approved = 2,

    /// <summary>Declined by an admin.</summary>
    Declined = 3,

    /// <summary>Sending to Radarr/Sonarr failed.</summary>
    Failed = 4,

    /// <summary>Fulfilled.</summary>
    Completed = 5,
}

/// <summary>Seerr media availability (server/constants/media.ts).</summary>
public enum SeerrMediaStatus
{
    /// <summary>No information.</summary>
    Unknown = 1,

    /// <summary>Requested, awaiting approval.</summary>
    Pending = 2,

    /// <summary>Approved and being fetched.</summary>
    Processing = 3,

    /// <summary>Some seasons/episodes available.</summary>
    PartiallyAvailable = 4,

    /// <summary>Fully available in Jellyfin.</summary>
    Available = 5,

    /// <summary>Blocklisted from requests.</summary>
    Blocklisted = 6,

    /// <summary>Seerr saw the media disappear from the server.</summary>
    Deleted = 7,
}

/// <summary>Paged list wrapper used by Seerr list endpoints.</summary>
/// <typeparam name="T">Item type.</typeparam>
public class SeerrPage<T>
{
    /// <summary>Gets or sets the paging info.</summary>
    public SeerrPageInfo? PageInfo { get; set; }

    /// <summary>Gets or sets the page results.</summary>
    public List<T> Results { get; set; } = [];
}

/// <summary>Seerr paging info.</summary>
public class SeerrPageInfo
{
    /// <summary>Gets or sets the total page count.</summary>
    public int Pages { get; set; }

    /// <summary>Gets or sets the total result count.</summary>
    public int Results { get; set; }
}

/// <summary>A Seerr media request.</summary>
public class SeerrRequest
{
    /// <summary>Gets or sets the request id.</summary>
    public int Id { get; set; }

    /// <summary>Gets or sets the raw request status.</summary>
    public int Status { get; set; }

    /// <summary>Gets or sets the media the request is for.</summary>
    public SeerrMedia? Media { get; set; }

    /// <summary>Gets or sets when the request was made.</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>Gets or sets when the request last changed.</summary>
    public DateTime UpdatedAt { get; set; }

    /// <summary>Gets or sets the requesting user.</summary>
    public SeerrUser? RequestedBy { get; set; }

    /// <summary>Gets or sets a value indicating whether this is a 4K request.</summary>
    public bool Is4k { get; set; }

    /// <summary>Gets or sets the media type ("movie" or "tv").</summary>
    public string? Type { get; set; }

    /// <summary>Gets or sets the requested seasons (TV only).</summary>
    public List<SeerrSeasonRequest>? Seasons { get; set; }

    /// <summary>Gets the typed request status.</summary>
    public SeerrRequestStatus RequestStatus => (SeerrRequestStatus)Status;
}

/// <summary>A season inside a TV request.</summary>
public class SeerrSeasonRequest
{
    /// <summary>Gets or sets the season number.</summary>
    public int SeasonNumber { get; set; }

    /// <summary>Gets or sets the season request status.</summary>
    public int Status { get; set; }
}

/// <summary>Seerr media record.</summary>
public class SeerrMedia
{
    /// <summary>Gets or sets the Seerr media id.</summary>
    public int Id { get; set; }

    /// <summary>Gets or sets the media type.</summary>
    public string? MediaType { get; set; }

    /// <summary>Gets or sets the TMDB id.</summary>
    public int TmdbId { get; set; }

    /// <summary>Gets or sets the TVDB id (TV only).</summary>
    public int? TvdbId { get; set; }

    /// <summary>Gets or sets the raw availability status.</summary>
    public int Status { get; set; }

    /// <summary>Gets or sets the raw 4K availability status.</summary>
    public int Status4k { get; set; }

    /// <summary>Gets or sets the Radarr/Sonarr id Seerr recorded.</summary>
    public int? ExternalServiceId { get; set; }

    /// <summary>Gets or sets the Jellyfin item id Seerr recorded.</summary>
    public string? JellyfinMediaId { get; set; }

    /// <summary>Gets or sets Seerr's own view of active downloads.</summary>
    public List<SeerrDownloadItem>? DownloadStatus { get; set; }

    /// <summary>Gets the typed availability status.</summary>
    public SeerrMediaStatus MediaStatus => (SeerrMediaStatus)Status;
}

/// <summary>Download entry tracked by Seerr.</summary>
public class SeerrDownloadItem
{
    /// <summary>Gets or sets the total size in bytes.</summary>
    public long Size { get; set; }

    /// <summary>Gets or sets the remaining bytes.</summary>
    public long SizeLeft { get; set; }

    /// <summary>Gets or sets the download client status.</summary>
    public string? Status { get; set; }

    /// <summary>Gets or sets the estimated completion time.</summary>
    public DateTime? EstimatedCompletionTime { get; set; }
}

/// <summary>Seerr user.</summary>
public class SeerrUser
{
    /// <summary>Gets or sets the Seerr user id.</summary>
    public int Id { get; set; }

    /// <summary>Gets or sets the display name.</summary>
    public string? DisplayName { get; set; }

    /// <summary>Gets or sets the linked Jellyfin user id.</summary>
    public string? JellyfinUserId { get; set; }

    /// <summary>Gets or sets the linked Jellyfin username.</summary>
    public string? JellyfinUsername { get; set; }

    /// <summary>Gets or sets the local username.</summary>
    public string? Username { get; set; }
}

/// <summary>Subset of Seerr's /movie/{id} and /tv/{id} responses.</summary>
public class SeerrTitleDetails
{
    /// <summary>Gets or sets the movie title.</summary>
    public string? Title { get; set; }

    /// <summary>Gets or sets the series name.</summary>
    public string? Name { get; set; }

    /// <summary>Gets or sets the movie release date.</summary>
    public string? ReleaseDate { get; set; }

    /// <summary>Gets or sets the series first air date.</summary>
    public string? FirstAirDate { get; set; }

    /// <summary>Gets or sets the TMDB poster path.</summary>
    public string? PosterPath { get; set; }

    /// <summary>Gets or sets the TMDB backdrop path.</summary>
    public string? BackdropPath { get; set; }
}
