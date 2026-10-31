using Jellyfin.Plugin.Requests.Model;
using Jellyfin.Plugin.Requests.Seerr;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Requests.Services;

/// <summary>
/// Daily full pass that catches anything the webhooks and library events missed
/// (e.g. content removed while Jellyfin was down). Shows under Dashboard → Scheduled Tasks.
/// </summary>
public sealed class CleanupTask : IScheduledTask
{
    private readonly CleanupService _cleanup;
    private readonly ILogger<CleanupTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="CleanupTask"/> class.
    /// </summary>
    /// <param name="cleanup">Cleanup service.</param>
    /// <param name="logger">Logger.</param>
    public CleanupTask(CleanupService cleanup, ILogger<CleanupTask> logger)
    {
        _cleanup = cleanup;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Clean up removed requests";

    /// <inheritdoc />
    public string Key => "JellyfinRequestsCleanup";

    /// <inheritdoc />
    public string Description => "Deletes Seerr requests whose content is gone from Jellyfin, Radarr or Sonarr. Honors the dry-run setting.";

    /// <inheritdoc />
    public string Category => "Jellyfin Requests";

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return
        [
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.DailyTrigger,
                TimeOfDayTicks = TimeSpan.FromHours(4).Ticks,
            },
        ];
    }

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        if (!SeerrClient.IsConfigured || Plugin.Instance?.Configuration.CleanupEnabled != true)
        {
            progress.Report(100);
            return;
        }

        var found = await _cleanup.RunAsync(CleanupTrigger.Reconcile, null, null, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Cleanup pass found {Count} removed title(s)", found.Count);
        progress.Report(100);
    }
}
