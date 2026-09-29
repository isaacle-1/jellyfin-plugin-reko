/**
 * Injects the Reko tab into the Jellyfin home page.
 *
 * There is no plugin API for this. The tab list is hard-coded in jellyfin-web's home route
 * (`getTabs()` returns exactly Home and Favorites) and there is no client-side plugin registry, so
 * the tab has to be added to the DOM the client itself builds.
 *
 * Two layouts, two insertion points, one tab:
 *
 *   Modern (the default since Jellyfin 12.0) hides the legacy `.skinHeader` with `display: none`.
 *   In 12.1 the visible home tabs are MUI buttons in the app bar, so Reko clones the Favorites
 *   button and sits directly after it. 12.0's Modern layout used the drawer for the same purpose,
 *   and a closed drawer mounts no list, so that insertion point is kept as well and simply no-ops
 *   when there is no drawer on the page.
 *
 *   Legacy and TV render a real, visible tab strip. Reko appends a `.emby-tab-button` to
 *   `.emby-tabs-slider` and a matching `.tabContent` panel.
 *
 * The panel contract is positional: `maintabsmanager` resolves the visible panel with
 * `querySelectorAll('.tabContent')[selectedIndex]`, so the panel must be appended in index order and
 * a button must exist for every index or `emby-tabs` throws when it deep-links to a tab that has
 * none. Both of those are handled below.
 *
 * `maintabsmanager.setTabs` also rewrites the whole tab bar with `innerHTML` whenever the home page
 * re-renders, which deletes anything injected. So `ensure()` is idempotent and re-run from a
 * MutationObserver, and every failure path is silent: a web client change must degrade to "Reko is
 * not on this page", never to a broken Jellyfin.
 */

import { el, empty, isVisible, createLogger } from './utils.js';

/**
 * The class list jellyfin-web puts on a home page tab panel.
 */
const PANEL_CLASS = 'tabContent pageTabContent';

/**
 * The selector form of {@link PANEL_CLASS}.
 *
 * Deliberately separate: interpolating the class *value* into a selector produces
 * `.tabContent pageTabContent`, which is a descendant combinator and matches nothing at all.
 */
const PANEL_SELECTOR = '.tabContent.pageTabContent';

const REKO_ATTR = 'data-reko';

const log = createLogger();

/** The live tab index, resolved by scanning rather than assumed. */
let tabIndex = 2;

/** The label shown on the tab and in the drawer. */
let tabLabel = 'Reko';

/**
 * The Reko application root, created once and reused.
 *
 * @returns {HTMLElement} The root element.
 */
function appRoot() {
    let root = document.getElementById('rekoApp');
    if (!root) {
        root = el('div.rekoApp', { id: 'rekoApp', attrs: { [REKO_ATTR]: 'app' } });
    }

    return root;
}

/**
 * Finds the home page roots that are actually on screen.
 *
 * Both the Modern and the Legacy home components can be in the DOM at once during a route
 * transition, and one of them may be inside the hidden legacy header. Picking a hidden root would
 * mean injecting into a panel the user can never reach.
 *
 * @returns {HTMLElement[]} The visible home page containers.
 */
function visibleHomeRoots() {
    const panels = Array.from(
        document.querySelectorAll(`${PANEL_SELECTOR}[data-index="0"]`)
    );

    const roots = [];
    for (const panel of panels) {
        const root = panel.parentElement;
        if (root && isVisible(root) && !roots.includes(root)) {
            roots.push(root);
        }
    }

    return roots;
}

/**
 * Finds the visible tab slider, if there is one.
 *
 * @returns {HTMLElement|null} The slider.
 */
function visibleSlider() {
    const sliders = Array.from(document.querySelectorAll('.emby-tabs-slider'));
    return sliders.find(isVisible) ?? null;
}

/**
 * Allocates the next free tab index.
 *
 * Other tab plugins exist and some of them hard-code an index, so the highest index already in use
 * is the only safe starting point. Scanning also means Reko does not overwrite a third-party tab.
 *
 * @param {HTMLElement|null} scope The element to scan.
 * @returns {number} The index to use.
 */
function nextFreeIndex(scope) {
    let max = 1; // Home is 0 and Favorites is 1 in every Jellyfin release that has a tab strip.

    for (const node of (scope ?? document).querySelectorAll('[data-index]')) {
        // Reko's own panel is skipped deliberately. Counting it would make every pass allocate one
        // index higher than the last, so the panel would stay on the index it was created with while
        // the injector believed it had moved, and every tab selection would address a panel that
        // does not exist.
        if (node.getAttribute(REKO_ATTR) === 'panel') {
            continue;
        }

        const value = Number.parseInt(node.getAttribute('data-index') ?? '', 10);
        if (Number.isFinite(value) && value > max) {
            max = value;
        }
    }

    return max + 1;
}

/**
 * The Netflix-style glyph used for the drawer entry and the tab.
 *
 * The Modern drawer uses Material icons, which are React components, so a cloned entry has to have
 * its `<svg>` swapped rather than a class added.
 *
 * @returns {SVGElement} The icon.
 */
function rekoIcon() {
    const ns = 'http://www.w3.org/2000/svg';
    const svg = document.createElementNS(ns, 'svg');
    svg.setAttribute('viewBox', '0 0 24 24');
    svg.setAttribute('aria-hidden', 'true');
    svg.setAttribute('focusable', 'false');
    svg.style.width = '1em';
    svg.style.height = '1em';
    svg.style.fill = 'currentColor';

    // A play triangle inside a rounded rectangle: reads as "streaming" rather than as another
    // generic folder, and it matches the metaphor of the rest of the tab.
    const rect = document.createElementNS(ns, 'rect');
    rect.setAttribute('x', '2.5');
    rect.setAttribute('y', '4.5');
    rect.setAttribute('width', '19');
    rect.setAttribute('height', '15');
    rect.setAttribute('rx', '2.5');
    rect.setAttribute('fill', 'none');
    rect.setAttribute('stroke', 'currentColor');
    rect.setAttribute('stroke-width', '1.7');

    const path = document.createElementNS(ns, 'path');
    path.setAttribute('d', 'M10.4 8.7v6.6l5.6-3.3z');

    svg.append(rect, path);
    return svg;
}

/**
 * The Modern layout's own home tabs, as rendered in Jellyfin 12.1.
 *
 * Home and Favorites are plain MUI buttons in the app bar, not entries in the drawer and not the
 * legacy `.emby-tab-button` strip: Jellyfin 12.1 keeps the legacy header mounted but hidden and
 * renders the modern one beside it. The Favorites link is found by its `href` rather than by its
 * position, so Reko keeps sitting directly after it if the order ever changes.
 *
 * @returns {HTMLElement|null} The Favorites link, or null when this layout is not in use.
 */
function modernFavoritesLink() {
    const bars = Array.from(document.querySelectorAll('.MuiAppBar-root')).filter(isVisible);
    const scope = bars.length ? bars : [document];

    for (const bar of scope) {
        const link = bar.querySelector?.('a.MuiButton-root[href*="tab=1"]');
        if (link) {
            return link;
        }
    }

    return null;
}

/**
 * Injects the Reko button into the Modern app bar, directly after Favorites.
 *
 * Cloning the existing markup is what makes the button look native: hover states, ripples, focus
 * rings, typography and spacing all come from the same MUI rules without Reko guessing at any of
 * them. The clone is a plain DOM node with no React handler attached, so the only thing that has to
 * work is its `href` — which is enough, because the Modern layout is a hash router and reads the tab
 * from the URL.
 *
 * @returns {boolean} True when a button is present afterwards.
 */
function ensureHeaderButton() {
    const favorites = modernFavoritesLink();
    if (!favorites) {
        return false;
    }

    const stack = favorites.parentElement;
    if (!stack) {
        return false;
    }

    const existing = stack.querySelector(`[${REKO_ATTR}="header"]`);
    if (existing) {
        return true;
    }

    const clone = favorites.cloneNode(true);
    clone.setAttribute(REKO_ATTR, 'header');
    clone.setAttribute('href', '#/home?tab=' + tabIndex);
    clone.setAttribute('aria-label', tabLabel);
    clone.removeAttribute('aria-current');

    // The button's content is an icon span followed by a bare text node, so it is rebuilt rather
    // than searched: the clone's own class list has already been copied, which is the part that
    // actually carries the native styling.
    const iconSlot = favorites.querySelector('.MuiButton-icon, .MuiButton-startIcon');
    empty(clone);

    if (iconSlot) {
        const slot = iconSlot.cloneNode(false);
        empty(slot).appendChild(rekoIcon());
        clone.appendChild(slot);
    } else {
        clone.appendChild(rekoIcon());
    }

    clone.appendChild(document.createTextNode(tabLabel));

    // A foreign sibling in a React-rendered stack is tolerated, and the observer re-inserts it if
    // React ever reconciles the bar away.
    stack.insertBefore(clone, favorites.nextSibling);
    return true;
}

/**
 * Injects a drawer entry after Favorites, for the layouts that use one.
 *
 * Jellyfin 12.0's Modern layout put Home and Favorites in the MUI drawer, and some third-party
 * themes do the same, so the drawer is still worth supporting. On 12.1 there is no drawer on the
 * home page and this is a no-op.
 *
 * @returns {boolean} True when an entry is present afterwards.
 */
function ensureDrawerEntry() {
    const anchors = Array.from(document.querySelectorAll('.MuiListItem-root a, .MuiButtonBase-root.MuiListItem-root'));

    const favorites = anchors.find((anchor) => {
        const href = anchor.getAttribute('href') ?? '';
        return href.includes('tab=1') || href.includes('tab%3D1');
    });

    if (!favorites) {
        return false;
    }

    const listItem = favorites.closest('.MuiListItem-root') ?? favorites;
    const list = listItem.parentElement;
    if (!list) {
        return false;
    }

    if (list.querySelector(`[${REKO_ATTR}="drawer"]`)) {
        return true;
    }

    const clone = listItem.cloneNode(true);
    clone.setAttribute(REKO_ATTR, 'drawer');
    clone.removeAttribute('selected');

    const link = clone.matches('.MuiListItem-root') ? clone : clone.querySelector('a');
    if (link) {
        link.setAttribute('href', '#/home?tab=' + tabIndex);
        link.removeAttribute('aria-current');

        const textNode = Array.from(link.querySelectorAll('*'))
            .reverse()
            .find((node) => node.children.length === 0 && node.textContent.trim() === 'Favorites');

        if (textNode) {
            textNode.textContent = tabLabel;
        }

        // Replace the heart icon with Reko's own glyph.
        const iconSlot = link.querySelector('.MuiListItemIcon-root, .MuiSvgIcon-root, svg');
        if (iconSlot) {
            const replacement = rekoIcon();
            if (iconSlot.classList.contains('MuiSvgIcon-root')) {
                iconSlot.replaceWith(replacement);
            } else {
                empty(iconSlot).appendChild(replacement);
            }
        }
    }

    // A foreign sibling in a React-rendered list is tolerated, and the observer re-inserts it if
    // React ever reconciles the list away.
    list.insertBefore(clone, listItem.nextSibling);
    return true;
}

/**
 * Injects the tab button when a visible tab strip exists, which is the Legacy and TV layouts.
 *
 * @param {HTMLElement} slider The visible slider.
 * @param {number} index The tab index to claim.
 * @returns {void}
 */
function ensureTabButton(slider, index) {
    if (slider.querySelector(`[${REKO_ATTR}="tab"]`)) {
        return;
    }

    const button = el('button.rekoTabButton.emby-tab-button', {
        type: 'button',
        text: '',
        attrs: {
            is: 'emby-button',
            [REKO_ATTR]: 'tab',
            'data-index': String(index),
            title: tabLabel
        }
    });

    button.appendChild(el('div.emby-button-foreground', { text: tabLabel }));
    slider.appendChild(button);

    // emby-button is a custom element; without this the label never renders.
    window.CustomElements?.upgradeSubtree?.(slider);

    // The tab scroller caches widths when it initialises, so a new button needs an explicit refresh or
    // it overlaps its neighbour until an unrelated resize happens.
    document.querySelector('[is="emby-tabs"]')?.refresh?.();
}

/**
 * Injects the Reko panel, or returns the existing one.
 *
 * The panel is appended last with the highest index, which is what keeps `maintabsmanager`'s
 * positional lookup (`querySelectorAll('.tabContent')[n]`) correct.
 *
 * This is also where the tab index is settled. An existing panel keeps the index it was created
 * with, rather than being re-indexed on every pass, because the index is baked into the URLs Reko
 * has already written: re-allocating it would orphan every deep link that is already in the user's
 * history and in their bookmarks.
 *
 * @param {HTMLElement} root The home page container.
 * @returns {HTMLElement} The panel.
 */
function ensurePanel(root) {
    let panel = root.querySelector(`[${REKO_ATTR}="panel"]`);

    if (panel) {
        const pinned = Number.parseInt(panel.getAttribute('data-index') ?? '', 10);
        if (Number.isFinite(pinned)) {
            tabIndex = pinned;
        }
    } else {
        tabIndex = nextFreeIndex(root);

        panel = el('div.rekoPanel', {
            // `class`, not `attrs.class`: el() appends a `class` property to whatever the tag
            // already put there, whereas an `attrs.class` entry overwrites the attribute outright
            // and the panel silently loses the one class its own stylesheet is written against.
            class: PANEL_CLASS,
            attrs: {
                [REKO_ATTR]: 'panel',
                'data-index': String(tabIndex)
            }
        });

        root.appendChild(panel);
    }

    if (!panel.contains(appRoot())) {
        panel.appendChild(appRoot());
    }

    return panel;
}

/**
 * Makes sure the Reko tab exists in the current layout.
 *
 * Safe to call as often as you like. Never throws.
 *
 * @param {Object} [options] Options.
 * @param {string} [options.label] The tab label.
 * @returns {HTMLElement|null} The Reko panel, or null when the tab could not be created.
 */
export function ensure(options = {}) {
    try {
        if (options.label) {
            tabLabel = options.label;
        }

        const roots = visibleHomeRoots();
        if (roots.length === 0) {
            log.debug(
                'no visible home page root yet. Panels:',
                document.querySelectorAll(`${PANEL_SELECTOR}[data-index="0"]`).length,
                'sliders:',
                document.querySelectorAll('.emby-tabs-slider').length
            );

            return null;
        }

        const root = roots[0];

        // Legacy and TV render a real, visible tab strip. Modern does not: it hides the strip along
        // with the rest of the legacy header and renders its own tabs in the app bar instead.
        //
        // The presence of a visible slider is used as the discriminator rather than a header check,
        // because there can be several `.skinHeader` elements in the DOM at once and "the first one"
        // is not reliably the one belonging to the page on screen.
        const slider = visibleSlider();
        const panel = ensurePanel(root);

        if (slider) {
            ensureTabButton(slider, tabIndex);
        } else {
            ensureHeaderButton();
            ensureDrawerEntry();
        }

        return panel;
    } catch (error) {
        // Never take Jellyfin's home page down because Reko could not inject.
        console.warn('[Reko] Could not inject the tab. Jellyfin will be left untouched.', error);
        return null;
    }
}

/**
 * The panel element, if it currently exists.
 *
 * @returns {HTMLElement|null} The panel.
 */
export function panel() {
    return document.querySelector(`[${REKO_ATTR}="panel"]`);
}

/**
 * The index Reko ended up on.
 *
 * @returns {number} The tab index.
 */
export function currentTabIndex() {
    return tabIndex;
}

/**
 * Whether a tab index is currently backed by a real tab button.
 *
 * @param {number} index The index.
 * @returns {boolean} True when a button exists.
 */
export function hasTabButton(index) {
    const slider = visibleSlider();
    if (!slider) {
        return false;
    }

    return [...slider.querySelectorAll('.emby-tab-button')].some(
        (button) => Number.parseInt(button.getAttribute('data-index') ?? '', 10) === index
    );
}

/**
 * Whether Reko's panel is the one currently on screen.
 *
 * Used to keep the Modern selection idempotent. Re-applying it on every pass is what survives the
 * home page being rebuilt, but re-applying it *unconditionally* means writing a class on a panel
 * that is already correct, and in the Modern layout that write is what the next pass reacts to.
 *
 * The Legacy and TV layouts do not consult this at all: there, the tab strip owns the selection and
 * is asked directly.
 *
 * @param {number} index The index to check.
 * @returns {boolean} True when the panel is active.
 */
export function isActive(index) {
    return (
        document
            .querySelector(`[${REKO_ATTR}="panel"][data-index="${index}"]`)
            ?.classList.contains('is-active') ?? false
    );
}

/**
 * Selects a tab on the home page.
 *
 * In the Legacy and TV layouts this goes through the tab strip, which is Jellyfin's own mechanism. In
 * the Modern layout there is no visible tab strip at all, so the active class is toggled directly on
 * the panels, which is the same thing `maintabsmanager` does and the same contract the home route
 * relies on.
 *
 * @param {number} index The tab index.
 * @returns {boolean} True when the selection was applied.
 */
export function selectTab(index) {
    const panel = document.querySelector(`[${REKO_ATTR}="panel"][data-index="${index}"]`);
    if (!panel) {
        return false;
    }

    // Legacy and TV: hand the selection to Jellyfin's own tab strip, which is the only thing that
    // knows how to hide the other panels. Whether it is called is decided by the strip's own
    // `selectedTabIndex`, not by the class on the panel: the two are not the same thing, the strip
    // is what decides, and a panel can carry `is-active` while the strip still believes in another
    // tab — which is exactly the state a re-render leaves behind, and exactly the state in which
    // both panels are on screen at once.
    const tabs = document.querySelector('[is="emby-tabs"]');
    if (tabs && typeof tabs.selectedIndex === 'function' && hasTabButton(index)) {
        if (tabs.selectedTabIndex !== index) {
            tabs.selectedIndex(index);
        }

        return true;
    }

    if (isActive(index)) {
        return true;
    }

    for (const sibling of document.querySelectorAll(PANEL_SELECTOR)) {
        sibling.classList.toggle('is-active', sibling === panel);
    }

    return true;
}
