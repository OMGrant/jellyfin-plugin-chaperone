using Jellyfin.Plugin.Chaperone.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Providers;

namespace Jellyfin.Plugin.Chaperone.Providers
{
    /// <summary>
    /// Shared gate deciding whether a provider should touch an item's rating.
    /// </summary>
    internal static class RatingGate
    {
        /// <summary>
        /// Returns true when the provider should leave the item alone because it already has a
        /// rating and neither overwrite nor a full-replace refresh is in effect.
        /// </summary>
        public static bool ShouldSkipExisting(
            BaseItem item,
            PluginConfiguration config,
            MetadataRefreshOptions? options)
        {
            var isFullReplace = options is not null
                && (options.ReplaceAllMetadata
                    || options.MetadataRefreshMode == MetadataRefreshMode.FullRefresh);

            return !string.IsNullOrEmpty(item.OfficialRating)
                && !config.OverwriteExisting
                && !isFullReplace;
        }
    }
}
