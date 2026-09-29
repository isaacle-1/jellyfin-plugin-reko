# Reko — a Netflix-style discovery tab for Jellyfin 12.1

## 0. Research summary (what I verified, and what it forces)

I read jellyfin-web `v12.0`/`v12.1` source and jellyfin server `v12.0` source, and cross-checked against
the `Jellyfin.Controller`/`Jellyfin.Model` `12.1.0` NuGet packages. Four findings drive the whole design.

### F1 — There is no plugin API for adding a home tab. The tab list is hard-coded.

`src/apps/modern/routes/home.tsx` (v12.1, read directly):

```tsx
const getTabs = () => {
    return [{ name: globalize.translate('Home') },
            { name: globalize.translate('Favorites') }];
};
```

and the panels are literal JSX siblings:

```tsx
<div className='tabContent pageTabContent' id='homeTab'      data-index='0'><div className="sections"></div></div>
<div className='tabContent pageTabContent' id='favoritesTab' data-index='1'><div className="sections"></div></div>
```

There is no `SectionFactory`, no `addRoute`, no client plugin registry. `grep` for
`addRoute|pluginRouter|registerRoute` across jellyfin-web 12.1 returns nothing.

### F2 — In the Modern layout (the default since 12.0) the Home|Favorites tab strip is **invisible**.

`src/components/AppHeader.tsx` (v12.1, read directly):

```tsx
/**
 * NOTE: These components are not used with the new layouts, but legacy views interact with the elements
 * directly so they need to be present in the DOM. We use display: none to hide them and prevent errors.
 */
<div style={isHidden ? { display: 'none' } : undefined}>
    <div className='mainDrawer hide' />
    <div className='skinHeader focuscontainer-x' />
    <div className='mainDrawerHandle' />
</div>
```

`.skinHeader` holds `.headerTabs` (created by `src/scripts/libraryMenu.js`), and `maintabsmanager.setTabs()`
writes the tab buttons into it. `RootAppRouter` passes `isHidden={layoutManager.modern || ...}`. So on
Modern desktop/mobile, Home and Favorites are **drawer entries, not visible tabs**
(`MainDrawerContent.tsx` → `to='/home'` and `to='/home?tab=1'`), and the tab bar they feed lives in a
`display:none` subtree.

**Consequence:** "a tab sitting beside Home and Favorites" has to be realised differently per layout, and
both need doing:

| Layout | Where Home/Favorites appear | What Reko does |
|---|---|---|
| **Modern** (default desktop/mobile) | drawer `List` items | injects a third drawer `ListItem` directly after Favorites, cloned MUI markup |
| **Legacy + TV** | visible `.emby-tabs-slider` | injects a real `.emby-tab-button[data-index="2"]` into the slider + a matching `.tabContent` panel at DOM position 2 |

Both resolve to the same panel and the same route, so it is one tab, two renderings, and
`#/home?tab=2` deep-links correctly in either.

### F3 — The tab contract is positional and self-healing injection is required.

`maintabsmanager.setTabs()` does `tabsContainerElem.innerHTML = tabsHtml` on every re-render, and indexes
panels by *DOM position*: `getTabContainersFn()` returns `querySelectorAll('.tabContent')` and
`tabContainers[selectedTabIndex]`. So Reko must (a) re-inject on every remount, (b) append its panel at
position N, and (c) allocate N by scanning for the max existing `data-index` rather than hard-coding 2 —
otherwise it collides with other tab plugins (Custom Tabs hard-codes `i + 2` and gets it wrong).

`home.tsx`'s `getTabController` has a `switch` with no `default`, so clicking tab 2 makes the dynamic
`import()` of a legacy controller reject. That is harmless — `loadTab` has `.catch()` and logs — and the
panel is already `is-active`; Reko owns its own panel lifecycle.

### F4 — Server-side: what actually works in 12.x.

| Thing | Status in 12.1 | Use |
|---|---|---|
| `IHasWebPages` → `GET /web/ConfigurationPage?name=X` | works, **anonymous**, content-type from resource extension | admin config page (`div[data-role="page"]`) |
| `IHasWebPages` → `.js` resource + `data-controller="__plugin/x"` | jellyfin-web `import()`s it as an ES module | the config page's controller |
| `[ServeFile]`, `EmbeddedFiles`, `IServerEntryPoint` | **removed** | n/a — use a controller |
| Plugin controllers under `web/...` | work, but need explicit `[Authorize]` | not needed |
| SPA fallback for unknown `/web/*` | **does not exist** (`/web/foo` → 404) | why we patch rather than route |
| `IStartupFilter` rewriting the `/web/index.html` **response** | works | how we get our script into the page without writing to the web folder |
| `TargetFramework` | **`net10.0`**, packages **`Jellyfin.Controller`/`Jellyfin.Model` `12.1.0`** | required; `net9.0` will not load |
| `build.yaml` | `targetAbi: "12.1.0.0"`, `framework: "net10.0"`, `artifacts: ["Jellyfin.Plugin.Reko.dll"]` | jprm |
| Release zip | **DLL at the zip root**, next to `meta.json` | flat, no wrapper folder |
| `manifest.json` `checksum` | **MD5** of the zip | not SHA-256 |
| Auth | `?ApiKey=` or `Authorization: MediaBrowser …`; `X-Emby-Token` and `?api_key=` now **401** | must use `ApiClient` |

The upstream `jellyfin-plugin-template` is **stale** — still `net9.0` / `10.11.5`. I will not copy it.
I will follow the `IStartupFilter` + `IHasWebPages` approach from Jellyfin-Enhanced, which is the only
in-the-wild 12.x plugin that solves exactly this problem.

### TMDB / Seerr corrections that shape the data layer

* TMDB v3 has **no batch/multi-details endpoint** — rails must be built from list payloads (which is fine,
  list objects already carry what a card needs).
* `collection` and `season` are **not** valid `append_to_response` values; that data is already inline.
* TMDB returns **no rate-limit headers**, so client-side budgeting is mandatory. I'll run a token bucket.
* Seerr merged Overseerr + Jellyseerr. The working endpoints are `/api/v1/auth/me` (**not** `/auth/user`,
  which returns an HTML 200 via the Next.js catch-all), `/api/v1/search`, `/api/v1/movie/{id}`,
  `/api/v1/tv/{id}`, `/api/v1/request`, `/api/v1/watchproviders/*`, `/api/v1/genres/*`.
* Two undocumented but essential Seerr behaviours: `X-API-User: <id>` makes the call run as that user
  (otherwise always user 1), and `GET /api/v1/user/jellyfin/{jellyfinUserId}` maps a Jellyfin GUID to a
  Seerr user. That gives per-user request attribution with no password storage.
* `POST /api/v1/request` can return **202 as a success no-op** when everything is already requested —
  must not be treated as an error.
* There is no Seerr "award winners" data and no TMDB awards data. The rail will be labelled
  **"Critically Acclaimed"** (vote consensus) rather than making a false claim.

## 1. Scope

### In
1. **Reko tab** in the Jellyfin home page — drawer entry (Modern) + real tab (Legacy/TV), beside Home and
   Favorites, with the tab badge configurable.
2. **Rotating hero billboard** — backdrop, title, logo, meta line, synopsis, actions, auto-rotate, dots.
3. **Horizontally scrolling rails** with arrow-key + button scrolling and edge fading.
4. **Ranked Top 10** rails with oversized numerals.
5. **Curated genre rails** — 12+ rails, each a TMDB query.
6. **Hover previews** — card grows, backdrop + meta + actions appear.
7. **Instant search** — debounced, keyboard navigable, across TMDB movie/tv/person.
8. **Title pages** — seasons, episode list with stills and overviews, cast, trailer, watch providers,
   collection, more-like-this.
9. **Category pages** — per-genre/per-keyword browse with sorting and filters.
10. **Seerr integration** — season picker, request, live status polling, per-user mapping.
11. **Personalised rows** derived from the signed-in user's Jellyfin playback history.
12. **In-library badges + one-click playback** using the Jellyfin library index.
13. **Continue Watching** from Jellyfin resume data.
14. **Dashboard ▸ Plugins ▸ Reko ▸ Settings** config page.
15. **Installable by repository URL** — `manifest.json` on `gh-pages`, release workflow, `jprm` zip.

### Out (deliberate)
* Injecting into Jellyfin's own global search box (user declined; the new 12.x `ISearchProvider` API is
  poorly documented and adds risk for no benefit to the brief).
* Patching the minified jellyfin-web bundle. Injecting a panel via chunk regex — the Custom Tabs
  technique — is verified to still match in 12.0 but is destroyed by any minifier change and cannot work
  in the Modern layout at all. DOM injection self-heals; bundle regex does not.
* TV-client polish beyond "it renders and is navigable".
* "Award Winners" rail (no honest data source).

## 2. Architecture

```
┌─ Server ─ Jellyfin.Controller 12.1.0, net10.0 ────────────────────────────────────┐
│                                                                                  │
│  Plugin : BasePlugin<PluginConfiguration>, IHasWebPages                          │
│     GetPages() → "rekoconfig"  (configPage.html, EnableInMainMenu)               │
│                 → "rekoconfigjs" (configPage.js, loaded via data-controller)     │
│                                                                                  │
│  PluginServiceRegistrator : IPluginServiceRegistrator                            │
│     + ScriptInjectionStartupFilter  → rewrites the /web/index.html *response*    │
│       to add <link href=/Reko/reko.css> and <script src=/Reko/client.js defer>  │
│     + TmdbClient, SeerrClient, LibraryIndex, RowCatalog, RekoPayloadBuilder,     │
│       RecommendationService, RekoCache, PrefetchService (BackgroundService)      │
│                                                                                  │
│  RekoController : ControllerBase   [Authorize]                                  │
│     /Reko/bootstrap      tab label, feature flags, TMDB config, cache key       │
│     /Reko/home           hero + rails, user-scoped                              │
│     /Reko/rail/{id}      one rail                                               │
│     /Reko/title          full detail, append_to_response                        │
│     /Reko/season, /Reko/episode, /Reko/collection, /Reko/person                │
│     /Reko/browse         discover with sort + filters                           │
│     /Reko/search         multi search                                           │
│     /Reko/state?ids=     batched in-library / request-state resolution          │
│     /Reko/seerr/status, /Reko/seerr/request (POST), /Reko/seerr/requests        │
│     /Reko/client.js, /Reko/reko.css, /Reko/js/*   (embedded resources)          │
└──────────────────────────────────────────────────────────────────────────────────┘
              │  index.html response rewritten; everything else is plain fetch
              ▼
┌─ Client ─ vanilla ES modules, no build step ─────────────────────────────────────┐
│  bootstrap  guard → install observer → watch hashchange                          │
│  inject     drawer item (Modern) · tab button+panel (Legacy/TV) · self-heal     │
│  router     #/home?tab=N&view=…&id=…  → view model; native back/forward          │
│  api        ApiClient.ajax wrappers (auth handled by jellyfin-apiclient)         │
│  views      hero · rail · card · detail · search · browse · seerr                │
└──────────────────────────────────────────────────────────────────────────────────┘
```

### Key decisions

**D1 — The TMDB API key never reaches the browser.** All TMDB and Seerr traffic is server-side. The
client only ever talks to `/Reko/*` on its own Jellyfin server. This also means one place to enforce
rate limits and one place to rotate keys.

**D2 — A server-side `LibraryIndex` is the single source of truth for everything library-related.**
One background service walks movies + series once and builds `tmdbId → { itemId, type, playbackPositionTicks, played, … }`,
keyed per user for the playback overlay. It invalidates on `ILibraryManager` add/update/remove and
refreshes on a timer. This gives in-library badges, play targets, continue-watching and personalisation
seeds from one cheap structure, and avoids per-card Jellyfin API round-trips.

**D3 — Payload minimisation.** Rails are built from TMDB *list* payloads, never per-item detail calls
(F3 above: no batch endpoint exists). A rail of 20 titles costs exactly 1 TMDB request. Only the title
page costs an `append_to_response` call.

**D4 — A token-bucket rate limiter fronts TMDB** (~40 req/s documented, no headers returned) plus a
request-coalescing cache keyed by URL with TTLs per class (hero 30 min, rails 3 h, trending 45 min,
search 15 min, detail 12 h). A rail requested by 5 users concurrently makes 1 upstream call.

**D5 — Re-inject, always.** A single `MutationObserver` plus `hashchange`/`pageshow` drives a
`ensureInjected()` that is idempotent and never throws. If jellyfin-web changes and we lose the anchors,
we log loudly and leave Jellyfin untouched rather than breaking the client.

**D6 — Degrade, don't break.** No TMDB key → the tab renders with an inline, styled "add your TMDB key"
state and links to the settings page. No Seerr → request buttons are replaced by a "not configured" state.
Everything else keeps working.

## 3. Layout

```
jellyfin-plugin-reko/
├── .github/workflows/{build.yaml,release.yaml}
├── build.yaml                    jprm metadata
├── manifest.json                 3rd-party repository manifest
├── Directory.Build.props
├── Jellyfin.Plugin.Reko.sln
├── LICENSE                       GPL-3.0 (required: links Jellyfin GPL assemblies)
├── README.md
├── docs/{ARCHITECTURE.md,INSTALL.md,API.md}
├── research/                     the TMDB + Seerr references produced during research
└── Jellyfin.Plugin.Reko/
    ├── Jellyfin.Plugin.Reko.csproj
    ├── Plugin.cs
    ├── PluginServiceRegistrator.cs
    ├── Api/{RekoController.cs,RekoDto.cs,RekoQuery.cs}
    ├── Configuration/{PluginConfiguration.cs,configPage.html,configPage.js,configPage.css}
    ├── Services/
    │   ├── ScriptInjectionStartupFilter.cs
    │   ├── Tmdb/{TmdbClient.cs,TmdbModels.cs,TmdbImages.cs,TmdbRateLimiter.cs}
    │   ├── Seerr/{SeerrClient.cs,SeerrModels.cs,SeerrUserMapper.cs}
    │   ├── Library/{LibraryIndex.cs,LibraryIndexBuilder.cs}
    │   ├── Rows/RowCatalog.cs
    │   ├── RekoPayloadBuilder.cs
    │   ├── RecommendationService.cs
    │   ├── RekoCache.cs
    │   └── PrefetchService.cs
    └── Web/
        ├── client.js
        ├── reko.css
        └── js/{router,api,store,inject,dom,hero,rail,card,detail,search,browse,seerr,modal,utils}.js
```

## 4. Build & release

* `dotnet publish -c Release -f net10.0 -p:PublishDir=artifacts/ -p:Version=X.Y.Z.W`
* jprm produces `reko_X.Y.Z.W.zip` = `Jellyfin.Plugin.Reko.dll` + `meta.json` at the **root**.
* Release workflow (on tag `v*`): build → md5/sha256 → attach to GH Release → `jprm repo add` →
  commit `manifest.json` + zips to `gh-pages`.
* Install URL: `https://isaacle-1.github.io/jellyfin-plugin-reko/manifest.json`

## 5. Verification plan

1. `dotnet build` clean with `TreatWarningsAsErrors` + StyleCop.
2. Start Jellyfin 12.1 with the built plugin, confirm it loads `Active` (not `NotSupported`).
3. `GET /Reko/bootstrap` and `/Reko/home` return 200 with a real TMDB key.
4. Confirm the config page renders in Dashboard ▸ Plugins ▸ Reko.
5. Browser-drive the web client: drawer item present, tab works, hero rotates, rails scroll, search,
   title page, request via Seerr.
6. Confirm the plugin is `Active` in `GET /Plugins` and that nothing else regressed.
