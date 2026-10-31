using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Jellyfin.Plugin.Requests.Arr;
using Jellyfin.Plugin.Requests.Model;
using Jellyfin.Plugin.Requests.Seerr;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Requests.Services;

/// <summary>A media record cleanup chose (or would choose) to delete.</summary>
public sealed class CleanupCandidate
{
    /// <summary>Gets or sets when it was evaluated.</summary>
    public DateTime Time { get; set; }

    /// <summary>Gets or sets the Seerr media id.</summary>
    public int MediaId { get; set; }

    /// <summary>Gets or sets the title.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Gets or sets "movie" or "tv".</summary>
    public string Type { get; set; } = "movie";

    /// <summary>Gets or sets why it qualifies.</summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>Gets or sets what triggered the check.</summary>
    public string Trigger { get; set; } = string.Empty;

    /// <summary>Gets or sets the users whose requests are affected.</summary>
    public List<string> RequestedBy { get; set; } = [];

    /// <summary>Gets or sets a value indicating whether this was a dry run.</summary>
    public bool DryRun { get; set; }

    /// <summary>Gets or sets a value indicating whether the Seerr record was deleted.</summary>
    public bool Deleted { get; set; }

    /// <summary>Gets or sets the error from Seerr, if deletion failed.</summary>
    public string? Error { get; set; }
}

/// <summary>
/// Deletes Seerr media records (and so their requests) once the content is gone from
/// Jellyfin, Radarr or Sonarr. Every decision goes through <see cref="CleanupPolicy"/>.
/// </summary>
public sealed class CleanupService : IDisposable
{
    private const int JournalSize = 200;

    private readonly SeerrClient _seerr;
    private readonly ArrClient _arr;
    private readonly LibraryIndex _library;
    private readonly MetadataCache _metadata;
    private readonly RequestStore _store;
    private readonly ILogger<CleanupService> _logger;
    private readonly SemaphoreSlim _runLock = new(1, 1);
    private readonly ConcurrentDictionary<string, byte> _scheduled = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Lock _journalLock = new();
    private List<CleanupCandidate>? _journal;

    /// <summary>
    /// Initializes a new instance of the <see cref="CleanupService"/> class.
    /// </summary>
    /// <param name="seerr">Seerr client.</param>
    /// <param name="arr">Radarr/Sonarr client.</param>
    /// <param name="library">Jellyfin library index.</param>
    /// <param name="metadata">Title cache.</param>
    /// <param name="store">Request store.</param>
    /// <param name="logger">Logger.</param>
    public CleanupService(
        SeerrClient seerr,
        ArrClient arr,
        LibraryIndex library,
        MetadataCache metadata,
        RequestStore store,
        ILogger<CleanupService> logger)
    {
        _seerr = seerr;
        _arr = arr;
        _library = library;
        _metadata = metadata;
        _store = store;
        _logger = logger;
    }

    private static Configuration.PluginConfiguration Config => Plugin.Instance?.Configuration ?? new Configuration.PluginConfiguration();

    private static string? JournalPath => Plugin.Instance is { } p ? Path.Combine(p.DataFolderPath, "cleanup-journal.json") : null;

    /// <summary>Gets recent cleanup decisions, newest first.</summary>
    /// <returns>Journal entries.</returns>
    public IReadOnlyList<CleanupCandidate> GetJournal()
    {
        lock (_journalLock)
        {
            return [.. Journal];
        }
    }

    /// <summary>Handles a Radarr MovieDelete or Sonarr SeriesDelete webhook.</summary>
    /// <param name="kind">Source app.</param>
    /// <param name="externalId">TMDB id (Radarr) or TVDB id (Sonarr).</param>
    public void OnArrDeleted(ArrKind kind, int externalId)
    {
        if (!Config.CleanupEnabled || !Config.CleanupOnArrDelete || externalId <= 0)
        {
            return;
        }

        Func<SeerrMedia, bool> scope = kind == ArrKind.Radarr
            ? m => m.MediaType == "movie" && m.TmdbId == externalId
            : m => m.MediaType == "tv" && m.TvdbId == externalId;

        _ = RunInBackgroundAsync(CleanupTrigger.ArrDeleted, scope, TimeSpan.Zero);
    }

    /// <summary>
    /// Handles Jellyfin removing a movie or series. The check runs after the grace period so
    /// upgrades and rescans that briefly drop an item do not delete anything.
    /// </summary>
    /// <param name="isSeries">True for a series.</param>
    /// <param name="tmdbId">TMDB id, if known.</param>
    /// <param name="tvdbId">TVDB id, if known.</param>
    public void OnJellyfinItemRemoved(bool isSeries, int? tmdbId, int? tvdbId)
    {
        if (!Config.CleanupEnabled || !Config.CleanupOnJellyfinRemoval || (tmdbId is null && tvdbId is null))
        {
            return;
        }

        var key = string.Create(CultureInfo.InvariantCulture, $"{(isSeries ? "tv" : "movie")}:{tmdbId}:{tvdbId}");
        if (!_scheduled.TryAdd(key, 0))
        {
            return;
        }

        Func<SeerrMedia, bool> scope = isSeries
            ? m => m.MediaType == "tv" && ((tvdbId is not null && m.TvdbId == tvdbId) || (tmdbId is not null && m.TmdbId == tmdbId))
            : m => m.MediaType == "movie" && m.TmdbId == tmdbId;

        var grace = TimeSpan.FromMinutes(Math.Max(1, Config.JellyfinRemovalGraceMinutes));
        _logger.LogInformation("Jellyfin removed {Key}; checking Seerr in {Minutes} min", key, grace.TotalMinutes);
        _ = RunInBackgroundAsync(CleanupTrigger.JellyfinRemoved, scope, grace)
            .ContinueWith(_ => _scheduled.TryRemove(key, out byte _), TaskScheduler.Default);
    }

    /// <summary>Evaluates Seerr media and deletes the records whose content is gone.</summary>
    /// <param name="trigger">What prompted the run.</param>
    /// <param name="scope">Optional filter limiting which media are considered.</param>
    /// <param name="dryRunOverride">Forces dry-run on or off; null uses the configured value.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="recordJournal">False for previews, which should not clutter the journal.</param>
    /// <returns>The candidates found.</returns>
    public async Task<List<CleanupCandidate>> RunAsync(
        CleanupTrigger trigger,
        Func<SeerrMedia, bool>? scope,
        bool? dryRunOverride,
        CancellationToken cancellationToken,
        bool recordJournal = true)
    {
        await _runLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await RunLockedAsync(trigger, scope, dryRunOverride ?? Config.CleanupDryRun, recordJournal, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _runLock.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _shutdown.Cancel();
        _shutdown.Dispose();
        _runLock.Dispose();
    }

    private async Task<List<CleanupCandidate>> RunLockedAsync(
        CleanupTrigger trigger,
        Func<SeerrMedia, bool>? scope,
        bool dryRun,
        bool recordJournal,
        CancellationToken cancellationToken)
    {
        var requests = await _seerr.GetAllRequestsAsync(cancellationToken).ConfigureAwait(false);
        var groups = requests
            .Where(r => r.Media is not null)
            .Select(r =>
            {
                r.Media!.MediaType ??= r.Type;
                return r;
            })
            .GroupBy(r => r.Media!.Id)
            .Where(g => scope is null || scope(g.First().Media!))
            .ToList();

        if (groups.Count == 0)
        {
            return [];
        }

        Dictionary<int, ArrPresence>? radarr = null, sonarr = null;
        if (trigger != CleanupTrigger.ArrDeleted)
        {
            _library.Invalidate();
            radarr = await TryGetLibraryAsync(ArrKind.Radarr, groups, "movie", cancellationToken).ConfigureAwait(false);
            sonarr = await TryGetLibraryAsync(ArrKind.Sonarr, groups, "tv", cancellationToken).ConfigureAwait(false);
        }

        var now = DateTime.UtcNow;
        var found = new List<CleanupCandidate>();
        foreach (var group in groups)
        {
            var media = group.First().Media!;
            var isTv = media.MediaType == "tv";
            var all4k = group.All(r => r.Is4k);

            var arr = ArrPresence.Missing;
            if (trigger != CleanupTrigger.ArrDeleted)
            {
                // 4K requests usually go to a separate Radarr/Sonarr that this plugin does not read.
                var lib = all4k ? null : isTv ? sonarr : radarr;
                var externalId = isTv ? media.TvdbId : media.TmdbId;
                arr = lib is null || externalId is null
                    ? ArrPresence.Unknown
                    : lib.GetValueOrDefault(externalId.Value, ArrPresence.Missing);
            }

            var facts = new MediaFacts(
                (SeerrMediaStatus)(all4k ? media.Status4k : media.Status),
                group.Select(r => r.RequestStatus).ToList(),
                group.Where(r => r.RequestStatus is SeerrRequestStatus.Approved or SeerrRequestStatus.Completed)
                    .Select(r => (DateTime?)r.UpdatedAt.ToUniversalTime())
                    .DefaultIfEmpty()
                    .Max(),
                _library.Find(media, forceFresh: true) is not null,
                arr);

            var reason = CleanupPolicy.Evaluate(facts, trigger, now);
            if (reason is null)
            {
                continue;
            }

            var details = _metadata.TryGet(media.MediaType ?? "movie", media.TmdbId);
            found.Add(new CleanupCandidate
            {
                Time = now,
                MediaId = media.Id,
                Type = media.MediaType ?? "movie",
                Title = (isTv ? details?.Name : details?.Title) ?? string.Create(CultureInfo.InvariantCulture, $"TMDB {media.TmdbId}"),
                Reason = reason,
                Trigger = trigger.ToString(),
                RequestedBy = group
                    .Select(r => r.RequestedBy?.DisplayName ?? r.RequestedBy?.JellyfinUsername ?? "unknown")
                    .Distinct()
                    .ToList(),
                DryRun = dryRun,
            });
        }

        if (!dryRun)
        {
            foreach (var c in found)
            {
                try
                {
                    await _seerr.DeleteMediaAsync(c.MediaId, cancellationToken).ConfigureAwait(false);
                    c.Deleted = true;
                    _logger.LogInformation("Deleted Seerr media {MediaId} ({Title}): {Reason}", c.MediaId, c.Title, c.Reason);
                }
                catch (HttpRequestException ex)
                {
                    c.Error = ex.Message;
                    _logger.LogWarning("Could not delete Seerr media {MediaId} ({Title}): {Message}", c.MediaId, c.Title, ex.Message);
                }
            }

            if (found.Any(c => c.Deleted))
            {
                _store.RequestRefresh(RefreshParts.Seerr);
            }
        }
        else
        {
            foreach (var c in found)
            {
                _logger.LogInformation("[dry run] Would delete Seerr media {MediaId} ({Title}): {Reason}", c.MediaId, c.Title, c.Reason);
            }
        }

        if (found.Count > 0 && recordJournal)
        {
            await AppendJournalAsync(found).ConfigureAwait(false);
        }

        return found;
    }

    private async Task<Dictionary<int, ArrPresence>?> TryGetLibraryAsync(
        ArrKind kind,
        List<IGrouping<int, SeerrRequest>> groups,
        string mediaType,
        CancellationToken cancellationToken)
    {
        if (!groups.Any(g => g.First().Media!.MediaType == mediaType))
        {
            return null;
        }

        try
        {
            return await _arr.GetLibraryAsync(kind, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            // Unknown presence makes the policy fall back to Jellyfin-only checks.
            _logger.LogWarning("Could not read {Kind} library for cleanup: {Message}", kind, ex.Message);
            return null;
        }
    }

    private async Task RunInBackgroundAsync(CleanupTrigger trigger, Func<SeerrMedia, bool> scope, TimeSpan delay)
    {
        try
        {
            var ct = _shutdown.Token;
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }

            if (!SeerrClient.IsConfigured || !Config.CleanupEnabled)
            {
                return;
            }

            await RunAsync(trigger, scope, null, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Server shutting down.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Cleanup ({Trigger}) failed", trigger);
        }
    }

    private List<CleanupCandidate> Journal => _journal ??= LoadJournal();

    private List<CleanupCandidate> LoadJournal()
    {
        try
        {
            if (JournalPath is { } path && File.Exists(path))
            {
                return JsonSerializer.Deserialize<List<CleanupCandidate>>(File.ReadAllText(path), SeerrClient.JsonOptions) ?? [];
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Ignoring unreadable cleanup journal");
        }

        return [];
    }

    private async Task AppendJournalAsync(List<CleanupCandidate> entries)
    {
        string json;
        lock (_journalLock)
        {
            Journal.InsertRange(0, entries);
            if (Journal.Count > JournalSize)
            {
                Journal.RemoveRange(JournalSize, Journal.Count - JournalSize);
            }

            json = JsonSerializer.Serialize(Journal, SeerrClient.JsonOptions);
        }

        if (JournalPath is not { } path)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, json).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not save cleanup journal");
        }
    }
}
