using System.Text.Json;
using Jellyfin.Plugin.Requests.Arr;
using Jellyfin.Plugin.Requests.Model;
using Jellyfin.Plugin.Requests.Seerr;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Requests.Services;

/// <summary>Which parts of the snapshot to reload.</summary>
[Flags]
public enum RefreshParts
{
    /// <summary>Only rebuild views from data already in memory.</summary>
    None = 0,

    /// <summary>Reload requests from Seerr.</summary>
    Seerr = 1,

    /// <summary>Reload the Radarr/Sonarr download queues.</summary>
    Queue = 2,

    /// <summary>Reload everything.</summary>
    All = Seerr | Queue,
}

/// <summary>
/// In-memory snapshot of every request, merged with live download progress and Jellyfin
/// library presence. Each content change bumps <see cref="Version"/> and wakes long-polling clients.
/// </summary>
public sealed class RequestStore
{
    private static readonly TimeSpan ActiveWindow = TimeSpan.FromSeconds(90);

    private readonly SeerrClient _seerr;
    private readonly ArrClient _arr;
    private readonly LibraryIndex _library;
    private readonly MetadataCache _metadata;
    private readonly IUserManager _userManager;
    private readonly ILogger<RequestStore> _logger;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly Dictionary<string, string> _warnings = [];

    private List<SeerrRequest> _requests = [];
    private Dictionary<string, List<ArrQueueEntry>> _queue = [];
    private IReadOnlyList<RequestView> _views = [];
    private string _hash = string.Empty;
    private long _version = DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;
    private TaskCompletionSource _changed = NewSignal();
    private long _lastActiveTicks;
    private int _pendingParts;
    private bool _loaded;

    /// <summary>
    /// Initializes a new instance of the <see cref="RequestStore"/> class.
    /// </summary>
    /// <param name="seerr">Seerr client.</param>
    /// <param name="arr">Radarr/Sonarr client.</param>
    /// <param name="library">Jellyfin library index.</param>
    /// <param name="metadata">Title cache.</param>
    /// <param name="userManager">Jellyfin user manager.</param>
    /// <param name="logger">Logger.</param>
    public RequestStore(
        SeerrClient seerr,
        ArrClient arr,
        LibraryIndex library,
        MetadataCache metadata,
        IUserManager userManager,
        ILogger<RequestStore> logger)
    {
        _seerr = seerr;
        _arr = arr;
        _library = library;
        _metadata = metadata;
        _userManager = userManager;
        _logger = logger;
    }

    /// <summary>Gets the current content version.</summary>
    public long Version => Interlocked.Read(ref _version);

    /// <summary>Gets the current views.</summary>
    public IReadOnlyList<RequestView> Views => _views;

    /// <summary>Gets when Seerr was last read successfully.</summary>
    public DateTime SeerrFetchedAt { get; private set; }

    /// <summary>Gets when the download queues were last read.</summary>
    public DateTime QueueFetchedAt { get; private set; }

    /// <summary>Gets a value indicating whether a client polled recently.</summary>
    public bool IsActive => DateTime.UtcNow.Ticks - Interlocked.Read(ref _lastActiveTicks) < ActiveWindow.Ticks;

    /// <summary>Gets current connection warnings for display.</summary>
    /// <returns>Warning messages.</returns>
    public IReadOnlyList<string> GetWarnings()
    {
        lock (_warnings)
        {
            return [.. _warnings.Values];
        }
    }

    /// <summary>Records that a client is watching, which turns on fast polling.</summary>
    public void MarkActive() => Interlocked.Exchange(ref _lastActiveTicks, DateTime.UtcNow.Ticks);

    /// <summary>Asks the background loop to reload the given parts soon. Safe to call from event handlers.</summary>
    /// <param name="parts">Parts to reload.</param>
    public void RequestRefresh(RefreshParts parts)
    {
        // Rebuild-only requests still need a non-zero marker to be picked up.
        var bits = parts == RefreshParts.None ? 4 : (int)parts;
        int current;
        do
        {
            current = _pendingParts;
        }
        while (Interlocked.CompareExchange(ref _pendingParts, current | bits, current) != current);
    }

    /// <summary>Takes any refresh requested via <see cref="RequestRefresh"/>.</summary>
    /// <param name="parts">The requested parts.</param>
    /// <returns>True when something was requested.</returns>
    public bool TryTakePendingRefresh(out RefreshParts parts)
    {
        var bits = Interlocked.Exchange(ref _pendingParts, 0);
        parts = (RefreshParts)(bits & (int)RefreshParts.All);
        return bits != 0;
    }

    /// <summary>Loads everything on first use.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task.</returns>
    public Task EnsureLoadedAsync(CancellationToken cancellationToken)
        => _loaded ? Task.CompletedTask : RefreshAsync(RefreshParts.All, cancellationToken);

    /// <summary>Waits until the version moves past <paramref name="since"/> or the timeout passes.</summary>
    /// <param name="since">Version the client already has.</param>
    /// <param name="timeout">Maximum wait.</param>
    /// <param name="cancellationToken">Cancellation token (client disconnect).</param>
    /// <returns>The current version.</returns>
    public async Task<long> WaitForChangeAsync(long since, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var signal = _changed.Task;
        if (Version != since)
        {
            return Version;
        }

        try
        {
            await signal.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // Normal long-poll expiry.
        }

        return Version;
    }

    /// <summary>Reloads the requested parts and rebuilds the views.</summary>
    /// <param name="parts">What to reload.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task.</returns>
    public async Task RefreshAsync(RefreshParts parts, CancellationToken cancellationToken)
    {
        await _refreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_loaded)
            {
                parts = RefreshParts.All;
            }

            if (parts.HasFlag(RefreshParts.Seerr))
            {
                await LoadSeerrAsync(cancellationToken).ConfigureAwait(false);
            }

            if (parts.HasFlag(RefreshParts.Queue))
            {
                await LoadQueuesAsync(cancellationToken).ConfigureAwait(false);
            }

            _loaded = true;
            Rebuild();
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private async Task LoadSeerrAsync(CancellationToken cancellationToken)
    {
        if (!SeerrClient.IsConfigured)
        {
            SetWarning("seerr", "Seerr is not configured yet. An admin can set it up in Dashboard → Plugins → Jellyfin Requests.");
            _requests = [];
            return;
        }

        try
        {
            var requests = await _seerr.GetAllRequestsAsync(cancellationToken).ConfigureAwait(false);
            await _metadata.EnsureAsync(
                requests.Where(r => r.Media is not null).Select(r => (r.Type ?? r.Media!.MediaType ?? "movie", r.Media!.TmdbId)),
                cancellationToken).ConfigureAwait(false);

            _requests = requests;
            SeerrFetchedAt = DateTime.UtcNow;
            SetWarning("seerr", null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            _logger.LogWarning("Could not read Seerr requests: {Message}", ex.Message);
            SetWarning("seerr", "Seerr is unreachable, showing the last known requests.");
        }
    }

    private async Task LoadQueuesAsync(CancellationToken cancellationToken)
    {
        var queue = new Dictionary<string, List<ArrQueueEntry>>();
        foreach (var kind in new[] { ArrKind.Radarr, ArrKind.Sonarr })
        {
            if (!ArrClient.IsConfigured(kind))
            {
                SetWarning(kind.ToString(), null);
                continue;
            }

            try
            {
                foreach (var entry in await _arr.GetQueueAsync(kind, cancellationToken).ConfigureAwait(false))
                {
                    var key = RequestMapper.QueueKey(kind, entry.ExternalId);
                    if (!queue.TryGetValue(key, out var list))
                    {
                        queue[key] = list = [];
                    }

                    list.Add(entry);
                }

                SetWarning(kind.ToString(), null);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
            {
                _logger.LogDebug("Could not read {Kind} queue: {Message}", kind, ex.Message);
                SetWarning(kind.ToString(), $"{kind} is unreachable, download progress may be stale.");

                // Keep the previous entries for this app rather than showing everything as stalled.
                var prefix = kind == ArrKind.Radarr ? "movie:" : "tv:";
                foreach (var (key, list) in _queue.Where(kv => kv.Key.StartsWith(prefix, StringComparison.Ordinal)))
                {
                    queue[key] = list;
                }
            }
        }

        _queue = queue;
        QueueFetchedAt = DateTime.UtcNow;
    }

    private void Rebuild()
    {
        var useArrQueue = ArrClient.IsConfigured(ArrKind.Radarr) || ArrClient.IsConfigured(ArrKind.Sonarr);
        var names = new Dictionary<string, string?>();
        var views = new List<RequestView>(_requests.Count);

        foreach (var request in _requests)
        {
            if (request.Media is not { } media)
            {
                continue;
            }

            var type = request.Type ?? media.MediaType ?? "movie";
            media.MediaType ??= type;

            DownloadView? download = null;
            var key = RequestMapper.QueueKey(media);
            var arrForType = type == "tv" ? ArrClient.IsConfigured(ArrKind.Sonarr) : ArrClient.IsConfigured(ArrKind.Radarr);
            if (useArrQueue && arrForType)
            {
                if (key is not null && _queue.TryGetValue(key, out var entries))
                {
                    download = RequestMapper.Summarize(entries);
                }
            }
            else
            {
                download = RequestMapper.Summarize(request.Is4k ? null : media.DownloadStatus);
            }

            string? itemId = null;
            try
            {
                itemId = _library.Find(media);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
            {
                _logger.LogDebug(ex, "Library lookup failed");
            }

            var jfUserId = RequestMapper.NormalizeUserId(request.RequestedBy?.JellyfinUserId);
            string? userName = null;
            if (jfUserId is not null && !names.TryGetValue(jfUserId, out userName))
            {
                userName = _userManager.GetUserById(Guid.ParseExact(jfUserId, "N"))?.Username;
                names[jfUserId] = userName;
            }

            views.Add(RequestMapper.ToView(request, _metadata.TryGet(type, media.TmdbId), download, itemId, userName));
        }

        var hash = JsonSerializer.Serialize(views, SeerrClient.JsonOptions) + string.Join('|', GetWarnings());
        if (hash == _hash)
        {
            return;
        }

        _hash = hash;
        _views = views;
        Interlocked.Increment(ref _version);
        Interlocked.Exchange(ref _changed, NewSignal()).TrySetResult();
    }

    private void SetWarning(string source, string? message)
    {
        lock (_warnings)
        {
            if (message is null)
            {
                _warnings.Remove(source);
            }
            else
            {
                _warnings[source] = message;
            }
        }
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
