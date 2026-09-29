/**
 * The title page: seasons, episodes, cast, trailer, watch providers and more like this.
 *
 * A title page replaces the home view inside the Reko tab rather than opening a separate route,
 * because jellyfin-web 12 has no plugin route registry. Back and forward still work, because the
 * view is expressed as query parameters on the home route.
 */

import * as api from './api.js';
import { el, empty, metaLine, formatRuntime } from './utils.js';
import { createRail } from './rail.js';
import { requestTitle } from './seerr.js';

/**
 * Builds a title page.
 *
 * @param {number} id The TMDB id.
 * @param {string} type `movie` or `tv`.
 * @param {Object} config The client configuration.
 * @param {Object} actions Handlers: `onBack`, `onPlay`, `onRequest`, `onOpen`.
 * @param {number|null} [initialSeason] The season to select.
 * @returns {Promise<HTMLElement>} The page element.
 */
export async function createTitlePage(id, type, config, actions, initialSeason = null) {
    const root = el('div.rekoPage.is-loading');
    root.appendChild(el('div.rekoSpinner', { attrs: { 'aria-label': 'Loading' } }));

    let payload;
    try {
        payload = await api.title(id, type);
    } catch (error) {
        empty(root);
        root.classList.remove('is-loading');
        root.appendChild(
            el('div.rekoError', null, [
                el('p', { text: api.messageOf(error) }),
                el('button.rekoButton.is-secondary', {
                    type: 'button',
                    text: 'Back',
                    on: { click: () => actions.onBack() }
                })
            ])
        );

        return root;
    }

    empty(root);
    root.classList.remove('is-loading');
    root.classList.add('is-title');

    const card = payload.card;

    // ---------------------------------------------------------------------------------------
    // Header
    // ---------------------------------------------------------------------------------------
    const header = el('div.rekoTitleHeader');

    if (card.backdrop) {
        header.appendChild(
            el('div.rekoTitleArt', { style: { backgroundImage: `url("${card.backdrop}")` } })
        );
    }

    const headerBody = el('div.rekoTitleHeaderBody');

    if (card.logo) {
        // The logo is an image, so on its own it leaves the page with no heading at all. The name
        // still goes in the accessibility tree, where it belongs; the logo's alt is then redundant
        // and is left empty so the title is not announced twice.
        headerBody.appendChild(el('h1.rekoHidden', { text: card.title }));
        headerBody.appendChild(el('img.rekoTitleLogo', { src: card.logo, alt: '' }));
    } else {
        headerBody.appendChild(el('h1.rekoTitleName', { text: card.title }));
    }

    if (card.originalTitle && card.originalTitle !== card.title) {
        headerBody.appendChild(el('div.rekoTitleOriginal', { text: card.originalTitle }));
    }

    const facts = metaLine(card);
    if (facts) {
        headerBody.appendChild(el('div.rekoTitleMeta', { text: facts }));
    }

    headerBody.appendChild(buildActions(card, config, payload, actions, root));

    if (payload.tagline) {
        headerBody.appendChild(el('p.rekoTitleTagline', { text: payload.tagline }));
    }

    if (card.overview) {
        headerBody.appendChild(el('p.rekoTitleOverview', { text: card.overview }));
    }

    if (card.genres?.length) {
        headerBody.appendChild(
            el('div.rekoChipRow', null, card.genres.map((genre) =>
                el('button.rekoChip', {
                    type: 'button',
                    text: genre,
                    on: { click: () => actions.onBrowseGenre(genre) }
                })
            ))
        );
    }

    if (payload.companies?.length) {
        headerBody.appendChild(
            el('div.rekoTitleMeta', { text: payload.companies.slice(0, 3).join(' · ') })
        );
    }

    header.appendChild(headerBody);
    root.appendChild(header);

    // ---------------------------------------------------------------------------------------
    // Body columns
    // ---------------------------------------------------------------------------------------
    const columns = el('div.rekoColumns');
    const main = el('div.rekoColumnMain');
    const side = el('div.rekoColumnSide');
    columns.append(main, side);
    root.appendChild(columns);

    if (config.showTrailers && payload.trailerKey) {
        main.appendChild(buildTrailer(payload.trailerKey, card));
    }

    if (type === 'tv' && payload.seasons?.length) {
        main.appendChild(await buildSeasons(card, payload, initialSeason, actions));
    }

    if (config.showWatchProviders && payload.providers?.length) {
        side.appendChild(buildProviders(payload.providers));
    }

    side.appendChild(buildFacts(card));

    if (config.showCast && payload.cast?.length) {
        main.appendChild(buildCast(payload.cast, actions));
    }

    if (payload.similar?.length) {
        root.appendChild(
            el('div.rekoSection', null, [
                el('h2.rekoSectionTitle', { text: 'More Like This' }),
                createRail(
                    {
                        id: `similar-${id}`,
                        title: 'More Like This',
                        ranked: false,
                        personalized: false,
                        items: payload.similar
                    },
                    config,
                    actions
                )
            ])
        );
    }

    return root;
}

/**
 * Builds the primary action row for a title page.
 *
 * @param {Object} card The card payload.
 * @param {Object} config The client configuration.
 * @param {Object} payload The full title payload.
 * @param {Object} actions Handlers.
 * @param {HTMLElement} page The page element, used to re-render after a request.
 * @returns {HTMLElement} The action row.
 */
function buildActions(card, config, payload, actions, page) {
    const row = el('div.rekoTitleActions');

    if (card.inLibrary) {
        row.appendChild(
            el('button.rekoButton.is-primary', {
                type: 'button',
                text: card.progress > 0.01 ? 'Resume' : 'Play',
                on: { click: () => actions.onPlay(card) }
            })
        );
    }
    else if (config.seerrEnabled) {
        row.appendChild(
            el('button.rekoButton.is-primary', {
                type: 'button',
                text: 'Request',
                on: {
                    click: () => requestTitle(card, config, payload, async () => {
                        // Re-read the title so the season list and the badge both come from Seerr.
                        const fresh = await api.title(card.id, card.type);
                        Object.assign(payload, fresh);
                        const next = await createTitlePage(card.id, card.type, config, actions, null);
                        page.replaceWith(next);
                    })
                }
            })
        );
    }

    row.appendChild(
        el('button.rekoButton.is-secondary', {
            type: 'button',
            text: 'Back to browsing',
            on: { click: () => actions.onBack() }
        })
    );

    if (card.watched) {
        row.appendChild(el('span.rekoBadge.is-library', { text: 'Watched' }));
    }
    else if (card.resumable) {
        row.appendChild(
            el('span.rekoProgress', null, [
                el('span.rekoProgressBar', { style: { width: `${Math.round(card.progress * 100)}%` } }),
                el('span.rekoProgressLabel', { text: `${Math.round(card.progress * 100)}% watched` })
            ])
        );
    }

    return row;
}

/**
 * Builds the season selector and episode list.
 *
 * @param {Object} card The card payload.
 * @param {Object} payload The full title payload.
 * @param {number|null} initialSeason The season to select.
 * @param {Object} actions Handlers.
 * @returns {Promise<HTMLElement>} The section element.
 */
async function buildSeasons(card, payload, initialSeason, actions) {
    const section = el('section.rekoSeasons');
    const episodeHost = el('div.rekoEpisodes');

    const regular = payload.seasons.filter((season) => season.number > 0);
    const specials = payload.seasons.filter((season) => season.number === 0);
    const ordered = [...regular, ...specials];

    const defaultSeason = ordered.find((season) => season.number === (initialSeason ?? 1))
        ?? ordered.find((season) => season.number > 0)
        ?? ordered[0];

    const picker = el('div.rekoSeasonTabs', { attrs: { role: 'tablist', 'aria-label': 'Seasons' } });

    const loadSeason = async (seasonNumber) => {
        for (const tab of picker.children) {
            const isActive = Number(tab.dataset.season) === seasonNumber;
            tab.classList.toggle('is-active', isActive);
            tab.setAttribute('aria-selected', isActive ? 'true' : 'false');
        }

        actions.onSelectSeason?.(seasonNumber);
        episodeHost.replaceChildren(el('div.rekoSpinner'));

        try {
            const season = await api.season(card.id, seasonNumber);
            empty(episodeHost);
            for (const episode of season.episodes) {
                episodeHost.appendChild(buildEpisode(episode));
            }

            if (season.episodes.length === 0) {
                episodeHost.appendChild(el('div.rekoRailMessage', { text: 'No episodes listed.' }));
            }
        } catch (error) {
            empty(episodeHost);
            episodeHost.appendChild(el('div.rekoError', { text: api.messageOf(error) }));
        }
    };

    for (const season of ordered) {
        const tab = el('button.rekoSeasonTab', {
            type: 'button',
            text: season.number === 0 ? 'Specials' : `S${season.number}`,
            attrs: {
                role: 'tab',
                'data-season': String(season.number),
                'aria-selected': 'false',
                title: season.name ?? ''
            },
            on: { click: () => loadSeason(season.number) }
        });

        if (season.requestStatus) {
            tab.appendChild(el('span.rekoSeasonDot', { attrs: { 'data-status': season.requestStatus } }));
        }

        picker.appendChild(tab);
    }

    section.append(
        el('h2.rekoSectionTitle', { text: 'Episodes' }),
        picker,
        episodeHost
    );

    if (defaultSeason) {
        await loadSeason(defaultSeason.number);
    }

    return section;
}

/**
 * Builds one episode row.
 *
 * @param {Object} episode The episode payload.
 * @returns {HTMLElement} The row.
 */
function buildEpisode(episode) {
    return el('article.rekoEpisode', null, [
        el('div.rekoEpisodeStill', null, episode.still
            ? [el('img', { src: episode.still, alt: '', loading: 'lazy' })]
            : [el('div.rekoEpisodePlaceholder')]),
        el('div.rekoEpisodeBody', null, [
            el('div.rekoEpisodeHead', null, [
                el('span.rekoEpisodeNumber', { text: `E${episode.number}` }),
                el('h3.rekoEpisodeName', { text: episode.name ?? `Episode ${episode.number}` }),
                el('span.rekoEpisodeMeta', {
                    text: [
                        episode.date ? episode.date.slice(0, 4) : null,
                        formatRuntime(episode.runtime)
                    ].filter(Boolean).join(' · ')
                })
            ]),
            el('p.rekoEpisodeOverview', { text: episode.overview ?? '' })
        ])
    ]);
}

/**
 * Builds the cast strip.
 *
 * @param {Array<Object>} cast The cast payload.
 * @returns {HTMLElement} The section.
 */
function buildCast(cast, actions) {
    const strip = el('div.rekoCastStrip');

    for (const person of cast) {
        strip.appendChild(
            el('button.rekoCastCard', {
                type: 'button',
                attrs: { title: person.role ? `${person.name} as ${person.role}` : person.name },
                on: { click: () => actions.onPerson?.(person.id) }
            }, [
                el('div.rekoCastImage', null, person.image
                    ? [el('img', { src: person.image, alt: '', loading: 'lazy' })]
                    : [el('div.rekoCastPlaceholder')]),
                el('div.rekoCastName', { text: person.name }),
                person.role ? el('div.rekoCastRole', { text: person.role }) : null
            ])
        );
    }

    return el('section.rekoCast', null, [
        el('h2.rekoSectionTitle', { text: 'Cast' }),
        strip
    ]);
}

/**
 * Builds the watch providers panel.
 *
 * @param {Array<Object>} providers The provider groups.
 * @returns {HTMLElement} The panel.
 */
function buildProviders(providers) {
    const section = el('section.rekoProviders');

    section.appendChild(el('h2.rekoSectionTitle', { text: 'Where to watch' }));

    for (const group of providers) {
        const row = el('div.rekoProviderRow', null, [
            el('span.rekoProviderLabel', { text: group.label }),
            el('div.rekoProviderLogos', null, group.providers.map((provider) =>
                el('div.rekoProvider', { attrs: { title: provider.name } }, [
                    provider.logo
                        ? el('img', { src: provider.logo, alt: provider.name, loading: 'lazy' })
                        : el('span', { text: provider.name })
                ])
            ))
        ]);

        section.appendChild(row);
    }

    // TMDB's terms require attribution when the where-to-watch data is shown.
    section.appendChild(
        el('div.rekoAttribution', { text: 'Streaming availability by TMDB' })
    );

    return section;
}

/**
 * Builds the facts sidebar.
 *
 * @param {Object} card The card payload.
 * @returns {HTMLElement} The panel.
 */
function buildFacts(card) {
    const list = el('dl.rekoFacts');

    const add = (label, value) => {
        if (!value) {
            return;
        }

        list.append(el('dt', { text: label }), el('dd', { text: String(value) }));
    };

    add('Status', card.type === 'tv' ? 'Series' : 'Movie');
    add('Released', card.date || null);
    add('Certification', card.certification);
    add(card.type === 'tv' ? 'Seasons' : 'Runtime', card.type === 'tv'
        ? card.seasonCount
        : formatRuntime(card.runtime));
    add('Rating', card.voteCount > 0 ? `${card.rating.toFixed(1)} / 10 from ${card.voteCount.toLocaleString()} votes` : null);
    add('Genres', card.genres?.join(', '));

    const section = el('section.rekoFactsPanel', null, [
        el('h2.rekoSectionTitle', { text: 'Details' }),
        list
    ]);

    return section;
}

/**
 * Builds the trailer section.
 *
 * The player is created on demand rather than up front, because a YouTube iframe pulls a
 * third-party script bundle and most people never press play on it. The poster behind it is the
 * title's own backdrop, so the section is recognisable before anything is clicked and is not a
 * black rectangle in the middle of the page.
 *
 * @param {string} key The YouTube video key.
 * @param {Object} card The card payload, for the poster and the accessible name.
 * @returns {HTMLElement} The section.
 */
function buildTrailer(key, card) {
    const watchUrl = `https://www.youtube.com/watch?v=${encodeURIComponent(key)}`;
    const host = el('div.rekoTrailer');

    if (card.backdrop) {
        host.appendChild(
            el('div.rekoTrailerPoster', { style: { backgroundImage: `url("${card.backdrop}")` } })
        );
    }

    /**
     * Swaps the poster for the player.
     */
    const play = () => {
        host.replaceChildren(
            el('iframe.rekoTrailerFrame', {
                src: embedUrl(key),
                title: `${card.title} trailer`,
                attrs: {
                    allow: 'accelerometer; autoplay; clipboard-write; encrypted-media; picture-in-picture',
                    allowfullscreen: '',

                    // YouTube refuses to configure a player whose embed request arrives with no
                    // Referer, and says so with "Error 153: Video player configuration error". The
                    // policy is normally inherited from the page, but Jellyfin is very often served
                    // through a reverse proxy that sets `Referrer-Policy: no-referrer` — which strips
                    // the header and breaks every embed on the server at once. Stating `origin` on
                    // the element overrides whatever the page and the proxy said, so the header is
                    // sent either way.
                    referrerpolicy: 'origin',

                    loading: 'lazy'
                }
            })
        );
    };

    host.appendChild(
        el('button.rekoTrailerPlay', {
            type: 'button',
            attrs: { 'aria-label': `Play the trailer for ${card.title}` },
            on: { click: play }
        }, [
            el('span.rekoTrailerGlyph', {
                attrs: { 'aria-hidden': 'true' },
                text: '▶'
            }),
            el('span.rekoTrailerPlayText', { text: 'Play trailer' })
        ])
    );

    return el('section.rekoTrailerSection', null, [
        el('h2.rekoSectionTitle', { text: 'Trailer' }),
        host,

        // Not decoration. Embedding a YouTube player is something YouTube is entitled to refuse —
        // by region, by age gate, by a video whose owner has since disallowed embedding — and when
        // it does, the page shows nothing but an error code. A link always works.
        el('div.rekoTrailerFoot', null, [
            el('a.rekoTrailerLink', {
                text: 'Watch on YouTube',
                attrs: { href: watchUrl, target: '_blank', rel: 'noopener noreferrer' }
            })
        ])
    ]);
}

/**
 * The embed URL for a YouTube video.
 *
 * `origin` is what YouTube uses to decide whether the player is allowed to run, and it has to match
 * the page exactly or the player refuses. `window.location.origin` is the only correct answer:
 * hard-coding a host would break on the next domain, and a trailing slash, a port or a path left on
 * the end is the usual reason a working embed turns into error 153 after a move to a new domain.
 *
 * @param {string} key The YouTube video key.
 * @returns {string} The embed URL.
 */
function embedUrl(key) {
    const params = new URLSearchParams({
        autoplay: '1',
        rel: '0',
        modestbranding: '1',
        playsinline: '1',
        origin: window.location.origin
    });

    return `https://www.youtube-nocookie.com/embed/${encodeURIComponent(key)}?${params.toString()}`;
}

/**
 * Builds the person page.
 *
 * @param {number} id The TMDB person id.
 * @param {Object} config The client configuration.
 * @param {Object} actions Handlers.
 * @returns {Promise<HTMLElement>} The page element.
 */
export async function createPersonPage(id, config, actions) {
    const root = el('div.rekoPage.is-loading');
    root.appendChild(el('div.rekoSpinner', { attrs: { 'aria-label': 'Loading' } }));

    let payload;
    try {
        payload = await api.person(id);
    } catch (error) {
        empty(root);
        root.classList.remove('is-loading');
        root.appendChild(el('div.rekoError', null, [
            el('p', { text: api.messageOf(error) }),
            el('button.rekoButton.is-secondary', {
                type: 'button',
                text: 'Back',
                on: { click: () => actions.onBack() }
            })
        ]));

        return root;
    }

    empty(root);
    root.classList.remove('is-loading');

    const columns = el('div.rekoColumns');
    const main = el('div.rekoColumnMain');
    const side = el('div.rekoColumnSide');
    columns.append(main, side);
    root.appendChild(columns);

    side.appendChild(
        el('div.rekoPersonHead', null, [
            el('div.rekoPersonImage', null, payload.image
                ? [el('img', { src: payload.image, alt: payload.name, loading: 'lazy' })]
                : [el('div.rekoPersonPlaceholder')]),
            el('h1.rekoPageTitle', { text: payload.name }),
            el('dl.rekoFacts', null, [
                el('dt', { text: 'Born' }),
                el('dd', { text: payload.birthday || 'Unknown' }),
                el('dt', { text: 'From' }),
                el('dd', { text: payload.placeOfBirth || 'Unknown' })
            ])
        ])
    );

    if (payload.biography) {
        main.appendChild(
            el('section.rekoBiography', null, [
                el('h2.rekoSectionTitle', { text: 'Biography' }),
                el('p', { text: payload.biography })
            ])
        );
    }

    if (payload.credits?.length) {
        main.appendChild(
            createRail(
                {
                    id: `person-${id}`,
                    title: 'Known for',
                    ranked: false,
                    personalized: false,
                    items: payload.credits
                },
                config,
                actions
            )
        );
    }

    root.prepend(
        el('button.rekoBackLink', {
            type: 'button',
            text: '‹ Back to browsing',
            on: { click: () => actions.onBack() }
        })
    );

    return root;
}
