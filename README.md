# Reko

A Netflix-style discovery tab for [Jellyfin](https://jellyfin.org) 12.1, backed by
[TMDB](https://developer.themoviedb.org), with requests routed through
[Overseerr](https://overseerr.dev) or [Jellyseerr](https://jellyseerr.dev).

Reko adds a **Reko** tab to the Jellyfin home page, directly beside Home and Favorites. It is a
*catalogue* browser rather than a library browser: it browses what exists in the world, and uses your
Jellyfin library only to tell you what you already have, to play it, to pick up where you left off,
and to build rows out of what you have been watching.

<!-- markdownlint-disable MD033 -->

## What you get

- **A hero billboard** that rotates through featured titles, using TMDB's logo artwork when a title
  has one and a typeset title when it does not.
- **Horizontal rails** — Trending, Popular, Top 10, curated genres, and rows built from your own
  viewing history — that scroll and snap, with arrows on hover.
- **Ranked Top 10 rows**, numbered the way Netflix numbers them.
- **Hover previews** on every card: backdrop, facts, synopsis, badges, and the actions available for
  that title.
- **Instant search** across movies, series and people, reachable from the header on every view.
- **Title pages** with the logo, tagline, overview, genres, network, trailer, where-to-watch
  providers, the full season and episode list, and the cast.
- **Person pages** for anyone in a cast or crew.
- **Requests** through Overseerr or Jellyseerr, with a season picker for series and live request
  status on every card afterwards.
- **"In library" badges and one-click playback** for anything your server already has, with resume
  points handed to Jellyfin rather than reimplemented.

## Requirements

| | |
|---|---|
| Jellyfin | **12.1** (12.1.0 or later in the 12.1 line) |
| TMDB | An [API Read Access Token](https://developer.themoviedb.org/docs/authentication-application) — free |
| Overseerr / Jellyseerr | Optional. Without it, the Request button is hidden and everything else works |

Reko targets Jellyfin's 12.1 ABI exactly. It will not load on 12.0 or on 10.x, and the plugin
repository will not offer it to them.

## Installing

1. In Jellyfin, open **Dashboard ▸ Plugins ▸ Repositories**.
2. **Add** a repository with this URL:

   ```
   https://raw.githubusercontent.com/isaacle-1/jellyfin-plugin-reko/gh-pages/manifest.json
   ```

3. **Save**, then go to **Dashboard ▸ Plugins ▸ Catalog** and install **Reko**.
4. Open **Dashboard ▸ Plugins ▸ Reko** and add your TMDB token. Add your Overseerr or Jellyseerr URL
   and API key too if you want requests.
5. Restart Jellyfin, or just reload the web page.

## Configuration

Everything is under **Dashboard ▸ Plugins ▸ Reko**.

| Setting | Meaning |
|---|---|
| **TMDB API Read Access Token** | Required. Without it Reko shows a "needs a TMDB key" notice and nothing else. |
| **Overseerr / Jellyseerr URL** | Optional, e.g. `https://seerr.example.com`. |
| **Overseerr / Jellyseerr API key** | Required alongside the URL. In Overseerr or Jellyseerr, open **Settings ▸ General ▸ API Key** and copy it. It stays on the server and is never sent to a browser. |
| **Tab label** | The text on the tab. Defaults to `Reko`. |
| **Hero titles** | How many titles in the rotating billboard, and how long each one stays. |
| **Items per row** | How many cards each rail requests. |
| **Hover previews**, **Instant search** | On or off. |
| **Continue watching**, **Personalised rows** | Build rows from your own playback history. Personalised rows need at least a few watched items before they appear. |
| **Minimum watched for personalised rows** | How much history Reko needs before it will build rows about your taste. |
| **Cache lifetimes** | How long rails, trending, title pages and the computed taste profile are held. Longer is faster and staler. |
| **TMDB language / region** | Language tag for text metadata (`en-US`) and region for watch providers (`US`). |
| **TMDB base URL** / **Image base URL** | Advanced. Point these at a mirror or a caching proxy. Useful behind a firewall, and for running with no outbound internet at all. Set both, or a mirror that proxies the API but not the image CDN gives you cards with broken artwork. |
| **Verbose plugin logging** | Writes Reko's decisions to the Jellyfin log. |

Not every option in the dashboard is listed here; the page itself is the reference.

### Requests

Requests go through Overseerr or Jellyseerr using the API key above. Reko resolves the signed-in
Jellyfin user to their matching Seerr user, so people see and make their own requests and their
quotas are attributed correctly. A series request opens a season picker; a movie request does not need
one. Once a request exists, its status is shown on the card and on the title page, and updates as
Seerr processes it.

## Frequently asked questions

**Is my library being indexed or scanned?**
Only enough to match names. Reko keeps a small in-memory index of your library's titles and their
playback state so it can put an "In library" badge on a card and hand you a resume point. It does not
walk your files, does not read them, and does not write anything back.

**Does Reko replace Jellyfin's search or library pages?**
No. Reko is a tab. Your existing library, playlists, favourites and Jellyfin's own search are
untouched, and Reko never starts playback itself — it hands the item to Jellyfin, which owns the
resume point, the audio and subtitle preferences, the version picker and the transcoding decision.

**Will it break when jellyfin-web changes?**
Reko is defensive by design: every injection path is idempotent, every failure is silent, and if the
tab cannot be created you get Jellyfin exactly as you had it. It supports both the Modern layout
(default since 12.0, including the app-bar tabs in 12.1) and the Legacy/TV layout (a real
`.emby-tab-button` in the tab strip). It deliberately does *not* patch jellyfin-web's minified
bundle, because that breaks silently on any minifier change and cannot reach the Modern layout's
React tree.

**Something is not showing up. How do I find out?**
Set `localStorage.rekoDebug = "1"` in the browser console on the Jellyfin web page and reload. Reko
then traces its decisions to the console under a `[Reko]` tag. This is opt-in, because an always-on
trace floods a shared console on every navigation.

## Building from source

```bash
dotnet build Jellyfin.Plugin.Reko.sln -c Release
dotnet publish Jellyfin.Plugin.Reko/Jellyfin.Plugin.Reko.csproj -c Release -f net10.0
```

The plugin is a single `Jellyfin.Plugin.Reko.dll`; the web client is embedded in it as resources and
served by the plugin, so there is no npm build and no bundler.

The browser modules are plain ES modules, so the useful check is not "does it compile" but "do the
modules still import and export what the code calls":

```bash
node tools/check-client.mjs
```

That runs every module against a small DOM shim and asserts the behaviour that unit tests can reach
— the tab injector against both layouts' markup, the router's URL grammar, and the ranked card's
structure. It exists because the failure mode that actually bit this project was a rename leaving a
dangling reference, which parses perfectly and dies at runtime on first render.

### Testing against a real server

`tools/` has what is needed to run the whole thing without any credentials:

```bash
# A Jellyfin 12.1 build, unpacked into a throwaway directory
export REKO_TEST_ROOT=/tmp/reko-test
mkdir -p "$REKO_TEST_ROOT/server"
curl -L -o /tmp/jellyfin.tar.gz \
  https://repo.jellyfin.org/files/server/linux/stable/v12.1/amd64/jellyfin_12.1-amd64.tar.gz
tar -xzf /tmp/jellyfin.tar.gz -C "$REKO_TEST_ROOT/server"

node tools/mock-services.js &   # a fake TMDB and Seerr on :8099
./tools/run-test-server.sh      # a Jellyfin 12.1 instance on :8096
./tools/redeploy.sh             # rebuild and reinstall into that instance
```

Then point Reko's TMDB base URL at `http://127.0.0.1:8099/tmdb/3/`, its image base URL at
`http://127.0.0.1:8099/image/` and its Seerr URL at `http://127.0.0.1:8099/seerr`.

The port is not a command line flag — Jellyfin 12 reads it from `network.xml` in the config
directory — so `REKO_TEST_ROOT` and `REKO_TEST_URL` are what these scripts take.

## Documentation

- [`PLAN.md`](PLAN.md) — the architecture, and the research findings about Jellyfin 12, TMDB and
  Seerr that the design rests on
- [`docs/architecture.md`](docs/architecture.md) — how the pieces fit together
- [`docs/installation.md`](docs/installation.md) — installing, configuring, and the settings reference
- [`docs/troubleshooting.md`](docs/troubleshooting.md) — what to check when something is missing
- [`research/`](research/) — the API references the server code was written against

## Licence

GPL-3.0. See [LICENSE](LICENSE).

Reko is not affiliated with or endorsed by Jellyfin, TMDB, Netflix, Overseerr or Jellyseerr. It
shows artwork and metadata provided by TMDB, which requires [attribution to TMDB](https://developer.themoviedb.org/docs/getting-started).
This product uses the TMDB API but is not endorsed or certified by TMDB.
