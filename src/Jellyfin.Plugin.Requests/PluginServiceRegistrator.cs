using Jellyfin.Plugin.Requests.Arr;
using Jellyfin.Plugin.Requests.Seerr;
using Jellyfin.Plugin.Requests.Services;
using Jellyfin.Plugin.Requests.Web;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.Requests;

/// <summary>
/// Registers the plugin's services with Jellyfin's DI container.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddHttpClient(SeerrClient.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(20));

        serviceCollection.AddSingleton<SeerrClient>();
        serviceCollection.AddSingleton<ArrClient>();
        serviceCollection.AddSingleton<LibraryIndex>();
        serviceCollection.AddSingleton<MetadataCache>();
        serviceCollection.AddSingleton<RequestStore>();
        serviceCollection.AddSingleton<CleanupService>();

        serviceCollection.AddHostedService<LiveUpdateService>();
        serviceCollection.AddHostedService<LibraryWatcher>();

        serviceCollection.AddSingleton<IStartupFilter, ScriptInjectionStartupFilter>();
    }
}
