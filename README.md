<div align="center">
  <img src="thumb.png" alt="Chaperone" width="420" />

# Chaperone

**Automatic parental ratings for your entire Jellyfin library — music, movies, shows, and anime.**

</div>

---

Jellyfin's parental controls are only as good as the `OfficialRating` metadata on your items. But a lot of content arrives with **no rating at all** — especially music pulled in by the *arr stack, and imports that never matched a certification. Anything unrated slips straight past age restrictions.

**Chaperone fills in the blanks.** It looks up missing ratings from public data sources and writes them back to Jellyfin's own rating field, so the parental controls you already configured actually work across everything.

## What it does

| Content | Source | How |
| --- | --- | --- |
| **Music** | [Deezer](https://www.deezer.com/), [MusicBrainz](https://musicbrainz.org/), [Apple Music](https://music.apple.com/) (iTunes Search), [LRCLIB](https://lrclib.net/) | Matches each track (artist + title) and reads its explicit-lyrics flag from Deezer, with an exact **ISRC** lookup via MusicBrainz when that's ambiguous; then Apple Music's catalogue; then lyrics from LRCLIB (instrumentals are clean, lyrics are checked against a configurable word list). |
| **Movies** | [TMDb](https://www.themoviedb.org/) | Reads release-date certifications across **all regions** and picks a rating. **Fan edits** are rated from their source film or show; foreign-only certifications can optionally be mapped to the nearest US rating. |
| **Shows** | [TMDb](https://www.themoviedb.org/) | Reads content-rating certifications across all regions. |
| **Anime** | [MyAnimeList](https://myanimelist.net/) (via [Jikan](https://jikan.moe/)) | Fallback resolver for anime that TMDb doesn't cover well. |

Ratings are written using Jellyfin's standard scale (`TV-G`, `TV-PG`, `TV-14`, `TV-MA`, and the MPAA equivalents), which interoperate numerically — so a kid restricted to `TV-14` is correctly blocked from `TV-MA` and `R` content regardless of which scale a given item uses.

For movies and shows, Chaperone also **re-examines items already tagged with something Jellyfin can't score** — `NR`, `Not Rated`, or a foreign format like `12` / `0+` — not just blank ones, and it **only ever writes a certification Jellyfin recognizes** (never a foreign string that would silently trip the "unrecognized" block). Anything it still can't resolve is normalized to the configurable **Unidentifiable movie/show rating** (default `Unrated`), so the field is always a recognized value.

### Explicit music → a real rating

Explicit tracks get **`TV-MA`** by default and clean tracks get **`TV-G`** (both configurable). That turns the streaming services' explicit flag into something Jellyfin's parental controls can actually enforce. Like that flag, it is about **explicit language**, not themes: a clean song about a battle is still clean.

## How it works

- **Automatic on import.** Chaperone registers metadata providers for audio, movies, and series, so new content gets rated as it's added to the library.
- **Albums are rated too.** Each music album is rated from its tracks using the *least restrictive* rating present, so parental controls don't hide the whole album (or its artist) just because the container has no rating of its own. A clean album stays browsable while an explicit track inside it remains individually blocked. Artists need no rating — Jellyfin shows an artist automatically once it has visible content underneath.
- **A chain of sources so tracks aren't left unrated.** For music the plugin tries, in order: (0) a track whose own title says it's an **instrumental / karaoke / backing-track** version is clean — checked first, so it's never judged by the vocal version; (1) a fuzzy Deezer search on artist + title; (2) an exact **ISRC** lookup via MusicBrainz for anything ambiguous; (3) **Apple Music's catalogue** (iTunes Search, matching artist *and* title; if both clean and explicit versions exist it doesn't guess); (4) **LRCLIB lyrics** — marked instrumental means clean, otherwise the words are checked (same recording only: durations must agree within 5 s); and (5) the track **inherits its album's rating**. If nothing helps, the track is labeled **`Unrated`** (configurable) rather than left blank — an honest fill, so there's never a gap. `Unrated` items are **re-checked on every scan**, so they get rated as soon as any source has data.

### The artist workaround

Turning on Jellyfin's **"Block items with no or unrecognized rating information"** is what actually hides unrated/unidentified music from restricted users — but Jellyfin then also blocks the **artist folder** itself unless it carries a recognized rating, which breaks browsing music by artist. Jellyfin gives no way to exempt artist containers from that block, so Chaperone stamps **every artist `TV-G`** (the *Keep artists browsable* option, on by default). This is a deliberate workaround, **not** a content judgement of the artist — the real filtering still happens on the albums and tracks inside. If you'd rather rate artists yourself, turn the option off.
- **Non-destructive by default.** It only fills in a rating when the field is **blank**. Existing ratings are left alone unless you turn on *Overwrite existing ratings*.
- **Daily library scan.** The scan runs every day at 3 AM by default (change it under **Dashboard → Scheduled Tasks → Chaperone**), and a button on the plugin's config page runs it on demand.
- **Temporary failures never become "Unrated".** If a service is rate-limited or overloaded (LRCLIB answers "server busy" at times), the track is left for the next scan instead of being labeled.
- **Automatic index refresh.** Jellyfin only recomputes a folder's effective parental rating (what restricted users are actually filtered on) during a *library scan* — not when items are edited. So after its own scan makes changes, Chaperone **queues a Jellyfin library scan automatically**, so the new album/artist/track ratings take effect for restricted users without you having to run *Scan Media Library* by hand.

## Let an AI model take the hard ones (MCP)

Some items no database knows — an obscure B-side, a fan edit, a home video. Chaperone is also an **MCP server**, so an AI
model (Claude, a local model through any MCP client, an assistant you run yourself) can research those and propose a rating:

- **Endpoint:** `POST https://<your-jellyfin>/Chaperone/mcp` (MCP Streamable HTTP). Authenticate with an **admin Jellyfin API key**
  in the header `Authorization: MediaBrowser Token="<key>"`. Give each model **its own key** (Dashboard → API Keys) — the key's
  name is how Chaperone tells clients apart, and you can revoke one without touching the others.
- **Tools:** `rating_scale` (the rules), `list_unrated`, `get_item`, `get_lyrics`, `propose_rating`, `list_proposals`.
- **Evidence is checked.** A proposal carries a reason and, ideally, a source URL plus an exact quote. Chaperone fetches the page
  and records whether the quote is really there (`verified` / `not found`), so invented evidence is visible.
- **You stay in charge.** Only API keys listed under **Trusted MCP clients** apply directly; everyone else's proposals wait on
  the plugin's settings page with **Approve / Reject** buttons. Applied ratings are **locked** (a metadata refresh can't undo
  them) and every decision is kept with who made it and why.
- **The model has no other powers**: it can only read item facts and propose ratings — it can't edit anything else.

Example (Claude Code): `claude mcp add --transport http chaperone https://<your-jellyfin>/Chaperone/mcp --header 'Authorization: MediaBrowser Token="<key>"'`

## Requirements

- **Jellyfin 12.2+** for Chaperone 1.5 and later (built against `12.2.0`). On Jellyfin 10.11, use 1.4.0.0.
- Outbound internet access to Deezer, MusicBrainz, Apple's iTunes Search, LRCLIB, TMDb, and Jikan. All are used with **free, no-auth public endpoints**, each politely rate-limited by the plugin.
- **No API keys required.** TMDb access uses Jellyfin's public key out of the box; you can supply your own TMDb v3 key in settings if you prefer.

## Installation

### Add the plugin repository (recommended)

1. In Jellyfin, go to **Dashboard → Plugins → Repositories** and add a new repository with this URL:
   ```
   https://raw.githubusercontent.com/OMGrant/jellyfin-plugin-chaperone/main/manifest.json
   ```
2. Open the **Catalog** tab, find **Chaperone** under *Metadata*, and install it.
3. Restart Jellyfin. Updates then show up in the catalog automatically.

### From a release (manual)

1. Download `chaperone_x.y.z.zip` from the [Releases](https://github.com/OMGrant/jellyfin-plugin-chaperone/releases) page.
2. Extract it into a new folder under your Jellyfin `plugins` directory (e.g. `/var/lib/jellyfin/plugins/Chaperone_1.0.0.0/`).
3. Restart Jellyfin. Chaperone appears under **Dashboard → Plugins**.

### Build it yourself

The build only needs the .NET 9 SDK:

```bash
dotnet publish -c Release -o ./publish
```

Then copy `publish/Jellyfin.Plugin.Chaperone.dll`, `meta.json`, and `thumb.png` together into a folder under your Jellyfin `plugins` directory and restart.

> No local SDK? Build in a container:
> ```bash
> podman run --rm -v "$PWD":/src:ro,z mcr.microsoft.com/dotnet/sdk:9.0 \
>   bash -c 'cp -r /src /b && cd /b && dotnet publish -c Release -o /b/out && cat /b/out/Jellyfin.Plugin.Chaperone.dll' > Jellyfin.Plugin.Chaperone.dll
> ```

## Configuration

Open **Dashboard → Plugins → Chaperone**.

| Setting | Default | What it does |
| --- | --- | --- |
| **Enabled** | on | Master switch. |
| **Explicit music rating** | `TV-MA` | Rating applied to explicit tracks. |
| **Clean music rating** | `TV-G` | Rating applied to clean tracks. |
| **TMDb API key (v3)** | Jellyfin's public key | Used for movie/show certifications; replace with your own if you like. |
| **Rate music / movies / shows / anime** | all on | Toggle each content type independently. |
| **Use Apple Music's catalogue** | on | iTunes Search as a second explicit-flag source for music. |
| **Use lyrics (LRCLIB)** | on | Instrumentals (by title or LRCLIB) are clean; otherwise lyrics are checked against the word list. |
| **Words that make lyrics explicit** | strong profanity and slurs | Comma-separated; `word*` matches as a prefix. Mild words are left out on purpose. |
| **Re-check items marked unidentified** | on | Looks at `Unrated` items again on every scan. |
| **Fan-edit libraries** | `Fan Edits` | Library names whose unrated movies are rated from their source film/show. Empty turns it off. |
| **Map foreign-only ratings to US equivalents** | off | e.g. German `12` → `PG-13`, Japanese `G` → `G`. Approximate by nature. |
| **Overwrite existing ratings** | off | When on, replaces ratings that are already set instead of only filling blanks. |

Use **Run full library scan** to backfill your existing library at any time.

## Privacy

Chaperone only sends the minimum needed to identify an item — a track's artist/title (or ISRC), or a movie/show/anime's title and provider IDs — to the public metadata services listed above. Lyrics are fetched to be checked locally; nothing about your library or users is sent anywhere. It stores nothing externally and adds no telemetry.

## A note on the code

> Yes, this plugin is vibe coded. No, I don't care to hear your opinion about it.
> We all cried when Tony Stark died — and he vibe coded 99% of his work too.

## License

[MIT](LICENSE) © Grant Garrison

Not affiliated with Jellyfin, Deezer, MusicBrainz, Apple, LRCLIB, TMDb, or MyAnimeList. This product uses the TMDb API but is not endorsed or certified by TMDb.
