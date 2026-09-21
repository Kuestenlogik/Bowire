// #689 — the action log stores what happened, not how it read.
//
// The Activity drawer used to paint a sentence that had been translated once, when the action
// happened, and then written to storage. Switching the interface language left every existing row
// in the old one, and a row recorded before a translation existed stayed English for good. Worse
// than untranslated: the string travels through a `.bww` export into somebody else's workspace,
// and nothing afterwards knows what it once meant.
//
// What is pinned here is the property that makes the difference — the same entry reads differently
// in two languages, without the entry changing — plus the one thing that must not regress: rows
// written before this change still render.

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, resolve } from 'node:path';

const __dirname = dirname(fileURLToPath(import.meta.url));
const SRC = resolve(__dirname, '../../../src/Kuestenlogik.Bowire/wwwroot/js/prologue.js');
const LOCALES = resolve(__dirname, '../../../src/Kuestenlogik.Bowire/wwwroot/locales');

const source = readFileSync(SRC, 'utf8');

/** The catalogue, read the way i18n.js reads it (comments and trailing commas allowed). */
function catalogue(lang) {
    const text = readFileSync(resolve(LOCALES, `${lang}.json`), 'utf8')
        .replace(/^\s*\/\/.*$/gm, '')
        .replace(/,(\s*[}\]])/g, '$1');
    return JSON.parse(text);
}

/**
 * actionTitle, snipped out of the fragment and run against a chosen language — prologue.js is a
 * bundle fragment and cannot be imported. `t` mirrors i18n.js: `{name}` is substituted, `{{name}}`
 * is Bowire's own variable syntax and survives.
 */
function loadFor(lang) {
    const start = source.indexOf('function actionTitle(entry) {');
    assert.ok(start >= 0, 'actionTitle not found in prologue.js');
    const open = source.indexOf('{', start);
    let depth = 0, i = open;
    for (; i < source.length; i++) {
        if (source[i] === '{') depth++;
        else if (source[i] === '}') { depth--; if (depth === 0) { i++; break; } }
    }
    const body = source.slice(start, i);
    const dict = catalogue(lang);
    return new Function('dict', `
        function t(key, params) {
            const text = dict[key];
            if (typeof text !== 'string' || text === '') return key;
            if (!params) return text;
            return text.replace(/(?<!\\{)\\{([a-zA-Z0-9_]+)\\}(?!\\})/g,
                (whole, name) => Object.prototype.hasOwnProperty.call(params, name)
                    ? String(params[name]) : whole);
        }
        ${body}
        return actionTitle;
    `)(dict);
}

const en = loadFor('en');
const de = loadFor('de');

test('the same entry reads in whichever language is active now', () => {
    // The whole point. One stored entry, two languages, no migration.
    const entry = {
        kind: 'workspace-create',
        titleKey: 'actionLog.workspaceCreated',
        titleParams: { name: 'Harbor' },
    };
    assert.equal(en(entry), 'Created workspace "Harbor"');
    assert.equal(de(entry), 'Arbeitsbereich „Harbor“ angelegt');
});

test('the operator\'s own words are not translated', () => {
    // The name is data. A workspace called "Deleted" stays "Deleted" in both languages, and a
    // workspace renamed later still reads under the name it had when the entry was written —
    // which is what the entry is about.
    const entry = {
        titleKey: 'actionLog.workspaceRenamed',
        titleParams: { from: 'Deleted', to: 'Angelegt' },
    };
    assert.ok(en(entry).includes('"Deleted"') && en(entry).includes('"Angelegt"'));
    assert.ok(de(entry).includes('„Deleted“') && de(entry).includes('„Angelegt“'));
});

test('an entry written before this change still renders, verbatim', () => {
    // The log is a rolling window and is deliberately not migrated. What is already in storage
    // carries a sentence and no key; it must keep painting rather than vanish.
    const legacy = { kind: 'workspace-create', title: 'Created workspace "Harbor"' };
    assert.equal(en(legacy), 'Created workspace "Harbor"');
    assert.equal(de(legacy), 'Created workspace "Harbor"');
});

test('a key beats a stale sentence on the same entry', () => {
    // Both present means the entry was rewritten by a newer build. The key is the newer truth.
    const both = {
        titleKey: 'actionLog.workspaceCreated',
        titleParams: { name: 'Harbor' },
        title: 'whatever it used to say',
    };
    assert.equal(de(both), 'Arbeitsbereich „Harbor“ angelegt');
});

test('an entry with neither falls back to its kind rather than to nothing', () => {
    // A row with no words at all would look like a rendering bug. The kind is at least a handle.
    assert.equal(en({ kind: 'workspace-create' }), 'workspace-create');
    assert.equal(en({}), '');
    assert.equal(en(null), '');
});

test('every key the call sites use exists in both catalogues', () => {
    // The parity test covers the catalogues against each other; this covers them against the
    // code. A key nobody translated renders as its own name, which reads as a bug on screen.
    const dir = resolve(__dirname, '../../../src/Kuestenlogik.Bowire/wwwroot/js');
    const used = new Set();
    for (const file of ['auth.js', 'collections.js', 'history-env.js', 'prologue.js',
        'render-env-auth.js', 'render-main.js', 'render-sidebar.js', 'settings.js',
        'workspace-templates.js']) {
        const text = readFileSync(resolve(dir, file), 'utf8');
        for (const m of text.matchAll(/titleKey:\s*'([^']+)'/g)) used.add(m[1]);
    }
    assert.ok(used.size >= 12, `expected the call sites to name many keys, saw ${used.size}`);

    const [e, d] = [catalogue('en'), catalogue('de')];
    const missing = [...used].filter(k => !(k in e) || !(k in d));
    assert.deepEqual(missing, []);
});

test('the store keeps the key, not the rendered row', () => {
    // Read off the source: _persistActionLog writes titleKey and titleParams. If it ever went
    // back to persisting only the sentence, every test above would still pass and the defect
    // would be back — the language would freeze at the moment of writing again.
    const from = source.indexOf('function _persistActionLog()');
    const slim = source.slice(from, from + 1200);
    assert.match(slim, /titleKey:/);
    assert.match(slim, /titleParams:/);
});
