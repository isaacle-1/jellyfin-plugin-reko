# Architecture

Reko is two halves that meet at an HTTP boundary: a Jellyfin plugin that talks to TMDB and Seerr,
and a web client that the plugin injects into jellyfin-web's own home page. Neither half knows
anything about the other's internals; they agree on a small JSON contract.

## The constraint that shapes everything

Jellyfin 12 has no plugin API for the web client. There is no registry to add a tab to, no
client-side extension point, and no server-side route to serve one. The home page's tab list is
hard-coded in jellyfin-web's home route, and the client is a `createHashRouter` SPA with no route
table a plugin can extend.

So the tab has to be added to the DOM that jellyfin-web itself builds, and the state that belongs to
Reko's own views has to live in the URL.

The alternative — patching jellyfin-web's minified bundle — was rejected. It breaks silently on any
minifier change, it cannot reach the Modern layout's React tree, and it would have to be redone for
every Jellyfin release.

## Server

```
Jellyfin.Plugin.Reko
├── Plugin.cs                        IPlugin, GUID, name
├── PluginServiceRegistrator.cs       DI registrations
├── Api/
│   ├── RekoController.cs             every /Reko/* endpoint, and ServeResource for the web client
│   └── RekoDto.cs                    the wire contract
├── Configuration/                    the dashboard settings page
└── Services/
    ├── ScriptInjectionStartupFilter.cs  injects the client into /web/index.html
    ├── Tmdb/                         TMDB v3 client, models, rate limiter
    ├── Seerr/                        Overseerr/Jellyseerr client and models
    ├── Library/                      the in-memory name → Jellyfin item index
    ├── Rows/RowCatalog.cs            which rows exist
    ├── RekoPayloadBuilder.cs         assembles the home payload
    ├── CardFactory.cs                normalises TMDB objects into cards
    └── TtlCache.cs
```

### The library index

Reko needs to know, for a TMDB title, whether you have it and where its resume point is. It builds
an in-memory index of your library keyed on normalised title and year, refreshed on a timer and on
library-change events. It reads titles and playback state; it does not walk files and does not write
anything back.

The index exists so that the *catalogue* can be annotated with *library* facts. That boundary is the
point of the whole design: TMDB decides what a card looks like, Jellyfin decides what you can play.

### Caching

Three tiers, with different lifetimes, because the data has different rates of change:

| Data | Lifetime | Why |
|---|---|---|
| Rails | 3 hours | Curated lists move slowly |
| Trending | 45 minutes | Moves daily |
| Title detail | 12 hours | Effectively static |
| Taste profile | 12 hours | Expensive to compute, changes slowly |

Caches are keyed per user where the data is per-user, and shared where it is not. A TMDB rate limiter
sits in front of the client so a cold cache with thirty-odd rows cannot trip TMDB's limits.

## Web client

Plain ES modules, embedded in the DLL and served by the plugin. No bundler, no npm, no framework.

```
Web/
├── early.js        classic script, first in <head>
├── client.js       entry module: boot, injection pass, navigation
├── reko.css        every rule namespaced under .reko*
└── js/
    ├── inject.js   puts the tab and its panel into jellyfin-web's DOM
    ├── router.js   Reko's views, as query parameters on Jellyfin's own route
    ├── app.js      view switching and the handlers shared by every view
    ├── api.js      the /Reko/* client
    ├── card.js     the poster card, badges, hover preview
    ├── rail.js     a row of cards, with arrows and lazy hydration
    ├── hero.js     the rotating billboard
    ├── detail.js   title and person pages
    ├── search.js   search and browse
    ├── seerr.js    the request picker and status
    ├── modal.js    a focus-trapped dialog
    └── utils.js    DOM and formatting helpers
```

### Injection

`inject.js` is the whole of the integration with jellyfin-web, and it is idempotent by construction:
a `MutationObserver` re-runs it whenever the home page is rebuilt, and every failure path is silent.
A web client change degrades to "Reko is not on this page", never to a broken Jellyfin.

Two layouts, two insertion points:

- **Modern** (the default since 12.0). The legacy `.skinHeader` stays mounted but hidden. In 12.1 the
  visible home tabs are MUI buttons in the app bar, so Reko clones the Favorites button and sits
  after it. 12.0's Modern layout used the drawer for the same purpose, and that insertion point is
  kept because it costs nothing when there is no drawer.
- **Legacy and TV** render a real tab strip, so Reko appends an `.emby-tab-button` to
  `.emby-tabs-slider` and lets Jellyfin's own `emby-tabs` own the selection.

The panel contract is positional: `maintabsmanager` resolves the visible panel with
`querySelectorAll('.tabContent')[n]`, so the panel must be appended in index order and a button must
exist for every index. The index is allocated by scanning for the highest one already in use, so Reko
does not overwrite a third-party tab plugin, and it is then *pinned* — an existing panel keeps the
index it was created with, because that index is already baked into the URLs Reko has written.

### Routing

Reko has no routes of its own. Its state lives in extra query parameters on Jellyfin's home route:

```
#/home?tab=2                                the tab
#/home?tab=2&view=title&id=603              a movie
#/home?tab=2&view=title&id=1399&type=tv&season=2
#/home?tab=2&view=search&q=alien
#/home?tab=2&view=browse&type=movie&genre=28&sort=popularity.desc
#/home?tab=2&view=person&id=6384
```

This is not a workaround. It means browser back and forward work exactly as a person expects,
because a history entry is a real URL the client's own router already understands. `tab` is
Jellyfin's own deep-link convention — `?tab=1` is Favorites — so `?tab=2` selects the Reko tab through
the same mechanism with no extra machinery.

`early.js` runs during parsing, before jellyfin-web's deferred bundles create the router, and removes
a `?tab=N` that no button backs yet, stashing it for Reko to re-apply. Without that, the router
selects a tab that does not exist and the deep link lands on Home.

### Caching on the client

Every module URL carries a build-specific query, rewritten server-side. A plugin update therefore
produces different URLs and cannot be served from a stale cache, which is what makes a 24-hour
`Cache-Control` safe on the client modules.

## Packaging

Two files carry almost the same fields in deliberately different shapes, and confusing them is the
easiest way to ship a plugin that installs and then misbehaves.

| | `manifest.json` | `meta.json` |
|---|---|---|
| Lives | `gh-pages` branch, and the repository root | Inside the release zip |
| Shape | An **array** of plugins, each with a **list** of versions | One **flat object**: one plugin, one version |
| Read by | Jellyfin, to decide what a repository offers | `PluginManager`, to reconcile the installed package |
| Carries `sourceUrl` and `checksum` | Yes — that is how the artifact is found and verified | No — it travels *inside* the artifact, so it would point at itself |

A version entry also needs `targetAbi`. Jellyfin filters the catalog by it, and a version without one
is not offered at all — silently, which is indistinguishable from a repository that was never added.

`GET /Packages` reads each installed plugin's local `meta.json` off disk. Shipping the array form
installs the plugin, reports success in the dashboard, and then logs a deserialization error on every
start; a `meta.json` that is missing entirely makes the whole endpoint fail with a 404. Both were
found by installing from the repository and reading the server log, not by looking at the dashboard,
which reported success either way.

The release workflow therefore generates both, and the checksum — which cannot be known before the zip
exists — is written into `manifest.json` by the workflow rather than by hand:

```
tools/build-package-meta.py <version>            manifest.json -> the zip's meta.json
tools/set-manifest-release.py <version> <md5>    manifest.json -> sourceUrl and checksum
```

## Testing

`tools/check-client.mjs` runs every module against a DOM shim with real selector matching, because
`inject.js` is nothing but selector matching and a stub that answers `null` to everything cannot tell
a working injector from a broken one. It covers both layouts' markup, the router's URL grammar, and
the ranked card's structure.

`tools/mock-services.js` serves a fake TMDB and a fake Seerr, so the whole plugin can be exercised
end to end on a real Jellyfin 12.1 with no credentials and no outbound network.

```bash
node tools/mock-services.js &        # a fake TMDB and Seerr on :8099
./tools/run-test-server.sh           # a Jellyfin 12.1 instance on :8096
./tools/redeploy.sh                  # rebuild and reinstall into that instance
```

`REKO_TEST_ROOT` and `REKO_TEST_URL` point these at a Jellyfin 12.1 build unpacked somewhere
disposable. The port is not a command line flag, because Jellyfin 12 reads it from `network.xml` in
the config directory. Then point Reko's TMDB base URL at `http://127.0.0.1:8099/tmdb/3/`, its image
base URL at `http://127.0.0.1:8099/image/` and its Seerr URL at `http://127.0.0.1:8099/seerr`.

The client-side trace is a local storage flag rather than a build:

```js
localStorage.rekoDebug = "1";
```

It is opt-in because an always-on trace floods a shared console on every navigation. It goes to
`console.info` rather than `console.debug`, because browser developer tools hide the debug level by
default and most log capture drops it, and an opt-in trace nobody can see is not a trace.
