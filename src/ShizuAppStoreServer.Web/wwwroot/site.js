// Opens and closes the detail page dialogs through delegated clicks.
document.addEventListener('click', (event) => {
    const trigger = event.target.closest('[data-dialog]');
    if (trigger) {
        const dialog = document.getElementById(trigger.dataset.dialog);
        if (dialog) {
            dialog.open = true;
        }
        return;
    }

    const close = event.target.closest('[data-dialog-close]');
    if (close) {
        const dialog = close.closest('mdui-dialog');
        if (dialog) {
            dialog.open = false;
        }
    }
});

// mdui only closes dropdowns on mdui-menu-item clicks, so close the
// category chip cloud explicitly.
document.addEventListener('click', (event) => {
    const chip = event.target.closest('.category-cloud a');
    if (chip) {
        const dropdown = chip.closest('mdui-dropdown');
        if (dropdown) {
            dropdown.open = false;
        }
    }
});

// Shows a dismissable hint when the open-in-app link appeared to do nothing.
const nudge = document.getElementById('app-nudge');
const openInApp = document.querySelector('.detail-actions mdui-button[href^="shizustore://"]');

if (nudge && openInApp) {
    let nudgeTimer = null;
    const cancelNudge = () => {
        if (nudgeTimer !== null) {
            clearTimeout(nudgeTimer);
            nudgeTimer = null;
        }
    };

    openInApp.addEventListener('click', () => {
        cancelNudge();
        nudgeTimer = setTimeout(() => {
            nudgeTimer = null;
            if (document.visibilityState === 'visible') {
                nudge.hidden = false;
            }
        }, 750);
    });

    document.addEventListener('visibilitychange', () => {
        if (document.visibilityState === 'hidden') {
            cancelNudge();
        }
    });

    nudge.querySelector('[data-nudge-close]')?.addEventListener('click', () => {
        nudge.hidden = true;
    });
}

// Fullscreen screenshot viewer with arrows, keyboard and swipe support.
const viewer = document.getElementById('screenshot-viewer');
const screenshotStrip = document.querySelector('.screenshots');

if (viewer && screenshotStrip) {
    const image = viewer.querySelector('.viewer-image');
    const links = Array.from(screenshotStrip.querySelectorAll('a'));
    let index = 0;

    const show = (next) => {
        index = (next + links.length) % links.length;
        image.src = links[index].href;
    };

    const open = (next) => {
        show(next);
        viewer.hidden = false;
        document.body.classList.add('viewer-open');
    };

    const close = () => {
        viewer.hidden = true;
        document.body.classList.remove('viewer-open');
        image.removeAttribute('src');
    };

    screenshotStrip.addEventListener('click', (event) => {
        const link = event.target.closest('a');
        if (!link || event.metaKey || event.ctrlKey || event.shiftKey || event.altKey) {
            return;
        }

        event.preventDefault();
        open(links.indexOf(link));
    });

    viewer.querySelector('.viewer-prev').addEventListener('click', () => show(index - 1));
    viewer.querySelector('.viewer-next').addEventListener('click', () => show(index + 1));
    viewer.querySelector('.viewer-close').addEventListener('click', close);
    viewer.addEventListener('click', (event) => {
        if (event.target === viewer) {
            close();
        }
    });

    document.addEventListener('keydown', (event) => {
        if (viewer.hidden) {
            return;
        }

        if (event.key === 'Escape') {
            close();
        } else if (event.key === 'ArrowLeft') {
            show(index - 1);
        } else if (event.key === 'ArrowRight') {
            show(index + 1);
        }
    });

    let touchStartX = null;
    viewer.addEventListener('touchstart', (event) => {
        touchStartX = event.changedTouches[0].clientX;
    }, { passive: true });
    viewer.addEventListener('touchend', (event) => {
        if (touchStartX === null) {
            return;
        }

        const delta = event.changedTouches[0].clientX - touchStartX;
        touchStartX = null;
        if (Math.abs(delta) >= 40) {
            show(index + (delta < 0 ? 1 : -1));
        }
    }, { passive: true });
}
