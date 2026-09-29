/**
 * View routing on top of Jellyfin's hash router.
 *
 * Reko cannot register a route of its own: jellyfin-web 12 is a `createHashRouter` SPA with no plugin
 * route registry, and the server has no SPA fallback, so a path that is not already in the client's
 * table renders "Page not found".
 *
 * The way through is to live inside the home route and carry Reko's own state in extra query
 * parameters on it:
 *
 *   #/home?tab=2                      the Reko tab
 *   #/home?tab=2&view=title&id=603    a movie
 *   #/home?tab=2&view=title&id=1399&type=tv&season=2
 *   #/home?tab=2&view=search&q=alien
 *   #/home?tab=2&view=browse&type=movie&genre=28
 *   #/home?tab=2&view=person&id=6384
 *
 * This is not a workaround. It means browser back and forward work exactly as a person expects,
 * because a history entry is a real URL that the client's own router already understands. `tab` is
 * Jellyfin's own deep-link convention (`?tab=1` is Favorites), so `?tab=2` selects the Reko tab the
 * same way with no extra machinery.
 */

const HOME_PATH = '/home';

/** The home route with no extra state. */
export const TAB_HASH = `#${HOME_PATH}?tab=2`;

/**
 * The Reko tab index.
 *
 * `2` is correct on a clean server. It is only ever used as a starting point; the injector allocates
 * the real index by scanning the slider, because other tab plugins may already have claimed one.
 */
export const DEFAULT_TAB_INDEX = 2;

/**
 * Parses the current hash into a Reko view model.
 *
 * `active` means "the home page is being shown", which is deliberately independent of the `tab`
 * parameter. Web/early.js strips `?tab=N` when no tab button backs it yet, so a URL with no `tab` at
 * all is a normal, expected state that still has to be recognised, not an inactive route.
 *
 * @param {string} [hash] The hash to parse. Defaults to the current one.
 * @returns {Object} `{ active, tabIndex, view, id, type, season, genre, sort, query, page }`.
 */
export function parse(hash = window.location.hash) {
    const result = {
        active: false,
        tabIndex: 0,
        view: 'home',
        id: null,
        type: null,
        season: null,
        genre: null,
        sort: null,
        query: '',
        page: 1
    };

    const raw = String(hash ?? '').replace(/^#/, '');
    if (!raw) {
        return result;
    }

    const queryIndex = raw.indexOf('?');
    const path = queryIndex === -1 ? raw : raw.slice(0, queryIndex);
    const search = queryIndex === -1 ? '' : raw.slice(queryIndex + 1);

    if (!path.startsWith(HOME_PATH)) {
        return result;
    }

    result.active = true;

    const params = new URLSearchParams(search);
    const tab = Number.parseInt(params.get('tab') ?? '', 10);
    if (Number.isFinite(tab) && tab >= 0) {
        result.tabIndex = tab;
    }

    result.view = params.get('view') || 'home';

    const id = Number.parseInt(params.get('id') ?? '', 10);
    result.id = Number.isFinite(id) ? id : null;

    const season = Number.parseInt(params.get('season') ?? '', 10);
    result.season = Number.isFinite(season) ? season : null;

    const genre = Number.parseInt(params.get('genre') ?? '', 10);
    result.genre = Number.isFinite(genre) ? genre : null;

    const page = Number.parseInt(params.get('page') ?? '', 10);
    result.page = Number.isFinite(page) && page > 0 ? page : 1;

    result.type = params.get('type');
    result.sort = params.get('sort');
    result.query = params.get('q') ?? '';

    return result;
}

/**
 * Builds a hash for a Reko view, preserving any state that is not being replaced.
 *
 * The tab index defaults to Reko's own tab rather than to whatever the URL says, because a view can
 * only be reached from inside Reko, so the parsed index is always the one being replaced.
 *
 * @param {Object} changes The fields to change.
 * @param {Object} [current] The current state, to preserve the rest. Defaults to the live hash.
 * @returns {string} The hash, without the leading `#`.
 */
export function build(changes = {}, current = parse()) {
    const state = { ...current, ...changes };

    const params = new URLSearchParams();
    params.set('tab', String(changes.tabIndex ?? DEFAULT_TAB_INDEX));

    if (state.view && state.view !== 'home') {
        params.set('view', state.view);
    }

    if (state.id !== null && state.id !== undefined) {
        params.set('id', String(state.id));
    }

    if (state.type) {
        params.set('type', state.type);
    }

    if (state.season !== null && state.season !== undefined) {
        params.set('season', String(state.season));
    }

    if (state.genre !== null && state.genre !== undefined) {
        params.set('genre', String(state.genre));
    }

    if (state.sort) {
        params.set('sort', state.sort);
    }

    if (state.query) {
        params.set('q', state.query);
    }

    if (state.page && state.page > 1) {
        params.set('page', String(state.page));
    }

    return `${HOME_PATH}?${params.toString()}`;
}

/**
 * Navigates within the Reko tab, pushing a history entry.
 *
 * @param {Object} changes The fields to change.
 */
export function navigate(changes = {}) {
    const next = `#${build(changes)}`;
    if (window.location.hash === next) {
        return;
    }

    window.location.hash = next;
}

/**
 * Replaces the current history entry.
 *
 * @param {Object} changes The fields to change.
 */
export function replace(changes = {}) {
    const next = `#${build(changes)}`;
    if (window.location.hash === next) {
        return;
    }

    const url = `${window.location.pathname}${window.location.search}${next}`;
    window.history.replaceState(window.history.state, '', url);
}
