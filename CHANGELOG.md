# Changelog

All notable changes to Reko are recorded here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and Reko uses
[semantic versioning](https://semver.org/spec/v2.0.0.html) on the four-part Jellyfin version.

## [1.0.2] — 2026-09-29

Found by a real server's log and a real scrolled page. The tab's header was sliding under Jellyfin's
own, every movie request was being rejected by Overseerr, and YouTube trailers played an error code
instead of a video.

### Fixed

- **The Reko header and search box disappeared as soon as you scrolled.** Jellyfin's header is
  `position: fixed` and opaque; Reko's is `position: sticky` in the scroll container. Both stick to the
  top, and Jellyfin's is at `z-index: 1100` against Reko's 4, so scrolling pushed Reko's underneath it.
  A new `chrome.js` measures whichever header the current layout uses and publishes the height as a
  custom property the stylesheet sticks to, in both the Modern and the Legacy and TV layouts.
- **Every movie request failed with an Overseerr validation error.** The body sent `seasons: null` for
  a movie; Overseerr's `seasons` is a `oneOf` of array, string and `"all"`, and `null` matches none of
  them, so the request was rejected before it reached the route. The property is now omitted entirely
  when there are no seasons. `tools/mock-services.js` accepted the null happily, which is how it
  shipped; it now rejects it the way Overseerr does.
- **YouTube trailers showed "Error 153: Video player configuration error."** That is YouTube refusing
  to configure a player whose embed request arrived with no `Referer`, which happens whenever
  something in front of Jellyfin sets `Referrer-Policy: no-referrer` — a reverse proxy header breaks
  every embed on the server at once. The iframe now states `referrerpolicy="origin"` itself, which
  overrides whatever the page and the proxy said, and the embed URL carries `origin` so YouTube's own
  check passes. A "Watch on YouTube" link sits under the player, because a video whose owner has
  disallowed embedding, or a region block, is still a video somebody can watch.
- **Reko's colour variables did not reach its own content.** Views were mounted beside Reko's
  application root rather than inside it, so `--reko-text`, `--reko-accent` and `--reko-border`
  resolved to nothing and every colour fell back to Jellyfin's inherited value. On a dark theme that
  is close enough to look fine, which is why it went unnoticed; on a light theme it is white text on a
  white page. It also meant the search field had no border and no background of its own.
- **`tools/check-client.mjs` took thirty seconds to exit.** Importing `client.js` starts its poller
  for the Jellyfin API client, and nothing unref'd it, so every CI run finished its checks and then sat
  there until the poller gave up. It now looks exactly like a hung build.

### Changed

- **Hover previews are gone.** They covered the row being read, rebuilt on every pointer move across a
  rail, and showed the title, year, certification, synopsis and badges that are either already on the
  card or one click away on the title page. The "Show a preview when hovering a card" setting has been
  removed with them.
- **The trailer has a poster.** It used to be a black rectangle with a small triangle in it; it is now
  the title's own backdrop with a centred play button and a label.

## [1.0.1] — 2026-09-29

Fixes the certifications on every movie and series, and the retry storm that was amplifying the
failure. Found by a real server's log, not by the tests: the fake TMDB in `tools/mock-services.js`
had been written from the same misreading of the API as the model, so a fake was agreeing with a
fake and nothing disagreed with TMDB.

### Fixed

- **`release_dates` and `content_ratings` were decoded as the wrong shape.** Both were modelled as a
  map keyed by country code. TMDB returns an *array* of per-country entries — for movies, each with
  its own array of dated releases. Neither deserialised, and because the failure is in the whole
  document rather than in one field, it took out every movie and series title page and every hero
  built from a movie. Certifications were the visible symptom; the endpoint itself was failing.
- **The certification lookup now skips uncertificated premieres.** TMDB lists every release of a film
  in a country, and the earliest ones are routinely uncertificated festival screenings. Taking the
  first entry rather than the first certificated one showed no rating for most films.
- **Shared cache production is no longer bound to the request that triggered it.** The first caller
  to miss a cache entry started the work under its own request token, so every later caller inherited
  that request's lifetime. When it ended, the shared work was cancelled, the entry was evicted as a
  failure, and the next caller started again — one unparseable response became a re-fetch on every
  rebuild. Production now runs under its own timeout, and callers abandon only their own wait.
- **A cancelled request returns 499 instead of a logged 500.** A person closing the tab was producing
  two error entries with a full stack trace each. The rail builder no longer reports a cancellation as
  an unexpected failure.
- **Movie and series pages no longer request TMDB keywords.** They were appended to every title page
  fetch, which is a separate upstream call each, and were read by nothing.

### Added

- `Jellyfin.Plugin.Reko.Tests`: the TMDB models are now asserted against TMDB's own published
  response bodies, and the cache's behaviour when callers come and go is covered. Both run in CI.
  `tools/mock-services.js` returns the real shapes too, with a comment explaining why that matters.

## [1.0.0] — 2026-09-29

Initial release.

### Added

- A **Reko** tab on the Jellyfin home page, directly beside Home and Favorites, in both the Modern
  layout (Jellyfin 12.0's drawer and 12.1's app-bar tabs) and the Legacy and TV layouts.
- A rotating hero billboard using TMDB logo artwork, with a typeset title where a title has no logo.
- Horizontal rails with scroll snapping, hover arrows and lazy hydration: trending, popular, ranked
  Top 10 today and this week, and curated genre and theme rows.
- Instant search across movies, series and people, reachable from the header on every view.
- Title pages with logo, tagline, overview, genres, network, trailer, where-to-watch providers, the
  full season and episode list, and cast.
- Person pages for anyone in a cast or crew.
- A genre and sort browser for digging past the curated rows.
- Requests through Overseerr or Jellyseerr, with a season picker for series, per-user attribution,
  and live request status on cards and title pages.
- "In library" badges, continue watching, and personalised rows built from your own playback history.
- One-click playback for anything your library already has, handed to Jellyfin so it owns the resume
  point, the version picker and transcoding.
- An in-browser configuration page under Dashboard ▸ Plugins ▸ Reko, including advanced TMDB mirror
  and proxy overrides.
- Deep links for every view, so browser back and forward work and any view can be shared.

### Notes

- Requires Jellyfin 12.1. The plugin targets that ABI exactly and will not load on 12.0 or 10.x.
- Requires a TMDB API Read Access Token. Overseerr or Jellyseerr is optional and only needed for
  requests.
- Reko reads your library's titles and playback state to match TMDB titles against what you have. It
  does not walk your files and does not write anything back.
