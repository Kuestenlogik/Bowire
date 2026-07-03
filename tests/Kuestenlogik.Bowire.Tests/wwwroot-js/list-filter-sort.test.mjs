// #356 / #362 — unit tests for the shared sidebar list helpers in
// helpers.js: applyListFilterSort (filter-by-name + sort) and
// moveInArrayById (manual drag-reorder move). Pure functions, so they
// load cleanly through the fragment harness — proof the safety net
// reaches helpers.js, not just the small stand-alone modules.

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, resolve } from 'node:path';

const __dirname = dirname(fileURLToPath(import.meta.url));
const SRC = readFileSync(
    resolve(__dirname, '../../../src/Kuestenlogik.Bowire/wwwroot/js/helpers.js'),
    'utf8'
);

// helpers.js is a big fragment of the one-IIFE bundle. Rather than eval
// the whole thing (it references dozens of globals), snip out just the
// two pure functions under test by name and eval those in isolation.
function extractFn(name) {
    const start = SRC.indexOf(`function ${name}(`);
    assert.ok(start >= 0, `function ${name} not found in helpers.js`);
    // Walk braces from the first { after the signature to its match.
    const open = SRC.indexOf('{', start);
    let depth = 0, i = open;
    for (; i < SRC.length; i++) {
        if (SRC[i] === '{') depth++;
        else if (SRC[i] === '}') { depth--; if (depth === 0) { i++; break; } }
    }
    return SRC.slice(start, i);
}

const api = new Function(
    extractFn('applyListFilterSort') + '\n' +
    extractFn('moveInArrayById') + '\n' +
    'return { applyListFilterSort, moveInArrayById };'
)();

// ---- applyListFilterSort ----

test('filter matches names case-insensitively (substring)', () => {
    const items = [{ name: 'Orders' }, { name: 'users' }, { name: 'ORDER-history' }];
    const out = api.applyListFilterSort(items, { filter: 'ORD', nameOf: x => x.name });
    // 'ORD' matches Orders + ORDER-history (case-insensitive), not users
    assert.deepEqual(out.map(x => x.name), ['Orders', 'ORDER-history']);
});

test('blank filter + no sort passes list through in original order', () => {
    const items = [{ name: 'z' }, { name: 'a' }];
    const out = api.applyListFilterSort(items, { filter: '', nameOf: x => x.name });
    assert.deepEqual(out.map(x => x.name), ['z', 'a']);
});

test('sort=name orders alphabetically without mutating the input', () => {
    const items = [{ name: 'zebra' }, { name: 'apple' }, { name: 'mango' }];
    const out = api.applyListFilterSort(items, { sort: 'name', nameOf: x => x.name });
    assert.deepEqual(out.map(x => x.name), ['apple', 'mango', 'zebra']);
    assert.deepEqual(items.map(x => x.name), ['zebra', 'apple', 'mango']); // non-mutating
});

test('sort=created orders by createdOf descending (newest first)', () => {
    const items = [{ name: 'old', t: 1 }, { name: 'new', t: 9 }, { name: 'mid', t: 5 }];
    const out = api.applyListFilterSort(items, { sort: 'created', nameOf: x => x.name, createdOf: x => x.t });
    assert.deepEqual(out.map(x => x.name), ['new', 'mid', 'old']);
});

test('handles null / non-array input as empty', () => {
    assert.deepEqual(api.applyListFilterSort(null, {}), []);
    assert.deepEqual(api.applyListFilterSort(undefined, {}), []);
});

// ---- moveInArrayById ----

test('moves an item before a target', () => {
    const arr = [{ id: 'a' }, { id: 'b' }, { id: 'c' }];
    const ok = api.moveInArrayById(arr, 'c', 'a', false);
    assert.equal(ok, true);
    assert.deepEqual(arr.map(x => x.id), ['c', 'a', 'b']);
});

test('moves an item after a target', () => {
    const arr = [{ id: 'a' }, { id: 'b' }, { id: 'c' }];
    api.moveInArrayById(arr, 'a', 'b', true); // a after b
    assert.deepEqual(arr.map(x => x.id), ['b', 'a', 'c']);
});

test('unknown drag id is a no-op returning false', () => {
    const arr = [{ id: 'a' }, { id: 'b' }];
    const ok = api.moveInArrayById(arr, 'zzz', 'a', false);
    assert.equal(ok, false);
    assert.deepEqual(arr.map(x => x.id), ['a', 'b']);
});

test('missing target appends to the end', () => {
    const arr = [{ id: 'a' }, { id: 'b' }, { id: 'c' }];
    api.moveInArrayById(arr, 'a', 'nope', false);
    assert.deepEqual(arr.map(x => x.id), ['b', 'c', 'a']);
});
