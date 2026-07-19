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
    traps: new WeakMap(),
    open: function (dialog) {
        this.previousFocus.set(dialog, document.activeElement);
        const target = dialog.querySelector('input:not([disabled]), select:not([disabled]), textarea:not([disabled]), button:not([disabled]), a[href]');
        (target || dialog).focus();

        const onKeyDown = (event) => {
            if (event.key !== 'Tab') return;
            const focusable = [...dialog.querySelectorAll('input:not([disabled]), select:not([disabled]), textarea:not([disabled]), button:not([disabled]), a[href], [tabindex]:not([tabindex="-1"])')]
                .filter(element => element.offsetParent !== null);
            if (focusable.length === 0) {
                event.preventDefault();
                dialog.focus();
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
        };
        dialog.addEventListener('keydown', onKeyDown);
        this.traps.set(dialog, onKeyDown);
    },
    close: function (dialog) {
        const trap = this.traps.get(dialog);
        if (trap) dialog.removeEventListener('keydown', trap);
        this.traps.delete(dialog);
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
