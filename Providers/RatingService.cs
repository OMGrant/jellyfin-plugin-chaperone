using System;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Chaperone.Providers
{
    /// <summary>
    /// Shared, DI-registered service that computes the parental rating to apply to an item.
    /// Used by both the metadata providers and the library-scan scheduled task, so the
    /// global MusicBrainz 1/s throttle is enforced consistently from every entry point.
    /// </summary>
    public class RatingService
    {
        private const string UserAgent = "Chaperone/1.0.0 ( +https://github.com/OMGrant/jellyfin-plugin-chaperone )";

        // MusicBrainz allows at most 1 request/second globally.
        private static readonly Throttle MusicBrainzThrottle = new Throttle(TimeSpan.FromMilliseconds(1100));

        private static readonly string[] FeatMarkers =
        {
            " feat.", " feat ", " ft.", " ft ", " featuring "
        };

        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IServerConfigurationManager _serverConfig;
        private readonly ILibraryManager _libraryManager;
        private readonly ILogger<RatingService> _logger;
        private readonly AnimeRatingResolver _anime;

        /// <summary>
        /// Initializes a new instance of the <see cref="RatingService"/> class.
        /// </summary>
        public RatingService(
            IHttpClientFactory httpClientFactory,
            IServerConfigurationManager serverConfig,
            ILibraryManager libraryManager,
            ILogger<RatingService> logger)
        {
            _httpClientFactory = httpClientFactory;
            _serverConfig = serverConfig;
            _libraryManager = libraryManager;
            _logger = logger;
            _anime = new AnimeRatingResolver(httpClientFactory, libraryManager, logger);
        }

        /// <summary>
        /// Computes the rating string to apply to a music track, or null if none can be determined.
        /// </summary>
        public async Task<string?> GetMusicRatingAsync(Audio item, CancellationToken cancellationToken)
        {
            var config = Plugin.Instance?.Configuration;
            if (config is null || !config.EnableMusic)
            {
                return null;
            }

            bool? isExplicit = null;

            // 1) Fast path: fuzzy Deezer search by artist + normalized title (no throttle).
            var fuzzy = FuzzyResult.Miss;
            try
            {
                fuzzy = await GetExplicitByFuzzySearchAsync(item, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "Chaperone: fuzzy Deezer search failed for '{Name}'.", item.Name);
            }

            if (fuzzy.Outcome == FuzzyOutcome.Confident)
            {
                // Confident + unambiguous: trust the fast Deezer result, skip MusicBrainz.
                isExplicit = fuzzy.Explicit;
                _logger.LogDebug(
                    "Chaperone: confident fuzzy Deezer match for '{Name}' (explicit={Explicit}).",
                    item.Name,
                    fuzzy.Explicit);
            }
            else
            {
                // 2) MISS or AMBIGUOUS (clean + explicit both present) -> exact ISRC path.
                _logger.LogDebug(
                    "Chaperone: fuzzy result for '{Name}' was {Outcome}; falling back to ISRC.",
                    item.Name,
                    fuzzy.Outcome);

                var mbid = GetProviderId(item, "MusicBrainzRecording") ?? GetProviderId(item, "MusicBrainzTrack");
                if (!string.IsNullOrWhiteSpace(mbid))
                {
                    try
                    {
                        var isrc = await GetFirstIsrcAsync(mbid!, cancellationToken).ConfigureAwait(false);
                        if (!string.IsNullOrWhiteSpace(isrc))
                        {
                            isExplicit = await GetExplicitByIsrcAsync(isrc!, cancellationToken).ConfigureAwait(false);
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.LogDebug(ex, "Chaperone: ISRC path failed for '{Name}'.", item.Name);
                    }
                }
            }

            if (isExplicit is null)
            {
                _logger.LogDebug("Chaperone: no confident match for '{Name}'; leaving unrated.", item.Name);
                return null;
            }

            var newRating = isExplicit.Value ? config.ExplicitRating : config.CleanRating;
            return string.IsNullOrWhiteSpace(newRating) ? null : newRating;
        }

        /// <summary>
        /// Computes the rating string to apply to a movie, or null if none can be determined.
        /// TMDb certifications first, then an anime (MyAnimeList) fallback.
        /// </summary>
        public async Task<string?> GetMovieRatingAsync(Movie item, CancellationToken cancellationToken)
        {
            var config = Plugin.Instance?.Configuration;
            if (config is null)
            {
                return null;
            }

            string? rating = null;

            if (config.EnableMovies
                && item.ProviderIds is not null
                && item.ProviderIds.TryGetValue("Tmdb", out var tmdbId)
                && !string.IsNullOrWhiteSpace(tmdbId)
                && !string.IsNullOrWhiteSpace(config.TmdbApiKey))
            {
                var country = _serverConfig.Configuration.MetadataCountryCode ?? "US";
                rating = await TmdbRatings.GetMovieCertificationAsync(
                    _httpClientFactory,
                    config.TmdbApiKey,
                    tmdbId!,
                    country,
                    _logger,
                    cancellationToken).ConfigureAwait(false);
            }

            if (string.IsNullOrWhiteSpace(rating) && config.EnableAnime && _anime.LooksLikeAnime(item))
            {
                rating = await _anime.ResolveAsync(item, cancellationToken).ConfigureAwait(false);
            }

            return string.IsNullOrWhiteSpace(rating) ? null : rating;
        }

        /// <summary>
        /// Computes the rating string to apply to a series, or null if none can be determined.
        /// TMDb content ratings first, then an anime (MyAnimeList) fallback.
        /// </summary>
        public async Task<string?> GetSeriesRatingAsync(Series item, CancellationToken cancellationToken)
        {
            var config = Plugin.Instance?.Configuration;
            if (config is null)
            {
                return null;
            }

            string? rating = null;

            if (config.EnableShows
                && item.ProviderIds is not null
                && item.ProviderIds.TryGetValue("Tmdb", out var tmdbId)
                && !string.IsNullOrWhiteSpace(tmdbId)
                && !string.IsNullOrWhiteSpace(config.TmdbApiKey))
            {
                var country = _serverConfig.Configuration.MetadataCountryCode ?? "US";
                rating = await TmdbRatings.GetSeriesRatingAsync(
                    _httpClientFactory,
                    config.TmdbApiKey,
                    tmdbId!,
                    country,
                    _logger,
                    cancellationToken).ConfigureAwait(false);
            }

            if (string.IsNullOrWhiteSpace(rating) && config.EnableAnime && _anime.LooksLikeAnime(item))
            {
                rating = await _anime.ResolveAsync(item, cancellationToken).ConfigureAwait(false);
            }

            return string.IsNullOrWhiteSpace(rating) ? null : rating;
        }

        private static string? GetProviderId(Audio item, string key)
        {
            if (item.ProviderIds is not null
                && item.ProviderIds.TryGetValue(key, out var value)
                && !string.IsNullOrWhiteSpace(value))
            {
                return value;
            }

            return null;
        }

        private async Task<string?> GetFirstIsrcAsync(string mbid, CancellationToken cancellationToken)
        {
            var url = string.Format(
                CultureInfo.InvariantCulture,
                "https://musicbrainz.org/ws/2/recording/{0}?inc=isrcs&fmt=json",
                Uri.EscapeDataString(mbid));

            var client = _httpClientFactory.CreateClient(NamedClient.Default);

            // Retry once on 503 (MusicBrainz "please wait").
            for (var attempt = 1; attempt <= 2; attempt++)
            {
                using var response = await MusicBrainzThrottle.RunAsync(
                    () =>
                    {
                        var request = new HttpRequestMessage(HttpMethod.Get, url);
                        request.Headers.UserAgent.ParseAdd(UserAgent);
                        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                        return client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                    },
                    cancellationToken).ConfigureAwait(false);

                if (response.StatusCode == HttpStatusCode.ServiceUnavailable && attempt == 1)
                {
                    await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
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

                if (doc.RootElement.TryGetProperty("isrcs", out var isrcs)
                    && isrcs.ValueKind == JsonValueKind.Array
                    && isrcs.GetArrayLength() > 0)
                {
                    return isrcs[0].GetString();
                }

                return null;
            }

            return null;
        }

        private async Task<bool?> GetExplicitByIsrcAsync(string isrc, CancellationToken cancellationToken)
        {
            var url = string.Format(
                CultureInfo.InvariantCulture,
                "https://api.deezer.com/track/isrc:{0}",
                Uri.EscapeDataString(isrc));

            var client = _httpClientFactory.CreateClient(NamedClient.Default);

            using var response = await SendDeezerAsync(client, url, cancellationToken).ConfigureAwait(false);
            if (response is null || !response.IsSuccessStatusCode)
            {
                return null;
            }

            await using var stream = await response.Content
                .ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var doc = await JsonDocument
                .ParseAsync(stream, default, cancellationToken).ConfigureAwait(false);

            var root = doc.RootElement;

            // Deezer returns an {"error":{...}} object on a miss.
            if (root.TryGetProperty("error", out _))
            {
                return null;
            }

            if (root.TryGetProperty("explicit_lyrics", out var exp))
            {
                return exp.ValueKind == JsonValueKind.True;
            }

            return null;
        }

        private async Task<FuzzyResult> GetExplicitByFuzzySearchAsync(Audio item, CancellationToken cancellationToken)
        {
            var title = item.Name;
            if (string.IsNullOrWhiteSpace(title))
            {
                return FuzzyResult.Miss;
            }

            var artist = item.Artists?.FirstOrDefault();
            if (string.IsNullOrWhiteSpace(artist))
            {
                artist = item.AlbumArtists?.FirstOrDefault();
            }

            if (string.IsNullOrWhiteSpace(artist))
            {
                return FuzzyResult.Miss;
            }

            var query = string.Format(
                CultureInfo.InvariantCulture,
                "artist:\"{0}\" track:\"{1}\"",
                artist,
                title);

            var url = string.Format(
                CultureInfo.InvariantCulture,
                "https://api.deezer.com/search?q={0}&limit=5",
                Uri.EscapeDataString(query));

            var client = _httpClientFactory.CreateClient(NamedClient.Default);

            using var response = await SendDeezerAsync(client, url, cancellationToken).ConfigureAwait(false);
            if (response is null || !response.IsSuccessStatusCode)
            {
                return FuzzyResult.Miss;
            }

            await using var stream = await response.Content
                .ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var doc = await JsonDocument
                .ParseAsync(stream, default, cancellationToken).ConfigureAwait(false);

            if (!doc.RootElement.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Array)
            {
                return FuzzyResult.Miss;
            }

            const double confidenceFloor = 0.85;
            var target = Normalize(title);

            var bestExplicit = false;
            var bestScore = 0.0;

            // Track whether the close-title matches contain both a clean and an explicit
            // version of the same song (ambiguous -> don't trust the fuzzy pick).
            var sawExplicitAmongClose = false;
            var sawCleanAmongClose = false;

            foreach (var element in data.EnumerateArray())
            {
                if (!element.TryGetProperty("title", out var titleProp))
                {
                    continue;
                }

                var candidate = titleProp.GetString();
                if (string.IsNullOrWhiteSpace(candidate))
                {
                    continue;
                }

                var explicitLyrics = element.TryGetProperty("explicit_lyrics", out var expProp)
                    && expProp.ValueKind == JsonValueKind.True;

                var score = Similarity(target, Normalize(candidate));

                if (score >= confidenceFloor)
                {
                    if (explicitLyrics)
                    {
                        sawExplicitAmongClose = true;
                    }
                    else
                    {
                        sawCleanAmongClose = true;
                    }
                }

                if (score > bestScore)
                {
                    bestScore = score;
                    bestExplicit = explicitLyrics;
                }
            }

            if (bestScore < confidenceFloor)
            {
                return FuzzyResult.Miss;
            }

            if (sawExplicitAmongClose && sawCleanAmongClose)
            {
                // Same song exists both clean and explicit -> need the exact recording via ISRC.
                return new FuzzyResult(FuzzyOutcome.Ambiguous, false);
            }

            return new FuzzyResult(FuzzyOutcome.Confident, bestExplicit);
        }

        private async Task<HttpResponseMessage?> SendDeezerAsync(
            HttpClient client,
            string url,
            CancellationToken cancellationToken)
        {
            const int maxAttempts = 3;
            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                var response = await client
                    .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);

                if (response.StatusCode != HttpStatusCode.TooManyRequests)
                {
                    return response;
                }

                response.Dispose();
                if (attempt == maxAttempts)
                {
                    return null;
                }

                await Task.Delay(200 * attempt, cancellationToken).ConfigureAwait(false);
            }

            return null;
        }

        private static string Normalize(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            // Drop bracketed qualifiers like "(feat. X)", "(Radio Remix)", "[Live]".
            value = RemoveBracketed(value);

            // Drop trailing dash-descriptors like " - Remastered 2011", " - Radio Edit".
            var dashIdx = value.IndexOf(" - ", StringComparison.Ordinal);
            if (dashIdx >= 0)
            {
                value = value.Substring(0, dashIdx);
            }

            var lower = value.ToLowerInvariant();
            foreach (var marker in FeatMarkers)
            {
                var idx = lower.IndexOf(marker, StringComparison.Ordinal);
                if (idx >= 0)
                {
                    lower = lower.Substring(0, idx);
                }
            }

            var sb = new StringBuilder(lower.Length);
            foreach (var ch in lower)
            {
                if (char.IsLetterOrDigit(ch))
                {
                    sb.Append(ch);
                }
                else if (char.IsWhiteSpace(ch))
                {
                    sb.Append(' ');
                }
            }

            return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        }

        private static string RemoveBracketed(string value)
        {
            var sb = new StringBuilder(value.Length);
            var depth = 0;
            foreach (var ch in value)
            {
                if (ch == '(' || ch == '[')
                {
                    depth++;
                    continue;
                }

                if (ch == ')' || ch == ']')
                {
                    if (depth > 0)
                    {
                        depth--;
                    }

                    continue;
                }

                if (depth == 0)
                {
                    sb.Append(ch);
                }
            }

            return sb.ToString();
        }

        private static double Similarity(string a, string b)
        {
            if (a.Length == 0 && b.Length == 0)
            {
                return 1.0;
            }

            if (a.Length == 0 || b.Length == 0)
            {
                return 0.0;
            }

            if (string.Equals(a, b, StringComparison.Ordinal))
            {
                return 1.0;
            }

            var distance = Levenshtein(a, b);
            var maxLen = Math.Max(a.Length, b.Length);
            return 1.0 - ((double)distance / maxLen);
        }

        private static int Levenshtein(string a, string b)
        {
            var prev = new int[b.Length + 1];
            var curr = new int[b.Length + 1];

            for (var j = 0; j <= b.Length; j++)
            {
                prev[j] = j;
            }

            for (var i = 1; i <= a.Length; i++)
            {
                curr[0] = i;
                for (var j = 1; j <= b.Length; j++)
                {
                    var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    curr[j] = Math.Min(
                        Math.Min(curr[j - 1] + 1, prev[j] + 1),
                        prev[j - 1] + cost);
                }

                (prev, curr) = (curr, prev);
            }

            return prev[b.Length];
        }

        private enum FuzzyOutcome
        {
            /// <summary>No confident match.</summary>
            Miss,

            /// <summary>A single confident, unambiguous match.</summary>
            Confident,

            /// <summary>Both a clean and an explicit near-match exist; needs exact ISRC lookup.</summary>
            Ambiguous
        }

        private readonly struct FuzzyResult
        {
            public FuzzyResult(FuzzyOutcome outcome, bool isExplicit)
            {
                Outcome = outcome;
                Explicit = isExplicit;
            }

            public FuzzyOutcome Outcome { get; }

            public bool Explicit { get; }

            public static FuzzyResult Miss => new FuzzyResult(FuzzyOutcome.Miss, false);
        }
    }
}
