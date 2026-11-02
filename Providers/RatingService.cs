using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Globalization;
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

        // Deezer's public API is limited to ~50 requests / 5 seconds per IP. Space requests ~150ms
        // apart (well under that) so a large library scan doesn't burst past the limit and start
        // failing lookups. Global (static) so concurrent refreshes + the scan share one budget.
        private static readonly Throttle DeezerThrottle = new Throttle(TimeSpan.FromMilliseconds(150));

        // The iTunes Search API allows roughly 20 requests per minute per IP.
        private static readonly Throttle ItunesThrottle = new Throttle(TimeSpan.FromMilliseconds(3100));

        // LRCLIB is a free community service with no published limit; stay polite at 1 request/second.
        private static readonly Throttle LrclibThrottle = new Throttle(TimeSpan.FromMilliseconds(1000));

        // A track whose own title says it's an instrumental/karaoke version has no lyrics, so it is
        // clean — and must never be judged by the vocal version's explicit flag or lyrics.
        private static readonly Regex InstrumentalTitle = new Regex(
            @"[\(\[\-–]\s*(instrumental|inst\.|karaoke|backing track)\b|\binstrumental version\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly string[] FeatMarkers =
        {
            " feat.", " feat ", " ft.", " ft ", " featuring "
        };

        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IServerConfigurationManager _serverConfig;
        private readonly ILibraryManager _libraryManager;
        private readonly ILocalizationManager _localization;
        private readonly ILogger<RatingService> _logger;
        private readonly AnimeRatingResolver _anime;

        /// <summary>
        /// Initializes a new instance of the <see cref="RatingService"/> class.
        /// </summary>
        public RatingService(
            IHttpClientFactory httpClientFactory,
            IServerConfigurationManager serverConfig,
            ILibraryManager libraryManager,
            ILocalizationManager localization,
            ILogger<RatingService> logger)
        {
            _httpClientFactory = httpClientFactory;
            _serverConfig = serverConfig;
            _libraryManager = libraryManager;
            _localization = localization;
            _logger = logger;
            _anime = new AnimeRatingResolver(httpClientFactory, libraryManager, logger);
        }

        /// <summary>
        /// Returns true when Jellyfin can score the given rating string (i.e. it's a real,
        /// recognized certification rather than blank, "NR", "Not Rated", "Unrated", "12", "0+", …).
        /// </summary>
        public bool IsRecognized(string? rating)
        {
            return !string.IsNullOrEmpty(rating) && _localization.GetRatingScore(rating) is not null;
        }

        /// <summary>
        /// Decides whether Chaperone should try to rate an item: yes when it has no rating, or a
        /// rating Jellyfin can't recognize (so "NR"/"12"/"0+" get re-examined), or on overwrite /
        /// full refresh. A value Chaperone itself uses as its "gave up" marker (the configured
        /// unidentified music/video rating) is treated as terminal and left alone.
        /// </summary>
        public bool ShouldRate(BaseItem item, MetadataRefreshOptions? options)
        {
            var config = Plugin.Instance?.Configuration;
            if (config is null)
            {
                return false;
            }

            var isFullReplace = options is not null
                && (options.ReplaceAllMetadata
                    || options.MetadataRefreshMode == MetadataRefreshMode.FullRefresh);
            if (config.OverwriteExisting || isFullReplace)
            {
                return true;
            }

            var current = item.OfficialRating;
            if (string.IsNullOrEmpty(current))
            {
                return true;
            }

            if (string.Equals(current, config.UnidentifiedMusicRating, StringComparison.Ordinal)
                || string.Equals(current, config.UnidentifiedVideoRating, StringComparison.Ordinal))
            {
                // Our own "gave up" marker: look again (new sources, or upstream data that has appeared
                // since) unless that's switched off. A miss leaves the marker in place.
                return config.RecheckUnidentified;
            }

            return !IsRecognized(current);
        }

        /// <summary>
        /// Derives an album's rating from the ratings already on its tracks, choosing the
        /// <em>least restrictive</em> rating present. This keeps a mixed album browsable — a child
        /// track flagged explicit stays individually blocked, but the album container (and its
        /// clean tracks) remain visible. Returns null when no track carries a usable rating.
        /// </summary>
        public string? DeriveAlbumRating(MusicAlbum album)
        {
            var config = Plugin.Instance?.Configuration;
            if (config is null || !config.EnableMusic)
            {
                return null;
            }

            var query = new InternalItemsQuery
            {
                Parent = album,
                Recursive = true,
                IncludeItemTypes = new[] { BaseItemKind.Audio },
                IsVirtualItem = false
            };

            string? best = null;
            var bestLevel = int.MaxValue;

            foreach (var track in _libraryManager.GetItemList(query))
            {
                var rating = track.OfficialRating;
                if (string.IsNullOrEmpty(rating))
                {
                    continue;
                }

                // Jellyfin 10.11 replaced GetRatingLevel with GetRatingScore; compare by the
                // numeric Score, using SubScore only as a tiebreak within the same Score.
                var score = _localization.GetRatingScore(rating);
                if (score is null)
                {
                    continue;
                }

                var level = (score.Score * 1000) + (score.SubScore ?? 0);
                if (level < bestLevel)
                {
                    bestLevel = level;
                    best = rating;
                }
            }

            return best;
        }

        /// <summary>
        /// Computes the rating string to apply to a music track, or null if none can be determined.
        /// </summary>
        public async Task<string?> GetMusicRatingAsync(Audio item, CancellationToken cancellationToken)
        {
            var result = await GetMusicRatingResultAsync(item, cancellationToken).ConfigureAwait(false);
            return result.Rating;
        }

        /// <summary>
        /// Like <see cref="GetMusicRatingAsync"/> but also reports whether the lookup could not be
        /// completed because of a transient failure (e.g. a Deezer rate limit or network error), so
        /// callers can avoid mistaking a throttled request for an unidentifiable track.
        /// </summary>
        public async Task<MusicRatingResult> GetMusicRatingResultAsync(Audio item, CancellationToken cancellationToken)
        {
            var config = Plugin.Instance?.Configuration;
            if (config is null || !config.EnableMusic)
            {
                return new MusicRatingResult(null, false);
            }

            bool? isExplicit = null;
            var transientFailure = false;

            // 0) An instrumental/karaoke version (by its own title) has no lyrics: clean. Checked first,
            // because the fuzzy searches below drop bracketed qualifiers and would match the vocal version.
            if (config.EnableLyrics && IsInstrumentalTitle(item.Name))
            {
                _logger.LogDebug("Chaperone: '{Name}' is an instrumental by its title; clean.", item.Name);
                return new MusicRatingResult(string.IsNullOrWhiteSpace(config.CleanRating) ? null : config.CleanRating, false);
            }

            // 1) Fast path: fuzzy Deezer search by artist + normalized title (throttled).
            var fuzzy = FuzzyResult.Miss;
            try
            {
                fuzzy = await GetExplicitByFuzzySearchAsync(item, cancellationToken).ConfigureAwait(false);
            }
            catch (TransientLookupException ex)
            {
                transientFailure = true;
                _logger.LogDebug(ex, "Chaperone: fuzzy Deezer lookup for '{Name}' failed transiently.", item.Name);
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
                    catch (TransientLookupException ex)
                    {
                        transientFailure = true;
                        _logger.LogDebug(ex, "Chaperone: ISRC lookup for '{Name}' failed transiently.", item.Name);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.LogDebug(ex, "Chaperone: ISRC path failed for '{Name}'.", item.Name);
                    }
                }
            }

            // 3) Apple Music's catalogue (iTunes Search): a second explicit-flag source with different coverage.
            if (isExplicit is null && config.EnableItunes)
            {
                try
                {
                    isExplicit = await GetExplicitByItunesAsync(item, cancellationToken).ConfigureAwait(false);
                    if (isExplicit is not null)
                    {
                        _logger.LogDebug("Chaperone: iTunes match for '{Name}' (explicit={Explicit}).", item.Name, isExplicit);
                    }
                }
                catch (TransientLookupException ex)
                {
                    transientFailure = true;
                    _logger.LogDebug(ex, "Chaperone: iTunes lookup for '{Name}' failed transiently.", item.Name);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogDebug(ex, "Chaperone: iTunes lookup failed for '{Name}'.", item.Name);
                }
            }

            // 4) Lyrics (LRCLIB): instrumental -> clean; otherwise check the words themselves.
            if (isExplicit is null && config.EnableLyrics)
            {
                try
                {
                    isExplicit = await GetExplicitByLyricsAsync(item, config.LyricsExplicitWords, cancellationToken).ConfigureAwait(false);
                    if (isExplicit is not null)
                    {
                        _logger.LogDebug("Chaperone: lyrics decided '{Name}' (explicit={Explicit}).", item.Name, isExplicit);
                    }
                }
                catch (TransientLookupException ex)
                {
                    transientFailure = true;
                    _logger.LogDebug(ex, "Chaperone: LRCLIB lookup for '{Name}' failed transiently.", item.Name);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogDebug(ex, "Chaperone: LRCLIB lookup failed for '{Name}'.", item.Name);
                }
            }

            if (isExplicit is null)
            {
                _logger.LogDebug("Chaperone: no confident match for '{Name}'; leaving unrated.", item.Name);
                return new MusicRatingResult(null, transientFailure);
            }

            var newRating = isExplicit.Value ? config.ExplicitRating : config.CleanRating;
            return new MusicRatingResult(
                string.IsNullOrWhiteSpace(newRating) ? null : newRating,
                false);
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
                    IsRecognized,
                    _logger,
                    cancellationToken,
                    config.MapForeignRatings).ConfigureAwait(false);
            }

            // A fan edit has no TMDb entry of its own: rate it as its source film or show.
            if (string.IsNullOrWhiteSpace(rating) && config.EnableMovies && IsFanEdit(item, config.FanEditLibraries))
            {
                rating = await GetFanEditRatingAsync(item, config, cancellationToken).ConfigureAwait(false);
            }

            if (string.IsNullOrWhiteSpace(rating) && config.EnableAnime && _anime.LooksLikeAnime(item))
            {
                rating = await _anime.ResolveAsync(item, cancellationToken).ConfigureAwait(false);
            }

            // Only ever hand back a rating Jellyfin can actually score — never a foreign format or
            // "NR" string that would just trip the "unrecognized" block.
            return IsRecognized(rating) ? rating : null;
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
                    IsRecognized,
                    _logger,
                    cancellationToken,
                    config.MapForeignRatings).ConfigureAwait(false);
            }

            if (string.IsNullOrWhiteSpace(rating) && config.EnableAnime && _anime.LooksLikeAnime(item))
            {
                rating = await _anime.ResolveAsync(item, cancellationToken).ConfigureAwait(false);
            }

            // Only ever hand back a rating Jellyfin can actually score.
            return IsRecognized(rating) ? rating : null;
        }

        /// <summary>
        /// True when a track's own title marks it as an instrumental, karaoke or backing-track version.
        /// </summary>
        public static bool IsInstrumentalTitle(string? title)
        {
            return !string.IsNullOrWhiteSpace(title) && InstrumentalTitle.IsMatch(title);
        }

        /// <summary>
        /// Checks lyrics against a comma-separated word list ("word" = whole word, "word*" = prefix).
        /// </summary>
        public static bool LyricsContainExplicit(string lyrics, string wordList)
        {
            if (string.IsNullOrWhiteSpace(lyrics) || string.IsNullOrWhiteSpace(wordList))
            {
                return false;
            }

            foreach (var raw in wordList.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var prefix = raw.EndsWith('*');
                var word = Regex.Escape(prefix ? raw.TrimEnd('*') : raw);
                if (word.Length == 0)
                {
                    continue;
                }

                var pattern = prefix ? $@"\b{word}" : $@"\b{word}\b";
                if (Regex.IsMatch(lyrics, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                {
                    return true;
                }
            }

            return false;
        }

        private bool IsFanEdit(BaseItem item, string? libraries)
        {
            if (string.IsNullOrWhiteSpace(libraries))
            {
                return false;
            }

            var names = new HashSet<string>(
                libraries.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var folder in _libraryManager.GetCollectionFolders(item))
                {
                    if (names.Contains(folder.Name))
                    {
                        return true;
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "Chaperone: couldn't resolve the library of '{Name}'.", item.Name);
            }

            return false;
        }

        /// <summary>
        /// Builds TMDb search terms from a fan edit's title, most specific first: e.g.
        /// "Star Wars - The Empire Strikes Back Revisited (Adywan)" -> "Star Wars The Empire Strikes Back",
        /// "The Empire Strikes Back", "Star Wars"; "Obi-Wan Kenobi: The Patterson Cut" -> "Obi-Wan Kenobi".
        /// </summary>
        public static IReadOnlyList<string> FanEditSearchTerms(string title)
        {
            var noBrackets = Regex.Replace(title, @"\s*[\(\[][^\)\]]*[\)\]]", string.Empty);
            var parts = Regex.Split(noBrackets, @"\s+-\s+|:\s+")
                .Select(p => Regex.Replace(
                    p,
                    @"\b(revisited|despecialized|remastered|extended|fan ?edit|edition|restoration|re-?edit|recut|cut)\b",
                    string.Empty,
                    RegexOptions.IgnoreCase).Trim())
                // drop segments that only named the editor/cut ("The Patterson Cut" -> "The Patterson")
                // or a part number ("Book I"), keeping real titles
                .Where(p => p.Length > 0
                    && !Regex.IsMatch(p, @"^the\s+\w+$", RegexOptions.IgnoreCase)
                    && !Regex.IsMatch(p, @"^(book|part|chapter|vol\.?|volume)\s+[ivx\d]+$", RegexOptions.IgnoreCase))
                .ToList();

            var terms = new List<string>();
            void Add(string t)
            {
                t = Regex.Replace(t, @"\s+", " ").Trim();
                if (t.Length > 1 && !terms.Contains(t, StringComparer.OrdinalIgnoreCase))
                {
                    terms.Add(t);
                }
            }

            if (parts.Count > 1)
            {
                Add(string.Join(' ', parts));
            }

            for (var i = parts.Count - 1; i >= 0; i--)
            {
                Add(parts[i]);
            }

            return terms;
        }

        private async Task<string?> GetFanEditRatingAsync(Movie item, Configuration.PluginConfiguration config, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(config.TmdbApiKey) || string.IsNullOrWhiteSpace(item.Name))
            {
                return null;
            }

            var country = _serverConfig.Configuration.MetadataCountryCode ?? "US";
            foreach (var term in FanEditSearchTerms(item.Name))
            {
                var hit = await TmdbRatings.SearchAsync(_httpClientFactory, config.TmdbApiKey, term, _logger, cancellationToken)
                    .ConfigureAwait(false);
                if (hit is null)
                {
                    continue;
                }

                var (type, id, matched) = hit.Value;
                var rating = type == "movie"
                    ? await TmdbRatings.GetMovieCertificationAsync(_httpClientFactory, config.TmdbApiKey, id, country, IsRecognized, _logger, cancellationToken, config.MapForeignRatings).ConfigureAwait(false)
                    : await TmdbRatings.GetSeriesRatingAsync(_httpClientFactory, config.TmdbApiKey, id, country, IsRecognized, _logger, cancellationToken, config.MapForeignRatings).ConfigureAwait(false);
                if (IsRecognized(rating))
                {
                    _logger.LogInformation(
                        "Chaperone: fan edit '{Name}' rated '{Rating}' from its source '{Source}' (TMDb {Type} {Id}).",
                        item.Name,
                        rating,
                        matched,
                        type,
                        id);
                    return rating;
                }
            }

            return null;
        }

        private static (string? Artist, string? Title) ArtistAndTitle(Audio item)
        {
            var artist = item.Artists?.FirstOrDefault();
            if (string.IsNullOrWhiteSpace(artist))
            {
                artist = item.AlbumArtists?.FirstOrDefault();
            }

            return (artist, item.Name);
        }

        private async Task<bool?> GetExplicitByItunesAsync(Audio item, CancellationToken cancellationToken)
        {
            var (artist, title) = ArtistAndTitle(item);
            if (string.IsNullOrWhiteSpace(artist) || string.IsNullOrWhiteSpace(title))
            {
                return null;
            }

            var url = string.Format(
                CultureInfo.InvariantCulture,
                "https://itunes.apple.com/search?term={0}&entity=song&limit=15",
                Uri.EscapeDataString(artist + " " + title));

            var client = _httpClientFactory.CreateClient(NamedClient.Default);
            HttpResponseMessage response;
            try
            {
                response = await ItunesThrottle.RunAsync(
                    () => client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                throw new TransientLookupException("iTunes request failed", ex);
            }

            using (response)
            {
                if (response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode == 403)
                {
                    throw new TransientLookupException("iTunes rate limit");
                }

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

                const double floor = 0.85;
                var targetTitle = Normalize(title);
                var targetArtist = Normalize(artist);
                var sawExplicit = false;
                var sawClean = false;

                foreach (var r in results.EnumerateArray())
                {
                    var name = r.TryGetProperty("trackName", out var tn) ? tn.GetString() : null;
                    var by = r.TryGetProperty("artistName", out var an) ? an.GetString() : null;
                    var flag = r.TryGetProperty("trackExplicitness", out var te) ? te.GetString() : null;
                    if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(by) || string.IsNullOrWhiteSpace(flag))
                    {
                        continue;
                    }

                    // The artist must match too: a same-titled song by someone else is a different song.
                    var byNorm = Normalize(by);
                    var artistOk = Similarity(targetArtist, byNorm) >= floor
                        || byNorm.StartsWith(targetArtist + " ", StringComparison.Ordinal)
                        || targetArtist.StartsWith(byNorm + " ", StringComparison.Ordinal);
                    if (!artistOk || Similarity(targetTitle, Normalize(name)) < floor)
                    {
                        continue;
                    }

                    if (string.Equals(flag, "explicit", StringComparison.OrdinalIgnoreCase))
                    {
                        sawExplicit = true;
                    }
                    else
                    {
                        // "notExplicit", or "cleaned" (the radio edit of an explicit song).
                        if (string.Equals(flag, "cleaned", StringComparison.OrdinalIgnoreCase))
                        {
                            sawExplicit = true;
                        }

                        sawClean = true;
                    }
                }

                if (sawExplicit && sawClean)
                {
                    // Both versions exist and nothing tells us which one this file is: don't guess.
                    return null;
                }

                return sawExplicit ? true : sawClean ? false : (bool?)null;
            }
        }

        private async Task<bool?> GetExplicitByLyricsAsync(Audio item, string wordList, CancellationToken cancellationToken)
        {
            var (artist, title) = ArtistAndTitle(item);
            if (string.IsNullOrWhiteSpace(artist) || string.IsNullOrWhiteSpace(title))
            {
                return null;
            }

            var query = new StringBuilder("https://lrclib.net/api/search?artist_name=")
                .Append(Uri.EscapeDataString(artist))
                .Append("&track_name=")
                .Append(Uri.EscapeDataString(title));

            var client = _httpClientFactory.CreateClient(NamedClient.Default);
            HttpResponseMessage response;

            // LRCLIB answers 503 "ServerOverloaded … please retry in a moment" when busy (cached songs still
            // answer). Retry twice with a short back-off before deferring the track to the next scan.
            var attempt = 0;
            while (true)
            {
                attempt++;
                try
                {
                    response = await LrclibThrottle.RunAsync(
                        () =>
                        {
                            var request = new HttpRequestMessage(HttpMethod.Get, query.ToString());
                            request.Headers.UserAgent.ParseAdd(UserAgent);
                            return client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                        },
                        cancellationToken).ConfigureAwait(false);
                }
                catch (HttpRequestException ex)
                {
                    throw new TransientLookupException("LRCLIB request failed", ex);
                }

                if (attempt < 3 && (response.StatusCode == HttpStatusCode.ServiceUnavailable || response.StatusCode == HttpStatusCode.TooManyRequests))
                {
                    response.Dispose();
                    await Task.Delay(TimeSpan.FromSeconds(attempt * 3), cancellationToken).ConfigureAwait(false);
                    continue;
                }

                break;
            }

            using (response)
            {
                if (response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500)
                {
                    throw new TransientLookupException("LRCLIB unavailable");
                }

                if (!response.IsSuccessStatusCode)
                {
                    return null;
                }

                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                using var doc = await JsonDocument.ParseAsync(stream, default, cancellationToken).ConfigureAwait(false);
                if (doc.RootElement.ValueKind != JsonValueKind.Array)
                {
                    return null;
                }

                var targetTitle = Normalize(title);
                var targetArtist = Normalize(artist);
                var durationSec = item.RunTimeTicks.HasValue ? TimeSpan.FromTicks(item.RunTimeTicks.Value).TotalSeconds : (double?)null;

                foreach (var r in doc.RootElement.EnumerateArray())
                {
                    var name = r.TryGetProperty("trackName", out var tn) ? tn.GetString() : null;
                    var by = r.TryGetProperty("artistName", out var an) ? an.GetString() : null;
                    if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(by)
                        || Similarity(targetTitle, Normalize(name)) < 0.85
                        || Similarity(targetArtist, Normalize(by)) < 0.85)
                    {
                        continue;
                    }

                    // Same recording? LRCLIB keys lyrics to a duration; a different cut (live, extended)
                    // can have different words, so require the lengths to agree within 5 seconds.
                    if (durationSec.HasValue && r.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number
                        && Math.Abs(d.GetDouble() - durationSec.Value) > 5)
                    {
                        continue;
                    }

                    if (r.TryGetProperty("instrumental", out var inst) && inst.ValueKind == JsonValueKind.True)
                    {
                        return false;
                    }

                    var lyrics = r.TryGetProperty("plainLyrics", out var pl) && pl.ValueKind == JsonValueKind.String ? pl.GetString() : null;
                    if (!string.IsNullOrWhiteSpace(lyrics))
                    {
                        return LyricsContainExplicit(lyrics!, wordList);
                    }
                }

                return null;
            }
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
            if (!response.IsSuccessStatusCode)
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
            if (!response.IsSuccessStatusCode)
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

        private async Task<HttpResponseMessage> SendDeezerAsync(
            HttpClient client,
            string url,
            CancellationToken cancellationToken)
        {
            const int maxAttempts = 3;
            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                HttpResponseMessage response;
                try
                {
                    // Proactively throttle so we stay under Deezer's rate limit instead of relying
                    // on the retry backstop below (which a big burst blows straight past).
                    response = await DeezerThrottle.RunAsync(
                        () => client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken),
                        cancellationToken).ConfigureAwait(false);
                }
                catch (HttpRequestException ex)
                {
                    // Network-level failure: transient, not a "no match".
                    if (attempt == maxAttempts)
                    {
                        throw new TransientLookupException("Deezer request failed", ex);
                    }

                    await Task.Delay(200 * attempt, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (response.StatusCode != HttpStatusCode.TooManyRequests)
                {
                    return response;
                }

                response.Dispose();
                if (attempt == maxAttempts)
                {
                    // Rate-limited even after retries: transient, not a "no match". Signal it so the
                    // caller doesn't mistake a throttled lookup for an unidentifiable track.
                    throw new TransientLookupException("Deezer rate limit exceeded");
                }

                await Task.Delay(200 * attempt, cancellationToken).ConfigureAwait(false);
            }

            throw new TransientLookupException("Deezer request exhausted retries");
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
