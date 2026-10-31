using Jellyfin.Plugin.Requests.Model;
using Jellyfin.Plugin.Requests.Seerr;

namespace Jellyfin.Plugin.Requests.Tests;

public class RemovalPlannerTests
{
    private static SeerrRequest Req(int id, int mediaId, SeerrRequestStatus status, string who = "Sam", bool is4k = false)
        => new()
        {
            Id = id,
            Status = (int)status,
            Is4k = is4k,
            Media = new SeerrMedia { Id = mediaId },
            RequestedBy = new SeerrUser { DisplayName = who },
        };

    [Fact]
    public void OnlyRequest_DeletesFromArrAndMedia()
    {
        var target = Req(1, 10, SeerrRequestStatus.Approved);
        var plan = RemovalPlanner.Plan(target, [target, Req(2, 99, SeerrRequestStatus.Approved)]);

        Assert.True(plan.DeleteFromArr);
        Assert.True(plan.DeleteMedia);
        Assert.Empty(plan.SharedWith);
    }

    [Fact]
    public void CompletedRequest_DeletesFromArr()
        => Assert.True(RemovalPlanner.Plan(Req(1, 10, SeerrRequestStatus.Completed), []).DeleteFromArr);

    [Theory]
    [InlineData(SeerrRequestStatus.Approved)]
    [InlineData(SeerrRequestStatus.Pending)]
    [InlineData(SeerrRequestStatus.Completed)]
    public void SharedTitle_StaysInArr(SeerrRequestStatus otherStatus)
    {
        var target = Req(1, 10, SeerrRequestStatus.Approved, "Sam");
        var plan = RemovalPlanner.Plan(target, [target, Req(2, 10, otherStatus, "Alex")]);

        Assert.False(plan.DeleteFromArr);
        Assert.False(plan.DeleteMedia);
        Assert.Equal(["Alex"], plan.SharedWith);
    }

    [Fact]
    public void DeclinedOthers_DoNotKeepItInArr()
    {
        var target = Req(1, 10, SeerrRequestStatus.Approved);
        var plan = RemovalPlanner.Plan(target, [target, Req(2, 10, SeerrRequestStatus.Declined, "Alex")]);

        Assert.True(plan.DeleteFromArr);
        Assert.True(plan.DeleteMedia);
    }

    [Fact]
    public void FourKRequest_OnlyBlocksTheFourKInstance()
    {
        var target = Req(1, 10, SeerrRequestStatus.Approved);
        var plan = RemovalPlanner.Plan(target, [target, Req(2, 10, SeerrRequestStatus.Approved, "Alex", is4k: true)]);

        // Regular copy goes; the 4K request keeps the Seerr media record alive.
        Assert.True(plan.DeleteFromArr);
        Assert.False(plan.DeleteMedia);
    }

    [Theory]
    [InlineData(SeerrRequestStatus.Declined)]
    [InlineData(SeerrRequestStatus.Failed)]
    public void NeverSentToArr_LeavesArrAlone(SeerrRequestStatus status)
        => Assert.False(RemovalPlanner.Plan(Req(1, 10, status), []).DeleteFromArr);
}
