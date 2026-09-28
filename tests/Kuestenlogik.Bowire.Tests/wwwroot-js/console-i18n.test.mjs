// #739 — the console is not the action log, and its exemption was not its own.
//
// Twenty-two lines carried `// i18n-exempt: the action log stores rendered text, see #689`
// while writing to `addConsoleEntry`, which stores nothing: `consoleLog` is an in-memory array
// with a cap, never `localStorage`, never a `.bww` export. The reason that carried #689 — a
// translated string in the data freezes a language and travels into somebody else's workspace —
// assumes storage. So the console could follow the interface language all along, and did not.
//
// What is pinned here is the property that makes the difference: the same entry reads in
// whichever language is active now. And the trap that made the obvious fix wrong — the row's
// colour used to be decided on the status text, so translating at the call site would have
// painted every response amber the moment the interface was not in English.

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, resolve } from 'node:path';
import { fragmentSources } from './_untranslated.mjs';

const __dirname = dirname(fileURLToPath(import.meta.url));
const CORE = resolve(__dirname, '../../../src/Kuestenlogik.Bowire/wwwroot/js');
const LOCALES = resolve(__dirname, '../../../src/Kuestenlogik.Bowire/wwwroot/locales');

const prologue = readFileSync(resolve(CORE, 'prologue.js'), 'utf8');
const execute = readFileSync(resolve(CORE, 'execute.js'), 'utf8');

/** The catalogue, read the way i18n.js reads it (comments and trailing commas allowed). */
function catalogue(lang) {
    const text = readFileSync(resolve(LOCALES, `${lang}.json`), 'utf8')
        .replace(/^\s*\/\/.*$/gm, '')
        .replace(/,(\s*[}\]])/g, '$1');
    return JSON.parse(text);
}

const english = catalogue('en');
const german = catalogue('de');

/** One named function, snipped out of a fragment — they are bundle pieces and cannot be imported. */
function snip(source, signature) {
    const start = source.indexOf(signature);
    assert.ok(start >= 0, `${signature} not found`);
    const open = source.indexOf('{', start);
    let depth = 0, i = open;
    for (; i < source.length; i++) {
        if (source[i] === '{') depth++;
        else if (source[i] === '}') { depth--; if (depth === 0) { i++; break; } }
    }
    return source.slice(start, i);
}

const T = `
    function t(key, params) {
        const text = dict[key];
        if (typeof text !== 'string' || text === '') return key;
        if (!params) return text;
        return text.replace(/(?<!\\{)\\{([a-zA-Z0-9_]+)\\}(?!\\})/g,
            (whole, name) => Object.prototype.hasOwnProperty.call(params, name)
                ? String(params[name]) : whole);
    }
`;

/** consoleStatus + consoleBody, run against a chosen language. */
function resolversFor(lang) {
    const body = snip(prologue, 'function consoleStatus(entry) {')
        + '\n' + snip(prologue, 'function consoleBody(entry) {');
    return new Function('dict', `${T}\n${body}\nreturn { consoleStatus, consoleBody };`)(
        catalogue(lang));
}

const en = resolversFor('en');
const de = resolversFor('de');

/** consoleEntryClass, which never needs a catalogue — that is the point of it. */
const consoleEntryClass = new Function(
    `${snip(execute, 'function consoleEntryClass(type, entry) {')}\nreturn consoleEntryClass;`)();

test('the same entry reads in whichever language is active now', () => {
    // The whole point. One entry, two languages, nothing about the entry changed.
    const entry = { type: 'response', statusKey: 'console.status.recordingStarted' };
    assert.equal(en.consoleStatus(entry), 'Recording started');
    assert.equal(de.consoleStatus(entry), 'Aufzeichnung gestartet');
});

test('a counted line is one key with a parameter, not a sentence built from pieces', () => {
    const entry = {
        type: 'response',
        statusKey: 'console.status.recordingStopped.many',
        statusParams: { count: 12 },
    };
    assert.equal(en.consoleStatus(entry), 'Recording stopped (12 steps)');
    assert.equal(de.consoleStatus(entry), 'Aufzeichnung beendet (12 Schritte)');
});

test('what a server said for itself is not translated', () => {
    // The body here is the remote's own problem title. Bowire has no business
    // rewriting it, and no key could.
    const entry = { type: 'error', status: 'Error', body: 'UNAVAILABLE: no healthy upstream' };
    assert.equal(en.consoleBody(entry), 'UNAVAILABLE: no healthy upstream');
    assert.equal(de.consoleBody(entry), 'UNAVAILABLE: no healthy upstream');
    assert.equal(de.consoleStatus(entry), 'Error');
});

test('an entry with neither key nor text renders nothing rather than the word undefined', () => {
    assert.equal(en.consoleStatus({ type: 'response' }), '');
    assert.equal(en.consoleBody({ type: 'response' }), '');
    assert.equal(en.consoleStatus(null), '');
});

test('the row colour is decided on the key, not on what a reader sees', () => {
    // Without this the fix would have been a regression: `consoleEntryClass` compared the status
    // against 'OK' / 'Completed' / 'Connected', so a German status would have fallen through to
    // 'response warn' and painted every successful response amber.
    const done = { type: 'response', status: 'Completed' };
    assert.equal(consoleEntryClass(done.type, done), 'response ok');

    const failed = { type: 'response', statusKey: 'console.status.recordingDropped' };
    assert.equal(consoleEntryClass(failed.type, failed), 'response warn');

    // And the class is the same whichever language is on screen, because it never asks.
    assert.notEqual(en.consoleStatus(failed), de.consoleStatus(failed));
    assert.equal(consoleEntryClass(failed.type, failed), 'response warn');

    const streaming = { type: 'response', status: 'Streaming' };
    assert.equal(consoleEntryClass(streaming.type, streaming), 'response');
});

// ---- the keys that travel as data ----
//
// These never go through `t('…')` at the call site — they sit on the entry as `statusKey:`,
// `bodyKey:` or `titleKey:` and are resolved when the row is painted. The catalogue guard for
// t() calls cannot see them, and a mistyped one renders as the bare key in the drawer.

const DATA_KEY = /\b(?:statusKey|bodyKey|titleKey)\s*:\s*'([a-zA-Z0-9_.-]+)'/g;

test('every key carried on an entry exists in both catalogues', () => {
    const missing = [];
    for (const { file, text } of fragmentSources()) {
        for (const [, key] of text.matchAll(DATA_KEY)) {
            if (!(key in english)) missing.push(`${file}: ${key} (en)`);
            if (!(key in german)) missing.push(`${file}: ${key} (de)`);
        }
    }
    assert.deepEqual(missing, [], `keys carried on an entry with no catalogue entry:\n  ${
        missing.join('\n  ')}`);
});

test('no exemption borrows the action log’s reason', () => {
    // The mark that started this: twenty-two console lines pointing at #689, whose reasoning
    // is about storage and never applied to them. An exemption that cites somebody else's
    // reason cannot be disagreed with, which is the whole value of writing it next to the string.
    const borrowed = [];
    for (const { file, text } of fragmentSources()) {
        text.split('\n').forEach((line, i) => {
            if (/\/\/\s*i18n-exempt\b/.test(line) && line.includes('#689')) {
                borrowed.push(`${file}:${i + 1}`);
            }
        });
    }
    assert.deepEqual(borrowed, [], `exemptions citing #689 rather than their own reason:\n  ${
        borrowed.join('\n  ')}`);
});
