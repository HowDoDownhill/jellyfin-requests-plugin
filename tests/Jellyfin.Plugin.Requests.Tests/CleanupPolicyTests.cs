using Jellyfin.Plugin.Requests.Arr;
using Jellyfin.Plugin.Requests.Model;
using Jellyfin.Plugin.Requests.Seerr;

namespace Jellyfin.Plugin.Requests.Tests;

public class CleanupPolicyTests
{
    private static readonly DateTime Now = new(2026, 10, 31, 12, 0, 0, DateTimeKind.Utc);

    private static MediaFacts Facts(
        SeerrMediaStatus status = SeerrMediaStatus.Available,
        bool? inJellyfin = false,
        ArrPresence arr = ArrPresence.Missing,
        DateTime? approvedAt = null,
        params SeerrRequestStatus[] requests)
        => new(
            status,
            requests.Length == 0 ? [SeerrRequestStatus.Approved] : requests,
            approvedAt ?? Now.AddDays(-3),
            inJellyfin,
            arr);

    [Fact]
    public void ArrDelete_DeletesApprovedRequest()
        => Assert.NotNull(CleanupPolicy.Evaluate(Facts(inJellyfin: true, arr: ArrPresence.HasFile), CleanupTrigger.ArrDeleted, Now));

    [Fact]
    public void ArrDelete_KeepsMediaWithPendingRequest()
        => Assert.Null(CleanupPolicy.Evaluate(
            Facts(requests: [SeerrRequestStatus.Approved, SeerrRequestStatus.Pending]),
            CleanupTrigger.ArrDeleted,
            Now));

    [Fact]
    public void DeclinedOnlyMedia_IsNeverTouched()
        => Assert.Null(CleanupPolicy.Evaluate(
            Facts(status: SeerrMediaStatus.Deleted, requests: [SeerrRequestStatus.Declined]),
            CleanupTrigger.ArrDeleted,
            Now));

    [Fact]
    public void FailedOnlyMedia_IsNeverTouched()
        => Assert.Null(CleanupPolicy.Evaluate(
            Facts(requests: [SeerrRequestStatus.Failed]),
            CleanupTrigger.Reconcile,
            Now));

    [Fact]
    public void SeerrDeletedStatus_IsCleanedUp()
        => Assert.NotNull(CleanupPolicy.Evaluate(
            Facts(status: SeerrMediaStatus.Deleted, inJellyfin: null, arr: ArrPresence.Unknown),
            CleanupTrigger.Reconcile,
            Now));

    [Theory]
    [InlineData(ArrPresence.Missing)]
    [InlineData(ArrPresence.NoFile)]
    [InlineData(ArrPresence.Unknown)]
    public void Available_GoneFromJellyfin_IsCleanedUp(ArrPresence arr)
        => Assert.NotNull(CleanupPolicy.Evaluate(Facts(arr: arr), CleanupTrigger.JellyfinRemoved, Now));

    [Fact]
    public void Available_GoneFromJellyfin_ButFilesOnDisk_IsKept()
        => Assert.Null(CleanupPolicy.Evaluate(Facts(arr: ArrPresence.HasFile), CleanupTrigger.Reconcile, Now));

    [Fact]
    public void Available_StillInJellyfin_IsKept()
        => Assert.Null(CleanupPolicy.Evaluate(Facts(inJellyfin: true, arr: ArrPresence.Missing), CleanupTrigger.Reconcile, Now));

    [Fact]
    public void Available_JellyfinUnknown_IsKept()
        => Assert.Null(CleanupPolicy.Evaluate(Facts(inJellyfin: null), CleanupTrigger.Reconcile, Now));

    [Fact]
    public void PartiallyAvailable_GoneEverywhere_IsCleanedUp()
        => Assert.NotNull(CleanupPolicy.Evaluate(
            Facts(status: SeerrMediaStatus.PartiallyAvailable),
            CleanupTrigger.Reconcile,
            Now));

    [Fact]
    public void Processing_RemovedFromArr_AfterGrace_IsCleanedUp()
        => Assert.NotNull(CleanupPolicy.Evaluate(
            Facts(status: SeerrMediaStatus.Processing, approvedAt: Now.AddHours(-3)),
            CleanupTrigger.Reconcile,
            Now));

    [Fact]
    public void Processing_JustApproved_IsKept()
        => Assert.Null(CleanupPolicy.Evaluate(
            Facts(status: SeerrMediaStatus.Processing, approvedAt: Now.AddMinutes(-20)),
            CleanupTrigger.Reconcile,
            Now));

    [Theory]
    [InlineData(ArrPresence.NoFile)]
    [InlineData(ArrPresence.HasFile)]
    [InlineData(ArrPresence.Unknown)]
    public void Processing_StillInArr_IsKept(ArrPresence arr)
        => Assert.Null(CleanupPolicy.Evaluate(
            Facts(status: SeerrMediaStatus.Processing, arr: arr),
            CleanupTrigger.Reconcile,
            Now));

    [Fact]
    public void CompletedRequest_CountsAsHavingContent()
        => Assert.NotNull(CleanupPolicy.Evaluate(
            Facts(requests: [SeerrRequestStatus.Completed]),
            CleanupTrigger.Reconcile,
            Now));
}
