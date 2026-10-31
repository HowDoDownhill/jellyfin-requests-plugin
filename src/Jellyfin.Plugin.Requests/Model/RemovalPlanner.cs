using Jellyfin.Plugin.Requests.Seerr;

namespace Jellyfin.Plugin.Requests.Model;

/// <summary>What removing one request should touch.</summary>
/// <param name="DeleteFromArr">Delete the title (and its files) from Radarr/Sonarr.</param>
/// <param name="DeleteMedia">Delete Seerr's media record, which removes every request on it.</param>
/// <param name="SharedWith">People whose active requests keep the title in Radarr/Sonarr.</param>
public sealed record RemovalPlan(bool DeleteFromArr, bool DeleteMedia, IReadOnlyList<string> SharedWith);

/// <summary>
/// Decides how an admin "Remove" applies. A title is deleted from Radarr/Sonarr only when no one
/// else still has an active request for it (TV seasons from different people share one series).
/// </summary>
public static class RemovalPlanner
{
    /// <summary>Plans the removal of <paramref name="target"/>.</summary>
    /// <param name="target">The request being removed.</param>
    /// <param name="all">Every Seerr request.</param>
    /// <returns>The plan.</returns>
    public static RemovalPlan Plan(SeerrRequest target, IEnumerable<SeerrRequest> all)
    {
        static bool Active(SeerrRequest r)
            => r.RequestStatus is SeerrRequestStatus.Pending or SeerrRequestStatus.Approved or SeerrRequestStatus.Completed;

        var others = all
            .Where(r => r.Id != target.Id && r.Media is not null && r.Media.Id == target.Media?.Id)
            .ToList();

        // 4K and regular versions live in separate Radarr/Sonarr instances.
        var sharing = others.Where(r => r.Is4k == target.Is4k && Active(r)).ToList();

        // Declined and failed requests never reached Radarr/Sonarr.
        var sentToArr = target.RequestStatus is SeerrRequestStatus.Approved or SeerrRequestStatus.Completed;

        return new RemovalPlan(
            DeleteFromArr: sentToArr && sharing.Count == 0,
            DeleteMedia: !others.Any(Active),
            SharedWith: sharing
                .Select(r => r.RequestedBy?.DisplayName ?? r.RequestedBy?.JellyfinUsername ?? "someone")
                .Distinct()
                .ToList());
    }
}
