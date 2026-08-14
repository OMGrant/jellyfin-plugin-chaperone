using System;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Chaperone.Providers
{
    /// <summary>
    /// Sets series official ratings via <see cref="RatingService"/> (TMDb, anime fallback).
    /// </summary>
    public class SeriesRatingProvider : ICustomMetadataProvider<Series>, IHasOrder
    {
        private readonly RatingService _ratingService;
        private readonly ILogger<SeriesRatingProvider> _logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="SeriesRatingProvider"/> class.
        /// </summary>
        public SeriesRatingProvider(RatingService ratingService, ILogger<SeriesRatingProvider> logger)
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
            Series item,
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
                _logger.LogDebug("Chaperone: series '{Name}' already rated; skipping.", item.Name);
                return ItemUpdateType.None;
            }

            var rating = await _ratingService.GetSeriesRatingAsync(item, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(rating)
                || string.Equals(item.OfficialRating, rating, StringComparison.Ordinal))
            {
                if (string.IsNullOrWhiteSpace(rating))
                {
                    _logger.LogDebug("Chaperone: no rating found for series '{Name}'.", item.Name);
                }

                return ItemUpdateType.None;
            }

            item.OfficialRating = rating;
            _logger.LogInformation("Chaperone: set '{Rating}' on series '{Name}'.", rating, item.Name);
            return ItemUpdateType.MetadataEdit;
        }
    }
}
