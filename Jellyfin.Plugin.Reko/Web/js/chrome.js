/**
 * Keeps Reko's sticky header clear of Jellyfin's own header.
 *
 * Jellyfin's header is `position: fixed`, `z-index: 1100`, and opaque once the page has scrolled.
 * Reko's header is `position: sticky` inside the scroll container. Both stick to the top of the
 * viewport, so scrolling pushes Reko's underneath Jellyfin's and the tab's title and search field
 * disappear — which reads as the tab having broken.
 *
 * CSS cannot measure a sibling, so the offset is measured here and published as `--reko-chrome-h`,
 * which the stylesheet uses as the sticky `top`.
 *
 * Two layouts, two headers, and neither can be assumed to exist:
 *
 *   Modern  `.MuiAppBar-root`, fixed, 48px tall.
 *   Legacy  `.skinHeader`, also fixed, and taller: it carries the title, the tab strip and the user
 *            menu. TV clients only ever use this one.
 *
 * The Modern layout keeps the legacy header mounted but collapsed to zero height, so "the first one
 * in the document" is not a usable answer. The tallest visible candidate is used instead.
 *
 * ## The header's height is not the same as how far down the screen it paints
 *
 * A theme can draw its own header decoration. Abyss — the most popular Jellyfin theme — puts a
 * `position: fixed` 10rem gradient with a `backdrop-filter: blur` on the app bar, and Abyss's own
 * legacy rules do the same thing with a 8em one. That decoration is 148px tall on a bar that is only
 * 64px, and a `backdrop-filter` blurs everything painted behind it: a Reko header sticking at 64 is
 * inside it, smeared and unclickable, however far down the page has scrolled.
 *
 * So the measurement is not the header's height but the depth at which Jellyfin's chrome stops
 * painting, taken as the header's own bottom edge and the bottom edge of any pseudo-element it
 * carries that is positioned. `::before` and `::after` are the only pseudo-elements reachable
 * without a selector hack, and a header decoration is essentially always one of the two.
 *
 * Out-ranking the decoration with a higher `z-index` would also work, and is the wrong answer: it
 * puts Reko's header above Jellyfin's navigation drawer, which sits at 1099 and is drawn over the
 * page content. Sitting below the decoration instead draws the same way the theme already looks.
 */

/** The CSS custom property the stylesheet reads. */
const OFFSET_PROPERTY = '--reko-chrome-h';

/** The depth most recently published, so the property is only written when it changes. */
let published = null;

/**
 * The set of roots the depth was last written to.
 *
 * Jellyfin rebuilds the home route on every navigation, which detaches Reko's root along with the
 * panel that held it, and a fresh root is created in its place. Comparing depths alone would then
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
 * How far down the viewport Jellyfin's chrome paints.
 *
 * @param {HTMLElement} header The header element.
 * @returns {number} The depth in pixels, never less than the header's own height.
 */
function paintedDepth(header) {
    const rect = header.getBoundingClientRect();
    let deepest = rect.bottom;

    for (const pseudo of ['::before', '::after']) {
        deepest = Math.max(deepest, pseudoDepth(header, pseudo, rect.top));
    }

    return Math.max(0, Math.round(deepest));
}

/**
 * How far down the viewport one pseudo-element of the header reaches.
 *
 * @param {HTMLElement} header The header element.
 * @param {string} pseudo The pseudo-element selector, `::before` or `::after`.
 * @param {number} headerTop The header's own top edge, for resolving an absolutely positioned box.
 * @returns {number} The bottom edge in viewport coordinates, or zero when there is nothing painted.
 */
function pseudoDepth(header, pseudo, headerTop) {
    const style = window.getComputedStyle(header, pseudo);

    // A pseudo-element that was never styled computes to the element's own style, so `content` is the
    // only reliable evidence that there is anything there at all.
    if (!style || style.content === 'none' || style.content === 'normal') {
        return 0;
    }

    if (style.position !== 'fixed' && style.position !== 'absolute') {
        return 0;
    }

    const height = Number.parseFloat(style.height);

    if (Number.isFinite(height) && height > 0) {
        const top = Number.parseFloat(style.top);
        const offset = Number.isFinite(top) ? top : 0;

        // Fixed is viewport-relative, absolute is relative to the header's padding box, which for a
        // fixed header is the same place on screen.
        return (style.position === 'fixed' ? offset : headerTop + offset) + height;
    }

    // A pseudo sized by `top` and `bottom` rather than a height. Only meaningful when fixed: an
    // absolute `bottom` is measured from the header, not the viewport.
    if (style.position === 'fixed') {
        const bottom = Number.parseFloat(style.bottom);
        if (Number.isFinite(bottom)) {
            return window.innerHeight - bottom;
        }
    }

    return 0;
}

/**
 * Publishes the current chrome depth, if it has changed.
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
    const depth = header ? paintedDepth(header) : 0;

    const sameDepth = depth === published;
    const sameRoots = roots.length === publishedOn.length
        && roots.every((root, index) => root === publishedOn[index]);

    if (sameDepth && sameRoots) {
        return;
    }

    published = depth;
    publishedOn = roots;

    for (const root of roots) {
        root.style.setProperty(OFFSET_PROPERTY, `${depth}px`);
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
