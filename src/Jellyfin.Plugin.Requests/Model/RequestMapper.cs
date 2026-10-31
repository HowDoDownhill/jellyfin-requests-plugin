using System.Globalization;
using Jellyfin.Plugin.Requests.Arr;
using Jellyfin.Plugin.Requests.Seerr;

namespace Jellyfin.Plugin.Requests.Model;

/// <summary>
/// Pure mapping from Seerr data plus live queue and library state to <see cref="RequestView"/>.
/// </summary>
public static class RequestMapper
{
    /// <summary>Normalizes a Jellyfin user id to lowercase N format so Seerr and Jellyfin ids compare equal.</summary>
    /// <param name="id">Raw id, with or without dashes.</param>
    /// <returns>Normalized id, or null.</returns>
    public static string? NormalizeUserId(string? id)
        => Guid.TryParse(id, out var g) ? g.ToString("N", CultureInfo.InvariantCulture) : null;

    /// <summary>Key used to join queue entries to Seerr media.</summary>
    /// <param name="media">Seerr media.</param>
    /// <returns>Join key, or null when the needed id is missing.</returns>
    public static string? QueueKey(SeerrMedia media)
        => media.MediaType == "tv"
            ? media.TvdbId is > 0 ? QueueKey(ArrKind.Sonarr, media.TvdbId.Value) : null
            : QueueKey(ArrKind.Radarr, media.TmdbId);

    /// <summary>Key used to join queue entries to Seerr media.</summary>
    /// <param name="kind">Queue source.</param>
    /// <param name="externalId">TMDB (Radarr) or TVDB (Sonarr) id.</param>
    /// <returns>Join key.</returns>
    public static string QueueKey(ArrKind kind, int externalId)
        => string.Create(CultureInfo.InvariantCulture, $"{(kind == ArrKind.Radarr ? "movie" : "tv")}:{externalId}");

    /// <summary>Summarizes queue entries for one title into a single progress figure.</summary>
    /// <param name="entries">Queue entries for the title.</param>
    /// <returns>The summary, or null when nothing is queued.</returns>
    public static DownloadView? Summarize(IReadOnlyCollection<ArrQueueEntry> entries)
    {
        if (entries.Count == 0)
        {
            return null;
        }

        var size = entries.Sum(e => e.Size);
        var left = entries.Sum(e => Math.Clamp(e.SizeLeft, 0, e.Size));
        var progress = size > 0 ? Math.Clamp(1 - (left / size), 0, 1) : 0;

        return new DownloadView
        {
            Progress = Math.Round(progress, 3),
            Eta = entries.Max(e => e.EstimatedCompletion),
            Status = SummarizeStatus(entries),
            Items = entries.Count,
        };
    }

    /// <summary>Summarizes Seerr's own download tracking, used when Radarr/Sonarr are not configured.</summary>
    /// <param name="items">Seerr download entries.</param>
    /// <returns>The summary, or null.</returns>
    public static DownloadView? Summarize(IReadOnlyCollection<SeerrDownloadItem>? items)
    {
        if (items is null || items.Count == 0)
        {
            return null;
        }

        return Summarize(items
            .Select(i => new ArrQueueEntry(ArrKind.Radarr, 0, i.Size, i.SizeLeft, i.Status, null, i.EstimatedCompletionTime, null, null))
            .ToList());
    }

    /// <summary>Works out what to show for a request.</summary>
    /// <param name="request">The Seerr request.</param>
    /// <param name="download">Live download summary, if any.</param>
    /// <param name="inJellyfin">Whether Jellyfin's library already has the title.</param>
    /// <returns>Display state.</returns>
    public static string ComputeState(SeerrRequest request, DownloadView? download, bool inJellyfin)
    {
        switch (request.RequestStatus)
        {
            case SeerrRequestStatus.Pending:
                return "pending";
            case SeerrRequestStatus.Declined:
                return "declined";
            case SeerrRequestStatus.Failed:
                return "failed";
        }

        var media = request.Media;
        var status = media is null
            ? SeerrMediaStatus.Unknown
            : (SeerrMediaStatus)(request.Is4k ? media.Status4k : media.Status);

        switch (status)
        {
            case SeerrMediaStatus.Available:
                return "available";
            case SeerrMediaStatus.Deleted:
                return "removed";
            case SeerrMediaStatus.Blocklisted:
                return "blocklisted";
        }

        if (download is not null)
        {
            return download.Status == "importing" ? "importing" : "downloading";
        }

        if (status == SeerrMediaStatus.PartiallyAvailable)
        {
            return "partial";
        }

        // Jellyfin sees a new movie before Seerr's next availability sync runs, so trust it.
        // A series showing up only means some episodes exist, which Seerr reports separately.
        if (inJellyfin && request.Type != "tv")
        {
            return "available";
        }

        return "processing";
    }

    /// <summary>Builds the full view for one request.</summary>
    /// <param name="request">The Seerr request.</param>
    /// <param name="details">Cached title metadata, if known.</param>
    /// <param name="download">Live download summary, if any.</param>
    /// <param name="jellyfinItemId">Jellyfin library item id, if present.</param>
    /// <param name="jellyfinUserName">Resolved Jellyfin user name for the requester, if known.</param>
    /// <returns>The view.</returns>
    public static RequestView ToView(
        SeerrRequest request,
        SeerrTitleDetails? details,
        DownloadView? download,
        string? jellyfinItemId,
        string? jellyfinUserName)
    {
        var media = request.Media ?? new SeerrMedia();
        var type = request.Type ?? media.MediaType ?? "movie";
        var user = request.RequestedBy;
        var jfUserId = NormalizeUserId(user?.JellyfinUserId);
        var date = type == "tv" ? details?.FirstAirDate : details?.ReleaseDate;

        return new RequestView
        {
            Id = request.Id,
            MediaId = media.Id,
            Type = type,
            Is4k = request.Is4k,
            TmdbId = media.TmdbId,
            Title = (type == "tv" ? details?.Name : details?.Title)
                ?? details?.Title
                ?? details?.Name
                ?? string.Create(CultureInfo.InvariantCulture, $"TMDB {media.TmdbId}"),
            Year = date is { Length: >= 4 } && int.TryParse(date.AsSpan(0, 4), NumberStyles.Integer, CultureInfo.InvariantCulture, out var y) ? y : null,
            PosterPath = details?.PosterPath,
            JellyfinItemId = jellyfinItemId,
            State = ComputeState(request, download, jellyfinItemId is not null),
            Seasons = request.Seasons?.Select(s => s.SeasonNumber).Order().ToList() ?? [],
            Download = download,
            RequestedBy = new RequesterView
            {
                Key = jfUserId ?? string.Create(CultureInfo.InvariantCulture, $"seerr-{user?.Id ?? 0}"),
                JellyfinUserId = jfUserId,
                Name = FirstNonEmpty(jellyfinUserName, user?.DisplayName, user?.JellyfinUsername, user?.Username) ?? "Unknown user",
            },
            CreatedAt = request.CreatedAt,
            UpdatedAt = request.UpdatedAt,
        };
    }

    private static string SummarizeStatus(IReadOnlyCollection<ArrQueueEntry> entries)
    {
        static bool Is(string? value, string expected) => string.Equals(value, expected, StringComparison.OrdinalIgnoreCase);

        if (entries.Any(e => Is(e.Status, "downloading")))
        {
            return "downloading";
        }

        if (entries.Any(e => Is(e.TrackedState, "importing") || Is(e.TrackedState, "importPending") || Is(e.Status, "completed")))
        {
            return "importing";
        }

        if (entries.All(e => Is(e.Status, "failed") || Is(e.TrackedState, "failed") || Is(e.TrackedState, "failedPending")))
        {
            return "failed";
        }

        if (entries.Any(e => Is(e.Status, "warning")))
        {
            return "warning";
        }

        if (entries.All(e => Is(e.Status, "paused")))
        {
            return "paused";
        }

        return "queued";
    }

    private static string? FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
}
