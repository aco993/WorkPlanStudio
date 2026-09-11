// Small JS interop surface used by the app.
// 1) blazorCulture   – remembers the chosen UI language.
// 2) workplanDb       – persists the in-browser SQLite database to localStorage.
// 3) workplanSettings – small key/value store for app settings (e.g. the optional
//                       AI assistant configuration). Values stay in this browser.

window.blazorCulture = {
    get: () => window.localStorage['BlazorCulture'],
    set: (value) => window.localStorage['BlazorCulture'] = value
};

// The boot screen and the framework's error bar are painted before the .NET
// runtime exists, so IStringLocalizer cannot reach them; they were the last
// untranslated strings in the app. The remembered language is in localStorage
// before anything renders, so the swap happens here, from `data-de` attributes
// that keep both languages next to each other in index.html.
(function localiseStaticChrome() {
    const culture = window.localStorage['BlazorCulture'] || '';
    if (!culture.startsWith('de')) { return; }
    document.documentElement.lang = 'de';
    document.querySelectorAll('[data-de]').forEach(el => el.textContent = el.getAttribute('data-de'));
})();

window.documentLanguage = {
    set: (value) => document.documentElement.lang = value
};

// Dialog focus. `close` deliberately takes no element: it is called from the
// component's render loop *after* the dialog has been removed from the DOM (a
// dialog closed by Cancel or Save never goes through the ✕ path), and an
// ElementReference to a detached node is no longer resolvable. A stack of
// { dialog, previous } pairs keeps the trap removable and the return target known
// without needing the element back from .NET.
window.workplanModal = {
    stack: [],
    open: function (dialog) {
        this.stack.push({ dialog: dialog, previous: document.activeElement });
        const target = dialog.querySelector('input:not([disabled]), select:not([disabled]), textarea:not([disabled]), button:not([disabled]), a[href]');
        (target || dialog).focus();
        window.workplanFocusTrap.install(dialog);
    },
    close: function () {
        const entry = this.stack.pop();
        if (!entry) { return; }
        window.workplanFocusTrap.remove(entry.dialog);
        if (entry.previous && document.contains(entry.previous)) {
            entry.previous.focus();
        }
    }
};

window.workplanSettings = {
    keyFor: (name) => 'workplanstudio.settings.' + name,
    get: function (name) { return window.localStorage.getItem(this.keyFor(name)); },
    set: function (name, value) { window.localStorage.setItem(this.keyFor(name), value); }
};

window.workplanDb = {
    storageKey: 'workplanstudio.db',
    // Only read, never written: the pre-atomic layout kept the version here.
    legacyVersionKey: 'workplanstudio.db.version',

    // Returns { data, version } or null when nothing has been stored yet.
    //
    // The payload and its version used to be two separate setItem calls. If the
    // second one hit the quota the data key held a current payload while the
    // version key was gone, load() reported version 0, and the app parked the
    // user on "unsupported schema" for a database that was perfectly fine - with
    // reset, i.e. total data loss, as the only way out. One JSON value under one
    // key makes the write atomic; the legacy two-key layout is still read so an
    // existing browser keeps its data.
    load: function () {
        const raw = window.localStorage.getItem(this.storageKey);
        if (raw === null) {
            return null;
        }
        if (raw.length > 0 && raw[0] === '{') {
            try {
                const parsed = JSON.parse(raw);
                if (parsed && typeof parsed.data === 'string' && Number.isInteger(parsed.version)) {
                    return { data: parsed.data, version: parsed.version };
                }
            } catch {
                // Fall through: treat it as the legacy Base64 layout.
            }
        }
        const version = parseInt(window.localStorage.getItem(this.legacyVersionKey) || '0', 10);
        return { data: raw, version: version };
    },

    // Reports the outcome instead of throwing, so a full quota becomes a typed
    // result the UI can explain rather than an opaque JSException.
    save: function (base64, version) {
        const payload = JSON.stringify({ data: base64, version: version });
        try {
            window.localStorage.setItem(this.storageKey, payload);
        } catch (error) {
            const quota = error && (error.name === 'QuotaExceededError'
                || error.name === 'NS_ERROR_DOM_QUOTA_REACHED'
                || error.code === 22 || error.code === 1014);
            return { ok: false, reason: quota ? 'quota' : 'error', message: String(error && error.message || error) };
        }

        // Read back before declaring success: a storage layer that silently
        // truncates must not be reported as a durable save.
        const written = window.localStorage.getItem(this.storageKey);
        if (written !== payload) {
            return { ok: false, reason: 'error', message: 'the stored payload did not match what was written' };
        }

        window.localStorage.removeItem(this.legacyVersionKey);
        return { ok: true };
    },

    clear: function () {
        window.localStorage.removeItem(this.storageKey);
        window.localStorage.removeItem(this.legacyVersionKey);
    },

    export: function (base64, version) {
        const payload = JSON.stringify({ version: version, data: base64 }, null, 2);
        const blob = new Blob([payload], { type: 'application/json' });
        const url = URL.createObjectURL(blob);
        const link = document.createElement('a');
        link.href = url;
        link.download = 'workplanstudio-browser-database-v' + version + '.json';
        link.click();
        URL.revokeObjectURL(url);
    },

    // The other half of export, which the app shipped without: a file the user
    // downloaded was previously readable by nothing at all.
    pickImport: function () {
        return new Promise((resolve) => {
            const input = document.createElement('input');
            input.type = 'file';
            input.accept = 'application/json,.json';
            input.addEventListener('cancel', () => resolve(null));
            input.addEventListener('change', async () => {
                const file = input.files && input.files[0];
                if (!file) { resolve(null); return; }
                try {
                    const parsed = JSON.parse(await file.text());
                    resolve(parsed && typeof parsed.data === 'string' && Number.isInteger(parsed.version)
                        ? { data: parsed.data, version: parsed.version }
                        : { data: '', version: -1 });
                } catch {
                    resolve({ data: '', version: -1 });
                }
            });
            input.click();
        });
    }
};

// Closes a <details> menu from Blazor (after a choice, or on Escape) and returns
// focus to its summary so keyboard users are not dropped on the page body.
window.workplanMenu = {
    close: function (details) {
        if (!details) { return; }
        details.removeAttribute('open');
        const summary = details.querySelector('summary');
        if (summary) { summary.focus(); }
    }
};

// Theme: the user's choice is 'system' (default), 'light' or 'dark'. The
// effective theme is stamped on <html data-theme> before Blazor boots so the
// first paint is already right, and re-stamped when the OS preference changes
// while in system mode.
window.workplanTheme = {
    key: 'workplanstudio.settings.theme',
    media: window.matchMedia('(prefers-color-scheme: dark)'),
    get: function () {
        const stored = window.localStorage.getItem(this.key);
        return stored === 'light' || stored === 'dark' ? stored : 'system';
    },
    set: function (mode) {
        if (mode === 'system') { window.localStorage.removeItem(this.key); }
        else { window.localStorage.setItem(this.key, mode); }
        this.apply();
        return this.effective();
    },
    effective: function () {
        const mode = this.get();
        return mode === 'system' ? (this.media.matches ? 'dark' : 'light') : mode;
    },
    apply: function () {
        const effective = this.effective();
        document.documentElement.setAttribute('data-theme', effective);
        document.documentElement.setAttribute('data-theme-mode', this.get());
        const meta = document.querySelector('meta[name="theme-color"]');
        if (meta) { meta.setAttribute('content', effective === 'dark' ? '#0f1117' : '#4f46e5'); }
    }
};
window.workplanTheme.apply();
window.workplanTheme.media.addEventListener('change', () => window.workplanTheme.apply());

// Keeps Tab inside an open dialog: the last focusable wraps to the first and
// back. Installed by workplanModal.open, removed by workplanModal.close.
window.workplanFocusTrap = {
    handlers: new WeakMap(),
    selector: 'a[href], button:not([disabled]), input:not([disabled]), select:not([disabled]), textarea:not([disabled]), [tabindex]:not([tabindex="-1"])',
    install: function (dialog) {
        const handler = (event) => {
            if (event.key !== 'Tab') { return; }
            const items = Array.from(dialog.querySelectorAll(this.selector)).filter(el => el.offsetParent !== null);
            if (items.length === 0) { return; }
            const first = items[0], last = items[items.length - 1];
            if (event.shiftKey && document.activeElement === first) { event.preventDefault(); last.focus(); }
            else if (!event.shiftKey && document.activeElement === last) { event.preventDefault(); first.focus(); }
        };
        dialog.addEventListener('keydown', handler);
        this.handlers.set(dialog, handler);
    },
    remove: function (dialog) {
        const handler = this.handlers.get(dialog);
        if (handler) { dialog.removeEventListener('keydown', handler); this.handlers.delete(dialog); }
    }
};

// The skip link: Blazor intercepts same-document links, so the jump to <main>
// is explicit - focus it (tabindex="-1") and scroll it into view.
// The chat thread is a bounded scroll box; a new answer is scrolled into view
// so the newest turn is what the reader sees.
window.workplanChat = {
    scrollToEnd: function () {
        const thread = document.querySelector('.chat-thread');
        if (thread) { thread.scrollTop = thread.scrollHeight; }
    }
};

// The mobile navigation drawer. Focus follows it in; the layout sends focus back
// to the hamburger on close.
window.workplanNav = {
    focusFirst: function () {
        const link = document.querySelector('.app-shell.nav-open .sidebar .nav-link');
        if (link) { link.focus(); }
    }
};

// The Gantt chart's arrow-key navigation. The browser scrolls a scroll container
// with the arrow keys, which fights the focus move the component is making, so the
// default is cancelled here rather than with Blazor's @onkeydown:preventDefault —
// that attribute is fixed per render, not per key, so it would also swallow Tab and
// trap the reader inside the chart. Enter and Space are deliberately left alone:
// they are the buttons' own activation.
window.workplanGantt = {
    keys: ['ArrowLeft', 'ArrowRight', 'ArrowUp', 'ArrowDown', 'Home', 'End'],
    install: function (chart) {
        if (!chart || chart.dataset.keysBound === 'true') { return; }
        chart.dataset.keysBound = 'true';
        chart.addEventListener('keydown', (event) => {
            if (this.keys.includes(event.key)) { event.preventDefault(); }
        });
    }
};

// Focus by id, for the Gantt chart's roving tabindex: the focused bar changes with
// the arrow keys, and the bars are rendered inside a @foreach where an
// ElementReference per bar would have to be kept in step with the list by hand.
window.workplanFocus = {
    byId: function (id) {
        const element = document.getElementById(id);
        if (element) { element.focus(); }
    }
};

window.workplanSkip = {
    toContent: function () {
        const main = document.getElementById('main');
        if (!main) { return; }
        main.focus({ preventScroll: true });
        main.scrollIntoView({ block: 'start' });
    }
};

// One turn of the browser's task queue, for the scheduling run.
//
// The run holds the tab's only thread and gives it back between multi-start
// restarts. What it gives it back *with* decides how much that is worth: a nested
// setTimeout is clamped to about 4 ms, and Chrome throttles a hidden tab's timers
// to roughly one wake-up a second - measured on this page as a run that crawled
// from 25 % to 27 % in ten seconds after the tab went to the background. A message
// posted to a port is a task and not a timer, so neither clamp applies, and the
// page still gets to paint between turns (which a microtask would not allow).
window.workplanYield = {
    next: function () {
        return new Promise((resolve) => {
            const channel = new MessageChannel();
            channel.port1.onmessage = () => { channel.port1.close(); resolve(); };
            channel.port2.postMessage(0);
        });
    }
};
