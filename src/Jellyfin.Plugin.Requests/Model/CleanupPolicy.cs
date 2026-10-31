using Jellyfin.Plugin.Requests.Arr;
using Jellyfin.Plugin.Requests.Seerr;

namespace Jellyfin.Plugin.Requests.Model;

/// <summary>What caused a cleanup evaluation.</summary>
public enum CleanupTrigger
{
    /// <summary>Scheduled or manual full pass.</summary>
    Reconcile,

    /// <summary>Radarr/Sonarr reported the title was deleted.</summary>
    ArrDeleted,

    /// <summary>Jellyfin removed the item from its library (checked after the grace period).</summary>
    JellyfinRemoved,
}

/// <summary>Everything cleanup needs to know about one Seerr media record.</summary>
/// <param name="Status">Seerr availability (4K status when every request is 4K).</param>
/// <param name="RequestStatuses">Status of each request on this media.</param>
/// <param name="LatestApprovalAt">Most recent update time among approved requests.</param>
/// <param name="InJellyfin">Whether Jellyfin has the title; null when unknown.</param>
/// <param name="Arr">Presence in Radarr/Sonarr.</param>
public sealed record MediaFacts(
    SeerrMediaStatus Status,
    IReadOnlyList<SeerrRequestStatus> RequestStatuses,
    DateTime? LatestApprovalAt,
    bool? InJellyfin,
    ArrPresence Arr);

/// <summary>
/// Decides whether a Seerr media record should be deleted because its content is gone.
/// </summary>
public static class CleanupPolicy
{
    /// <summary>How long an approved title may be absent from Radarr/Sonarr before it counts as removed.</summary>
    public static readonly TimeSpan ArrAddGrace = TimeSpan.FromHours(2);

    /// <summary>Evaluates one media record.</summary>
    /// <param name="facts">Current facts.</param>
    /// <param name="trigger">What prompted the check.</param>
    /// <param name="nowUtc">Current time.</param>
    /// <returns>A human-readable reason to delete, or null to keep it.</returns>
    public static string? Evaluate(MediaFacts facts, CleanupTrigger trigger, DateTime nowUtc)
    {
        // Someone is still waiting on approval; deleting the media would delete their request.
        if (facts.RequestStatuses.Contains(SeerrRequestStatus.Pending))
        {
            return null;
        }

        // Only fulfilled-or-in-flight requests can have had content that was later removed.
        var hadContent = facts.RequestStatuses.Any(s => s is SeerrRequestStatus.Approved or SeerrRequestStatus.Completed);
        if (!hadContent)
        {
            return null;
        }

        if (trigger == CleanupTrigger.ArrDeleted)
        {
            return "Deleted from Radarr/Sonarr";
        }

        if (facts.Status == SeerrMediaStatus.Deleted)
        {
            return "Seerr marked the media as deleted";
        }

        if (facts.Status is SeerrMediaStatus.Available or SeerrMediaStatus.PartiallyAvailable)
        {
            // Files still on disk means Jellyfin just hasn't scanned them yet.
            if (facts.InJellyfin == false && facts.Arr != ArrPresence.HasFile)
            {
                return facts.Arr switch
                {
                    ArrPresence.Missing => "Gone from Jellyfin and Radarr/Sonarr",
                    ArrPresence.NoFile => "Gone from Jellyfin; files deleted in Radarr/Sonarr",
                    _ => "Gone from Jellyfin",
                };
            }

            return null;
        }

        // Approved but not yet downloaded: only a removal from Radarr/Sonarr counts.
        if (facts.Arr == ArrPresence.Missing
            && facts.LatestApprovalAt is { } approvedAt
            && nowUtc - approvedAt > ArrAddGrace)
        {
            return "Removed from Radarr/Sonarr before it finished downloading";
        }

        return null;
    }
}
