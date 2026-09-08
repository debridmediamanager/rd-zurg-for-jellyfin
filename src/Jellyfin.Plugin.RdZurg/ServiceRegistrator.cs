using Jellyfin.Plugin.RdZurg.Streaming;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.RdZurg;

/// <summary>
/// Registers the plugin's own services before Jellyfin builds its container.
/// </summary>
public class ServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        // One resolver for the whole server, so its link and archive caches are shared by every
        // request rather than rebuilt per controller instance.
        serviceCollection.AddSingleton<LinkResolver>();
    }
}
