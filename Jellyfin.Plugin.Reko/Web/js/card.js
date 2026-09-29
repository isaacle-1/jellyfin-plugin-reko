/**
 * Poster cards.
 *
 * A card is a real `<button>` so it is keyboard reachable and announced correctly, and its behaviour
 * is decided by what the server already told us about the title, not by a second lookup:
 *
 *   in library   -> Play jumps straight into Jellyfin
 *   not in library -> More Info opens the Reko title page, where Request lives
 *
 * There is deliberately no hover preview. An earlier version put one there and it was a bad trade in
 * every direction: it covered the row being read, it re-rendered on every pointer move across a rail,
 * and everything it showed — the title, the year, the certification, the overview, the badges — is
 * already on the card or one click away on the title page. Hovering a poster to be told what it is
 * also makes the posters themselves harder to hit.
 */

import { el } from './utils.js';

const BADGES = {
    'request-pending': 'Requested',
    'request-approved': 'Approved',
    'request-declined': 'Declined',
    'request-failed': 'Failed',
    'request-completed': 'In library',
    pending: 'Requested',
    downloading: 'Downloading',
    'partially-available': 'Partly here',
    available: 'In library',
    blocked: 'Blocked',
    removed: 'Removed'
};

const CARD_ATTR = 'data-reko-card';

/**
 * Builds the badge list for a card.
 *
 * @param {Object} card The card payload.
 * @returns {Array<{kind: string, text: string}>} The badges.
 */
function badgesFor(card) {
    const badges = [];

    if (card.inLibrary) {
        badges.push({ kind: 'library', text: card.watched ? 'Watched' : 'In library' });
    }

    const request = BADGES[card.requestStatus];
    if (request && request !== 'In library') {
        badges.push({ kind: card.requestStatus, text: request });
    }

    return badges;
}

/**
 * Builds one poster card.
 *
 * @param {Object} card The card payload.
 * @param {Object} config The client configuration.
 * @param {Object} actions Handlers: `onOpen`, `onRequest`, `onPlay`.
 * @param {number} [rank] The 1-based position, for the ranked Top 10 rows.
 * @returns {HTMLElement} The card element.
 */
export function createCard(card, config, actions, rank = null) {
    const node = el('button.rekoCard', {
        type: 'button',
        attrs: {
            [CARD_ATTR]: JSON.stringify(card),
            'aria-label': `${card.title}${card.year ? `, ${card.year}` : ''}`
        }
    });

    if (rank) {
        node.classList.add('is-ranked');
    }

    const art = el('div.rekoCardArt');
    if (card.poster) {
        art.appendChild(
            el('img.rekoCardImg', {
                src: card.poster,
                alt: '',
                loading: 'lazy',
                decoding: 'async'
            })
        );
    } else {
        art.appendChild(el('div.rekoCardPlaceholder', { text: initialsOf(card.title) }));
    }

    if (card.progress > 0.01) {
        art.appendChild(
            el('div.rekoCardProgress', null, [
                el('div.rekoCardProgressBar', {
                    style: { width: `${Math.round(card.progress * 100)}%` }
                })
            ])
        );
    }

    const badges = badgesFor(card);
    if (badges.length) {
        art.appendChild(
            el('div.rekoCardBadges', null, badges.map((badge) =>
                el(`span.rekoBadge.is-${badge.kind}`, { text: badge.text })
            ))
        );
    }

    // The poster and its caption are wrapped so a ranked card can lay the number out beside them.
    // Absolutely positioning the number instead puts it behind the neighbouring cards' posters,
    // which is where it disappears from: the row is a horizontal scroller, so the leftmost card's
    // number has nothing to its left to be seen against.
    const body = el('div.rekoCardBody', null, [
        art,
        el('div.rekoCardInfo', null, [
            el('div.rekoCardTitle', { text: card.title, attrs: { title: card.title } }),
            el('div.rekoCardSub', {
                text: [
                    card.year ?? '',
                    card.type === 'tv' ? 'Series' : 'Movie'
                ].filter(Boolean).join(' · ')
            })
        ])
    ]);

    if (rank) {
        node.appendChild(el('span.rekoCardRank', { text: String(rank), attrs: { 'aria-hidden': 'true' } }));
    }

    node.appendChild(body);

    node.addEventListener('click', (event) => {
        event.preventDefault();
        openCard(card, config, actions);
    });

    return node;
}

/**
 * Decides what a card click does. A title that is already in the library goes straight to
 * Jellyfin; anything else opens the Reko title page, where the request button lives.
 *
 * @param {Object} card The card payload.
 * @param {Object} config The client configuration.
 * @param {Object} actions The handlers.
 */
function openCard(card, config, actions) {
    if (card.inLibrary && card.jellyfinItemId && actions.onPlay) {
        actions.onPlay(card);
        return;
    }

    if (actions.onOpen) {
        actions.onOpen(card);
    }
}

/**
 * First letters of a title, used when TMDB has no poster.
 *
 * @param {string} title The title.
 * @returns {string} Up to two letters.
 */
function initialsOf(title) {
    return String(title ?? '?')
        .split(/\s+/)
        .filter(Boolean)
        .slice(0, 2)
        .map((word) => word[0].toUpperCase())
        .join('');
}
