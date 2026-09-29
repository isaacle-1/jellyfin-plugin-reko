/**
 * The HTTP client for Reko's own endpoints.
 *
 * Two things matter here. First, jellyfin-web 12 removed the legacy `X-Emby-Token` header and the
 * `api_key` query parameter, so the token must travel as `?ApiKey=` or in an `Authorization`
 * header. `ApiClient.getUrl` already does the right thing, so every URL is built through it rather
 * than by hand. Second, the TMDB and Seerr credentials live on the server; nothing in this file ever
 * sees them.
 */

/**
 * Returns the active Jellyfin API client.
 *
 * @returns {Object|null} The API client, or null before the app has booted.
 */
function apiClient() {
    return window.ApiClient ?? null;
}

/**
 * Builds a URL for a Reko endpoint, including the server's base URL and the auth token.
 *
 * @param {string} name The endpoint name, for example `home`.
 * @param {Object} [params] Query parameters. Undefined and null values are dropped.
 * @returns {string} The URL.
 */
export function url(name, params = null) {
    const client = apiClient();
    if (!client) {
        throw new Error('The Jellyfin API client is not available yet.');
    }

    const clean = {};
    for (const [key, value] of Object.entries(params ?? {})) {
        if (value !== null && value !== undefined && value !== '') {
            clean[key] = value;
        }
    }

    return client.getUrl(`Reko/${name}`, clean);
}

/**
 * Performs a GET request and parses the JSON response.
 *
 * @param {string} name The endpoint name.
 * @param {Object} [params] Query parameters.
 * @returns {Promise<Object>} The parsed response.
 */
export async function get(name, params = null) {
    const client = apiClient();
    if (!client) {
        throw new Error('The Jellyfin API client is not available yet.');
    }

    // ApiClient.ajax is used rather than fetch because it is the one path jellyfin-web guarantees
    // to attach credentials to, across every layout and every auth mode the server supports.
    return client.ajax({
        type: 'GET',
        url: url(name, params),
        dataType: 'json',
        headers: { accept: 'application/json' }
    });
}

/**
 * Performs a POST request with a JSON body.
 *
 * @param {string} name The endpoint name.
 * @param {Object} body The body.
 * @returns {Promise<Object>} The parsed response.
 */
export async function post(name, body) {
    const client = apiClient();
    if (!client) {
        throw new Error('The Jellyfin API client is not available yet.');
    }

    return client.ajax({
        type: 'POST',
        url: url(name),
        data: JSON.stringify(body),
        dataType: 'json',
        contentType: 'application/json',
        headers: { accept: 'application/json' }
    });
}

/**
 * Normalises an error into a message worth showing a person.
 *
 * Jellyfin's ApiClient rejects with a parsed error object for its own endpoints, and Seerr-shaped
 * messages can arrive as plain strings. Both are handled so the UI never shows "[object Object]".
 *
 * @param {unknown} error The rejection value.
 * @returns {string} The message.
 */
export function messageOf(error) {
    if (!error) {
        return 'Something went wrong.';
    }

    if (typeof error === 'string') {
        return error;
    }

    const candidates = [
        error?.message,
        error?.Message,
        error?.error?.message,
        error?.response?.message
    ];

    for (const candidate of candidates) {
        if (typeof candidate === 'string' && candidate.trim()) {
            return candidate;
        }
    }

    if (typeof error?.status === 'number') {
        return `Request failed with status ${error.status}.`;
    }

    return 'Something went wrong.';
}

/** Fetches the Reko bootstrap payload. */
export const bootstrap = () => get('bootstrap');

/** Fetches the whole home page payload. */
export const home = () => get('home');

/**
 * Fetches a single rail.
 *
 * @param {string} railId The rail id.
 * @returns {Promise<Object>} The rail.
 */
export const rail = (railId) => get(`rail/${encodeURIComponent(railId)}`);

/**
 * Fetches a title page.
 *
 * @param {number} id The TMDB id.
 * @param {string} type `movie` or `tv`.
 * @returns {Promise<Object>} The title payload.
 */
export const title = (id, type) => get('title', { id, type });

/**
 * Fetches a season's episodes.
 *
 * @param {number} seriesId The TMDB series id.
 * @param {number} season The season number.
 * @returns {Promise<Object>} The season payload.
 */
export const season = (seriesId, season) => get('season', { seriesId, season });

/**
 * Fetches a person.
 *
 * @param {number} id The TMDB person id.
 * @returns {Promise<Object>} The person payload.
 */
export const person = id => get('person', { id });

/**
 * Runs a search.
 *
 * @param {string} query The search text.
 * @param {number} [page] The 1-based page number.
 * @returns {Promise<Object>} The results.
 */
export const search = (query, page = 1) => get('search', { query, page });

/**
 * Runs a category browse.
 *
 * @param {Object} params The browse parameters.
 * @returns {Promise<Object>} The results.
 */
export const browse = (params) => get('browse', params);

/** Fetches the Seerr status. */
export const seerrStatus = () => get('seerr/status');

/**
 * Submits a request through Seerr.
 *
 * @param {Object} body The request body.
 * @returns {Promise<Object>} The outcome.
 */
export const seerrRequest = (body) => post('seerr/request', body);

/**
 * Refreshes the Seerr state of several titles.
 *
 * @param {number[]} ids The TMDB ids.
 * @returns {Promise<Object>} The states.
 */
export const seerrStates = (ids) => get('seerr/states', { ids: ids.join(',') });
