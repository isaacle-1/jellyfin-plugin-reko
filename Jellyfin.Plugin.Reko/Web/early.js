/**
 * Reko's pre-boot script.
 *
 * This is injected as the very first element in `<head>`, as a classic blocking script rather than a
 * deferred module. That placement is the whole point of the file.
 *
 * jellyfin-web's home route reads `?tab=N` from the URL and calls `emby-tabs.selectedIndex(N)` when
 * it mounts. When no tab button carries that index, `emby-tabs` dereferences an undefined tab and
 * throws inside Jellyfin's own code, which aborts its `onResume` and leaves the Home tab empty. On a
 * page reload while the Reko tab is open, the DOM has been rebuilt from scratch and no Reko button
 * exists yet, so the URL is momentarily invalid no matter how the tab was reached.
 *
 * Every jellyfin-web bundle is `defer` in `<head>`, and the router is created while they evaluate.
 * A module appended before `</body>` therefore runs too late to influence what the router reads, no
 * matter how fast its first statement is. A classic, non-deferred script in `<head>` executes during
 * parsing, before the deferred list is processed, which is the only reliable way to get ahead of it.
 *
 * It is a separate served file rather than an inline script so that a site with a strict
 * Content-Security-Policy does not silently lose the fix.
 *
 * What it does: if the URL asks for a tab that nothing backs, remove the parameter and remember it.
 * Reko then re-applies the selection through the tab strip once its own button exists.
 */
(function () {
    'use strict';

    try {
        var hash = window.location.hash;

        if (!hash || hash.length < 2) {
            return;
        }

        var questionMark = hash.indexOf('?');
        if (questionMark === -1) {
            return;
        }

        var path = hash.slice(1, questionMark);
        var query = hash.slice(questionMark + 1);
        var params = new URLSearchParams(query);
        var raw = params.get('tab');

        if (raw === null) {
            return;
        }

        var requested = parseInt(raw, 10);
        if (!Number.isFinite(requested) || requested < 0) {
            return;
        }

        // Only Home (0) and Favorites (1) exist before Reko has run. Anything higher is a tab that
        // some plugin has yet to create, and the safe action is to defer the selection rather than
        // let Jellyfin dereference a button that is not there.
        if (requested > 1) {
            window.__rekoPendingTab = requested;
        }

        params.delete('tab');
        var remaining = params.toString();
        var next = '#' + path + (remaining ? '?' + remaining : '');

        window.history.replaceState(
            window.history.state,
            '',
            window.location.pathname + window.location.search + next
        );
    } catch (error) {
        // Never let this break the page it is protecting.
        if (window.console) {
            window.console.warn('[Reko] Could not prepare the tab parameter.', error);
        }
    }
})();
