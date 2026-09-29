/**
 * The Reko application: bootstrap, view switching, and the handlers shared by every view.
 */

import * as api from './api.js';
import * as router from './router.js';
import { el, empty, createLogger, isModernLayout } from './utils.js';
import { createHero } from './hero.js';
import { watchChrome } from './chrome.js';
import { createRail } from './rail.js';
import { createTitlePage, createPersonPage } from './detail.js';
import { createSearchView, createBrowseView } from './search.js';
import { requestTitle, refreshBadges, checkSeerr } from './seerr.js';

const log = createLogger();

const state = {
    config: { tabLabel: 'Reko' },
    home: null,
    cardsById: new Map(),
    rendered: null,
    generation: 0
};

/**
 * The tab label, used by the injector before the first payload has loaded.
 *
 * @returns {string} The label.
 */
export function tabLabel() {
    return state.config?.tabLabel || 'Reko';
}

/**
 * The panel and route the last render was for.
 *
 * `start()` is called on every injection pass, and a pass is scheduled by a DOM mutation. Rendering
 * replaces the panel's children, which is itself a DOM mutation, so an unguarded render schedules
 * the pass that renders again: a loop that rebuilds every rail as fast as the browser can paint,
 * forever. It also makes the page a moving target, so nothing about it can be measured or clicked
 * reliably.
 *
 * @type {{panel: HTMLElement|null, key: string|null}}
 */
let lastRender = { panel: null, key: null };

/**
 * Starts Reko inside the given panel, or re-renders the current route if it is already running.
 *
 * @param {HTMLElement} panel The tab panel.
 * @returns {Promise<void>} Resolves once the first render has settled.
 */
export async function start(panel) {
    if (!panel) {
        return;
    }

    watchChrome();

    // The route on its own is not enough to identify a render: the home route is remounted on every
    // navigation, and the new panel starts empty, so the panel has to be part of the key too.
    const key = JSON.stringify(router.parse());
    if (state.rendered?.isConnected && panel === lastRender.panel && key === lastRender.key) {
        return;
    }

    // Claimed before the awaits below, so a pass that arrives while the first render is still in
    // flight waits for it rather than starting a second one over the top of it.
    lastRender = { panel, key };

    if (state.rendered?.isConnected) {
        await renderRoute(panel);
        return;
    }

    let bootstrap;
    try {
        bootstrap = await api.bootstrap();
    } catch (error) {
        show(panel, fatal('Reko could not start', api.messageOf(error)));
        return;
    }

    state.config = { ...state.config, ...(bootstrap.config ?? {}) };
    log.debug('Reko bootstrapped.', bootstrap);

    if (!bootstrap.tmdbConfigured) {
        show(panel, notConfigured(bootstrap));
        return;
    }

    try {
        state.home = await api.home();
        indexCards(state.home);
    } catch (error) {
        show(panel, fatal('Reko could not load', api.messageOf(error)));
        return;
    }

    if (state.home.notice) {
        show(panel, notice(state.home.notice));
        return;
    }

    await renderRoute(panel);

    // A wrong Seerr setup is worth surfacing before the first request rather than during it.
    void checkSeerr().then((seerr) => {
        if (seerr?.warning) {
            log.warn(seerr.warning);
            showBanner(seerr.warning);
        }
    });
}

/**
 * Renders whatever the current hash asks for.
 *
 * @param {HTMLElement} panel The tab panel.
 * @returns {Promise<void>} Resolves when the view is on screen.
 */
export async function renderRoute(panel) {
    const route = router.parse();

    const generation = ++state.generation;
    const host = el('div.rekoHost');
    show(panel, host);

    const stale = () => generation !== state.generation;

    /**
     * Puts a view on screen inside the page frame.
     *
     * The frame, and the sticky header in it, belong to Reko rather than to any one view: a person
     * who has opened a title expects to be able to search from there, exactly as they can on the
     * rows, and a search box that only exists on the home view is a dead end halfway through a
     * browse.
     *
     * @param {Node} view The view.
     */
    const present = (view) => {
        if (stale()) {
            return;
        }

        host.replaceChildren(el('div.rekoPage.rekoView', null, [buildHeader(), view]));
    };

    switch (route.view) {
        case 'title':
            if (route.id) {
                present(
                    await createTitlePage(
                        route.id,
                        route.type === 'tv' ? 'tv' : 'movie',
                        state.config,
                        actions(),
                        route.season
                    )
                );
            }

            break;

        case 'person':
            if (route.id) {
                present(await createPersonPage(route.id, state.config, actions()));
            }

            break;

        case 'search':
            present(createSearchView(route.query, state.config, actions()));
            break;

        case 'browse':
            present(
                createBrowseView({
                    title: route.query || 'Browse',
                    type: route.type === 'tv' ? 'tv' : 'movie',
                    genre: route.genre,
                    sort: route.sort,
                    config: state.config,
                    actions: actions()
                })
            );
            break;

        default:
            present(buildHome());
            break;
    }
}

/**
 * Builds the home view: the hero and the rails.
 *
 * The sticky header is not built here. `renderRoute` puts one above every view, so that search is
 * reachable from a title page and a browse as well as from the rows.
 *
 * @returns {HTMLElement} The home element.
 */
function buildHome() {
    const home = state.home ?? { rails: [], hero: [] };
    const root = el('div.rekoHome');

    if (home.hero?.length) {
        root.appendChild(createHero(home.hero, state.config, actions()));
    }

    for (const rail of home.rails ?? []) {
        root.appendChild(createRail(rail, state.config, actions()));
    }

    if ((home.rails ?? []).length === 0) {
        root.appendChild(
            el('div.rekoRailMessage', {
                text: 'No rows could be loaded. Check the TMDB key and the server log.'
            })
        );
    }

    return root;
}

/**
 * Builds the sticky Reko header, with the search field.
 *
 * @returns {HTMLElement} The header.
 */
function buildHeader() {
    const header = el('header.rekoHeader');

    header.appendChild(el('span.rekoHeaderTitle', { text: state.config.tabLabel }));

    if (state.config.enableSearch) {
        const input = el('input.rekoHeaderSearch', {
            type: 'search',
            placeholder: 'Search',

            // Carried over from the URL, so arriving at a search by deep link or by the browser's
            // back button shows the query that was run rather than an empty box.
            value: router.parse().query,
            attrs: {
                'aria-label': `Search ${state.config.tabLabel}`,
                autocomplete: 'off',
                enterkeyhint: 'search'
            },
            on: {
                keydown: (event) => {
                    if (event.key !== 'Enter') {
                        return;
                    }

                    const query = input.value.trim();
                    if (query) {
                        router.navigate({
                            view: 'search',
                            query,
                            id: null,
                            type: null,
                            season: null
                        });
                    }
                }
            }
        });

        header.appendChild(input);
    }

    return header;
}

/**
 * The handlers shared by every view.
 *
 * @returns {Object} The action bundle.
 */
function actions() {
    return {
        onOpen: (card) => router.navigate({
            view: 'title',
            id: card.id,
            type: card.type,
            season: null,
            query: ''
        }),

        onPlay: playJellyfinItem,

        onRequest: async (card) => {
            // A series needs its season list before the picker can be shown, and the title endpoint
            // is the only place it exists.
            const payload = card.type === 'tv'
                ? await api.title(card.id, card.type).catch((error) => {
                    log.warn('Could not load seasons for the request picker.', error);
                    return null;
                })
                : null;

            await requestTitle(card, state.config, payload, refreshCurrentView);
        },

        onBack: () => router.navigate({
            view: 'home',
            id: null,
            type: null,
            season: null,
            query: ''
        }),

        onSelectSeason: (season) => router.replace({ season }),

        onSortChange: (sort) => router.replace({ sort }),

        onQueryChange: (query) => router.replace({ query }),

        onBrowseGenre: (genre) => router.navigate({
            view: 'browse',
            id: null,
            type: null,
            season: null,
            genre,
            query: genre
        }),

        onPerson: (id) => router.navigate({
            view: 'person',
            id,
            type: null,
            season: null
        })
    };
}

/**
 * Records every card from the home payload so badges can be refreshed later.
 *
 * @param {Object} payload The home payload.
 */
function indexCards(payload) {
    state.cardsById.clear();

    const add = (card) => state.cardsById.set(`${card.type}:${card.id}`, card);

    for (const card of payload?.hero ?? []) {
        add(card);
    }

    for (const rail of payload?.rails ?? []) {
        for (const card of rail.items) {
            add(card);
        }
    }
}

/**
 * Re-reads the home payload and re-renders. This is how a badge changes after a request.
 *
 * @returns {Promise<void>} Resolves when re-rendered.
 */
async function refreshCurrentView() {
    const panel = document.querySelector('[data-reko="panel"]');
    if (!panel) {
        return;
    }

    try {
        state.home = await api.home();
        indexCards(state.home);
    } catch (error) {
        log.warn('Could not refresh the home payload.', error);
    }

    await renderRoute(panel);
}

/**
 * Opens a Jellyfin item.
 *
 * Reko never starts playback itself. It hands the item to Jellyfin, which owns the resume point, the
 * audio and subtitle preferences, the version picker and the transcoding decision. Reimplementing
 * any of that would be worse in every way.
 *
 * @param {Object} card The card payload.
 */
function playJellyfinItem(card) {
    if (!card.jellyfinItemId) {
        router.navigate({ view: 'title', id: card.id, type: card.type, season: null, query: '' });
        return;
    }

    // The Modern layout routes to /item?id=; the Legacy layout uses its own controller path. The
    // discriminator is the same one the injector uses to decide where the tab lives.
    window.location.hash = isModernLayout()
        ? `#/item?id=${encodeURIComponent(card.jellyfinItemId)}`
        : `#/itemdetailshome.html?id=${encodeURIComponent(card.jellyfinItemId)}`;
}

/**
 * Replaces the panel contents with a node, reusing the mounted host when there is one.
 *
 * The node goes *inside* Reko's own root rather than replacing it. The whole stylesheet is written
 * against the custom properties declared on `.rekoApp` — `--reko-text`, `--reko-accent`,
 * `--reko-border` and the rest — so anything mounted beside that element resolves none of them and
 * renders with every colour falling back to whatever Jellyfin happened to have inherited. On a dark
 * theme that is close enough to Reko's own palette to look correct, so the bug hides completely until
 * somebody switches to a light theme and finds white text on a white page.
 *
 * @param {HTMLElement} panel The tab panel.
 * @param {HTMLElement} node The node to show.
 */
function show(panel, node) {
    const root = appRootOf(panel);

    if (state.rendered?.parentElement === root) {
        state.rendered.replaceWith(node);
    } else {
        empty(root);
        root.appendChild(node);
    }

    state.rendered = node;
}

/**
 * Reko's application root inside a panel.
 *
 * @param {HTMLElement} panel The tab panel.
 * @returns {HTMLElement} The root, or the panel itself before the injector has made one.
 */
function appRootOf(panel) {
    // The injector owns this element and re-appends it on every pass, so mounting inside a temporary
    // one would only buy a frame of the same problem.
    return panel.querySelector('[data-reko="app"]') ?? panel;
}

/**
 * Shows a dismissible warning above the current view.
 *
 * @param {string} message The warning.
 */
export function showBanner(message) {
    const panel = document.querySelector('[data-reko="panel"]');
    if (!panel || panel.querySelector('.rekoBanner')) {
        return;
    }

    const banner = el('div.rekoBanner', null, [
        el('span', { text: message }),
        el('button', {
            type: 'button',
            text: '×',
            attrs: { 'aria-label': 'Dismiss' },
            on: {
                click: () => banner.remove()
            }
        })
    ]);

    // Inside the root, like everything else: the banner reads --reko-text and --reko-border, and a
    // banner with neither is a black rectangle with black writing on it.
    appRootOf(panel).prepend(banner);
}

/**
 * Refreshes request badges for the cards currently indexed.
 *
 * @returns {Promise<void>} Resolves when done.
 */
export async function refreshVisibleBadges() {
    if (!state.config?.seerrEnabled || state.cardsById.size === 0) {
        return;
    }

    const panel = document.querySelector('[data-reko="panel"]');
    if (!panel) {
        return;
    }

    await refreshBadges([...state.cardsById.values()], () => void renderRoute(panel));
}

/**
 * A full-panel error state.
 *
 * @param {string} title The heading.
 * @param {string} message The body.
 * @returns {HTMLElement} The node.
 */
function fatal(title, message) {
    return el('div.rekoNotice.is-error', null, [
        el('h2', { text: title }),
        el('p', { text: message })
    ]);
}

/**
 * The "add a TMDB key" state, shown when Reko is installed but unconfigured.
 *
 * @param {Object} bootstrap The bootstrap payload.
 * @returns {HTMLElement} The node.
 */
function notConfigured(bootstrap) {
    const node = el('div.rekoNotice', null, [
        el('h2', { text: `${state.config.tabLabel} needs a TMDB key` }),
        el('p', {
            text: 'Add a TMDB API Read Access Token in Dashboard > Plugins > Reko > Settings, then reload this page.'
        })
    ]);

    if (bootstrap?.librarySize) {
        node.appendChild(
            el('p.rekoNoticeMeta', {
                text: `${bootstrap.librarySize.toLocaleString()} library items are indexed and ready to match.`
            })
        );
    }

    return node;
}

/**
 * A non-fatal notice.
 *
 * @param {string} message The message.
 * @returns {HTMLElement} The node.
 */
function notice(message) {
    return el('div.rekoNotice', null, [el('p', { text: message })]);
}
