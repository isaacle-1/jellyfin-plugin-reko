/**
 * The Reko configuration page controller.
 *
 * Registered as a `PluginPageInfo` whose embedded resource ends in `.js`, so jellyfin-web loads this
 * file with a dynamic `import()` and calls its default export with the page element. That is the
 * 12.x-supported way to attach behaviour to a plugin page; there is no `data-controller` script tag
 * convention in the modern client.
 *
 * The page is plain markup plus `ApiClient.getPluginConfiguration`, which is the same contract every
 * other plugin config page uses, so it keeps working across layouts.
 */

const PLUGIN_ID = 'b3f19d2c-7a41-4e58-9c06-1d5a8e3f2b47';

/** Fields that must be written back as numbers, with their bounds. */
const NUMERIC = {
    HeroCount: [1, 10],
    HeroRotationSeconds: [3, 60],
    ItemsPerRail: [6, 40],
    ContinueWatchingCount: [4, 30],
    PersonalizedRailCount: [1, 6],
    MinimumWatchedForPersonalized: [1, 50],
    CacheMinutes: [5, 1440],
    TrendingCacheMinutes: [5, 720],
    DetailCacheHours: [1, 168],
    PersonalizedCacheMinutes: [15, 10080]
};

/** Fields that are written back as booleans. */
const BOOLEAN = [
    'EnableSeerr',
    'SeerrMapJellyfinUsers',
    'EnableTab',
    'EnableHoverPreviews',
    'EnableContinueWatching',
    'EnablePersonalizedRows',
    'HideWatchedFromPersonalized',
    'EnableSearch',
    'ShowCast',
    'ShowTrailers',
    'ShowWatchProviders',
    'IncludeMovies',
    'IncludeSeries',
    'EnableDebugLogging'
];

/** Fields that are written back as trimmed strings. */
const TEXT = [
    'TabLabel',
    'TmdbApiKey',
    'TmdbBaseUrl',
    'TmdbImageBaseUrl',
    'TmdbLanguage',
    'TmdbImageLanguage',
    'TmdbRegion',
    'SeerrUrl',
    'SeerrApiKey',
    'RailOrder'
];

/**
 * Reads the trimmed value of a text or number input.
 *
 * @param {HTMLElement} page The page element.
 * @param {string} id The element id.
 * @returns {string} The value.
 */
function readValue(page, id) {
    const node = page.querySelector('#' + id);
    return node ? String(node.value ?? '').trim() : '';
}

/**
 * Shows a transient message in Jellyfin's toast area.
 *
 * @param {HTMLElement} page The page element.
 * @param {string} message The message.
 */
function toast(page, message) {
    if (typeof window.Dashboard?.showLoadingMsg === 'function') {
        window.Dashboard.showLoadingMsg(message);
        return;
    }

    const banner = document.createElement('div');
    banner.className = 'fieldDescription';
    banner.setAttribute('role', 'status');
    banner.textContent = message;
    page.querySelector('#RekoConfigForm')?.prepend(banner);
    setTimeout(() => banner.remove(), 6000);
}

/**
 * Renders the configuration into the form.
 *
 * @param {HTMLElement} page The page element.
 * @param {Object} config The plugin configuration.
 */
function fill(page, config) {
    for (const [id, [min, max]] of Object.entries(NUMERIC)) {
        const node = page.querySelector('#' + id);
        if (!node) {
            continue;
        }

        const value = Number(config[id]);
        node.value = Number.isFinite(value) ? String(Math.min(max, Math.max(min, value))) : String(min);
    }

    for (const id of BOOLEAN) {
        const node = page.querySelector('#' + id);
        if (node) {
            node.checked = config[id] === true;
        }
    }

    for (const id of TEXT) {
        const node = page.querySelector('#' + id);
        if (node) {
            node.value = config[id] ?? '';
        }
    }

    // The Seerr section is pointless while requests are switched off, so it is hidden rather than
    // left on screen looking like it does something.
    const seerrBlock = page.querySelector('#EnableSeerr')?.closest('.verticalSection');
    const applyVisibility = () => {
        const enabled = page.querySelector('#EnableSeerr')?.checked === true;
        if (seerrBlock) {
            seerrBlock.style.opacity = enabled ? '1' : '0.55';
        }
    };

    page.querySelector('#EnableSeerr')?.addEventListener('change', applyVisibility);
    applyVisibility();
}

/**
 * Builds the configuration to save from the form.
 *
 * @param {HTMLElement} page The page element.
 * @param {Object} config The existing configuration, so untouched fields survive.
 * @returns {Object} The configuration to save.
 */
function collect(page, config) {
    const result = { ...config };

    for (const [id, [min, max]] of Object.entries(NUMERIC)) {
        const parsed = Number.parseInt(readValue(page, id), 10);
        result[id] = Number.isFinite(parsed) ? Math.min(max, Math.max(min, parsed)) : min;
    }

    for (const id of BOOLEAN) {
        result[id] = page.querySelector('#' + id)?.checked === true;
    }

    for (const id of TEXT) {
        result[id] = readValue(page, id);
    }

    // An empty tab label would render a blank tab button, so fall back rather than save it.
    if (!result.TabLabel) {
        result.TabLabel = 'Reko';
    }

    // An unparseable region would silently produce no watch providers, so normalise it.
    const region = (result.TmdbRegion || 'US').toUpperCase();
    result.TmdbRegion = /^[A-Z]{2}$/.test(region) ? region : 'US';

    return result;
}

/**
 * The page controller.
 *
 * @param {HTMLElement} page The page element.
 */
export default function RekoConfigPage(page) {
    const form = page.querySelector('#RekoConfigForm');
    if (!form) {
        return;
    }

    // Hold the form back until the real values are in. A form briefly showing defaults is how a
    // careless save overwrites working settings.
    page.classList.add('is-loading');

    if (typeof window.Dashboard?.showLoadingMsg === 'function') {
        window.Dashboard.showLoadingMsg();
    }

    window.ApiClient.getPluginConfiguration(PLUGIN_ID)
        .then((config) => {
            fill(page, config);
            page.classList.remove('is-loading');

            if (typeof window.Dashboard?.hideLoadingMsg === 'function') {
                window.Dashboard.hideLoadingMsg();
            }

            if (!config.TmdbApiKey) {
                toast(page, 'Reko needs a TMDB API Read Access Token before it can show anything.');
            }
        })
        .catch((error) => {
            page.classList.remove('is-loading');

            if (typeof window.Dashboard?.hideLoadingMsg === 'function') {
                window.Dashboard.hideLoadingMsg();
            }

            toast(page, `Could not load the Reko settings: ${error?.message ?? error}`);
        });

    form.addEventListener('submit', (event) => {
        event.preventDefault();

        window.ApiClient.getPluginConfiguration(PLUGIN_ID)
            .then((config) => {
                const updated = collect(page, config);

                window.ApiClient.updatePluginConfiguration(PLUGIN_ID, updated)
                    .then((result) => {
                        if (typeof window.Dashboard?.processPluginConfigurationUpdateResult === 'function') {
                            window.Dashboard.processPluginConfigurationUpdateResult(result);
                        }

                        toast(page, 'Reko settings saved. Reload the web page to pick up changes.');
                    })
                    .catch((error) => toast(page, `Could not save: ${error?.message ?? error}`));
            })
            .catch((error) => toast(page, `Could not load the current settings: ${error?.message ?? error}`));
    });
}
