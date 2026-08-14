using Jellyfin.Plugin.Chaperone.Providers;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.Chaperone
{
    /// <summary>
    /// Registers Chaperone's shared services with the Jellyfin DI container.
    /// </summary>
    public class PluginServiceRegistrator : IPluginServiceRegistrator
    {
        /// <inheritdoc />
        public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
        {
            serviceCollection.AddSingleton<RatingService>();
        }
    }
}
