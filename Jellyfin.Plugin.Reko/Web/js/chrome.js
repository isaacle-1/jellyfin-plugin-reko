/**
 * Keeps Reko's sticky header clear of Jellyfin's own header.
 *
 * Jellyfin's header is `position: fixed` and, once the page has scrolled, opaque. Reko's header is
 * `position: sticky` inside that scroll container. Both stick to the top of the viewport, so
 * whichever one the browser resolves last wins — and Jellyfin's sits at `z-index: 1100` against
 * Reko's 4. The result is that scrolling pushes the Reko title and the search field underneath the
 * app bar and they simply disappear, which reads as the tab having broken.
 *
 * CSS cannot measure a sibling, so the offset is measured here and published as
 * `--reko-chrome-h`, which the stylesheet uses as the sticky `top`. The header then parks directly
 * below Jellyfin's rather than behind it.
 *
 * Two layouts, two headers, and neither can be assumed to exist:
 *
 *   Modern  `.MuiAppBar-root`, fixed, 48px tall, opaque once the page scrolls.
 *   Legacy  `.skinHeader`, also fixed, and taller: it carries the title, the tab strip and the user
 *            menu. TV clients only ever use this one.
 *
 * The Modern layout keeps the legacy header mounted but collapsed to zero height, so "the first one
 * in the document" is not a usable answer. The tallest visible candidate is used instead.
 */

/** The CSS custom property the stylesheet reads. */
const OFFSET_PROPERTY = '--reko-chrome-h';

/** The height most recently published, so the property is only written when it changes. */
let published = null;

/**
 * The set of roots the height was last written to.
 *
 * Jellyfin rebuilds the home route on every navigation, which detaches Reko's root along with the
 * panel that held it, and a fresh root is created in its place. Comparing heights alone would then
 * wrongly conclude there was nothing to do and leave the new root with no offset at all — which is
 * the same bug as never having measured it in the first place.
 */
let publishedOn = [];

/** The attribute every Reko root carries, so all of them can be found. */
const ROOT_SELECTOR = '[data-reko="app"]';

/** Whether the listeners are already attached. */
let watching = false;

/** A pending write, coalesced onto the next task. */
let pending = null;

/**
 * The header Jellyfin is currently drawing across the top of the viewport.
 *
 * @returns {HTMLElement|null} The header, or null when there is none on screen.
 */
function chromeElement() {
    const candidates = [
        ...document.querySelectorAll('.MuiAppBar-root'),
        ...document.querySelectorAll('.skinHeader')
    ];

    let tallest = null;

    for (const candidate of candidates) {
        if (!isPainted(candidate)) {
            continue;
        }

        const rect = candidate.getBoundingClientRect();

        // The Modern layout keeps the legacy header in the DOM at zero height. Measuring first means
        // a collapsed header never wins over the real one, whatever the document order happens to be.
        if (rect.height <= 0 || rect.bottom <= 0) {
            continue;
        }

        if (!tallest || rect.height > tallest.getBoundingClientRect().height) {
            tallest = candidate;
        }
    }

    return tallest;
}

/**
 * Whether an element is actually visible, rather than merely present in the document.
 *
 * @param {HTMLElement} node The element.
 * @returns {boolean} True when it takes up space.
 */
function isPainted(node) {
    if (node.getClientRects().length === 0) {
        return false;
    }

    const style = window.getComputedStyle(node);
    return style.display !== 'none' && style.visibility !== 'hidden' && style.opacity !== '0';
}

/**
 * Publishes the current header height, if it has changed.
 *
 * Every root is written, not just the first. Jellyfin keeps the previous home page mounted behind the
 * one on screen, so there are routinely two Reko roots in the document and which one is visible is
 * not knowable from here. Writing to the first and stopping leaves whichever is on screen with the
 * stylesheet's fallback offset, which is the Modern layout's 48px — so on the Legacy layout, whose
 * header is 110px tall, the Reko header parks under Jellyfin's tab strip and disappears again.
 */
function sync() {
    pending = null;

    const roots = Array.from(document.querySelectorAll(ROOT_SELECTOR));
    if (roots.length === 0) {
        return;
    }

    const header = chromeElement();
    const height = header ? Math.round(header.getBoundingClientRect().height) : 0;

    const sameHeight = height === published;
    const sameRoots = roots.length === publishedOn.length
        && roots.every((root, index) => root === publishedOn[index]);

    if (sameHeight && sameRoots) {
        return;
    }

    published = height;
    publishedOn = roots;

    for (const root of roots) {
        root.style.setProperty(OFFSET_PROPERTY, `${height}px`);
    }
}

/**
 * Measures on the next task, coalescing any number of triggers into one write.
 *
 * Scroll fires this on every frame and `getBoundingClientRect` forces a layout, so doing the work
 * synchronously in the handler is a jank source on a page with forty rails. A timeout is used
 * rather than `requestAnimationFrame` because this has to be correct on a tab that is not being
 * painted — a TV client, or a browser window in the background, throttles frames to nothing and the
 * header would sit in the wrong place indefinitely.
 */
function schedule() {
    if (pending !== null) {
        return;
    }

    pending = setTimeout(sync, 100);
}

/**
 * Starts keeping `--reko-chrome-h` up to date. Safe to call on every render.
 */
export function watchChrome() {
    sync();

    if (watching) {
        return;
    }

    watching = true;

    // `capture`, because Jellyfin's own scroller is not the one that scrolls: the document does.
    // Listening in the bubble phase at the window would still see it, but capture is explicit about
    // wanting it and costs nothing here.
    window.addEventListener('scroll', schedule, { passive: true, capture: true });
    window.addEventListener('resize', schedule, { passive: true });
}
