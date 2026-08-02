// #551 — what the render path is allowed to do per method row.
//
// `coverageState` / `getMethodCoverage` / `isFavorite` are each called
// once (or twice) per row while the sidebar renders. All three read
// localStorage and parsed it, and the coverage cache computed its own
// invalidation signature FROM the parsed list — so the cache saved the
// grouping and never the parse. At the default 500-entry cap that is
// ~54 KB re-parsed two or three times per row; at the settable cap of
// 5000 it is ~545 KB.
//
// These tests count the parses rather than timing them, so they stay
// meaningful on any machine.

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, resolve } from 'node:path';

const __dirname = dirname(fileURLToPath(import.meta.url));
const SRC_DIR = resolve(__dirname, '../../../src/Kuestenlogik.Bowire/wwwroot/js');
const COVERAGE = readFileSync(resolve(SRC_DIR, 'coverage.js'), 'utf8');
const HISTORY = readFileSync(resolve(SRC_DIR, 'history-env.js'), 'utf8');

// A counting JSON shim plus an in-memory localStorage, wrapped around a
// fragment the way the concat bundle does.
function load(src, exportsExpr, seed) {
    const prelude = `
        var _ls = ${JSON.stringify(seed || {})};
        var _parses = 0;
        var _gets = 0;
        var localStorage = {
            getItem: function (k) { _gets++; return Object.prototype.hasOwnProperty.call(_ls, k) ? _ls[k] : null; },
            setItem: function (k, v) { _ls[k] = String(v); },
            removeItem: function (k) { delete _ls[k]; }
        };
        // Read the real parser off globalThis: the JSON shim declared
        // below shadows the global for this whole scope and is hoisted,
        // so reading JSON.parse here would hit the unassigned local.
        var _realParse = globalThis.JSON.parse;
        var _realStringify = globalThis.JSON.stringify;
        var JSON = {
            parse: function (s) { _parses++; return _realParse(s); },
            stringify: _realStringify
        };
        function wsKey(k) { return k; }
        // Declared in prologue.js in production; the fragments share one
        // IIFE scope, so history-env.js closes over it.
        var FAVORITES_KEY = 'bowire_favorites';
        var HISTORY_KEY = 'bowire_history';
        var MAX_HISTORY = 100;
        var historySearchQuery = '';
        var historyStatusFilter = 'all';
        function render() {}
        function el() { return null; }
        function svgIcon() { return ''; }
        var window = {};
        var services = [];
    `;
    const postlude = `
        return Object.assign(${exportsExpr}, {
            _parses: function () { return _parses; },
            _gets: function () { return _gets; },
            _write: function (k, v) { _ls[k] = v; }
        });
    `;
    return new Function(prelude + '\n' + src + '\n' + postlude)();
}

function runHistory(n) {
    const out = [];
    for (let i = 0; i < n; i++) {
        out.push({ methodId: 'Svc::M' + (i % 40), startedAt: 1000 + i, outcome: 'ok', durationMs: 3 });
    }
    return JSON.stringify(out);
}

// ---- coverage ----

test('coverage: 200 rows cost ONE parse, not one per lookup', () => {
    const sb = load(COVERAGE,
        '{ getMethodCoverage: getMethodCoverage, coverageState: coverageState, _invalidate: _invalidateCoverageCache }',
        { 'bowire_run_history': runHistory(500) });

    const before = sb._parses();
    for (let row = 0; row < 200; row++) {
        sb.coverageState('Svc', 'M' + (row % 40));
        sb.getMethodCoverage('Svc', 'M' + (row % 40));
    }
    const parses = sb._parses() - before;

    assert.equal(parses, 1,
        `expected one parse for the whole render pass, got ${parses} — the cache is reading before it checks`);
});

test('coverage: a write invalidates the cache exactly', () => {
    const sb = load(COVERAGE,
        '{ getMethodCoverage: getMethodCoverage, coverageState: coverageState }',
        { 'bowire_run_history': runHistory(10) });

    sb.coverageState('Svc', 'M1');
    const after = sb._parses();

    sb._write('bowire_run_history', runHistory(11));
    sb.coverageState('Svc', 'M1');

    assert.equal(sb._parses(), after + 1, 'a changed history is re-parsed once');
});

test('coverage: a same-length rewrite is still noticed', () => {
    // The old signature was length + last startedAt. An edit preserving
    // both went unnoticed and served a stale index.
    const sb = load(COVERAGE,
        '{ coverageState: coverageState, getMethodCoverage: getMethodCoverage }',
        { 'bowire_run_history': JSON.stringify([{ methodId: 'A::x', startedAt: 5, outcome: 'ok' }]) });

    assert.equal(sb.getMethodCoverage('A', 'x').runs, 1);
    assert.equal(sb.getMethodCoverage('B', 'y').runs, 0);

    // Same entry count, same startedAt, different method.
    sb._write('bowire_run_history', JSON.stringify([{ methodId: 'B::y', startedAt: 5, outcome: 'ok' }]));

    assert.equal(sb.getMethodCoverage('B', 'y').runs, 1, 'the rewrite is seen');
    assert.equal(sb.getMethodCoverage('A', 'x').runs, 0);
});

// ---- favorites ----

test('favorites: 200 rows cost ONE parse', () => {
    const favs = JSON.stringify(
        Array.from({ length: 50 }, (_, i) => ({ service: 'Svc', method: 'M' + i })));
    const sb = load(HISTORY, '{ isFavorite: isFavorite, getFavorites: getFavorites }',
        { 'bowire_favorites': favs });

    const before = sb._parses();
    for (let row = 0; row < 200; row++) sb.isFavorite('Svc', 'M' + (row % 50));
    const parses = sb._parses() - before;

    assert.equal(parses, 1, `expected one parse per render pass, got ${parses}`);
});

test('favorites: membership is exact', () => {
    const sb = load(HISTORY, '{ isFavorite: isFavorite }',
        { 'bowire_favorites': JSON.stringify([{ service: 'Orders', method: 'List' }]) });

    assert.equal(sb.isFavorite('Orders', 'List'), true);
    assert.equal(sb.isFavorite('Orders', 'Get'), false);
    assert.equal(sb.isFavorite('Invoices', 'List'), false);
});

test('favorites: a write is picked up on the next read', () => {
    const sb = load(HISTORY, '{ isFavorite: isFavorite }',
        { 'bowire_favorites': '[]' });

    assert.equal(sb.isFavorite('Orders', 'List'), false);
    sb._write('bowire_favorites', JSON.stringify([{ service: 'Orders', method: 'List' }]));
    assert.equal(sb.isFavorite('Orders', 'List'), true,
        'keyed on the raw string, so any writer invalidates it');
});
