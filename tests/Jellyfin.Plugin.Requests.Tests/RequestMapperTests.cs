using Jellyfin.Plugin.Requests.Arr;
using Jellyfin.Plugin.Requests.Model;
using Jellyfin.Plugin.Requests.Seerr;

namespace Jellyfin.Plugin.Requests.Tests;

public class RequestMapperTests
{
    private static SeerrRequest Request(
        SeerrRequestStatus status = SeerrRequestStatus.Approved,
        SeerrMediaStatus media = SeerrMediaStatus.Processing,
        string type = "movie",
        bool is4k = false,
        SeerrMediaStatus media4k = SeerrMediaStatus.Unknown)
        => new()
        {
            Id = 1,
            Status = (int)status,
            Type = type,
            Is4k = is4k,
            Media = new SeerrMedia { Id = 10, TmdbId = 550, TvdbId = type == "tv" ? 81189 : null, MediaType = type, Status = (int)media, Status4k = (int)media4k },
            RequestedBy = new SeerrUser { Id = 3, DisplayName = "Seerr Name", JellyfinUserId = "0F8FAD5B-D9CB-469F-A165-70867728950E" },
            CreatedAt = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc),
        };

    private static ArrQueueEntry Entry(double size, double left, string status = "downloading", string? tracked = "downloading")
        => new(ArrKind.Radarr, 550, size, left, status, tracked, new DateTime(2026, 10, 31, 13, 0, 0, DateTimeKind.Utc), null, null);

    [Theory]
    [InlineData(SeerrRequestStatus.Pending, "pending")]
    [InlineData(SeerrRequestStatus.Declined, "declined")]
    [InlineData(SeerrRequestStatus.Failed, "failed")]
    public void RequestStatus_WinsOverMedia(SeerrRequestStatus status, string expected)
        => Assert.Equal(expected, RequestMapper.ComputeState(Request(status, SeerrMediaStatus.Available), null, false));

    [Theory]
    [InlineData(SeerrMediaStatus.Available, "available")]
    [InlineData(SeerrMediaStatus.Deleted, "removed")]
    [InlineData(SeerrMediaStatus.Blocklisted, "blocklisted")]
    [InlineData(SeerrMediaStatus.PartiallyAvailable, "partial")]
    [InlineData(SeerrMediaStatus.Processing, "processing")]
    public void MediaStatus_Maps(SeerrMediaStatus media, string expected)
        => Assert.Equal(expected, RequestMapper.ComputeState(Request(media: media), null, false));

    [Fact]
    public void FourK_UsesFourKStatus()
        => Assert.Equal("processing", RequestMapper.ComputeState(
            Request(media: SeerrMediaStatus.Available, is4k: true, media4k: SeerrMediaStatus.Processing), null, false));

    [Fact]
    public void ActiveDownload_ShowsDownloading()
    {
        var dl = RequestMapper.Summarize([Entry(100, 40)]);
        Assert.Equal("downloading", RequestMapper.ComputeState(Request(), dl, false));
    }

    [Fact]
    public void ImportingDownload_ShowsImporting()
    {
        var dl = RequestMapper.Summarize([Entry(100, 0, "completed", "importPending")]);
        Assert.Equal("importing", RequestMapper.ComputeState(Request(), dl, false));
    }

    [Fact]
    public void MovieInJellyfin_BeforeSeerrSync_ShowsAvailable()
        => Assert.Equal("available", RequestMapper.ComputeState(Request(), null, inJellyfin: true));

    [Fact]
    public void SeriesInJellyfin_StaysOnSeerrStatus()
        => Assert.Equal("processing", RequestMapper.ComputeState(Request(type: "tv"), null, inJellyfin: true));

    [Fact]
    public void Summarize_AggregatesProgressAcrossEpisodes()
    {
        var dl = RequestMapper.Summarize([Entry(100, 50), Entry(300, 50)]);
        Assert.NotNull(dl);
        Assert.Equal(0.75, dl!.Progress);
        Assert.Equal(2, dl.Items);
        Assert.Equal("downloading", dl.Status);
    }

    [Fact]
    public void Summarize_AllPaused_IsPaused()
        => Assert.Equal("paused", RequestMapper.Summarize([Entry(100, 50, "paused"), Entry(100, 50, "paused")])!.Status);

    [Fact]
    public void Summarize_ZeroSize_DoesNotDivideByZero()
        => Assert.Equal(0, RequestMapper.Summarize([Entry(0, 0, "queued", null)])!.Progress);

    [Fact]
    public void Summarize_Empty_IsNull()
        => Assert.Null(RequestMapper.Summarize(Array.Empty<ArrQueueEntry>()));

    [Fact]
    public void ToView_NormalizesUserIdAndPrefersJellyfinName()
    {
        var view = RequestMapper.ToView(
            Request(type: "tv"),
            new SeerrTitleDetails { Name = "Show", FirstAirDate = "2008-01-20", PosterPath = "/p.jpg" },
            null,
            null,
            "jfname");

        Assert.Equal("0f8fad5bd9cb469fa16570867728950e", view.RequestedBy.Key);
        Assert.Equal("0f8fad5bd9cb469fa16570867728950e", view.RequestedBy.JellyfinUserId);
        Assert.Equal("jfname", view.RequestedBy.Name);
        Assert.Equal("Show", view.Title);
        Assert.Equal(2008, view.Year);
    }

    [Fact]
    public void ToView_WithoutLinkedJellyfinUser_GroupsBySeerrId()
    {
        var request = Request();
        request.RequestedBy!.JellyfinUserId = null;
        var view = RequestMapper.ToView(request, null, null, null, null);

        Assert.Equal("seerr-3", view.RequestedBy.Key);
        Assert.Equal("Seerr Name", view.RequestedBy.Name);
        Assert.Equal("TMDB 550", view.Title);
    }

    [Fact]
    public void QueueKey_TvWithoutTvdb_IsNull()
        => Assert.Null(RequestMapper.QueueKey(new SeerrMedia { MediaType = "tv", TmdbId = 1, TvdbId = null }));
}
