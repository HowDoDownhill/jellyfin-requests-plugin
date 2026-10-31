using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Requests.Configuration;

/// <summary>
/// Admin settings for the requests page. Stored by Jellyfin as XML in the plugin config folder.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>Gets or sets the Seerr base URL the server uses (e.g. http://seerr:5055).</summary>
    public string SeerrUrl { get; set; } = string.Empty;

    /// <summary>Gets or sets the Seerr API key (Settings → General).</summary>
    public string SeerrApiKey { get; set; } = string.Empty;

    /// <summary>Gets or sets the Seerr URL users can open in a browser. Falls back to <see cref="SeerrUrl"/>.</summary>
    public string SeerrPublicUrl { get; set; } = string.Empty;

    /// <summary>Gets or sets the Radarr base URL. Optional.</summary>
    public string RadarrUrl { get; set; } = string.Empty;

    /// <summary>Gets or sets the Radarr API key.</summary>
    public string RadarrApiKey { get; set; } = string.Empty;

    /// <summary>Gets or sets the Sonarr base URL. Optional.</summary>
    public string SonarrUrl { get; set; } = string.Empty;

    /// <summary>Gets or sets the Sonarr API key.</summary>
    public string SonarrApiKey { get; set; } = string.Empty;

    /// <summary>Gets or sets the shared secret webhook senders must pass as ?token= or Authorization header.</summary>
    public string WebhookToken { get; set; } = string.Empty;

    /// <summary>Gets or sets the label shown in the sidebar and page header.</summary>
    public string NavLabel { get; set; } = "Requests";

    /// <summary>Gets or sets a value indicating whether non-admin users see every user's requests.</summary>
    public bool ShowAllUsersRequests { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether users may cancel their own pending requests.</summary>
    public bool AllowUsersToCancelPending { get; set; } = true;

    /// <summary>Gets or sets how many days declined and failed requests stay visible. 0 keeps them forever.</summary>
    public int HideDeclinedAfterDays { get; set; } = 14;

    /// <summary>Gets or sets how often (seconds) the download queue is polled while someone has the page open.</summary>
    public int LiveRefreshSeconds { get; set; } = 10;

    /// <summary>Gets or sets how often (seconds) Seerr requests are re-read while someone has the page open. Webhooks refresh immediately.</summary>
    public int SeerrRefreshSeconds { get; set; } = 60;

    /// <summary>Gets or sets a value indicating whether removed content gets its Seerr request deleted.</summary>
    public bool CleanupEnabled { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether cleanup only logs what it would delete.</summary>
    public bool CleanupDryRun { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether a Radarr/Sonarr delete webhook triggers cleanup.</summary>
    public bool CleanupOnArrDelete { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether removing an item from the Jellyfin library triggers cleanup.</summary>
    public bool CleanupOnJellyfinRemoval { get; set; } = true;

    /// <summary>
    /// Gets or sets the minutes to wait after a Jellyfin item disappears before treating it as removed.
    /// Upgrades and rescans briefly remove and re-add items, so this must stay above zero.
    /// </summary>
    public int JellyfinRemovalGraceMinutes { get; set; } = 15;
}
