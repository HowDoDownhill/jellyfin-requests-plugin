using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Requests.Services;

/// <summary>
/// Keeps the request snapshot fresh. Polls the download queues and Seerr while someone has the
/// page open, and applies refreshes requested by webhooks and library events at any time.
/// </summary>
public sealed class LiveUpdateService : BackgroundService
{
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(500);

    private readonly RequestStore _store;
    private readonly ILogger<LiveUpdateService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="LiveUpdateService"/> class.
    /// </summary>
    /// <param name="store">Request store.</param>
    /// <param name="logger">Logger.</param>
    public LiveUpdateService(RequestStore store, ILogger<LiveUpdateService> logger)
    {
        _store = store;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Tick);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            var parts = RefreshParts.None;
            var due = _store.TryTakePendingRefresh(out var requested);
            parts |= requested;

            if (_store.IsActive)
            {
                var config = Plugin.Instance?.Configuration;
                var now = DateTime.UtcNow;
                if (now - _store.QueueFetchedAt > TimeSpan.FromSeconds(Math.Max(3, config?.LiveRefreshSeconds ?? 10)))
                {
                    parts |= RefreshParts.Queue;
                    due = true;
                }

                if (now - _store.SeerrFetchedAt > TimeSpan.FromSeconds(Math.Max(10, config?.SeerrRefreshSeconds ?? 60)))
                {
                    parts |= RefreshParts.Seerr;
                    due = true;
                }
            }

            if (!due)
            {
                continue;
            }

            try
            {
                await _store.RefreshAsync(parts, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Request refresh failed");
            }
        }
    }
}
