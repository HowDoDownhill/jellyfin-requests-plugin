using System.Globalization;
using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using Jellyfin.Plugin.Requests.Arr;
using Jellyfin.Plugin.Requests.Model;
using Jellyfin.Plugin.Requests.Seerr;
using Jellyfin.Plugin.Requests.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Requests.Api;

/// <summary>
/// Endpoints for the injected requests page and the admin config page.
/// Jellyfin authenticates callers with their normal session token.
/// </summary>
[ApiController]
[Route(Plugin.RoutePrefix)]
[Authorize]
public class RequestsController : ControllerBase
{
    private static readonly TimeSpan LongPollTimeout = TimeSpan.FromSeconds(25);

    private readonly RequestStore _store;
    private readonly SeerrClient _seerr;
    private readonly ArrClient _arr;
    private readonly CleanupService _cleanup;

    /// <summary>
    /// Initializes a new instance of the <see cref="RequestsController"/> class.
    /// </summary>
    /// <param name="store">Request store.</param>
    /// <param name="seerr">Seerr client.</param>
    /// <param name="arr">Radarr/Sonarr client.</param>
    /// <param name="cleanup">Cleanup service.</param>
    public RequestsController(RequestStore store, SeerrClient seerr, ArrClient arr, CleanupService cleanup)
    {
        _store = store;
        _seerr = seerr;
        _arr = arr;
        _cleanup = cleanup;
    }

    private static Configuration.PluginConfiguration Config => Plugin.Instance?.Configuration ?? new Configuration.PluginConfiguration();

    private bool IsAdmin => User.IsInRole("Administrator");

    private string? CurrentUserId => RequestMapper.NormalizeUserId(
        User.Claims.FirstOrDefault(c => string.Equals(c.Type, "Jellyfin-UserId", StringComparison.OrdinalIgnoreCase))?.Value);

    /// <summary>Returns every request the caller may see.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The page payload.</returns>
    [HttpGet("Requests")]
    public async Task<ActionResult> GetRequests(CancellationToken cancellationToken)
    {
        _store.MarkActive();
        await _store.EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);

        var config = Config;
        var me = CurrentUserId;
        var isAdmin = IsAdmin;
        var hideBefore = config.HideDeclinedAfterDays > 0 ? DateTime.UtcNow.AddDays(-config.HideDeclinedAfterDays) : DateTime.MinValue;

        var items = _store.Views
            .Where(v => isAdmin || config.ShowAllUsersRequests || v.RequestedBy.JellyfinUserId == me)
            .Where(v => v.State is not ("declined" or "failed") || v.UpdatedAt.ToUniversalTime() >= hideBefore)
            .ToList();

        return CamelJson(new
        {
            version = _store.Version,
            label = config.NavLabel,
            isAdmin,
            currentUserId = me,
            canCancelOwn = config.AllowUsersToCancelPending,
            // Only a public address goes to browsers; the server-side URL is often internal.
            seerrUrl = string.IsNullOrWhiteSpace(config.SeerrPublicUrl) ? null : config.SeerrPublicUrl.TrimEnd('/'),
            warnings = isAdmin ? _store.GetWarnings() : _store.GetWarnings().Take(1).ToArray(),
            items,
        });
    }

    /// <summary>
    /// Long-poll: returns as soon as the data changes past <paramref name="since"/>, or after 25 seconds.
    /// </summary>
    /// <param name="since">Version the client has.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The current version.</returns>
    [HttpGet("Changes")]
    public async Task<ActionResult> WaitForChanges([FromQuery] long since, CancellationToken cancellationToken)
    {
        _store.MarkActive();
        await _store.EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
        var version = await _store.WaitForChangeAsync(since, LongPollTimeout, cancellationToken).ConfigureAwait(false);
        return CamelJson(new { version });
    }

    /// <summary>Cancels a pending request. Requesters can cancel their own; admins can cancel any.</summary>
    /// <param name="id">Request id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>No content.</returns>
    [HttpPost("Requests/{id:int}/Cancel")]
    public async Task<ActionResult> Cancel([FromRoute] int id, CancellationToken cancellationToken)
    {
        var view = _store.Views.FirstOrDefault(v => v.Id == id);
        if (view is null)
        {
            return NotFound();
        }

        var mine = view.RequestedBy.JellyfinUserId is not null && view.RequestedBy.JellyfinUserId == CurrentUserId;
        if (!IsAdmin && !(mine && view.State == "pending" && Config.AllowUsersToCancelPending))
        {
            return Forbid();
        }

        return await CallSeerrAsync(ct => _seerr.DeleteRequestAsync(id, ct), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Removes a request (admin). Unless someone else still has an active request for the title,
    /// this also deletes it from Radarr/Sonarr with its files.
    /// </summary>
    /// <param name="id">Request id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>What was removed.</returns>
    [HttpPost("Requests/{id:int}/Remove")]
    public async Task<ActionResult> Remove([FromRoute] int id, CancellationToken cancellationToken)
    {
        if (!IsAdmin)
        {
            return Forbid();
        }

        try
        {
            var all = await _seerr.GetAllRequestsAsync(cancellationToken).ConfigureAwait(false);
            var target = all.FirstOrDefault(r => r.Id == id);
            if (target?.Media is null)
            {
                return NotFound();
            }

            var plan = RemovalPlanner.Plan(target, all);
            if (plan.DeleteFromArr)
            {
                await _seerr.DeleteMediaFilesAsync(target.Media.Id, target.Is4k, cancellationToken).ConfigureAwait(false);
            }

            if (plan.DeleteMedia)
            {
                await _seerr.DeleteMediaAsync(target.Media.Id, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await _seerr.DeleteRequestAsync(id, cancellationToken).ConfigureAwait(false);
            }

            await _store.RefreshAsync(RefreshParts.All, cancellationToken).ConfigureAwait(false);
            return CamelJson(new { removedFromArr = plan.DeleteFromArr, sharedWith = plan.SharedWith });
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or JsonException)
        {
            return Problem(ex.Message, statusCode: StatusCodes.Status502BadGateway);
        }
    }

    /// <summary>Approves a pending request (admin).</summary>
    /// <param name="id">Request id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>No content.</returns>
    [HttpPost("Requests/{id:int}/Approve")]
    public Task<ActionResult> Approve([FromRoute] int id, CancellationToken cancellationToken)
        => IsAdmin
            ? CallSeerrAsync(ct => _seerr.SetRequestApprovalAsync(id, true, ct), cancellationToken)
            : Task.FromResult<ActionResult>(Forbid());

    /// <summary>Declines a pending request (admin).</summary>
    /// <param name="id">Request id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>No content.</returns>
    [HttpPost("Requests/{id:int}/Decline")]
    public Task<ActionResult> Decline([FromRoute] int id, CancellationToken cancellationToken)
        => IsAdmin
            ? CallSeerrAsync(ct => _seerr.SetRequestApprovalAsync(id, false, ct), cancellationToken)
            : Task.FromResult<ActionResult>(Forbid());

    /// <summary>Tests the saved Seerr, Radarr and Sonarr settings (admin).</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Null per service on success, otherwise the error.</returns>
    [HttpPost("Admin/Test")]
    public async Task<ActionResult> Test(CancellationToken cancellationToken)
    {
        if (!IsAdmin)
        {
            return Forbid();
        }

        var seerr = _seerr.TestAsync(cancellationToken);
        var radarr = _arr.TestAsync(ArrKind.Radarr, cancellationToken);
        var sonarr = _arr.TestAsync(ArrKind.Sonarr, cancellationToken);
        await Task.WhenAll(seerr, radarr, sonarr).ConfigureAwait(false);
        _store.RequestRefresh(RefreshParts.All);

        return CamelJson(new { seerr = seerr.Result, radarr = radarr.Result, sonarr = sonarr.Result });
    }

    /// <summary>Runs a full cleanup pass (admin).</summary>
    /// <param name="dryRun">True to only report what would be deleted.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The candidates.</returns>
    [HttpPost("Admin/Cleanup")]
    public async Task<ActionResult> RunCleanup([FromQuery] bool dryRun, CancellationToken cancellationToken)
    {
        if (!IsAdmin)
        {
            return Forbid();
        }

        try
        {
            var found = await _cleanup.RunAsync(CleanupTrigger.Reconcile, null, dryRun, cancellationToken, recordJournal: !dryRun)
                .ConfigureAwait(false);
            return CamelJson(new { dryRun, items = found });
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or JsonException)
        {
            return Problem(ex.Message, statusCode: StatusCodes.Status502BadGateway);
        }
    }

    /// <summary>Recent cleanup decisions (admin).</summary>
    /// <returns>Journal entries, newest first.</returns>
    [HttpGet("Admin/CleanupJournal")]
    public ActionResult GetCleanupJournal()
        => IsAdmin ? CamelJson(new { items = _cleanup.GetJournal() }) : Forbid();

    /// <summary>Serves the client script injected into jellyfin-web.</summary>
    /// <returns>JavaScript.</returns>
    [HttpGet("client.js")]
    [AllowAnonymous]
    public ActionResult GetClientScript() => EmbeddedFile("Web.client.js", "application/javascript");

    /// <summary>Serves the client stylesheet.</summary>
    /// <returns>CSS.</returns>
    [HttpGet("client.css")]
    [AllowAnonymous]
    public ActionResult GetClientStyles() => EmbeddedFile("Web.client.css", "text/css");

    // Jellyfin's MVC serializer keeps PascalCase names; the web client expects camelCase.
    private static JsonResult CamelJson(object value) => new(value, SeerrClient.JsonOptions);

    private ActionResult EmbeddedFile(string name, string contentType)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var stream = assembly.GetManifestResourceStream(typeof(Plugin).Namespace + "." + name);
        if (stream is null)
        {
            return NotFound();
        }

        // URLs carry a version query, so a long cache is safe.
        Response.Headers.CacheControl = Request.Query.ContainsKey("v") ? "public, max-age=604800" : "no-cache";
        return File(stream, contentType);
    }

    private async Task<ActionResult> CallSeerrAsync(Func<CancellationToken, Task> call, CancellationToken cancellationToken)
    {
        try
        {
            await call(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
        {
            return Problem(ex.Message, statusCode: StatusCodes.Status502BadGateway);
        }

        await _store.RefreshAsync(RefreshParts.Seerr, cancellationToken).ConfigureAwait(false);
        return NoContent();
    }
}
