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

// #117 — the pseudo-locale.
//
// Six times in this sweep the scanner reported zero while the running
// workbench still showed English, and six times the gap was a call shape
// nobody had told the scanner about. A scanner can only find what it was
// taught to look for; the product knows the answer already.
//
// So: set the locale to `qps` and every string that comes *through the
// catalogue* is wrapped in ⟦…⟧ with its vowels accented. Anything on screen
// without the brackets never went through t(). That makes the boundary the
// product rather than a regular expression, and it costs a translator nothing
// because they never see it — it is not offered in the language selector.
//
//     localStorage.setItem('bowire_locale', 'qps'); location.reload();
//
// The accents also stretch the text, so a label that only just fits in
// English shows its truncation here rather than in the first translation
// that ships.
var PSEUDO_LOCALE = 'qps';

var PSEUDO_MAP = {
    a: 'á', e: 'ë', i: 'ï', o: 'ö', u: 'û', y: 'ÿ',
    A: 'Á', E: 'Ë', I: 'Ï', O: 'Ö', U: 'Û', Y: 'Ÿ'
};

/**
 * Mark a string as having come from the catalogue.
 *
 * Placeholders survive untouched — both `{name}`, which interpolate() fills,
 * and `{{name}}`, which is Bowire's own variable syntax and has to reach the
 * screen intact. Accenting either would break the very thing the marking is
 * meant to verify.
 */
function pseudoise(text) {
    var out = '';
    var rest = String(text);
    var re = /\{\{?[a-zA-Z0-9_]+\}?\}/g;
    var at = 0;
    var m;
    while ((m = re.exec(rest)) !== null) {
        out += accent(rest.slice(at, m.index)) + m[0];
        at = m.index + m[0].length;
    }
    out += accent(rest.slice(at));
    return '⟦' + out + '⟧';
}

function accent(chunk) {
    var out = '';
    for (var i = 0; i < chunk.length; i++) {
        var ch = chunk.charAt(i);
        out += PSEUDO_MAP[ch] || ch;
    }
    return out;
}

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
    if (preferred && String(preferred).toLowerCase() === PSEUDO_LOCALE) {
        return PSEUDO_LOCALE;
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
    text = formatMessage(String(text), params);
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

// ---- plural and select (#688) ----
//
// The two pieces of ICU MessageFormat a sentence actually needs:
//
//   {count, plural, one {# run} other {# runs}}
//   {kind, select, file {the file} folder {the folder} other {it}}
//
// A plural picks its branch by the active locale's own categories through
// Intl.PluralRules, so a language with three or six forms works without a
// code change; `=0`-style exact branches win over the category, and `other`
// is the fallback. `#` inside a plural branch is the number. A string with
// no such block takes the plain `{name}` path unchanged, and `{{token}}`
// stays literal.

var ICU_BLOCK = /\{\s*[A-Za-z0-9_]+\s*,\s*(?:plural|select)\s*,/;

function pluralLocale() {
    return activeLocale === PSEUDO_LOCALE ? 'en' : activeLocale;
}

function formatMessage(text, params) {
    if (!ICU_BLOCK.test(text)) return text;
    return formatBlocks(text, params, null);
}

// Index of the brace that closes the one at `open`, counting nesting.
function closingBrace(text, open) {
    var depth = 0;
    for (var i = open; i < text.length; i++) {
        var ch = text.charAt(i);
        if (ch === '{') depth++;
        else if (ch === '}') {
            depth--;
            if (depth === 0) return i;
        }
    }
    return -1;
}

function formatBlocks(text, params, number) {
    var out = '';
    var i = 0;
    while (i < text.length) {
        var ch = text.charAt(i);
        if (ch === '{' && text.charAt(i + 1) === '{') {
            // A Bowire variable: copied as written, braces and all.
            var end = text.indexOf('}}', i + 2);
            if (end < 0) { out += text.slice(i); break; }
            out += text.slice(i, end + 2);
            i = end + 2;
            continue;
        }
        if (ch === '{') {
            var close = closingBrace(text, i);
            if (close < 0) { out += text.slice(i); break; }
            var inner = text.slice(i + 1, close);
            var m = /^\s*([A-Za-z0-9_]+)\s*,\s*(plural|select)\s*,([\s\S]*)$/.exec(inner);
            out += m ? formatBlock(m[1], m[2], m[3], params, number, text.slice(i, close + 1)) : text.slice(i, close + 1);
            i = close + 1;
            continue;
        }
        if (ch === '#' && number !== null) {
            out += Number(number).toLocaleString(pluralLocale());
            i++;
            continue;
        }
        out += ch;
        i++;
    }
    return out;
}

// The branches of one block as [selector, body] pairs, in order.
function branchesOf(rest) {
    var branches = [];
    var i = 0;
    while (i < rest.length) {
        while (i < rest.length && /\s/.test(rest.charAt(i))) i++;
        var open = rest.indexOf('{', i);
        if (open < 0) break;
        var selector = rest.slice(i, open).trim();
        var close = closingBrace(rest, open);
        if (close < 0 || !selector) break;
        branches.push([selector, rest.slice(open + 1, close)]);
        i = close + 1;
    }
    return branches;
}

function formatBlock(name, type, rest, params, number, whole) {
    if (!Object.prototype.hasOwnProperty.call(params, name)) return whole;
    var value = params[name];
    var branches = branchesOf(rest);
    var pick = null;
    if (type === 'plural') {
        var n = Number(value);
        for (var b = 0; b < branches.length && pick === null; b++) {
            if (branches[b][0] === '=' + n) pick = branches[b][1];
        }
        if (pick === null) {
            var category = 'other';
            try { category = new Intl.PluralRules(pluralLocale()).select(n); } catch { /* unknown locale: other */ }
            for (var c = 0; c < branches.length && pick === null; c++) {
                if (branches[c][0] === category) pick = branches[c][1];
            }
        }
        number = n;
    } else {
        for (var s = 0; s < branches.length && pick === null; s++) {
            if (branches[s][0] === String(value)) pick = branches[s][1];
        }
    }
    if (pick === null) {
        for (var o = 0; o < branches.length && pick === null; o++) {
            if (branches[o][0] === 'other') pick = branches[o][1];
        }
    }
    return pick === null ? whole : formatBlocks(pick, params, number);
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
    if (activeLocale === PSEUDO_LOCALE) {
        var source = localeCatalogues.en;
        var raw = source ? source[key] : undefined;
        // A key with no English entry stays bare, exactly as it would in any
        // other locale: seeing the key itself is the signal that it is missing.
        if (raw === undefined) return key;
        // Plural and select first: accenting their keywords would break them.
        return interpolate(pseudoise(params ? formatMessage(String(raw), params) : raw), params);
    }
    var fromLocale = localeCatalogues[activeLocale];
    var text = fromLocale ? fromLocale[key] : undefined;
    if (text === undefined || text === '') {
        var english = localeCatalogues.en;
        text = english ? english[key] : undefined;
    }
    if (text === undefined) return key;
    return interpolate(text, params);
}

/**
 * A translated sentence with one slot filled by a DOM node instead of text.
 *
 * The alternative — splitting the sentence into a prefix key and a suffix key
 * around the element — freezes English word order into the catalogue. A
 * translator who needs the name at the end of the clause cannot get it there.
 * Here they place `{slot}` wherever their grammar wants it, and the node is
 * spliced in at that point.
 *
 * Returns an array of strings and nodes, ready to spread into el(...). A key
 * whose text has lost the slot still renders, just without the node — a
 * missing name is better than a blank sentence.
 */
function tNodes(key, slot, nodes, params) {
    var text = t(key, params);
    var marker = '{' + slot + '}';
    var at = text.indexOf(marker);
    if (at < 0) return [text];
    var middle = Array.isArray(nodes) ? nodes : [nodes];
    return [text.slice(0, at)].concat(middle, [text.slice(at + marker.length)]);
}
