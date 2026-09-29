/**
 * Small DOM and formatting helpers.
 *
 * Everything here is deliberately dependency free. Reko is injected into a page it does not own and
 * must not assume a build step, a framework, or a global that jellyfin-web might rename.
 */

/**
 * Creates an element.
 *
 * @param {string} tag The tag name, optionally with .class names appended.
 * @param {Object} [props] Properties. `class`, `text`, `html`, `dataset`, `style` and `on` are special.
 * @param {Array<Node|string|null|undefined>} [children] Child nodes or text.
 * @returns {HTMLElement} The element.
 */
export function el(tag, props = null, children = null) {
    const [name, ...classes] = tag.split('.');
    const node = document.createElement(name || 'div');

    if (classes.length) {
        node.className = classes.join(' ');
    }

    if (props) {
        for (const [key, value] of Object.entries(props)) {
            if (value === null || value === undefined || value === false) {
                continue;
            }

            switch (key) {
                case 'class':
                    node.className = node.className ? `${node.className} ${value}` : String(value);
                    break;
                case 'text':
                    node.textContent = String(value);
                    break;
                case 'html':
                    node.innerHTML = String(value);
                    break;
                case 'dataset':
                    for (const [dk, dv] of Object.entries(value)) {
                        if (dv !== null && dv !== undefined) {
                            node.dataset[dk] = String(dv);
                        }
                    }

                    break;
                case 'style':
                    Object.assign(node.style, value);
                    break;
                case 'on':
                    for (const [event, handler] of Object.entries(value)) {
                        node.addEventListener(event, handler);
                    }

                    break;
                case 'attrs':
                    for (const [an, av] of Object.entries(value)) {
                        if (av !== null && av !== undefined && av !== false) {
                            node.setAttribute(an, String(av));
                        }
                    }

                    break;
                default:
                    node[key] = value;
                    break;
            }
        }
    }

    appendAll(node, children);
    return node;
}

/**
 * Appends children, skipping nullish entries and flattening arrays.
 *
 * @param {Node} parent The parent node.
 * @param {Array<Node|string|null|undefined>|Node|string|null} children The children.
 */
export function appendAll(parent, children) {
    if (children === null || children === undefined) {
        return;
    }

    if (Array.isArray(children)) {
        for (const child of children) {
            appendAll(parent, child);
        }

        return;
    }

    parent.appendChild(
        children instanceof Node ? children : document.createTextNode(String(children))
    );
}

/**
 * Removes every child of a node.
 *
 * @param {Node} node The node to empty.
 * @returns {Node} The same node.
 */
export function empty(node) {
    while (node.firstChild) {
        node.removeChild(node.firstChild);
    }

    return node;
}

/**
 * Whether an element is actually rendered.
 *
 * An element inside Jellyfin's `display: none` legacy header still satisfies every selector, which
 * is the single biggest source of "why did it inject in the wrong place" bugs here. Checking
 * visibility is what makes the Modern and Legacy layouts separable.
 *
 * @param {Element|null} node The element.
 * @returns {boolean} True when the element is visible.
 */
export function isVisible(node) {
    if (!node) {
        return false;
    }

    if (node.getClientRects().length > 0) {
        return true;
    }

    // getClientRects is empty for detached nodes too, which is the case we want to reject.
    return false;
}

/**
 * Whether the Modern layout is active.
 *
 * Modern is the default from Jellyfin 12.0. It keeps the legacy header mounted but hidden, and what
 * receives `display: none` is an *ancestor* of `.skinHeader`, not the header itself.
 *
 * There can be more than one `.skinHeader` in the DOM at once, so the question is not "is the first
 * one hidden" but "is any header visible at all". A header that is itself visible with no hidden
 * ancestor means the legacy chrome is on screen, which is the Legacy or TV layout.
 *
 * @returns {boolean} True in the Modern layout.
 */
export function isModernLayout() {
    const headers = document.querySelectorAll('.skinHeader');
    if (headers.length === 0) {
        return true;
    }

    for (const header of headers) {
        let node = header;
        let hidden = false;

        while (node && node !== document.documentElement) {
            if (getComputedStyle(node).display === 'none') {
                hidden = true;
                break;
            }

            node = node.parentElement;
        }

        if (!hidden) {
            return false;
        }
    }

    return true;
}

/**
 * Formats a runtime in minutes as hours and minutes.
 *
 * @param {number|null|undefined} minutes The runtime.
 * @returns {string} For example "2h 16m", or an empty string.
 */
export function formatRuntime(minutes) {
    if (!minutes || minutes <= 0) {
        return '';
    }

    const hours = Math.floor(minutes / 60);
    const rest = Math.round(minutes % 60);
    if (hours <= 0) {
        return `${rest}m`;
    }

    return rest > 0 ? `${hours}h ${rest}m` : `${hours}h`;
}

/**
 * Formats a TMDB date as a year.
 *
 * @param {string|null|undefined} value A yyyy-MM-dd date.
 * @returns {string} The year, or an empty string.
 */
export function yearOf(value) {
    return value && value.length >= 4 ? value.slice(0, 4) : '';
}

/**
 * Builds a meta line such as "2023 · R · 2h 16m · 8.2".
 *
 * @param {Object} card The card.
 * @param {Object} [options] Options. `showMatch` replaces the rating with an invented "match" score,
 *   which Reko never does; it is here so the caller can leave it off.
 * @returns {string} The meta line.
 */
export function metaLine(card, options = {}) {
    const parts = [];
    const year = yearOf(card?.date) || (card?.year ? String(card.year) : '');
    if (year) {
        parts.push(year);
    }

    if (card?.certification) {
        parts.push(card.certification);
    }

    if (card?.type === 'tv' && card.episodeRuntime) {
        parts.push(`${card.episodeRuntime}m episodes`);
    } else {
        const runtime = formatRuntime(card?.runtime);
        if (runtime) {
            parts.push(runtime);
        }
    }

    if (!options.hideRating && card?.voteCount > 0) {
        parts.push(`${card.rating.toFixed(1)} ★`);
    }

    if (card?.type === 'tv' && card.seasonCount) {
        parts.push(`${card.seasonCount} season${card.seasonCount === 1 ? '' : 's'}`);
    }

    return parts.filter(Boolean).join('  ·  ');
}

/**
 * Debounces a function.
 *
 * @param {Function} fn The function.
 * @param {number} wait The wait in milliseconds.
 * @returns {Function} The debounced function, with a `cancel` method.
 */
export function debounce(fn, wait) {
    let timer = null;

    const wrapped = (...args) => {
        if (timer !== null) {
            clearTimeout(timer);
        }

        timer = setTimeout(() => {
            timer = null;
            fn(...args);
        }, wait);
    };

    wrapped.cancel = () => {
        if (timer !== null) {
            clearTimeout(timer);
            timer = null;
        }
    };

    return wrapped;
}

/**
 * Escapes text for use in an attribute value.
 *
 * @param {string} value The value.
 * @returns {string} The escaped value.
 */
export function escapeAttr(value) {
    return String(value)
        .replaceAll('&', '&amp;')
        .replaceAll('"', '&quot;')
        .replaceAll('<', '&lt;')
        .replaceAll('>', '&gt;');
}

/**
 * A tiny namespaced logger, so Reko's messages are identifiable in a shared console.
 *
 * Verbose output is opt-in through the `rekoDebug` local storage key rather than a constructor
 * argument, because the decision of whether to trace is made in code that runs before any
 * configuration has been fetched.
 *
 * @returns {Object} The logger.
 */
export function createLogger() {
    const tag = '%c[Reko]';
    const style = 'color:#e50914;font-weight:700';

    const debugEnabled = () => {
        try {
            return window.localStorage?.getItem('rekoDebug') === '1';
        } catch {
            return false;
        }
    };

    return {
        info: (...args) => console.log(tag, style, ...args),
        warn: (...args) => console.warn(tag, style, ...args),
        error: (...args) => console.error(tag, style, ...args),
        debug: (...args) => {
            if (debugEnabled()) {
                // console.info rather than console.debug: the debug level is hidden by default in
                // browser developer tools and is dropped by most log capture, so an opt-in trace that
                // cannot be seen is not a trace.
                console.info(tag, style, ...args);
            }
        }
    };
}
