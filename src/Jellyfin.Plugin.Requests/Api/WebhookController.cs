using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.Requests.Arr;
using Jellyfin.Plugin.Requests.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Requests.Api;

/// <summary>
/// Receives webhooks from Seerr, Radarr and Sonarr. Seerr and Radarr/Sonarr cannot send a
/// Jellyfin token, so these are anonymous and checked against the plugin's webhook token,
/// passed as ?token=, an Authorization header, or the password of HTTP basic auth.
/// </summary>
[ApiController]
[Route(Plugin.RoutePrefix + "/Webhook")]
[AllowAnonymous]
public class WebhookController : ControllerBase
{
    private readonly RequestStore _store;
    private readonly CleanupService _cleanup;
    private readonly ILogger<WebhookController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="WebhookController"/> class.
    /// </summary>
    /// <param name="store">Request store.</param>
    /// <param name="cleanup">Cleanup service.</param>
    /// <param name="logger">Logger.</param>
    public WebhookController(RequestStore store, CleanupService cleanup, ILogger<WebhookController> logger)
    {
        _store = store;
        _cleanup = cleanup;
        _logger = logger;
    }

    /// <summary>Seerr webhook. Any notification triggers a refresh.</summary>
    /// <param name="token">Webhook token.</param>
    /// <returns>OK.</returns>
    [HttpPost("Seerr")]
    public ActionResult Seerr([FromQuery] string? token)
    {
        if (!Authorized(token))
        {
            return Unauthorized();
        }

        _store.RequestRefresh(RefreshParts.All);
        return Ok();
    }

    /// <summary>Radarr webhook.</summary>
    /// <param name="token">Webhook token.</param>
    /// <param name="payload">Radarr event payload.</param>
    /// <returns>OK.</returns>
    [HttpPost("Radarr")]
    public ActionResult Radarr([FromQuery] string? token, [FromBody] JsonElement payload)
        => HandleArr(ArrKind.Radarr, token, payload, "MovieDelete", "movie", "tmdbId");

    /// <summary>Sonarr webhook.</summary>
    /// <param name="token">Webhook token.</param>
    /// <param name="payload">Sonarr event payload.</param>
    /// <returns>OK.</returns>
    [HttpPost("Sonarr")]
    public ActionResult Sonarr([FromQuery] string? token, [FromBody] JsonElement payload)
        => HandleArr(ArrKind.Sonarr, token, payload, "SeriesDelete", "series", "tvdbId");

    private ActionResult HandleArr(ArrKind kind, string? token, JsonElement payload, string deleteEvent, string objectName, string idField)
    {
        if (!Authorized(token))
        {
            return Unauthorized();
        }

        var eventType = payload.TryGetProperty("eventType", out var et) ? et.GetString() : null;
        if (string.Equals(eventType, deleteEvent, StringComparison.OrdinalIgnoreCase))
        {
            var id = payload.TryGetProperty(objectName, out var obj) && obj.TryGetProperty(idField, out var idProp) && idProp.TryGetInt32(out var v)
                ? v
                : 0;
            _logger.LogInformation("{Kind} reported {Event} for {Field} {Id}", kind, eventType, idField, id);
            _cleanup.OnArrDeleted(kind, id);
            _store.RequestRefresh(RefreshParts.Queue);
        }
        else if (!string.Equals(eventType, "Test", StringComparison.OrdinalIgnoreCase))
        {
            // Grab, Download, Added, health events... all change what the page should show.
            _store.RequestRefresh(RefreshParts.All);
        }

        return Ok();
    }

    private bool Authorized(string? queryToken)
    {
        var expected = Plugin.Instance?.Configuration.WebhookToken;
        if (string.IsNullOrEmpty(expected))
        {
            return false;
        }

        if (Matches(queryToken, expected))
        {
            return true;
        }

        var header = Request.Headers.Authorization.ToString();
        if (string.IsNullOrEmpty(header))
        {
            return false;
        }

        if (header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(header[6..].Trim()));
                var sep = decoded.IndexOf(':', StringComparison.Ordinal);
                return sep >= 0 && Matches(decoded[(sep + 1)..], expected);
            }
            catch (FormatException)
            {
                return false;
            }
        }

        var raw = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header[7..].Trim() : header.Trim();
        return Matches(raw, expected);
    }

    private static bool Matches(string? given, string expected)
        => given is not null
            && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(given), Encoding.UTF8.GetBytes(expected));
}
