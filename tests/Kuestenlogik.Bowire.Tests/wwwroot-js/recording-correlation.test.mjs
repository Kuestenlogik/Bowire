// #539 — the correlated-timeline fragment's PURE helpers, plus the two
// contracts that keep it from taking the workbench down.
//
// The analysis itself is C# (RecordingCorrelationAnalyzerTests covers
// it). What lives in JS is cache identity, key resolution, envelope
// parsing and formatting — and the rule that renderRecordingTimeline is
// a read. render() has no try/catch, so a renderer that fetches,
// persists or re-renders is not a style problem, it is an outage.

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { compileFragment } from './_load-fragment.mjs';

// The fragment is spliced into core's IIFE, so it is evaluated with
// core's globals already in scope. Stub the handful it reads in the
// appended block (hoisted), and compile it alone with its real filename
// so V8 attributes coverage to recording-correlation.js (#367).
const _prelude = `
    var recordingsList = (state.recordingsList || []);
    var recordingManagerSelectedId = state.selectedId || null;
    var config = { prefix: '/bowire' };
    var calls = { render: 0, persist: 0, fetch: 0 };
    function render() { calls.render++; }
    function persistRecordings() { calls.persist++; }
    function hydrateRecording(r) { return Promise.resolve(r); }
    function toast() {}
    function svgIcon() { return ''; }
    var fetch = function () { calls.fetch++; return Promise.reject(new Error('not stubbed')); };
`;
const _postlude = `
    return {
        calls: calls,
        correlationCacheKey: correlationCacheKey,
        correlationKeyOf: correlationKeyOf,
        correlationModelFor: correlationModelFor,
        correlationIsPending: correlationIsPending,
        currentCorrelationRecording: currentCorrelationRecording,
        recordingActiveDetailTab: recordingActiveDetailTab,
        setRecordingDetailTab: setRecordingDetailTab,
        parseEnvelope: _parseRecordingEnvelope,
        fmtOffset: _fmtOffset,
        fmtDuration: _fmtDuration,
        matchWord: _matchWord,
        eventFailed: _correlationEventFailed,
        cache: function () { return recordingCorrelationCache; },
        keyMenuOpen: function () { return recordingCorrelationKeyMenuOpen; }
    };
`;
const _load = compileFragment(
    '../../../src/Kuestenlogik.Bowire.Recordings/wwwroot/js/recording-correlation.js',
    ['state'],
    _prelude + '\n' + _postlude
);

function load(state) {
    return _load({ state: state || {} });
}

// A couple of tests below inspect the raw fragment text.
const SRC = _load.source;

// Comments name the very symbols the contract forbids, so strip them
// before searching or the assertions pass/fail for the wrong reason.
function stripComments(src) {
    return src
        .replace(/\/\*[\s\S]*?\*\//g, '')
        .split('\n')
        .map((l) => l.replace(/(^|[^:])\/\/.*$/, '$1'))
        .join('\n');
}

function bodyOf(name) {
    const src = stripComments(SRC);
    const start = src.indexOf('function ' + name + '(');
    assert.notEqual(start, -1, name + ' must exist in the fragment');
    let depth = 0;
    let i = src.indexOf('{', start);
    const from = i;
    for (; i < src.length; i++) {
        if (src[i] === '{') depth++;
        else if (src[i] === '}') {
            depth--;
            if (depth === 0) return src.slice(from, i + 1);
        }
    }
    throw new Error('unbalanced braces in ' + name);
}

// ---- the render-path contract ----

test('renderRecordingTimeline never fetches, persists or re-renders', () => {
    const body = bodyOf('renderRecordingTimeline');
    assert.ok(!/\bfetch\s*\(/.test(body), 'renderRecordingTimeline must not fetch');
    assert.ok(!/persistRecordings\s*\(/.test(body), 'renderRecordingTimeline must not persist');
    assert.ok(!/[^a-zA-Z_]render\s*\(\s*\)/.test(body), 'renderRecordingTimeline must not call render()');
    assert.ok(!/ensureRecordingCorrelation\s*\(/.test(body),
        'renderRecordingTimeline must not kick the analysis — the tab click does');
});

test('the lane click handler re-resolves its target from the DOM, not a closure', () => {
    const body = bodyOf('_correlationLaneClick');
    assert.match(body, /closest\(/);
    assert.match(body, /dataset\.stepId/);
});

test('the key menu handler re-resolves the recording at click time', () => {
    const body = bodyOf('_correlationKeyMenuClick');
    assert.match(body, /currentCorrelationRecording\(\)/);
    assert.match(body, /dataset\.corrName/);
});

test('the fragment declares its module state with var, not let/const', () => {
    // recording-correlation.js is spliced BEFORE recording.js (ordinal
    // by resource name), and both share core's single IIFE. A `let`
    // here is a temporal-dead-zone ReferenceError waiting for the first
    // reader that runs earlier than expected.
    const decls = stripComments(SRC).match(/^\s{4}(var|let|const)\s+recording\w+/gm) || [];
    assert.ok(decls.length >= 4, 'expected the module state block to be found');
    for (const d of decls) {
        assert.match(d.trim(), /^var /, 'module state must use var: ' + d.trim());
    }
});

// ---- cache identity ----

test('the cache key changes with the recording, its step count and the key', () => {
    const h = load();
    const rec = { id: 'r1', steps: [{}, {}] };
    const base = h.correlationCacheKey(rec, 'shipId', '101');

    assert.equal(h.correlationCacheKey(rec, 'shipId', '101'), base, 'stable for identical inputs');
    assert.notEqual(h.correlationCacheKey(rec, 'shipId', '102'), base);
    assert.notEqual(h.correlationCacheKey(rec, 'craneId', '101'), base);
    assert.notEqual(h.correlationCacheKey({ id: 'r2', steps: [{}, {}] }, 'shipId', '101'), base);
    // A new capture invalidates: the model would otherwise be missing a
    // step and quietly wrong.
    assert.notEqual(h.correlationCacheKey({ id: 'r1', steps: [{}, {}, {}] }, 'shipId', '101'), base);
});

test('the cache key counts manifest-only and stepCount-only shapes too', () => {
    const h = load();
    assert.equal(
        h.correlationCacheKey({ id: 'r', stepsManifest: [{}, {}] }, null, null),
        h.correlationCacheKey({ id: 'r', stepCount: 2 }, null, null));
});

// ---- key resolution ----

test('correlationKeyOf reads the persisted correlation field, or nothing', () => {
    const h = load();
    assert.equal(h.correlationKeyOf({ id: 'r' }), null);
    assert.equal(h.correlationKeyOf({ id: 'r', correlation: {} }), null);
    assert.equal(h.correlationKeyOf({ id: 'r', correlation: { name: 'shipId' } }), null,
        'a name without a value is not a key');
    assert.deepEqual(
        h.correlationKeyOf({ id: 'r', correlation: { name: 'shipId', value: 101 } }),
        { name: 'shipId', value: '101' },
        'values are normalised to strings — a JSON number and its text must hit one cache slot');
});

test('correlationModelFor and correlationIsPending are reads with no side effects', () => {
    const h = load();
    const rec = { id: 'r', steps: [{}] };
    assert.equal(h.correlationModelFor(rec), null);
    assert.equal(h.correlationIsPending(rec), false);
    h.cache()[h.correlationCacheKey(rec, null, null)] = { spanMs: 5 };
    assert.deepEqual(h.correlationModelFor(rec), { spanMs: 5 });
    assert.equal(h.calls.render, 0);
    assert.equal(h.calls.fetch, 0);
    assert.equal(h.calls.persist, 0);
});

// ---- tab scoping ----

test('the detail tab is scoped to the recording it was opened on', () => {
    const h = load();
    assert.equal(h.recordingActiveDetailTab('r1'), 'steps');
    h.setRecordingDetailTab('r1', 'timeline');
    assert.equal(h.recordingActiveDetailTab('r1'), 'timeline');
    // Selecting a different recording opens on Steps rather than
    // inheriting a Timeline tab whose model has not been computed.
    assert.equal(h.recordingActiveDetailTab('r2'), 'steps');
    assert.equal(h.recordingActiveDetailTab(null), 'steps');
});

test('leaving the timeline tab closes the key picker', () => {
    const h = load();
    h.setRecordingDetailTab('r1', 'timeline');
    assert.equal(h.keyMenuOpen(), false);
    h.setRecordingDetailTab('r1', 'steps');
    assert.equal(h.keyMenuOpen(), false);
});

// ---- .bwr import ----

test('both .bwr envelopes parse, and nothing else does', () => {
    const h = load();
    assert.equal(h.parseEnvelope('{"recordings":[{"id":"a","steps":[]},{"id":"b","steps":[]}]}').length, 2);
    assert.equal(h.parseEnvelope('{"id":"a","name":"x","steps":[{}]}').length, 1);
    assert.equal(h.parseEnvelope('{"recordings":[]}').length, 0);
    // A workspace file, a HAR, an arbitrary object: no steps, no import.
    assert.equal(h.parseEnvelope('{"log":{"entries":[]}}').length, 0);
    assert.equal(h.parseEnvelope('{"recordings":[{"id":"a"}]}').length, 0);
    assert.throws(() => h.parseEnvelope('not json'));
});

// ---- formatting ----

test('offsets and durations read as human time, not raw milliseconds', () => {
    const h = load();
    assert.equal(h.fmtOffset(0), '0 ms');
    assert.equal(h.fmtOffset(950), '950 ms');
    assert.equal(h.fmtOffset(1500), '1.50 s');
    assert.equal(h.fmtOffset(65000), '1 min 5 s');
    assert.equal(h.fmtDuration(38), '38 ms');
    assert.equal(h.fmtDuration(3000), '3.00 s');
    assert.equal(h.matchWord('strong'), 'strong');
    assert.equal(h.matchWord('weak'), 'weak');
    assert.equal(h.matchWord('none'), 'no');
});

test('failure shading follows the recorded status, across protocol vocabularies', () => {
    const h = load();
    assert.equal(h.eventFailed({ status: 'OK' }), false);
    assert.equal(h.eventFailed({ status: '200' }), false);
    assert.equal(h.eventFailed({ status: '204' }), false);
    assert.equal(h.eventFailed({ status: '301' }), false);
    assert.equal(h.eventFailed({ status: '404' }), true);
    assert.equal(h.eventFailed({ status: '500' }), true);
    assert.equal(h.eventFailed({ status: 'NOT_FOUND' }), true, 'gRPC status names are failures too');
    assert.equal(h.eventFailed({ status: '' }), false);
});

// ---- click-time recording resolution ----

test('currentCorrelationRecording follows the selection, not the render-time argument', () => {
    const h = load({
        recordingsList: [{ id: 'r1' }, { id: 'r2' }],
        selectedId: 'r2'
    });
    assert.equal(h.currentCorrelationRecording().id, 'r2');
});
