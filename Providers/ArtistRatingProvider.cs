using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Chaperone.Providers
{
    /// <summary>
    /// Stamps every music artist with TV-G so the artist container stays browsable.
    /// <para>
    /// Workaround, not a content judgement: with "Block items with no or unrecognized rating
    /// information" enabled, Jellyfin blocks the artist container unless it has a recognized rating,
    /// which breaks browsing music by artist. Jellyfin offers no way to exempt artist containers, so
    /// the folder is stamped TV-G (lowest recognized rating) and the real filtering happens at the
    /// album and track level below it. Controlled by <c>RateAllArtistsBrowsable</c>.
    /// </para>
    /// </summary>
    public class ArtistRatingProvider : ICustomMetadataProvider<MusicArtist>, IHasOrder
    {
        // Lowest recognized rating — keeps the artist folder open for everyone.
        private const string BrowsableRating = "TV-G";

        private readonly ILogger<ArtistRatingProvider> _logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="ArtistRatingProvider"/> class.
        /// </summary>
        public ArtistRatingProvider(ILogger<ArtistRatingProvider> logger)
        {
            _logger = logger;
        }

        /// <inheritdoc />
        public string Name => "Chaperone";

        /// <inheritdoc />
        public int Order => 1200;

        /// <inheritdoc />
        public Task<ItemUpdateType> FetchAsync(
            MusicArtist item,
            MetadataRefreshOptions options,
            CancellationToken cancellationToken)
        {
            var config = Plugin.Instance?.Configuration;
            if (config is null || !config.Enabled || !config.EnableMusic || !config.RateAllArtistsBrowsable)
            {
                return Task.FromResult(ItemUpdateType.None);
            }

            // Only fill an artist that has no rating yet; never clobber an existing one.
            if (!string.IsNullOrEmpty(item.OfficialRating))
            {
                return Task.FromResult(ItemUpdateType.None);
            }

            item.OfficialRating = BrowsableRating;
            _logger.LogInformation("Chaperone: set '{Rating}' on artist '{Name}' (browsable workaround).", BrowsableRating, item.Name);
            return Task.FromResult(ItemUpdateType.MetadataEdit);
        }
    }
}
