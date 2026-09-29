# Installation

## Requirements

- **Jellyfin 12.1.** Reko targets Jellyfin's 12.1 ABI exactly. It will not load on 12.0 or on 10.x.
- **A TMDB API Read Access Token.** Free, from
  [developer.themoviedb.org](https://developer.themoviedb.org/docs/authentication-application).
  Look for "API Read Access Token" — *not* the older "API Key", which is a different credential and
  will not work.
- **Overseerr or Jellyseerr**, optionally. Only needed for the Request button.

## From the plugin repository

1. **Dashboard ▸ Plugins ▸ Repositories ▸ Add**
2. Paste:

   ```
   https://raw.githubusercontent.com/isaacle-1/jellyfin-plugin-reko/gh-pages/manifest.json
   ```

3. **Save**, then **Dashboard ▸ Plugins ▸ Catalog** and install **Reko**.
4. Restart Jellyfin.

The repository manifest lives on the `gh-pages` branch, so installing from the URL above always
offers the latest published version, whatever `main` happens to contain.

## From a release archive

1. Download `reko_<version>.zip` from the
   [releases page](https://github.com/isaacle-1/jellyfin-plugin-reko/releases).
2. Unzip it somewhere.
3. **Dashboard ▸ Plugins ▸ Install from manifest** is not what you want here. Instead copy the
   contents of the zip into a directory of your choosing and add it under
   **Dashboard ▸ Plugins ▸ Additional Plugins ▸ Install from folder**.

## Configuring

**Dashboard ▸ Plugins ▸ Reko.**

### Required

| Setting | Notes |
|---|---|
| **TMDB API Read Access Token** | The Read Access Token, not the API Key. It stays on the server and is never sent to a browser. |

Without it, Reko's tab appears and shows a notice telling you what is missing. Nothing else loads,
and no error is thrown at Jellyfin.

### Optional — requests

| Setting | Notes |
|---|---|
| **Enable requests** | Master switch. Off means no Request button anywhere. |
| **Overseerr / Jellyseerr URL** | e.g. `https://seerr.example.com`. No trailing slash needed. |
| **Overseerr / Jellyseerr API key** | Overseerr/Jellyseerr ▸ Settings ▸ General ▸ API Key. |
| **Map Jellyfin users to Seerr users** | On by default. Turns requests and quotas into the right person's. |

If the URL or the key is missing, the Request button is hidden rather than failing, and a warning
banner explains why the first time the tab loads.

### Appearance and content

| Setting | Default | Notes |
|---|---|---|
| **Tab label** | `Reko` | The text on the tab and in the drawer. |
| **Hero titles** | 5 | How many titles rotate through the billboard. |
| **Hero rotation** | 9 seconds | Time per title. Set 0 to stop rotating. |
| **Items per row** | 20 | Cards per rail. |
| **Continue watching** | on | The resume row, if you have anything part-watched. |
| **Continue watching items** | 12 | |
| **Personalised rows** | on | Rows built from what you have watched. |
| **Personalised rows to build** | 4 | |
| **Minimum watched for personalised rows** | 3 | Below this, Reko does not try to guess your taste. |
| **Hide watched from personalised rows** | on | Stops the rows filling up with things you have already seen. |
| **Include movies / series** | both on | Turn one off to halve the number of TMDB calls. |

### Detail pages

Cast, trailers and watch providers can each be turned off. Watch providers are region-specific: if
the row is always empty, your **TMDB region** is probably set to a country with no distribution
data for what you are browsing.

### Regional and language

| Setting | Default | Notes |
|---|---|---|
| **TMDB language** | `en-US` | An IETF primary subtag, optionally with a region. |
| **TMDB region** | `US` | Used only for watch providers. |
| **TMDB image language** | *(empty)* | Leave empty to follow the language setting. Only set it to fetch logos in another language on purpose. |

### Caching

| Setting | Default |
|---|---|
| **Rails** | 180 minutes |
| **Trending** | 45 minutes |
| **Title details** | 12 hours |
| **Taste profile** | 720 minutes |

Raising these makes the tab faster to load and staler. Lowering them costs TMDB calls.

### Advanced

| Setting | Notes |
|---|---|
| **TMDB base URL** | An alternative API root, including the `/3/` suffix. For a mirror or a caching proxy. Leave blank for TMDB itself. |
| **Image base URL** | An alternative image CDN root, up to but excluding the size token. |
| **Verbose logging** | Reko's decisions go to the Jellyfin log. |

Set the base URL and the image base URL **together**. A mirror that proxies the API but not the
image CDN produces cards whose artwork never loads, and the symptom looks like a CSS bug rather than
a configuration one.

## Changing layout

Reko works in both of Jellyfin's layouts and picks the right one automatically:

- **Modern**, the default from 12.0.
- **Legacy** — Dashboard ▸ Settings ▸ Display ▸ Display mode ▸ Desktop.

There is nothing to configure. Reko detects which one is on screen by what is actually visible rather
than by a version number, because both layouts are present in the DOM at once and only one of them is
on screen.

## Turning it off

**Enable tab**, off. Reko's panel and button disappear and its endpoints keep answering, harmlessly,
in case a stale page has not been reloaded yet. Nothing is written to your library either way.
