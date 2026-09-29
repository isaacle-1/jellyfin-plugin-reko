# TMDB API v3 — Integration Reference for a Netflix-style Browsing UI

Companion to `overseerr-jellyseerr-api-reference.md`. This document covers the **metadata / discovery /
images** side of a Netflix-style UI: rails of posters, hero backdrops, title pages, cast rows,
search, and "where can I watch this" panels.

Everything below was read out of TMDB's own published OpenAPI 3.1 spec (148 paths) and guide pages, not
from memory. Where I deviate from a common assumption I say so explicitly and mark it.

---

## 0. Sources of truth

| Source | URL | Notes |
|---|---|---|
| **Consolidated OpenAPI spec (best single source)** | `https://developer.themoviedb.org/openapi/tmdb-api.json` | ~3 MB, OpenAPI 3.1.0, `title: tmdb-api`, `version: 3`, **148 paths**, `servers: [https://api.themoviedb.org]`. This is the authoritative endpoint list. |
| Spec index page | `https://developer.themoviedb.org/openapi` | Links to `tmdb-api.json`. |
| Per-endpoint reference (markdown) | `https://developer.themoviedb.org/reference/<slug>.md` | Append `.md` to any docs URL for clean markdown. The site is behind Anubis bot protection for HTML but the `.md` variants serve fine. |
| `llms.txt` index | `https://developer.themoviedb.org/llms.txt` | **Incomplete** — lists only ~30 of the 148 reference pages. Don't rely on it for discovery. |
| Guides | `https://developer.themoviedb.org/docs/{slug}.md` | `authentication-application`, `append-to-response`, `image-basics`, `image-languages`, `languages`, `region-support`, `rate-limiting`, `errors`, `json-and-jsonp`, `popularity-and-trending`, `daily-id-exports`, `finding-data`, `search-and-query-for-details`, `getting-started`. |
| Live API | `https://api.themoviedb.org` | Any request without a valid credential returns `401` **for every path**, including nonsense paths — so you cannot probe endpoint existence unauthenticated. |

**Slug naming gotcha (costs you time):** the reference slugs are not uniform. TV series uses
`tv-series-*` (`/reference/tv-series-details`), trending movie is `trending-movies` (plural),
TV certifications is `certifications-tv-list` (plural "certifications"), the watch-provider region
list is `watch-providers-available-regions`, and several pages have nonsense slugs
(`/3/network/{id}/images` → `alternative-names-copy`). The HTML reference index at
`https://developer.themoviedb.org/reference/getting-started` contains the real nav; the `sitemap.xml`
does not (it lists only 28 pages).

### Corrections to assumptions in the original brief

| Brief said | Reality |
|---|---|
| `append_to_response=...collection...` on movie details | **Not a thing.** There is no `/3/movie/{id}/collection` endpoint. Collection info is already inline as `belongs_to_collection`. |
| `append_to_response=...season...` on TV details | **Not a thing.** There is no `/3/tv/{id}/season` endpoint. Season summaries are already inline as `seasons[]`. |
| `/3/tv/now_playing` | **Does not exist.** The TV equivalent of `now_playing` is **`/3/tv/on_the_air`**. |
| `/3/collection/{id}/search` | **Does not exist.** It is **`/3/search/collection?query=`**. |
| `with_release_date` on discover/movie | Not a param. Use `release_date.gte` / `release_date.lte` (or `primary_release_date.gte` / `.lte`). |
| `/3/search/{type}?query=` suggestions endpoint | No suggestion/autocomplete endpoint exists in v3. See §9.4. |
| `/3/collection/list`, `/3/company/list`, `/3/keyword/list` | **Not in the official 148-path spec.** They are long-standing, widely-used, undocumented endpoints that still respond on the live API, but TMDB gives no support guarantee. See §11.3. |
| batch / multi-details endpoint | **Does not exist in v3.** See §16. |
| `include_video_language` on `/3/movie/{id}/videos` | Does not exist on movie videos. It exists only on the three **TV** video endpoints. |

---

## 1. Authentication

### 1.1 The two credentials

Your TMDB account settings page (`https://www.themoviedb.org/settings/api`) issues two things:

| Credential | Format | How to send it | Works on |
|---|---|---|---|
| **API Read Access Token** | long, JWT-shaped string | `Authorization: Bearer <token>` | v3 **and** v4 |
| **API Key** | 32-char hex | `?api_key=<key>` query param | v3 only |

From TMDB's own auth guide: *"Both authentication methods provide the same level of access, and which one
you choose is completely up to you."*

The OpenAPI spec declares **only** the header form:

```json
"components": { "securitySchemes": {
  "sec0": { "type": "apiKey", "in": "header", "name": "Authorization", "x-bearer-format": "bearer" }
} },
"security": [ { "sec0": [] } ]        // applied globally to all 148 paths
```

### 1.2 Recommendation: **use the Bearer token**

- The credential never appears in a URL, so it does not land in web-server access logs, reverse-proxy
  logs, browser history, `Referer` headers, or crash reports. TMDB's docs show every cURL example using
  the header.
- It's the only credential that also works against v4, so it survives a future migration.
- The spec documents it; the query param is documented only in prose.
- "API Read Access Token" is read-only by design — which is exactly right for a browsing UI, and it
  means a leaked UI token cannot write ratings/watchlists on a user's behalf.

Use `api_key` only if you are writing throwaway scripts. Never ship it in browser code — a
`?api_key=` in a frontend URL is a public key leak, and unlike the Bearer token you cannot revoke just
the browser exposure.

**Do not call TMDB directly from the browser.** Put a thin backend/proxy in front of it that injects the
token, so you control caching, rate limiting, and attribution.

```bash
curl --request GET \
     --url 'https://api.themoviedb.org/3/movie/11' \
     --header 'Authorization: Bearer <API_READ_ACCESS_TOKEN>' \
     --header 'accept: application/json'
```

### 1.3 Validating a key

```
GET /3/authentication
```
Returns `{"success":true,"status_code":1,"status_message":"Success."}` when the credential is good.
Docs slug: `authentication-validate-key`. Takes no parameters.

### 1.4 Rate limits

From TMDB's rate-limiting guide, verbatim in substance:

- **The legacy limit is gone.** "As of December 16, 2019, we have disabled the original API rate limiting
  (40 requests every 10 seconds.)"
- **The current limit is deliberately unspecified**: "we do still have some upper limits to help mitigate
  needlessly high bulk scraping. They sit **somewhere in the 40 requests per second range**. This limit
  could change at any time so be respectful of the service we have built and **respect the `429`** if you
  receive one."
- Error **25** → `HTTP 429`, message `"Your request count (#) is over the allowed limit of (40)."`

**Engineering consequence:** there is no quota to read, no negotiation, no SLA (TMDB explicitly
disclaims an SLA and publishes only a best-effort status page). Treat the limit as "unknown, be polite".
Client-side budget of **~10–20 req/s** with a concurrency cap (e.g. 6) and a request queue is a
comfortable operating point. On `429`, back off exponentially with jitter and honour any `Retry-After`
if present. See §18 for headers.

### 1.5 Other hard limits

| Limit | Value | Error |
|---|---|---|
| Pages start at 1, max 500 | 500 | `22` → `HTTP 400` "Invalid page: Pages start at 1 and max at 500." |
| `append_to_response` sub-calls | **20 max** | `27` → `HTTP 400` "Too many append to response objects..." |
| Date range on `*/changes` | **14 days max** | `20` → `HTTP 422` "Invalid date range..." |
| `date` format | `YYYY-MM-DD` | `23` → `HTTP 400` |

Trending endpoints ignore `page` (see §3) and report `total_pages: 1000`; keyword search/keyword-movie
paging goes to 1000. Everything else caps at 500.

### 1.6 Error codes (from TMDB's Errors guide)

`{ "success": false, "status_code": <n>, "status_message": "..." }`

| Code | HTTP | Meaning | | Code | HTTP | Meaning |
|---|---|---|---|---|---|---|
| 1 | 200 | Success | | 24 | 504 | Backend timeout — retry |
| 2 | 501 | Invalid service | | **25** | **429** | **Over request limit (40)** |
| 3 | 401 | Auth failed / no permission | | 26 | 400 | Missing username/password |
| 4 | 405 | Invalid format | | **27** | **400** | **Too many append_to_response (max 20)** |
| 5 | 422 | Invalid parameters | | 28 | 400 | Invalid timezone |
| 6 | 404 | Invalid id | | 29 | 400 | Missing `confirm=true` |
| **7** | **401** | **Invalid API key** | | 30 | 401 | Invalid username/password |
| 8 | 403 | Duplicate entry | | 31 | 401 | Account disabled |
| 9 | 503 | Service offline | | 32 | 401 | Email not verified |
| 10 | 401 | Suspended API key | | 33 | 401 | Invalid request token |
| 11 | 500 | Internal error | | 34 | 404 | Resource not found |
| 12 | 201 | Updated | | 35 | 401 | Invalid token |
| 13 | 200 | Deleted | | 36 | 401 | Token lacks write permission |
| 14 | 401 | Authentication failed | | 37 | 404 | Session not found |
| 15 | 500 | Failed | | 38 | 401 | No edit permission |
| 16 | 401 | Device denied | | 39 | 401 | Resource is private |
| 17 | 401 | Session denied | | 40 | 200 | Nothing to update |
| 18 | 400 | Validation failed | | 41 | 422 | Token not approved by user |
| 19 | 406 | Invalid `accept` header | | 42 | 405 | Method not supported |
| 20 | 422 | Date range > 14 days | | 43 | 502 | Can't reach backend |
| **21** | **200** | **Entry not found (note: HTTP 200!)** | | 44 | 500 | The ID is invalid |
| **22** | **400** | **Invalid page (1..500)** | | 45 | 403 | User suspended |
| **23** | **400** | **Invalid date format** | | 46 | 503 | API under maintenance |
| | | | | 47 | 400 | Input not valid |

**Two traps:** error `21` returns **HTTP 200** with `success: false`, and `34`/`6` return 404. Never treat
"HTTP 2xx" as success — check `success === true`.

### 1.7 JSONP (legacy escape hatch)

Only JSON is supported, but a `callback` query param wraps it for cross-domain `<script>` use:

```
GET /3/search/movie?query=Batman&callback=test
```

Prefer CORS/proxy over JSONP.

---

## 2. Images

### 2.1 URL construction

```
{secure_base_url}{size}{file_path}
```

`GET /3/configuration` returns:

```json
{
  "images": {
    "base_url": "http://image.tmdb.org/t/p/",
    "secure_base_url": "https://image.tmdb.org/t/p/",
    "backdrop_sizes":  ["w300","w780","w1280","original"],
    "logo_sizes":      ["w45","w92","w154","w185","w300","w500","original"],
    "poster_sizes":    ["w92","w154","w185","w342","w500","w780","original"],
    "profile_sizes":   ["w45","w185","h632","original"],
    "still_sizes":     ["w92","w185","w300","original"]
  },
  "change_keys": [ "adult", "air_date", "also_known_as", "alternative_titles", "biography",
                   "birthday", "budget", "cast", "certifications", "character_names",
                   "created_by", "crew", "deathday", "episode", "episode_number",
                   "episode_run_time", "freebase_id", "freebase_mid", "general", "genres",
                   "guest_stars", "homepage", "images", "imdb_id", "languages", "name",
                   "network", "origin_country", "original_name", "original_title", "overview",
                   "parts", "place_of_birth", "plot_keywords", "production_code",
                   "production_companies", "production_countries", "releases", "revenue",
                   "runtime", "season", "season_number", "season_regular", "spoken_languages",
                   "status", "tagline", "title", "translations", "tvdb_id", "tvrage_id",
                   "type", "video", "videos" ]
}
```

Always use `secure_base_url`. Example:

```
https://image.tmdb.org/t/p/w500/1E5baAaEse26fej7uHcjOgEE2t2.jpg
```

`file_path` always begins with `/`. It can be `null` — guard every URL builder.

### 2.2 Complete size tables (verbatim from `/3/configuration`)

| Type | Aspect | Available sizes | Use for |
|---|---|---|---|
| **poster** | 2:3 (0.667) | `w92` `w154` `w185` `w342` `w500` `w780` `original` | grid cards, hero side panel |
| **backdrop** | 16:9 (1.778) | `w300` `w780` `w1280` `original` | hero banner, wide cards |
| **still** | 16:9 | `w92` `w185` `w300` `original` | episode thumbnails |
| **profile** | 2:3 (0.666) | `w45` `w185` `h632` `original` | cast/person rows |
| **logo** | varies, very wide | `w45` `w92` `w154` `w185` `w300` `w500` `original` | title lockups, network/provider marks |

`h632` is the only **height**-based size (a fixed 632px-tall crop) — use it when you need a uniform
row height with unknown source aspect. Every other size is width-based.

### 2.3 `still` vs `backdrop` — pick the right one

- **Episodes** carry `still_path` → use **`still_sizes`**. Aspect 16:9.
- **Seasons, shows, movies** carry `backdrop_path` → use **`backdrop_sizes`**.
- Requesting a `w1280` on a `still_path` yields a hard 400 (size not valid for that image type).
- Posters are 2:3, so never use a poster as a 16:9 hero.

Which field goes where (from the image-languages guide):

- `poster_path` → **the** poster; language-aware lookup.
- `backdrop_path` → language-agnostic (99% of backdrops contain no text).
- `still_path` → language-agnostic, highest-rated.

### 2.4 SVG logos

Company and network logos exist as SVG and PNG. **All `logo_path` fields return `.png`**, for backwards
compatibility, even when the uploaded original was SVG. Image objects from the `/images` endpoints carry
a `file_type` field (documented in the image-basics guide) telling you the original format — but note the
`/images` response **schema in the OpenAPI spec does not declare `file_type`**, so treat it as optional and
code defensively.

SVGs are not resized, so for an SVG you must request **`original`**. If you'd rather have the PNG
rasterisation, any normal size works. Netflix's logo, for example:

```
https://image.tmdb.org/t/p/original/wwemzKWzjKYJFfCeiB57q3r4Bcm.svg
https://image.tmdb.org/t/p/original/wwemzKWzjKYJFfCeiB57q3r4Bcm.png
https://image.tmdb.org/t/p/w500/wwemzKWzjKYJFfCeiB57q3r4Bcm.png
```

### 2.5 Which endpoints have which image arrays

| Endpoint | Arrays |
|---|---|
| `/3/movie/{id}/images` | `posters`, `backdrops`, `logos` |
| `/3/tv/{id}/images` | `posters`, `backdrops`, `logos` |
| `/3/tv/{id}/season/{s}/images` | `posters` **only** |
| `/3/tv/{id}/season/{s}/episode/{e}/images` | `stills` **only** |
| `/3/collection/{id}/images` | `posters`, `backdrops` |
| `/3/person/{id}/images` | `profiles` |
| `/3/person/{id}/tagged_images` | flat `results[]` with `image_type` + embedded `media` |

> **Consequence:** you cannot get a season backdrop or a season logo. Series-level backdrops are the
> closest available. For a season page, the poster (`season.poster_path` / season `posters[]`) is your
> primary art and the series `backdrop_path` is your hero.

Image object shape (all variants share it):

```json
{
  "aspect_ratio": 0.667,
  "height": 900,
  "iso_639_1": "pt",          // or null for untagged
  "file_path": "/r3pPehX4ik8NLYPpbDRAh0YRtMb.jpg",
  "vote_average": 5.258,      // 0–10, community quality rating
  "vote_count": 6,
  "width": 600
}
```

---

## 3. Trending

```
GET /3/trending/{movie|tv|all|person}/{day|week}
```

| Param | In | Required | Type | Default | Enum |
|---|---|---|---|---|---|
| `time_window` | path | **yes** | string | `day` | `day`, `week` |
| `language` | query | no | string | `en-US` | ISO-639-1-ISO-3166-1 |

**There is no `page` parameter.** TMDB returns 20 items and reports `total_results: 20000` /
`total_pages: 1000`, but you cannot request page 2 — the ranked list is simply the first 20. Design your
"Top 10" rails around exactly 20 items and stop there. The spec documents only `200` and `401` responses.

Response envelope: `{ page, results, total_pages, total_results }`.

**`/3/trending/movie/{w}`** result item (standard movie list object + `media_type`):

```json
{
  "adult": false, "backdrop_path": "/44immBwzhDVyjn87b3x3l9mlhAD.jpg",
  "id": 934433, "title": "Scream VI", "original_language": "en",
  "original_title": "Scream VI", "overview": "...", "poster_path": "/wDWwtvkRRlgTiUr6TyLSMX8FCuZ.jpg",
  "media_type": "movie", "genre_ids": [27,9648,53], "popularity": 609.941,
  "release_date": "2023-03-08", "video": false, "vote_average": 7.374, "vote_count": 684
}
```

**`/3/trending/tv/{w}`** result item: `name`, `original_name`, `first_air_date`, `origin_country[]`
instead of `title`/`release_date`.

**`/3/trending/all/{w}`** returns a **mixed** array; every item has `media_type` of `movie` or `tv` and
carries the union of both field sets (`name`/`original_name` are `null`-able/absent on movies).
**Always branch on `media_type`.**

### Trending vs popularity (TMDB's own explanation)

- **Popularity** is a "lifetime" score influenced by: daily votes, daily views, daily favourites, daily
  watchlist adds, release date (movies) / next-or-last episode air date (TV), total votes, and previous
  days' scores.
- **Trending** uses much shorter windows (daily, weekly) and exists specifically "to surface the relevant
  content of today (the new stuff) much easier."

For a Netflix-style home page you generally want **trending/week** as the "Trending Now" rail and
`/3/movie/popular` (or `popularity.desc` discover) for a steadier "Popular" rail.

---

## 4. Discover

TMDB's guidance: use `/search` for text, `/discover` for filters, `/find` for external IDs.

### 4.1 Advanced filtering behaviour (from the discover-movie doc)

**AND / OR logic.** A number of filters accept `,` (AND) or `|` (OR):

| Separator | Meaning | Example |
|---|---|---|
| `,` | **AND** — must match all | `with_genres=80,18` = Crime **AND** Drama |
| `\|` | **OR** — matches any | `with_genres=80\|53` = Crime **OR** Thriller |

**`region` changes which date `release_date.*` filters use.** From the doc: *"If you specify the region
parameter, the regional release date will be used instead of the primary release date. The date returned
will be the first date based on your query (i.e. if a `with_release_type` is specified)."* If you omit
`with_release_type` while using `region`, `region` simply requires ≥1 matching release date in that
country.

**Release type order matters.** `with_release_type=2|3` returns the *limited* theatrical date;
`with_release_type=3|2` returns the *theatrical* date. (Values below.)

### 4.2 `GET /3/discover/movie` — complete parameter list

| Param | Type | Default | Notes |
|---|---|---|---|
| `certification` | string | | use with `region` |
| `certification.gte` | string | | use with `region` |
| `certification.lte` | string | | use with `region` |
| `certification_country` | string | | use with `certification*` |
| `include_adult` | boolean | `false` | |
| `include_video` | boolean | `false` | |
| `language` | string | `en-US` | |
| `page` | integer | `1` | 1..500 |
| `primary_release_year` | integer | | |
| `primary_release_date.gte` | date | `YYYY-MM-DD` | |
| `primary_release_date.lte` | date | `YYYY-MM-DD` | |
| `region` | string | | ISO-3166-1 |
| `release_date.gte` | date | `YYYY-MM-DD` | regional if `region` set |
| `release_date.lte` | date | `YYYY-MM-DD` | regional if `region` set |
| `sort_by` | string | `popularity.desc` | enum below |
| `vote_average.gte` | float | | **0–10 scale** |
| `vote_average.lte` | float | | |
| `vote_count.gte` | float | | docs declare float; pass an integer |
| `vote_count.lte` | float | | |
| `watch_region` | string | | with `with_watch_monetization_types` / `with_watch_providers` |
| `with_cast` | string | | person IDs, `,`/`\|` |
| `with_companies` | string | | company IDs, `,`/`\|` |
| `with_crew` | string | | person IDs, `,`/`\|` |
| `with_genres` | string | | genre IDs, `,`/`\|` |
| `with_keywords` | string | | keyword IDs, `,`/`\|` |
| `with_origin_country` | string | | ISO-3166-1 (documented on movie too) |
| `with_original_language` | string | | ISO-639-1 |
| `with_people` | string | | person IDs, `,`/`\|` |
| `with_release_type` | int | | `1`–`6`, `,`/`\|`, use with `region` |
| `with_runtime.gte` | int | | minutes |
| `with_runtime.lte` | int | | minutes |
| `with_watch_monetization_types` | string | | `flatrate` `free` `ads` `rent` `buy`; use with `watch_region` |
| `with_watch_providers` | string | | provider IDs; use with `watch_region` |
| `without_companies` | string | | |
| `without_genres` | string | | |
| `without_keywords` | string | | |
| `without_watch_providers` | string | | |
| `year` | integer | | primary release year |

**`sort_by` enum (movies) — all 14 values:**

```
original_title.asc  original_title.desc
popularity.asc      popularity.desc        (default)
revenue.asc         revenue.desc
primary_release_date.asc  primary_release_date.desc
title.asc           title.desc
vote_average.asc    vote_average.desc
vote_count.asc      vote_count.desc
```

**Release types:**

| `with_release_type` | Meaning |
|---|---|
| 1 | Premiere |
| 2 | Theatrical (limited) |
| 3 | Theatrical |
| 4 | Digital |
| 5 | Physical |
| 6 | TV |

Result item = standard movie list object (see §16.2) with `genre_ids` instead of `genres`.

### 4.3 `GET /3/discover/tv` — complete parameter list

| Param | Type | Default | Notes |
|---|---|---|---|
| `air_date.gte` | string | | any episode air date in range |
| `air_date.lte` | string | | |
| `first_air_date_year` | int | | |
| `first_air_date.gte` | string | | |
| `first_air_date.lte` | string | | |
| `include_adult` | boolean | `false` | |
| `include_null_first_air_dates` | boolean | `false` | |
| `language` | string | `en-US` | |
| `page` | integer | `1` | |
| `screened_theatrically` | boolean | | include shows that screened in theatres |
| `sort_by` | string | `popularity.desc` | enum below |
| `timezone` | string | | IANA, e.g. `America/New_York` |
| `vote_average.gte` / `.lte` | number | | |
| `vote_count.gte` / `.lte` | number | | |
| `watch_region` | string | | with watch filters |
| `with_companies` | string | | `,`/`\|` |
| `with_genres` | string | | `,`/`\|` |
| `with_keywords` | string | | `,`/`\|` |
| `with_networks` | int | | network IDs; **docs declare integer, not a list** — one network per request |
| `with_origin_country` | string | | ISO-3166-1 |
| `with_original_language` | string | | ISO-639-1 |
| `with_runtime.gte` / `.lte` | int | | |
| `with_status` | string | | `0`–`5` |
| `with_type` | string | | `0`–`6` |
| `with_watch_monetization_types` | string | | with `watch_region` |
| `with_watch_providers` | string | | with `watch_region` |
| `without_companies` / `without_genres` / `without_keywords` / `without_watch_providers` | string | | |

**`sort_by` enum (TV) — 12 values. Note: no `revenue`, no date sort beyond `first_air_date`:**

```
first_air_date.asc  first_air_date.desc
name.asc            name.desc
original_name.asc   original_name.desc
popularity.asc      popularity.desc        (default)
vote_average.asc    vote_average.desc
vote_count.asc      vote_count.desc
```

**TV-specific enum meanings (from the spec descriptions):**

- `with_status`: `0` Returning Series, `1` Planned, `2` In Production, `3` Ended *(TMDB documents the
  range `0–5`; `4`/`5` are not defined publicly — stick to `0`–`3`.)*
- `with_type`: `0` Documentary, `1` News, `2` Miniseries, `3` Reality, `4` Scripted, `5` Talk Show,
  `6` Video *(range documented as `0–6`)*

**Not available on discover/tv:** `revenue`, `primary_release_date.*`, `with_release_type`,
`with_people`, `with_cast`, `with_crew`, `without_*` for cast/crew.

TV result item: `first_air_date`, `name`, `origin_country[]`, `original_name`, `genre_ids[]`, no `adult`
in the documented schema (present in the `airing_today` schema but not `discover/tv` — treat `adult` as
optional everywhere).

### 4.4 Discovering genre and keyword IDs

**Genres** — dedicated endpoints, cache forever:

```
GET /3/genre/movie/list?language=en
GET /3/genre/tv/list?language=en
→ { "genres": [ { "id": 28, "name": "Action" }, ... ] }
```

**Movie genre IDs (19, English):**

| ID | Name | ID | Name | ID | Name |
|---|---|---|---|---|---|
| 28 | Action | 18 | Drama | 9648 | Mystery |
| 12 | Adventure | 10751 | Family | 10749 | Romance |
| 16 | Animation | 14 | Fantasy | 878 | Science Fiction |
| 35 | Comedy | 36 | History | 10770 | TV Movie |
| 80 | Crime | 27 | Horror | 53 | Thriller |
| 99 | Documentary | 10402 | Music | 10752 | War |
| 37 | Western | | | | |

**TV genre IDs (16, English):**

| ID | Name | ID | Name | ID | Name |
|---|---|---|---|---|---|
| 10759 | Action & Adventure | 10751 | Family | 10766 | Soap |
| 16 | Animation | 10762 | Kids | 10767 | Talk |
| 35 | Comedy | 9648 | Mystery | 10768 | War & Politics |
| 80 | Crime | 10763 | News | 37 | Western |
| 99 | Documentary | 10764 | Reality | | |
| 18 | Drama | 10765 | Sci-Fi & Fantasy | | |

> Hard-code these tables if you want zero latency on your genre rails. Note the IDs are **stable** but the
> `name` strings are **translated** — fetch `/3/genre/*/list?language=<ui-locale>` if you localise labels,
> and cache per locale.

**Keywords** — no list endpoint in the spec. Discover them three ways:

1. `GET /3/search/keyword?query=<text>&page=1` → `{page, results:[{id,name}], total_pages, total_results}`
   (has no `include_adult`/`language` params)
2. `GET /3/keyword/{keyword_id}` → `{id, name}`
3. `GET /3/keyword/{keyword_id}/movies?page=1&include_adult=false&language=en-US` →
   `{id, page, results:[movie list items], total_pages, total_results}` (page max 1000)
4. Bulk: `https://files.tmdb.org/p/exports/keyword_ids_MM_DD_YYYY.json.gz` (no auth; NDJSON-per-line,
   gzipped; files are **deleted after 3 months**; regenerated daily ~07:00 UTC, ready by 08:00 UTC)

**Resolve keyword IDs at runtime** rather than hard-coding them, e.g.:

```ts
const kw = await tmdb('/3/search/keyword?query=' + encodeURIComponent('anime'));
const animeId = kw.results[0].id;
```

**Company IDs:** `GET /3/search/company?query=<text>&page=1` → `{page, results:[{id,name,logo_path,origin_country}], ...}`;
`GET /3/company/{id}` → `{description, headquarters, homepage, id, logo_path, name, origin_country, parent_company}`;
`GET /3/company/{id}/images`, `GET /3/company/{id}/alternative_names`.
Bulk: `https://files.tmdb.org/p/exports/production_company_ids_MM_DD_YYYY.json.gz`.

**Collection IDs:** `GET /3/search/collection?query=...`; bulk:
`https://files.tmdb.org/p/exports/collection_ids_MM_DD_YYYY.json.gz`.

Other daily exports (all under `https://files.tmdb.org/p/exports/`, no auth, 3-month retention):
`movie_ids_*`, `tv_series_ids_*`, `person_ids_*`, `collection_ids_*`, `tv_network_ids_*`,
`keyword_ids_*`, `production_company_ids_*`, plus `adult_movie_ids_*`, `adult_tv_series_ids_*`,
`adult_person_ids_*`. They contain the valid ID set plus `adult`, `video`, and `popularity` — which makes
them a good offline seed and a good way to bulk-filter adult titles client-side.

---

## 5. Movie details

### 5.1 `GET /3/movie/{movie_id}`

Path `movie_id` (int, required). Query: `language` (default `en-US`), `append_to_response`.

```json
{
  "adult": false,
  "backdrop_path": "/2w4xG178RpB4MDAIfTkqAuSJzec.jpg",
  "belongs_to_collection": { "id": 10, "name": "Star Wars Collection",
    "poster_path": "/pWVLFh4OuejTpUaDQbB1C4zoS2p.jpg",
    "backdrop_path": "/iY2ujEY2m68OTTlPFTiHub9joHS.jpg" },
  "budget": 11000000,
  "genres": [ { "id": 12, "name": "Adventure" }, { "id": 28, "name": "Action" },
              { "id": 878, "name": "Science Fiction" } ],
  "homepage": "http://www.starwars.com/films/star-wars-episode-iv-a-new-hope",
  "id": 11,
  "imdb_id": "tt0076759",
  "origin_country": ["US"],
  "original_language": "en",
  "original_title": "Star Wars",
  "overview": "Princess Leia is captured and held hostage ...",
  "popularity": 20.6912,
  "poster_path": "/6FfCtAuVAW8XJjZ7eWeLibRLWTw.jpg",
  "production_companies": [ { "id": 1, "logo_path": "/tlVSws0RvvtPBwViUyOFAO0vcQS.png",
    "name": "Lucasfilm Ltd.", "origin_country": "US" } ],
  "production_countries": [ { "iso_3166_1": "US", "name": "United States of America" } ],
  "release_date": "1977-05-25",
  "revenue": 775398007,
  "runtime": 121,
  "spoken_languages": [ { "english_name": "English", "iso_639_1": "en", "name": "English" } ],
  "status": "Released",
  "tagline": "A long time ago in a galaxy far, far away...",
  "title": "Star Wars",
  "video": false,
  "vote_average": 8.2,
  "vote_count": 22061
}
```

> `origin_country` on a **movie** is unusual and usually absent — it's a TV field. Don't rely on it.

### 5.2 `append_to_response` for movies

```
GET /3/movie/550?append_to_response=credits,videos,images,recommendations,similar,
                 release_dates,watch/providers,external_ids,keywords
```

Each value is a **sub-resource of the movie namespace**; each becomes a top-level key in the response.
Valid values (15 — every GET sub-resource of `/3/movie/{id}` except the write endpoints):

```
account_states   alternative_titles   changes   credits       external_ids
images           keywords             lists     release_dates recommendations
reviews          similar              translations          videos
watch/providers
```

**Invalid / non-existent:** `collection` (no such endpoint), `latest`, `now_playing`, `popular`.

`account_states` requires a user `session_id` and is useless for a public UI. `POST`/`DELETE
/3/movie/{id}/rating` cannot be appended.

`watch/providers` contains a slash and **does work** in `append_to_response` — URL-encode the comma
list carefully and don't accidentally split on the slash.

Max **20** appended sub-calls (error `27`). Each append is a separate internal remote call, so a 20-way
append is ~21× the work — don't do it on list/detail-page prefetches, only on the title page.

### 5.3 `credits` — `GET /3/movie/{id}/credits`

Params: `language` (default `en-US`).

```json
{
  "id": 550,
  "cast": [{
    "adult": false, "gender": 2, "id": 819,
    "known_for_department": "Acting",
    "name": "Edward Norton", "original_name": "Edward Norton",
    "popularity": 26.99,
    "profile_path": "/8nytsqL59SFJTVYVrN72k6qkGgJ.jpg",
    "cast_id": 4, "character": "The Narrator",
    "credit_id": "52fe4250c3a36847f80149f3", "order": 0
  }],
  "crew": [{
    "adult": false, "gender": 2, "id": 376,
    "known_for_department": "Production",
    "name": "Arnon Milchan", "original_name": "Arnon Milchan",
    "popularity": 2.931,
    "profile_path": "/b2hBExX4NnczNAnLuTBF4kmNhZm.jpg",
    "credit_id": "55731b8192514111610027d7",
    "department": "Production", "job": "Executive Producer"
  }]
}
```

- `cast[]` is already ordered by billing (`order`, 0 = top-billed). **Don't re-sort** unless you want
  alphabetical.
- `gender`: `0` unknown, `1` female, `2` male, `3` non-binary.
- `cast` has `cast_id` + `character`; `crew` has `department` + `job`. They are different shapes —
  write two TypeScript types.
- `cast[].profile_path` → `profile_sizes`.

### 5.4 `videos` — `GET /3/movie/{id}/videos`

Params: `language` (default `en-US`). **No `include_video_language` on movies** (TV only).

```json
{
  "id": 550,
  "results": [{
    "iso_639_1": "en",
    "iso_3166_1": "US",
    "name": "Fight Club (1999) Trailer - Starring Brad Pitt, Edward Norton, Helena…",
    "key": "O-b2VfmmbyA",
    "site": "YouTube",
    "size": 720,
    "type": "Trailer",
    "official": false,
    "published_at": "2016-03-05T02:03:14.000Z",
    "id": "639d5326be6d88007f170f44"
  }]
}
```

**Trailer selection algorithm** (this is the important part):

```ts
const RANK = { Trailer: 0, Teaser: 1, Clip: 2, Recap: 3,
               'Behind the Scenes': 4, Featurette: 5, Bloopers: 6,
               'Opening Credits': 7 };

function pickTrailer(videos) {
  const list = videos.results ?? [];
  if (!list.length) return null;
  // 1. official first, 2. prefer Trailer, 3. then highest quality
  return list
    .filter(v => v.site === 'YouTube')
    .sort((a, b) =>
      Number(b.official) - Number(a.official) ||
      (RANK[a.type] ?? 99) - (RANK[b.type] ?? 99) ||
      b.size - a.size ||
      b.published_at.localeCompare(a.published_at)
    )[0] ?? null;
}
// https://www.youtube.com/watch?v={key}
```

Notes:
- `type` is a **free-form string** in the OpenAPI schema (not an enum) — values observed in the wild
  include `Trailer`, `Teaser`, `Clip`, `Featurette`, `Behind the Scenes`, `Bloopers`, `Recap`,
  `Opening Credits`. Match defensively; treat unknown types as lowest priority.
- `site` is `YouTube` for essentially everything; historically `Vimeo` also appears. Key on `site`
  before building a URL, and fall back to a plain search if it's neither.
- `size` is the source resolution: `360` `480` `720` `1080`. Useful for quality badges.
- `official` distinguishes studio trailers from fan uploads — strongly prefer `official: true`.
- For a hero "Play" button, `site === 'YouTube'` → `https://www.youtube.com/watch?v=${key}` (or
  `https://www.youtube.com/embed/${key}` for an in-page lightbox).

### 5.5 `images` — `GET /3/movie/{id}/images`

Params: `language`, `include_image_language`.

```json
{
  "backdrops": [ { "aspect_ratio": 1.778, "height": 800, "iso_639_1": null,
                   "file_path": "/hZkgoQYus5vegHoetLkCJzb17zJ.jpg",
                   "vote_average": 5.622, "vote_count": 20, "width": 1422 } ],
  "id": 550,
  "logos":     [ { "aspect_ratio": 5.203, "height": 79, "iso_639_1": "he",
                   "file_path": "/c1KLulrIhUqY5fT42nmC5aERGCp.png",
                   "vote_average": 5.312, "vote_count": 1, "width": 411 } ],
  "posters":   [ { "aspect_ratio": 0.667, "height": 900, "iso_639_1": "pt",
                   "file_path": "/r3pPehX4ik8NLYPpbDRAh0YRtMb.jpg",
                   "vote_average": 5.258, "vote_count": 6, "width": 600 } ]
}
```

Arrays are **not** pre-sorted — you must rank them yourself (see §19). Real counts for Fight Club:
`backdrops: 41`, `logos: 28`, `posters: 81`. These arrays are large; don't ship them to a list view.

### 5.6 `watch/providers` — `GET /3/movie/{id}/watch/providers`

No parameters. Response is keyed by **ISO-3166-1 region code** at the top level:

```json
{
  "id": 550,
  "results": {
    "US": {
      "link": "https://www.themoviedb.org/movie/550-fight-club/watch?locale=US",
      "rent":     [ { "logo_path": "/5NyLm42TmCqCMOZFvH4fcoSNKEW.jpg", "provider_id": 10,
                      "provider_name": "Amazon Video", "display_priority": 13 } ],
      "flatrate": [ { "logo_path": "/jPXksae158ukMLFhhlNvzsvaEyt.jpg", "provider_id": 257,
                      "provider_name": "fuboTV", "display_priority": 5 } ],
      "buy":      [ { "logo_path": "/peURlLlr8jggOwK53fJ5wdQl05y.jpg", "provider_id": 2,
                      "provider_name": "Apple TV", "display_priority": 4 } ]
    },
    "GB": { "link": "…", "flatrate": [ … ] },
    "…":  { }
  }
}
```

- The **five monetization buckets**, all optional and often absent: **`flatrate`** (subscription),
  **`free`**, **`ads`**, **`rent`**, **`buy`**. Never assume a key exists.
- Provider object: `{ logo_path, provider_id, provider_name, display_priority }`. `display_priority` is
  TMDB's global ranking for that provider (lower = more prominent) — **sort ascending** to match
  TMDB's own "JustWatch" ordering.
- Regions present in the Fight Club response: AE AL AR AT AU BA BB BE BG BH BO BR BS CA CH CL CO CR CV CZ
  DE DK DO EC EE EG ES FI FJ FR GB GF GI GR GT HK HN HR HU ID IE IL IN IQ IS IT JM JO JP KR KW LB LI LT
  LV MD MK MT MU MX MY MZ NL NO NZ OM PA PE PH PK PL PS PT PY QA RO RS RU SA SE SG SI SK SM SV TH TR TT
  TW UG US UY VE YE ZA. Your region is simply the key you want; a missing key means "no data", which is
  not the same as "unavailable".
- `link` is a TMDB affiliate/JustWatch redirect to the TMDB watch page — link out, don't scrape the
  destination. TMDB requires you to use their link.
- `logo_path` is a **logo** → use `logo_sizes` (typically `w92` in a pill, `w154`/`w185` in a grid).
- Response is chunky (a Fight Club payload covers ~90 regions). For a UI pinned to one market, extract
  `results[MY_REGION]` at your proxy and drop the rest.

### 5.7 Other movie sub-resources

| Append | Shape |
|---|---|
| `release_dates` | `{ id, results: [ { iso_3166_1, release_dates: [ { certification, descriptors:[], iso_639_1, note, release_date (ISO datetime), type (1–6) } ] } ] }` |
| `external_ids` | `{ id, imdb_id, wikidata_id, facebook_id, instagram_id, twitter_id }` — others are `null`, not absent |
| `keywords` | `{ id, keywords: [ { id, name } ] }` — **key is `keywords`** |
| `recommendations` | `{ page, results:[movie], total_pages, total_results }` — may be **empty** |
| `similar` | `{ page, results:[movie], total_pages, total_results }` |
| `reviews` | `{ id, page, results:[ { author, author_details:{name,username,avatar_path,rating}, content, created_at, id, updated_at, url } ], total_pages, total_results }` |
| `alternative_titles` | `{ id, titles: [ { iso_3166_1, title, type } ] }`; query param `country` (ISO-3166-1) |
| `translations` | per-language title/tagline/overview; use for a true "also known as" list |
| `lists` | `{ id, page, results:[ { description, favorite_count, id, item_count, iso_639_1, list_type, name, poster_path } ], total_pages, total_results }` |
| `changes` | see §14.4 |

---

## 6. TV details

### 6.1 `GET /3/tv/{series_id}`

Path `series_id` (int, required). Query: `language` (default `en-US`), `append_to_response`.

```json
{
  "adult": false,
  "backdrop_path": "/6LWy0jvMpmjoS9fojNgHIKoWL05.jpg",
  "created_by": [ { "id": 9813, "credit_id": "5256c8c219c2956ff604858a",
                    "name": "David Benioff", "gender": 2,
                    "profile_path": "/xvNN5huL0X8yJ7h3IZfGG4O2zBD.jpg" } ],
  "episode_run_time": [60],
  "first_air_date": "2011-04-17",
  "genres": [ { "id": 10765, "name": "Sci-Fi & Fantasy" } ],
  "homepage": "http://www.hbo.com/game-of-thrones",
  "id": 1399,
  "in_production": false,
  "languages": ["en"],
  "last_air_date": "2019-05-19",
  "last_episode_to_air": {
    "id": 1551830, "name": "The Iron Throne", "overview": "…",
    "vote_average": 4.809, "vote_count": 241, "air_date": "2019-05-19",
    "episode_number": 6, "production_code": "806", "runtime": 80,
    "season_number": 8, "show_id": 1399, "still_path": "/zBi2O5EJfgTS6Ae0HdAYLm9o2nf.jpg"
  },
  "name": "Game of Thrones",
  "next_episode_to_air": null,
  "networks": [ { "id": 49, "logo_path": "/tuomPhY2UtuPTqqFnKMVHvSb724.png",
                  "name": "HBO", "origin_country": "US" } ],
  "number_of_episodes": 73,
  "number_of_seasons": 8,
  "origin_country": ["GB"],
  "original_language": "en",
  "original_name": "Game of Thrones",
  "overview": "Seven noble families fight for control …",
  "popularity": 346.098,
  "poster_path": "/1XS1oqL89opfnbLl8WnZY1O1uJx.jpg",
  "production_companies": [ { "id": 76043, "logo_path": "/9RO2vbQ67otPrBLXCaC8UMp3Qat.png",
                              "name": "Revolution Sun Studios", "origin_country": "US" } ],
  "production_countries": [ { "iso_3166_1": "GB", "name": "United Kingdom" } ],
  "seasons": [ { "air_date": "2010-12-05", "episode_count": 272, "id": 3627,
                 "name": "Specials", "overview": "", 
                 "poster_path": "/kMTcwNRfFKCZ0O2OaBZS0nZ2AIe.jpg",
                 "season_number": 0, "vote_average": 0 } ],
  "spoken_languages": [ { "english_name": "English", "iso_639_1": "en", "name": "English" } ],
  "status": "Ended",
  "tagline": "Winter Is Coming",
  "type": "Scripted",
  "vote_average": 8.438,
  "vote_count": 21390
}
```

Notes for a season-selector UI:
- `seasons[]` includes **season 0 = Specials**. **Hide season 0** unless the show has meaningful
  specials — it is usually the largest and least interesting season (GoT: 272 of 345 episodes).
- `seasons[].overview` is `""` for specials; `vote_average: 0` there too — don't render a 0.0 badge.
- `episode_run_time` is an **array** (`[60]`) because it varies per episode; always take `[0]` and
  handle `null`/`[]`.
- `status`: `Returning Series`, `Ended`, `Canceled`, `In Production`, `Planned`.
- `type`: `Scripted`, `Reality`, `Documentary`, `Miniseries`, `News`, `Talk Show`, `Video`.
- `networks[].logo_path` is a **logo** → `logo_sizes`.
- `next_episode_to_air` / `last_episode_to_air` are `null` for finished shows. Both have the exact
  episode shape (see §8).

### 6.2 `append_to_response` for TV

```
GET /3/tv/1399?append_to_response=credits,aggregate_credits,content_ratings,external_ids,
                  images,keywords,episode_groups,recommendations,similar,screened_theatrically,
                  videos,watch/providers
```

Valid values (all GET sub-resources of `/3/tv/{id}` except write endpoints):

```
account_states   aggregate_credits   alternative_titles   changes        content_ratings
credits          episode_groups      external_ids         images         keywords
lists            recommendations     reviews              screened_theatrically
similar          translations        videos               watch/providers
```

**Invalid / non-existent:** `season` (no such endpoint — season summaries are inline), `next_episode_to_air`
(already a top-level field), `airing_today`, `on_the_air`.

### 6.3 `credits` vs `aggregate_credits` — use aggregate for series

**`credits` (`/3/tv/{id}/credits`, params `language`)** — the doc is explicit: *"Get the **latest season**
credits of a TV show."* Same item shape as movie credits but **no `cast_id`/`order` on `crew`** and cast
has `character` + `credit_id` + `order`:

```json
{ "id": 1399,
  "cast": [ { "adult": false, "gender": 2, "id": 22970, "known_for_department": "Acting",
              "name": "Peter Dinklage", "original_name": "Peter Dinklage", "popularity": 30.6,
              "profile_path": "/lRsRgnksAhBRXwAB68MFjmTtLrk.jpg",
              "character": "Tyrion Lannister", "credit_id": "5256c8b219c2956ff6047cd8",
              "order": 0 } ],
  "crew": [ { "adult": false, "gender": 2, "id": 1406855, "known_for_department": "Production",
              "name": "Duncan Muggoch", "original_name": "Duncan Muggoch", "popularity": 1.592,
              "profile_path": "/ukGjJ62Ejd4cFziald03G34Fsrp.png",
              "credit_id": "5ceab029c3a3682e93217a85",
              "department": "Production", "job": "Producer" } ] }
```

**`aggregate_credits` (`/3/tv/{id}/aggregate_credits`, params `language`)** — **this is the one you want
for a series page.** Rolls a person up once, with every role they played across the whole run:

```json
{ "id": 1399,
  "cast": [{
    "adult": false, "gender": 1, "id": 1223786, "known_for_department": "Acting",
    "name": "Emilia Clarke", "original_name": "Emilia Clarke", "popularity": 42.737,
    "profile_path": "/u59kTmNHXzaGZqokivxLPiBVIML.jpg",
    "roles": [ { "credit_id": "5256c8af19c2956ff60479f6",
                 "character": "Daenerys Targaryen", "episode_count": 78 } ],
    "total_episode_count": 78,
    "order": 6
  }],
  "crew": [{
    "adult": false, "gender": 1, "id": 6411, "known_for_department": "Art",
    "name": "Deborah Riley", "original_name": "Deborah Riley", "popularity": 1.4,
    "profile_path": "/cjhADpqdrnwB1PdDUKaBnWrIj2Q.jpg",
    "jobs": [ { "credit_id": "54eee9e5c3a3686d5800584e",
                "job": "Production Design", "episode_count": 43 } ],
    "department": "Art",
    "total_episode_count": 43
  }] }
```

Key differences: `roles[]` (cast) and `jobs[]` (crew) instead of scalar `character`/`job`; plus
`total_episode_count`. Sort cast by `order`; you can show `total_episode_count` as "78 eps".

### 6.4 `content_ratings` — `GET /3/tv/{id}/content_ratings`

```json
{ "id": 1399,
  "results": [ { "descriptors": [], "iso_3166_1": "DE", "rating": "16" },
               { "descriptors": [], "iso_3166_1": "US", "rating": "TV-MA" } ] }
```

`descriptors` is for jurisdictions that use descriptive ratings instead of letters (e.g. some
European systems). Look up your region; fall back to `US`.

### 6.5 `keywords` — `GET /3/tv/{id}/keywords`

```json
{ "id": 1399, "results": [ { "name": "based on novel or book", "id": 818 } ] }
```

> **Gotcha: the TV endpoint returns `results`, the movie endpoint returns `keywords`.** Same content,
> different key. Normalise it.

### 6.6 `screened_theatrically` — `GET /3/tv/{id}/screened_theatrically`

```json
{ "id": 1399, "results": [ { "id": 1159054, "episode_number": 10, "season_number": 5 } ] }
```

Theatre-released episodes of an otherwise TV show (documentaries, finales, anime OVAs). Pair with
`/3/movie/{id}` using the episode `id` to get a full movie-like record with a `backdrop_path`.

### 6.7 `episode_groups` — `GET /3/tv/{id}/episode_groups`

```json
{ "id": 1399,
  "results": [ { "description": "", "episode_count": 102, "group_count": 9,
                 "id": "5e9077d2e640d600151f32bd", "name": "Aired Order",
                 "network": { "id": 49, "logo_path": "/tuomPhY2UtuPTqqFnKMVHvSb724.png",
                              "name": "HBO", "origin_country": "US" },
                 "type": 1 } ] }
```

`id` is a **string**, and `type` is an **integer** — unlike every other TMDB id. `network` is
nullable (anthologies).

`GET /3/tv/episode_group/{tv_episode_group_id}`:

```json
{ "description": "Comedians in Cars organized in Netflix's collections.",
  "episode_count": 83, "group_count": 6,
  "groups": [ { "id": "5acf93efc3a368739a0000a9", "name": "First Cup", "order": 1,
                "episodes": [ { "air_date": "2015-06-17", "episode_number": 3, "id": 1078262,
                                "name": "Jim Carrey: We Love Breathing …", "overview": "…",
                                "production_code": "", "runtime": null, "season_number": 6,
                                "show_id": 59717, "still_path": "/aOyE420zuFq9zWtEWjIccAiTrzU.jpg",
                                "vote_average": 7.4, "vote_count": 5, "order": 0 } ],
                "locked": true } ],
  "id": "5acf93e60e0a26346d0000ce", "name": "Netflix Collections",
  "network": { "id": 213, "logo_path": "/wwemzKWzjKYJFfCeiB57q3r4Bcm.png",
               "name": "Netflix", "origin_country": "" },
  "type": 4 }
```

`groups[].episodes[]` is a **flattened cross-season episode list** with a local `order` — this is exactly
the "seasons in a different order" feature (Netflix's own collections use it; the example above is
literally Netflix). For a "Recommended order" or "Franchise order" episode rail, use this.

`type` integer values are **not documented in the spec**. Community consensus: `1` original air order,
`2` DVD order, `3` absolute/numeric, `4` production order, `5` TV/broadcaster order. `name` is the safe
field to display ("Aired Order", "DVD Order", "Absolute Order", "Production Order", "Netflix
Collections"). Present `name`; don't build UI on the integer.

### 6.8 `external_ids` — `GET /3/tv/{id}/external_ids`

```json
{ "id": 1399, "imdb_id": "tt0944947", "freebase_mid": "/m/0524b41",
  "freebase_id": "/en/game_of_thrones", "tvdb_id": 121361, "tvrage_id": 24493,
  "wikidata_id": "Q23572", "facebook_id": "GameOfThrones",
  "instagram_id": "gameofthrones", "twitter_id": "GameOfThrones" }
```

`freebase_*` are dead services — ignore them. `imdb_id`, `tvdb_id` and `wikidata_id` are the useful ones.
The **movie** variant is a smaller object (no `tvdb_id`/`tvrage_id`/`freebase_*`).

### 6.9 `images` — `GET /3/tv/{id}/images`

Params: `language`, `include_image_language`. Same shape as movie images:
`{ backdrops: [...], id, logos: [...], posters: [...] }`.

### 6.10 `videos` — `GET /3/tv/{id}/videos`

Params: **`include_video_language`** (*"filter the list results by language, supports more than one value
by using a comma"*) and `language`. This is the **only** place `include_video_language` is available
besides season and episode videos.

```json
{ "id": 1399, "results": [ { "iso_639_1": "en", "iso_3166_1": "US",
  "name": "Inside Game of Thrones: A Story in Camera Work – BTS (HBO)",
  "key": "y2ZJ3lTaREY", "site": "YouTube", "size": 1080,
  "type": "Behind the Scenes", "official": true,
  "published_at": "2019-03-25T14:00:06.000Z", "id": "5c999b48c3a36863b73b9d42" } ] }
```

Use `include_video_language=en,null` to catch untagged videos when your UI language is English.

### 6.11 `changes` — see §14.4

### 6.12 Reverse lookup

- Network: `GET /3/network/{network_id}` →
  `{ headquarters, homepage, id, logo_path, name, origin_country }`
- `GET /3/network/{id}/images`, `GET /3/network/{id}/alternative_names`
- `discover/tv?with_networks={id}` (single integer per request)
- `discover/movie?with_companies={id}` (list-capable, `,`/`|`)

---

## 7. Season details

### 7.1 `GET /3/tv/{series_id}/season/{season_number}`

All three path params required (`series_id`, `season_number`). `season_number` is an **integer index**,
not the `seasons[].id` — use `0` for Specials.

Query: `language` (default `en-US`), `append_to_response`.

```json
{
  "_id": "5256c89f19c2956ff6046d47",
  "air_date": "2011-04-17",
  "episodes": [ {
    "air_date": "2011-04-17",
    "episode_number": 1,
    "episode_type": "standard",
    "id": 63056,
    "name": "Winter Is Coming",
    "overview": "Jon Arryn, the Hand of the King, is dead …",
    "production_code": "101",
    "runtime": 62,
    "season_number": 1,
    "show_id": 1399,
    "still_path": "/9hGF3WUkBf7cSjMg0cdMDHJkByd.jpg",
    "vote_average": 8.1,
    "vote_count": 396,
    "crew": [ /* full crew objects, same shape as episode detail crew */ ],
    "guest_stars": [ /* full cast objects */ ]
  } ],
  "name": "Season 1",
  "networks": [ { "id": 49, "logo_path": "/tuomPhY2UtuPTqqFnKMVHvSb724.png",
                  "name": "HBO", "origin_country": "US" } ],
  "overview": "Trouble is brewing in the Seven Kingdoms of Westeros …",
  "id": 3624,
  "poster_path": "/wgfKiqzuMrFIkU1M68DDDY8kGC1.jpg",
  "season_number": 1,
  "vote_average": 8.4
}
```

Episode object fields, all present as you need them:

| Field | Type | Notes |
|---|---|---|
| `episode_number` | int | **display order within the season** (can differ from production order) |
| `season_number` | int | |
| `still_path` | string\|null | → **`still_sizes`**, 16:9 |
| `air_date` | string | `YYYY-MM-DD`; can be `""` for unaired |
| `vote_average` | number | 0–10 |
| `vote_count` | int | |
| `runtime` | int\|null | minutes; `null` is common |
| `show_id` | int | useful for self-contained episode records |
| `id` | int | episode id (not the same namespace as `show_id`) |
| `name`, `overview` | string | |
| `production_code` | string | often `""` for modern shows |
| `episode_type` | string | `standard`, `finale`, `mid_season`, etc. |
| `crew` | array | populated, full objects |
| `guest_stars` | array | populated, full objects |

- `episodes[].crew` and `.guest_stars` are **populated in this response** (not just on the episode
  detail endpoint) — a season call gives you everything for an episode-grid UI in one request.
- There is **no `next_episode_to_air`/`last_episode_to_air` here**, and no season `backdrop_path`.
- `_id` is a legacy Mongo id; use the numeric `id`.
- `vote_average` on the season is the mean of its episodes.
- `air_date` can be `""` for unaired/announced episodes — sort with a null-safe comparator and label
  them "TBA".

### 7.2 `append_to_response` for seasons

```
GET /3/tv/1399/season/1?append_to_response=credits,aggregate_credits,images,videos,external_ids,watch/providers
```

Valid values (all GET sub-resources of the season path):

```
account_states   aggregate_credits   credits   external_ids   images
translations     videos              watch/providers
```

**Not appendable:** `episodes` (already inline), `poster_path`.

---

## 8. Episode details

### 8.1 `GET /3/tv/{series_id}/season/{season_number}/episode/{episode_number}`

All three path params required. Query: `language` (default `en-US`), `append_to_response`.

```json
{
  "air_date": "2011-04-17",
  "crew": [ { "department": "Directing", "job": "Director",
              "credit_id": "5256c8a219c2956ff6046e77",
              "adult": false, "gender": 2, "id": 44797,
              "known_for_department": "Directing",
              "name": "Timothy Van Patten", "original_name": "Timothy Van Patten",
              "popularity": 7.775, "profile_path": "/MzSOFrd99HRdr6pkSRSctk3kBR.jpg" } ],
  "episode_number": 1,
  "guest_stars": [ { "character": "Benjen Stark", "credit_id": "5256c8b919c2956ff604836a",
                     "order": 62, "adult": false, "gender": 2, "id": 119783,
                     "known_for_department": "Acting", "name": "Joseph Mawle",
                     "original_name": "Joseph Mawle", "popularity": 6.758,
                     "profile_path": "/1Ocb9v3h54beGVoJMm4w50UQhLf.jpg" } ],
  "name": "Winter Is Coming",
  "overview": "Jon Arryn, the Hand of the King, is dead …",
  "id": 63056,
  "production_code": "101",
  "runtime": 62,
  "season_number": 1,
  "still_path": "/9hGF3WUkBf7cSjMg0cdMDHJkByd.jpg",
  "vote_average": 7.8,
  "vote_count": 286
}
```

- `guest_stars[].order` is billing order within this episode.
- **No `media_type`, no `poster_path`** — an episode is not addressable as a movie.
- `append_to_response=credits,images,videos` as the brief says is valid, but note the endpoint is
  already named `.../credits`, so the appended `credits` key is the same data. Valid episode appends:
  `account_states`, `credits`, `external_ids`, `images`, `translations`, `videos`.

### 8.2 Episode videos & images

- `GET /3/tv/{id}/season/{s}/episode/{e}/videos` — params `language`, `include_video_language`
- `GET /3/tv/{id}/season/{s}/episode/{e}/images` — params `language`, `include_image_language`;
  returns **`stills` only**: `{ id, stills: [ image objects ] }`
- `GET /3/tv/{id}/season/{s}/images` — returns **`posters` only**: `{ id, posters: [ … ] }`
- `GET /3/tv/{id}/season/{s}/external_ids`, `.../translations`, `.../credits`,
  `.../aggregate_credits`, `.../watch/providers`

---

## 9. Search

### 9.1 Endpoints and parameters

| Endpoint | Params | Notes |
|---|---|---|
| `GET /3/search/movie` | `query`*, `include_adult`=`false`, `language`=`en-US`, `primary_release_year`, `page`=`1`, `region`, `year` | original + translated + alternative titles |
| `GET /3/search/tv` | `query`*, `first_air_date_year` (1000–9999), `include_adult`=`false`, `language`=`en-US`, `page`=`1`, `year` (1000–9999) | `year` searches first air date **and all episode air dates**; `first_air_date_year` only the first air date |
| `GET /3/search/person` | `query`*, `include_adult`=`false`, `language`=`en-US`, `page`=`1` | name + also-known-as |
| `GET /3/search/multi` | `query`*, `include_adult`=`false`, `language`=`en-US`, `page`=`1` | movies + TV + people in one call |
| `GET /3/search/keyword` | `query`*, `page`=`1` | **no** `include_adult`/`language` |
| `GET /3/search/company` | `query`*, `page`=`1` | **no** `include_adult`/`language` |
| `GET /3/search/collection` | `query`*, `include_adult`=`false`, `language`=`en-US`, `page`=`1`, `region` | original + translated + alternative names |

`*` = required. Envelope for all: `{ page, results, total_pages, total_results }`.

### 9.2 Result shapes

**`search/movie`** — standard movie list object, exactly like discover:
`adult, backdrop_path, genre_ids[], id, original_language, original_title, overview, popularity, poster_path, release_date, title, video, vote_average, vote_count`

**`search/tv`:** `adult, backdrop_path, genre_ids[], id, origin_country[], original_language, original_name, overview, popularity, poster_path, first_air_date, name, vote_average, vote_count`

**`search/person`:** `adult, gender, id, known_for_department, name, original_name, popularity, profile_path, known_for[]`
(`known_for[]` holds full mini media objects — a free "person's best-known works" row.)

**`search/multi`** — a **union**, disambiguated by `media_type`:

```json
{ "adult": false, "backdrop_path": "/aDYSnJAK0BTA…", "id": 11,
  "title": "Star Wars", "original_language": "en", "original_title": "Star Wars",
  "overview": "Princess Leia is captured …", "poster_path": "/6FfCtAuVAW8XJjZ7eWeLibRLWTw.jpg",
  "media_type": "movie", "genre_ids": [12,28,878], "popularity": 78.047,
  "release_date": "1977-05-25", "video": false, "vote_average": 8.208, "vote_count": 18528,
  "name": null, "original_name": null }
```

The documented schema carries both `title`/`release_date` (movie) and `name`/`original_name` (TV), and
`media_type` is the only reliable discriminator. **Person results also appear in multi** in practice;
they carry `profile_path` and `known_for_department` instead of `poster_path`. Branch defensively:

```ts
switch (item.media_type) {
  case 'movie': /* title, release_date, poster_path, genre_ids */ break;
  case 'tv':    /* name, first_air_date, poster_path, origin_country */ break;
  case 'person':/* profile_path, known_for_department, known_for */ break;
}
```

`/3/search/multi` does **not** return collections.

### 9.3 The `region` parameter on search

It's a **presentation filter**, not a filter: *"In the event that we don't have a release date entered
for the country you are searching for, we simply default back to the primary release date like always.
This is the same as entering no region parameter."*

```
GET /3/search/movie?query=Whiplash&language=de-DE&region=DE
```

### 9.4 Autocomplete / suggestions — there is none

**Verified against the 148-path spec: TMDB v3 has no suggestion or autocomplete endpoint.**
`/3/search/{type}/suggestions` does not exist. TMDB's own website typeahead uses an internal endpoint
that is not part of the public API.

What to do instead:

1. **Debounced `/3/search/multi`** with a 2–3 character minimum and ~250 ms debounce. This is what
   virtually every TMDB client does.
2. **Prefer narrow endpoints when you know the intent:** `/3/search/keyword` for a keyword picker,
   `/3/search/person` for a cast picker, `/3/search/collection` for a franchise picker.
3. **Cache aggressively client-side.** There is no minimum query length enforced by the API, so a
   one-character query returns a huge, useless result set — the guard is on your side.
4. If you already have a title, `/3/find/{external_id}?external_source=imdb_id` is the fastest path
   (see §16.3).

---

## 10. Collections

### 10.1 `GET /3/collection/{collection_id}`

Path `collection_id` (int, required). Query: `language` (default `en-US`).

```json
{ "id": 10, "name": "Star Wars Collection", "original_language": "en",
  "original_name": "Star Wars Collection",
  "overview": "An epic space-opera theatrical film series …",
  "poster_path": "/22dj38IckjzEEUZwN1tPU5VJ1qq.jpg",
  "backdrop_path": "/4z9ijhgEthfRHShoOvMaBlpciXS.jpg",
  "parts": [ {
    "adult": false, "backdrop_path": "/2w4xG178RpB4MDAIfTkqAuSJzec.jpg", "id": 11,
    "name": "Star Wars", "original_name": "Star Wars", "overview": "Princess Leia …",
    "poster_path": "/6FfCtAuVAW8XJjZ7eWeLibRLWTw.jpg",
    "media_type": "movie", "original_language": "en", "genre_ids": [12,28,878],
    "popularity": 15.8557, "release_date": "1977-05-25", "video": false,
    "vote_average": 8.205, "vote_count": 21522
  } ] }
```

**`parts[]` uses `name`/`original_name`, not `title`/`original_title`,** and carries
`media_type: "movie"`. That is the opposite of a movie list object. Collections are **movies only** —
there are no TV parts. Re-order `parts` by `release_date` yourself (TMDB's order is franchise order,
which is usually right, but chronology is what a "collection rail" wants).

Entry point to this endpoint from a movie: `movie.belongs_to_collection.id`.

### 10.2 `GET /3/collection/{collection_id}/images`

Query: `language`, `include_image_language`. Returns **`posters` and `backdrops` only** (no `logos`):

```json
{ "id": 10,
  "backdrops": [ { "aspect_ratio": 1.778, "height": 1080, "iso_639_1": null,
                   "file_path": "/d8duYyyC9J5T825Hg7grmaabfxQ.jpg",
                   "vote_average": 5.464, "vote_count": 30, "width": 1920 } ],
  "posters":   [ { "aspect_ratio": 0.667, "height": 3000, "iso_639_1": "en",
                   "file_path": "/r8Ph5MYXL04Qzu4QBbq2KjqwtkQ.jpg",
                   "vote_average": 5.516, "vote_count": 14, "width": 2000 } ] }
```

### 10.3 `GET /3/collection/{collection_id}/translations`

Per-language `title`, `overview`, `homepage`. Query: no `language` needed.

### 10.4 `GET /3/collection/list`

**Not in the official spec.** Long-standing and widely used, returns a paginated list of every
collection, but undocumented and unsupported. See §11.3 for the recommended substitutes.

---

## 11. Genres, companies, keywords

### 11.1 Genres

`GET /3/genre/movie/list?language=en` and `GET /3/genre/tv/list?language=en` → `{ genres: [{id, name}] }`.
Full ID tables in §4.4. Cache forever (only `name` changes with locale).

### 11.2 Keywords

| Endpoint | Purpose |
|---|---|
| `GET /3/keyword/{keyword_id}` | `{ id, name }` |
| `GET /3/keyword/{keyword_id}/movies?page=1&include_adult=false&language=en-US` | paginated movie list (page max 1000) |
| `GET /3/search/keyword?query=&page=1` | find ids by name |
| `https://files.tmdb.org/p/exports/keyword_ids_MM_DD_YYYY.json.gz` | full id dump, no auth |

There is **no** keyword-equivalent for TV items other than via `discover/tv?with_keywords=` and
`/3/tv/{id}/keywords`.

### 11.3 `/3/collection/list`, `/3/company/list`, `/3/keyword/list` — undocumented

I checked all three against the authoritative 148-path spec: **none of them are in it.** They are also
absent from the docs reference navigation. They are, however, long-standing endpoints that the live API
still answers, and they're extremely common in existing codebases (browsing all keywords, all
collections, all companies).

**Recommendation:** don't build a hard dependency on them. Use:

| Need | Documented substitute |
|---|---|
| enumerate keywords | `GET /3/search/keyword?query=<letter or term>` per term, plus the daily `keyword_ids_*.json.gz` export |
| look up a company | `GET /3/search/company?query=`, `GET /3/company/{id}`; bulk via `production_company_ids_*.json.gz` |
| enumerate collections | `GET /3/search/collection?query=`, plus `collection_ids_*.json.gz` |
| discover a movie's franchise | `movie.belongs_to_collection` → `GET /3/collection/{id}` |

If you do want the list endpoints, wrap them behind a feature flag with a fallback, and treat a non-200
as "unsupported" rather than as an error.

---

## 12. Watch providers

### 12.1 Per-title

| Endpoint | Response |
|---|---|
| `GET /3/movie/{movie_id}/watch/providers` | `{ id, results: { "<ISO-3166-1>": { link, buy?, rent?, flatrate?, ads?, free? } } }` |
| `GET /3/tv/{series_id}/watch/providers` | same shape, per **series** |
| `GET /3/tv/{series_id}/season/{season_number}/watch/providers` | same shape, per **season** |

No parameters on any of the three. Full structure and a worked `US` example in §5.6. The `link` values
differ by type:

```
movie: https://www.themoviedb.org/movie/550-fight-club/watch?locale=US
tv:    https://www.themoviedb.org/tv/1399-game-of-thrones/watch?locale=US
```

### 12.2 Provider and region catalogues

```
GET /3/watch/providers/movie?language=en-US&watch_region=US
GET /3/watch/providers/tv?language=en-US&watch_region=US
→ { "results": [ { "display_priorities": { "US": 4, "GB": 5, "CA": 6, … },   // ISO → rank
                   "display_priority": 2,                                     // default/global rank
                   "logo_path": "/peURlLlr8jggOwK53fJ5wdQl05y.jpg",
                   "provider_name": "Apple TV",
                   "provider_id": 2 } ] }
```

`display_priorities` gives the per-region ranking; fall back to the scalar `display_priority` when your
region isn't in the map. `provider_id` is what you feed to `discover?with_watch_providers=`.

```
GET /3/watch/providers/regions?language=en-US
→ { "results": [ { "iso_3166_1": "AD", "english_name": "Andorra", "native_name": "Andorra" }, … ] }
```

120 regions. Use this to populate a country selector and to validate `watch_region` / `region` input.

### 12.3 Discovering by provider

```
GET /3/discover/movie?watch_region=US&with_watch_providers=8&with_watch_monetization_types=flatrate
GET /3/discover/tv?watch_region=US&with_watch_providers=213&with_watch_monetization_types=flatrate
```

`with_watch_monetization_types` ∈ `flatrate` `free` `ads` `rent` `buy` (comma = AND, pipe = OR).
`without_watch_providers` negates. This is how you build a "Streaming on Netflix" / "Streaming on
Maximo" rail without hard-coding provider ids — resolve them from `/3/watch/providers/{movie,tv}` at
startup and cache.

### 12.4 Attribution requirement

These endpoints drive revenue to TMDB's watch partners. TMDB's terms require using the provided `link`
and displaying TMDB attribution (see §21). If you build a "where to watch" panel, link the provider
name to `link` and keep the TMDB logo/notice in your About/Credits.

---

## 13. Certifications

### 13.1 `GET /3/certification/movie/list`

**No parameters.** 45 regions:
`AU BG BR CA CA-QC DE DK ES FI FR GB HU IN IT LT MY NL NO NZ PH PT RU SE US KR SK TH MX ID TR AR GR TW ZA SG IE PR JP VI CH IL HK MO LV LU`

```json
{ "certifications": {
  "US": [
    { "certification": "G",      "order": 1, "meaning": "All ages admitted. There is no content that would be objectionable to most parents. …" },
    { "certification": "PG",     "order": 2, "meaning": "Some material may not be suitable for children under 10. …" },
    { "certification": "PG-13",  "order": 3, "meaning": "Some material may be inappropriate for children under 13. …" },
    { "certification": "R",      "order": 4, "meaning": "Under 17 requires accompanying parent or adult guardian 21 or older. …" },
    { "certification": "NC-17",  "order": 5, "meaning": "These films contain excessive graphic violence …" },
    { "certification": "NR",     "order": 0, "meaning": "No rating information." }
  ],
  "GB": [ { "certification": "U", … }, { "certification": "PG", … }, { "certification": "12", … },
          { "certification": "12A", … }, { "certification": "15", … }, { "certification": "18", … },
          { "certification": "R18", … } ],
  "DE": [ … ]
} }
```

`order` is the severity ranking (**`NR` is `order: 0`, i.e. lowest**) — use it to sort or to compare
thresholds, never the lexicographic order of the `certification` string (`"R"` vs `"NC-17"` vs `"PG-13"`
sorts wrongly as text).

### 13.2 `GET /3/certification/tv/list`

**No parameters.** 40 regions:
`AU BR CA CA-QC DE ES FR GB HU KR LT NL PH PT RU SK TH US IT FI MY NZ NO BG MX IN DK SE ID TR AR PL MA GR IL TW ZA SG PR VI`

```json
{ "certifications": { "US": [
  { "certification": "TV-Y",   "order": 1, "meaning": "This program is designed to be appropriate for all children." },
  { "certification": "TV-Y7",  "order": 2, "meaning": "This program is designed for children age 7 and above." },
  { "certification": "TV-G",   "order": 3, "meaning": "Most parents would find this program suitable for all ages." },
  { "certification": "TV-PG",  "order": 4, "meaning": "This program contains material that parents may find unsuitable for younger children." },
  { "certification": "TV-14",  "order": 5, "meaning": "This program contains some material that many parents would find unsuitable for children under 14…" },
  { "certification": "TV-MA",  "order": 6, "meaning": "This program is specifically designed to be viewed by adults …" },
  { "certification": "NR",     "order": 0, "meaning": "No rating information." }
] } }
```

### 13.3 Using certifications in discover

```
GET /3/discover/movie?region=US&certification=PG-13
GET /3/discover/movie?region=US&certification.gte=PG&certification.lte=R
GET /3/discover/movie?certification_country=US&certification.gte=PG
```

`certification*` **requires a region context** — use `region` (movie) or `certification_country`.

### 13.4 Getting a title's ratings

- **Movie:** `GET /3/movie/{id}/release_dates` → find `results[].iso_3166_1 === 'US'` →
  `release_dates[].certification`.
- **TV:** `GET /3/tv/{id}/content_ratings` → `results[].iso_3166_1 === 'US'` → `rating`.

Both are big-ish appends; for a card badge use your own cached certification table + a single append on
the title page.

---

## 14. Browse lists, changes, and other list endpoints

### 14.1 Curated browse lists

| Endpoint | Params | Notes |
|---|---|---|
| `GET /3/movie/now_playing` | `language`, `page`, `region` | also returns `dates: { maximum, minimum }` |
| `GET /3/movie/popular` | `language`, `page`, `region` | |
| `GET /3/movie/upcoming` | `language`, `page`, `region` | |
| `GET /3/movie/top_rated` | `language`, `page` | |
| `GET /3/tv/airing_today` | `language`, `page`, `timezone` | shows airing today, not new shows |
| **`GET /3/tv/on_the_air`** | `language`, `page`, `timezone` | **this is TV's `now_playing`** — next 7 days |
| `GET /3/tv/popular` | `language`, `page` | |
| `GET /3/tv/top_rated` | `language`, `page` | |
| `GET /3/person/popular` | `language`, `page` | |

Envelope: `{ page, results, total_pages, total_results }` (+ `dates` on `now_playing`).
`now_playing` is region-aware — always pass `region`.

> `/3/tv/now_playing` **does not exist** and will 401/404. Use `/3/tv/on_the_air`.

### 14.2 Latest & changes feeds

| Endpoint | Notes |
|---|---|
| `GET /3/movie/latest` | newest movie added to TMDB |
| `GET /3/tv/latest` | newest TV series |
| `GET /3/person/latest` | newest person |
| `GET /3/movie/changes` | paginated list of recently **changed** movies (browse-level feed) |
| `GET /3/tv/changes` | same for TV |
| `GET /3/person/changes` | same for people |

The `/changes` *browse* endpoints return an `id` per item so you can then call the per-title changes
endpoint. Useful for a "recently added" rail and for background cache warming.

### 14.3 Reviews & lists

- `GET /3/movie/{id}/reviews?language=&page=` — `{ id, page, results[], total_pages, total_results }`;
  `results[]` = `{ author, author_details: { name, username, avatar_path, rating }, content,
  created_at, id, updated_at, url }`
- `GET /3/tv/{id}/reviews` — same shape
- `GET /3/review/{review_id}` — `{ id, author, author_details, content, created_at, updated_at, url }`
- `GET /3/movie/{id}/lists`, `GET /3/tv/{id}/lists` — user lists containing the title

### 14.4 Per-title changes — `GET /3/movie/{id}/changes`, `/3/tv/{id}/changes`, `/3/person/{id}/changes`

Params: `start_date`, `end_date` (**max 14-day range**, error `20`), `page`.

```json
{ "changes": [ { "key": "images",
  "items": [ { "id": "640435cf021cee0084710972", "action": "updated",
               "time": "2023-03-05 06:25:19 UTC",
               "iso_639_1": "en", "iso_3166_1": "",
               "value":         { "poster": { "file_path": "/ouudK6RCNnsbT1CSXrlATXQIQTG.jpg", "iso_639_1": "en" } },
               "original_value":{ "poster": { "file_path": "/ouudK6RCNnsbT1CSXrlATXQIQTG.jpg", "iso_639_1": "fr" } } },
             { "id": "640472b9e61e6d0086e02342", "action": "added",
               "time": "2023-03-05 10:45:13 UTC", "iso_639_1": "", "iso_3166_1": "",
               "value": { "poster": { "file_path": "/YC9R1jhQMS4xAf0VhGHrCwDOYw.jpg" } } } ] } ] }
```

`action` ∈ `added` `updated` `deleted`. `time` is `"YYYY-MM-DD HH:MM:SS UTC"` (note the space, not `T`).
`original_value` is absent on `added`. `value`'s inner shape is polymorphic — it depends on `key` and
whether the field is an image, a scalar, or a nested object. **Treat `value` as `unknown` and narrow
defensively**; don't write a strict discriminated union for it.

Other change endpoints (by the sub-resource's own id, not the parent path):
`GET /3/tv/season/{season_id}/changes`, `GET /3/tv/episode/{episode_id}/changes`.

### 14.5 Network, credit, find

```
GET /3/network/{network_id}            → { headquarters, homepage, id, logo_path, name, origin_country }
GET /3/network/{network_id}/images
GET /3/network/{network_id}/alternative_names

GET /3/credit/{credit_id}              → { credit_type, department, job, media, media_type, id, person }
```
`credit_type` ∈ `cast` `crew`. `media` is a full media object. This is how you resolve a `credit_id`
(from `credits`, `cast[].credit_id`) to a title.

```
GET /3/find/{external_id}?external_source=imdb_id&language=en-US
```
`external_source` enum: `imdb_id`, `facebook_id`, `instagram_id`, `tvdb_id`, `tiktok_id`,
`twitter_id`, `wikidata_id`, `youtube_id`. Returns `{ movie_results, person_results, tv_results,
tv_episode_results }` — some arrays may be absent. This is the best route from an IMDb/TMDB ID you
already have to TMDB ids, and the fastest way to seed a detail page without a search.

### 14.6 Other configuration endpoints

```
GET /3/configuration                     → image sizes (§2) + change_keys
GET /3/configuration/languages           → [ { iso_639_1, english_name, name } ]  (187 entries)
GET /3/configuration/primary_translations→ [ "af-ZA", "ar-AE", "ar-SA", "be-BY", … ]
GET /3/configuration/countries           → [ { iso_3166_1, english_name, native_name } ]
GET /3/configuration/timezones           → [ { iso_3166_1, zones: ["Europe/Andorra", …] } ]  (249 countries)
GET /3/configuration/jobs                → department/job strings
```

`primary_translations` is the list of `xx-YY` values TMDB treats as first-class UI locales — use it to
populate a language picker instead of inventing the set.

---

## 15. Person

### 15.1 `GET /3/person/{person_id}`

Path `person_id` (int, required). Query: `language` (default `en-US`), `append_to_response`.

```json
{ "adult": false,
  "also_known_as": ["Thomas Jeffrey Hanks", "…"],
  "biography": "Thomas Jeffrey Hanks (born July 9, 1956) is an American actor and fil…",
  "birthday": "1956-07-09",
  "deathday": null,
  "gender": 2,
  "homepage": null,
  "id": 31,
  "imdb_id": "nm0000158",
  "known_for_department": "Acting",
  "name": "Tom Hanks",
  "place_of_birth": "Concord, California, USA",
  "popularity": 82.989,
  "profile_path": "/xndWFsBlClOJFRdhSt4NBwiPq2o.jpg" }
```

`homepage` and `deathday` are `null` for living people without a homepage — nullable, not optional.
`also_known_as` is the alias list (useful for a "search by other name" affordance).
`gender`: `0` unknown, `1` female, `2` male, `3` non-binary.

### 15.2 `append_to_response` for people

```
GET /3/person/31?append_to_response=combined_credits,movie_credits,tv_credits,images,external_ids,tagged_images,translations
```

Valid values: `changes`, `combined_credits`, `external_ids`, `images`, `movie_credits`,
`tagged_images`, `translations`, `tv_credits`.

### 15.3 Credits

All three return `{ id, cast: [...], crew: [...] }`.

**`combined_credits`** — movies **and** TV merged, each item with a `media_type` discriminator:

```json
{ "id": 31,
  "cast": [ { "adult": false, "backdrop_path": "/3h1JZGDhZ8nzxdgvkxha0qBqi05.jpg",
              "genre_ids": [18], "id": 13, "original_language": "en",
              "original_title": "Forrest Gump", "overview": "A man with a low IQ …",
              "popularity": 62.225, "poster_path": "/arw2vcBveWOVZr6pxd9XTd1TdQa.jpg",
              "release_date": "1994-06-23", "title": "Forrest Gump", "video": false,
              "vote_average": 8.481, "vote_count": 24535,
              "character": "Forrest Gump", "credit_id": "52fe420ec3a36847f800074f",
              "order": 0, "media_type": "movie" } ],
  "crew": [ { "…movie fields…", "credit_id": "5d818a63d34eb3002c4f8fea",
              "department": "Crew", "job": "Thanks", "media_type": "movie" } ] }
```

- Again a **union**: `title`/`release_date` for `media_type: "movie"`, `name`/`first_air_date` for
  `media_type: "tv"`.
- `character` on cast, `department`+`job` on crew.
- Real sizes: Tom Hanks `combined_credits` = 217 cast + 69 crew; `movie_credits` = 150 cast + 41 crew.
  These arrays are **huge** and unpaginated — do not put them on a page that renders eagerly. Virtualise,
  or fetch and page client-side.

**`movie_credits`** — movies only; identical item shape minus `media_type`.

**`tv_credits`** — TV only; items are the TV list object **plus** credit fields:

```json
{ "adult": false, "backdrop_path": "/ttvojTMgaINU8gqB5LlNqO4vPN.jpg",
  "genre_ids": [10767], "id": 1900, "origin_country": ["US"], "original_language": "en",
  "original_name": "LIVE with Kelly and Mark", "overview": "…", "popularity": 700.508,
  "poster_path": "/l5y8egG27p2fSTyq8s21SQMmQLy.jpg", "first_air_date": "1988-09-05",
  "name": "LIVE with Kelly and Mark", "vote_average": 5.4, "vote_count": 25,
  "character": "", "credit_id": "52571af019c29571140d5c92", "episode_count": 1 }
```

Note `episode_count` (per-episode, here) vs `total_episode_count` (in `aggregate_credits`).

### 15.4 `images` — `GET /3/person/{id}/images`

No `language`/`include_image_language` params documented.

```json
{ "id": 287,
  "profiles": [ { "aspect_ratio": 0.666, "height": 980, "iso_639_1": null,
                  "file_path": "/cckcYc2v0yh1tc9QjRelptcOBko.jpg",
                  "vote_average": 5.288, "vote_count": 89, "width": 653 } ] }
```

Use `profile_sizes` — `w185` for a cast row, `h632` for a uniform-height grid, `w45` for tiny avatars.
`person.profile_path` (on the person object) is already the best-rated profile.

### 15.5 `tagged_images` — `GET /3/person/{id}/tagged_images`

```json
{ "id": 31, "page": 1, "total_pages": 3, "total_results": 55,
  "results": [ { "aspect_ratio": 0.6666, "file_path": "/1wY4psJ5NVEhCuOYROwLH2XExM2.jpg",
                 "height": 1500, "id": "5b235d740e0a265b5d0031d9", "iso_639_1": "en",
                 "vote_average": 5.456, "vote_count": 7, "width": 1000,
                 "image_type": "poster",
                 "media": { "adult": false, "backdrop_path": "/bdD39MpSVhKjxarTxLSfX6baoMP.jpg",
                            "id": 857, "title": "Saving Private Ryan", "…": "…" } } ] }
```

`image_type` ∈ `poster` `backdrop` `profile` `logo` `still`. `media` is the title the person appears in —
`media_type` is not always present, so infer it from the field names inside `media` (`title` vs `name`).

---

## 16. Multi / batch details — verified answer

### 16.1 There is **no** batch or multi-details endpoint in TMDB API v3

I checked the full 148-path OpenAPI spec. Searching for any batch-style or bare-collection path:

- No `/3/movie/batch`, no `/3/tv/batch`, no `/3/multi`, no `/3/details`, no `/3/movie` or `/3/tv` without
  an id.
- The only path matching `multi` is **`/3/search/multi`**, which is *search*, not details.
- `POST` is used only for: `account/{id}/favorite`, `account/{id}/watchlist`, `authentication/session*`,
  `authentication/token/validate_with_login`, `list`, `list/{id}/add_item|clear|remove_item`,
  `movie/{id}/rating`, `tv/{id}/rating`, `tv/{id}/season/{s}/episode/{e}/rating`. **None are batch reads.**

**Implication for a Netflix-style UI:** if you need details for 20 rail items, you either already have
them (list endpoints return everything a card needs — see §16.2) or you make 20 individual
requests. Lean on the list payloads; that's the design intent.

### 16.2 The "standard list object" you get from every list endpoint

Movies (`/3/movie/*` lists, `discover/movie`, `search/movie`, `trending/movie`, `trending/all` movies,
`keyword/{id}/movies`, `collection.parts` variants, `person.movie_credits`):

```
adult, backdrop_path, genre_ids[], id, original_language, original_title,
overview, popularity, poster_path, release_date, title, video,
vote_average, vote_count
```

TV (`/3/tv/*` lists, `discover/tv`, `search/tv`, `trending/tv`, `trending/all` tvs, `person.tv_credits`):

```
adult, backdrop_path, first_air_date, genre_ids[], id, name, origin_country[],
original_language, original_name, overview, popularity, poster_path,
vote_average, vote_count
```

**This is enough to render a complete rail card**: poster, backdrop, title, date, rating, genres, id.
Only the *detail* page needs a per-title call.

### 16.3 The one shortcut that exists: `/3/find`

If you have an external id, `GET /3/find/{external_id}?external_source=imdb_id` returns
`movie_results` + `tv_results` + `person_results` + `tv_episode_results` in one call. Still search-driven,
not a general batch.

### 16.4 Normalising to one UI type

Because field names differ by source, normalise once at your proxy boundary:

```ts
type MediaKind = 'movie' | 'tv' | 'person';

interface Card {
  id: number;
  kind: Exclude<MediaKind, 'person'>;
  mediaType: 'movie' | 'tv';
  title: string;              // title | name | collection.parts[].name
  originalTitle: string;      // original_title | original_name
  overview: string | null;
  posterPath: string | null;
  backdropPath: string | null;
  genreIds: number[];
  releaseDate: string | null; // release_date | first_air_date
  originCountry: string[];    // tv only
  popularity: number;
  voteAverage: number;
  voteCount: number;
  adult: boolean;
}

export function toCard(raw: any): Card | null {
  const kind =
    raw.media_type ??
    (raw.title !== undefined || raw.release_date !== undefined ? 'movie'
      : raw.name !== undefined || raw.first_air_date !== undefined ? 'tv'
      : raw.profile_path !== undefined && raw.known_for_department !== undefined ? 'person'
      : null);
  if (kind === 'person' || kind === null) return null;
  return {
    id: raw.id,
    kind,
    mediaType: kind,
    title: raw.title ?? raw.name ?? '',
    originalTitle: raw.original_title ?? raw.original_name ?? '',
    overview: raw.overview ?? null,
    posterPath: raw.poster_path ?? null,
    backdropPath: raw.backdrop_path ?? null,
    genreIds: raw.genre_ids ?? (raw.genres ?? []).map((g: any) => g.id),
    releaseDate: raw.release_date ?? raw.first_air_date ?? null,
    originCountry: raw.origin_country ?? [],
    popularity: raw.popularity ?? 0,
    voteAverage: raw.vote_average ?? 0,
    voteCount: raw.vote_count ?? 0,
    adult: raw.adult ?? false,
  };
}
```

Then build image URLs centrally:

```ts
const IMG = 'https://image.tmdb.org/t/p/';
const size = {
  poster:   { card: 'w342', hero: 'w780', detail: 'w500' },
  backdrop: { card: 'w780', hero: 'w1280' },
  still:    { card: 'w300' },
  profile:  { tiny: 'w45', row: 'w185', grid: 'h632' },
  logo:     { pill: 'w92', card: 'w154' },
} as const;

type ImgKind = keyof typeof size;
export const img = (path: string | null | undefined, kind: ImgKind, preset: string) =>
  path ? `${IMG}${size[kind][preset]}${path}` : null;
```

---

## 17. Language, region, translation

### 17.1 The `language` parameter

Format: **`ISO-639-1`-`ISO-3166-1`**, e.g. `en-US`, `pt-BR`, `de-DE`, `fr-CA`, `ja-JP`.

- ISO 639-1 is the language part. TMDB notes: *"Unfortunately, there are a number of languages that don't
  have a ISO-639-1 representation. We may decide to upgrade to ISO-639-3 in the future but do not have
  any immediate plans to do so."*
- ISO 3166-1 alpha-2 is the region part. Region is a **presentation variant selector**; the underlying
  translation is shared, so `en-US` and `en-GB` differ mainly in regional wording/spelling where TMDB has
  it.
- Most list/detail endpoints default to `en-US`.
- **Two localisation gaps**, stated by TMDB: **person names and character names are not translated.**
  Don't expect a localised cast list.
- The bare `en-US` example the docs use:
  `GET /3/tv/1399?language=en-US` and `GET /3/movie/popular?language=pt-BR`.

Available codes: `GET /3/configuration/languages` → 187 entries of
`{ iso_639_1, english_name, name }` (187 languages, so 187 × 3166 region combos is not a thing — the
`language` param wants a *pair*, and the practical set is `primary_translations`).

### 17.2 The `region` parameter

ISO-3166-1 alpha-2, no dash. Two distinct meanings — don't conflate them:

| Context | Meaning |
|---|---|
| `search/*`, `discover/movie`, `movie/popular`, `movie/upcoming`, `movie/now_playing` | selects which **release date** to show/filter on, and (with `with_release_type`) which release type |
| `watch_region` on `discover/*` + `with_watch_providers` | which market's **streaming availability** to filter on |
| `certification_country` on `discover/movie` | which **rating system** to apply to `certification*` |

`region` on search is presentation-only; see §9.3. Valid regions: `/3/watch/providers/regions` (120) for
streaming, `/3/configuration/countries` for everything else.

### 17.3 `include_image_language`

Available **only on the `/images` endpoints**: `/3/movie/{id}/images`, `/3/tv/{id}/images`,
`/3/tv/{id}/season/{s}/images`, `/3/tv/{id}/season/{s}/episode/{e}/images`,
`/3/collection/{id}/images`.

- Comma-separated list of **ISO-639-1** values. The literal string **`null`** means "images with no
  language tag".
- Your `language` param **filters** the `/images` response, so you almost always need
  `include_image_language` to get anything else.

```
GET /3/movie/550/images?language=en-US&include_image_language=en,null
GET /3/movie/550?append_to_response=images&language=en-US&include_image_language=en-US,null
```

The image-languages guide's rules, which explain why `poster_path` behaves the way it does:

- **`poster_path`**: "will query the language you specify in your query first and default back to the
  highest rated image of the media's 'original language' if it's present. If that image doesn't exist,
  it simply falls back to the highest rated." Regional variants (e.g. `en-US` vs `en-GB`) are **not
  supported for images** at the time of writing.
- **`backdrop_path`**: "Since 99% of backdrops don't contain a language, the default lookup … is simply
  to query for the highest rated backdrop with no language. If that doesn't exist, then we return the
  overall highest rated."
- **`still_path`**: "Like backdrops, TV episode images don't inherently have languages. We query for the
  highest rated."

**Practical consequence:** `poster_path` is language-aware; `backdrop_path` and `still_path` are not.
You do not need an extra call to get a good backdrop — `backdrop_path` already is the best one.

### 17.4 `include_video_language`

Available **only on the three TV video endpoints**:

```
GET /3/tv/{id}/videos?include_video_language=en,null&language=en-US
GET /3/tv/{id}/season/{s}/videos?include_video_language=en,null
GET /3/tv/{id}/season/{s}/episode/{e}/videos?include_video_language=en,null
```

*"Filter the list results by language, supports more than one value by using a comma."* Supports `null`
for untagged. **It does not exist on `/3/movie/{id}/videos`** — for movies, filter `results[].iso_639_1`
yourself.

### 17.5 Translation endpoints

Per-title full translation lists, if you want a true "available in" / "also known as" feature:

```
GET /3/movie/{id}/translations
GET /3/tv/{id}/translations
GET /3/collection/{id}/translations
GET /3/tv/{id}/season/{s}/translations
GET /3/tv/{id}/season/{s}/episode/{e}/translations
GET /3/person/{id}/translations
```

These take **no `language` param** — they return everything at once, which is why they're appendable but
never auto-localised.

---

## 18. Rate limit headers, caching headers, and error handling

### 18.1 Rate limit headers: **none**

I inspected live responses from `https://api.themoviedb.org`. A successful v3 GET returns only:

```
server: openresty
cache-control: public, max-age=<seconds>
etag: "…"
age: <seconds>            (only when the CDN has it cached)
vary: Origin
x-cache / via / x-amz-cf-*   (CloudFront plumbing)
```

**There is no `X-RateLimit-Limit`, no `X-RateLimit-Remaining`, no `X-RateLimit-Reset`, and no
`Retry-After` in the documented or observed behaviour.** The OpenAPI spec documents only two response
codes across all 148 paths: `200` and `401`.

Consequences you must design around:

1. You cannot pre-emptively throttle — there is nothing to read. Client-side budget is mandatory.
2. You cannot tell a shared/quota-limited key apart from a global limit.
3. Detect throttling **only** from `HTTP 429` (error code `25`) and back off.

### 18.2 Cache headers — these you *can* use

Every GET carries `Cache-Control: public, max-age=N` plus a weak `ETag`. Observed `max-age` values vary
by resource (order of minutes to hours; trending is short, `movie/{id}` and `search/*` are long).
Because these are CDN headers on a public URL, your proxy can safely honour them:

- Store responses keyed by full URL.
- Serve stale on upstream failure (TMDB returns `503`/`504`/`502` transiently — errors `9`, `24`, `43`).
- Send `If-None-Match` and handle `304` to make repeat calls nearly free.

Practical cache tiers:

| Data | Cache for | Invalidate via |
|---|---|---|
| `/3/configuration*`, `/3/genre/*/list`, `/3/certification/*/list` | forever | deploy-time refresh |
| rail snapshots (trending, popular, now_playing, on_the_air) | 10–60 min | TTL; optionally `/changes` feeds |
| `discover/*` results | 1–6 h, keyed by full param set | TTL |
| `search/*` | hours (long `max-age`) | TTL |
| `movie/{id}`, `tv/{id}` (+ appends) | hours, keyed by id + `language` + append set | `/changes` or TTL |
| `watch/providers` | 6–24 h | TTL — provider data changes slowly |
| images | CDN-cached forever at the image host | immutable; cache by URL |

Cache on the **complete URL including `language`**, or you will serve the wrong locale. And because
`append_to_response` changes the payload, include the append list in your cache key.

### 18.3 Error handling checklist

```ts
async function tmdbGet(path: string, params: Record<string, string|number|boolean> = {}) {
  const qs = new URLSearchParams(
    Object.entries(params).filter(([, v]) => v !== undefined && v !== '')
  ).toString();
  const url = `https://api.themoviedb.org${path}${qs ? '?' + qs : ''}`;

  for (let attempt = 0; ; attempt++) {
    const res = await fetch(url, {
      headers: { Authorization: `Bearer ${TOKEN}`, accept: 'application/json' },
    });

    if (res.status === 429) {                       // error 25 — unknown limit
      await sleep(500 * 2 ** attempt + Math.random() * 250);
      continue;
    }
    if ([502, 503, 504].includes(res.status)) {     // errors 43, 9, 24 — transient
      await sleep(300 * 2 ** attempt);
      continue;
    }
    if (!res.ok) throw new TmdbError(res.status, path, await res.text());

    const body = await res.json();
    // error 21 arrives with HTTP 200!
    if (body?.success === false) throw new TmdbError(body.status_code, path, body.status_message);
    return body;
  }
}
```

- Retry `429`, `502`, `503`, `504` with exponential backoff + jitter, capped attempts.
- Do **not** retry `401` (fix your credential), `404` (fix your id) or `400` (fix your request).
- `400` codes `22` (page), `23` (date), `27` (too many appends), `28` (timezone), `47` (input) are
  *your* bugs — they should never happen in production and should be loud in dev.
- **`success === false` must be checked on every 200.**

---

## 19. Image selection heuristics

### 19.1 Rules of thumb

| Slot | Source | Size | Why |
|---|---|---|---|
| Rail / grid card poster | `poster_path` from the list object | **`w342`** | The canonical grid size; 2:3; sharp on 1× and 2× small cards |
| Large poster (title-page side panel) | `poster_path` | `w500` | |
| Hero side panel / featured rail | `poster_path` | `w780` | |
| Hero backdrop (16:9 banner) | `backdrop_path` from the list object | **`w1280`** | Already the best-rated backdrop; TMDB has no language filtering here |
| Wide card | `backdrop_path` | `w780` | |
| Small backdrop chip | `backdrop_path` | `w300` | |
| Episode thumbnail | `still_path` | **`w300`** | 16:9 still; use `w185` in dense lists, `original` for a click-to-expand lightbox |
| Cast row avatar | `profile_path` / `profiles[]` | **`w185`** | |
| Uniform-height cast grid | `profile_path` | **`h632`** | Fixed height crop — only size that guarantees uniform rows |
| Tiny avatar | `profile_path` | `w45` | |
| Title logo lockup | `logos[]` from `/images` | `w500` | Logos are tiny in source; `w500` is the max sensible |
| Provider pill | watch-provider `logo_path` | `w92` | |
| Network mark | `networks[].logo_path` | `w92` / `w154` | |

**`poster_path` and `backdrop_path` from a list object are already the best available image** (§17.3).
For 95% of a browsing UI you never need the `/images` endpoint at all. Call it only for:
a hero that must avoid text-heavy/late-release posters, a curated "gallery" rail, or a language-locked
title lockup.

### 19.2 Ranking an `/images` array

The arrays are unsorted. Rank like this:

```ts
function pickBackdrop(images: TmdbImage[], minAspect = 1.6) {
  return images.backdrops
    .filter(b => b.iso_639_1 === null)            // untagged = no text = safest for a hero
    .filter(b => b.aspect_ratio >= minAspect)     // avoid pillarboxed 4:3 in a 16:9 slot
    .filter(b => b.vote_count >= 3)              // avoid 1-vote noise
    .sort((a, b) =>
      b.vote_average - a.vote_average || b.vote_count - a.vote_count || b.width - a.width
    )[0] ?? null;
}

function pickPoster(images: TmdbImage[], lang: string) {
  const rank = (p: TmdbImage) =>
    (p.iso_639_1 === lang ? 0 : p.iso_639_1 === null ? 1 : 2);
  return images.posters
    .filter(p => p.aspect_ratio > 0.6 && p.aspect_ratio < 0.72)   // 2:3
    .filter(p => p.vote_count >= 2)
    .sort((a, b) =>
      rank(a) - rank(b) || b.vote_average - a.vote_average || b.vote_count - a.vote_count
    )[0] ?? null;
}

function pickLogo(images: TmdbImage[], lang: string) {
  return images.logos
    .filter(l => l.iso_639_1 === lang || l.iso_639_1 === null)
    .filter(l => l.aspect_ratio >= 1.5 && l.aspect_ratio <= 8)   // 1.5 is a real logo; 8 is a wordmark
    .filter(l => l.vote_count >= 1)
    .sort((a, b) => b.vote_average - a.vote_average || b.width - a.width)
    .at(0) ?? null;
}
```

Rationale for each filter:

- **`iso_639_1 === null` for backdrops** — 99% of backdrops are language-free; a tagged backdrop often
  has burned-in text or a regional logo, which looks wrong in a hero next to a localised UI.
- **`aspect_ratio >= 1.6` for backdrops** — the hero slot is 16:9 (1.778). TMDB backdrops are usually
  exactly 1.778, but some uploads are 2.35:1 or 1.85:1 (fine, crops cleanly) while others are 4:3 or
  1.33 (letterboxes badly). Rejecting sub-1.6 avoids visible pillarboxing.
- **`vote_count >= 3`** — `vote_average` on a 1-vote image is noise. The Fight Club backdrop example is
  `vote_average: 5.622, vote_count: 20` — that's a real signal. A `vote_average: 5.0, vote_count: 1`
  is not.
- **Tie-break on `vote_count`, then `width`** — prefer consensus, then resolution.
- **Posters: aspect 0.6–0.72** filters out the odd ultrawide/portrait-variant uploads that break a
  uniform grid.
- **Logo aspect 1.5–8** — below ~1.5 it's a square/boxy mark, above ~8 it's an unreadable wordmark.

### 19.3 Other things to get right

- **Always guard `null` file paths.** Plenty of catalogue entries have `poster_path: null` (and
  `/3/movie/{id}/lists` even returns `poster_path: null` routinely). Have a placeholder per image type
  with the **right aspect ratio** (2:3 for poster/profile, 16:9 for backdrop/still) so the grid doesn't
  jump.
- **Never cross sizes between image types.** `w1280` is backdrop-only; a still will 400.
- **SVG logos:** request `original` if `file_type` says SVG; otherwise any size (PNG rasterisation).
- **Company/network/provider `logo_path` is always `.png`** — never rewrite the extension, and don't try
  to guess at an `.svg` sibling.
- **`h632` is a height, not a width.** `h632` + a 2:3 source gives you ~421×632. Perfect for a uniform
  cast grid; wrong for a fixed-width layout.
- **Serve images from `image.tmdb.org` directly** (or via a CDN in front of it) and do not proxy them
  through your API — it wastes your request budget on bytes you don't need to parse.
- **`srcset`**: build it from the size list — e.g. poster `w185, w342, w500` and backdrop `w780, w1280`
  — with `sizes` matching your layout. The size lists in §2.2 are exactly the `srcset` candidates.

---

## 20. Netflix-style rails: concrete recipes

Every URL below is a real request. `<T>` is your Bearer token; put these behind your proxy.

### 20.1 "Trending Now" (mixed, weekly)

```
GET /3/trending/all/week?language=en-US
```

Take all 20, drop `adult === true`, normalise via `toCard`, render as a poster rail at `w342`. Because
there is no `page` param, this rail is **always exactly ≤20 items** — which is perfect for a "Top 10"
presentation (see 20.2) and a little thin for an infinite scroller. For a longer trending rail, use
`/3/trending/movie/week` + `/3/trending/tv/week` and concatenate, or use `/3/discover/*` with
`sort_by=popularity.desc`.

### 20.2 "Top 10 Movies Today" (numbered)

```
GET /3/trending/movie/day?language=en-US
```

`results.slice(0, 10)`, render at `w342` with the rank badge (1–10) overlaid. Netflix's numbered
Top-10 row is exactly this shape: poster-forward, big numerals, no backdrop needed.

For the weekly variant, swap `day` → `week`. For TV, `/3/trending/tv/day`.

### 20.3 "New Releases"

```
GET /3/movie/now_playing?language=en-US&region=US&page=1
GET /3/movie/upcoming?language=en-US&region=US&page=1
GET /3/tv/on_the_air?language=en-US&page=1&timezone=America/New_York
```

`now_playing` also returns `dates: { minimum, maximum }` — useful for a "in theatres
2026-09-12 → 2026-10-03" caption. `/3/tv/on_the_air` is the TV equivalent (7-day window).

### 20.4 "Popular on TMDB"

```
GET /3/movie/popular?language=en-US&region=US&page=1
GET /3/tv/popular?language=en-US&page=1
```

### 20.5 "Because you watched X"

Primary, best-quality recommendations:

```
GET /3/movie/550/recommendations?language=en-US&page=1
GET /3/tv/1399/recommendations?language=en-US&page=1
```

Fallback when recommendations come back empty (this genuinely happens — the Fight Club
`recommendations` example in the spec is an empty object):

```
GET /3/movie/550/similar?language=en-US&page=1
GET /3/tv/1399/similar?language=en-US&page=1
```

Implement `recommendations.length ? recommendations : similar` and seed the rail from the current
detail page. Add a `?seed=` param to your own rail endpoint so the hero's rail refetches on navigation.

### 20.6 "More Like This" on a title page

Combine both, dedupe by id, and exclude the current title:

```
GET /3/movie/550?append_to_response=recommendations,similar
```

One request instead of two; both keys land at the top level of the same payload.

### 20.7 "Kids"

Movies — family genre plus a runtime/language guard, certification-filtered for the market:

```
GET /3/discover/movie?language=en-US&region=US&with_genres=10751
  &certification.gte=G&certification.lte=PG&vote_count.gte=200
  &sort_by=popularity.desc
```

TV — the dedicated Kids genre (`10762`) plus the TV-Y/TV-Y7/TV-G band:

```
GET /3/discover/tv?language=en-US&with_genres=10762
  &sort_by=popularity.desc&vote_count.gte=50
```

Note `certification*` needs a region context on movies, and `discover/tv` has no certification filter —
use `content_ratings` client-side if you need a TV rating gate.

### 20.8 "Documentaries"

```
GET /3/discover/movie?language=en-US&with_genres=99&sort_by=popularity.desc&vote_count.gte=100
GET /3/discover/tv?language=en-US&with_genres=99&with_type=0&sort_by=vote_average.desc&vote_count.gte=50
```

`with_type=0` is Documentary for TV. Add
`&screened_theatrically=true` on the TV query to catch feature-length doc series that hit cinemas.

### 20.9 "Crime Dramas"

Comma = AND, so this is Crime **and** Drama:

```
GET /3/discover/movie?language=en-US&with_genres=80,18&sort_by=popularity.desc&vote_count.gte=500
GET /3/discover/tv?language=en-US&with_genres=80,18&sort_by=popularity.desc&vote_count.gte=200
```

**OR** variant (either genre is fine) uses a pipe — URL-encode it as `%7C`:

```
GET /3/discover/movie?language=en-US&with_genres=80%7C18&sort_by=popularity.desc
```

### 20.10 "Sci-Fi & Fantasy"

```
GET /3/discover/movie?language=en-US&with_genres=878,14&sort_by=popularity.desc&vote_count.gte=300
GET /3/discover/tv?language=en-US&with_genres=10765&sort_by=popularity.desc&vote_count.gte=100
```

### 20.11 "Thrillers" / "Horror" / "Comedy" / "Action & Adventure"

```
GET /3/discover/movie?language=en-US&with_genres=53&sort_by=popularity.desc   # Thriller
GET /3/discover/movie?language=en-US&with_genres=27&sort_by=popularity.desc   # Horror
GET /3/discover/movie?language=en-US&with_genres=35&sort_by=popularity.desc   # Comedy
GET /3/discover/movie?language=en-US&with_genres=28&sort_by=popularity.desc   # Action
GET /3/discover/tv?language=en-US&with_genres=10759&sort_by=popularity.desc  # Action & Adventure
```

Optionally add `&vote_average.gte=6.5` to keep these rails from being dominated by sequels.

### 20.12 "Anime"

There is **no** anime genre and **no** reliable anime keyword id. Resolve the keyword at runtime:

```
GET /3/search/keyword?query=anime        →  pick the "anime" entry
GET /3/discover/movie?language=ja-JP&with_genres=16&with_keywords=<id>&sort_by=popularity.desc
GET /3/discover/tv?language=ja-JP&with_genres=16&with_original_language=ja
  &with_keywords=<id>&sort_by=popularity.desc
```

Animation is `16` for both movies and TV. `with_original_language=ja` is the highest-precision anime
filter; combine with `with_keywords=<animeId>` for series that are tagged as adaptations. A reliable
third signal is `production_companies` — resolve known studios (Ghibli `id=141`, MAPPA, ufotable,
Madhouse…) via `/3/company/{id}` and use `with_companies=<id>`.

Cache the resolved keyword id at startup; it is stable.

### 20.13 "Classic Movies" (1920–1990)

```
GET /3/discover/movie?language=en-US
  &primary_release_date.gte=1920-01-01&primary_release_date.lte=1990-12-31
  &vote_average.gte=7.0&vote_count.gte=500
  &sort_by=vote_average.desc
```

Use `primary_release_date.*` (not `release_date.*`) so the filter is market-independent, and don't pass
`region`. Adjust the window per rail:

```
&primary_release_date.gte=1950-01-01&primary_release_date.lte=1979-12-31   # "Golden Age"
&primary_release_date.gte=1980-01-01&primary_release_date.lte=1999-12-31   # "80s & 90s"
```

For a "vintage TV" rail use `discover/tv` with `first_air_date.gte`/`.lte` and
`with_status=3` (Ended).

### 20.14 "Award Winners"

**TMDB has no awards data.** There is no Oscars/Emmy/BAFTA dataset, no award endpoints, and no award
fields. Any "Award Winners" rail has to be a proxy. Two workable options:

**Option A — keyword proxy** (resolve ids at runtime, cache them):

```
GET /3/search/keyword?query=academy award
GET /3/search/keyword?query=bafta
GET /3/discover/movie?language=en-US
  &with_keywords=<oscarId>,<baftaId>          # comma = AND: won both
  &vote_average.gte=7.5&vote_count.gte=1000
  &sort_by=vote_count.desc
```

**Option B — consensus proxy** (no keyword dependency, more stable):

```
GET /3/discover/movie?language=en-US
  &vote_average.gte=8.0&vote_count.gte=10000
  &primary_release_date.gte=1990-01-01
  &sort_by=vote_average.desc
```

`vote_count.gte` is the load-bearing part: a high `vote_average` with few votes is a cult title, not an
acclaimed one. Pair with `primary_release_year` for a "Best of the 1990s" rail.

**If you need real awards**, source them elsewhere (e.g. Wikidata via
`/3/find/{wikidata_id}`) and join on `external_ids.wikidata_id`. Say so honestly in the UI — label the
rail "Critically Acclaimed", not "Award Winners", unless the data really is awards data.

### 20.15 "Franchises" / "Collections"

```
GET /3/collection/10?language=en-US            # Star Wars Collection
```

Seed the rail from `movie.belongs_to_collection.id` on a title page, plus
`GET /3/search/collection?query=…` for a browse entry point, plus
`/3/collection/{id}/images` for a collection backdrop. Reorder `parts` by `release_date`.

### 20.16 "Streaming on <Service>"

```
GET /3/watch/providers/movie?language=en-US&watch_region=US   # resolve provider_id + logos
GET /3/discover/movie?language=en-US&watch_region=US
  &with_watch_providers=8&with_watch_monetization_types=flatrate
  &sort_by=popularity.desc
```

Provider `logo_path` (from the provider catalogue, or from any title's `watch/providers`) at `w92` makes
a clean service-logo rail header. Use `without_watch_providers` to build "leaving soon" rails
(complement = also available on buy/rent).

### 20.17 "Recommended episode order" (season pages)

```
GET /3/tv/1399/episode_groups
  → pick the group whose name isn't "Aired Order" (e.g. "Netflix Collections")
GET /3/tv/episode_group/5e9077d2e640d600151f32bd
  → groups[].episodes[]  (flat, with local `order`, crosses seasons)
```

Pair with `/3/tv/{id}/season/{s}` for the aired-order grid. Present `name`, not the `type` integer.

### 20.18 "Critically acclaimed series"

```
GET /3/discover/tv?language=en-US&vote_average.gte=8.5&vote_count.gte=1000
  &with_status=0,2,3,4&sort_by=vote_average.desc
```

`with_status` as a comma list = any of Returning Series / Planned / In Production / Ended.

### 20.19 Rail implementation checklist

1. **Every rail is one request** returning 20 items with full card data. No per-card detail calls.
2. Normalise once (`toCard`, §16.4) and cache the normalised card, not the raw payload.
3. Key your rail cache on the **full URL** (including `language` and `region`).
4. Refill rails on scroll with `page=2, 3…` (works for discover/list endpoints; **not** for trending).
5. Dedupe across rails client-side by `mediaType:id` so a title doesn't repeat in adjacent rows —
   a very visible Netflix-like quality signal.
6. Set `alt` text to the card title and always ship a correctly-proportioned placeholder.
7. Post-filter client-side: `!adult`, `vote_count >= <threshold>` for quality rails,
   `poster_path !== null` so you never render a broken card.
8. Sanity-check your own filters once: TMDB **silently ignores unknown query parameters** and returns
   unfiltered results, so a typo in `sort_by` looks like it "worked". Compare `total_results` with and
   without the param when you add one.

---

## 21. Legal / attribution requirements

Non-commercial use is free but **attribution is mandatory**. From TMDB's FAQ and terms:

> You shall use the TMDB logo to identify your use of the TMDB APIs. You shall place the following notice
> prominently on your application: **"This product uses the TMDB API but is not endorsed or certified by
> TMDB."**

- The notice goes in an **"About" or "Credits"** section.
- Any TMDB logo must be **less prominent** than your own product mark, must not be recoloured, resized
  in aspect, flipped or rotated, and must not imply endorsement. Use an
  [approved logo](https://www.themoviedb.org/about/logos-attribution).
- Refer to the service as "TMDB" or "The Movie Database" only, and link back to
  `https://www.themoviedb.org`.
- **Commercial use requires a separate written agreement** (`sales@themoviedb.org`; see
  `https://www.themoviedb.org/api-for-business`). A Netflix-style product is a commercial product —
  get the licence before shipping, and note the **attribution must be in the Credits/About section**,
  not just a terms page.
- TMDB disclaims any SLA; status is best-effort at `https://status.themoviedb.org`.
- Your UI must not present itself as TMDB or imply affiliation. The "not endorsed or certified" notice
  is not optional boilerplate.
- TMDB disclaims ownership of all data and images and complies with DMCA takedowns. Don't build a
  "download the full catalogue" feature — that is the bulk-scraping behaviour their rate limiting exists
  to prevent.

---

## 22. Complete endpoint index (all 148 documented paths)

Base: `https://api.themoviedb.org`. All `GET` unless noted.

**ACCOUNT** — `/3/account/{account_id}` · `+ /favorite` (POST) · `+ /favorite/movies` · `+ /favorite/tv` ·
`+ /lists` · `+ /rated/movies` · `+ /rated/tv` · `+ /rated/tv/episodes` · `+ /watchlist` (POST) ·
`+ /watchlist/movies` · `+ /watchlist/tv`

**AUTHENTICATION** — `/3/authentication` · `/3/authentication/guest_session/new` ·
`/3/authentication/session` (DELETE) · `/3/authentication/session/convert/4` (POST) ·
`/3/authentication/session/new` (POST) · `/3/authentication/token/new` ·
`/3/authentication/token/validate_with_login` (POST)

**CERTIFICATIONS** — `/3/certification/movie/list` · `/3/certification/tv/list`

**COLLECTIONS** — `/3/collection/{collection_id}` · `+ /images` · `+ /translations`

**COMPANIES** — `/3/company/{company_id}` · `+ /alternative_names` · `+ /images`

**CONFIGURATION** — `/3/configuration` · `+ /countries` · `+ /jobs` · `+ /languages` ·
`+ /primary_translations` · `+ /timezones`

**CREDITS** — `/3/credit/{credit_id}`

**DISCOVER** — `/3/discover/movie` · `/3/discover/tv`

**FIND** — `/3/find/{external_id}`

**GENRES** — `/3/genre/movie/list` · `/3/genre/tv/list`

**GUEST SESSIONS** — `/3/guest_session/{guest_session_id}/rated/movies` · `+ /rated/tv` · `+ /rated/tv/episodes`

**KEYWORDS** — `/3/keyword/{keyword_id}` · `/3/keyword/{keyword_id}/movies`

**LISTS** — `/3/list` (POST) · `/3/list/{list_id}` (GET, DELETE) · `+ /add_item` (POST) ·
`+ /clear` (POST) · `+ /item_status` · `+ /remove_item` (POST)

**MOVIE LISTS** — `/3/movie/now_playing` · `/3/movie/popular` · `/3/movie/top_rated` · `/3/movie/upcoming`

**MOVIES** — `/3/movie/latest` · `/3/movie/changes` · `/3/movie/{movie_id}` · `+ /account_states` ·
`+ /alternative_titles` · `+ /changes` · `+ /credits` · `+ /external_ids` · `+ /images` · `+ /keywords` ·
`+ /lists` · `+ /rating` (POST, DELETE) · `+ /recommendations` · `+ /release_dates` · `+ /reviews` ·
`+ /similar` · `+ /translations` · `+ /videos` · `+ /watch/providers`

**NETWORKS** — `/3/network/{network_id}` · `+ /alternative_names` · `+ /images`

**PEOPLE** — `/3/person/latest` · `/3/person/popular` · `/3/person/changes` · `/3/person/{person_id}` ·
`+ /changes` · `+ /combined_credits` · `+ /external_ids` · `+ /images` · `+ /movie_credits` ·
`+ /tagged_images` · `+ /translations` · `+ /tv_credits`

**REVIEWS** — `/3/review/{review_id}`

**SEARCH** — `/3/search/collection` · `/3/search/company` · `/3/search/keyword` · `/3/search/movie` ·
`/3/search/multi` · `/3/search/person` · `/3/search/tv`

**TRENDING** — `/3/trending/all/{time_window}` · `/3/trending/movie/{time_window}` ·
`/3/trending/person/{time_window}` · `/3/trending/tv/{time_window}`

**TV SERIES LISTS** — `/3/tv/airing_today` · `/3/tv/on_the_air` · `/3/tv/popular` · `/3/tv/top_rated`

**TV SERIES** — `/3/tv/latest` · `/3/tv/changes` · `/3/tv/{series_id}` · `+ /account_states` ·
`+ /aggregate_credits` · `+ /alternative_titles` · `+ /changes` · `+ /content_ratings` · `+ /credits` ·
`+ /episode_groups` · `+ /external_ids` · `+ /images` · `+ /keywords` · `+ /lists` · `+ /rating`
(POST, DELETE) · `+ /recommendations` · `+ /reviews` · `+ /screened_theatrically` · `+ /similar` ·
`+ /translations` · `+ /videos` · `+ /watch/providers`

**TV SEASONS** — `/3/tv/{series_id}/season/{season_number}` · `+ /account_states` ·
`+ /aggregate_credits` · `+ /credits` · `+ /external_ids` · `+ /images` · `+ /translations` ·
`+ /videos` · `+ /watch/providers` · `/3/tv/season/{season_id}/changes`

**TV EPISODES** — `/3/tv/{series_id}/season/{season_number}/episode/{episode_number}` ·
`+ /account_states` · `+ /credits` · `+ /external_ids` · `+ /images` · `+ /translations` · `+ /videos` ·
`/3/tv/episode/{episode_id}/changes`

**TV EPISODE GROUP** — `/3/tv/episode_group/{tv_episode_group_id}`

**WATCH PROVIDERS** — `/3/watch/providers/movie` · `/3/watch/providers/tv` · `/3/watch/providers/regions`

---

## 23. Pitfalls checklist

1. **`success === false` can arrive with HTTP 200** (error 21). Always check the body.
2. **Movie keywords are under `keywords`; TV keywords are under `results`.** Normalise.
3. **`collection.parts[]` and `combined_credits[]` use `name`, not `title`.** Normalise via `toCard`.
4. **Season 0 = Specials.** Filter it out of season pickers by default.
5. **`seasons[].vote_average` is `0` and `overview` is `""` for Specials.** Don't render a 0.0 badge.
6. **Trending has no `page` param.** 20 items, hard stop.
7. **`with_runtime.gte` etc. are per-episode for TV** (the episode runtime range), not total series length.
8. **`vote_average` is 0–10**, not 0–100. Multiply by 10 for a percentage display.
9. **`vote_average` without `vote_count` is meaningless.** Always gate quality rails on `vote_count.gte`.
10. **Comma = AND, pipe = OR** in multi-value discover filters. `80,18` ≠ `80|18`. URL-encode the pipe.
11. **`sort_by=vote_average.desc` with no `vote_count.gte` surfaces 1-vote 10.0 titles.** Always pair.
12. **`region` silently changes which date `release_date.*` filters apply to.** Use
    `primary_release_date.*` for market-independent filters.
13. **`discover/tv` has no `revenue` sort and no `with_release_type`.** Use `first_air_date.*`.
14. **`discover/tv?with_networks` takes a single integer** per the spec (not a `,`/`|` list).
15. **Certifications have no `order` that sorts as text.** Use the numeric `order` field.
16. **No rate-limit headers exist.** Client-side budget + `429` backoff is the only option.
17. **No batch endpoint.** Use list payloads; they already contain full card data.
18. **Unknown query params are ignored, not rejected.** A typo silently returns unfiltered results.
19. **`append_to_response` max 20**, and each append is a real internal call — don't 20-way-append list views.
20. **`include_video_language` is TV-only**; filter movie videos client-side.
21. **`include_image_language` is `/images`-only**, and your `language` param filters that response.
22. **Seasons have posters but no backdrops; episodes have stills only.** Don't plan a season hero backdrop.
23. **Person names and character names are not translated.** Localised cast lists will look English.
24. **All `logo_path` fields are `.png`** even when the source is SVG. Don't rewrite extensions.
25. **`file_path` can be `null` everywhere.** Ship per-type placeholders with correct aspect ratios.
26. **`/3/tv/now_playing` and `/3/collection/{id}/search` don't exist.** Use `/3/tv/on_the_air` and
    `/3/search/collection`.
27. **There is no suggestions/autocomplete endpoint.** Debounce `/3/search/multi`.
28. **No awards data in TMDB.** "Award Winners" rails are proxies — label them honestly.
29. **Attribution is mandatory and must live in About/Credits**; commercial use needs a licence.
30. **Never call TMDB from the browser.** Proxy it, or your Bearer token is a public key.
