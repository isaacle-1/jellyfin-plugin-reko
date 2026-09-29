/**
 * A stand-in for TMDB and Overseerr/Jellyseerr, for verifying Reko end to end without live
 * credentials or outbound network access.
 *
 * It is a test fixture, not part of the plugin. It is not shipped in the plugin zip and the plugin
 * has no compile-time or runtime dependency on it.
 *
 * Routes implemented, matching the real response shapes closely enough for Reko's parsing:
 *   GET  /tmdb/3/configuration
 *   GET  /tmdb/3/genre/{movie,tv}/list
 *   GET  /tmdb/3/trending/{movie,tv,all}/{day,week}
 *   GET  /tmdb/3/discover/movie, /tmdb/3/discover/tv
 *   GET  /tmdb/3/movie/{id}, /tmdb/3/tv/{id}
 *   GET  /tmdb/3/tv/{id}/season/{n}
 *   GET  /tmdb/3/collection/{id}
 *   GET  /tmdb/3/search/multi
 *   GET  /seerr/api/v1/status
 *   GET  /seerr/api/v1/settings/public
 *   GET  /seerr/api/v1/user/jellyfin/{id}
 *   GET  /seerr/api/v1/movie/{id}, /seerr/api/v1/tv/{id}
 *   GET  /seerr/api/v1/request
 *   POST /seerr/api/v1/request
 */

const http = require('node:http');

const PORT = Number(process.argv[2] || 8099);

const IMAGE = 'https://image.tmdb.org/t/p';
const watchedTmdbIds = new Set();

const MOVIE_GENRES = [
    [28, 'Action'], [12, 'Adventure'], [16, 'Animation'], [35, 'Comedy'], [80, 'Crime'],
    [99, 'Documentary'], [18, 'Drama'], [10751, 'Family'], [14, 'Fantasy'], [27, 'Horror'],
    [9648, 'Mystery'], [10749, 'Romance'], [878, 'Science Fiction'], [53, 'Thriller'],
    [10752, 'War'], [37, 'Western']
];

const TV_GENRES = [
    [10759, 'Action & Adventure'], [16, 'Animation'], [35, 'Comedy'], [80, 'Crime'],
    [99, 'Documentary'], [18, 'Drama'], [10751, 'Family'], [10762, 'Kids'], [9648, 'Mystery'],
    [10764, 'Reality'], [10765, 'Sci-Fi & Fantasy'], [10766, 'Soap'], [10767, 'Talk'],
    [10768, 'War & Politics'], [37, 'Western']
];

const NOUNS = [
    'Harbour', 'Lantern', 'Requiem', 'Cartographer', 'Vantage', 'Thicket', 'Meridian', 'Cascade',
    'Solstice', 'Foundry', 'Almanac', 'Tidewater', 'Observatory', 'Ballad', 'Aperture'
];

const ADJECTIVES = [
    'Silent', 'Endless', 'Broken', 'Golden', 'Hollow', 'Northern', 'Quiet', 'Distant',
    'Radiant', 'Restless', 'Crimson', 'Weightless'
];

/**
 * Builds a deterministic pseudo-random number from a seed, so a given id always produces the same
 * fixture. Without this, a reload reshuffles every rail and a visual diff is meaningless.
 *
 * @param {number} seed The seed.
 * @returns {number} A value between 0 and 1.
 */
function rand(seed) {
    const x = Math.sin(seed * 12.9898) * 43758.5453;
    return x - Math.floor(x);
}

/**
 * Builds a list item.
 *
 * @param {number} id The TMDB id.
 * @param {boolean} isMovie True for a movie.
 * @param {string} [mediaType] Explicit media_type, as trending and search return.
 * @returns {Object} The item.
 */
function makeItem(id, isMovie, mediaType) {
    const r1 = Math.floor(rand(id) * ADJECTIVES.length);
    const r2 = Math.floor(rand(id * 7) * NOUNS.length);
    const year = 1985 + Math.floor(rand(id * 3) * 39);
    const vote = (5.4 + rand(id * 11) * 4.4).toFixed(1);
    const votes = 200 + Math.floor(rand(id * 13) * 90000);

    const item = {
        id,
        backdrop_path: `/${id}-backdrop.jpg`,
        poster_path: `/${id}-poster.jpg`,
        overview: `A ${ADJECTIVES[r1].toLowerCase()} story about a ${NOUNS[r2].toLowerCase()}, and what it costs to get it back. This is fixture text standing in for a real synopsis so the card, hero and title page layouts can be checked with plausible length.`,
        vote_average: Number(vote),
        vote_count: votes,
        popularity: Number((rand(id * 17) * 400).toFixed(3)),
        genre_ids: [MOVIE_GENRES[Math.floor(rand(id * 19) * MOVIE_GENRES.length)][0]],
        adult: false,
        original_language: 'en',
        origin_country: ['US']
    };

    if (mediaType) {
        item.media_type = mediaType;
    }

    if (isMovie) {
        item.title = `The ${ADJECTIVES[r1]} ${NOUNS[r2]}`;
        item.original_title = item.title;
        item.release_date = `${year}-0${1 + (id % 9)}-1${id % 9}`;
        item.video = false;
    } else {
        item.name = `${NOUNS[r2]} ${ADJECTIVES[r1]}`;
        item.original_name = item.name;
        item.first_air_date = `${year}-1${id % 9}-0${1 + (id % 9)}`;
    }

    return item;
}

/**
 * Builds a list of items.
 *
 * @param {number} start The first id.
 * @param {number} count How many.
 * @param {boolean} isMovie True for movies.
 * @param {boolean} [withMediaType] Whether to set media_type, as trending/all and search do.
 * @returns {Array<Object>} The items.
 */
function makeList(start, count, isMovie, withMediaType) {
    return Array.from({ length: count }, (_, index) =>
        makeItem(start + index, isMovie, withMediaType ? (isMovie ? 'movie' : 'tv') : undefined));
}

/**
 * Builds a movie detail payload.
 *
 * @param {number} id The id.
 * @returns {Object} The detail.
 */
function makeMovieDetail(id) {
    const base = makeItem(id, true);
    return {
        ...base,
        budget: 20000000 + id * 1000,
        genres: MOVIE_GENRES.slice(id % 3, id % 3 + 3).map(([gid, name]) => ({ id: gid, name })),
        homepage: null,
        imdb_id: `tt${String(1000000 + id).padStart(7, '0')}`,
        production_companies: [
            { id: 1, name: 'Northlight Pictures', logo_path: '/company.png', origin_country: 'US' },
            { id: 2, name: 'Harbour Line', logo_path: null, origin_country: 'GB' }
        ],
        production_countries: [{ iso_3166_1: 'US', name: 'United States of America' }],
        revenue: 40000000 + id * 5000,
        runtime: 88 + (id % 74),
        spoken_languages: [{ english_name: 'English', iso_639_1: 'en', name: 'English' }],
        status: 'Released',
        tagline: 'Every map is a promise you can break.',
        belongs_to_collection: id % 3 === 0
            ? { id: 900 + id, name: `The ${NOUNS[id % NOUNS.length]} Collection`, poster_path: '/col.jpg', backdrop_path: '/colb.jpg' }
            : null,
        credits: {
            cast: makeCast(id),
            crew: [{ id: 5000 + id, name: 'R. Alcott', job: 'Director', department: 'Directing', profile_path: '/p1.jpg', popularity: 12 }]
        },
        videos: {
            results: [
                { id: 'v1', key: `yt${id}`, name: 'Official Trailer', site: 'YouTube', type: 'Trailer', official: true, iso_639_1: 'en', published_at: '2024-02-01T00:00:00.000Z' },
                { id: 'v2', key: `te${id}`, name: 'Teaser', site: 'YouTube', type: 'Teaser', official: false, iso_639_1: 'en', published_at: '2023-12-01T00:00:00.000Z' }
            ]
        },
        images: {
            posters: [
                { iso_639_1: 'en', file_path: `/logo-poster-${id}.jpg`, vote_average: 5.6, vote_count: 40, width: 500 },
                { iso_639_1: null, file_path: `/nologo-poster-${id}.jpg`, vote_average: 5.1, vote_count: 10, width: 500 }
            ],
            backdrops: [{ iso_639_1: null, file_path: `/bd-${id}.jpg`, vote_average: 5.9, vote_count: 120, width: 1920 }],
            logos: [
                { iso_639_1: 'fr', file_path: `/logo-fr-${id}.png`, vote_average: 5.0, vote_count: 3, width: 500 },
                { iso_639_1: 'en', file_path: `/logo-en-${id}.png`, vote_average: 5.8, vote_count: 90, width: 500 },
                { iso_639_1: null, file_path: `/logo-none-${id}.png`, vote_average: 5.4, vote_count: 20, width: 500 }
            ]
        },
        recommendations: page(makeList(id * 3, 20, true), 1, 400, 20),
        similar: page(makeList(id * 5, 20, true), 1, 400, 20),
        'watch/providers': {
            id,
            results: {
                US: {
                    link: 'https://www.themoviedb.org/movie/x/watch?locale=US',
                    flatrate: [
                        { provider_id: 8, provider_name: 'Netflix', logo_path: '/netflix.jpg', display_priority: 1 },
                        { provider_id: 2, provider_name: 'Apple TV', logo_path: '/apple.jpg', display_priority: 4 }
                    ],
                    rent: [{ provider_id: 3, provider_name: 'Google Play', logo_path: '/play.jpg', display_priority: 5 }],
                    buy: [{ provider_id: 3, provider_name: 'Google Play', logo_path: '/play.jpg', display_priority: 5 }]
                }
            }
        },
        // These shapes are transcribed from TMDB, not invented. `release_dates.results` and
        // `content_ratings.results` are ARRAYS of per-country entries, not objects keyed by country
        // code; getting that wrong here once is what let a model with the same mistake ship, because
        // a fake written from a fake proves nothing about the real thing. `watch/providers` really is
        // keyed by region, which is the trap in the other direction.
        release_dates: {
            id,
            results: [
                {
                    iso_3166_1: 'US',
                    release_dates: [
                        {
                            certification: '',
                            descriptors: [],
                            iso_639_1: '',
                            note: 'Mock Film Festival',
                            release_date: '2019-01-02T00:00:00.000Z',
                            type: 1
                        },
                        {
                            certification: id % 4 === 0 ? 'R' : 'PG-13',
                            descriptors: [],
                            iso_639_1: '',
                            note: '',
                            release_date: '2019-03-15T00:00:00.000Z',
                            type: 3
                        }
                    ]
                },
                { iso_3166_1: 'GB', release_dates: [{ certification: '12', note: '', type: 3 }] }
            ]
        }
    };
}

/**
 * Builds a cast list.
 *
 * @param {number} id The seed.
 * @returns {Array<Object>} The cast.
 */
function makeCast(id) {
    return Array.from({ length: 12 }, (_, index) => ({
        id: 3000 + id * 20 + index,
        name: `${ADJECTIVES[(id + index) % ADJECTIVES.length]} ${NOUNS[(id * 2 + index) % NOUNS.length]}`,
        character: index === 0 ? 'Detective Rowe' : `Character ${index}`,
        profile_path: `/profile-${id}-${index}.jpg`,
        popularity: 40 - index * 3,
        order: index
    }));
}

/**
 * Builds a series detail payload.
 *
 * @param {number} id The id.
 * @returns {Object} The detail.
 */
function makeSeriesDetail(id) {
    const base = makeItem(id, false);
    const seasonCount = 2 + (id % 5);
    const seasons = Array.from({ length: seasonCount }, (_, index) => ({
        id: id * 100 + index,
        season_number: index + 1,
        name: `Season ${index + 1}`,
        overview: index === 0 ? 'The first chapter, and the one that explains the whole thing.' : null,
        poster_path: `/season-${id}-${index}.jpg`,
        air_date: `${1995 + index}-03-14`,
        episode_count: 6 + (index % 4)
    }));

    return {
        ...base,
        genres: TV_GENRES.slice(id % 3, id % 3 + 3).map(([gid, name]) => ({ id: gid, name })),
        episode_run_time: [42, 48],
        homepage: null,
        network: { id: 49, name: 'Harbour Television', logo_path: '/net.png', origin_country: 'US' },
        number_of_seasons: seasonCount,
        number_of_episodes: seasons.reduce((sum, s) => sum + s.episode_count, 0),
        seasons,
        status: 'Returning Series',
        tagline: 'Some maps are drawn in blood.',
        type: 'Scripted',
        aggregate_credits: { cast: makeCast(id), crew: [{ id: 7000 + id, name: 'M. Vale', job: 'Creator', profile_path: '/p9.jpg', popularity: 20 }] },
        videos: { results: [{ id: `s${id}`, key: `ys${id}`, name: 'Series Trailer', site: 'YouTube', type: 'Trailer', official: true, iso_639_1: 'en', published_at: '2025-01-05T00:00:00.000Z' }] },
        images: {
            posters: [{ iso_639_1: 'en', file_path: `/sposter-${id}.jpg`, vote_average: 5.5, vote_count: 30, width: 500 }],
            backdrops: [{ iso_639_1: null, file_path: `/sbd-${id}.jpg`, vote_average: 5.7, vote_count: 60, width: 1920 }],
            logos: [{ iso_639_1: 'en', file_path: `/slogo-${id}.png`, vote_average: 5.6, vote_count: 22, width: 500 }]
        },
        recommendations: page(makeList(id * 7, 20, false), 1, 400, 20),
        similar: page(makeList(id * 11, 20, false), 1, 400, 20),
        'watch/providers': {
            id,
            results: {
                US: {
                    link: 'https://www.themoviedb.org/tv/x/watch?locale=US',
                    flatrate: [{ provider_id: 213, provider_name: 'Hulu', logo_path: '/hulu.jpg', display_priority: 2 }],
                    ads: [{ provider_id: 613, provider_name: 'The Roku Channel', logo_path: '/roku.jpg', display_priority: 9 }]
                }
            }
        },
        // An array of per-country entries, as TMDB returns it. See the note on release_dates.
        content_ratings: {
            id,
            results: [
                { descriptors: [], iso_3166_1: 'US', rating: 'TV-MA' },
                { descriptors: [], iso_3166_1: 'GB', rating: '12' }
            ]
        }
    };
}

/**
 * Builds a season payload.
 *
 * @param {number} id The series id.
 * @param {number} seasonNumber The season number.
 * @returns {Object} The season.
 */
function makeSeason(id, seasonNumber) {
    const count = 6 + ((id + seasonNumber) % 4);
    return {
        id: id * 100 + seasonNumber - 1,
        season_number: seasonNumber,
        name: `Season ${seasonNumber}`,
        overview: seasonNumber === 1 ? 'The first chapter, and the one that explains the whole thing.' : null,
        poster_path: `/season-${id}-${seasonNumber - 1}.jpg`,
        air_date: `${1995 + seasonNumber}-03-14`,
        episodes: Array.from({ length: count }, (_, index) => ({
            id: id * 1000 + seasonNumber * 100 + index,
            name: `The ${NOUNS[(id + index) % NOUNS.length]} of ${ADJECTIVES[(seasonNumber + index) % ADJECTIVES.length]}`,
            episode_number: index + 1,
            season_number: seasonNumber,
            overview: index === 0
                ? 'A cold open on a harbour at four in the morning, and a decision that cannot be walked back.'
                : null,
            air_date: `${1995 + seasonNumber}-03-${String(10 + index).padStart(2, '0')}`,
            runtime: 41 + ((id + index) % 9),
            vote_average: Number((6.2 + rand(id + index) * 3).toFixed(1)),
            vote_count: 50 + Math.floor(rand(id * index) * 900),
            still_path: `/still-${id}-${seasonNumber}-${index}.jpg`,
            crew: [],
            guest_stars: []
        }))
    };
}

/**
 * Wraps items in the TMDB paged envelope.
 *
 * @param {Array<Object>} results The items.
 * @param {number} pageNumber The page number.
 * @param {number} totalResults The total.
 * @param {number} perPage The page size.
 * @returns {Object} The envelope.
 */
function page(results, pageNumber, totalResults, perPage) {
    return {
        page: pageNumber,
        results,
        total_pages: Math.max(1, Math.ceil(totalResults / perPage)),
        total_results: totalResults
    };
}

/**
 * Builds a collection payload.
 *
 * @param {number} id The id.
 * @returns {Object} The collection.
 */
function makeCollection(id) {
    return {
        id,
        name: `The ${NOUNS[id % NOUNS.length]} Collection`,
        overview: 'Every film in the series, newest first.',
        poster_path: '/col.jpg',
        backdrop_path: '/colb.jpg',
        parts: makeList(id * 13, 8, true)
    };
}

/**
 * Builds a person payload.
 *
 * @param {number} id The id.
 * @returns {Object} The person.
 */
function makePerson(id) {
    return {
        id,
        name: `${ADJECTIVES[id % ADJECTIVES.length]} ${NOUNS[id % NOUNS.length]}`,
        biography: 'A working biography, long enough to wrap several lines and prove the column layout holds up with realistic text rather than a placeholder.',
        birthday: `${1950 + (id % 40)}-04-11`,
        deathday: null,
        place_ofBirth: `${NOUNS[id % NOUNS.length]}, United Kingdom`,
        profile_path: `/person-${id}.jpg`,
        known_for_department: 'Acting',
        popularity: 22.5,
        combined_credits: {
            cast: makeList(id * 3, 14, true).map((item) => ({ ...item, media_type: 'movie' }))
                .concat(makeList(id * 5, 8, false).map((item) => ({ ...item, media_type: 'tv' }))),
            crew: []
        }
    };
}

// -------------------------------------------------------------------------------------------------
// Seerr
// -------------------------------------------------------------------------------------------------

const SEERR_REQUESTS = new Map();

/**
 * Builds the Seerr public settings.
 *
 * @returns {Object} The settings.
 */
function seerrSettings() {
    return {
        initialized: true,
        applicationTitle: 'Jellyseerr',
        applicationUrl: 'http://127.0.0.1:8099/seerr',
        mediaServerType: 2,
        discoverRegion: 'US',
        jellyfinServerName: 'Test Server',
        partialRequestsEnabled: true,
        localLogin: true,
        mediaServerLogin: true
    };
}

/**
 * Builds a Seerr media record.
 *
 * @param {number} tmdbId The TMDB id.
 * @param {string} mediaType `movie` or `tv`.
 * @returns {Object|null} The record, or null when unknown.
 */
function seerrMedia(tmdbId, mediaType) {
    if (!watchedTmdbIds.has(`${mediaType}:${tmdbId}`)) {
        return null;
    }

    const detail = mediaType === 'tv' ? makeSeriesDetail(tmdbId) : makeMovieDetail(tmdbId);

    return {
        id: 7000 + tmdbId,
        tmdbId,
        tvdbId: 100000 + tmdbId,
        imdbId: null,
        status: 5,
        mediaType,
        serviceUrl: null,
        status4k: 1,
        createdAt: '2026-09-01T10:00:00.000Z',
        lastSeasonChange: '2026-09-01T10:00:00.000Z',
        mediaAddedAt: '2026-09-01T10:00:00.000Z',
        requests: [{ id: 900 + tmdbId, status: 5, createdAt: '2026-09-01T10:00:00.000Z', updatedAt: '2026-09-02T10:00:00.000Z', modifiedBy: null, is4k: false, profileId: null, rootFolder: null, languageProfileId: null, tags: [], seasons: (detail.seasons ?? []).map((s) => ({ seasonNumber: s.season_number, status: 5 })) }],
        seasons: (detail.seasons ?? []).map((s) => ({ seasonNumber: s.season_number, status: 5 }))
    };
}

// -------------------------------------------------------------------------------------------------
// Routing
// -------------------------------------------------------------------------------------------------

/**
 * Writes a JSON response.
 *
 * @param {http.ServerResponse} res The response.
 * @param {number} status The status code.
 * @param {Object} body The body.
 */
function send(res, status, body) {
    const payload = JSON.stringify(body);
    res.writeHead(status, { 'Content-Type': 'application/json; charset=utf-8', 'Content-Length': Buffer.byteLength(payload) });
    res.end(payload);
}

/**
 * Handles a request.
 *
 * @param {http.IncomingMessage} req The request.
 * @param {http.ServerResponse} res The response.
 */
function handle(req, res) {
    const url = new URL(req.url, `http://${req.headers.host}`);
    const path = url.pathname;
    const q = url.searchParams;

    // Reko's own static assets live under /reko on this fixture, so the browser can load them even
    // though the real server serves them from the Jellyfin host.
    if (path.startsWith('/reko/')) {
        return serveStatic(path.slice('/reko/'.length), res);
    }

    if (path.startsWith('/tmdb/3/')) {
        return handleTmdb(path.slice('/tmdb/3/'.length), q, res);
    }

    if (path.startsWith('/seerr/api/v1/')) {
        return handleSeerr(req, path.slice('/seerr/api/v1/'.length), q, res);
    }

    return send(res, 404, { status_message: 'not found' });
}

/**
 * Routes a TMDB request.
 *
 * @param {string} path The path after the /3/ prefix.
 * @param {URLSearchParams} q The query.
 * @param {http.ServerResponse} res The response.
 */
function handleTmdb(path, q, res) {
    if (path === 'configuration') {
        return send(res, 200, {
            images: {
                base_url: `${IMAGE}/`,
                secure_base_url: `${IMAGE}/`,
                poster_sizes: ['w92', 'w154', 'w185', 'w342', 'w500', 'w780', 'original'],
                backdrop_sizes: ['w300', 'w780', 'w1280', 'original'],
                still_sizes: ['w92', 'w185', 'w300', 'original'],
                profile_sizes: ['w45', 'w185', 'h632', 'original'],
                logo_sizes: ['w45', 'w92', 'w154', 'w185', 'w300', 'w500', 'original']
            },
            change_keys: []
        });
    }

    if (path.startsWith('genre/')) {
        const kind = path.split('/')[1];
        return send(res, 200, { genres: (kind === 'movie' ? MOVIE_GENRES : TV_GENRES).map(([id, name]) => ({ id, name })) });
    }

    if (path.startsWith('trending/')) {
        const [, kind, window] = path.split('/');
        const seed = window === 'day' ? 100 : 200;
        const isMovie = kind === 'movie';
        const isAll = kind === 'all';

        // Trending has no page parameter and always returns 20 items. Reko's Top 10 rails slice this
        // to 10, which is the behaviour being verified.
        const items = isAll
            ? makeList(seed, 20, true, true).map((item, index) => (index % 3 === 0 ? makeItem(seed + index, false, 'tv') : item))
            : makeList(seed, 20, isMovie, false);

        return send(res, 200, { page: 1, results: items, total_pages: 1000, total_results: 20000 });
    }

    if (path === 'discover/movie' || path === 'discover/tv') {
        const isMovie = path.endsWith('movie');
        const pageNumber = Number(q.get('page') ?? '1');
        const perPage = 20;
        const genres = (q.get('with_genres') ?? '').split(/[,|]/).map(Number).filter((n) => n > 0);
        const origin = q.get('with_origin_country') ?? q.get('with_original_language');
        const seedBase = 3000 + pageNumber * 100 + (isMovie ? 0 : 50) + (genres[0] ?? 0);

        let items = makeList(seedBase, perPage, isMovie, false);

        if (genres.length) {
            const map = isMovie ? MOVIE_GENRES : TV_GENRES;
            items = items.map((item) => ({ ...item, genre_ids: genres }));
            void map;
        }

        if (origin) {
            items = items.map((item) => ({ ...item, origin_country: [origin], original_language: origin.toLowerCase() }));
        }

        const sort = q.get('sort_by') ?? 'popularity.desc';
        items.sort((a, b) => {
            if (sort.startsWith('vote_average')) {
                return b.vote_average - a.vote_average;
            }

            if (sort.startsWith('primary_release_date') || sort.startsWith('first_air_date')) {
                return String(b.release_date ?? b.first_air_date ?? '').localeCompare(String(a.release_date ?? a.first_air_date ?? ''));
            }

            if (sort.startsWith('title') || sort.startsWith('name')) {
                return String(a.title ?? a.name).localeCompare(String(b.title ?? b.name));
            }

            return b.popularity - a.popularity;
        });

        return send(res, 200, page(items, pageNumber, 500, perPage));
    }

    if (path === 'movie/popular' || path === 'tv/popular') {
        const isMovie = path.startsWith('movie');
        return send(res, 200, page(makeList(isMovie ? 4100 : 4200, 20, isMovie), Number(q.get('page') ?? '1'), 500, 20));
    }

    if (path === 'tv/on_the_air') {
        return send(res, 200, page(makeList(4300, 20, false), Number(q.get('page') ?? '1'), 300, 20));
    }

    if (path === 'search/multi') {
        const query = (q.get('query') ?? '').toLowerCase();
        const items = [];

        for (let id = 1; id <= 200 && items.length < 20; id++) {
            for (const isMovie of [true, false]) {
                const item = makeItem(id * 3 + (isMovie ? 0 : 1), isMovie, isMovie ? 'movie' : 'tv');
                const haystack = `${item.title ?? item.name} ${item.overview}`.toLowerCase();
                if (query && haystack.includes(query)) {
                    items.push(item);
                }
            }

            if (items.length >= 20) {
                break;
            }
        }

        // Fall back to a generated set so a search for anything still returns a full grid, which is
        // what the empty-state and grid layout need in order to be checked.
        const filler = items.length > 0
            ? items
            : makeList(9000, 20, true, true).map((item, index) => ({
                ...item,
                media_type: index % 3 === 0 ? 'tv' : 'movie',
                title: index % 3 === 0 ? undefined : `${q.get('query')} ${item.title}`,
                name: index % 3 === 0 ? `${q.get('query')} ${item.name}` : undefined
            }));

        return send(res, 200, { page: 1, results: filler, total_pages: 1, total_results: 42 });
    }

    const season = path.match(/^tv\/(\d+)\/season\/(\d+)$/);
    if (season) {
        return send(res, 200, makeSeason(Number(season[1]), Number(season[2])));
    }

    const recommend = path.match(/^(movie|tv)\/(\d+)\/recommendations$/);
    if (recommend) {
        return send(res, 200, page(makeList(Number(recommend[2]) * 3 + 1, 20, recommend[1] === 'movie'), 1, 400, 20));
    }

    const collection = path.match(/^collection\/(\d+)$/);
    if (collection) {
        return send(res, 200, makeCollection(Number(collection[1])));
    }

    const person = path.match(/^person\/(\d+)$/);
    if (person) {
        return send(res, 200, makePerson(Number(person[1])));
    }

    const movie = path.match(/^movie\/(\d+)$/);
    if (movie) {
        return send(res, 200, makeMovieDetail(Number(movie[1])));
    }

    const tv = path.match(/^tv\/(\d+)$/);
    if (tv) {
        return send(res, 200, makeSeriesDetail(Number(tv[1])));
    }

    return send(res, 404, { status_message: 'no fixture for ' + path, status_code: 34 });
}

/**
 * Routes a Seerr request.
 *
 * @param {http.IncomingMessage} req The request.
 * @param {string} path The path after /api/v1/.
 * @param {URLSearchParams} q The query.
 * @param {http.ServerResponse} res The response.
 */
function handleSeerr(req, path, q, res) {
    if (path === 'status') {
        return send(res, 200, { version: '2.4.0', commitTag: 'fixture', restartRequired: false });
    }

    if (path === 'settings/public') {
        return send(res, 200, seerrSettings());
    }

    const jellyfinUser = path.match(/^user\/jellyfin\/(.+)$/);
    if (jellyfinUser) {
        return send(res, 200, {
            id: 2,
            email: 'viewer@localhost',
            username: 'viewer',
            displayName: 'Viewer',
            permissions: 32,
            userType: 3,
            jellyfinUsername: 'rekoadmin',
            jellyfinUserId: jellyfinUser[1],
            requestCount: 12
        });
    }

    if (path === 'request' && req.method === 'GET') {
        const results = [...SEERR_REQUESTS.entries()].map(([key, record]) => {
            const [mediaType, tmdbId] = key.split(':');
            return {
                id: record.id,
                status: record.status,
                media: { id: 7000 + Number(tmdbId), tmdbId: Number(tmdbId), mediaType, status: 5, status4k: 1, seasons: record.seasons },
                createdAt: record.createdAt,
                updatedAt: record.updatedAt,
                modifiedBy: null,
                is4k: false,
                profileId: null,
                rootFolder: null,
                languageProfileId: null,
                tags: [],
                seasons: record.seasons
            };
        });

        return send(res, 200, {
            pageInfo: { pages: 1, page: 1, results: results.length, pageSize: 500 },
            results
        });
    }

    if (path === 'request' && req.method === 'POST') {
        let body = '';
        req.on('data', (chunk) => { body += chunk; });
        req.on('end', () => {
            let parsed;
            try {
                parsed = JSON.parse(body || '{}');
            } catch {
                return send(res, 400, { message: 'invalid json' });
            }

            if (!parsed.mediaId || !['movie', 'tv'].includes(parsed.mediaType)) {
                return send(res, 400, { message: 'validation failed' });
            }

            // Overseerr runs express-openapi-validator with validateRequests, and `seasons` is a
            // oneOf of array, string and the literal "all". A JSON null matches none of the three,
            // so it is rejected before the route ever runs. Transcribed from the validator's own
            // wording, because the plugin shipped `seasons: null` on every movie request and the
            // permissive version of this mock was perfectly happy to accept it.
            if ('seasons' in parsed) {
                const valid = Array.isArray(parsed.seasons)
                    || typeof parsed.seasons === 'string'
                    || parsed.seasons === 'all';

                if (!valid) {
                    return send(res, 400, {
                        message: 'request/body/seasons must be array, request/body/seasons must be string, request/body/seasons must be equal to one of the allowed values: all, request/body/seasons must match exactly one schema in oneOf'
                    });
                }
            }

            const key = `${parsed.mediaType}:${parsed.mediaId}`;
            const seasons = (Array.isArray(parsed.seasons) ? parsed.seasons : []).map((seasonNumber) => ({ seasonNumber, status: 5 }));

            if (SEERR_REQUESTS.has(key)) {
                return send(res, 409, { message: 'Request for this media already exists.' });
            }

            const id = 900 + SEERR_REQUESTS.size;
            const record = { id, status: 2, createdAt: new Date().toISOString(), updatedAt: new Date().toISOString(), seasons };
            SEERR_REQUESTS.set(key, record);
            watchedTmdbIds.add(key);

            return send(res, 201, record);
        });
        return undefined;
    }

    const media = path.match(/^(movie|tv)\/(\d+)$/);
    if (media) {
        const record = seerrMedia(Number(media[2]), media[1]);
        return record ? send(res, 200, { mediaInfo: record, credits: { cast: makeCast(Number(media[2])), crew: [] } }) : send(res, 404, { message: 'not found' });
    }

    return send(res, 404, { message: 'no seerr fixture for ' + path });
}

/**
 * Serves a fixture image, so posters and backdrops are visible rather than broken.
 *
 * @param {string} name The requested file name.
 * @param {http.ServerResponse} res The response.
 */
function serveImage(name, res) {
    const isLogo = name.startsWith('logo') || name.includes('company') || name.includes('net.') || name.includes('play') || name.includes('roku');
    const isProfile = name.startsWith('profile') || name.startsWith('person');
    const isStill = name.startsWith('still') || name.startsWith('season');
    const isBackdrop = name.includes('backdrop') || name.startsWith('bd') || name.startsWith('sbd') || name.startsWith('colb');

    let w = 300;
    let h = 450;
    let bg = '#243447';
    let fg = '#7fa8d0';
    let label = name.replace(/\.(jpg|png)$/i, '').slice(0, 18);

    if (isLogo || name.includes('netflix') || name.includes('apple') || name.includes('hulu')) {
        w = 120; h = 40; bg = '#101820'; fg = '#e8e8e8'; label = 'LOGO';
    } else if (isProfile) {
        w = 200; h = 300; bg = '#33475b'; fg = '#9fc0e0';
    } else if (isStill || isBackdrop) {
        w = 640; h = 360; bg = '#1b2733'; fg = '#88a9c9';
    } else {
        w = 400; h = 600;
    }

    const svg = `<svg xmlns="http://www.w3.org/2000/svg" width="${w}" height="${h}" viewBox="0 0 ${w} ${h}">`
        + `<rect width="${w}" height="${h}" fill="${bg}"/>`
        + `<rect x="8" y="8" width="${w - 16}" height="${h - 16}" fill="none" stroke="${fg}" stroke-width="2" opacity="0.5"/>`
        + `<text x="50%" y="50%" fill="${fg}" font-family="sans-serif" font-size="${Math.round(w / 12)}" text-anchor="middle" dominant-baseline="middle">${label}</text>`
        + `</svg>`;

    res.writeHead(200, { 'Content-Type': 'image/svg+xml', 'Cache-Control': 'public, max-age=600' });
    res.end(svg);
}

/**
 * Serves a Reko static asset from disk, so the fixture can also stand in for the server.
 *
 * @param {string} rel The path relative to the Reko web root.
 * @param {http.ServerResponse} res The response.
 */
function serveStatic(rel, res) {
    const fs = require('node:fs');
    const path = require('node:path');

    const root = process.env.REKO_WEB_ROOT;
    if (!root) {
        return send(res, 404, { error: 'REKO_WEB_ROOT not set' });
    }

    const safe = rel.replace(/\.\./g, '');
    const file = path.join(root, safe);
    if (!fs.existsSync(file)) {
        return send(res, 404, { error: 'not found: ' + safe });
    }

    const types = { '.js': 'text/javascript; charset=utf-8', '.mjs': 'text/javascript; charset=utf-8', '.css': 'text/css; charset=utf-8', '.html': 'text/html; charset=utf-8' };
    res.writeHead(200, { 'Content-Type': types[path.extname(file)] ?? 'application/octet-stream' });
    res.end(fs.readFileSync(file));
}

// The TMDB image CDN is referenced with absolute URLs inside payloads. The fixture rewrites them to
// itself so images actually render in a browser test.
http.createServer((req, res) => {
    if (req.url.startsWith('/image/')) {
        return serveImage(req.url.slice('/image/'.length).split('/').pop(), res);
    }

    return handle(req, res);
}).listen(PORT, '127.0.0.1', () => {
    console.log(`mock tmdb + seerr listening on http://127.0.0.1:${PORT}`);
});
