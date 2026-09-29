# Troubleshooting

Start here: **Dashboard ▸ Plugins ▸ Reko ▸ Caching and diagnostics ▸ Verbose logging**, on, then
reload the web page and read the Jellyfin log. Reko's decisions are logged with a `[Reko]` prefix.

For the client side, open the browser console and run:

```js
localStorage.rekoDebug = "1";
```

then reload the page. Reko traces its own decisions to the console under a `[Reko]` tag. It is opt-in
because an always-on trace floods a shared console on every navigation. Clear it with
`localStorage.removeItem('rekoDebug')`.

## The tab is not there at all

- **Check the plugin is loaded.** Dashboard ▸ Plugins. Reko should say `Active`. If it says
  `PendingRestart`, restart Jellyfin.
- **Check the version.** Reko targets Jellyfin 12.1 exactly. On 12.0 or 10.x Jellyfin will refuse to
  load it; that is the plugin manifest's `targetAbi`, not a bug.
- **Check "Enable tab"** is on, under Dashboard ▸ Plugins ▸ Reko ▸ Home tab.
- **Reload the web page, not just the tab.** Reko injects itself into the page as it loads. An open
  tab that was never reloaded after installing the plugin will not have it.
- **Hard-reload.** Client modules are cached with a long `Cache-Control`, keyed on a build-specific
  query. A normal reload is enough after an update; a hard reload is enough if something is stuck.

If the tab is missing but the console shows `[Reko] pass: the home page is not ready yet` repeating,
Reko is running and jellyfin-web's home page is not the shape it expects. That is a jellyfin-web
change; the `rekoDebug` trace will show which selector found nothing. Please report it with the
Jellyfin version and the trace.

## The tab is there but says it needs a TMDB key

The **API Read Access Token** field is empty, or holds the older API key instead. TMDB issues both;
only the Read Access Token works. Copy it from
[themovordb.org/settings/api](https://www.themoviedb.org/settings/api) — the long one under
"API Read Access Token".

If the token is definitely set and you still get this, reload the web page after saving: the client
reads the configuration once at startup.

## The tab is there but every row is empty

- **Check the token works** by saving it and reloading. A wrong token produces rows that fail to load
  rather than an error banner, because one failing row should not take the page down.
- **Turn on verbose logging** and look for `401` or `401`/`403` responses from
  `api.themoviedb.org` in the Jellyfin log.
- **Check the server can reach TMDB.** Jellyfin makes the requests, not your browser. A server with no
  outbound internet needs the advanced **TMDB base URL** and **image base URL** set to a mirror.

## Cards have no artwork, but everything else works

The API is reachable and the image CDN is not. Set **Image base URL** as well as **API base URL** — a
mirror that proxies the API but not the image CDN produces exactly this.

## "Where to watch" is always empty

Watch providers are region-specific, and TMDB has data for some countries and not others. Set
**TMDB region** to a two-letter country code that has distribution data for what you are looking at.
`US`, `GB`, `DE`, `FR`, `CA`, `AU` and `NL` have the most.

## The Request button is missing

Requests need **all** of:

- **Enable requests** on,
- an **Overseerr / Jellyseerr URL**,
- an **Overseerr / Jellyseerr API key**.

The key is in Overseerr or Jellyseerr under Settings ▸ General ▸ API Key. Reko hides the button
rather than offering one that would fail.

## Requests fail, or the wrong person's requests show up

- **Check the Seerr URL is reachable from the Jellyfin server**, not just from your browser. Jellyfin
  makes the request.
- **Leave "Map Jellyfin users to Seerr users" on.** It matches the signed-in Jellyfin user to their
  Seerr user so requests and quotas are attributed correctly. If the match fails, everyone sees the
  same requests.
- **Verbose logging** will show the Seerr status code.

## Personalised rows never appear

Reko needs a history before it will try to guess your taste. If you have watched fewer than
**Minimum watched for personalised rows** items — 3 by default — it deliberately shows nothing rather
than a row built from one film. Watch something, or lower the threshold.

If you have plenty of history, check that **Personalised rows** and **Continue watching** are on
under Dashboard ▸ Plugins ▸ Reko ▸ Personalisation.

## The tab renders but the page behind it is blank, or two tabs are showing at once

Both layouts should show exactly one panel at a time. If the Home panel and the Reko panel are
visible together, you are on the Legacy layout and something is resetting Jellyfin's own tab
selection after Reko applies it — usually another plugin that also manipulates `.emby-tabs`.

With `localStorage.rekoDebug = "1"`, the trace will show whether Reko is asking Jellyfin's tab strip
to select its tab. If it is and the selection still reverts, the culprit is the other plugin; disable
it and Reko is fine.

## Two Reko tabs, or the tab has the wrong number

Reko allocates its tab index by scanning for the highest one already in use, so it does not overwrite
another tab plugin. If you have two tab plugins installed, check that the other one is not also
injecting into the home page. Reko's own tab is at `#/home?tab=<n>` — the number is in the tab's link.

## Everything looks stale

Raise the cache lifetimes, or clear them by restarting Jellyfin. Conversely, if you want fresher data
at the cost of TMDB calls, lower them under Caching.

## Reko is fighting with my theme

Reko is a full-bleed dark page, the way the tab it is imitating is. Its stylesheet is namespaced under
`.reko*` so it cannot restyle Jellyfin, and it follows Jellyfin's light/dark switch. If the app bar
above it looks wrong, that is Jellyfin's translucent app bar over Reko's black page, and it is the
same on Jellyfin's own pages.

If Reko's own colours clash with a custom theme, the custom properties at the top of `.rekoApp` in
`reko.css` are the ones to override.

## Reporting a problem

Please include:

- Jellyfin version (`Dashboard ▸ System`), and whether you are on the Modern or Legacy layout
  (Dashboard ▸ Settings ▸ Display ▸ Display mode),
- the browser and its version,
- the output of `localStorage.rekoDebug = "1"` followed by a reload and the problem,
- the Jellyfin log with verbose logging on,
- whether Overseerr or Jellyseerr is involved, and if so its version.

The jellyfin-web version matters more than it looks: Reko integrates with the DOM jellyfin-web builds,
and that is the thing most likely to have changed.
