// #117 — the translation catalogues and the layer that reads them.
//
// Two jobs. The first half checks the JSON files against each other, which is
// the "CI enforces parity" the issue asks for: a translation that quietly
// loses a key falls back to English and nobody notices until a user reports a
// half-translated screen. The second half exercises i18n.js itself.

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync, readdirSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, resolve } from 'node:path';
import { compileFragment } from './_load-fragment.mjs';

const __dirname = dirname(fileURLToPath(import.meta.url));
const LOCALES = resolve(__dirname, '../../../src/Kuestenlogik.Bowire/wwwroot/locales');

// The catalogues are JSON with comments: `bowire docs translations` writes
// the English text above each blank so a translator can work in one file. A
// naive comment strip would eat the `//` inside a value like "http://host",
// so this walks the text and only drops what is outside a string.
function stripComments(text) {
    let out = '';
    let inString = false;
    let escaped = false;
    for (let i = 0; i < text.length; i++) {
        const ch = text[i];
        if (inString) {
            out += ch;
            if (escaped) escaped = false;
            else if (ch === '\\') escaped = true;
            else if (ch === '"') inString = false;
            continue;
        }
        if (ch === '"') { inString = true; out += ch; continue; }
        if (ch === '/' && text[i + 1] === '/') {
            while (i < text.length && text[i] !== '\n') i++;
            out += '\n';
            continue;
        }
        out += ch;
    }
    // Trailing commas are tolerated for the same reason.
    return out.replace(/,(\s*[}\]])/g, '$1');
}

const load = (file) => JSON.parse(stripComments(readFileSync(resolve(LOCALES, file), 'utf8')));

// `_comment` is the file's own note to translators, not a UI string.
const keysOf = (obj) => Object.keys(obj).filter((k) => !k.startsWith('_')).sort();
const placeholders = (text) =>
    [...String(text).matchAll(/(?<!\{)\{([a-zA-Z0-9_]+)\}(?!\})/g)].map((m) => m[1]).sort();

const files = readdirSync(LOCALES).filter((f) => f.endsWith('.json'));
const english = load('en.json');
const translations = files.filter((f) => f !== 'en.json');

// ---- the catalogues ----

test('the comment strip does not eat a // inside a value', () => {
    // The trap: `bowire docs translations` writes `// en: ...` lines, so the
    // reader has to skip comments -- and a naive strip would also cut
    // "http://host" in half, silently corrupting a translated string.
    const source = [
        '{',
        '  // en: Open the docs',
        '  "a.url": "Visit http://example.com/x for more",',
        '  "a.last": "fine",',
        '}',
    ].join(String.fromCharCode(10));

    const parsed = JSON.parse(stripComments(source));
    assert.equal(parsed['a.url'], 'Visit http://example.com/x for more');
    assert.equal(parsed['a.last'], 'fine', 'a trailing comma is tolerated');
    assert.equal(Object.keys(parsed).length, 2, 'the comment line is gone');
});

test('en.json is the source of truth and is not empty', () => {
    assert.ok(files.includes('en.json'), 'en.json must exist');
    assert.ok(keysOf(english).length > 0, 'en.json carries no strings');
});

test('at least one translation ships', () => {
    // The issue's point: shipping the machinery with nothing translated
    // proves nothing about whether the machinery works.
    assert.ok(translations.length > 0, 'no locale besides en.json');
});

for (const file of translations) {
    const locale = file.replace(/\.json$/, '');
    const catalogue = load(file);

    test(`${locale}: same key set as English`, () => {
        const want = keysOf(english);
        const have = keysOf(catalogue);
        const missing = want.filter((k) => !have.includes(k));
        const extra = have.filter((k) => !want.includes(k));
        assert.deepEqual(missing, [], `${file} is missing: ${missing.join(', ')}`);
        assert.deepEqual(extra, [],
            `${file} has keys English does not: ${extra.join(', ')} — add them to en.json first`);
    });

    test(`${locale}: no empty strings`, () => {
        // An empty value falls back to English at runtime, so it looks like a
        // translation exists when none does.
        const blank = keysOf(catalogue).filter((k) => !String(catalogue[k]).trim());
        assert.deepEqual(blank, [], `${file} has empty values: ${blank.join(', ')}`);
    });

    test(`${locale}: the same placeholders as English`, () => {
        // A translation that drops {url} renders a sentence with a hole in
        // it; one that invents {name} renders a literal brace. Neither shows
        // up in a key-set check.
        const wrong = [];
        for (const key of keysOf(english)) {
            const want = placeholders(english[key]);
            const have = placeholders(catalogue[key]);
            if (want.join(',') !== have.join(',')) {
                wrong.push(`${key}: expected {${want.join('} {')}}, got {${have.join('} {')}}`);
            }
        }
        assert.deepEqual(wrong, [], `${file} placeholder mismatch:\n  ${wrong.join('\n  ')}`);
    });
}

test('keys are dotted and area-first', () => {
    // The convention is what lets a translator work through one surface at a
    // time, and what keeps the flat file navigable at a thousand entries.
    const bad = keysOf(english).filter((k) => !/^[a-z][a-zA-Z0-9]*(\.[a-zA-Z0-9]+)+$/.test(k));
    assert.deepEqual(bad, [], `keys not in area.thing.part form: ${bad.join(', ')}`);
});

// ---- i18n.js ----

const i18n = compileFragment(
    '../../../src/Kuestenlogik.Bowire/wwwroot/js/i18n.js',
    ['store', 'browserLanguage'],
    `
    var navigator = { language: browserLanguage };
    var localStorage = {
        getItem: function (k) { return store && k in store ? store[k] : null; },
        setItem: function (k, v) { if (store) store[k] = v; },
        removeItem: function (k) { if (store) delete store[k]; },
    };
    return {
        registerLocaleCatalogue, resolveLocale, loadLocale, setLocale,
        localePreference, availableLocales, interpolate, t, tNodes,
        active: () => activeLocale,
    };
    `
);

function withCatalogues(opts = {}) {
    const api = i18n({ store: opts.store || {}, browserLanguage: opts.browserLanguage || 'en-GB' });
    api.registerLocaleCatalogue('en', opts.en || { greeting: 'Hello', only: 'English only' });
    api.registerLocaleCatalogue('de', opts.de || { greeting: 'Hallo' });
    return api;
}

test('t falls back through the locale, then English, then the key itself', () => {
    const api = withCatalogues({ store: { bowire_locale: 'de' } });
    api.loadLocale();
    assert.equal(api.t('greeting'), 'Hallo');
    // Present in English only — the German UI shows the English text rather
    // than a gap.
    assert.equal(api.t('only'), 'English only');
    // Nowhere at all: the key shows, which is ugly on purpose.
    assert.equal(api.t('landing.nothing.here'), 'landing.nothing.here');
});

test('an empty translation falls back rather than rendering blank', () => {
    const api = withCatalogues({ de: { greeting: '' }, store: { bowire_locale: 'de' } });
    api.loadLocale();
    assert.equal(api.t('greeting'), 'Hello');
});

test('the browser language decides when nothing is stored', () => {
    const api = withCatalogues({ browserLanguage: 'de-AT' });
    // de-AT has no catalogue, but de does — a regional variant is closer to
    // its base language than to English.
    assert.equal(api.loadLocale(), 'de');
});

test('an unknown browser language lands on English', () => {
    const api = withCatalogues({ browserLanguage: 'ja-JP' });
    assert.equal(api.loadLocale(), 'en');
});

test('an explicit choice beats the browser, and auto gives it back', () => {
    const store = {};
    const api = withCatalogues({ store, browserLanguage: 'de-DE' });
    assert.equal(api.loadLocale(), 'de');

    assert.equal(api.setLocale('en'), 'en');
    assert.equal(store.bowire_locale, 'en');
    assert.equal(api.localePreference(), 'en');

    assert.equal(api.setLocale('auto'), 'de', 'auto follows the browser again');
    assert.equal('bowire_locale' in store, false, 'auto stores nothing');
    assert.equal(api.localePreference(), 'auto');
});

test('English is listed first among the available locales', () => {
    const api = withCatalogues();
    assert.deepEqual(api.availableLocales(), ['en', 'de']);
});

test('placeholders are substituted, and unknown ones are left standing', () => {
    const api = withCatalogues();
    assert.equal(api.interpolate('Read {url} now', { url: 'http://x' }), 'Read http://x now');
    // A visible {count} is a bug report; "undefined" is a mystery.
    assert.equal(api.interpolate('Read {count} now', {}), 'Read {count} now');
});

test('a Bowire variable in a UI string survives translation', () => {
    // The header-library value placeholder really does read
    // "{{token}} resolves against the active environment". If interpolation
    // ate the inner braces the hint would render as "{token}".
    const api = withCatalogues();
    assert.equal(
        api.interpolate('{{token}} resolves against {env}', { token: 'X', env: 'staging' }),
        '{{token}} resolves against staging');
});

test('a catalogue that is not an object is ignored rather than breaking t', () => {
    const api = withCatalogues();
    api.registerLocaleCatalogue('fr', null);
    api.registerLocaleCatalogue('', { greeting: 'x' });
    assert.deepEqual(api.availableLocales(), ['en', 'de']);
});

// ---- the fragments against the catalogue ----
//
// The sweep is incremental: one surface at a time moves onto t(). Both
// directions of that move can fail silently, so CI checks both.
//
// A mistyped key renders as the raw key in the UI ("presets.manage.headng"),
// because t() falls back to the key itself rather than throwing. And a key
// added to the catalogue but never wired up leaves the literal sitting in the
// code, translated nowhere and noticed by nobody — that is exactly how
// headerLibrary.settings.lede came to exist for two commits without a caller.

const FRAGMENTS = resolve(__dirname, '../../../src/Kuestenlogik.Bowire/wwwroot/js');

// t('a.b.c') where the `t` is a whole identifier — not the tail of format(,
// assert( or split(. Keys built at run time (t('x.' + field)) are collected
// separately by their prefix, because their leaves cannot be read statically.
const STATIC_CALL = /(?<![A-Za-z0-9_$.])t\(\s*'([a-zA-Z0-9_.]+)'/g;
const DYNAMIC_CALL = /(?<![A-Za-z0-9_$.])t\(\s*'([a-zA-Z0-9_.]+\.)'\s*\+/g;

function fragmentSources() {
    return readdirSync(FRAGMENTS)
        .filter((f) => f.endsWith('.js') && f !== '_locales.js' && f !== 'i18n.js')
        .map((f) => ({ file: f, text: readFileSync(resolve(FRAGMENTS, f), 'utf8') }));
}

test('every t() key in the fragments exists in the catalogue', () => {
    const missing = [];
    for (const { file, text } of fragmentSources()) {
        for (const [, key] of text.matchAll(STATIC_CALL)) {
            if (key.endsWith('.')) continue;   // dynamic, handled above
            if (!(key in english)) missing.push(`${file}: ${key}`);
        }
    }
    assert.deepEqual(missing, [], `t() keys with no catalogue entry:\n  ${missing.join('\n  ')}`);
});

test('every catalogue key has a caller', () => {
    const sources = fragmentSources();
    const statics = new Set();
    const prefixes = [];
    for (const { text } of sources) {
        for (const [, key] of text.matchAll(STATIC_CALL)) statics.add(key);
        for (const [, prefix] of text.matchAll(DYNAMIC_CALL)) prefixes.push(prefix);
        // Key names also travel as data — a table of scopes carries
        // 'headerLibrary.scope.url' as a label and t()s it at render time.
        for (const [, key] of text.matchAll(/'([a-z][a-zA-Z0-9]*(?:\.[a-zA-Z0-9]+)+)'/g)) {
            statics.add(key);
        }
    }
    const orphans = keysOf(english).filter(
        (k) => !statics.has(k) && !prefixes.some((p) => k.startsWith(p)));
    assert.deepEqual(orphans, [], `catalogue keys nothing reads:\n  ${orphans.join('\n  ')}`);
});

test('no fragment both shadows t and calls it', () => {
    // Every fragment shares one IIFE scope, and the translator is a
    // one-letter name in it. `t` is also the obvious name for a loop counter,
    // a callback parameter, a temporary — the codebase has about a hundred of
    // them, and each one blinds its own scope to the translator.
    //
    // This is not theoretical. toast() held `var t = el('div', ...)` and the
    // sweep put t('common.dismiss') inside it, so every toast in Bowire threw
    // "t is not a function" until the local was renamed. Nothing else caught
    // it: the bundle parses, the analyser is happy, and no test rendered a
    // toast.
    //
    // The check is per file rather than per scope, which is stricter than the
    // language requires — a shadow three functions away from any t() call is
    // harmless. That is the point: the rule "a file that translates does not
    // name anything t" is one a reader can hold, and a scope-accurate check
    // would need a parser to answer a question nobody should have to ask.
    const DECLARES_T = [
        /(?<![A-Za-z0-9_$.])(?:var|let|const)\s+t\s*[=;,)]/,   // var t = …
        /function\s*\**\s*[A-Za-z0-9_$]*\s*\([^)]*(?<![A-Za-z0-9_$.])t\s*[,)]/, // function (t)
        /\(\s*t\s*\)\s*=>/,                                    // (t) => …
        /(?<![A-Za-z0-9_$.])for\s*\(\s*(?:var|let)\s+t\s*[=;]/, // for (var t = 0
        /(?<![A-Za-z0-9_$.])catch\s*\(\s*t\s*\)/,
    ];
    const CALLS_T = /(?<![A-Za-z0-9_$.])t\(\s*['"]/;

    const offenders = [];
    for (const { file, text } of fragmentSources()) {
        if (!CALLS_T.test(text)) continue;   // not swept yet — its turn will come
        const shadows = [];
        text.split('\n').forEach((line, i) => {
            if (line.trim().startsWith('//')) return;
            if (DECLARES_T.some((re) => re.test(line))) shadows.push(`${i + 1}: ${line.trim()}`);
        });
        if (shadows.length) offenders.push(`${file}\n    ${shadows.join('\n    ')}`);
    }

    assert.deepEqual(offenders, [],
        `a fragment that calls t() must not bind the name t:\n  ${offenders.join('\n  ')}`);
});

// ---- tNodes ----
//
// The alternative this exists to avoid: a `viewingAsPrefix` key and a
// `viewingAsSuffix` key with the <strong> wedged between them. That renders
// correctly in English and nowhere else, because it freezes English word
// order into the catalogue and a translator has no way to move the name.

test('tNodes splits the sentence where the translator put the slot', () => {
    const api = withCatalogues({
        en: { greet: 'Viewing as {name}. Anything you change is yours.' },
    });
    api.loadLocale();
    const node = { tag: 'strong' };
    assert.deepEqual(api.tNodes('greet', 'name', node), [
        'Viewing as ',
        node,
        '. Anything you change is yours.',
    ]);
});

test('tNodes follows the slot when a translation moves it', () => {
    // German wants the name later in the clause. That is the whole point.
    const api = withCatalogues({
        en: { greet: 'Viewing as {name}. Anything you change is yours.' },
        de: { greet: 'Sie sehen den Arbeitsplatz von {name}. Ihre Änderungen bleiben Ihre.' },
        store: { bowire_locale: 'de' },
    });
    api.loadLocale();
    const node = { tag: 'strong' };
    const parts = api.tNodes('greet', 'name', node);
    assert.equal(parts[0], 'Sie sehen den Arbeitsplatz von ');
    assert.equal(parts[1], node);
    assert.equal(parts[2], '. Ihre Änderungen bleiben Ihre.');
});

test('tNodes takes several nodes for one slot', () => {
    // The account chip puts a name and an optional e-mail in the same slot.
    const api = withCatalogues({ en: { greet: 'Viewing as {name}.' } });
    api.loadLocale();
    const strong = { tag: 'strong' };
    const email = { tag: 'span' };
    assert.deepEqual(api.tNodes('greet', 'name', [strong, email]),
        ['Viewing as ', strong, email, '.']);
});

test('tNodes still renders when a translation lost the slot', () => {
    // A sentence without the name beats a blank line, and the missing slot is
    // visible in the result rather than swallowed.
    const api = withCatalogues({ en: { greet: 'Viewing as somebody.' } });
    api.loadLocale();
    assert.deepEqual(api.tNodes('greet', 'name', { tag: 'strong' }), ['Viewing as somebody.']);
});

test('tNodes interpolates the other placeholders as usual', () => {
    const api = withCatalogues({ en: { greet: '{count} open, last was {name}.' } });
    api.loadLocale();
    const node = { tag: 'strong' };
    assert.deepEqual(api.tNodes('greet', 'name', node, { count: 3 }),
        ['3 open, last was ', node, '.']);
});
