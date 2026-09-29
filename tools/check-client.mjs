#!/usr/bin/env node
/**
 * A smoke test for Reko's browser modules.
 *
 * `node --check` only proves a file parses. It does not catch a reference to an identifier that no
 * longer exists, which is the failure mode that actually bit this codebase: a rename left one function
 * referring to a variable that had been renamed with it, and the whole client died at runtime on the
 * first render with a ReferenceError that no amount of reading the diff made obvious.
 *
 * So every module is imported against a minimal DOM shim and then poked. Anything the module does at
 * import time runs, which surfaces broken imports and top-level mistakes, and the exported surface is
 * checked for the functions the app actually calls.
 */

import { copyFileSync, mkdirSync, readdirSync, rmSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const webRoot = join(here, '..', 'Jellyfin.Plugin.Reko', 'Web');
const tmp = join(here, '..', '.reko-jscheck');

/**
 * A very small DOM shim. Enough for module-level code and for the exported helpers to be called
 * without a browser.
 *
 * It is not a toy: selector matching is real, because `inject.js` is nothing but selector matching.
 * Every layout decision Reko makes — which panel is on screen, whether the tab strip or the app bar
 * is in use, which index is free — is a query against markup this file has to reproduce, and a stub
 * that answers `null` to everything cannot tell a working injector from a broken one.
 */

/**
 * Matches one compound selector, such as `a.MuiButton-root[href*="tab=1"]`, against a node.
 *
 * @param {object} node The node.
 * @param {string} compound The compound selector.
 * @returns {boolean} True when every part of the selector matches.
 */
function matchesCompound(node, compound) {
    const tokens = compound.match(/[.#]?[\w-]+|\[[^\]]+\]/g) ?? [];

    return tokens.every((token) => {
        if (token.startsWith('.')) {
            return node.classList.contains(token.slice(1));
        }

        if (token.startsWith('#')) {
            return node.getAttribute('id') === token.slice(1);
        }

        if (token.startsWith('[')) {
            const parsed = token.slice(1, -1).match(/^([\w-]+)(?:([*^$~|]?)=["']?([^"']*)["']?)?$/);
            if (!parsed) {
                return false;
            }

            const [, name, operator, wanted] = parsed;
            const actual = node.getAttribute(name);

            // `undefined` means the attribute was only required to exist. An empty operator is
            // `=`, not "absent": the optional group matches either nothing at all or an operator,
            // and conflating the two makes every attribute selector on the page match everything.
            if (operator === undefined) {
                return actual !== null;
            }

            if (actual === null) {
                return false;
            }

            switch (operator) {
                case '*':
                    return actual.includes(wanted);
                case '^':
                    return actual.startsWith(wanted);
                case '$':
                    return actual.endsWith(wanted);
                case '~':
                    return actual.split(/\s+/).includes(wanted);
                default:
                    return actual === wanted;
            }
        }

        return node.tagName === token.toUpperCase();
    });
}

/**
 * Matches a full selector, including comma-separated lists and descendant combinators.
 *
 * @param {object} node The node.
 * @param {string} selector The selector.
 * @returns {boolean} True when the node matches.
 */
function matchesSelector(node, selector) {
    return String(selector)
        .split(',')
        .some((part) => {
            const chain = part.trim().split(/\s+/).filter(Boolean);
            let current = node;

            for (let i = chain.length - 1; i >= 0; i--) {
                if (!current || !matchesCompound(current, chain[i])) {
                    return false;
                }

                current = current.parentElement;
            }

            return true;
        });
}

function installDomShim() {
    // A DOMTokenList, including iteration, because the production code reads `className` back out
    // after setting it in pieces.
    class FakeClassList {
        constructor() {
            this.tokens = new Set();
        }

        add(...names) {
            names.forEach((n) => this.tokens.add(n));
        }

        remove(...names) {
            names.forEach((n) => this.tokens.delete(n));
        }

        toggle(name, force) {
            const on = force === undefined ? !this.tokens.has(name) : Boolean(force);
            if (on) {
                this.tokens.add(name);
            } else {
                this.tokens.delete(name);
            }

            return on;
        }

        contains(name) {
            return this.tokens.has(name);
        }

        forEach(callback, thisArg) {
            [...this.tokens].forEach((name) => callback.call(thisArg, name, name, this));
        }

        get length() {
            return this.tokens.size;
        }

        item(index) {
            return [...this.tokens][index] ?? null;
        }

        [Symbol.iterator]() {
            return this.tokens[Symbol.iterator]();
        }

        toString() {
            return [...this.tokens].join(' ');
        }
    }

    const makeClassList = () => new FakeClassList();

    /**
     * A stand-in for CSSStyleDeclaration, carrying the custom-property API as well as the plain
     * properties.
     *
     * Custom properties are not reachable by assignment: `style['--x'] = '1px'` is a plain field on
     * the object and `style.getPropertyValue('--x')` returns nothing, so a shim without this lets a
     * module look like it published something when it published nothing at all.
     *
     * @param {object} [initial] Properties to start with.
     * @returns {object} The style object.
     */
    function makeStyle(initial = null) {
        const custom = new Map();
        const style = {};

        if (initial) {
            for (const [name, value] of Object.entries(initial)) {
                if (name.startsWith('--')) {
                    custom.set(name, value);
                } else {
                    style[name] = value;
                }
            }
        }

        style.setProperty = (name, value) => custom.set(name, String(value));
        style.getPropertyValue = (name) => custom.get(name) ?? '';
        style.removeProperty = (name) => custom.delete(name);

        return style;
    }

    /**
     * Copies a node deeply, elements and text alike.
     *
     * @param {object} node The node to copy.
     * @returns {object} An independent copy.
     */
    function cloneShallow(node) {
        if (typeof node.cloneNode === 'function') {
            return node.cloneNode(true);
        }

        return { nodeType: 3, textContent: String(node.textContent ?? ''), parentElement: null };
    }

    class FakeElement {
        constructor(tag = 'div') {
            this.tagName = String(tag).toUpperCase();
            this.children = [];
            this.childNodes = this.children;
            this.parentElement = null;
            this.classList = makeClassList();
            this.dataset = {};

            // A real CSSStyleDeclaration, not a plain object: Reko publishes a custom property with
            // setProperty and reads it back with getPropertyValue, and a bare object silently loses
            // both, so the code under test would look like it ran and had done nothing.
            this.style = makeStyle();
            this.attributes = {};
            this.value = '';
            this.checked = false;
            this.disabled = false;
            this.hidden = false;
            this.listeners = {};
            this.ownText = '';
        }

        get className() {
            return this.classList.toString();
        }

        set className(value) {
            this.classList = makeClassList();
            String(value ?? '')
                .split(/\s+/)
                .filter(Boolean)
                .forEach((n) => this.classList.add(n));
            this.attributes.class = this.className;
        }

        // The DOM aggregates descendant text, and Reko reads it back when it looks for Jellyfin's
        // own labels ("Favorites") so it can clone the right element.
        get textContent() {
            return this.ownText + this.children.map((c) => c.textContent ?? '').join('');
        }

        set textContent(value) {
            this.children.length = 0;
            this.ownText = String(value ?? '');
        }

        get firstChild() {
            return this.children[0] ?? null;
        }

        get nextSibling() {
            const siblings = this.parentElement?.children;
            if (!siblings) {
                return null;
            }

            return siblings[siblings.indexOf(this) + 1] ?? null;
        }

        get previousSibling() {
            const siblings = this.parentElement?.children;
            if (!siblings) {
                return null;
            }

            return siblings[siblings.indexOf(this) - 1] ?? null;
        }

        get isConnected() {
            let node = this;
            while (node.parentElement) {
                node = node.parentElement;
            }

            return node === globalThis.document?.body || node === globalThis.document?.documentElement;
        }

        appendChild(child) {
            child.parentElement?.removeChild(child);
            this.children.push(child);

            if (child && typeof child === 'object') {
                child.parentElement = this;
            }

            return child;
        }

        append(...nodes) {
            nodes.forEach((n) => this.appendChild(n));
        }

        prepend(...nodes) {
            nodes.reverse().forEach((n) => {
                n.parentElement?.removeChild(n);
                this.children.unshift(n);

                if (n && typeof n === 'object') {
                    n.parentElement = this;
                }
            });
        }

        insertBefore(node, reference) {
            if (!reference) {
                return this.appendChild(node);
            }

            node.parentElement?.removeChild(node);
            this.children.splice(this.children.indexOf(reference), 0, node);
            node.parentElement = this;
            return node;
        }

        removeChild(child) {
            const index = this.children.indexOf(child);
            if (index >= 0) {
                this.children.splice(index, 1);
                child.parentElement = null;
            }

            return child;
        }

        replaceChildren(...nodes) {
            [...this.children].forEach((c) => this.removeChild(c));
            this.append(...nodes);
        }

        replaceWith(node) {
            const parent = this.parentElement;
            if (parent) {
                const index = parent.children.indexOf(this);
                if (index >= 0) {
                    parent.children[index] = node;
                    node.parentElement = parent;
                }
            }
        }

        remove() {
            this.parentElement?.removeChild(this);
        }

        cloneNode(deep = false) {
            const copy = new FakeElement(this.tagName.toLowerCase());
            copy.attributes = { ...this.attributes };
            copy.classList = makeClassList();
            this.classList.forEach((n) => copy.classList.add(n));
            copy.ownText = this.ownText;
            copy.style = makeStyle(this.style);
            copy.hidden = this.hidden;
            copy.href = this.href;

            if (deep) {
                // Text nodes are copied, not shared. Sharing one would silently unhook it from the
                // node it was cloned from the moment the copy is appended, because appending
                // re-parents — and a label disappearing from Jellyfin's own markup is precisely the
                // kind of bug this shim is here to make visible.
                this.children.forEach((child) => copy.appendChild(cloneShallow(child)));
            }

            return copy;
        }

        setAttribute(name, value) {
            // `className` and the class attribute are the same thing in a real DOM, and Reko's
            // element helper relies on that. Modelling them separately is what let a panel lose its
            // own class without anything noticing.
            if (name === 'class') {
                this.className = value;
                return;
            }

            this.attributes[name] = String(value);
        }

        getAttribute(name) {
            if (name === 'class') {
                return this.className;
            }

            return Object.prototype.hasOwnProperty.call(this.attributes, name)
                ? this.attributes[name]
                : null;
        }

        removeAttribute(name) {
            if (name === 'class') {
                this.className = '';
                return;
            }

            delete this.attributes[name];
        }

        hasAttribute(name) {
            return this.getAttribute(name) !== null;
        }

        matches(selector) {
            return matchesSelector(this, selector);
        }

        closest(selector) {
            let node = this;
            while (node) {
                if (node.matches?.(selector)) {
                    return node;
                }

                node = node.parentElement;
            }

            return null;
        }

        querySelectorAll(selector) {
            const found = [];
            const walk = (node) => {
                for (const child of node.children) {
                    // Text nodes are children here too, and a selector never matches one.
                    if (typeof child.matches !== 'function') {
                        continue;
                    }

                    if (matchesSelector(child, selector)) {
                        found.push(child);
                    }

                    walk(child);
                }
            };

            walk(this);
            return found;
        }

        querySelector(selector) {
            return this.querySelectorAll(selector)[0] ?? null;
        }

        getClientRects() {
            return this.hidden ? [] : [{}];
        }

        getBoundingClientRect() {
            // `box` lets a test say how big an element is. Jellyfin's two headers differ only in
            // size — 48px for the Modern app bar, more for the Legacy one — and that difference is
            // the whole of what the sticky offset depends on.
            if (this.box) {
                const { width = 0, height = 0, top = 0, left = 0 } = this.box;
                return { top, left, width, height, right: left + width, bottom: top + height };
            }

            return this.hidden
                ? { top: 0, left: 0, width: 0, height: 0, right: 0, bottom: 0 }
                : { top: 0, left: 0, width: 800, height: 600, right: 800, bottom: 600 };
        }

        contains(node) {
            let current = node;
            while (current) {
                if (current === this) {
                    return true;
                }

                current = current.parentElement;
            }

            return false;
        }

        addEventListener(type, handler) {
            (this.listeners[type] ??= []).push(handler);
        }

        removeEventListener() {}

        focus() {}

        click() {
            (this.listeners.click ?? []).forEach((h) => h({ type: 'click', target: this }));
        }
    }

    const body = new FakeElement('body');
    const documentElement = new FakeElement('html');
    documentElement.appendChild(body);

    globalThis.document = {
        body,
        documentElement,
        activeElement: null,
        hidden: false,
        createElement: (tag) => new FakeElement(tag),
        createElementNS: (ns, tag) => new FakeElement(tag),
        createTextNode: (text) => ({ nodeType: 3, textContent: String(text), parentElement: null }),
        createRange: () => ({ createContextualFragment: () => new FakeElement() }),
        querySelector: (selector) => body.querySelector(selector),
        querySelectorAll: (selector) => body.querySelectorAll(selector),
        getElementById: (id) => body.querySelector(`#${id}`),
        addEventListener() {},
        removeEventListener() {}
    };

    // A real class, because the modules use `instanceof Node` when deciding whether a value is a DOM
    // node or a string.
    class Node {
        static ELEMENT_NODE = 1;

        static TEXT_NODE = 3;
    }

    globalThis.Node = Node;
    Object.setPrototypeOf(FakeElement.prototype, Node.prototype);
    globalThis.MutationObserver = class {
        observe() {}

        disconnect() {}
    };

    globalThis.ResizeObserver = class {
        observe() {}

        disconnect() {}
    };

    globalThis.requestAnimationFrame = (fn) => setTimeout(fn, 0);
    globalThis.getComputedStyle = () => ({ display: 'block', visibility: 'visible', opacity: '1' });

    // Timers are unref'd rather than removed. Reko uses them to poll for the Jellyfin API client, to
    // debounce search, and to rotate the hero, and client.js's boot poller alone keeps the process
    // alive for thirty seconds after every check has passed — which is a slow CI run that looks
    // exactly like a hung one. Unref'd timers still fire; they just cannot hold the process open.
    const realSetTimeout = globalThis.setTimeout;
    const realSetInterval = globalThis.setInterval;
    globalThis.setTimeout = (...args) => {
        const handle = realSetTimeout(...args);
        handle.unref?.();
        return handle;
    };
    globalThis.setInterval = (...args) => {
        const handle = realSetInterval(...args);
        handle.unref?.();
        return handle;
    };
    globalThis.localStorage = { getItem: () => null, setItem() {}, removeItem() {} };
    globalThis.CustomElements = { upgradeSubtree() {} };
    globalThis.Event = class {
        constructor(type) {
            this.type = type;
        }
    };
    globalThis.HTMLElement = FakeElement;
    globalThis.URLSearchParams = URLSearchParams;
    globalThis.CSS = { escape: (v) => v };

    // router.parse() defaults to the live location, so the shim needs one.
    //
    // `window` is a facade over the global object rather than a separate object, because in a browser
    // it *is* the global object and the modules may reach for either. A shim where `window` is a
    // private object fails in the wrong direction: code that calls `window.getComputedStyle` throws
    // here and would have worked in the browser, which teaches the reader to distrust passing tests.
    globalThis.window = new Proxy(globalThis, {
        get(target, property) {
            if (property in target) {
                return target[property];
            }

            return undefined;
        },
        has() {
            return true;
        }
    });
    globalThis.window.location = { hash: '#/home', pathname: '/web/', search: '' };
    globalThis.window.history = { state: null, replaceState() {}, pushState() {} };
    globalThis.window.innerWidth = 1280;
    globalThis.window.innerHeight = 800;
    globalThis.location = globalThis.window.location;
    globalThis.ApiClient = null;

    // Listeners are recorded rather than dropped, so a test can fire one and see what the handler did.
    const windowListeners = new Map();
    globalThis.__windowListeners = windowListeners;
    globalThis.addEventListener = (type, handler) => {
        if (!windowListeners.has(type)) {
            windowListeners.set(type, []);
        }

        windowListeners.get(type).push(handler);
    };
    globalThis.removeEventListener = (type, handler) => {
        const list = windowListeners.get(type) ?? [];
        windowListeners.set(type, list.filter((h) => h !== handler));
    };

    globalThis.__fakeElement = FakeElement;
}

installDomShim();

// The modules use relative specifiers, so they are copied to a scratch directory and imported from
// there. Rewriting specifiers in memory would test something other than what ships.
// Files are copied with their real names, extension and all, so the import specifiers under test are
// byte for byte the ones that ship. A package.json marks the directory as ES modules so Node resolves
// them the way a browser would.
rmSync(tmp, { recursive: true, force: true });
mkdirSync(join(tmp, 'js'), { recursive: true });
writeFileSync(join(tmp, 'package.json'), JSON.stringify({ type: 'module' }));
copyFileSync(join(webRoot, 'client.js'), join(tmp, 'client.js'));
copyFileSync(join(webRoot, 'early.js'), join(tmp, 'early.js'));
for (const name of readdirSync(join(webRoot, 'js'))) {
    copyFileSync(join(webRoot, 'js', name), join(tmp, 'js', name));
}

const failures = [];

/**
 * Asserts a condition, recording rather than throwing so every check runs.
 *
 * @param {string} what A description of the check.
 * @param {boolean} condition The condition.
 */
function check(what, condition) {
    if (!condition) {
        failures.push(what);
    }
}

// Every module must import cleanly, which executes its top level.
const modules = {};
for (const entry of ['utils', 'api', 'router', 'inject', 'chrome', 'card', 'rail', 'hero', 'modal', 'seerr', 'detail', 'search', 'app']) {
    const url = pathToFileURL(join(tmp, 'js', `${entry}.js`)).href;
    try {
        modules[entry] = await import(url);
    } catch (error) {
        failures.push(`${entry}.js failed to import: ${error.message}`);
    }
}

// client.js is the entry point and wires the whole graph together.
try {
    await import(pathToFileURL(join(tmp, 'client.js')).href);
} catch (error) {
    failures.push(`client.js failed to import: ${error.message}`);
}

// The exported surface the application actually calls.
const expected = {
    utils: ['el', 'empty', 'isVisible', 'isModernLayout', 'formatRuntime', 'metaLine', 'debounce', 'createLogger'],
    api: ['bootstrap', 'home', 'rail', 'title', 'season', 'person', 'search', 'browse', 'seerrStatus', 'seerrRequest', 'seerrStates', 'messageOf'],
    router: ['parse', 'build', 'navigate', 'replace', 'TAB_HASH', 'DEFAULT_TAB_INDEX'],
    inject: ['ensure', 'panel', 'currentTabIndex', 'hasTabButton', 'selectTab'],
    card: ['createCard'],
    chrome: ['watchChrome'],
    rail: ['createRail', 'createRailPlaceholder', 'hydrateRail'],
    hero: ['createHero'],
    modal: ['showModal', 'closeModal', 'showMessage'],
    seerr: ['requestTitle', 'checkSeerr', 'refreshBadges'],
    detail: ['createTitlePage', 'createPersonPage'],
    search: ['createSearchView', 'createBrowseView'],
    app: ['start', 'renderRoute', 'tabLabel', 'refreshVisibleBadges', 'showBanner']
};

for (const [name, symbols] of Object.entries(expected)) {
    const mod = modules[name];
    if (!mod) {
        continue;
    }

    for (const symbol of symbols) {
        check(`${name} does not export ${symbol}`, typeof mod[symbol] !== 'undefined');
    }
}

// A few behavioural checks that would otherwise only fail in a browser.
try {
    const router = modules.router;
    check('parse reports a bare #/home as active', router.parse('#/home').active === true);
    check('parse defaults the tab index to 0', router.parse('#/home').tabIndex === 0);
    check('parse reads the tab index', router.parse('#/home?tab=2').tabIndex === 2);
    check('parse reads a title view', router.parse('#/home?tab=2&view=title&id=603').id === 603);
    check('parse ignores other routes', router.parse('#/items').active === false);
    check('build emits the tab parameter', router.build({ view: 'title', id: 1, type: 'movie' }).includes('tab=2'));
} catch (error) {
    failures.push(`router checks threw: ${error.message}`);
}

try {
    const utils = modules.utils;
    check('formatRuntime formats hours and minutes', utils.formatRuntime(136) === '2h 16m');
    check('formatRuntime returns empty for zero', utils.formatRuntime(0) === '');
    check('yearOf extracts a year', utils.yearOf('2023-03-30') === '2023');
} catch (error) {
    failures.push(`utils checks threw: ${error.message}`);
}

try {
    // Every card helper must be callable: this is what caught the hoverTimer rename.
    const card = modules.card;
    const rail = modules.rail;
    const hero = modules.hero;
    const sample = { id: 1, type: 'movie', title: 'X', genres: [], inLibrary: false };
    card.createCard(sample, {}, {});
    rail.createRail({ id: 'r', title: 'R', items: [sample] }, {}, {});
    hero.createHero([sample], { heroSeconds: 5 }, {});

    // The rank is a flex sibling of the poster, not an absolutely positioned overlay. The
    // difference is invisible in a unit test and obvious on screen: a number drawn to the left of a
    // poster in a horizontal scroller is either clipped by the viewport or painted over by the
    // previous card.
    const plain = card.createCard(sample, {}, {});
    check('an unranked card has no rank', plain.querySelector('.rekoCardRank') === null);
    check('an unranked card holds only a body', plain.children.length === 1);

    const ranked = card.createCard(sample, {}, {}, 3);
    check('a ranked card is marked', ranked.classList.contains('is-ranked'));
    check('a ranked card shows its number', ranked.querySelector('.rekoCardRank')?.textContent === '3');
    check('the rank comes before the poster body', ranked.children[0]?.className === 'rekoCardRank');
    check('the rank is hidden from assistive technology', ranked.querySelector('.rekoCardRank')?.getAttribute('aria-hidden') === 'true');
    check('the card still exposes its title', ranked.querySelector('.rekoCardTitle')?.textContent === 'X');
} catch (error) {
    failures.push(`component checks threw: ${error.stack || error.message}`);
}

try {
    // The sticky header's offset. This is the whole of what stands between "the Reko title and the
    // search box scroll under Jellyfin's app bar and disappear" and them staying put, and it is
    // invisible in a unit test and obvious on a scrolled page.
    const chrome = modules.chrome;
    const FakeElement = globalThis.__fakeElement;
    const body = globalThis.document.body;

    body.replaceChildren();

    const appBar = new FakeElement('div');
    appBar.className = 'MuiAppBar-root';
    appBar.box = { width: 1286, height: 48 };
    body.appendChild(appBar);

    // The Modern layout keeps the legacy header mounted at zero height. It must not win.
    const collapsed = new FakeElement('div');
    collapsed.className = 'skinHeader';
    collapsed.box = { width: 1286, height: 0 };
    body.appendChild(collapsed);

    const root = new FakeElement('div');
    root.setAttribute('data-reko', 'app');
    body.appendChild(root);

    chrome.watchChrome();
    check('the Modern app bar height is published', root.style.getPropertyValue('--reko-chrome-h') === '48px');

    // The Legacy layout's header is taller and is the only one on screen.
    appBar.hidden = true;
    collapsed.box = { width: 1286, height: 96 };
    chrome.watchChrome();
    check('the Legacy header height is published', root.style.getPropertyValue('--reko-chrome-h') === '96px');

    // Jellyfin rebuilds the home route on every navigation, which detaches Reko's root. A fresh root
    // must be given the offset even though the height has not changed: comparing heights alone would
    // leave it with none, which is the bug the offset was added to fix.
    const replaced = new FakeElement('div');
    replaced.setAttribute('data-reko', 'app');
    collapsed.box = { width: 1286, height: 0 };
    body.replaceChildren(appBar, collapsed, replaced);
    appBar.hidden = false;

    chrome.watchChrome();
    check('a rebuilt root is given the offset too', replaced.style.getPropertyValue('--reko-chrome-h') === '48px');

    // Jellyfin also keeps the previous home page mounted behind the one on screen, so there are
    // routinely two roots and only one of them is visible. Publishing to the first and stopping puts
    // the stylesheet's fallback on the one being looked at — which on the Legacy layout is the
    // Modern layout's 48px, under a 96px header.
    const hidden = new FakeElement('div');
    hidden.setAttribute('data-reko', 'app');
    const visible = new FakeElement('div');
    visible.setAttribute('data-reko', 'app');
    hidden.hidden = true;
    body.replaceChildren(appBar, collapsed, hidden, visible);

    appBar.hidden = true;
    collapsed.box = { width: 1286, height: 96 };
    chrome.watchChrome();
    check('every root is published, not just the first', hidden.style.getPropertyValue('--reko-chrome-h') === '96px'
        && visible.style.getPropertyValue('--reko-chrome-h') === '96px');
} catch (error) {
    failures.push(`chrome checks threw: ${error.stack || error.message}`);
}

/**
 * Builds a home page in the shape Jellyfin 12.1's Modern layout renders it.
 *
 * @param {Object} [options] Options.
 * @param {boolean} [options.legacyTabStrip] Whether to render the legacy tab strip instead of the
 *   modern app bar, which is how the Legacy and TV layouts differ.
 * @returns {object} The page container the panels live in.
 */
function buildModernHome({ legacyTabStrip = false } = {}) {
    const FakeElement = globalThis.__fakeElement;
    const body = globalThis.document.body;
    body.replaceChildren();

    const app = new FakeElement('div');
    app.className = 'skinBody';
    body.appendChild(app);

    const bar = new FakeElement('div');
    bar.className = 'MuiAppBar-root MuiPaper-root';
    const stack = new FakeElement('div');
    stack.className = 'MuiStack-root';

    const home = new FakeElement('a');
    home.className = 'MuiButtonBase-root MuiButton-root';
    home.setAttribute('href', '#/');
    home.appendChild(globalThis.document.createTextNode('Reko Test'));

    const favorites = new FakeElement('a');
    favorites.className = 'MuiButtonBase-root MuiButton-root';
    favorites.setAttribute('href', '#/home?tab=1');
    favorites.appendChild(globalThis.document.createTextNode('Favorites'));

    stack.append(home, favorites);
    bar.appendChild(stack);

    const legacyHeader = new FakeElement('div');
    legacyHeader.className = 'skinHeader';
    legacyHeader.hidden = true; // Modern keeps the legacy header mounted but hidden.
    if (legacyTabStrip) {
        legacyHeader.hidden = false;
        const tabs = new FakeElement('div');
        tabs.className = 'tabs-viewmenubar';
        tabs.setAttribute('is', 'emby-tabs');
        const slider = new FakeElement('div');
        slider.className = 'emby-tabs-slider';
        slider.appendChild(new FakeElement('button'));
        tabs.appendChild(slider);
        legacyHeader.appendChild(tabs);
    }

    const page = new FakeElement('div');
    page.className = 'page homePage';

    for (let i = 0; i < 2; i++) {
        const panel = new FakeElement('div');
        panel.className = 'tabContent pageTabContent';
        panel.setAttribute('data-index', String(i));
        if (i === 0) {
            panel.classList.add('is-active');
        }

        page.appendChild(panel);
    }

    app.append(legacyHeader, bar, page);
    return page;
}

try {
    // The injector is the one module with no pure functions: every decision it makes is a query
    // against Jellyfin's markup, so it is the one module that can only be tested against markup.
    const inject = modules.inject;

    // --- Modern: tabs live in the app bar.
    const modernPage = buildModernHome();
    const modernPanel = inject.ensure({ label: 'Reko' });

    check('ensure creates the Reko panel', modernPanel !== null);
    check(
        'the panel carries both its own class and Jellyfin\'s tab classes',
        modernPanel?.className === 'rekoPanel tabContent pageTabContent'
    );
    check('the panel takes the first free index', modernPanel?.getAttribute('data-index') === '2');
    check('the panel follows the two Jellyfin panels', modernPage.querySelectorAll('.tabContent').length === 3);

    const appBarButton = globalThis.document.querySelector('[data-reko="header"]');
    check('ensure adds a button to the modern app bar', appBarButton !== null);
    check('the app bar button deep-links to the Reko tab', appBarButton?.getAttribute('href') === '#/home?tab=2');
    check('the app bar button is labelled', appBarButton?.textContent.trim() === 'Reko');
    check(
        'the app bar button sits directly after Favorites',
        appBarButton?.previousSibling?.textContent.trim() === 'Favorites'
    );

    // Re-running is the normal case, not an edge case: a MutationObserver calls it on every render.
    check('ensure is idempotent', inject.ensure() === modernPanel);
    check('ensure does not add a second app bar button', globalThis.document.querySelectorAll('[data-reko="header"]').length === 1);
    check('the tab index does not drift between passes', inject.currentTabIndex() === 2);
    check('the panel is not double-appended', modernPage.querySelectorAll('.tabContent').length === 3);

    check('selectTab reports success for its own panel', inject.selectTab(2) === true);
    check('selectTab activates the panel', modernPanel.classList.contains('is-active'));
    check('selectTab deactivates the panel it replaces', !modernPage.querySelector('[data-index="0"]').classList.contains('is-active'));
    check('selectTab ignores an index Reko does not own', inject.selectTab(7) === false);
    check('hasTabButton is false without a visible tab strip', inject.hasTabButton(2) === false);

    // --- Legacy: the tab strip is on screen and Reko has to join it.
    const legacyPage = buildModernHome({ legacyTabStrip: true });
    const legacyPanel = inject.ensure({ label: 'Reko' });

    check('ensure creates a panel in the legacy layout too', legacyPanel !== null);
    const stripButtons = globalThis.document.querySelectorAll('.emby-tabs-slider .emby-tab-button');
    check('ensure adds a button to the legacy tab strip', stripButtons.length === 1);
    check('the strip button is the Reko tab', stripButtons[0]?.getAttribute('data-index') === '2');
    check('the strip button is labelled', stripButtons[0]?.textContent.trim() === 'Reko');
    check('hasTabButton is true for a real strip button', inject.hasTabButton(2) === true);
    check('ensure adds no app bar button when a tab strip is on screen', globalThis.document.querySelectorAll('[data-reko="header"]').length === 0);

    // In the legacy layout the selection is delegated to Jellyfin's own tab strip, and the strip —
    // not the panel's class — decides whether it needs to be asked. Asking based on the panel's
    // class is what leaves the Home panel and the Reko panel on screen at the same time: a
    // re-render marks Reko's panel active while the strip still believes in Home.
    const tabsElement = globalThis.document.querySelector('[is="emby-tabs"]');
    const asked = [];
    tabsElement.selectedTabIndex = 0;
    tabsElement.selectedIndex = (value) => {
        asked.push(value);
        tabsElement.selectedTabIndex = value;
    };

    inject.selectTab(2);
    check('selectTab asks the tab strip to select Reko', asked[asked.length - 1] === 2);

    inject.selectTab(2);
    check('selectTab does not re-ask a strip that already agrees', asked.length === 1);

    tabsElement.selectedTabIndex = 0;
    inject.selectTab(2);
    check('selectTab asks again when the strip disagrees', asked.length === 2);

    // --- No home page: the only correct answer is to do nothing at all.
    globalThis.document.body.replaceChildren();
    check('ensure returns null with no home page', inject.ensure() === null);
} catch (error) {
    failures.push(`inject checks threw: ${error.stack || error.message}`);
}

try {
    // Every view is mounted *inside* Reko's root, because that is the element every custom property in
    // the stylesheet is declared on. Mounting beside it resolves none of --reko-text, --reko-accent,
    // --reko-border and the rest, so the whole palette quietly falls back to Jellyfin's inherited
    // colours — indistinguishable on a dark theme, and unreadable on a light one.
    const app = modules.app;
    const inject = modules.inject;
    const page = buildModernHome();
    const panel = inject.ensure({ label: 'Reko' });
    const root = panel.querySelector('[data-reko="app"]');

    check('the injector makes an application root', root !== null);

    // The client goes through ApiClient.ajax rather than fetch, because that is the one path
    // jellyfin-web guarantees to attach credentials to.
    globalThis.ApiClient = {
        getUrl: (name) => `/Reko/${name}`,
        ajax: async ({ url: target }) => ({
            config: { tabLabel: 'Reko', enableSearch: true },
            tmdbConfigured: true,
            rails: [],
            hero: [],
            target
        })
    };

    await app.start(panel);

    const header = panel.querySelector('.rekoHeader');
    check('a view is rendered', header !== null);
    check('the rendered view is inside the application root', header !== null && root.contains(header));
    check('the application root survives the first render', panel.contains(root));

    // The tab is switched away from and back again, which is what a re-render looks like: the view is
    // torn down and rebuilt, and the root has to still be there to build it into.
    await app.renderRoute(panel);
    check('the root survives a re-render', panel.contains(root) && root.querySelector('.rekoHeader') !== null);

    // Restored so that client.js's boot poller, which is watching for exactly this, keeps waiting
    // instead of racing the rest of the run.
    globalThis.ApiClient = null;
} catch (error) {
    failures.push(`mount checks threw: ${error.stack || error.message}`);
}

rmSync(tmp, { recursive: true, force: true });

if (failures.length) {
    console.error(`\n${failures.length} check(s) failed:\n`);
    for (const failure of failures) {
        console.error(`  x ${failure}`);
    }

    process.exit(1);
}

console.log('All Reko client checks passed.');
