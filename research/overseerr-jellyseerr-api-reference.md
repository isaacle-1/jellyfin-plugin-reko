# Overseerr / Jellyseerr HTTP API — Integration Reference

Target use case: a **Jellyfin server-side plugin** that lets a Jellyfin user search Overseerr/Jellyseerr
and submit a media request, attributed to that Jellyfin user.

---

## 0. Sources of truth (and which ones to trust)

| Source | URL | Status |
|---|---|---|
| Overseerr OpenAPI spec | `https://api-docs.overseerr.dev/overseerr-api.yml` (Swagger UI, `swagger-config.json` → spec) | Current for Overseerr. Overseerr is Plex-only and archived. |
| Overseerr repo | `github.com/sct/overseerr` (branch `develop`) | Archived; banner says superseded by Seerr. |
| Jellyseerr repo | `github.com/Fallenbagel/jellyseerr` (branch `develop`) | **Now literally the Seerr codebase** — the `develop` branch is post-merge, files are branded "Seerr" and the spec file is named `seerr-api.yml`. |
| Seerr spec | `https://raw.githubusercontent.com/seerr-team/seerr/refs/heads/develop/seerr-api.yml` | Best single spec reference; ~8300 lines. |
| Seerr docs (Docusaurus) | `https://docs.seerr.dev/api/seerr-api/` | Rendered from the same spec. |
| Live API docs on any instance | `http://<host>:5055/api-docs` | Swagger UI served from the bundled spec. |

`api.overseerr.dev` and `api.jellyseerr.dev` **do not resolve** — they are not real doc hosts.
The correct Overseerr host is `api-docs.overseerr.dev`.

> **Trust hierarchy:** where the OpenAPI spec and the TypeScript source disagree, **the source wins**.
> The spec is generated from swagger-jsdoc comments in the route files and is frequently incomplete
> (e.g. `PublicSettings` documents one field but the route returns ~30). Everything below marked
> "verified in source" was read directly from the route/entity code.

### Project status (matters for your plugin)

Overseerr and Jellyseerr have **merged into Seerr** (`seerr-team/seerr`). Jellyseerr's `develop` branch *is*
the merged codebase. If you target Overseerr you are targeting a frozen, archived, Plex-only product.
**Target Jellyseerr/Seerr.**

---

## 1. Base URL, auth, and the two headers that matter

### Base URL

- Default bind: `http://localhost:5055`
- All endpoints live under **`/api/v1`**. The spec declares `servers: [{ url: '{server}/api/v1' }]` with
  `server` defaulting to `http://localhost:5055`.
- Self-hosted: `http(s)://<your-seerr-host>/api/v1`. Port and path are user-configurable; the plugin must
  store a configurable base URL. `PORT` env var overrides 5055.
- Sanity check with no auth: `GET {base}/api/v1/` → `{"api":"Seerr API","version":"1.0"}`

### `X-Api-Key`

```
X-Api-Key: <api key>
```

- Read via `req.header('X-API-Key')` → **HTTP header names are case-insensitive**, so `X-API-Key`,
  `X-Api-Key`, and `x-api-key` all work. Send whatever; `X-Api-Key` is conventional.
- Compared with `===` against `settings.main.apiKey`. No other auth mechanism for API keys
  (no `Authorization: Bearer`, no query param).
- **Single global key.** There are no per-user API keys. (Tracked upstream as feature request
  `sct/overseerr#4070`.) Permissions of the *acting user* are still evaluated per request.

### How a user gets an API key

1. Sign in to the Overseerr/Jellyseerr web UI as an **admin** (user `id === 1`).
2. **Settings → General → API Key** — shown on screen; copy it.
3. Or programmatically, as admin: `GET /api/v1/settings/main` returns `apiKey`
   (see the `filteredMainSettings` caveat in §11), and `POST /api/v1/settings/main/regenerate`
   rotates it.
4. Or pin it via the `API_KEY` environment variable — `generateApiKey()` returns
   `process.env.API_KEY` verbatim if set, and `load()` forces `main.apiKey` to match it. **This is the
   best option for a plugin**: set it in the container env so it is stable across restarts and upgrades.
5. Never expose it to the Jellyfin client. It is admin-equivalent by default. Store it in plugin
   configuration and call Overseerr **server-side** (which you are doing).

### `X-API-User` — undocumented impersonation header (use this)

This is the single most important finding for per-user attribution, and it is **not in the OpenAPI spec**.

From `server/middleware/auth.ts` (byte-identical in Overseerr `sct/overseerr` and Jellyseerr):

```ts
export const checkUser: Middleware = async (req, _res, next) => {
  const settings = getSettings();
  let user: User | undefined | null;

  if (req.header('X-API-Key') === settings.main.apiKey) {
    const userRepository = getRepository(User);
    let userId = 1;                       // default = the original administrator account
    if (req.header('X-API-User')) {
      userId = Number(req.header('X-API-User'));
    }
    user = await userRepository.findOne({ where: { id: userId } });
  } else if (req.session?.userId) {
    ...
  }
  if (user) { req.user = user; }
  req.locale = user?.settings?.locale ? user.settings.locale : settings.main.locale;
  next();
};
```

Consequences:

- Every API-key request is executed **as some Seerr user**. Without `X-API-User` that is **user id 1 (admin)**.
- With `X-API-User: 42`, the request runs as Seerr user 42 — that user's `permissions` are enforced,
  their request quota is charged, and their `locale`/region settings drive the response.
- `X-API-User` is only honoured **when the `X-Api-Key` is also correct**. Cookie sessions ignore it.
- There is **no permission check on `X-API-User` itself**. Any holder of the admin API key may act as
  any user. That is fine for your plugin (the key is already admin-equivalent), but it means you must
  validate the Jellyfin→Seerr user mapping yourself before sending the header.

**Recommended integration shape:**

```
GET /api/v1/user/jellyfin/{jellyfinUserId}   with X-Api-Key only
  -> 200 { id, permissions, jellyfinUsername, displayName, ... }   => seerrUserId
  -> 404 => the Jellyfin user has never signed into Jellyseerr (see §16)

GET|POST /api/v1/...                        with X-Api-Key + X-API-User: seerrUserId
```

### Alternative: cookie session

`POST /api/v1/auth/local` returns `Set-Cookie: connect.sid=...`. You can keep a `connect.sid` cookie
per Jellyfin user instead of using `X-API-User`. This is the officially documented second auth method
and requires storing `localLogin` to be enabled. `X-API-User` is far simpler for a stateless server plugin.

### `isAuthenticated` — the 403 shape

Failure is **403**, not 401, and the body is **not** `{message}`:

```json
{ "status": 403, "error": "You do not have permission to access this endpoint" }
```

This is a common gotcha: the final error handler emits `{message, errors}`, but the auth middleware
bypasses it and writes `{status, error}` directly. Handle **both** shapes.

---

## 2. `GET /api/v1/auth/me` — current user

> **Correction: the route is `/auth/me`, not `/auth/user`.** Verified in `server/routes/auth.ts`
> (`authRoutes.get('/me', ...)`) and in both specs. There is no `/auth/user` route — it will 404
> (actually fall through to the Next.js catch-all `server.get('*path', ...)` and return HTML).

Response (verified in source) — `user.toJSON()` plus a re-added `settings` sub-object:

```json
{
  "id": 1,
  "displayName": "alice",
  "email": "alice@example.com",
  "username": "alice",
  "plexUsername": null,
  "jellyfinUsername": "alice",
  "userType": 3,
  "permissions": 32,
  "avatar": "/avatarproxy/6d8f...",
  "avatarETag": null,
  "avatarVersion": null,
  "requestCount": 12,
  "plexId": null,
  "jellyfinUserId": "6d8f1c2e...",
  "movieQuotaLimit": null,
  "movieQuotaDays": null,
  "tvQuotaLimit": null,
  "tvQuotaDays": null,
  "recoveryLinkExpirationDate": null,
  "createdAt": "2024-01-01T00:00:00.000Z",
  "updatedAt": "2024-06-01T00:00:00.000Z",
  "warnings": [],
  "settings": {
    "locale": "en",
    "discoverRegion": "US",
    "streamingRegion": "US",
    "originalLanguage": "en",
    "notificationTypes": { "discord": 2, "email": 2, "...": 0 },
    "watchlistSyncMovies": false,
    "watchlistSyncTv": false
  }
}
```

Field notes (all from `server/entity/User.ts`):

- **`displayName`** is **computed** in an `@AfterLoad()` hook:
  `username || plexUsername || jellyfinUsername || email`. Never stored.
- **`userType`** — `server/constants/user.ts`:
  `PLEX = 1, LOCAL = 2, JELLYFIN = 3, EMBY = 4`.
  (Overseerr has only `PLEX = 1, LOCAL = 2`.)
- **`permissions`** — bitmask, see §8.
- **`requestCount`** is a TypeORM `@RelationCount` on `requests`, i.e. **lifetime request count for this
  user** (no date window). It is *not* a quota figure.
- `plexToken`, `jellyfinAuthToken`, `jellyfinDeviceId`, `password`, `resetPasswordGuid` are
  `select: false` at the ORM layer and are additionally stripped by `filter()`/`toJSON()`. They never
  leave the server.
- `emailNotifications` **does not exist**. There is no such field on the user object. Email notification
  preference lives at `settings.notificationTypes.email` (bitmask) and the global agent toggle is
  `emailEnabled` in `/settings/public`.
- `region` / `locale` / `originalLanguage` are **not top-level** — they are inside `settings`
  (`discoverRegion`, `streamingRegion`, `originalLanguage`, `locale`).
- `email` **is** present here (because this handler uses `toJSON()`), but it is **absent** from
  `POST /auth/local` responses and from most nested `user` objects (those use `filter()`, which strips
  `email`). Do not rely on `email` being present.

`filter(showFiltered?)` strips: `email, plexId, password, resetPasswordGuid, jellyfinDeviceId,
jellyfinAuthToken, plexToken, settings` — unless `showFiltered` is true (own profile or admin).
`toJSON()` strips only `settings`.

### The Jellyfin ↔ Seerr user mapping (the key endpoint)

**`GET /api/v1/user/jellyfin/{jellyfinUserId}`** (verified in source, Jellyseerr/Seerr only)

- Look up a Seerr user by their **Jellyfin user GUID**.
- The GUID is normalised server-side by `normalizeJellyfinGuid()`: strips `-`, lowercases, must match
  `/^[0-9a-f]{32}$/`, else **400 `Invalid Jellyfin User ID.`**. So both
  `6d8f1c2e-...-...` and `6d8f1c2e...` (32 hex) work.
- `404 User not found.` if that Jellyfin user has never signed in to Jellyseerr, or has not been
  imported by an admin.
- Returns `user.filter(callerHasMANAGE_USERS)` → includes `id`, `permissions`, `userType`,
  `jellyfinUsername`, `jellyfinUserId`, `displayName`, `avatar`, `requestCount`; `email` only if the
  caller has `MANAGE_USERS` (your plugin's key is admin, so you get it).
- Only requires `isAuthenticated()` — no per-endpoint permission gate.

Related, admin-only: `GET /api/v1/settings/jellyfin/users` returns the list of *unimported* Jellyfin
users as `[{ username, id, thumb, email }]`, and `POST /api/v1/user/import-from-jellyfin` with
`{ jellyfinUserIds: string[] }` creates the Seerr accounts. **These are the cleanest way to auto-provision
Jellyfin users into Jellyseerr from your plugin** — either call them as admin, or have the user complete
one Jellyfin SSO sign-in.

Also available: `GET /api/v1/user?q=<term>` (searches `username`, `email`, `plexUsername`,
`jellyfinUsername`), `?includeIds=1,2,3`, `?sort=displayname|created|updated|requests|usertype|role`,
`?sortDirection=asc|desc`, `?take=`, `?skip=`. Returns
`{ pageInfo: {pages,page,results,pageSize}, results: User[] }`.

**`GET /api/v1/auth/me` + `X-API-User` is the right "who am I" call:**
`GET /auth/me` with `X-Api-Key: <key>` and `X-API-User: 42` returns user 42's record — that is your
permission/identity check in one call.

---

## 3. `GET /api/v1/status` — no auth required

```json
{
  "version": "1.1.0.0",
  "commitTag": "e3f1a2b",
  "updateAvailable": false,
  "commitsBehind": 0,
  "restartRequired": false
}
```

- `version` = app version. This is where you read the version — **not** in `/settings/public`.
- `commitTag` = git SHA, or the literal `"local"` for non-git installs.
- `updateAvailable` / `commitsBehind` are **only present when a version check ran**. Controlled by
  `?checkUpdateAvailable=<bool>` query param, else falls back to `settings.main.versionCheck`
  (default `true`). This makes an outbound GitHub API call, so it can be slow — **send
  `?checkUpdateAvailable=false` from your plugin** to keep the health check fast and offline-safe.
- `restartRequired` — set by a restart flag (e.g. after a settings change needing restart).

`GET /api/v1/status/appdata` (no auth) → `{ appData, appDataPath, appDataPermissions }` — Docker volume
mount health. Useful for diagnosing "why is nothing working".

---

## 4. `GET /api/v1/search?query=...`

Params: `query` (required), `page`, `language` (defaults to the acting user's `settings.locale`).

> **Correction: the response is flat, not `{results: {mediaInfo, results}}`.**

```json
{
  "page": 1,
  "totalPages": 20,
  "totalResults": 380,
  "results": [ /* MovieResult | TvResult | PersonResult | CollectionResult */ ]
}
```

Each entry is a TMDB object **renamed to camelCase**, with an optional `mediaInfo` attached when
Overseerr already tracks that title (verified via `server/models/Search.ts`):

**MovieResult** (`mediaType: "movie"`):
`id, mediaType, adult, genreIds, originalLanguage, originalTitle, overview, popularity, releaseDate,
title, video, voteAverage, voteCount, backdropPath, posterPath, mediaInfo?`

**TvResult** (`mediaType: "tv"`):
`id, firstAirDate, genreIds, mediaType, name, originCountry, originalLanguage, originalName, overview,
popularity, voteAverage, voteCount, backdropPath, posterPath, mediaInfo?`

**PersonResult** (`mediaType: "person"`):
`id, name, popularity, adult, mediaType, profilePath, knownFor: (MovieResult|TvResult)[]`

**CollectionResult** (`mediaType: "collection"`):
`id, mediaType, adult, originalLanguage, originalTitle, title, overview, backdropPath, posterPath`

Notes:
- `mediaType` values are exactly `'movie' | 'tv' | 'person' | 'collection'` — these are the
  **TMDB** media types, distinct from the request-side `MediaType` enum.
- `mediaInfo` is present **only** if the title has a `Media` row (i.e. it was requested or scanned into
  the library). Absence means "unknown", not "unavailable".
- `mediaInfo.hasActiveRequest` is only populated on discover/recommendation endpoints when
  `main.hideRequested` is on — not on `/search`.
- There is **no `tmdbId`/`tvdbId`/`imdbId`** on search results. `id` **is** the TMDB id. Use `id` as
  `mediaId` when POSTing a request.
- `GET /api/v1/search/keyword?query=` and `/api/v1/search/company?query=` return **raw TMDB** shapes
  (snake_case), not mapped.

---

## 5. `GET /api/v1/movie/{id}` and `GET /api/v1/tv/{id}`

Params: `id` (TMDB id), `language` (optional; defaults to acting user's `settings.locale`).
Both are behind `isAuthenticated()`.

> **Correction: `cast` and `crew` are nested under `credits`, not top-level.**
> `GET /movie/{id}` returns `MovieDetails`; `GET /tv/{id}` returns `TvDetails`
> (both from `server/models/Movie.ts` / `server/models/Tv.ts`).

### MovieDetails

```jsonc
{
  "id": 1234,                       // TMDB id
  "imdbId": "tt1234567",
  "adult": false,
  "backdropPath": "/abc.jpg",       // TMDB path fragment, NOT a URL
  "budget": 1000000,
  "posterPath": "/xyz.jpg",
  "revenue": 5000000,
  "runtime": 120,                   // minutes
  "status": "Released",             // TMDB status string
  "tagline": "...",
  "title": "Movie Title",
  "originalTitle": "...",
  "originalLanguage": "en",
  "overview": "...",
  "popularity": 12.3,
  "homepage": "https://...",
  "video": false,
  "voteAverage": 7.8,
  "voteCount": 900,
  "genres": [{ "id": 18, "name": "Drama" }],
  "keywords": [{ "id": 1, "name": "anime" }],
  "productionCompanies": [{ "id": 1, "name": "...", "originCountry": "US",
                            "logoPath": "...", "description": "...",
                            "headquarters": "...", "homepage": "..." }],
  "productionCountries": [{ "iso_3166_1": "US", "name": "United States" }],
  "spokenLanguages": [{ "iso_639_1": "en", "name": "English" }],
  "releases": {                     // raw TMDB release_dates payload
    "results": [{ "iso_3166_1": "US", "rating": "PG-13",
                  "release_dates": [{ "certification": "PG-13", "iso_639_1": null,
                                      "note": "Blu ray", "type": 4,
                                      "release_date": "2017-07-12T00:00:00.000Z" }] }]
  },
  "relatedVideos": [                // trailers/teasers from YouTube
    { "site": "YouTube", "key": "9qhL2_UxXM0", "name": "Trailer for X (1978)",
      "size": 1080, "type": "Trailer",
      "url": "https://www.youtube.com/watch?v=9qhL2_UxXM0" }
  ],
  "credits": {
    "cast": [{ "id": 1, "castId": 10, "character": "Some Character",
               "creditId": "c123", "name": "Actor Name", "order": 0,
               "gender": 2, "profilePath": "/p.jpg" }],
    "crew": [{ "id": 2, "creditId": "c456", "department": "Directing",
               "job": "Director", "name": "Person Name", "gender": 1,
               "profilePath": "/q.jpg" }]
  },
  "collection": { "id": 10, "name": "A collection",
                  "posterPath": "...", "backdropPath": "..." },   // absent if not in a collection
  "externalIds": { "imdbId": "tt123", "tvdbId": 12345, "freebaseId": "...",
                   "freebaseMid": "...", "tvrageId": "...", "facebookId": "...",
                   "instagramId": null, "twitterId": null },
  "watchProviders": [
    { "iso_3166_1": "US", "link": "https://www.themoviedb.org/movie/1234/watch?locale=US",
      "buy":       [{ "id": 8, "name": "Apple TV", "logoPath": "...", "displayPriority": 1 }],
      "flatrate":  [{ "id": 9, "name": "Netflix",  "logoPath": "...", "displayPriority": 1 }] }
  ],
  "mediaInfo": { /* see §5.3 — present only if tracked */ },
  "mediaUrl": "https://jellyfin/web/index.html#!/details?id=...&context=home&serverId=...",
  "onUserWatchlist": false
}
```

`relatedVideos[].type` enum: `Clip | Teaser | Trailer | Featurette | Opening Credits | Behind the Scenes | Bloopers`.
`site` is always `YouTube`. **`url` is pre-built** by `siteUrlCreator()` as
`https://www.youtube.com/watch?v={key}` — no assembly needed. For an inline player use
`https://www.youtube.com/embed/{key}`.

### TvDetails

Same conventions, plus TV-specific fields:

```jsonc
{
  "id": 1399, "name": "Game of Thrones", "originalName": "...",
  "firstAirDate": "2011-04-17", "lastAirDate": "2019-05-19",
  "inProduction": false, "type": "Scripted", "status": "Ended",
  "numberOfSeasons": 8, "numberOfEpisodes": 73, "episodeRunTime": [60],
  "networks": [{ "id": 49, "name": "HBO", "logoPath": "...", "originCountry": "US",
                 "headquarters": "...", "homepage": "..." }],
  "languages": ["en"],
  "originCountry": ["US"],
  "contentRatings": { "results": [{ "iso_3166_1": "US", "rating": "TV-MA" }] },
  "createdBy": [{ "id": 1, "name": "...", "gender": 1, "profilePath": "..." }],
  "lastEpisodeToAir":  { /* Episode */ },
  "nextEpisodeToAir":  { /* Episode */ },
  "seasons": [ /* Season, see §6 */ ],
  "keywords": [...], "genres": [...], "credits": { "cast": [...], "crew": [...] },
  "relatedVideos": [...], "watchProviders": [...], "externalIds": {...},
  "mediaInfo": { /* ... */ },
  "mediaUrl": "...", "onUserWatchlist": false
}
```

Note: TV `credits.cast` comes from TMDB **aggregate_credits** and takes `roles[0].character` — so it
is the actor's most-frequent role, not a per-role list.

**Both movie and TV details fall back to English** if `overview` is empty in the requested locale
(explicit re-fetch in the route handler). So `overview` is generally non-empty.

### 5.3 `mediaInfo` (the `Media` entity) — verified in `server/entity/Media.ts`

> **Corrections:** there is **no** `mediaInfo.externalIds` (external IDs live on the *detail* response
> as `externalIds`; `Media` itself has flat `imdbId` and `tvdbId` columns), and **no** `rating`/`votes`
> fields anywhere. Vote data is `voteAverage`/`voteCount` on the TMDB detail object.

```jsonc
{
  "id": 42,                          // internal Media id
  "mediaType": "movie",              // 'movie' | 'tv'
  "tmdbId": 1234,
  "tvdbId": 12345,                   // nullable
  "imdbId": "tt123",                 // nullable
  "status": 5,                       // MediaStatus enum — see §8
  "status4k": 1,                     // separate 4K availability state
  "requests": [ /* MediaRequest[] — see §7 */ ],
  "seasons": [                       // TV only; EAGER-loaded, always present
    { "id": 1, "seasonNumber": 1, "status": 5, "status4k": 1,
      "createdAt": "...", "updatedAt": "..." }
  ],
  "watchlists": null | [],
  "issues": [],
  "blocklist": null,                 // present iff blocklisted
  "createdAt": "...", "updatedAt": "...",
  "lastSeasonChange": "...",         // when the library copy last changed
  "mediaAddedAt": "...",             // when the title appeared in the library; NULL if never synced
  "serviceId": 0, "serviceId4k": null,          // Radarr/Sonarr server id
  "externalServiceId": 300, "externalServiceId4k": null,  // Radarr/Sonarr internal movie/series id
  "externalServiceSlug": "movie-slug", "externalServiceSlug4k": null,
  "ratingKey": "12345", "ratingKey4k": null,      // Plex ratingKey
  "jellyfinMediaId": "abc123def456", "jellyfinMediaId4k": null,  // Jellyfin/Emby item id
  "serviceUrl": "http://radarr:7878/movie/movie-slug",   // computed @AfterLoad
  "serviceUrl4k": null,
  "mediaUrl": "https://jellyfin/web/index.html#!/details?id=abc123def456&context=home&serverId=...",
  "mediaUrl4k": null,
  "iOSPlexUrl": "plex://preplay/?metadataKey=%2Flibrary%2Fmetadata%2F12345&server=...",
  "tautulliUrl": "http://tautulli:8181/info?rating_key=12345",  // Plex + Tautulli only
  "downloadStatus": [],              // Radarr/Sonarr live download progress items
  "downloadStatus4k": null,
  "hasActiveRequest": true           // only on discover/recommend endpoints w/ hideRequested
}
```

**`mediaUrl` is the field you want** for a "Watch on Jellyfin" deep link. It is a
Jellyfin web-URL string, built by the server:
- Jellyfin → `.../web/index.html#!/details?id={jellyfinMediaId}&context=home&serverId={serverId}`
- Emby → `.../web/index.html#!/item?id={jellyfinMediaId}&context=home&serverId={serverId}`
- Plex → `...#!/server/{machineId}/details?key=%2Flibrary%2Fmetadata%2F{ratingKey}`

Only present when the title is actually in the library (i.e. `status == AVAILABLE`).

`filter(user)` on `Media` also gates `issues` (empty unless the caller has
`MANAGE_ISSUES | VIEW_ISSUES | CREATE_ISSUES`) and applies `filter()` to nested
`requests[].requestedBy` / `modifiedBy`. So nested users in `mediaInfo.requests[].requestedBy`
**lack `email`**.

---

## 6. Seasons

> **Correction: there is no `?seasons=` query parameter** on `GET /tv/{id}`. It returns all seasons
> (without episodes) unconditionally.

`GET /api/v1/tv/{tvId}` → `seasons[]`, each a `Season`:

```json
{ "id": 3624, "airDate": "2011-04-17", "episodeCount": 10, "name": "Season 1",
  "overview": "...", "posterPath": "/s1.jpg", "seasonNumber": 1 }
```

No `episodes[]` here. For the episode list:

**`GET /api/v1/tv/{tvId}/season/{seasonNumber}`** (verified in source; param is a **number**).
Note it was `{seasonId}` in older Overseerr — the param is now `seasonNumber`.

```json
{ "id": 3624, "airDate": "2011-04-17", "name": "Season 1", "overview": "...",
  "seasonNumber": 1, "posterPath": "/s1.jpg",
  "externalIds": { "imdbId": null, "tvdbId": null, "...": null },
  "episodes": [
    { "id": 63056, "name": "Winter Is Coming", "airDate": "2011-04-17",
      "episodeNumber": 1, "overview": "...", "productionCode": "101",
      "seasonNumber": 1, "showId": 1399, "voteAverage": 7.9, "voteCount": 1200,
      "stillPath": "/still.jpg" }
  ] }
```

Note: this route returns **TMDB** season data only — it does **not** include the Seerr
`mediaInfo.seasons[]` availability state. To know which seasons are already available/requested you
must cross-reference `GET /tv/{id}` → `mediaInfo.seasons[]` (`seasonNumber` + `status`/`status4k`).

> `mediaInfo.seasons[]` is the `Season` entity (`server/entity/Season.ts`): `id, seasonNumber, status,
> status4k, createdAt, updatedAt`. There are **no `episodes[]`** there either.

**Special seasons:** season `0` is specials. Jellyseerr strips season 0 from requests unless
`main.enableSpecialEpisodes` is true, and `main.partialRequestsEnabled` (default **true** in
Jellyseerr) controls whether individual seasons can be requested rather than the whole series.

---

## 7. Requests

### 7.1 `POST /api/v1/request`

**Success: `201 Created`** with the full `MediaRequest` object.

Request body — `MediaRequestBody` (verified in `server/interfaces/api/requestInterfaces.ts`):

| Field | Type | Required | Notes |
|---|---|---|---|
| `mediaType` | `'movie' \| 'tv'` | **yes** | |
| `mediaId` | `number` | **yes** | **TMDB id** |
| `seasons` | `number[] \| 'all'` | TV only | Season numbers, or the literal string `"all"`. Sending `"all"` resolves to every season with `season_number !== 0 && episode_count > 0`. |
| `is4k` | `boolean` | no | Default `false` |
| `tvdbId` | `number` | no | Only needed when the metadata provider is TVDB |
| `serverId` | `number` | no | Radarr/Sonarr server; requires `REQUEST_ADVANCED` or `MANAGE_REQUESTS` to be honoured meaningfully |
| `profileId` | `number` | no | Quality profile id |
| `profileName` | `string` | no | Alternative to `profileId` |
| `rootFolder` | `string` | no | |
| `languageProfileId` | `number` | no | Sonarr only |
| `userId` | `number` | no | **Attribute the request to another Seerr user.** Requires `MANAGE_USERS` or `MANAGE_REQUESTS`. |
| `tags` | `number[]` | no | Radarr/Sonarr tag ids |
| `ignoreQuota` | `boolean` | no | Requires `MANAGE_REQUESTS`; only effective if that quota limit > 0 |

**Minimum viable body for your plugin:**

```json
{ "mediaType": "movie", "mediaId": 1234 }
{ "mediaType": "tv", "mediaId": 1399, "seasons": [1, 2] }
```

You generally do **not** need `userId` — combine `X-API-User` (so permissions/quota apply to the right
user) with the plain body. `userId` is only needed if you want to *submit on behalf of* someone else
(e.g. an admin plugin action). Using both together is consistent: the lock/quota logic keys off
`userId` when the caller has permission.

**Status codes:**

| Code | Meaning |
|---|---|
| `201` | Created. Body is the `MediaRequest`. |
| `401` | No authenticated user (`You must be logged in to request media.`) |
| `403` | `RequestPermissionError`, `QuotaRestrictedError` (`Movie Quota exceeded.` / `Series Quota exceeded.`), or `BlocklistedMediaError` (`This media is blocklisted.`), or `You do not have permission to bypass user quota limits.` |
| `409` | `DuplicateMediaRequestError` — `Request for this media already exists.` (movies only; TV dedupes per-season) |
| `202` | `NoSeasonsAvailableError` — accepted-but-nothing-to-do: every requested season is already requested or available. Body is `{message}` and **no request is created**. |
| `400` | OpenAPI request validation failure (see §15) |
| `500` | anything else |

**Auto-approval:** if the acting user has `MANAGE_REQUESTS`, or `AUTO_APPROVE` (+`_MOVIE`/`_TV`), or
the 4K equivalents, the request is created with `status = APPROVED` and `modifiedBy` set to that user.
Otherwise `status = PENDING`.

**Duplicate rules (worth knowing before you enable a button):**
- Movies: **one** non-declined, non-completed request per `(tmdbId, is4k)`. A second attempt is a `409`
  even for a *different* user.
- TV: per-season. Overlapping seasons are silently dropped from the new request; if *nothing* is left
  you get `202`.
- Already-available seasons are dropped even if no request exists.

### 7.2 `GET /api/v1/request`

Params (all optional):

| Param | Type | Default | Notes |
|---|---|---|---|
| `take` | number | `10` | Page size |
| `skip` | number | `0` | Offset |
| `filter` | enum | all | see below |
| `sort` | `'added' \| 'modified'` | `added` | `added` → `ORDER BY request.id`, `modified` → `ORDER BY request.updatedAt` |
| `sortDirection` | `'asc' \| 'desc'` | `desc` | |
| `requestedBy` | number | — | Seerr user id |
| `mediaType` | `'movie' \| 'tv' \| 'all'` | `all` | |

> **Correction: `sort` is not just `added`** — it is `added` or `modified`, and there is a separate
> `sortDirection`. Also `filter` has **nine** values, not three, and each one filters on
> **both** `request.status` **and** `media.status` simultaneously.

`filter` semantics (verified in `server/routes/request.ts`):

| `filter` | `request.status IN` | `media.status IN` |
|---|---|---|
| `pending` | `PENDING` | *(default = all)* |
| `approved` | `APPROVED` | *(default = all)* |
| `unavailable` | `PENDING`, `APPROVED` | `UNKNOWN`, `PENDING`, `PROCESSING`, `PARTIALLY_AVAILABLE` |
| `processing` | `APPROVED` | `UNKNOWN`, `PENDING`, `PROCESSING`, `PARTIALLY_AVAILABLE` |
| `available` | *(default = all)* | `AVAILABLE` |
| `deleted` | `COMPLETED` | `DELETED` |
| `completed` | `COMPLETED` | *(default = all)* |
| `failed` | `FAILED` | *(default = all)* |
| `all` / omitted | all 5 | all 6 |

`media.status` is compared against `media.status` for non-4K requests and `media.status4k` for 4K
requests — so the filter is 4K-aware.

Response:

```json
{
  "pageInfo": { "pages": 3, "pageSize": 10, "results": 25, "page": 1 },
  "results": [ /* MediaRequest[] */ ],
  "serviceErrors": {
    "radarr":  [{ "id": 0, "name": "Radarr Main" }],
    "sonarr":  [{ "id": 0, "name": "Sonarr Main" }]
  }
}
```

`serviceErrors` lists configured Radarr/Sonarr servers that could not be reached. **Always surface
this** — a request can "succeed" (201) while the *arr never got it. If `serviceErrors` is non-empty,
poll `/request/{id}` for `status === FAILED`.

Enriched per request (computed server-side, not columns):
- `profileName` — resolved from the owning *arr's quality profile list. `undefined` if the server is
  unreachable.
- `canRemove` — **only present if the acting user has `MANAGE_REQUESTS`**; `true` if the owning Radarr/
  Sonarr server still exists. Absent (not `false`) for non-managers.

### 7.3 Per-user visibility (answers your Q2 directly)

```ts
if (!req.user?.hasPermission([MANAGE_REQUESTS, REQUEST_VIEW], { type: 'or' })) {
  if (requestedBy && requestedBy !== req.user?.id) {
    return next({ status: 403, message: "You do not have permission to view this user's requests." });
  }
  query = query.andWhere('requestedBy.id = :id', { id: req.user?.id });
} else if (requestedBy) {
  query = query.andWhere('requestedBy.id = :id', { id: requestedBy });
}
```

- **Yes, per-user request scoping exists and is automatic.** A user with neither `MANAGE_REQUESTS` nor
  `REQUEST_VIEW` is hard-scoped to their own requests.
- With `MANAGE_REQUESTS` or `REQUEST_VIEW`, you see everything, and `?requestedBy=<id>` narrows it.
- **This is why `X-API-User` is safe**: impersonating a low-privilege user restricts that request to
  their own data. Do not send `?requestedBy` for a non-viewer — you get a 403.

### 7.4 `MediaRequest` object shape (verified in `server/entity/MediaRequest.ts`)

```jsonc
{
  "id": 7,
  "type": "tv",                     // 'movie' | 'tv'
  "status": 1,                      // MediaRequestStatus — see §8
  "is4k": false,
  "isAutoRequest": false,           // created by a watchlist/auto-request job, not a human
  "ignoreQuota": false,
  "seasonCount": 3,                 // RelationCount of seasons[]
  "seasons": [                      // TV only
    { "id": 1, "seasonNumber": 1, "status": 1 }
  ],
  "serverId": 0,
  "profileId": 4,
  "profileName": "HD-1080p",        // only from GET /request (list), not from POST/GET-by-id
  "rootFolder": "/movies",
  "languageProfileId": 1,
  "tags": [1, 2],                   // array (stored comma-joined; "none" ⇄ [])
  "media": { /* Media — see §5.3 */ },
  "requestedBy": { /* filtered User: no email unless MANAGE_USERS */ },
  "modifiedBy": { /* filtered User, or null */ },
  "createdAt": "2024-06-01T10:00:27.000Z",
  "updatedAt": "2024-06-01T10:00:27.000Z"
}
```

### 7.5 Remaining routes

| Route | Notes |
|---|---|
| `GET /api/v1/request/count` | `{ total, movie, tv, pending, approved, declined, processing, available, completed }`. **Global**, not scoped to the caller. Cheap dashboard poll. |
| `GET /api/v1/request/{requestId}` | `200` / `403` (`You do not have permission to view this request.`) / `404` (`Request not found.`) |
| `PUT /api/v1/request/{requestId}` | Edit. `200` / `403` / `404` / **`409` `Only pending requests can be modified.`** / `202` (`No seasons available to request`, TV only). Body: same shape as `MediaRequestBody` but needs `mediaType`; `seasons` is **required** for TV. Owner may edit their own only if the request is TV **or** they hold `REQUEST_ADVANCED`. |
| `DELETE /api/v1/request/{requestId}` | **`204 No Content`** (empty body). Requires `MANAGE_REQUESTS` **or** (owner **and** `status === PENDING`). On permission failure returns **`401`**, not 403. `404` if not found. |
| `POST /api/v1/request/{requestId}/retry` | Requires **`MANAGE_REQUESTS`**. **Only `status === FAILED`**, else **`409` `Only failed requests can be retried.`** Sets `status = APPROVED`, `modifiedBy = caller`, which re-triggers the parent `Media` status update and the send-to-*arr. `200` with the request, or `404`. |
| `POST /api/v1/request/{requestId}/{approve\|decline}` | Requires `MANAGE_REQUESTS`. `400` if status is not literally `approve`/`decline`; **`409` `Only pending requests can be approved or declined.`** otherwise. `200` / `404`. |

Note the `retry` + `approve/decline` routes are **admin-only** — a Jellyfin plugin acting as a normal
user (via `X-API-User`) cannot use them, which is correct.

---

## 8. Enum reference (all verified in source — several differ from the published docs)

### `MediaRequestStatus` — `server/constants/media.ts`

| Value | Name | Meaning |
|---|---|---|
| `1` | `PENDING` | Awaiting approval |
| `2` | `APPROVED` | Accepted, sent to Radarr/Sonarr |
| `3` | `DECLINED` | Rejected by an admin |
| `4` | `FAILED` | Send to Radarr/Sonarr failed; retryable |
| `5` | `COMPLETED` | Fully available in the library |

> **Correction: there is no `FULFILLED` status.** The set is `PENDING, APPROVED, DECLINED, FAILED,
> COMPLETED`. The OpenAPI description string ("1 = PENDING APPROVAL, 2 = APPROVED, 3 = DECLINED") is
> both incomplete and out of date.

`COMPLETED` is set by the availability-sync job once **all** requested seasons are available.

### `MediaStatus` — `server/constants/media.ts` (applies to `mediaInfo.status`, `mediaInfo.status4k`, and `mediaInfo.seasons[].status`/`status4k`)

| Value | Name | Meaning |
|---|---|---|
| `1` | `UNKNOWN` | Not in library, not requested |
| `2` | `PENDING` | Requested, not yet picked up by Radarr/Sonarr |
| `3` | `PROCESSING` | Added to Radarr/Sonarr, downloading |
| `4` | `PARTIALLY_AVAILABLE` | Some requested seasons present |
| `5` | `AVAILABLE` | Present in the library |
| `6` | `BLOCKLISTED` | Blocklisted by an admin (Jellyseerr only) |
| `7` | `DELETED` | Was in the library, now removed |

> **Corrections:** there is **no `UNAVAILABLE`** member, and the name is **`BLOCKLISTED`**, not
> `BLACKLISTED`. Also the doc comment "6 = `DELETED`" is **wrong** — `DELETED` is `7`.
> The blocklist is Jellyseerr/Seerr-only; Overseerr has no `BLOCKLISTED` and no blocklist routes at all.

### `MediaType` — `server/constants/media.ts`
`MOVIE = 'movie'`, `TV = 'tv'`. (String, not numeric. Distinct from TMDB's `media_type`.)

### `MediaServerType` — `server/constants/server.ts`
`PLEX = 1`, `JELLYFIN = 2`, `EMBY = 3`, `NOT_CONFIGURED = 4`.
This is the value of `mediaServerType` in `/settings/public` and `settings.main.mediaServerType`.
**`NOT_CONFIGURED` (4) is your "not set up" sentinel.**

### `UserType` — `server/constants/user.ts`
`PLEX = 1`, `LOCAL = 2`, `JELLYFIN = 3`, `EMBY = 4`.

### `Permission` bitmask — `server/lib/permissions.ts`

| Bit | Name | Bit | Name |
|---|---|---|---|
| 0 | `NONE` | 262144 | `REQUEST_MOVIE` |
| 2 | `ADMIN` | 524288 | `REQUEST_TV` |
| 4 | `MANAGE_SETTINGS` | 1048576 | `MANAGE_ISSUES` |
| 8 | `MANAGE_USERS` | 2097152 | `VIEW_ISSUES` |
| 16 | `MANAGE_REQUESTS` | 4194304 | `CREATE_ISSUES` |
| 32 | `REQUEST` | 8388608 | `AUTO_REQUEST` |
| 64 | `VOTE` | 16777216 | `AUTO_REQUEST_MOVIE` |
| 128 | `AUTO_APPROVE` | 33554432 | `AUTO_REQUEST_TV` |
| 256 | `AUTO_APPROVE_MOVIE` | 67108864 | `RECENT_VIEW` |
| 512 | `AUTO_APPROVE_TV` | 134217728 | `WATCHLIST_VIEW` |
| 1024 | `REQUEST_4K` | 268435456 | `MANAGE_BLOCKLIST` |
| 2048 | `REQUEST_4K_MOVIE` | 1073741824 | `VIEW_BLOCKLIST` |
| 4096 | `REQUEST_4K_TV` | | |
| 8192 | `REQUEST_ADVANCED` | | |
| 16384 | `REQUEST_VIEW` | | |
| 32768 | `AUTO_APPROVE_4K` | | |
| 65536 | `AUTO_APPROVE_4K_MOVIE` | | |
| 131072 | `AUTO_APPROVE_4K_TV` | | |

`hasPermission()` short-circuits: **`ADMIN` (bit 2) satisfies every check**, unconditionally.

Request permission test for `POST /request`:
- movie, non-4K → `(REQUEST | REQUEST_MOVIE)` *or* admin
- movie, 4K → `(REQUEST_4K | REQUEST_4K_MOVIE)` *or* admin
- tv, non-4K → `(REQUEST | REQUEST_TV)` *or* admin
- tv, 4K → `(REQUEST_4K | REQUEST_4K_TV)` *or* admin

The practical default for a Jellyfin user is `32` (`REQUEST`).

---

## 9. Discover

> **Corrections:** `/api/v1/discover/movies/recommended` and `/api/v1/discover/tv/recommended`
> **do not exist.** There is no global "recommended" route. Two separate concepts exist:
> - **Per-title** TMDB recommendations: `GET /api/v1/movie/{movieId}/recommendations` and
>   `GET /api/v1/tv/{tvId}/recommendations` (see §13).
> - **"Recommended" as a discover slider** in the UI is `/api/v1/discover/genreslider/movie` and
>   `/discover/genreslider/tv`, which return per-genre **backdrop image paths** (hero carousels), not
>   media.

### `GET /api/v1/discover/movies` and `GET /api/v1/discover/tv`

Shared query params (parsed by a **zod** schema, so unknown values are rejected or coerced to
`undefined`; invalid `sortBy` is `.catch(undefined)` → falls back to TMDB default):

`page`, `language`, `genre`, `studio` (movies), `network` (tv), `keywords`, `excludeKeywords`,
`sortBy`, `withRuntimeGte`, `withRuntimeLte`, `voteAverageGte`, `voteAverageLte`, `voteCountGte`,
`voteCountLte`, `watchProviders`, `watchRegion`, `status` (tv), `certification`, `certificationGte`,
`certificationLte`, `certificationCountry`,
plus date windows — **movies**: `primaryReleaseDateGte` / `primaryReleaseDateLte`;
**tv**: `firstAirDateGte` / `firstAirDateLte`.

Date params accept anything `new Date()` parses and are normalised to `YYYY-MM-DD` before hitting TMDB.

`sortBy` values are TMDB sort keys, comma-joined: `popularity.desc`, `popularity.asc`,
`voteAverage.desc`, `voteAverage.asc`, `releaseDate.desc`, `releaseDate.asc`, `title.asc`,
`title.desc`, `originalTitle.asc`, `originalTitle.desc`, `voteCount.desc`, `voteCount.asc`,
`revenue.desc`, `revenue.asc`, `runtime.desc`, `runtime.asc` (movies);
`popularity.*`, `voteAverage.*`, `firstAirDate.*`, `name.asc/desc`, `originalName.*`, `voteCount.*`
(tv). Invalid values are silently dropped rather than erroring.

> **Correction: the response is flat `{page, totalPages, totalResults, keywords, results[]}` — there is
> no `results` map keyed by genre, and no top-level `genres`/`studios` arrays.** Those sub-objects are
> echoed by the *dedicated* single-value routes instead:

| Route | Extra top-level key |
|---|---|
| `/discover/movies/genre/{genreId}` | `genre: {id, name}` (`404 Genre not found.`) |
| `/discover/movies/studio/{studioId}` | `studio: {id, name, logoPath, originCountry, ...}` |
| `/discover/movies/language/{language}` | `language: {iso_639_1, english_name, name}` (`404 Language not found.`) |
| `/discover/tv/genre/{genreId}` | `genre` |
| `/discover/tv/network/{networkId}` | `network: {id, name, logoPath, originCountry, ...}` |
| `/discover/tv/language/{language}` | `language` |

`results[]` items are `MovieResult` / `TvResult` (§4) with `mediaInfo` attached, and
`hasActiveRequest` set when `main.hideRequested` is enabled. `keywords` is only non-empty when you
passed `?keywords=`.

### `GET /api/v1/discover/trending`

Params: `mediaType` (`all` | `movie` | `tv`, default `all`), `timeWindow` (`day` | `week`, default
`day`), `page`, `language`. Returns the same flat shape; with `mediaType=all` results may be movies,
tv, people **and collections**.

### Other discover routes

`/discover/movies/upcoming`, `/discover/tv/upcoming` (from today forward),
`/discover/keyword/{keywordId}/movies`, `/discover/genreslider/movie`, `/discover/genreslider/tv`,
`/discover/watchlist` (Plex watchlist of the acting user; falls back to the Seerr watchlist table for
non-Plex users, else empty).

---

## 10. `GET /api/v1/settings/public` — the configuration probe

**No authentication required** (`security: []` in the spec, and it is registered on the top-level
router *before* the `isAuthenticated(ADMIN)` `/settings` mount).

> **Corrections:** there is **no** `overseerrVersion` / `jellyseerrVersion` field here, **no**
> `mediaServerStatus`, and **no** TMDB-API-key flag. Version comes from `/status`. Exact shape, from
> the `fullPublicSettings` getter in `server/lib/settings/index.ts`:

```json
{
  "initialized": true,                        // setup wizard completed
  "applicationTitle": "Jellyseerr",
  "applicationUrl": "https://seerr.example.com",
  "hideAvailable": false,
  "hideBlocklisted": false,
  "hideRequested": false,
  "localLogin": true,                         // POST /auth/local usable
  "mediaServerLogin": true,                   // Jellyfin/Emby/Plex SSO usable
  "movie4kEnabled": true,                     // ∃ default 4K Radarr
  "series4kEnabled": false,                   // ∃ default 4K Sonarr
  "discoverRegion": "US",
  "streamingRegion": "US",
  "originalLanguage": "en",
  "mediaServerType": 2,                       // MediaServerType: 1=Plex 2=Jellyfin 3=Emby 4=NOT_CONFIGURED
  "jellyfinExternalHost": "https://jelly.example.com",
  "jellyfinForgotPasswordUrl": "https://jelly.example.com/web/index.html#!/forgotpassword.html",
  "jellyfinServerName": "Main Server",
  "partialRequestsEnabled": true,
  "enableSpecialEpisodes": false,
  "cacheImages": true,
  "vapidPublic": "BEl62i...",
  "enablePushRegistration": false,            // forced false if caller disabled webpush
  "locale": "en",
  "emailEnabled": true,                       // global SMTP agent on
  "userEmailRequired": false,
  "newPlexLogin": true,
  "youtubeUrl": "",
  "versionCheck": true,
  "plexClientIdentifier": "6919275e-142a-48d8-be6b-93594cbd4626"
}
```

The three Jellyfin keys are `undefined` (i.e. **absent from the JSON**) when not configured.

**How to use this as a "is it configured / is it right for me" check:**

```
1. GET /api/v1/settings/public
     -> 404 / connection refused  => nothing there; URL wrong
     -> initialized === false      => setup wizard not finished; requests will fail
     -> mediaServerType === 4      => NOT_CONFIGURED; no media server linked
     -> mediaServerType !== 2      => not a Jellyfin instance (you probably want to bail)
2. GET /api/v1/status?checkUpdateAvailable=false
     -> version                    => feature-detect from here, not from /settings/public
3. GET /api/v1/settings/radarr + /settings/sonarr   (admin only)
     -> confirm at least one *arr server and that it is reachable
```

Because the response omits rather than nulls the Jellyfin fields, test with
`"jellyfinServerName" in json` or `json.get("jellyfinServerName")` being truthy.

`POST /api/v1/settings/initialize` (admin) sets `initialized = true` and returns the same
`PublicSettings` object.

---

## 11. `GET /api/v1/settings` (admin)

> **Correction: there is no aggregate `GET /api/v1/settings` endpoint.** The whole `/settings` subtree
> is mounted with `isAuthenticated(Permission.ADMIN)`, and the aggregate is split into per-section
> routes. Requesting `/api/v1/settings` itself falls through to the Next.js catch-all.

| Route | Returns |
|---|---|
| `GET /api/v1/settings/main` | `MainSettings` — **includes `apiKey`, but only if the caller has `ADMIN`** (`filteredMainSettings()` does `omit(main, 'apiKey')` otherwise). Your plugin's key is admin, so you get the key back. |
| `GET /api/v1/settings/network` | `NetworkSettings` — `csrfProtection`, `forceIpv4First`, `trustProxy`, `proxy{...}`, `dnsCache{...}`, `apiRequestTimeout` |
| `GET /api/v1/settings/plex`, `/plex/library`, `/plex/users`, `/plex/sync` | Plex config & device list |
| `GET /api/v1/settings/jellyfin` | `{ name, ip, port, useSsl, urlBase, externalHostname, jellyfinForgotPasswordUrl, libraries[], serverId, apiKey }` — **contains the Jellyfin API key; treat as secret** |
| `GET /api/v1/settings/jellyfin/library`, `/jellyfin/users`, `/jellyfin/sync` | Library list, unimported-user list, scanner status |
| `GET /api/v1/settings/tautulli` | Tautulli config (Plex only) |
| `GET /api/v1/settings/radarr`, `/sonarr` | Arrays of `RadarrSettings` / `SonarrSettings` — **with `apiKey` in each entry** |
| `GET /api/v1/settings/metadatas` | `{ settings: { tv: 'tmdb'\|'tvdb', anime: 'tmdb'\|'tvdb' } }` |
| `GET /api/v1/settings/about` | `{ version, totalMediaItems, totalRequests, tz, appDataPath }` — cheap health probe |
| `GET /api/v1/settings/jobs`, `/cache`, `/logs` | Ops info. `/logs` is rate-limited to **50 req/min** per IP. |

For "are Radarr/Sonarr healthy", prefer `GET /api/v1/request`'s `serviceErrors` (no auth gate, and
already scoped to the data you care about) over poking admin settings. If you must use settings, the
shape of a `RadarrSettings` entry is:

```jsonc
{ "id": 0, "name": "Radarr Main", "hostname": "127.0.0.1", "port": 7878,
  "apiKey": "secret", "useSsl": false, "baseUrl": "", "externalUrl": "",
  "activeProfileId": 1, "activeProfileName": "HD-1080p", "activeDirectory": "/movies",
  "is4k": false, "isDefault": true, "isActive": true,
  "minimumAvailability": "In Cinema",
  "tags": [], "syncEnabled": false, "preventSearch": false,
  "tagRequests": false, "overrideRule": [] }
```

`SonarrSettings` adds `seriesType`, `animeSeriesType`, `activeAnime*`, `activeLanguageProfileId`,
`enableSeasonFolders`, `monitorNewItems` ('all' | 'none').

Use `GET /api/v1/service/radarr` / `/service/sonarr` for the **non-sensitive** (API-key-redacted)
list — that is the endpoint designed for this.

---

## 12. Watch providers & genres

> **Corrections:** `/api/v1/providers` and `/api/v1/providers/{id}` **do not exist**, and neither does
> `/api/v1/genre`. The real routes:

| Route | Auth | Returns |
|---|---|---|
| `GET /api/v1/watchproviders/regions` | **none** | `[{ iso_3166_1, english_name, native_name }]` |
| `GET /api/v1/watchproviders/movies?watchRegion=US` | **none** | `[{ displayPriority, logoPath, id, name }]` — provider list only |
| `GET /api/v1/watchproviders/tv?watchRegion=US` | **none** | same |
| `GET /api/v1/genres/movie?language=en` | `isAuthenticated()` | `[{ id, name }]` |
| `GET /api/v1/genres/tv?language=en` | `isAuthenticated()` | `[{ id, name }]` |
| `GET /api/v1/regions` | `isAuthenticated()` | `[{ iso_3166_1, english_name, name }]` |
| `GET /api/v1/languages` | `isAuthenticated()` | `[{ iso_639_1, english_name, name }]` |
| `GET /api/v1/certifications/movie`, `/certifications/tv` | `isAuthenticated()` | TMDB certification tables |
| `GET /api/v1/studio/{id}` | **none** | `{ id, name, logoPath, originCountry, description, headquarters, homepage }` |
| `GET /api/v1/network/{id}` | **none** | `{ id, name, logoPath, originCountry, headquarters, homepage }` |
| `GET /api/v1/keyword/{keywordId}` | **none** | TMDB keyword object |
| `GET /api/v1/backdrops` | **none** | `string[]` of trending backdrop paths (used for the UI hero) |

Per-title watch providers are on the detail responses as `watchProviders[]`
(§5), shaped `{iso_3166_1, link, buy[], flatrate[]}`.

Note the odd auth asymmetry — genres/languages need auth but watch providers, studios, networks,
keywords and backdrops do not. Everything still needs `checkUser` to have run; unauthenticated is fine
only because those handlers have no `isAuthenticated()`.

---

## 13. Per-title recommendations / similar

> **Confirmed exact route names:**

```
GET /api/v1/movie/{movieId}/recommendations     ✔ exists
GET /api/v1/movie/{movieId}/similar              ✔ exists
GET /api/v1/tv/{tvId}/recommendations            ✔ exists
GET /api/v1/tv/{tvId}/similar                    ✔ exists
```

There are **no** `/discover/movies/recommended` or `/discover/tv/recommended` routes.

Params: `page`, `language`. Response is the flat paginated envelope
`{ page, totalPages, totalResults, results: (MovieResult|TvResult)[] }` with `mediaInfo` attached and
`hasActiveRequest` populated (these four routes pass `{ includeActiveRequest: true }`, so the flag
appears even when `hideRequested` is off).

Also per title:
- `GET /api/v1/movie/{movieId}/ratings` — Rotten Tomatoes; `404 Rotten Tomatoes ratings not found.`
- `GET /api/v1/movie/{movieId}/ratingscombined` — RT + IMDB → `{ rt?, imdb? }`; `404 No ratings found.`
  **Movies only** — there is no `/tv/{tvId}/ratingscombined`.
- `GET /api/v1/person/{personId}`, `/person/{personId}/combined_credits`
- `GET /api/v1/collection/{collectionId}` → `{ id, name, overview, posterPath, backdropPath, parts: MovieResult[] }`
- `GET /api/v1/media` (list tracked media), `GET /api/v1/media/{mediaId}`, `GET /api/v1/media/{mediaId}/file`,
  `GET /api/v1/media/{mediaId}/watch_data`, `POST /api/v1/media/{mediaId}/{available|partial|unavailable|pending|unknown}`

---

## 14. Errors

Two distinct error shapes. **Handle both.**

**(a) Route/thrown errors — via the Express error handler** in `server/index.ts`:

```json
{ "message": "Request for this media already exists.", "errors": undefined }
```

`errors` is a `string[]` used only by a few endpoints (e.g. user creation returns
`errors: ["USER_EXISTS"]`); it is otherwise `undefined` and is **omitted** from the JSON.

**(b) Auth middleware errors — written directly, bypassing the handler:**

```json
{ "status": 403, "error": "You do not have permission to access this endpoint" }
```

**(c) `POST /api/v1/auth/local` and some `auth/*` handlers return yet another shape:**

```json
{ "error": "Password sign-in is disabled." }
```
`{ "error": "Plex login is disabled" }`, `{ "error": "Jellyfin login is disabled" }`,
`{ "error": "You must provide both an email address and a password." }`

**Status codes to handle:**

| Code | Typical source |
|---|---|
| `400` | OpenAPI validation failure, invalid Jellyfin GUID, bad `approve`/`decline` value |
| `401` | `POST /request` with no user; `DELETE /request/{id}` permission failure (**not** 403) |
| `403` | `isAuthenticated()` (shape b), permissions, quota exceeded, blocklisted, denied Jellyfin/Plex sign-in |
| `404` | Unknown ids; `Request not found.`; `Genre not found.`; `Language not found.`; `User not found.`; `Cache not found.`; `Job not found.` |
| `405` | Attempting to delete user id 1, or a user with `ADMIN` when you aren't id 1 |
| `409` | Duplicate movie request; modifying a non-`PENDING` request; approving/declining a non-`PENDING` request; retrying a non-`FAILED` request; `User already exists with submitted email.` |
| `202` | `No seasons available to request` — **success-with-no-op**, not an error |
| `500` | Most upstream failures (TMDB down, *arr unreachable). Messages are deliberately vague: `Unable to retrieve movie.`, `Unable to retrieve search results.`, `Something went wrong.` |

**A `404` on `/api/v1/movie/{id}` etc. is usually a `500`.** These handlers wrap everything in
try/catch and return `500 Unable to retrieve movie.` on any throw — including TMDB 404 for an
unknown id. Do not treat non-200 from detail routes as "does not exist".

Also: unknown paths under `/api/v1` fall through to `server.get('*path', handle)` (the Next.js
renderer) and will return **HTML with a 200**, not JSON. **Always check `Content-Type` before parsing.**

---

## 15. CORS, CSRF, rate limits, request validation

### CORS
There is **no CORS middleware**. Server-side calls are unaffected — irrelevant for your plugin. Do not
attempt to call the API from the Jellyfin *browser* client.

### CSRF — the one real footgun
`csurf` is mounted **only if `settings.network.csrfProtection === true`**, and when enabled it is
applied **globally to every request, with no exemption for `X-Api-Key`**:

```ts
if (settings.network.csrfProtection) {
  server.use(csurf({ cookie: { httpOnly: true, sameSite: true, secure: !dev, key: '_csrf', path: '/' } }));
  server.use((req, res, next) => { res.cookie('XSRF-TOKEN', req.csrfToken(), { sameSite: true, secure: !dev }); next(); });
}
```

- Default is **`false`**, so in the common case `X-Api-Key` alone is sufficient — including for
  `POST /api/v1/request`.
- **If an admin has enabled it, `X-Api-Key` alone will NOT work for POST/PUT/DELETE** (403
  `invalid csrf token`). You would need to also send the `_csrf` cookie and an `X-CSRF-TOKEN` header
  (or a `_csrf` body field), which means first doing a `GET` to collect them and tracking session
  cookies.
- Note it also flips the session cookie to `sameSite: 'strict'`, breaking the cookie-auth approach
  cross-site.
- **Recommendation for the plugin:** document "if requests fail with 403 invalid csrf token, turn off
  Network → CSRF Protection" in your setup instructions, and detect the failure by message rather
  than by status code alone (it is a 403 like every other permission error).

### Rate limiting
Effectively **none** on the endpoints you need — no global limiter. The only `express-rate-limit`
in the codebase is `GET /api/v1/settings/logs` at **50 requests / 60 s per IP**. Your search/detail/
request calls are unthrottled. Be a good citizen anyway: cache TMDB-backed detail responses.

### Request validation — easy to trip over
`express-openapi-validator` is mounted **globally** with `validateRequests: true`, validating every
request body/params against the bundled spec **before** the route runs. Malformed or unexpected
fields produce a `400` with the validator's own error shape, and the route's real validation never
runs. A `res.json` wrapper stringifies dates first to keep response validation happy.

Implications: send exactly the documented fields; don't add extra properties; `mediaId` must be a
number; `mediaType` must be exactly `"movie"` or `"tv"`.

### Images / URLs
`posterPath` / `backdropPath` / `logoPath` / `profilePath` are **TMDB path fragments**, not URLs.
Prefix with `https://image.tmdb.org/t/p/w500` (posters), `/t/p/w1280` (backdrops) to build an image
URL. There is also a proxy at `{base}/imageproxy/tmdb/{...}` if you want Overseerr's image cache
(`cacheImages`).

Avatars are `avatar: "/avatarproxy/{jellyfinUserId}?v={avatarVersion}"` — a **relative** path on the
Overseerr host, so resolve it against your configured base URL, not against your Jellyfin host.

---

## 16. Jellyseerr specifics: local users, per-user requests, impersonation

### Yes — Jellyseerr supports logging in as a *local* user, and scoping requests per user.

`POST /api/v1/auth/local` — body `{ email, password }`:

- Requires `settings.main.localLogin === true`, else `500 { error: 'Password sign-in is disabled.' }`.
  (This is `localLogin` in Jellyseerr/Seerr; Overseerr has the identical flag.)
- Missing `email`/`password` → `500 { error: 'You must provide both an email address and a password.' }`
- On success: `200` with `user.filter()` — **note `email` is stripped here**, so identify the user by
  `id` — and a `connect.sid` session cookie is set.
- Bad credentials → `403 { message: 'Access denied.' }`.
- `POST /api/v1/auth/logout` destroys the session (and, on Jellyfin/Emby, deletes the Jellyfin device).

**The key Jellyseerr behaviour:** when a Jellyfin user signs in through
`POST /api/v1/auth/jellyfin` for the first time, Jellyseerr **mirrors the password they just typed
into a local password hash**:

```ts
//initialize Jellyfin/Emby users with local login
const passedExplicitPassword = body.password && body.password.length > 0;
if (passedExplicitPassword) {
  await user.setPassword(body.password ?? '');
}
```

So every Jellyfin-SSO user in Jellyseerr can *also* sign in with email + password via `/auth/local`.
**Do not implement that in your plugin** — it would require storing user passwords. Use
`X-API-User` impersonation instead, which needs no credentials.

### Jellyfin user import / SSO flow
- `POST /api/v1/auth/jellyfin` body `{ username, password, hostname?, port?, urlBase?, useSsl?, email?, serverType? }`.
  The first-ever sign-in also *configures* the Jellyfin server (creates an API token, sets
  `jellyfin.ip/port/serverId/apiKey`) and **requires the Jellyfin user to be an administrator**
  (`Policy.IsAdministrator`), else `403 NotAdmin`.
- Subsequent sign-ins: matched on `jellyfinUserId`. Unknown Jellyfin users are only auto-created if
  `settings.main.newPlexLogin` is true (the Jellyfin equivalent of "new Plex login"), else
  `403 Access denied.` — i.e. **an admin must import them first**.
- Admin bulk import: `GET /api/v1/settings/jellyfin/users` then
  `POST /api/v1/user/import-from-jellyfin` `{ jellyfinUserIds: [...] }`.
- Jellyfin **Quick Connect** (Jellyseerr only): `POST /auth/jellyfin/quickconnect/initiate` →
  `{ code, secret }`, `GET /auth/jellyfin/quickconnect/check?secret=` → `{ authenticated }`,
  `POST /auth/jellyfin/quickconnect/authenticate` `{ secret }` → user + session. Not usable
  server-side for arbitrary users.
- `avatar` for Jellyfin users is the proxied `/avatarproxy/{jellyfinUserId}` path, refreshed at
  login via `checkAvatarChanged()`.

### Per-user request visibility — summary
- Automatic and enforced: non-viewers are hard-scoped to `requestedBy.id = req.user.id` (§7.3).
- Visibility of others' requests requires `MANAGE_REQUESTS` (16) **or** `REQUEST_VIEW` (16384).
- `REQUEST_VIEW` is a distinct, read-only permission added in Jellyseerr — a good option to grant to
  a plugin-facing role.
- Admins (`MANAGE_REQUESTS`) can additionally read *any* single request via
  `GET /api/v1/request/{requestId}`, which checks `request.requestedBy.id !== req.user.id`.

### Impersonation recap
- `X-API-User: <seerrUserId>` + valid `X-Api-Key` ⇒ act as that user. Undocumented but present and
  identical in Overseerr and Jellyseerr. Default without the header: user id 1 (admin).
- No permission check on the header itself.
- Only works with `X-Api-Key`; ignored for cookie sessions.

### Blocklist (Jellyseerr/Seerr only)
`GET /api/v1/blocklist`, `GET /api/v1/blocklist/{tmdbId}`,
`POST /api/v1/blocklist/{tmdbId}`, `DELETE /api/v1/blocklist/{tmdbId}`,
`POST /api/v1/blocklist/collection/{collectionId}`, `GET /api/v1/blocklist/tags`.
`/api/v1/blacklist` still exists but is a **deprecated alias** sunset **2026-06-01**.
Blocked media throws `BlocklistedMediaError` → `403 This media is blocklisted.`, and
`mediaInfo.status` becomes `6` (`BLOCKLISTED`).

---

## 17. Overseerr vs Jellyseerr — explicit differences

Overseerr is **archived** and Plex-only. Treat the following as the diff:

| Area | Overseerr (`sct/overseerr`) | Jellyseerr / Seerr |
|---|---|---|
| Media servers | Plex only | Plex, **Jellyfin**, **Emby** (one at a time) |
| `MediaServerType` | Plex / not-configured | `PLEX=1, JELLYFIN=2, EMBY=3, NOT_CONFIGURED=4` |
| `UserType` | `PLEX=1, LOCAL=2` | `PLEX=1, LOCAL=2, JELLYFIN=3, EMBY=4` |
| `User` fields | no `jellyfinUsername`, no `jellyfinUserId` | both present |
| `User.avatar` | Plex thumb URL | `/avatarproxy/{jellyfinUserId}?v=...` relative path |
| `GET /auth/user` | **404 / HTML** — use `/auth/me` | same |
| Auth routes | `/auth/plex`, `/auth/local`, `/auth/logout`, `/auth/reset-password` | adds `/auth/jellyfin`, `/auth/jellyfin/quickconnect/{initiate,check,authenticate}` |
| `X-Api-Key`, `X-API-User`, `connect.sid` | identical | identical |
| `MediaStatus` | `UNKNOWN=1 … AVAILABLE=5, DELETED=6` (no `BLOCKLISTED`) | `BLOCKLISTED=6`, `DELETED=7` |
| `MediaRequestStatus` | `PENDING, APPROVED, DECLINED, FAILED, COMPLETED` | identical |
| `Permission` enum | ends at `REQUEST_VIEW` (16384) | adds `AUTO_APPROVE_4K*`, `REQUEST_MOVIE/TV`, `MANAGE_ISSUES`/`VIEW_ISSUES`/`CREATE_ISSUES`, `AUTO_REQUEST*`, `RECENT_VIEW`, `WATCHLIST_VIEW`, `MANAGE_BLOCKLIST`, `VIEW_BLOCKLIST` |
| Request body | `{mediaType, mediaId, seasons}` | adds `is4k, tvdbId, serverId, profileId, profileName, rootFolder, languageProfileId, userId, tags, ignoreQuota` |
| `GET /request` | `take, skip, filter, sort=added` | adds `sort=added|modified`, `sortDirection`, `requestedBy`, `mediaType`; `filter` has 9 values; response adds `pageInfo.pageSize`, `serviceErrors`, `profileName`, `canRemove` |
| `GET /user/jellyfin/{id}` | **does not exist** | exists — your user-mapping endpoint |
| Blocklist | none (`/blacklist` absent) | `/blocklist` (+ deprecated `/blacklist` alias) |
| Watchlist | Plex watchlist only | `/watchlist` routes + Plex fallback |
| `Permission` on user | — | `REQUEST_VIEW` enables cross-user request reads |
| Main settings | `hideAvailable` | adds `hideBlocklisted`, `hideRequested`, `mediaServerLogin`, `defaultQuotas`, `blocklist*`, `enableSpecialEpisodes`, `locale`, `youtubeUrl`, `versionCheck` |
| `NetworkSettings` | `csrfProtection`, `trustProxy` | adds `forceIpv4First`, `proxy{...}`, `dnsCache{...}`, `apiRequestTimeout` |
| `/settings/public` | spec documents only `{initialized}`; source also has a `fullPublicSettings` getter (shape unverified here — expect no `jellyfin*` keys) | full shape in §10 |
| Metadata provider | TMDB only | TMDB or TVDB (`/settings/metadatas`, `tv`/`anime` independent) |
| DB | SQLite | SQLite **or PostgreSQL** |
| Notifications | Discord, Slack, WebPush, Webhook, Telegram, Pushbullet, Pushover, Gotify, Email, LunaSea | adds **ntfy**, drops LunaSea |
| `POST /request/{id}/retry` | present | present (same rules) |
| `/status` | `version`, `commitTag`, `updateAvailable`, `commitsBehind`, `restartRequired` | identical |
| Spec file | `overseerr-api.yml` | `seerr-api.yml` (renamed post-merge) |
| Docs | `api-docs.overseerr.dev` | `docs.jellyseerr.dev`, `docs.seerr.dev` |
| Docker | `lscr.io/linuxserver/overseerr` | `fallenbagel/jellyseerr` |

**Bottom line for your plugin: target Jellyseerr/Seerr only.** Overseerr lacks the Jellyfin user
mapping endpoint entirely, so there is no clean per-user attribution path against it.

---

## 18. Recommended integration flow

```
Configuration (Jellyfin plugin settings)
  - seerrBaseUrl      e.g. http://jellyseerr:5055   (no trailing /api/v1)
  - seerrApiKey       admin key (prefer API_KEY env pin, or copy from Settings → General)

Health / setup check (once, cached)
  GET {base}/api/v1/status?checkUpdateAvailable=false   -> 200 => reachable; read `version`
  GET {base}/api/v1/settings/public
      -> initialized === true                (else: "finish setup in the web UI")
      -> mediaServerType === 2               (else: "not a Jellyfin instance")
      -> mediaServerType === 4               (else: "no media server linked")
      -> localLogin / mediaServerLogin       (informational)

Per-request user resolution
  GET {base}/api/v1/user/jellyfin/{jellyfinUserId}     X-Api-Key only
      200 -> { id: seerrUserId, permissions, jellyfinUsername, ... }
      404 -> the Jellyfin user must sign in to Jellyseerr once, or an admin must
             POST /api/v1/user/import-from-jellyfin  { jellyfinUserIds: [jellyfinUserId] }
  (optionally) GET {base}/api/v1/auth/me
             X-Api-Key + X-API-User: seerrUserId     -> re-confirm identity + quota context

Search
  GET {base}/api/v1/search?query={q}&page=1            X-Api-Key + X-API-User
      -> { results: [...] }  each with .mediaType ('movie'|'tv'|'person'|'collection') and .id (TMDB)

Detail (for the request dialog)
  GET {base}/api/v1/movie/{id}                          X-Api-Key + X-API-User
  GET {base}/api/v1/tv/{id}                             X-Api-Key + X-API-User
      -> .mediaInfo?.seasons[] for per-season availability
      -> .mediaInfo?.mediaUrl  for the "Watch on Jellyfin" link
      -> .seasons[]            for the season picker (episodeCount > 0)

Submit
  POST {base}/api/v1/request                             X-Api-Key + X-API-User
  { "mediaType": "movie", "mediaId": 1234 }
  { "mediaType": "tv", "mediaId": 1399, "seasons": [1,2] }

      201 -> created; status 2 = auto-approved, 1 = needs admin approval
      403 -> permission / quota / blocklisted  -> surface the `message`
      409 -> duplicate request                   -> "already requested"
      202 -> every season already requested/available -> treat as success, no-op

Status / history
  GET {base}/api/v1/request?requestedBy={seerrUserId}&take=20&sort=added&sortDirection=desc
  GET {base}/api/v1/request/count
      -> { pageInfo, results[], serviceErrors }   // surface serviceErrors!
      -> map results[].status: 1 Pending, 2 Approved, 3 Declined, 4 Failed, 5 Available
      -> results[].media.status: 5 => available -> use media.mediaUrl
      -> self-service cancel: DELETE /api/v1/request/{id} (owner + status 1) -> 204
```

### Pitfall checklist

1. `/auth/me`, not `/auth/user`.
2. Always send `X-API-User` (after resolving the Jellyfin user) or every request is attributed to admin
   id 1 and charged against the admin's quota.
3. `sort=added` **and** `sortDirection=desc` — `sort=added` alone does not order newest-first.
4. `202` from `POST /request` is a **success no-op**, not a failure.
5. `409` on retry/approve/decline means "wrong state" — re-read the request, don't retry blindly.
6. `DELETE /request/{id}` returns **401** (not 403) for permission denial.
7. Auth failures return `{status, error}`; everything else returns `{message, errors?}`.
8. Unknown paths return **HTML 200** — check `Content-Type`.
9. Detail routes return **500** (not 404) for a genuinely missing TMDB id.
10. Enable `X-Api-Key` only; leave `Network → CSRF Protection` off or you must implement `_csrf`.
11. `mediaInfo` absent ≠ unavailable. It only appears for titles Overseerr already tracks.
12. Poster/backdrop paths are TMDB fragments — prefix with `https://image.tmdb.org/t/p/...`.
13. `/settings/public` gives no version — use `/status`, and pass `?checkUpdateAvailable=false`.
14. If a 201'd request never shows up in Radarr/Sonarr, check `serviceErrors` and `status === 4`.
15. `seasons: "all"` is a valid string, but season 0 is excluded unless
    `enableSpecialEpisodes` is on.
