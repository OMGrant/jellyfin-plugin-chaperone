using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Chaperone.Providers
{
    /// <summary>
    /// Resolves an anime content rating via the Jikan (MyAnimeList) public API.
    /// Used as a fallback by the movie and series providers when TMDb yields nothing.
    /// </summary>
    internal sealed class AnimeRatingResolver
    {
        // Jikan asks for a gentle rate; ~2 req/s -> 500ms spacing. Shared across all callers.
        private static readonly Throttle JikanThrottle = new Throttle(TimeSpan.FromMilliseconds(500));

        private static readonly string[] AnimeProviderKeys = { "AniList", "AniDb", "AniDB", "MyAnimeList", "Mal" };

        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILibraryManager _libraryManager;
        private readonly ILogger _logger;

        public AnimeRatingResolver(
            IHttpClientFactory httpClientFactory,
            ILibraryManager libraryManager,
            ILogger logger)
        {
            _httpClientFactory = httpClientFactory;
            _libraryManager = libraryManager;
            _logger = logger;
        }

        /// <summary>
        /// Heuristic: does this item look like anime?
        /// </summary>
        public bool LooksLikeAnime(BaseItem item)
        {
            if (item.Genres is not null
                && item.Genres.Any(g => string.Equals(g, "Anime", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            if (item.ProviderIds is not null)
            {
                foreach (var key in item.ProviderIds.Keys)
                {
                    if (AnimeProviderKeys.Any(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase)))
                    {
                        return true;
                    }
                }
            }

            try
            {
                var folders = _libraryManager.GetCollectionFolders(item);
                if (folders is not null
                    && folders.Any(f => f.Name is not null
                        && f.Name.Contains("Anime", StringComparison.OrdinalIgnoreCase)))
                {
                    return true;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Chaperone: failed to inspect collection folders for '{Name}'.", item.Name);
            }

            return false;
        }

        /// <summary>
        /// Looks up the MAL rating for the item and maps it to a Jellyfin-style rating string.
        /// Returns null when no rating can be determined.
        /// </summary>
        public async Task<string?> ResolveAsync(BaseItem item, CancellationToken cancellationToken)
        {
            string? malId = null;
            if (item.ProviderIds is not null
                && (item.ProviderIds.TryGetValue("MyAnimeList", out malId)
                    || item.ProviderIds.TryGetValue("Mal", out malId)))
            {
                malId = string.IsNullOrWhiteSpace(malId) ? null : malId;
            }

            string url;
            if (malId is not null)
            {
                url = string.Format(
                    CultureInfo.InvariantCulture,
                    "https://api.jikan.moe/v4/anime/{0}",
                    Uri.EscapeDataString(malId));
            }
            else
            {
                if (string.IsNullOrWhiteSpace(item.Name))
                {
                    return null;
                }

                url = string.Format(
                    CultureInfo.InvariantCulture,
                    "https://api.jikan.moe/v4/anime?q={0}&limit=1",
                    Uri.EscapeDataString(item.Name));
            }

            string? malRating;
            try
            {
                malRating = await FetchRatingAsync(url, malId is not null, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "Chaperone: Jikan lookup failed for '{Name}'.", item.Name);
                return null;
            }

            if (string.IsNullOrWhiteSpace(malRating))
            {
                return null;
            }

            var mapped = MapMalRating(malRating);
            if (mapped is null)
            {
                _logger.LogDebug("Chaperone: unmapped MAL rating '{Rating}' for '{Name}'.", malRating, item.Name);
            }

            return mapped;
        }

        private async Task<string?> FetchRatingAsync(string url, bool byId, CancellationToken cancellationToken)
        {
            var client = _httpClientFactory.CreateClient(NamedClient.Default);

            const int maxAttempts = 3;
            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                using var response = await JikanThrottle.RunAsync(
                    () => client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken),
                    cancellationToken).ConfigureAwait(false);

                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    if (attempt == maxAttempts)
                    {
                        return null;
                    }

                    await Task.Delay(500 * attempt, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    return null;
                }

                await using var stream = await response.Content
                    .ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                using var doc = await JsonDocument
                    .ParseAsync(stream, default, cancellationToken).ConfigureAwait(false);

                if (!doc.RootElement.TryGetProperty("data", out var data))
                {
                    return null;
                }

                JsonElement entry;
                if (byId)
                {
                    entry = data;
                }
                else
                {
                    if (data.ValueKind != JsonValueKind.Array || data.GetArrayLength() == 0)
                    {
                        return null;
                    }

                    entry = data[0];
                }

                if (entry.ValueKind == JsonValueKind.Object
                    && entry.TryGetProperty("rating", out var ratingProp)
                    && ratingProp.ValueKind == JsonValueKind.String)
                {
                    return ratingProp.GetString();
                }

                return null;
            }

            return null;
        }

        private static string? MapMalRating(string malRating)
        {
            // Match on the leading token so slight text variations still map.
            var value = malRating.Trim();

            if (value.StartsWith("G ", StringComparison.OrdinalIgnoreCase) || value.Equals("G", StringComparison.OrdinalIgnoreCase))
            {
                return "G";
            }

            if (value.StartsWith("PG-13", StringComparison.OrdinalIgnoreCase))
            {
                return "PG-13";
            }

            if (value.StartsWith("PG", StringComparison.OrdinalIgnoreCase))
            {
                return "PG";
            }

            if (value.StartsWith("Rx", StringComparison.OrdinalIgnoreCase))
            {
                return "NC-17";
            }

            if (value.StartsWith("R+", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("R ", StringComparison.OrdinalIgnoreCase)
                || value.Equals("R", StringComparison.OrdinalIgnoreCase))
            {
                return "R";
            }

            // Explicit table fallback for exact strings.
            return malRating switch
            {
                "G - All Ages" => "G",
                "PG - Children" => "PG",
                "PG-13 - Teens 13 or older" => "PG-13",
                "R - 17+ (violence & profanity)" => "R",
                "R+ - Mild Nudity" => "R",
                "Rx - Hentai" => "NC-17",
                _ => null
            };
        }
    }
}
