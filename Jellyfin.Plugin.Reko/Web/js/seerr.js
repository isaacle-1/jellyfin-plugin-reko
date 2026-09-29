/**
 * Requests through Overseerr or Jellyseerr.
 *
 * The flow mirrors what those applications do, and it is deliberately explicit about three things
 * that are easy to get wrong:
 *
 *   1. Series request a season list, not the whole show, and the user picks which.
 *   2. Seerr answers 202 when every selected season is already requested or available. That is a
 *      success, not an error, and reporting it as a failure is the single most common bug in
 *      integrations like this one.
 *   3. After a request is created, the badge is refreshed from the server rather than guessed, so
 *      "Requested" appears only when Seerr actually says so.
 */

import * as api from './api.js';
import { el } from './utils.js';
import { showModal, showMessage, setModalBody } from './modal.js';

/**
 * Opens the request flow for a title.
 *
 * @param {Object} card The card payload.
 * @param {Object} config The client configuration.
 * @param {Object} payload The full title payload, used to get the season list for a series.
 * @param {Function} onChanged Called after a successful request so the caller can refresh badges.
 * @returns {Promise<void>} Resolves once the flow is closed.
 */
export async function requestTitle(card, config, payload, onChanged) {
    if (!config?.seerrEnabled) {
        showMessage(
            'Requests are not set up',
            'Add your Overseerr or Jellyseerr URL and API key in Dashboard > Plugins > Reko > Settings to request titles from here.',
            'info'
        );
        return;
    }

    if (card.type === 'tv') {
        await requestSeries(card, config, payload, onChanged);
        return;
    }

    const result = await submit(card, { id: card.id, type: card.type });

    if (result) {
        showMessage(
            titleFor(result.outcome),
            result.message,
            result.outcome === 'created' ? 'success' : 'info'
        );

        if (result.outcome === 'created') {
            await onChanged?.();
        }
    }
}

/**
 * Opens the season picker for a series.
 *
 * @param {Object} card The card payload.
 * @param {Object} config The client configuration.
 * @param {Object} payload The full title payload.
 * @param {Function} onChanged Called after a successful request.
 * @returns {Promise<void>} Resolves once the flow is closed.
 */
async function requestSeries(card, config, payload, onChanged) {
    const seasons = (payload?.seasons ?? []).filter((season) => season.episodeCount > 0 || season.number > 0);

    if (seasons.length === 0) {
        // No season list means there is nothing to choose from, so ask for the whole show rather
        // than showing an empty picker. Seerr resolves "all" to every season with episodes.
        const result = await submit(card, { id: card.id, type: 'tv' });
        if (result) {
            showMessage(titleFor(result.outcome), result.message, result.outcome === 'created' ? 'success' : 'info');
            if (result.outcome === 'created') {
                await onChanged?.();
            }
        }

        return;
    }

    const inputs = new Map();
    const list = el('div.rekoSeasonList');

    for (const season of seasons) {
        const inputId = `reko-season-${season.number}`;

        const input = el('input', {
            type: 'checkbox',
            id: inputId,
            checked: season.number === 1 || isUnrequested(season),
            attrs: { 'data-season': String(season.number) }
        });

        inputs.set(season.number, input);

        const status = season.requestStatus
            ? el('span.rekoBadge', {
                class: `is-${season.requestStatus}`,
                text: statusLabel(season.requestStatus)
            })
            : null;

        list.appendChild(
            el('label.rekoSeasonRow', { attrs: { for: inputId } }, [
                input,
                el('span.rekoSeasonName', {
                    text: season.number === 0 ? 'Specials' : season.name || `Season ${season.number}`
                }),
                el('span.rekoSeasonMeta', {
                    text: [
                        season.number === 0 ? null : `Season ${season.number}`,
                        season.episodeCount ? `${season.episodeCount} episodes` : null,
                        season.date ? season.date.slice(0, 4) : null
                    ].filter(Boolean).join(' · ')
                }),
                status
            ])
        );
    }

    const summary = el('div.rekoModalMessage');

    const updateSummary = () => {
        const chosen = [...inputs.values()].filter((input) => input.checked).length;
        summary.textContent = chosen === 0
            ? 'Choose at least one season.'
            : `${chosen} season${chosen === 1 ? '' : 's'} selected.`;
    };

    for (const input of inputs.values()) {
        input.addEventListener('change', updateSummary);
    }

    updateSummary();

    showModal({
        title: `Request ${card.title}`,
        body: [list, summary],
        actions: [
            { label: 'Cancel' },
            {
                label: 'Request',
                primary: true,
                onClick: async ({ close }) => {
                    const chosen = [...inputs.entries()]
                        .filter(([, input]) => input.checked)
                        .map(([number]) => number);

                    if (chosen.length === 0) {
                        summary.textContent = 'Choose at least one season.';
                        return;
                    }

                    const result = await submit(card, { id: card.id, type: 'tv', seasons: chosen });
                    if (!result) {
                        return;
                    }

                    close();
                    showMessage(
                        titleFor(result.outcome),
                        result.message,
                        result.outcome === 'created' ? 'success' : 'info'
                    );

                    if (result.outcome === 'created') {
                        await onChanged?.();
                    }
                }
            }
        ]
    });
}

/**
 * Submits a request, turning a rejection into a readable message.
 *
 * @param {Object} card The card payload.
 * @param {Object} body The request body.
 * @returns {Promise<Object|null>} The outcome, or null when the request failed.
 */
async function submit(card, body) {
    try {
        return await api.seerrRequest(body);
    } catch (error) {
        showMessage('Could not send the request', api.messageOf(error), 'error');
        return null;
    }
}

/**
 * Whether a season has not been requested yet, so it is pre-selected.
 *
 * @param {Object} season The season payload.
 * @returns {boolean} True when the season looks untouched.
 */
function isUnrequested(season) {
    return !season.requestStatus;
}

/**
 * A friendly label for a Seerr status name.
 *
 * @param {string} status The status name.
 * @returns {string} The label.
 */
function statusLabel(status) {
    const labels = {
        pending: 'Requested',
        downloading: 'Downloading',
        'partially-available': 'Partly here',
        available: 'In library',
        blocked: 'Blocked',
        removed: 'Removed',
        'request-pending': 'Requested',
        'request-approved': 'Approved',
        'request-completed': 'In library'
    };

    return labels[status] ?? status;
}

/**
 * A heading for a request outcome.
 *
 * @param {string} outcome The outcome name.
 * @returns {string} The heading.
 */
function titleFor(outcome) {
    switch (outcome) {
        case 'created':
            return 'Request sent';
        case 'alreadySatisfied':
            return 'Nothing to request';
        case 'duplicate':
            return 'Already requested';
        default:
            return 'Request';
    }
}

/**
 * Fetches and reports the state of the Seerr connection, so a misconfiguration is visible up front
 * rather than at the moment somebody tries to request something.
 *
 * @returns {Promise<Object|null>} The status, or null when Seerr is not configured.
 */
export async function checkSeerr() {
    try {
        return await api.seerrStatus();
    } catch (error) {
        console.warn('[Reko] Could not read the Seerr status.', error);
        return null;
    }
}

/**
 * Refreshes request badges for a set of cards in place.
 *
 * @param {Array<Object>} cards The card payloads to update.
 * @param {Function} repaint Called with the card after each successful update.
 * @returns {Promise<void>} Resolves when done.
 */
export async function refreshBadges(cards, repaint) {
    if (!cards?.length) {
        return;
    }

    const ids = cards
        .filter((card) => !card.inLibrary)
        .slice(0, 60)
        .map((card) => card.id);

    if (ids.length === 0) {
        return;
    }

    let states;
    try {
        states = await api.seerrStates(ids);
    } catch (error) {
        console.warn('[Reko] Could not refresh request badges.', error);
        return;
    }

    const byId = new Map((states?.items ?? []).map((item) => [item.id, item]));

    for (const card of cards) {
        const state = byId.get(card.id);
        if (!state) {
            continue;
        }

        card.requestStatus = state.requestStatus ?? state.status ?? null;
        repaint?.(card);
    }
}
