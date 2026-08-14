using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Chaperone.Configuration
{
    /// <summary>
    /// Plugin configuration for Chaperone.
    /// </summary>
    public class PluginConfiguration : BasePluginConfiguration
    {
        /// <summary>
        /// Gets or sets a value indicating whether the plugin is enabled.
        /// </summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// Gets or sets a value indicating whether to overwrite an existing OfficialRating.
        /// </summary>
        public bool OverwriteExisting { get; set; }

        /// <summary>
        /// Gets or sets the official rating applied to explicit music tracks.
        /// </summary>
        public string ExplicitRating { get; set; } = "TV-MA";

        /// <summary>
        /// Gets or sets the official rating applied to clean music tracks.
        /// </summary>
        public string CleanRating { get; set; } = "TV-G";

        /// <summary>
        /// Gets or sets the TMDb v3 API key.
        /// Defaults to Jellyfin's public TMDb key so the plugin works with zero user action;
        /// replace with your own key if desired.
        /// </summary>
        public string TmdbApiKey { get; set; } = "4219e299c89411838049ab0dab19ebd5";

        /// <summary>
        /// Gets or sets a value indicating whether music rating is enabled.
        /// </summary>
        public bool EnableMusic { get; set; } = true;

        /// <summary>
        /// Gets or sets a value indicating whether movie rating is enabled.
        /// </summary>
        public bool EnableMovies { get; set; } = true;

        /// <summary>
        /// Gets or sets a value indicating whether show rating is enabled.
        /// </summary>
        public bool EnableShows { get; set; } = true;

        /// <summary>
        /// Gets or sets a value indicating whether the anime (MyAnimeList) fallback is enabled.
        /// </summary>
        public bool EnableAnime { get; set; } = true;
    }
}
