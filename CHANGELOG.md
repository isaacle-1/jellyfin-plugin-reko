# Changelog

All notable changes to Reko are recorded here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and Reko uses
[semantic versioning](https://semver.org/spec/v2.0.0.html) on the four-part Jellyfin version.

## [1.0.0] — unreleased

Initial release.

### Added

- A **Reko** tab on the Jellyfin home page, directly beside Home and Favorites, in both the Modern
  layout (Jellyfin 12.0's drawer and 12.1's app-bar tabs) and the Legacy and TV layouts.
- A rotating hero billboard using TMDB logo artwork, with a typeset title where a title has no logo.
- Horizontal rails with scroll snapping, hover arrows and lazy hydration: trending, popular, ranked
  Top 10 today and this week, and curated genre and theme rows.
- Hover previews on every card, showing backdrop, facts, synopsis, library and request badges, and
  the actions available for that title.
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
