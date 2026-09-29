/**
 * The rotating hero billboard.
 *
 * Full-bleed backdrop, title, meta line, synopsis and two actions, auto-advancing. The title uses
 * TMDB's logo artwork when it exists, because a hero that says "The Matrix Reloaded" in a 64px
 * heading looks like a settings page, and TMDB has the real wordmark for most popular titles.
 *
 * Rotation pauses on hover, on focus within, and when the browser tab is hidden, and it stops for
 * good once the last slide is reached so it does not loop forever on a five item hero.
 */

import { el, empty, metaLine } from './utils.js';
import { hidePreview } from './card.js';

const BADGE_LABELS = {
    'request-pending': 'Requested',
    'request-approved': 'Approved',
    'request-declined': 'Declined',
    'request-failed': 'Request failed',
    'request-completed': 'In library',
    pending: 'Requested',
    downloading: 'Downloading',
    'partially-available': 'Partly here',
    available: 'In library',
    blocked: 'Blocked',
    removed: 'Removed'
};

/**
 * Builds the hero.
 *
 * @param {Array<Object>} items The hero cards.
 * @param {Object} config The client configuration.
 * @param {Object} actions Handlers: `onOpen`, `onRequest`, `onPlay`.
 * @returns {HTMLElement} The hero element.
 */
export function createHero(items, config, actions) {
    const root = el('div.rekoHero', { attrs: { 'aria-label': 'Featured titles' } });

    if (!items || items.length === 0) {
        return root;
    }

    const art = el('div.rekoHeroArt');
    const content = el('div.rekoHeroContent');
    const dots = el('div.rekoHeroDots', { attrs: { role: 'tablist', 'aria-label': 'Featured titles' } });

    let current = 0;
    let timer = null;
    let paused = false;

    const stop = () => {
        if (timer !== null) {
            clearInterval(timer);
            timer = null;
        }
    };

    const schedule = () => {
        if (items.length < 2) {
            return;
        }

        const seconds = Math.max(3, config?.heroSeconds ?? 9);
        timer = setInterval(() => {
            if (paused || document.hidden || !root.isConnected) {
                return;
            }

            // Stop rather than loop, so a hero never becomes a carousel nobody can escape.
            if (current >= items.length - 1) {
                stop();
                return;
            }

            paint(current + 1);
        }, seconds * 1000);
    };

    const restart = () => {
        stop();
        schedule();
    };

    const primaryAction = (card) => (card.inLibrary
        ? el('button.rekoButton.is-primary', {
            type: 'button',
            text: card.progress > 0.01 ? 'Resume' : 'Play',
            on: {
                click: () => {
                    stop();
                    hidePreview();
                    actions.onPlay?.(card);
                }
            }
        })
        : el('button.rekoButton.is-primary', {
            type: 'button',
            text: 'Request',
            on: {
                click: () => {
                    stop();
                    hidePreview();
                    actions.onRequest?.(card);
                }
            }
        }));

    function paint(index) {
        const card = items[index];
        current = index;

        empty(art);
        empty(content);
        empty(dots);

        art.style.backgroundImage = card.backdrop ? `url("${card.backdrop}")` : '';
        art.setAttribute('data-title', card.title ?? '');

        if (card.logo) {
            content.appendChild(
                el('img.rekoHeroLogo', { src: card.logo, alt: card.title, loading: 'eager' })
            );
        } else {
            content.appendChild(el('h1.rekoHeroTitle', { text: card.title }));
        }

        const facts = metaLine(card);
        if (facts) {
            content.appendChild(el('div.rekoHeroMeta', { text: facts }));
        }

        if (card.requestStatus && BADGE_LABELS[card.requestStatus]) {
            content.appendChild(
                el('div.rekoHeroBadges', null, [
                    el(`span.rekoBadge.is-${card.requestStatus}`, { text: BADGE_LABELS[card.requestStatus] })
                ])
            );
        }

        if (card.overview) {
            content.appendChild(el('p.rekoHeroOverview', { text: card.overview }));
        }

        content.appendChild(
            el('div.rekoHeroActions', null, [
                primaryAction(card),
                el('button.rekoButton.is-secondary', {
                    type: 'button',
                    text: 'More Info',
                    on: {
                        click: () => {
                            stop();
                            hidePreview();
                            actions.onOpen?.(card);
                        }
                    }
                })
            ])
        );

        if (items.length > 1) {
            items.forEach((_, dotIndex) => {
                const dot = el('button.rekoHeroDot', {
                    type: 'button',
                    class: dotIndex === index ? 'is-active' : '',
                    attrs: {
                        role: 'tab',
                        'aria-selected': dotIndex === index ? 'true' : 'false',
                        'aria-label': `Show ${items[dotIndex].title}`
                    },
                    on: {
                        click: () => {
                            paint(dotIndex);
                            restart();
                        }
                    }
                });

                dots.appendChild(dot);
            });
        }

        root.dataset.index = String(index);
    }

    root.addEventListener('mouseenter', () => {
        paused = true;
    });
    root.addEventListener('mouseleave', () => {
        paused = false;
    });
    root.addEventListener('focusin', () => {
        paused = true;
    });
    root.addEventListener('focusout', () => {
        paused = false;
    });

    root.append(art, content, dots);

    paint(0);
    schedule();

    return root;
}
