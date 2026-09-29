/**
 * Instant search and the category browse page.
 *
 * Both render inside the Reko tab. Search is debounced and cancellable: with a 220ms debounce and a
 * request generation counter, a slow response for "ali" can never overwrite the results for
 * "alien", which is the failure mode that makes naive instant search feel broken.
 */

import * as api from './api.js';
import { el, empty, debounce } from './utils.js';
import { createCard } from './card.js';

/**
 * Builds the search view.
 *
 * @param {string} initialQuery The query to start with.
 * @param {Object} config The client configuration.
 * @param {Object} actions Handlers passed through to each card.
 * @returns {HTMLElement} The search element.
 */
export function createSearchView(initialQuery, config, actions) {
    const root = el('div.rekoPage.rekoSearch');

    const input = el('input.rekoSearchInput', {
        type: 'search',
        value: initialQuery ?? '',
        placeholder: 'Search movies, series and people',
        attrs: {
            'aria-label': 'Search TMDB',
            autocomplete: 'off',
            autocorrect: 'off',
            spellcheck: 'false',
            enterkeyhint: 'search'
        }
    });

    const clear = el('button.rekoSearchClear', {
        type: 'button',
        text: '×',
        attrs: { 'aria-label': 'Clear search', hidden: 'hidden' }
    });

    const results = el('div.rekoSearchResults');
    const status = el('div.rekoSearchStatus', { attrs: { role: 'status', 'aria-live': 'polite' } });

    root.append(
        el('div.rekoSearchBar', null, [input, clear]),
        status,
        results
    );

    let generation = 0;

    const run = debounce(async (query) => {
        const trimmed = query.trim();

        if (trimmed.length < 2) {
            empty(results);
            status.textContent = trimmed.length === 1 ? 'Type one more character.' : '';
            return;
        }

        // A newer request supersedes anything still in flight.
        generation += 1;
        const mine = generation;

        status.textContent = 'Searching…';
        results.replaceChildren(el('div.rekoSpinner'));

        try {
            const payload = await api.search(trimmed, 1);
            if (mine !== generation) {
                return;
            }

            empty(results);

            if (payload.items.length === 0) {
                status.textContent = `Nothing found for “${trimmed}”.`;
                return;
            }

            status.textContent = `${payload.total.toLocaleString()} result${payload.total === 1 ? '' : 's'}`;

            const grid = el('div.rekoGrid');
            payload.items.forEach((card) => grid.appendChild(createCard(card, config, actions)));
            results.appendChild(grid);
        } catch (error) {
            if (mine !== generation) {
                return;
            }

            empty(results);
            status.textContent = api.messageOf(error);
        }
    }, 220);

    input.addEventListener('input', () => {
        clear.hidden = input.value.length === 0;
        run(input.value);
    });

    clear.addEventListener('click', () => {
        input.value = '';
        clear.hidden = true;
        run.cancel();
        generation += 1;
        empty(results);
        status.textContent = '';
        input.focus();
        actions.onQueryChange?.('');
    });

    // Escape clears rather than closing the page, which is what a search field is expected to do.
    input.addEventListener('keydown', (event) => {
        if (event.key === 'Escape' && input.value) {
            event.stopPropagation();
            clear.click();
        }
    });

    if (initialQuery) {
        clear.hidden = false;
        run(initialQuery);
    } else {
        input.focus();
    }

    return root;
}

/**
 * Builds the category browse page.
 *
 * @param {Object} options Options.
 * @param {string} options.title The page title.
 * @param {string} options.type `movie` or `tv`.
 * @param {number|null} [options.genre] A TMDB genre id.
 * @param {string} [options.sort] A sort expression.
 * @param {Object} options.config The client configuration.
 * @param {Object} options.actions Handlers passed through to each card.
 * @returns {HTMLElement} The browse element.
 */
export function createBrowseView(options) {
    const root = el('div.rekoPage');

    const SORTS = [
        { value: 'popularity.desc', label: 'Popular' },
        { value: 'vote_average.desc', label: 'Top rated' },
        { value: 'primary_release_date.desc', label: 'Newest' },
        { value: 'title.asc', label: 'A to Z' }
    ];

    const sorts = options.type === 'tv'
        ? [
            { value: 'popularity.desc', label: 'Popular' },
            { value: 'vote_average.desc', label: 'Top rated' },
            { value: 'first_air_date.desc', label: 'Newest' },
            { value: 'name.asc', label: 'A to Z' }
        ]
        : SORTS;

    let sort = options.sort && sorts.some((entry) => entry.value === options.sort)
        ? options.sort
        : 'popularity.desc';

    let page = 1;

    const grid = el('div.rekoGrid');
    const status = el('div.rekoBrowseStatus', { attrs: { role: 'status', 'aria-live': 'polite' } });

    const select = el('select.rekoSelect', {
        attrs: { 'aria-label': 'Sort by' },
        on: {
            change: () => {
                sort = select.value;
                page = 1;
                actions.onSortChange?.(sort);
                load();
            }
        }
    });

    for (const entry of sorts) {
        select.appendChild(el('option', { value: entry.value, text: entry.label }));
    }

    select.value = sort;

    root.append(
        el('div.rekoBrowseHeader', null, [
            el('h1.rekoPageTitle', { text: options.title }),
            el('div.rekoBrowseControls', null, [select])
        ]),
        status,
        grid
    );

    const more = el('button.rekoButton.is-secondary.rekoLoadMore', {
        type: 'button',
        text: 'Load more',
        on: { click: () => { page += 1; load({ append: true }); } }
    });

    const load = async ({ append = false } = {}) => {
        if (!append) {
            grid.replaceChildren(el('div.rekoSpinner'));
        }

        more.disabled = true;
        status.textContent = append ? 'Loading…' : '';

        try {
            const payload = await api.browse({
                type: options.type,
                genreId: options.genre ?? null,
                sort,
                page
            });

            if (!append) {
                empty(grid);
            }

            for (const card of payload.items) {
                grid.appendChild(createCard(card, options.config, options.actions));
            }

            status.textContent = `${payload.items.length} of ${payload.total.toLocaleString()} titles`;
            more.hidden = page >= payload.totalPages;
        } catch (error) {
            status.textContent = api.messageOf(error);
            if (!append) {
                empty(grid);
            }
        } finally {
            more.disabled = false;
        }
    };

    root.appendChild(more);
    load();

    return root;
}
