using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Chaperone.Providers;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Chaperone
{
    /// <summary>
    /// Scheduled task that scans the whole library and fills in missing parental ratings.
    /// Manual-run only (no default triggers).
    /// </summary>
    public class LibraryScanTask : IScheduledTask
    {
        private readonly ILibraryManager _libraryManager;
        private readonly RatingService _ratingService;
        private readonly ILogger<LibraryScanTask> _logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="LibraryScanTask"/> class.
        /// </summary>
        public LibraryScanTask(
            ILibraryManager libraryManager,
            RatingService ratingService,
            ILogger<LibraryScanTask> logger)
        {
            _libraryManager = libraryManager;
            _ratingService = ratingService;
            _logger = logger;
        }

        /// <inheritdoc />
        public string Name => "Chaperone: Scan library for missing ratings";

        /// <inheritdoc />
        public string Key => "ChaperoneLibraryScan";

        /// <inheritdoc />
        public string Description =>
            "Looks up every music track, movie, and show with no parental rating and fills it in "
            + "using Deezer, TMDb, and MyAnimeList.";

        /// <inheritdoc />
        public string Category => "Chaperone";

        /// <inheritdoc />
        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            return Array.Empty<TaskTriggerInfo>();
        }

        /// <inheritdoc />
        public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        {
            var config = Plugin.Instance?.Configuration;
            if (config is null || !config.Enabled)
            {
                _logger.LogInformation("Chaperone scan: plugin disabled; nothing to do.");
                progress.Report(100);
                return;
            }

            var items = new List<BaseItem>();

            if (config.EnableMusic)
            {
                items.AddRange(GetItems(BaseItemKind.Audio));
            }

            // Movies and series are also scanned when only the anime fallback is enabled,
            // matching how the metadata providers behave.
            if (config.EnableMovies || config.EnableAnime)
            {
                items.AddRange(GetItems(BaseItemKind.Movie));
            }

            if (config.EnableShows || config.EnableAnime)
            {
                items.AddRange(GetItems(BaseItemKind.Series));
            }

            // Albums are rated last, after their tracks, by deriving from child ratings. This keeps
            // parental controls from hiding the album container just for lacking a rating of its own.
            var albums = config.EnableMusic
                ? GetItems(BaseItemKind.MusicAlbum)
                : (IReadOnlyList<BaseItem>)Array.Empty<BaseItem>();

            var total = items.Count + albums.Count;
            _logger.LogInformation(
                "Chaperone scan: starting over {Total} item(s) ({Tracks} tracks/movies/shows, {Albums} albums).",
                total,
                items.Count,
                albums.Count);
            progress.Report(0);

            var scanned = 0;
            var rated = 0;
            var processed = 0;

            for (var i = 0; i < items.Count; i++)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    _logger.LogInformation(
                        "Chaperone scan: cancelled after scanning {Scanned}/{Total} (rated {Rated}).",
                        scanned,
                        total,
                        rated);
                    return;
                }

                var item = items[i];
                scanned++;

                var needsRating = string.IsNullOrEmpty(item.OfficialRating) || config.OverwriteExisting;
                if (needsRating)
                {
                    try
                    {
                        var rating = await ResolveRatingAsync(item, cancellationToken).ConfigureAwait(false);
                        if (!string.IsNullOrWhiteSpace(rating)
                            && !string.Equals(item.OfficialRating, rating, StringComparison.Ordinal))
                        {
                            item.OfficialRating = rating;
                            await _libraryManager.UpdateItemAsync(
                                item,
                                item.GetParent(),
                                ItemUpdateType.MetadataEdit,
                                cancellationToken).ConfigureAwait(false);
                            rated++;
                            _logger.LogInformation(
                                "Chaperone scan: set '{Rating}' on '{Name}'.",
                                rating,
                                item.Name);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Chaperone scan: failed to rate '{Name}'.", item.Name);
                    }
                }

                processed++;
                progress.Report(processed * 100.0 / Math.Max(total, 1));
            }

            for (var i = 0; i < albums.Count; i++)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    _logger.LogInformation(
                        "Chaperone scan: cancelled after scanning {Scanned}/{Total} (rated {Rated}).",
                        scanned,
                        total,
                        rated);
                    return;
                }

                var album = albums[i];
                scanned++;

                if (album is MusicAlbum musicAlbum)
                {
                    try
                    {
                        // Derive the album's own rating from its identified tracks (least restrictive).
                        var derived = config.RateAlbums ? _ratingService.DeriveAlbumRating(musicAlbum) : null;

                        var albumNeedsRating = string.IsNullOrEmpty(album.OfficialRating) || config.OverwriteExisting;
                        if (!string.IsNullOrWhiteSpace(derived)
                            && albumNeedsRating
                            && !string.Equals(album.OfficialRating, derived, StringComparison.Ordinal))
                        {
                            album.OfficialRating = derived;
                            await _libraryManager.UpdateItemAsync(
                                album,
                                album.GetParent(),
                                ItemUpdateType.MetadataEdit,
                                cancellationToken).ConfigureAwait(false);
                            rated++;
                            _logger.LogInformation(
                                "Chaperone scan: set '{Rating}' on album '{Name}'.",
                                derived,
                                album.Name);
                        }

                        // Third fallback: a track that neither Deezer nor MusicBrainz could identify
                        // inherits its album's rating, so the plugin's promise to fill the gap holds
                        // instead of leaving the track unrated (and, for a rated album, needlessly hidden).
                        var albumRating = !string.IsNullOrWhiteSpace(derived) ? derived : album.OfficialRating;
                        if (config.InheritAlbumRatingForTracks && !string.IsNullOrWhiteSpace(albumRating))
                        {
                            foreach (var track in GetChildAudio(musicAlbum))
                            {
                                if (!string.IsNullOrEmpty(track.OfficialRating))
                                {
                                    continue;
                                }

                                track.OfficialRating = albumRating;
                                await _libraryManager.UpdateItemAsync(
                                    track,
                                    track.GetParent(),
                                    ItemUpdateType.MetadataEdit,
                                    cancellationToken).ConfigureAwait(false);
                                rated++;
                                _logger.LogInformation(
                                    "Chaperone scan: inherited album rating '{Rating}' for unmatched track '{Name}'.",
                                    albumRating,
                                    track.Name);
                            }
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Chaperone scan: failed to rate album '{Name}'.", album.Name);
                    }
                }

                processed++;
                progress.Report(processed * 100.0 / Math.Max(total, 1));
            }

            // Artist workaround: stamp every artist TV-G so the container stays browsable when the
            // user has "Block items with no or unrecognized rating information" on (Jellyfin blocks
            // unrated artist containers and gives us no way to exempt them). Not a content judgement;
            // real filtering happens at the album/track level below.
            if (config.EnableMusic && config.RateAllArtistsBrowsable)
            {
                foreach (var artist in GetItems(BaseItemKind.MusicArtist))
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        _logger.LogInformation(
                            "Chaperone scan: cancelled during artist pass (rated {Rated}).",
                            rated);
                        return;
                    }

                    if (!string.IsNullOrEmpty(artist.OfficialRating))
                    {
                        continue;
                    }

                    artist.OfficialRating = "TV-G";
                    await _libraryManager.UpdateItemAsync(
                        artist,
                        artist.GetParent(),
                        ItemUpdateType.MetadataEdit,
                        cancellationToken).ConfigureAwait(false);
                    rated++;
                    _logger.LogInformation(
                        "Chaperone scan: set 'TV-G' on artist '{Name}' (browsable workaround).",
                        artist.Name);
                }
            }

            // Final fallback: any music track still unrated after Deezer, MusicBrainz, and album
            // inheritance gets the configured "unidentified" rating (default "Unrated"), so the
            // plugin never leaves a gap. Runs last so real ratings and album inheritance win first.
            if (config.EnableMusic && !string.IsNullOrWhiteSpace(config.UnidentifiedMusicRating))
            {
                foreach (var track in GetItems(BaseItemKind.Audio))
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        _logger.LogInformation(
                            "Chaperone scan: cancelled during final fallback (rated {Rated}).",
                            rated);
                        return;
                    }

                    if (!string.IsNullOrEmpty(track.OfficialRating))
                    {
                        continue;
                    }

                    track.OfficialRating = config.UnidentifiedMusicRating;
                    await _libraryManager.UpdateItemAsync(
                        track,
                        track.GetParent(),
                        ItemUpdateType.MetadataEdit,
                        cancellationToken).ConfigureAwait(false);
                    rated++;
                    _logger.LogInformation(
                        "Chaperone scan: marked unidentifiable track '{Name}' as '{Rating}'.",
                        track.Name,
                        config.UnidentifiedMusicRating);
                }
            }

            progress.Report(100);
            _logger.LogInformation(
                "Chaperone scan: complete. Scanned {Scanned}, rated {Rated}.",
                scanned,
                rated);

            // Bulk rating edits leave Jellyfin's folder-level parental index stale: an album's or
            // artist's effective rating (what parental controls actually filter on) is only recomputed
            // during a library scan, not when individual items are edited. Queue one so the new ratings
            // take effect for restricted users without anyone having to run "Scan Media Library" by hand.
            if (rated > 0)
            {
                _logger.LogInformation(
                    "Chaperone scan: queuing a library scan so parental filtering picks up the new ratings.");
                _libraryManager.QueueLibraryScan();
            }
        }

        private IReadOnlyList<BaseItem> GetItems(BaseItemKind kind)
        {
            var query = new InternalItemsQuery
            {
                IncludeItemTypes = new[] { kind },
                Recursive = true,
                IsVirtualItem = false
            };

            return _libraryManager.GetItemList(query);
        }

        private IReadOnlyList<BaseItem> GetChildAudio(BaseItem album)
        {
            var query = new InternalItemsQuery
            {
                Parent = album,
                Recursive = true,
                IncludeItemTypes = new[] { BaseItemKind.Audio },
                IsVirtualItem = false
            };

            return _libraryManager.GetItemList(query);
        }

        private async Task<string?> ResolveRatingAsync(BaseItem item, CancellationToken cancellationToken)
        {
            switch (item)
            {
                case Audio audio:
                    return await _ratingService.GetMusicRatingAsync(audio, cancellationToken).ConfigureAwait(false);
                case Movie movie:
                    return await _ratingService.GetMovieRatingAsync(movie, cancellationToken).ConfigureAwait(false);
                case Series series:
                    return await _ratingService.GetSeriesRatingAsync(series, cancellationToken).ConfigureAwait(false);
                default:
                    return null;
            }
        }
    }
}
