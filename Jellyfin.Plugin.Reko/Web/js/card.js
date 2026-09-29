/**
 * Poster cards, with the hover preview.
 *
 * A card is a real `<button>` so it is keyboard reachable and announced correctly, and its behaviour
 * is decided by what the server already told us about the title, not by a second lookup:
 *
 *   in library   -> Play jumps straight into Jellyfin
 *   not in library -> More Info opens the Reko title page, where Request lives
 *
 * The hover preview is a single shared element rather than one per card. Twenty rails of twenty
 * cards is 400 previews, and 400 absolutely positioned overlays is how a rail starts dropping
 * frames. One element, repositioned on pointer enter, costs nothing.
 */

import { el, metaLine, isVisible } from './utils.js';

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

const PREVIEW_ATTR = 'data-reko-preview';
const CARD_ATTR = 'data-reko-card';

let previewEl = null;
let hoverTimer = null;

/**
 * Lazily creates the shared hover preview.
 *
 * It is mounted inside Reko's own root rather than on `document.body`, because the whole stylesheet
 * is driven by custom properties declared on `.rekoApp`. On the body those properties do not exist,
 * so `background: var(--reko-surface-solid)` and friends resolve to nothing and the preview comes
 * out transparent, with the row showing through it.
 *
 * @returns {HTMLElement} The preview element.
 */
function getPreview() {
    if (previewEl && document.body.contains(previewEl)) {
        return previewEl;
    }

    previewEl = el('div.rekoPreview', { attrs: { [PREVIEW_ATTR]: 'true' } });
    (document.getElementById('rekoApp') ?? document.body).appendChild(previewEl);
    return previewEl;
}

/**
 * Hides the shared preview.
 */
export function hidePreview() {
    if (hoverTimer !== null) {
        clearTimeout(hoverTimer);
        hoverTimer = null;
    }

    if (previewEl) {
        previewEl.classList.remove('is-open');
    }
}

/**
 * Shows the shared preview anchored to a card.
 *
 * @param {HTMLElement} anchor The card element.
 * @param {Object} card The card payload.
 * @param {Object} config The client configuration.
 */
function showPreview(anchor, card, config) {
    if (!config?.enableHoverPreviews) {
        return;
    }

    const preview = getPreview();
    emptyPreview(preview);

    if (card.backdrop) {
        preview.appendChild(
            el('div.rekoPreviewArt', {
                style: { backgroundImage: `url("${card.backdrop}")` }
            })
        );
    }

    const body = el('div.rekoPreviewBody', null, [
        el('div.rekoPreviewTitle', { text: card.title })
    ]);

    const facts = metaLine(card, { hideRating: !card.voteCount });
    if (facts) {
        body.appendChild(el('div.rekoPreviewMeta', { text: facts }));
    }

    if (card.overview) {
        body.appendChild(el('div.rekoPreviewOverview', { text: card.overview }));
    }

    if (card.genres?.length) {
        body.appendChild(
            el('div.rekoPreviewGenres', { text: card.genres.slice(0, 3).join(' · ') })
        );
    }

    body.appendChild(
        el('div.rekoPreviewBadges', null, badgesFor(card).map((badge) =>
            el(`span.rekoBadge.is-${badge.kind}`, { text: badge.text })
        ))
    );

    body.appendChild(
        el('div.rekoPreviewActions', null, [
            el('span.rekoPreviewAction', { text: card.inLibrary ? 'Play' : 'More Info' }),
            !card.inLibrary && config.seerrEnabled
                ? el('span.rekoPreviewAction', { text: 'Request' })
                : null
        ])
    );

    preview.appendChild(body);
    preview.setAttribute(PREVIEW_ATTR, 'false');

    // Opened before it is measured, because a hidden element has no size to measure. Both happen in
    // the same task, so nothing is painted in between and the preview is never seen in the wrong
    // place.
    preview.classList.add('is-open');
    positionPreview(preview, anchor);
}

/**
 * Positions the preview above the card, flipping below when there is no room.
 *
 * @param {HTMLElement} preview The preview element.
 * @param {HTMLElement} anchor The card.
 */
function positionPreview(preview, anchor) {
    if (!isVisible(anchor)) {
        // A card can be laid out and still be nowhere near the screen — a row scrolled out of view
        // fires mouseenter when the page moves under a stationary pointer. Leaving the preview open
        // would strand it in the corner of the screen, which is worse than showing nothing.
        hidePreview();
        return;
    }

    // Measured synchronously. requestAnimationFrame would be tidier, but it is paused whenever the
    // page is not being painted, which would leave the preview stuck in the top-left corner on a
    // background tab or a TV client.
    const cardRect = anchor.getBoundingClientRect();
    const previewRect = preview.getBoundingClientRect();
    const margin = 12;

    const left = Math.max(
        margin,
        Math.min(
            cardRect.left + cardRect.width / 2 - previewRect.width / 2,
            window.innerWidth - previewRect.width - margin
        )
    );

    const above = cardRect.top - previewRect.height - 10;
    const below = cardRect.bottom + 10;
    const top = cardRect.top > previewRect.height + margin ? above : below;

    preview.style.left = `${Math.round(left)}px`;
    preview.style.top = `${Math.round(
        Math.max(margin, Math.min(top, window.innerHeight - previewRect.height - margin))
    )}px`;
}

/**
 * Empties the preview element.
 *
 * @param {HTMLElement} preview The preview element.
 */
function emptyPreview(preview) {
    while (preview.firstChild) {
        preview.removeChild(preview.firstChild);
    }
}

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
                    card.type === 'tv' ? 'Series' : 'Movie',
                    card.inLibrary ? '' : ''
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

    node.addEventListener('mouseenter', () => {
        if (hoverTimer !== null) {
            clearTimeout(hoverTimer);
        }

        // A short delay stops the preview flickering as the pointer crosses a row.
        hoverTimer = setTimeout(() => {
            hoverTimer = null;
            showPreview(node, card, config);
        }, 260);
    });

    node.addEventListener('focus', () => showPreview(node, card, config));
    node.addEventListener('mouseleave', hidePreview);
    node.addEventListener('blur', hidePreview);

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
    hidePreview();

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
