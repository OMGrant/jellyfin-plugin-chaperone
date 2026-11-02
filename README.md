<div align="center">
  <img src="thumb.png" alt="Chaperone" width="420" />

# Chaperone

**Automatic parental ratings for your whole Jellyfin library: music, movies, shows, anime, and the odd items no database knows.**

[![Release](https://img.shields.io/github/v/release/OMGrant/jellyfin-plugin-chaperone?label=release)](https://github.com/OMGrant/jellyfin-plugin-chaperone/releases/latest)
[![Jellyfin](https://img.shields.io/badge/Jellyfin-12.2%2B-00a4dc)](https://jellyfin.org/)
[![License: MIT](https://img.shields.io/badge/license-MIT-green)](LICENSE)

</div>

---

Jellyfin's parental controls are only as good as the `OfficialRating` on each item. A lot of content arrives with **no rating at all**: music from the *arr stack, imports that never matched a certification, or a film that only carries a foreign rating. Anything unrated either slips past the age limit or, with *"Block items with no or unrecognized rating information"* turned on, disappears for restricted users even when it's harmless.

**Chaperone fills in the blanks.** It looks up real ratings from public data sources and writes them into Jellyfin's own rating field. Then the parental controls you already set up work across everything: kids see what they should, and nothing they shouldn't.

## Features

**Music: explicit or clean, track by track**
- Reads the **explicit-lyrics flag** the streaming services use, from [Deezer](https://www.deezer.com/), confirmed by an exact **ISRC** lookup on [MusicBrainz](https://musicbrainz.org/) when the match is ambiguous.
- Uses **Apple Music's catalogue** (iTunes Search) as a second source.
- Checks **lyrics from [LRCLIB](https://lrclib.net/)** against a word list you can configure, as a last resort.
- **Detects instrumentals**: a track whose title says *instrumental / karaoke / backing track*, or that LRCLIB marks instrumental, is clean.
- Explicit tracks become **`TV-MA`** and clean tracks **`TV-G`**, so Jellyfin can actually enforce them. Both values are configurable. Like the streaming flag, this rates **language, not themes**.
- **Rates albums from their tracks** (least restrictive), so a clean album stays browsable while an explicit track inside it is still blocked.
- Fills in a track nobody could identify from **its album's rating**.
- **Keeps artists browsable** (see [the artist workaround](#the-artist-workaround)).

**Movies and shows**
- Reads certifications from [TMDb](https://www.themoviedb.org/) across **all regions**. Your server's preferred country comes first, then the US.
- Uses an **anime fallback** via [MyAnimeList](https://myanimelist.net/) (through [Jikan](https://jikan.moe/)) for titles TMDb covers poorly.
- **Fan edits** in the libraries you name are rated from their **source film or show** (e.g. *The Empire Strikes Back Revisited* is rated as *The Empire Strikes Back*).
- Can **map foreign-only ratings** to the nearest US equivalent (German `12` → `PG-13`, BBFC `15` → `R`, …). This is optional and off by default.
- Rechecks items tagged with something Jellyfin can't score (`NR`, `Not Rated`, `0+`, …), and **only ever writes a rating Jellyfin recognizes**.

**An AI model for the hard ones (MCP)** *(new in 1.7)*
- Chaperone is an **MCP server**. Claude, a local model, or any MCP client can list what's still unrated, research it, and **propose a rating with evidence**.
- **Evidence is checked:** Chaperone fetches the cited page and records whether the quoted text is really there.
- **Trusted clients** apply directly. Proposals from everyone else **wait for your Approve / Reject** on the settings page.
- Applied ratings are **locked** against metadata refreshes, and every decision is logged with who made it and why. Two minutes after the last applied rating, Chaperone re-runs its scan so albums pick up their tracks' new ratings.

**Runs itself**
- **Rates on import**: metadata providers for tracks, albums, artists, movies, and series.
- **Daily library scan at 3 AM**, which you can change under *Scheduled Tasks*, plus a *Run full library scan* button.
- **Non-destructive by default**: it only fills blanks unless you turn on *Overwrite existing ratings*.
- **Never labels anything because of an outage**: if a source is rate-limited or busy, the item waits for the next scan.
- Rechecks `Unrated` items on every scan, so they get rated as soon as any source has data.
- **Refreshes Jellyfin's parental index**: after a scan that changed anything, it queues a Jellyfin library scan so restricted users see the result.
- **No API keys needed**: free public endpoints, each rate-limited politely.

## Installation

Requires **Jellyfin 12.2 or later**. On Jellyfin 10.11, use Chaperone 1.4.0.0.

### From the plugin repository (recommended)

1. In Jellyfin, open **Dashboard → Plugins → Repositories** and add:
   ```
   https://raw.githubusercontent.com/OMGrant/jellyfin-plugin-chaperone/main/manifest.json
   ```
2. Open **Catalog**, find **Chaperone** under *Metadata*, and install it.
3. Restart Jellyfin. Updates then appear in the catalog.

### Manual

1. Download `chaperone_x.y.z.w.zip` from [Releases](https://github.com/OMGrant/jellyfin-plugin-chaperone/releases).
2. Extract it into a new folder under Jellyfin's `plugins` directory (e.g. `/var/lib/jellyfin/plugins/Chaperone_1.7.1.0/`).
3. Restart Jellyfin.

### Build it yourself

You need the **.NET 10 SDK**:

```bash
dotnet publish -c Release -o ./publish
```

Copy `publish/Jellyfin.Plugin.Chaperone.dll`, `meta.json`, and `thumb.png` into a folder under `plugins`, then restart Jellyfin. No local SDK? Use the `mcr.microsoft.com/dotnet/sdk:10.0` container.

## How a rating is decided

**Music track.** Chaperone stops at the first step that gives an answer:

0. **Title says instrumental / karaoke / backing track** → clean. This is checked first so a vocal version is never used to judge it.
1. **Deezer**: fuzzy match on artist and title, then read the explicit flag.
2. **MusicBrainz ISRC**: an exact recording lookup for an ambiguous match.
3. **Apple Music**: artist *and* title must match closely. If clean and explicit versions both exist, it doesn't guess.
4. **LRCLIB lyrics**: it must be the same recording (durations within 5 s). Marked instrumental → clean; otherwise the words are checked against the list.
5. **Album's rating**: the track inherits it.
6. Otherwise the track is labeled **`Unrated`** (configurable) and checked again on the next scan. Restricted users can't see it until then.

**Album.** Rated with the least restrictive rating among its tracks.

**Movie or show.** Steps in order:
1. TMDb certification for your preferred country, then the US.
2. A mapped foreign rating, if mapping is on.
3. Any recognized rating.
4. Fan edits: the source title's rating.
5. Anime: the MyAnimeList fallback.
6. Otherwise **`Unrated`** (configurable).

Ratings use Jellyfin's standard scales (`TV-Y` … `TV-MA`, `G` … `NC-17`), which line up numerically. A user limited to `TV-14` is blocked from both `TV-MA` and `R`.

### The artist workaround

Turning on *"Block items with no or unrecognized rating information"* is what hides unrated music from restricted users. But Jellyfin then also blocks the **artist folder** unless the artist has a recognized rating, which breaks browsing by artist, and Jellyfin has no way to exempt artists. So Chaperone stamps **every artist `TV-G`** (*Keep artists browsable*, on by default). This is a workaround, **not a judgement of the artist**: the real filtering still happens on the albums and tracks inside. Turn it off if you'd rather rate artists yourself.

## Let an AI model take the hard ones (MCP)

Some items no database knows: an obscure B-side, a live session, a fan edit, a featurette. Chaperone exposes them over the [Model Context Protocol](https://modelcontextprotocol.io/), so an AI model can do the research while Chaperone keeps the rules and the record.

**Connect**
- **Endpoint:** `POST https://<your-jellyfin>/Chaperone/mcp` (MCP Streamable HTTP, JSON responses).
- **Auth:** an **admin** Jellyfin API key in the header `Authorization: MediaBrowser Token="<key>"`.
- Give each model **its own key** (Dashboard → API Keys). The key's name identifies the client, and you can revoke one without touching the rest.

**Tools**

| Tool | What it does |
| --- | --- |
| `rating_scale` | The valid ratings and the house rules: music = explicit language only; featurettes and fan edits take their film's rating; never guess. |
| `list_unrated` | Items still needing a rating: blank, `Unrated`, or not recognized. Filter by music, album, movie, or show; paged. |
| `get_item` | What Jellyfin knows about an item: artist, album, duration, overview, genres, provider IDs, file name, an album's tracks. |
| `get_lyrics` | LRCLIB lyrics for a track (matched on artist, title, and duration), including whether it's instrumental. |
| `propose_rating` | Proposes a rating with a reason and, ideally, `evidence_url` plus an exact `evidence_quote`. |
| `list_proposals` | Proposals so far, filtered by status if wanted. |

**Safeguards**
- **Evidence check.** Chaperone fetches `evidence_url` and records `verified`, `not found`, or `fetch failed`, so invented evidence shows.
- **Trusted vs. review.** Keys named under **Trusted MCP clients** apply at once. Proposals from every other key wait under **Rating proposals** on the settings page with **Approve / Reject**. A newer proposal for the same item supersedes the older one.
- **Locked and logged.** An applied rating is locked so a metadata refresh can't overwrite it. Every proposal keeps its reason, evidence, client, and decision.
- **Narrow powers.** A model can read item facts and propose ratings. It cannot edit anything else, and music only accepts the clean or explicit value.

**Example (Claude Code)**

```bash
claude mcp add --transport http chaperone https://<your-jellyfin>/Chaperone/mcp \
  --header 'Authorization: MediaBrowser Token="<your-api-key>"'
```

## Configuration

Open **Dashboard → Plugins → Chaperone**.

| Setting | Default | What it does |
| --- | --- | --- |
| Enabled | on | Master switch. |
| Rate music / movies / shows | on | Turns each content type on or off. |
| Anime fallback (MyAnimeList) | on | Uses Jikan when TMDb has nothing. |
| Explicit music rating | `TV-MA` | Rating for explicit tracks. |
| Clean music rating | `TV-G` | Rating for clean tracks. |
| Unidentifiable music rating | `Unrated` | For tracks no source could identify. Empty leaves them blank. |
| Unidentifiable movie/show rating | `Unrated` | For movies and shows with no recognized rating. Empty leaves them as they are. |
| Rate albums from their tracks | on | Uses the least restrictive rating among the tracks. |
| Fill unidentified tracks from their album | on | The album-inheritance step. |
| Keep artists browsable (rate all artists TV-G) | on | See [the artist workaround](#the-artist-workaround). |
| Use Apple Music's catalogue | on | iTunes Search as an extra explicit-flag source. |
| Use lyrics (LRCLIB) | on | Instrumentals are clean; other lyrics are checked against the word list. |
| Words that make lyrics explicit | strong profanity and slurs | Comma-separated. `word*` matches as a prefix. Mild words (damn, hell…) are left out on purpose. |
| Re-check items marked unidentified | on | Looks up `Unrated` items again on every scan. |
| Fan-edit libraries | `Fan Edits` | Libraries whose unrated movies take their source's rating. Empty turns this off. |
| Map foreign-only ratings to US equivalents | off | Nearest US band for items with only foreign certifications. Approximate by nature. |
| Overwrite existing ratings | off | Replaces ratings that are already set instead of only filling blanks. |
| TMDb API key (v3) | Jellyfin's public key | Use your own if you prefer. |
| Trusted MCP clients | *(empty)* | API key names whose proposals apply directly. Empty means every proposal is reviewed. |

## Privacy

Chaperone sends only what it needs to identify an item to the services above:
- **Music:** artist and title, ISRC, duration.
- **Movies and shows:** title, year, and provider IDs.

Lyrics are fetched and checked **on your server**. When an MCP client cites evidence, your server fetches that one URL to check the quote. There is no telemetry and no external storage, and nothing about your users or viewing is sent anywhere.

## Compatibility

| Chaperone | Jellyfin | .NET |
| --- | --- | --- |
| 1.5.0.0 – 1.7.1.0 | 12.2+ | 10 |
| 1.0.0.0 – 1.4.0.0 | 10.11 | 9 |

Full history is in the [changelog in `manifest.json`](manifest.json) and on the [Releases](https://github.com/OMGrant/jellyfin-plugin-chaperone/releases) page.

## A note on the code

> Yes, this plugin is vibe coded. No, I don't care to hear your opinion about it.
> We all cried when Tony Stark died — and he vibe coded 99% of his work too.

## License

[MIT](LICENSE) © Grant Garrison

Not affiliated with Jellyfin, Deezer, MusicBrainz, Apple, LRCLIB, TMDb, or MyAnimeList. This product uses the TMDb API but is not endorsed or certified by TMDb.
