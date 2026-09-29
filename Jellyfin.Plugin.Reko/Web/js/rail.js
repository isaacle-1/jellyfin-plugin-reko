/**
 * Horizontally scrolling rails.
 *
 * A rail is a scrolling strip with edge fades and arrow buttons, not a grid. Grid layouts lose the
 * "there is more to the right" signal, which is most of what makes a streaming home page feel like a
 * streaming home page.
 *
 * Scrolling is done with `scrollBy` on the strip rather than with a carousel library, so native
 * touch, trackpad, keyboard and remote interactions all keep working. The arrows appear only when
 * there is actually somewhere to scroll, which is checked on scroll and on resize.
 */

import { el, empty } from './utils.js';
import { createCard } from './card.js';

/**
 * Builds one rail.
 *
 * @param {Object} rail The rail payload.
 * @param {Object} config The client configuration.
 * @param {Object} actions Handlers passed through to each card.
 * @returns {HTMLElement} The rail element.
 */
export function createRail(rail, config, actions) {
    const strip = el('div.rekoRailStrip', { attrs: { role: 'list' } });

    rail.items.forEach((card, position) => {
        const node = createCard(card, config, actions, rail.ranked ? position + 1 : null);
        node.setAttribute('role', 'listitem');
        strip.appendChild(node);
    });

    const section = el('section.rekoRail', {
        attrs: {
            'data-rail': rail.id,
            'aria-label': rail.title
        }
    });

    section.appendChild(
        el('div.rekoRailHeader', null, [
            el('h2.rekoRailTitle', { text: rail.title }),
            rail.personalized ? el('span.rekoRailTag', { text: 'For you' }) : null
        ])
    );

    const viewport = el('div.rekoRailViewport');
    viewport.appendChild(strip);

    const previous = arrowButton('rekoRailArrow.is-prev', '‹', 'Scroll left');
    const next = arrowButton('rekoRailArrow.is-next', '›', 'Scroll right');

    previous.addEventListener('click', () => scrollStrip(strip, -1));
    next.addEventListener('click', () => scrollStrip(strip, 1));

    viewport.append(previous, next);

    section.appendChild(viewport);
    wireArrows(viewport, strip, previous, next);

    return section;
}

/**
 * Builds a placeholder rail, shown while a row loads or when it fails.
 *
 * @param {string} title The rail title.
 * @param {string} [message] An optional message.
 * @returns {HTMLElement} The element.
 */
export function createRailPlaceholder(title, message = null) {
    return el('section.rekoRail.is-loading', null, [
        el('div.rekoRailHeader', null, [el('h2.rekoRailTitle', { text: title })]),
        message
            ? el('div.rekoRailMessage', { text: message })
            : el('div.rekoRailSkeleton', null, Array.from({ length: 8 }, () => el('div.rekoSkeletonCard')))
    ]);
}

/**
 * Builds one of the two scrolling arrows.
 *
 * @param {string} className The class list.
 * @param {string} glyph The glyph.
 * @param {string} label The accessible label.
 * @returns {HTMLElement} The button.
 */
function arrowButton(className, glyph, label) {
    return el('button.rekoRailArrow', {
        type: 'button',
        class: className,
        text: glyph,
        attrs: { 'aria-label': label, tabindex: '-1' }
    });
}

/**
 * Scrolls a strip by roughly one viewport.
 *
 * @param {HTMLElement} strip The strip.
 * @param {number} direction -1 for left, 1 for right.
 */
function scrollStrip(strip, direction) {
    const amount = Math.max(240, Math.round(strip.clientWidth * 0.85));
    strip.scrollBy({ left: amount * direction, behavior: 'smooth' });
}

/**
 * Shows an arrow only when there is somewhere to go, and keeps it that way.
 *
 * @param {HTMLElement} viewport The viewport.
 * @param {HTMLElement} strip The strip.
 * @param {HTMLElement} previous The left arrow.
 * @param {HTMLElement} next The right arrow.
 */
function wireArrows(viewport, strip, previous, next) {
    const update = () => {
        const max = strip.scrollWidth - strip.clientWidth;
        // A one-pixel tolerance: fractional layout widths otherwise leave an arrow showing forever.
        const atStart = strip.scrollLeft <= 1;
        const atEnd = strip.scrollLeft >= max - 1;

        previous.classList.toggle('is-hidden', atStart || max <= 0);
        next.classList.toggle('is-hidden', atEnd || max <= 0);
    };

    strip.addEventListener('scroll', update, { passive: true });

    const observer = new ResizeObserver(update);
    observer.observe(strip);
    observer.observe(viewport);

    // The strip is populated after this runs on the first paint, so defer one frame.
    requestAnimationFrame(update);
}

/**
 * Fills a rail placeholder with real cards, replacing the skeleton.
 *
 * @param {HTMLElement} section The rail section.
 * @param {Object} rail The rail payload.
 * @param {Object} config The client configuration.
 * @param {Object} actions Handlers passed through to each card.
 */
export function hydrateRail(section, rail, config, actions) {
    empty(section);

    section.classList.remove('is-loading');
    section.setAttribute('data-rail', rail.id);
    section.setAttribute('aria-label', rail.title);

    const strip = el('div.rekoRailStrip', { attrs: { role: 'list' } });
    rail.items.forEach((card, position) => {
        const node = createCard(card, config, actions, rail.ranked ? position + 1 : null);
        node.setAttribute('role', 'listitem');
        strip.appendChild(node);
    });

    section.appendChild(
        el('div.rekoRailHeader', null, [
            el('h2.rekoRailTitle', { text: rail.title }),
            rail.personalized ? el('span.rekoRailTag', { text: 'For you' }) : null
        ])
    );

    const viewport = el('div.rekoRailViewport');
    viewport.appendChild(strip);

    const previous = arrowButton('rekoRailArrow.is-prev', '‹', 'Scroll left');
    const next = arrowButton('rekoRailArrow.is-next', '›', 'Scroll right');
    previous.addEventListener('click', () => scrollStrip(strip, -1));
    next.addEventListener('click', () => scrollStrip(strip, 1));
    viewport.append(previous, next);

    section.appendChild(viewport);
    wireArrows(viewport, strip, previous, next);
}
