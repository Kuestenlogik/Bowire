// #117 — the translation layer.
//
// Every user-visible string comes from a catalogue keyed by a dotted id.
// `wwwroot/locales/en.json` is the source of truth; every other locale is a
// file beside it with the same key set. Translators edit JSON and nothing
// else — no .NET, no build.
//
// The catalogues are **embedded into the bundle at build time** (the
// EmbedLocales task in the csproj writes wwwroot/js/_locales.js from the JSON
// files) rather than fetched at boot. init() is synchronous and renders
// immediately, so a fetched catalogue would arrive after the first paint and
// every string would flash from its key to its text. Embedding costs a few KB
// and removes the race entirely.
//
// Placeholders are `{name}`, not `{{name}}`. The double-brace form is taken:
// it is Bowire's own variable syntax, and UI strings legitimately *contain*
// it — the header-library value placeholder reads "{{token}} resolves against
// the active environment" and must survive translation unexpanded.

// locale id -> flat { key: string }. Populated by the generated
// _locales.js fragment, which is concatenated after this one.
var localeCatalogues = {};

/**
 * Called once per locale by the generated fragment.
 */
function registerLocaleCatalogue(locale, entries) {
    if (!locale || !entries || typeof entries !== 'object') return;
    localeCatalogues[String(locale).toLowerCase()] = entries;
}

var LOCALE_KEY = 'bowire_locale';

/**
 * Which locale should the workbench speak?
 *
 * The operator's explicit choice wins; otherwise the browser's. A browser
 * asking for `de-AT` gets `de` when there is no `de-AT` catalogue, because a
 * regional variant is closer to its base language than to English.
 */
function resolveLocale(preferred, available) {
    var have = available || localeCatalogues;
    function pick(tag) {
        if (!tag) return null;
        var lower = String(tag).toLowerCase();
        if (have[lower]) return lower;
        var base = lower.split('-')[0];
        return have[base] ? base : null;
    }
    var chosen = pick(preferred);
    if (chosen) return chosen;
    var fromBrowser = (typeof navigator !== 'undefined' && navigator.language) || '';
    return pick(fromBrowser) || 'en';
}

var activeLocale = 'en';

/** Read the stored preference, or fall back to the browser's language. */
function loadLocale() {
    var stored = null;
    try { stored = localStorage.getItem(LOCALE_KEY); } catch { /* private mode */ }
    activeLocale = resolveLocale(stored);
    return activeLocale;
}

/**
 * Switch language. Pass null / 'auto' to go back to following the browser.
 */
function setLocale(locale) {
    try {
        if (!locale || locale === 'auto') localStorage.removeItem(LOCALE_KEY);
        else localStorage.setItem(LOCALE_KEY, locale);
    } catch { /* private mode — the choice lasts for this session */ }
    activeLocale = resolveLocale(locale === 'auto' ? null : locale);
    return activeLocale;
}

/** The stored preference as the settings selector needs to show it. */
function localePreference() {
    try { return localStorage.getItem(LOCALE_KEY) || 'auto'; }
    catch { return 'auto'; }
}

/** Every locale the build embedded, English first, then alphabetical. */
function availableLocales() {
    return Object.keys(localeCatalogues).sort(function (a, b) {
        if (a === 'en') return -1;
        if (b === 'en') return 1;
        return a < b ? -1 : (a > b ? 1 : 0);
    });
}

/**
 * Substitute `{name}` placeholders.
 *
 * A placeholder with no matching parameter is left standing rather than
 * replaced with "undefined": a visible `{count}` in the UI is a bug report,
 * where "undefined" is a mystery.
 */
function interpolate(text, params) {
    if (!params) return text;
    // The lookaround skips a doubled brace: `{{token}}` is Bowire's own
    // variable syntax and appears literally inside UI strings, so it has to
    // come out the other side untouched even when `params` happens to carry a
    // key of the same name.
    return String(text).replace(/(?<!\{)\{([a-zA-Z0-9_]+)\}(?!\})/g, function (whole, name) {
        return Object.prototype.hasOwnProperty.call(params, name)
            ? String(params[name])
            : whole;
    });
}

/**
 * The translated string for `key`.
 *
 * Resolution order: the active locale, then English, then the key itself.
 * Returning the key rather than an empty string means a missing translation
 * shows up as `landing.noServices.title` in the UI — ugly on purpose, because
 * silence would ship unnoticed.
 */
function t(key, params) {
    var fromLocale = localeCatalogues[activeLocale];
    var text = fromLocale ? fromLocale[key] : undefined;
    if (text === undefined || text === '') {
        var english = localeCatalogues.en;
        text = english ? english[key] : undefined;
    }
    if (text === undefined) return key;
    return interpolate(text, params);
}
