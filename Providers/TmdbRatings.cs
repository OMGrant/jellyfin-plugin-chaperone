using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Net;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Chaperone.Providers
{
    /// <summary>
    /// Helper for fetching certification / content rating strings from TMDb.
    /// </summary>
    internal static class TmdbRatings
    {
        /// <summary>
        /// Fetches a movie certification (e.g. "PG-13", "R"). Returns null if none found.
        /// </summary>
        public static async Task<string?> GetMovieCertificationAsync(
            IHttpClientFactory httpClientFactory,
            string apiKey,
            string tmdbId,
            string preferredCountry,
            Func<string?, bool> isRecognized,
            ILogger logger,
            CancellationToken cancellationToken,
            bool mapForeign = false)
        {
            var url = string.Format(
                CultureInfo.InvariantCulture,
                "https://api.themoviedb.org/3/movie/{0}/release_dates?api_key={1}",
                Uri.EscapeDataString(tmdbId),
                Uri.EscapeDataString(apiKey));

            var byCountry = await FetchAsync(
                httpClientFactory,
                url,
                logger,
                (element) =>
                {
                    var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    if (!element.TryGetProperty("results", out var results)
                        || results.ValueKind != JsonValueKind.Array)
                    {
                        return map;
                    }

                    foreach (var entry in results.EnumerateArray())
                    {
                        if (!entry.TryGetProperty("iso_3166_1", out var countryProp))
                        {
                            continue;
                        }

                        var country = countryProp.GetString();
                        if (string.IsNullOrWhiteSpace(country)
                            || !entry.TryGetProperty("release_dates", out var releases)
                            || releases.ValueKind != JsonValueKind.Array)
                        {
                            continue;
                        }

                        foreach (var release in releases.EnumerateArray())
                        {
                            if (release.TryGetProperty("certification", out var certProp))
                            {
                                var cert = certProp.GetString();
                                if (!string.IsNullOrWhiteSpace(cert) && !map.ContainsKey(country))
                                {
                                    map[country] = cert;
                                    break;
                                }
                            }
                        }
                    }

                    return map;
                },
                cancellationToken).ConfigureAwait(false);

            return Pick(byCountry, preferredCountry, isRecognized, mapForeign, false);
        }

        /// <summary>
        /// Fetches a TV content rating (e.g. "TV-14", "TV-MA"). Returns null if none found.
        /// </summary>
        public static async Task<string?> GetSeriesRatingAsync(
            IHttpClientFactory httpClientFactory,
            string apiKey,
            string tmdbId,
            string preferredCountry,
            Func<string?, bool> isRecognized,
            ILogger logger,
            CancellationToken cancellationToken,
            bool mapForeign = false)
        {
            var url = string.Format(
                CultureInfo.InvariantCulture,
                "https://api.themoviedb.org/3/tv/{0}/content_ratings?api_key={1}",
                Uri.EscapeDataString(tmdbId),
                Uri.EscapeDataString(apiKey));

            var byCountry = await FetchAsync(
                httpClientFactory,
                url,
                logger,
                (element) =>
                {
                    var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    if (!element.TryGetProperty("results", out var results)
                        || results.ValueKind != JsonValueKind.Array)
                    {
                        return map;
                    }

                    foreach (var entry in results.EnumerateArray())
                    {
                        if (!entry.TryGetProperty("iso_3166_1", out var countryProp)
                            || !entry.TryGetProperty("rating", out var ratingProp))
                        {
                            continue;
                        }

                        var country = countryProp.GetString();
                        var rating = ratingProp.GetString();
                        if (!string.IsNullOrWhiteSpace(country)
                            && !string.IsNullOrWhiteSpace(rating)
                            && !map.ContainsKey(country))
                        {
                            map[country] = rating;
                        }
                    }

                    return map;
                },
                cancellationToken).ConfigureAwait(false);

            return Pick(byCountry, preferredCountry, isRecognized, mapForeign, true);
        }

        /// <summary>
        /// Finds the best TMDb movie or TV match for a free-text title (used for fan edits, which have no
        /// TMDb entry of their own). Returns ("movie"|"tv", id, matched title), or null on no match.
        /// </summary>
        public static async Task<(string MediaType, string Id, string Title)?> SearchAsync(
            IHttpClientFactory httpClientFactory,
            string apiKey,
            string query,
            ILogger logger,
            CancellationToken cancellationToken)
        {
            var url = string.Format(
                CultureInfo.InvariantCulture,
                "https://api.themoviedb.org/3/search/multi?query={0}&include_adult=false&api_key={1}",
                Uri.EscapeDataString(query),
                Uri.EscapeDataString(apiKey));

            try
            {
                var client = httpClientFactory.CreateClient(NamedClient.Default);
                using var response = await client
                    .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    return null;
                }

                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                using var doc = await JsonDocument.ParseAsync(stream, default, cancellationToken).ConfigureAwait(false);
                if (!doc.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
                {
                    return null;
                }

                // TMDb orders by relevance; take the first movie or show (skip people).
                foreach (var r in results.EnumerateArray())
                {
                    var type = r.TryGetProperty("media_type", out var mt) ? mt.GetString() : null;
                    if ((type == "movie" || type == "tv") && r.TryGetProperty("id", out var idProp))
                    {
                        var title = r.TryGetProperty(type == "movie" ? "title" : "name", out var t) ? t.GetString() ?? string.Empty : string.Empty;
                        return (type!, idProp.GetRawText(), title);
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogDebug(ex, "Chaperone: TMDb search for '{Query}' failed.", query);
            }

            return null;
        }

        private static async Task<Dictionary<string, string>> FetchAsync(
            IHttpClientFactory httpClientFactory,
            string url,
            ILogger logger,
            Func<JsonElement, Dictionary<string, string>> parse,
            CancellationToken cancellationToken)
        {
            var empty = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var client = httpClientFactory.CreateClient(NamedClient.Default);
                using var response = await client
                    .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    return empty;
                }

                await using var stream = await response.Content
                    .ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                using var doc = await JsonDocument
                    .ParseAsync(stream, default, cancellationToken).ConfigureAwait(false);

                return parse(doc.RootElement);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogDebug(ex, "Chaperone: TMDb request failed.");
                return empty;
            }
        }

        private static string? Pick(
            Dictionary<string, string> byCountry,
            string preferredCountry,
            Func<string?, bool> isRecognized,
            bool mapForeign,
            bool forSeries)
        {
            if (byCountry.Count == 0)
            {
                return null;
            }

            // Prefer the configured country, then US, then any other region — but only a
            // certification Jellyfin can actually score. A foreign-only format (e.g. "12", "0+")
            // that Jellyfin can't recognize is skipped rather than written, since it would just
            // trip the "unrecognized rating" block. Returns null when nothing is recognized.
            if (!string.IsNullOrWhiteSpace(preferredCountry)
                && byCountry.TryGetValue(preferredCountry, out var preferred)
                && isRecognized(preferred))
            {
                return preferred;
            }

            if (byCountry.TryGetValue("US", out var us) && isRecognized(us))
            {
                return us;
            }

            // No preferred/US certification: optionally translate a foreign one to its nearest US
            // equivalent (German "12" -> PG-13) rather than writing the foreign label as-is.
            if (mapForeign)
            {
                var mapped = ForeignRatings.ToUs(byCountry, forSeries);
                if (isRecognized(mapped))
                {
                    return mapped;
                }
            }

            foreach (var kvp in byCountry)
            {
                if (isRecognized(kvp.Value))
                {
                    return kvp.Value;
                }
            }

            return null;
        }
    }
}
