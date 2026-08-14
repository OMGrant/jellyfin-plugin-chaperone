using System;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Chaperone.Providers
{
    /// <summary>
    /// Sets movie official ratings via <see cref="RatingService"/> (TMDb, anime fallback).
    /// </summary>
    public class MovieRatingProvider : ICustomMetadataProvider<Movie>, IHasOrder
    {
        private readonly RatingService _ratingService;
        private readonly ILogger<MovieRatingProvider> _logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="MovieRatingProvider"/> class.
        /// </summary>
        public MovieRatingProvider(RatingService ratingService, ILogger<MovieRatingProvider> logger)
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
            Movie item,
            MetadataRefreshOptions options,
            CancellationToken cancellationToken)
        {
            var config = Plugin.Instance?.Configuration;
            if (config is null || !config.Enabled)
            {
                return ItemUpdateType.None;
            }

            if (!_ratingService.ShouldRate(item, options))
            {
                _logger.LogDebug("Chaperone: movie '{Name}' already recognizably rated; skipping.", item.Name);
                return ItemUpdateType.None;
            }

            var rating = await _ratingService.GetMovieRatingAsync(item, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(rating)
                || string.Equals(item.OfficialRating, rating, StringComparison.Ordinal))
            {
                if (string.IsNullOrWhiteSpace(rating))
                {
                    _logger.LogDebug("Chaperone: no rating found for movie '{Name}'.", item.Name);
                }

                return ItemUpdateType.None;
            }

            item.OfficialRating = rating;
            _logger.LogInformation("Chaperone: set '{Rating}' on movie '{Name}'.", rating, item.Name);
            return ItemUpdateType.MetadataEdit;
        }
    }
}
