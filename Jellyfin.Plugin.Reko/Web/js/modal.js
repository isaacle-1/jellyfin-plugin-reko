/**
 * A small modal, used for the season picker and for request results.
 *
 * Deliberately minimal: a backdrop, a focus trap, Escape to close, and a close button. Reko's modals
 * are short-lived and must not fight Jellyfin's own dialogs, so it does not use a portal into
 * Jellyfin's DOM, and it restores focus to whatever was focused before it opened.
 */

import { el, empty } from './utils.js';

let openModal = null;

/**
 * Opens a modal.
 *
 * @param {Object} options Options.
 * @param {string} options.title The title.
 * @param {Node|Node[]} options.body The body content.
 * @param {Array<{label: string, primary?: boolean, onClick?: Function}>} [options.actions] Buttons.
 * @param {Function} [options.onClose] Called when the modal closes.
 * @returns {Object} `{ close }`.
 */
export function showModal(options) {
    closeModal();

    const previouslyFocused = document.activeElement;

    const body = el('div.rekoModalBody');
    if (Array.isArray(options.body)) {
        body.append(...options.body);
    } else {
        body.appendChild(options.body);
    }

    const footer = el('div.rekoModalFooter');
    const buttons = [];

    for (const action of options.actions ?? []) {
        const button = el(`button.rekoButton.${action.primary ? 'is-primary' : 'is-secondary'}`, {
            type: 'button',
            text: action.label,
            on: {
                click: () => action.onClick?.({ close })
            }
        });

        buttons.push(button);
        footer.appendChild(button);
    }

    const dialog = el('div.rekoModal', {
        attrs: {
            role: 'dialog',
            'aria-modal': 'true',
            'aria-label': options.title
        }
    }, [
        el('div.rekoModalHeader', null, [
            el('h2.rekoModalTitle', { text: options.title }),
            el('button.rekoModalClose', {
                type: 'button',
                text: '×',
                attrs: { 'aria-label': 'Close' },
                on: { click: () => close() }
            })
        ]),
        body,
        footer
    ]);

    const backdrop = el('div.rekoModalBackdrop', {
        on: {
            click: (event) => {
                if (event.target === backdrop) {
                    close();
                }
            }
        }
    }, dialog);

    function onKeyDown(event) {
        if (event.key === 'Escape') {
            event.stopPropagation();
            close();
            return;
        }

        if (event.key !== 'Tab') {
            return;
        }

        // A focus trap, so tabbing cannot wander into the Jellyfin page behind the modal.
        const focusable = dialog.querySelectorAll(
            'button, [href], input, select, textarea, [tabindex]:not([tabindex="-1"])'
        );

        if (focusable.length === 0) {
            return;
        }

        const first = focusable[0];
        const last = focusable[focusable.length - 1];

        if (event.shiftKey && document.activeElement === first) {
            event.preventDefault();
            last.focus();
        } else if (!event.shiftKey && document.activeElement === last) {
            event.preventDefault();
            first.focus();
        }
    }

    function close() {
        if (openModal !== state) {
            return;
        }

        document.removeEventListener('keydown', onKeyDown, true);
        backdrop.remove();
        openModal = null;
        options.onClose?.();

        if (previouslyFocused instanceof HTMLElement && document.contains(previouslyFocused)) {
            previouslyFocused.focus();
        }
    }

    const state = { close, dialog, buttons, body };
    openModal = state;

    document.addEventListener('keydown', onKeyDown, true);

    // Mounted inside Reko's own root rather than on `document.body`, for the same reason the hover
    // preview is: the theme's custom properties are declared on `.rekoApp`, and a modal on the body
    // falls back to hard-coded colours and stops following a light theme.
    (document.getElementById('rekoApp') ?? document.body).appendChild(backdrop);

    // Focus the first meaningful control rather than the close button.
    const preferred = dialog.querySelector('.rekoModalBody button, .rekoModalBody input, .rekoButton.is-primary');
    (preferred ?? dialog).focus?.();

    return state;
}

/**
 * Closes the open modal, if any.
 */
export function closeModal() {
    openModal?.close();
}

/**
 * Shows a message modal.
 *
 * @param {string} title The title.
 * @param {string} message The message.
 * @param {string} [tone] One of `info`, `success` or `error`.
 * @returns {Object} The modal handle.
 */
export function showMessage(title, message, tone = 'info') {
    return showModal({
        title,
        body: el('div.rekoModalMessage', { class: `is-${tone}`, text: message }),
        actions: [{ label: 'Close', primary: true }]
    });
}

/**
 * Replaces a modal's body in place, used to show progress during a request.
 *
 * @param {HTMLElement} node The body node to replace the contents of.
 * @param {Node} content The new content.
 */
export function setModalBody(node, content) {
    empty(node);
    node.appendChild(content);
}
