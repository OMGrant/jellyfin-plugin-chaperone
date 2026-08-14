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
            CancellationToken cancellationToken)
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

            return Pick(byCountry, preferredCountry, isRecognized);
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
            CancellationToken cancellationToken)
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

            return Pick(byCountry, preferredCountry, isRecognized);
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
            Func<string?, bool> isRecognized)
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
