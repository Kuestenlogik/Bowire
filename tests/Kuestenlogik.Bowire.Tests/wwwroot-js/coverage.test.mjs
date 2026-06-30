// v2.2 T3 — coverage.js unit tests.
//
// Targets the pure helpers:
//   * recordMethodRun         — append + FIFO eviction + outcome bucketing
//   * getRunHistory           — round-trip through the localStorage shim
//   * getMethodCoverage       — per-method aggregate counters
//   * coverageState           — state machine (recent / stale / failing / uncovered)
//   * getCoverageSummary      — workspace-wide rollup over `services`
//   * methodIdFor             — canonical `<svc>::<method>` shape
//
// Same wrap trick as history-env.test.mjs — the fragment is concatenated
// into prologue.js's IIFE in production; we recreate that scope by
// declaring the host-provided names (wsKey, render, el, services) at the
// top of the wrapper and an in-memory localStorage shim.

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, resolve } from 'node:path';

const __dirname = dirname(fileURLToPath(import.meta.url));
const SRC = readFileSync(
    resolve(__dirname, '../../../src/Kuestenlogik.Bowire/wwwroot/js/coverage.js'),
    'utf8'
);

function loadCoverage(state) {
    state = state || {};
    const prelude = `
        // In-memory localStorage shim.
        var _ls = {};
        var localStorage = {
            getItem: function (k) { return Object.prototype.hasOwnProperty.call(_ls, k) ? _ls[k] : null; },
            setItem: function (k, v) { _ls[k] = String(v); },
            removeItem: function (k) { delete _ls[k]; }
        };
        function wsKey(k) { return 'ws::' + k; }
        function render() {}
        // helpers.js' \`el\` is referenced from the DOM-side renderers
        // (renderCoverageChip, renderRunHistoryView). Tests only touch
        // the pure-data helpers — provide a tolerant stub.
        function el() { return null; }
        // services[] is read by getCoverageSummary. Tests inject their
        // own via _setServices below.
        var services = ${JSON.stringify(state.services || [])};
        // Tour-event no-op so the coverage layer never explodes if a
        // future enhancement hooks into bowireFireTourEvent.
        var window = (typeof globalThis.window !== 'undefined') ? globalThis.window : {};
    `;
    const postlude = `
        return {
            recordMethodRun: recordMethodRun,
            safeRecordMethodRun: safeRecordMethodRun,
            getRunHistory: getRunHistory,
            clearRunHistory: clearRunHistory,
            getMethodCoverage: getMethodCoverage,
            coverageState: coverageState,
            getCoverageSummary: getCoverageSummary,
            methodIdFor: methodIdFor,
            _setServices: function (s) { services = s; _invalidateCoverageCache(); },
            _setRunHistoryCap: function (n) { localStorage.setItem(RUN_HISTORY_CAP_KEY, String(n)); },
            _getRawStorage: function () { return _ls; }
        };
    `;
    const body = prelude + '\n' + SRC + '\n' + postlude;
    const fn = new Function(body);
    return fn();
}

// ---- methodIdFor ----

test('methodIdFor: canonical <svc>::<method> form', () => {
    const sb = loadCoverage();
    assert.equal(sb.methodIdFor('Greeter', 'SayHello'), 'Greeter::SayHello');
});

test('methodIdFor: empty inputs do not crash', () => {
    const sb = loadCoverage();
    assert.equal(sb.methodIdFor(undefined, undefined), '::');
    assert.equal(sb.methodIdFor('', ''), '::');
    assert.equal(sb.methodIdFor('A', null), 'A::');
});

// ---- recordMethodRun ----

test('recordMethodRun: appends an entry with the canonical methodId', () => {
    const sb = loadCoverage();
    const e = sb.recordMethodRun({
        service: 'Greeter', method: 'SayHello',
        source: 'discover', durationMs: 42, outcome: 'ok'
    });
    assert.ok(e);
    assert.equal(e.methodId, 'Greeter::SayHello');
    assert.equal(e.outcome, 'ok');
    assert.equal(e.source, 'discover');
    assert.equal(typeof e.runId, 'string');
    assert.ok(e.startedAt > 0);
});

test('recordMethodRun: rejects empty methodId (no svc + no method)', () => {
    const sb = loadCoverage();
    const e = sb.recordMethodRun({ outcome: 'ok' });
    assert.equal(e, null);
    assert.deepEqual(sb.getRunHistory(), []);
});

test('recordMethodRun: unknown outcome buckets as error', () => {
    const sb = loadCoverage();
    const e = sb.recordMethodRun({
        service: 'A', method: 'B', outcome: 'banana'
    });
    assert.equal(e.outcome, 'error');
});

test('recordMethodRun: three runs land as three history entries', () => {
    const sb = loadCoverage();
    sb.recordMethodRun({ service: 'A', method: 'B', outcome: 'ok' });
    sb.recordMethodRun({ service: 'A', method: 'B', outcome: 'ok' });
    sb.recordMethodRun({ service: 'A', method: 'B', outcome: 'fail' });
    const hist = sb.getRunHistory();
    assert.equal(hist.length, 3);
    assert.equal(hist[0].methodId, 'A::B');
    assert.equal(hist[2].outcome, 'fail');
});

test('recordMethodRun: FIFO-evicts when cap is hit', () => {
    const sb = loadCoverage();
    sb._setRunHistoryCap(50);   // minimum allowed
    for (let i = 0; i < 60; i++) {
        sb.recordMethodRun({
            service: 'A', method: 'B',
            outcome: i % 7 === 0 ? 'fail' : 'ok'
        });
    }
    const hist = sb.getRunHistory();
    assert.equal(hist.length, 50);
});

test('recordMethodRun: round-trips through the localStorage shim', () => {
    const sb = loadCoverage();
    sb.recordMethodRun({ service: 'A', method: 'B', outcome: 'ok' });
    const raw = sb._getRawStorage()['ws::bowire_run_history'];
    assert.ok(raw, 'storage bucket should be populated');
    const parsed = JSON.parse(raw);
    assert.equal(parsed.length, 1);
    assert.equal(parsed[0].methodId, 'A::B');
});

test('safeRecordMethodRun: never throws even on malformed input', () => {
    const sb = loadCoverage();
    // Should swallow the would-be exception silently.
    assert.doesNotThrow(() => sb.safeRecordMethodRun(null));
    assert.doesNotThrow(() => sb.safeRecordMethodRun({}));
    assert.doesNotThrow(() => sb.safeRecordMethodRun({ service: 'A' })); // no method
});

// ---- getMethodCoverage ----

test('getMethodCoverage: empty for an un-run method', () => {
    const sb = loadCoverage();
    const c = sb.getMethodCoverage('A', 'B');
    assert.equal(c.runs, 0);
    assert.equal(c.lastRunAt, null);
    assert.equal(c.lastOutcome, null);
});

test('getMethodCoverage: aggregates last-7-day / last-30-day counts', () => {
    const sb = loadCoverage();
    const NOW = Date.now();
    const DAY = 24 * 60 * 60 * 1000;
    sb.recordMethodRun({
        service: 'A', method: 'B', outcome: 'ok',
        startedAt: NOW - 2 * DAY
    });
    sb.recordMethodRun({
        service: 'A', method: 'B', outcome: 'ok',
        startedAt: NOW - 20 * DAY
    });
    sb.recordMethodRun({
        service: 'A', method: 'B', outcome: 'fail',
        startedAt: NOW - 40 * DAY
    });
    const c = sb.getMethodCoverage('A', 'B');
    assert.equal(c.runs, 3);
    assert.equal(c.runsLast7Days, 1);
    assert.equal(c.runsLast30Days, 2);
    assert.equal(c.passRate30d, 1); // both 30d-window entries were 'ok'
});

test('getMethodCoverage: lastOutcome reflects the most-recent run', () => {
    const sb = loadCoverage();
    sb.recordMethodRun({ service: 'A', method: 'B', outcome: 'ok', startedAt: Date.now() - 1000 });
    sb.recordMethodRun({ service: 'A', method: 'B', outcome: 'fail', startedAt: Date.now() });
    const c = sb.getMethodCoverage('A', 'B');
    assert.equal(c.lastOutcome, 'fail');
});

test('getMethodCoverage: sources is a deduped set of source labels', () => {
    const sb = loadCoverage();
    sb.recordMethodRun({ service: 'A', method: 'B', outcome: 'ok', source: 'discover' });
    sb.recordMethodRun({ service: 'A', method: 'B', outcome: 'ok', source: 'discover' });
    sb.recordMethodRun({ service: 'A', method: 'B', outcome: 'ok', source: 'benchmark' });
    const c = sb.getMethodCoverage('A', 'B');
    assert.deepEqual(c.sources.sort(), ['benchmark', 'discover']);
});

// ---- coverageState ----

test('coverageState: no runs → uncovered', () => {
    const sb = loadCoverage();
    assert.equal(sb.coverageState('A', 'B'), 'uncovered');
});

test('coverageState: recent ok → recent', () => {
    const sb = loadCoverage();
    sb.recordMethodRun({ service: 'A', method: 'B', outcome: 'ok' });
    assert.equal(sb.coverageState('A', 'B'), 'recent');
});

test('coverageState: 20-day-old ok → stale', () => {
    const sb = loadCoverage();
    const DAY = 24 * 60 * 60 * 1000;
    sb.recordMethodRun({
        service: 'A', method: 'B', outcome: 'ok',
        startedAt: Date.now() - 20 * DAY
    });
    assert.equal(sb.coverageState('A', 'B'), 'stale');
});

test('coverageState: most-recent run was fail → failing (even if old)', () => {
    const sb = loadCoverage();
    sb.recordMethodRun({
        service: 'A', method: 'B', outcome: 'ok',
        startedAt: Date.now() - 2 * 60 * 60 * 1000   // 2h ago
    });
    sb.recordMethodRun({
        service: 'A', method: 'B', outcome: 'fail',
        startedAt: Date.now() - 1000                   // 1s ago
    });
    assert.equal(sb.coverageState('A', 'B'), 'failing');
});

test('coverageState: 60-day-old ok → uncovered (aged out of window)', () => {
    const sb = loadCoverage();
    const DAY = 24 * 60 * 60 * 1000;
    sb.recordMethodRun({
        service: 'A', method: 'B', outcome: 'ok',
        startedAt: Date.now() - 60 * DAY
    });
    assert.equal(sb.coverageState('A', 'B'), 'uncovered');
});

// ---- getCoverageSummary ----

test('getCoverageSummary: empty services array → zero totals', () => {
    const sb = loadCoverage();
    const s = sb.getCoverageSummary();
    assert.equal(s.total, 0);
});

test('getCoverageSummary: counts every state across the services tree', () => {
    const sb = loadCoverage({
        services: [{
            name: 'Greeter',
            methods: [
                { name: 'SayHello' },
                { name: 'SayGoodbye' },
                { name: 'Whisper' }
            ]
        }]
    });
    // SayHello run cleanly → recent.
    sb.recordMethodRun({ service: 'Greeter', method: 'SayHello', outcome: 'ok' });
    // SayGoodbye last run was fail → failing.
    sb.recordMethodRun({ service: 'Greeter', method: 'SayGoodbye', outcome: 'fail' });
    // Whisper never run → uncovered.
    const summary = sb.getCoverageSummary();
    assert.equal(summary.total, 3);
    assert.equal(summary.recent, 1);
    assert.equal(summary.failing, 1);
    assert.equal(summary.uncovered, 1);
});

// ---- clearRunHistory ----

test('clearRunHistory: wipes the bucket', () => {
    const sb = loadCoverage();
    sb.recordMethodRun({ service: 'A', method: 'B', outcome: 'ok' });
    assert.equal(sb.getRunHistory().length, 1);
    sb.clearRunHistory();
    assert.equal(sb.getRunHistory().length, 0);
});
