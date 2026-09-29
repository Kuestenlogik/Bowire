// #313 — a distributed parallel run in the Benchmarks pane.
//
// collections.js's "Save to Benchmarks" turns a finished parallel run into a
// benchmark envelope, and every later run of the same source into another
// entry of that envelope's runs[] — the history the diff banner reads. These
// tests hold that for the distributed shape: the run keeps its hosts and
// what each of them did, and a second save lands next to the first instead
// of replacing it.

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { compileFragment } from './_load-fragment.mjs';

const _host = `
        var benchmarksList = [];
        var benchmarksSelectedId = null;
        var railMode = 'design';
        var toasts = [];
        function toast(msg, kind) { toasts.push({ msg: msg, kind: kind }); }
        function loadBenchmarks() {}
        var persisted = 0;
        function persistBenchmarks() { persisted++; }
        function _benchmarkHistoryCap() { return 5; }
        function render() {}
        var localStorage = { getItem: function () { return null; }, setItem: function () {} };
        return {
            toSpec: _parallelStateToBenchmarkSpec,
            save: _saveParallelRunToBenchmarks,
            list: function () { return benchmarksList; },
            persisted: function () { return persisted; },
            toasts: function () { return toasts; }
        };
`;

const load = compileFragment('../../../src/Kuestenlogik.Bowire/wwwroot/js/collections.js', [], _host);

function distributedState(passes) {
    return {
        kind: 'collection',
        sourceId: 'col_1',
        sourceName: 'Orders',
        sessionCount: 2,
        distributed: true,
        hosts: ['https://node-a.example', 'http://10.0.0.5:5080'],
        hostSummaries: [
            { host: 'https://node-a.example', sessionCount: 2, passCount: passes, failCount: 0, durationMs: 40 },
            { host: 'http://10.0.0.5:5080', sessionCount: 0, passCount: 0, failCount: 0, error: 'refused: requireSignedExecutor: a non-loopback executor must be https' }
        ],
        startedAt: 1_700_000_000_000,
        durationMs: 40,
        sessions: [
            { index: 0, results: Array.from({ length: passes }, () => ({ pass: true, status: 200, durationMs: 10, stepIndex: 0, stepLabel: 'GET /orders' })) },
            { index: 1, results: [] }
        ]
    };
}

test('the saved run keeps its hosts and what each did, a refusal included', () => {
    const f = load();
    const spec = f.toSpec(distributedState(3));
    const marker = spec.lastRun.parallelSessions;
    assert.equal(marker.distributed, true);
    assert.deepEqual(marker.hosts, ['https://node-a.example', 'http://10.0.0.5:5080']);
    assert.match(marker.hostSummaries[1].error, /^refused:/);
    assert.equal(spec.lastRun.total, 3);
    assert.equal(spec.lastRun.success, 3);
});

test('a second run of the same source lands next to the first, for the diff', () => {
    const f = load();
    f.save(distributedState(3));
    f.save(distributedState(5));
    const list = f.list();
    assert.equal(list.length, 1);
    assert.equal(list[0].runs.length, 2);
    assert.deepEqual(list[0].runs.map((r) => r.total), [3, 5]);
    assert.equal(list[0].lastRun.total, 5);
    assert.equal(f.persisted(), 2);
});

test('the saved host summaries are copies, not the live run state', () => {
    const f = load();
    const state = distributedState(1);
    const spec = f.toSpec(state);
    state.hostSummaries[0].passCount = 99;
    assert.equal(spec.lastRun.parallelSessions.hostSummaries[0].passCount, 1);
});
