using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Chaperone.Providers;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Chaperone.Mcp
{
    /// <summary>
    /// The work behind Chaperone's MCP tools and its review endpoints: find what's unrated, give a model the facts
    /// and lyrics it needs, and take rating proposals — applied at once for trusted clients, queued for an admin
    /// otherwise. Models do the research; Chaperone keeps the record and enforces the rules.
    /// </summary>
    public class ChaperoneTools
    {
        private static Timer? _scanDebounce;

        private readonly ILibraryManager _libraryManager;
        private readonly RatingService _ratings;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<ChaperoneTools> _logger;
        private readonly ITaskManager _taskManager;

        /// <summary>
        /// Initializes a new instance of the <see cref="ChaperoneTools"/> class.
        /// </summary>
        public ChaperoneTools(
            ILibraryManager libraryManager,
            RatingService ratings,
            IHttpClientFactory httpClientFactory,
            ILogger<ChaperoneTools> logger,
            ITaskManager taskManager)
        {
            _taskManager = taskManager;
            _libraryManager = libraryManager;
            _ratings = ratings;
            _httpClientFactory = httpClientFactory;
            _logger = logger;
        }

        /// <summary>The house rules a model must follow, returned by the rating_scale tool.</summary>
        public JsonObject RatingScale()
        {
            var config = Plugin.Instance?.Configuration;
            return new JsonObject
            {
                ["music"] = new JsonObject
                {
                    ["clean"] = config?.CleanRating ?? "TV-G",
                    ["explicit"] = config?.ExplicitRating ?? "TV-MA",
                    ["rule"] = "Music is rated on explicit LANGUAGE only (like the streaming services' explicit flag), not themes. "
                        + "An instrumental is clean. Use the clean or explicit value above."
                },
                ["movies_and_shows"] = new JsonObject
                {
                    ["rule"] = "Use the official certification (MPA: G, PG, PG-13, R, NC-17; US TV: TV-Y, TV-Y7, TV-G, TV-PG, TV-14, TV-MA). "
                        + "Fan edits take their source film's or show's rating; a featurette takes its film's rating. "
                        + "With no official rating, judge from content descriptions and say so in the reason."
                },
                ["unsure"] = $"If you can't establish it from evidence, propose nothing (it stays '{config?.UnidentifiedMusicRating ?? "Unrated"}', "
                    + "which restricted users can't see). Never guess from an artist's style or a title.",
                ["evidence"] = "Give evidence_url and an exact evidence_quote from that page whenever you can: Chaperone fetches the "
                    + "page and checks the quote is there. Proposals without verified evidence are easier to reject.",
                ["recognized_examples"] = new JsonArray("G", "PG", "PG-13", "R", "NC-17", "TV-Y", "TV-Y7", "TV-G", "TV-PG", "TV-14", "TV-MA")
            };
        }

        /// <summary>Lists items that still need a rating.</summary>
        public JsonObject ListUnrated(string? kind, int limit, int offset)
        {
            kind = string.IsNullOrWhiteSpace(kind) ? "all" : kind.ToLowerInvariant();
            var types = kind switch
            {
                "music" => new[] { BaseItemKind.Audio },
                "album" => new[] { BaseItemKind.MusicAlbum },
                "movie" => new[] { BaseItemKind.Movie },
                "show" => new[] { BaseItemKind.Series },
                _ => new[] { BaseItemKind.Audio, BaseItemKind.MusicAlbum, BaseItemKind.Movie, BaseItemKind.Series }
            };

            var items = _libraryManager.GetItemList(new InternalItemsQuery
                {
                    IncludeItemTypes = types,
                    Recursive = true,
                    IsVirtualItem = false
                })
                .Where(NeedsRating)
                .OrderBy(i => i.GetType().Name)
                .ThenBy(i => (i as Audio)?.AlbumArtists?.FirstOrDefault() ?? string.Empty)
                .ThenBy(i => (i as Audio)?.Album ?? string.Empty)
                .ThenBy(i => i.Name)
                .ToList();

            var page = new JsonArray();
            foreach (var item in items.Skip(Math.Max(0, offset)).Take(Math.Clamp(limit, 1, 200)))
            {
                page.Add(Summary(item));
            }

            return new JsonObject { ["total"] = items.Count, ["offset"] = offset, ["items"] = page };
        }

        /// <summary>Full details for one item.</summary>
        public JsonObject GetItem(string id)
        {
            var item = Find(id);
            var o = Summary(item);
            o["overview"] = item.Overview;
            o["genres"] = new JsonArray(item.Genres.Select(g => (JsonNode)JsonValue.Create(g)!).ToArray());
            o["provider_ids"] = new JsonObject(item.ProviderIds.Select(kv => new KeyValuePair<string, JsonNode?>(kv.Key, kv.Value)));
            o["file"] = System.IO.Path.GetFileName(item.Path);
            if (item is MusicAlbum album)
            {
                var tracks = new JsonArray();
                foreach (var t in _libraryManager.GetItemList(new InternalItemsQuery { Parent = album, Recursive = true, IncludeItemTypes = new[] { BaseItemKind.Audio } }))
                {
                    tracks.Add(new JsonObject { ["id"] = t.Id.ToString("N"), ["name"] = t.Name, ["rating"] = t.OfficialRating });
                }

                o["tracks"] = tracks;
            }

            return o;
        }

        /// <summary>LRCLIB lyrics for a track, via Chaperone's rate-limited, retrying lookup.</summary>
        public async Task<JsonObject> GetLyricsAsync(string id, CancellationToken cancellationToken)
        {
            if (Find(id) is not Audio track)
            {
                throw new ArgumentException("Lyrics are only available for music tracks.");
            }

            var o = new JsonObject { ["id"] = id, ["name"] = track.Name, ["title_says_instrumental"] = RatingService.IsInstrumentalTitle(track.Name) };
            try
            {
                var found = await _ratings.FindLyricsAsync(track, cancellationToken).ConfigureAwait(false);
                if (found is null)
                {
                    o["status"] = "not found";
                }
                else
                {
                    o["status"] = "found";
                    o["source"] = "LRCLIB";
                    o["matched"] = $"{found.MatchedArtist} – {found.MatchedTitle}";
                    o["instrumental"] = found.Instrumental;
                    o["lyrics"] = found.Lyrics;
                }
            }
            catch (TransientLookupException)
            {
                o["status"] = "LRCLIB busy — try again later";
            }

            return o;
        }

        /// <summary>Records a proposal; applies it at once when the client is trusted.</summary>
        public async Task<JsonObject> ProposeAsync(
            string id, string rating, string reason, string? evidenceUrl, string? evidenceQuote, string client, bool trusted, CancellationToken cancellationToken)
        {
            var item = Find(id);
            rating = rating?.Trim() ?? string.Empty;
            if (!_ratings.IsRecognized(rating))
            {
                throw new ArgumentException($"'{rating}' isn't a rating Jellyfin recognizes. Call rating_scale for valid values.");
            }

            if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length < 10)
            {
                throw new ArgumentException("A reason is required (one or two sentences on what the rating is based on).");
            }

            var config = Plugin.Instance?.Configuration;
            if (item is Audio && config is not null
                && !string.Equals(rating, config.CleanRating, StringComparison.Ordinal)
                && !string.Equals(rating, config.ExplicitRating, StringComparison.Ordinal))
            {
                throw new ArgumentException($"Music takes the clean ({config.CleanRating}) or explicit ({config.ExplicitRating}) rating.");
            }

            var proposal = new Proposal
            {
                ItemId = item.Id.ToString("N"),
                ItemName = Describe(item),
                Kind = item.GetType().Name,
                PreviousRating = item.OfficialRating,
                Rating = rating,
                Reason = reason.Trim(),
                EvidenceUrl = string.IsNullOrWhiteSpace(evidenceUrl) ? null : evidenceUrl.Trim(),
                EvidenceQuote = string.IsNullOrWhiteSpace(evidenceQuote) ? null : evidenceQuote.Trim(),
                Client = client
            };

            if (proposal.EvidenceUrl is not null && proposal.EvidenceQuote is not null)
            {
                proposal.EvidenceCheck = await CheckEvidenceAsync(proposal.EvidenceUrl, proposal.EvidenceQuote, cancellationToken).ConfigureAwait(false);
            }

            ProposalStore.Add(proposal);
            if (trusted)
            {
                await ApplyAsync(proposal.Id, client, cancellationToken).ConfigureAwait(false);
                proposal.Status = "applied";
            }

            _logger.LogInformation(
                "Chaperone MCP: {Client} proposed '{Rating}' for '{Name}' ({Status}, evidence {Check}).",
                client,
                rating,
                proposal.ItemName,
                proposal.Status,
                proposal.EvidenceCheck);

            return new JsonObject
            {
                ["proposal_id"] = proposal.Id,
                ["status"] = proposal.Status,
                ["evidence_check"] = proposal.EvidenceCheck,
                ["note"] = trusted ? "Applied and locked." : "Queued for the admin's review in Chaperone's settings."
            };
        }

        /// <summary>Lists proposals, optionally by status.</summary>
        public JsonArray ListProposals(string? status)
        {
            var arr = new JsonArray();
            foreach (var p in ProposalStore.All().Where(p => string.IsNullOrWhiteSpace(status) || p.Status == status))
            {
                arr.Add(System.Text.Json.JsonSerializer.SerializeToNode(p));
            }

            return arr;
        }

        /// <summary>Applies a proposal: sets the rating, locks the field so a refresh can't undo it.</summary>
        public async Task<Proposal> ApplyAsync(string proposalId, string decidedBy, CancellationToken cancellationToken)
        {
            var p = ProposalStore.All().FirstOrDefault(x => x.Id == proposalId) ?? throw new ArgumentException("No such proposal.");
            if (p.Status is "applied" or "rejected")
            {
                throw new ArgumentException($"Proposal is already {p.Status}.");
            }

            var item = Find(p.ItemId);
            item.OfficialRating = p.Rating;
            if (!item.LockedFields.Contains(MetadataField.OfficialRating))
            {
                item.LockedFields = item.LockedFields.Append(MetadataField.OfficialRating).ToArray();
            }

            await _libraryManager.UpdateItemAsync(item, item.GetParent(), ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
            QueueScanSoon();
            _logger.LogInformation("Chaperone MCP: applied '{Rating}' to '{Name}' (decided by {By}).", p.Rating, p.ItemName, decidedBy);

            return ProposalStore.Update(proposalId, x =>
            {
                x.Status = "applied";
                x.DecidedBy = decidedBy;
                x.DecidedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            })!;
        }

        /// <summary>Rejects a pending proposal.</summary>
        public Proposal Reject(string proposalId, string decidedBy)
        {
            return ProposalStore.Update(proposalId, x =>
            {
                if (x.Status != "pending")
                {
                    throw new ArgumentException($"Proposal is already {x.Status}.");
                }

                x.Status = "rejected";
                x.DecidedBy = decidedBy;
                x.DecidedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            }) ?? throw new ArgumentException("No such proposal.");
        }

        private bool NeedsRating(BaseItem item)
        {
            var config = Plugin.Instance?.Configuration;
            var r = item.OfficialRating;
            return string.IsNullOrEmpty(r)
                || string.Equals(r, config?.UnidentifiedMusicRating, StringComparison.Ordinal)
                || string.Equals(r, config?.UnidentifiedVideoRating, StringComparison.Ordinal)
                || !_ratings.IsRecognized(r);
        }

        private BaseItem Find(string id)
        {
            if (!Guid.TryParse(id, out var guid))
            {
                throw new ArgumentException($"'{id}' isn't an item id.");
            }

            return _libraryManager.GetItemById(guid) ?? throw new ArgumentException($"No item {id}.");
        }

        private static string Describe(BaseItem item) => item switch
        {
            Audio a => $"{a.AlbumArtists?.FirstOrDefault() ?? a.Artists?.FirstOrDefault()} – {a.Name} ({a.Album})",
            MusicAlbum al => $"{al.AlbumArtists?.FirstOrDefault()} – {al.Name} (album)",
            _ => item.ProductionYear.HasValue ? $"{item.Name} ({item.ProductionYear})" : item.Name
        };

        private JsonObject Summary(BaseItem item)
        {
            var o = new JsonObject
            {
                ["id"] = item.Id.ToString("N"),
                ["kind"] = item switch { Audio => "music", MusicAlbum => "album", Movie => "movie", Series => "show", _ => item.GetType().Name },
                ["name"] = item.Name,
                ["rating"] = item.OfficialRating,
                ["year"] = item.ProductionYear
            };
            if (item is Audio a)
            {
                o["artist"] = a.Artists?.FirstOrDefault() ?? a.AlbumArtists?.FirstOrDefault();
                o["album_artist"] = a.AlbumArtists?.FirstOrDefault();
                o["album"] = a.Album;
                o["seconds"] = a.RunTimeTicks.HasValue ? (int)TimeSpan.FromTicks(a.RunTimeTicks.Value).TotalSeconds : null;
            }
            else if (item is MusicAlbum al)
            {
                o["artist"] = al.AlbumArtists?.FirstOrDefault();
            }

            try
            {
                o["library"] = _libraryManager.GetCollectionFolders(item).FirstOrDefault()?.Name;
            }
            catch (Exception)
            {
                // library name is a nicety
            }

            return o;
        }

        private async Task<string> CheckEvidenceAsync(string url, string quote, CancellationToken cancellationToken)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != "https" && uri.Scheme != "http"))
            {
                return "fetch failed";
            }

            try
            {
                var client = _httpClientFactory.CreateClient(NamedClient.Default);
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(TimeSpan.FromSeconds(15));
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (compatible; Chaperone evidence check)");
                using var response = await client.SendAsync(request, cts.Token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    return "fetch failed";
                }

                var html = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
                if (html.Length > 3_000_000)
                {
                    html = html.Substring(0, 3_000_000);
                }

                return Squash(StripHtml(html)).Contains(Squash(quote), StringComparison.Ordinal) ? "verified" : "not found";
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                _logger.LogDebug(ex, "Chaperone MCP: evidence fetch failed for {Url}.", url);
                return "fetch failed";
            }
        }

        private static string StripHtml(string html)
        {
            html = Regex.Replace(html, @"<(script|style)[^>]*>.*?</\1>", " ", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            html = Regex.Replace(html, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase);
            html = Regex.Replace(html, "<[^>]+>", " ");
            return System.Net.WebUtility.HtmlDecode(html);
        }

        // Compare on letters/digits only, lower-case, so line breaks, punctuation and curly quotes don't matter.
        private static string Squash(string s) => new string(s.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

        private void QueueScanSoon()
        {
            // Albums take their rating from their tracks, and folder-level parental ratings only refresh on a
            // library scan. Chaperone's own scan task does both (it re-derives album ratings, then queues a library
            // scan), so run it once, 2 minutes after the last applied rating, to cover a whole batch of proposals.
            var timer = new Timer(_ => _taskManager.QueueScheduledTask<LibraryScanTask>(), null, TimeSpan.FromMinutes(2), Timeout.InfiniteTimeSpan);
            Interlocked.Exchange(ref _scanDebounce, timer)?.Dispose();
        }
    }
}
