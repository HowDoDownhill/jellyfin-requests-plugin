using System.Globalization;
using System.Security.Cryptography;
using Jellyfin.Plugin.Requests.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.Requests;

/// <summary>
/// Plugin entry point. Holds configuration and exposes the admin config page.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>Route prefix shared by the API controller and the injected script tag.</summary>
    public const string RoutePrefix = "JellyfinRequests";

    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Application paths.</param>
    /// <param name="xmlSerializer">XML serializer.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;

        if (string.IsNullOrWhiteSpace(Configuration.WebhookToken))
        {
            Configuration.WebhookToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
            SaveConfiguration();
        }

        ConfigStamp = DateTime.UtcNow.Ticks;
    }

    /// <summary>Raised after an admin saves the configuration.</summary>
    public event EventHandler? ConfigurationSaved;

    /// <summary>Gets the running plugin instance.</summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public override string Name => "Jellyfin Requests";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("5b0f3a52-8f1e-4d0c-9a77-2f6f7c1e9b41");

    /// <inheritdoc />
    public override string Description => "A themed requests page backed by Seerr, Radarr and Sonarr, with automatic cleanup of removed content.";

    /// <summary>Gets a stamp that changes whenever configuration is saved, used to bust the client script cache.</summary>
    public long ConfigStamp { get; private set; }

    /// <summary>Gets the cache key appended to the injected script URL.</summary>
    public string ScriptCacheKey => string.Create(
        CultureInfo.InvariantCulture,
        $"{Version}-{ConfigStamp % 100000}");

    /// <inheritdoc />
    public override void UpdateConfiguration(BasePluginConfiguration configuration)
    {
        base.UpdateConfiguration(configuration);
        ConfigStamp = DateTime.UtcNow.Ticks;
        ConfigurationSaved?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return
        [
            new PluginPageInfo
            {
                Name = Name,
                EmbeddedResourcePath = GetType().Namespace + ".Configuration.configPage.html",
            },
        ];
    }
}
