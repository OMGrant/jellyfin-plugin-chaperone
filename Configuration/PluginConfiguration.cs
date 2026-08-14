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
        /// Gets or sets the rating applied to music that no source (Deezer, MusicBrainz, or the
        /// track's album) could identify. Defaults to "Unrated" — an honest label that fills the
        /// field without fabricating a maturity rating. Set to empty to leave such tracks blank.
        /// </summary>
        public string UnidentifiedMusicRating { get; set; } = "Unrated";

        /// <summary>
        /// Gets or sets the rating applied to a movie or show that has no rating Jellyfin recognizes
        /// (e.g. only a foreign certification, or an "NR"/"Not Rated" string) and that TMDb couldn't
        /// resolve to a recognized one. Defaults to "Unrated"; set to empty to leave it as-is.
        /// </summary>
        public string UnidentifiedVideoRating { get; set; } = "Unrated";

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

        /// <summary>
        /// Gets or sets a value indicating whether music albums are rated from their tracks
        /// (least-restrictive), so parental controls don't hide the album container for lacking a
        /// rating of its own.
        /// </summary>
        public bool RateAlbums { get; set; } = true;

        /// <summary>
        /// Gets or sets a value indicating whether a track that no source could identify inherits
        /// its album's rating (the third fallback), instead of being left to the unidentified rating.
        /// </summary>
        public bool InheritAlbumRatingForTracks { get; set; } = true;

        /// <summary>
        /// Gets or sets a value indicating whether every music artist is rated TV-G.
        /// <para>
        /// This is a deliberate workaround, not a content judgement. When a user has
        /// "Block items with no or unrecognized rating information" enabled (the setting that makes
        /// unrated tracks and albums actually get hidden), Jellyfin also blocks the artist container
        /// itself unless it carries a recognized rating — which breaks browsing music by artist.
        /// Jellyfin gives us no way to exempt artist containers from that block, so the only fix is
        /// to stamp every artist with the lowest recognized rating (TV-G) so the folder always opens.
        /// The real content filtering still happens one level down, at the album and track level.
        /// </para>
        /// </summary>
        public bool RateAllArtistsBrowsable { get; set; } = true;
    }
}
