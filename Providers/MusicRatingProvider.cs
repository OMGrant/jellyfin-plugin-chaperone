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
    /// Sets music track ratings based on explicit status via <see cref="RatingService"/>.
    /// </summary>
    public class MusicRatingProvider : ICustomMetadataProvider<Audio>, IHasOrder
    {
        private readonly RatingService _ratingService;
        private readonly ILogger<MusicRatingProvider> _logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="MusicRatingProvider"/> class.
        /// </summary>
        public MusicRatingProvider(RatingService ratingService, ILogger<MusicRatingProvider> logger)
        {
            _ratingService = ratingService;
            _logger = logger;
        }

        /// <inheritdoc />
        public string Name => "Chaperone";

        /// <inheritdoc />
        public int Order => 1000;

        /// <inheritdoc />
        public async Task<ItemUpdateType> FetchAsync(
            Audio item,
            MetadataRefreshOptions options,
            CancellationToken cancellationToken)
        {
            var config = Plugin.Instance?.Configuration;
            if (config is null || !config.Enabled)
            {
                return ItemUpdateType.None;
            }

            if (RatingGate.ShouldSkipExisting(item, config, options))
            {
                _logger.LogDebug("Chaperone: '{Name}' already rated; skipping.", item.Name);
                return ItemUpdateType.None;
            }

            var rating = await _ratingService.GetMusicRatingAsync(item, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(rating)
                || string.Equals(item.OfficialRating, rating, StringComparison.Ordinal))
            {
                return ItemUpdateType.None;
            }

            item.OfficialRating = rating;
            _logger.LogInformation("Chaperone: set '{Rating}' on music '{Name}'.", rating, item.Name);
            return ItemUpdateType.MetadataEdit;
        }
    }
}
