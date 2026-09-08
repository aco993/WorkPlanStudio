// Small JS interop surface used by the app.
// 1) blazorCulture   – remembers the chosen UI language.
// 2) workplanDb       – persists the in-browser SQLite database to localStorage.
// 3) workplanSettings – small key/value store for app settings (e.g. the optional
//                       AI assistant configuration). Values stay in this browser.

window.blazorCulture = {
    get: () => window.localStorage['BlazorCulture'],
    set: (value) => window.localStorage['BlazorCulture'] = value
};

window.documentLanguage = {
    set: (value) => document.documentElement.lang = value
};

window.workplanModal = {
    previousFocus: new WeakMap(),
    open: function (dialog) {
        this.previousFocus.set(dialog, document.activeElement);
        const target = dialog.querySelector('input:not([disabled]), select:not([disabled]), textarea:not([disabled]), button:not([disabled]), a[href]');
        (target || dialog).focus();
        window.workplanFocusTrap.install(dialog);
    },
    close: function (dialog) {
        window.workplanFocusTrap.remove(dialog);
        const previous = this.previousFocus.get(dialog);
        if (previous && document.contains(previous)) {
            previous.focus();
        }
        this.previousFocus.delete(dialog);
    }
};

window.workplanSettings = {
    keyFor: (name) => 'workplanstudio.settings.' + name,
    get: function (name) { return window.localStorage.getItem(this.keyFor(name)); },
    set: function (name, value) { window.localStorage.setItem(this.keyFor(name), value); }
};

window.workplanDb = {
    storageKey: 'workplanstudio.db',
    versionKey: 'workplanstudio.db.version',

    // Returns { data, version } or null when nothing has been stored yet.
    load: function () {
        const data = window.localStorage.getItem(this.storageKey);
        if (data === null) {
            return null;
        }
        const version = parseInt(window.localStorage.getItem(this.versionKey) || '0', 10);
        return { data: data, version: version };
    },

    save: function (base64, version) {
        window.localStorage.setItem(this.storageKey, base64);
        window.localStorage.setItem(this.versionKey, String(version));
    },

    clear: function () {
        window.localStorage.removeItem(this.storageKey);
        window.localStorage.removeItem(this.versionKey);
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
window.workplanSkip = {
    toContent: function () {
        const main = document.getElementById('main');
        if (!main) { return; }
        main.focus({ preventScroll: true });
        main.scrollIntoView({ block: 'start' });
    }
};
