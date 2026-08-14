using System;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Chaperone.Providers
{
    /// <summary>
    /// Rates a music album by deriving from its tracks' ratings (least restrictive), so parental
    /// controls don't hide the whole album container just because it has no rating of its own.
    /// </summary>
    public class AlbumRatingProvider : ICustomMetadataProvider<MusicAlbum>, IHasOrder
    {
        private readonly RatingService _ratingService;
        private readonly ILogger<AlbumRatingProvider> _logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="AlbumRatingProvider"/> class.
        /// </summary>
        public AlbumRatingProvider(RatingService ratingService, ILogger<AlbumRatingProvider> logger)
        {
            _ratingService = ratingService;
            _logger = logger;
        }

        /// <inheritdoc />
        public string Name => "Chaperone";

        // Run after the track provider (Order 1000) so child ratings are settled first.
        /// <inheritdoc />
        public int Order => 1100;

        /// <inheritdoc />
        public Task<ItemUpdateType> FetchAsync(
            MusicAlbum item,
            MetadataRefreshOptions options,
            CancellationToken cancellationToken)
        {
            var config = Plugin.Instance?.Configuration;
            if (config is null || !config.Enabled || !config.EnableMusic)
            {
                return Task.FromResult(ItemUpdateType.None);
            }

            if (RatingGate.ShouldSkipExisting(item, config, options))
            {
                return Task.FromResult(ItemUpdateType.None);
            }

            var rating = _ratingService.DeriveAlbumRating(item);
            if (string.IsNullOrWhiteSpace(rating)
                || string.Equals(item.OfficialRating, rating, StringComparison.Ordinal))
            {
                return Task.FromResult(ItemUpdateType.None);
            }

            item.OfficialRating = rating;
            _logger.LogInformation("Chaperone: set '{Rating}' on album '{Name}'.", rating, item.Name);
            return Task.FromResult(ItemUpdateType.MetadataEdit);
        }
    }
}
