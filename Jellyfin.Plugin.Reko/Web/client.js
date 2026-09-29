/**
 * Reko's entry point.
 *
 * This module is injected into `/web/index.html` by the server's `ScriptInjectionStartupFilter`,
 * before any of jellyfin-web's own bundles have finished booting. From there it:
 *
 *   1. waits for the Jellyfin API client to exist,
 *   2. installs a MutationObserver that re-injects the tab whenever the home page is (re)built,
 *   3. re-renders the view on every hash change.
 *
 * The observer is the load-bearing part. jellyfin-web's `maintabsmanager.setTabs` rebuilds the whole
 * tab bar with `innerHTML` on every home page render, which deletes anything Reko injected, and
 * React unmounts and remounts the home route on every navigation. Without something that re-checks,
 * the tab would appear once and then vanish.
 *
 * Everything here is defensive. If any of it fails, Jellyfin is left exactly as it was.
 */

import { createLogger } from './js/utils.js';
import * as inject from './js/inject.js';
import * as app from './js/app.js';
import * as router from './js/router.js';

const log = createLogger();

/**
 * Traces Reko's decisions, so "why is my tab not there?" is answerable without a debugger.
 *
 * Off unless `rekoDebug` is set in local storage, which is a deliberate opt-in: an always-on trace
 * would flood a shared console on every navigation.
 *
 * @param {...unknown} args The values to log.
 */
function trace(...args) {
    log.debug(...args);
}

/** Coalesces the injection pass onto one animation frame. */
let scheduled = false;

/** The panel the application is currently mounted on. */
let mountedPanel = null;

/**
 * A tab index the URL asked for that no tab button backs yet.
 *
 * @type {number|null}
 */
let deferredTab = null;

/**
 * Picks up a tab selection that Web/early.js deferred.
 *
 * early.js runs during parsing, before jellyfin-web's deferred bundles create the hash router, and
 * removes a `?tab=N` that no button backs so the router never sees it. This re-applies the selection
 * once Reko's own button exists.
 */
function adoptDeferredTab() {
    if (deferredTab === null && typeof window.__rekoPendingTab === 'number') {
        deferredTab = window.__rekoPendingTab;
        window.__rekoPendingTab = null;
    }
}

/**
 * Waits for the Jellyfin API client.
 *
 * Reko is injected before the app boots, so the client that carries the access token does not exist
 * yet. Polling is used rather than an event because jellyfin-web exposes no "client ready" hook.
 *
 * @param {number} [timeoutMs] How long to wait before giving up.
 * @returns {Promise<boolean>} True when the client appeared.
 */
function waitForApiClient(timeoutMs = 30000) {
    return new Promise((resolve) => {
        if (window.ApiClient) {
            resolve(true);
            return;
        }

        const started = Date.now();
        const timer = setInterval(() => {
            if (window.ApiClient) {
                clearInterval(timer);
                resolve(true);
                return;
            }

            if (Date.now() - started > timeoutMs) {
                clearInterval(timer);
                log.warn('Gave up waiting for the Jellyfin API client. Reko will not load.');
                resolve(false);
            }
        }, 120);
    });
}

/**
 * The single injection and render pass.
 *
 * Idempotent, and never throws: a failure here must not take Jellyfin's home page down.
 */
function pass() {
    scheduled = false;

    try {
        adoptDeferredTab();

        const route = router.parse();
        if (!route.active) {
            trace('pass: not on the home route, ignoring');
            return;
        }

        const panel = inject.ensure({ label: app.tabLabel() });
        if (!panel) {
            trace('pass: the home page is not ready yet');
            return;
        }

        if (panel !== mountedPanel) {
            mountedPanel = panel;
            trace(`pass: tab ready on index ${inject.currentTabIndex()}`);
        }

        // Re-apply the requested selection on every pass. The home page is rebuilt on each
        // navigation, which resets the active panel, so the pass has to put it back.
        //
        // A tab index in the URL is honoured whichever way it got there: `deferredTab` covers a hard
        // load, where early.js stripped a `?tab=N` that no button backed yet, and the live hash
        // covers everything after that, including a click on Reko's own button and the browser's
        // back and forward. An index that is not Reko's is left alone, so `?tab=1` still reaches
        // Favorites and another plugin's tab is never hijacked.
        const requested = deferredTab !== null ? deferredTab : route.tabIndex;
        if (requested === inject.currentTabIndex() && inject.selectTab(requested)) {
            if (deferredTab !== null) {
                deferredTab = null;

                // Put the tab index back in the URL now that a button backs it, so a reload or a
                // shared link reopens on the Reko tab. replaceState, so it is not a history entry.
                router.replace({ tabIndex: inject.currentTabIndex() });
            }
        }

        void app.start(panel);
    } catch (error) {
        log.error('The Reko pass failed. Jellyfin is unaffected.', error);
    }
}

/**
 * Coalesces calls to {@link pass} into a single timeout.
 *
 * A timeout rather than requestAnimationFrame. Reko is injected into a page it does not control, and
 * rAF is paused whenever the renderer decides the page is not being painted: a background tab, a
 * minimised window, an unfocused TV client. Using it for the injection pass means the tab silently
 * fails to appear in exactly the situations where a user has most reason to wonder why.
 */
function schedule() {
    if (scheduled) {
        return;
    }

    scheduled = true;
    setTimeout(pass, 0);
}

/**
 * Whether a mutation could have removed or replaced Reko's tab.
 *
 * Deliberately permissive. A narrow filter looks like an optimisation, but the cost of a false
 * negative is a missing tab that only the slow safety net can repair, and the cost of a false
 * positive is one coalesced pass that returns in microseconds when there is nothing to do. React
 * rebuilds the home page subtree on every navigation and `maintabsmanager` rewrites the tab strip
 * with `innerHTML`, and neither announces itself in a way worth pattern-matching against.
 *
 * @param {Node} node The added or removed node.
 * @returns {boolean} True when the pass is worth running.
 */
function isInteresting(node) {
    if (node?.nodeType !== Node.ELEMENT_NODE) {
        // Text and comment nodes cannot carry the structure, but their parent might.
        return Boolean(node?.parentElement && node.parentElement.nodeType === Node.ELEMENT_NODE);
    }

    return true;
}

/**
 * Watches for the home page being rebuilt, and for navigation.
 */
function watch() {
    const observer = new MutationObserver((records) => {
        for (const record of records) {
            if (record.type !== 'childList') {
                continue;
            }

            for (const node of record.addedNodes) {
                if (isInteresting(node)) {
                    schedule();
                    return;
                }
            }

            for (const node of record.removedNodes) {
                if (node === mountedPanel || isInteresting(node)) {
                    schedule();
                    return;
                }
            }
        }
    });

    // Child lists only. Watching attributes would fire on every focus, hover and scroll-driven class
    // change jellyfin-web performs.
    observer.observe(document.body, { childList: true, subtree: true });

    // jellyfin-web's Modern layout navigates with the History API. `pushState` and `replaceState`
    // fire neither `hashchange` nor `popstate`, so listening for those alone means the client is
    // never told that a navigation happened and the tab never appears. Wrapping the two methods is
    // the only way to observe History API navigation from outside the app.
    for (const method of ['pushState', 'replaceState']) {
        const original = window.history[method];
        if (typeof original !== 'function') {
            continue;
        }

        window.history[method] = function patchedHistoryMethod(...args) {
            const result = original.apply(this, args);
            schedule();
            return result;
        };
    }

    // Still needed for the Legacy layout and for the browser's own back and forward.
    window.addEventListener('hashchange', schedule);
    window.addEventListener('popstate', schedule);
    window.addEventListener('pageshow', schedule);

    // A slow safety net. If a future jellyfin-web release changes the home page's structure enough
    // that the observer no longer fires, this still puts the tab back, at a cost of one selector
    // every two seconds. The check is "no panel on screen" rather than "the panel I remember is
    // missing", because the first pass legitimately runs before the home page has rendered and both
    // values are then null, which would compare equal and never re-run.
    setInterval(() => {
        if (!router.parse().active) {
            return;
        }

        const panel = inject.panel();

        if (!panel || !panel.isConnected) {
            schedule();
        }
    }, 2000);
}

/**
 * Boots Reko.
 */
async function boot() {
    const ready = await waitForApiClient();
    if (!ready) {
        return;
    }

    schedule();
    watch();
}

void boot();
