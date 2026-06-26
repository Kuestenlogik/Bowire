// #233 — perf-diff.js unit tests.
//
// Targets the pure helpers — captureResponseForDiff ring buffer,
// computeLineDiff LCS, prettyJson normalisation, formatMs scale.

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, resolve } from 'node:path';

const __dirname = dirname(fileURLToPath(import.meta.url));
const SRC = readFileSync(
    resolve(__dirname, '../../../src/Kuestenlogik.Bowire/wwwroot/js/perf-diff.js'),
    'utf8'
);

function loadPerfDiff(state) {
    state = state || {};
    const prelude = `
        var responseSnapshots = state.snapshots || {};
        var MAX_RESPONSE_SNAPSHOTS = state.cap || 50;
        var selectedService = state.selectedService || null;
        var selectedMethod = state.selectedMethod || null;
        var benchmark = state.benchmark || {};
        function el() { return { appendChild: function () {}, className: '' }; }
        function render() {}
        function stopBenchmark() {}
        function runBenchmark() {}
        var connectionStatuses = {};
    `;
    const postlude = `
        return {
            captureResponseForDiff: captureResponseForDiff,
            getResponseSnapshots: getResponseSnapshots,
            computeLineDiff: computeLineDiff,
            prettyJson: prettyJson,
            formatMs: formatMs,
            _setSelected: function (svc, mth) { selectedService = svc; selectedMethod = mth; },
            _getSnapshots: function () { return responseSnapshots; },
            _cap: function () { return MAX_RESPONSE_SNAPSHOTS; }
        };
    `;
    const body = prelude + '\n' + SRC + '\n' + postlude;
    const fn = new Function('state', body);
    return fn(state);
}

// ---- captureResponseForDiff + getResponseSnapshots ring buffer ----

test('captureResponseForDiff: ignores calls with missing args', () => {
    const sb = loadPerfDiff();
    sb.captureResponseForDiff(null, 'm', 'body');
    sb.captureResponseForDiff('s', null, 'body');
    sb.captureResponseForDiff('s', 'm', null);
    assert.deepEqual(sb._getSnapshots(), {});
});

test('captureResponseForDiff: keyed by service::method', () => {
    const sb = loadPerfDiff();
    sb.captureResponseForDiff('Svc', 'Mth', '{"x":1}', 'OK', 12);
    const snaps = sb._getSnapshots();
    assert.equal(snaps['Svc::Mth'].length, 1);
    assert.equal(snaps['Svc::Mth'][0].body, '{"x":1}');
    assert.equal(snaps['Svc::Mth'][0].status, 'OK');
    assert.equal(snaps['Svc::Mth'][0].durationMs, 12);
});

test('captureResponseForDiff: ring evicts FIFO at cap', () => {
    const sb = loadPerfDiff({ cap: 3 });
    sb.captureResponseForDiff('S', 'M', 'a');
    sb.captureResponseForDiff('S', 'M', 'b');
    sb.captureResponseForDiff('S', 'M', 'c');
    sb.captureResponseForDiff('S', 'M', 'd');
    const list = sb._getSnapshots()['S::M'];
    assert.equal(list.length, 3);
    assert.deepEqual(list.map(x => x.body), ['b', 'c', 'd']);
});

test('captureResponseForDiff: missing status defaults to OK + missing duration to 0', () => {
    const sb = loadPerfDiff();
    sb.captureResponseForDiff('S', 'M', 'b');
    const e = sb._getSnapshots()['S::M'][0];
    assert.equal(e.status, 'OK');
    assert.equal(e.durationMs, 0);
});

test('getResponseSnapshots: returns [] when nothing selected', () => {
    const sb = loadPerfDiff();
    assert.deepEqual(sb.getResponseSnapshots(), []);
});

test('getResponseSnapshots: returns the slice for the active method', () => {
    const sb = loadPerfDiff();
    sb._setSelected({ name: 'A' }, { name: 'a' });
    sb.captureResponseForDiff('A', 'a', '{}');
    sb.captureResponseForDiff('B', 'b', '{}');
    const got = sb.getResponseSnapshots();
    assert.equal(got.length, 1);
});

test('getResponseSnapshots: empty slice when active method has no entries yet', () => {
    const sb = loadPerfDiff();
    sb._setSelected({ name: 'A' }, { name: 'a' });
    assert.deepEqual(sb.getResponseSnapshots(), []);
});

// ---- computeLineDiff (LCS) ----

test('computeLineDiff: identical inputs → all eq', () => {
    const sb = loadPerfDiff();
    const diff = sb.computeLineDiff('a\nb\nc', 'a\nb\nc');
    assert.deepEqual(diff.map(d => d.type), ['eq', 'eq', 'eq']);
});

test('computeLineDiff: pure insertion → all add', () => {
    const sb = loadPerfDiff();
    const diff = sb.computeLineDiff('', 'a\nb');
    const adds = diff.filter(d => d.type === 'add');
    // 'a\nb' splits to ['a','b'] but '' splits to [''], so there's
    // an initial 'eq' followed by adds.
    assert.ok(adds.length >= 2);
    assert.deepEqual(adds.map(a => a.text), ['a', 'b']);
});

test('computeLineDiff: pure deletion → all del', () => {
    const sb = loadPerfDiff();
    const diff = sb.computeLineDiff('a\nb', '');
    const dels = diff.filter(d => d.type === 'del');
    assert.deepEqual(dels.map(d => d.text), ['a', 'b']);
});

test('computeLineDiff: middle replacement', () => {
    const sb = loadPerfDiff();
    const diff = sb.computeLineDiff('a\nb\nc', 'a\nX\nc');
    // The LCS should preserve a + c as eq; b → X as del + add.
    const types = diff.map(d => d.type);
    assert.deepEqual(types[0], 'eq');
    assert.deepEqual(types[types.length - 1], 'eq');
    assert.ok(types.includes('del'));
    assert.ok(types.includes('add'));
});

test('computeLineDiff: handles null / undefined safely', () => {
    const sb = loadPerfDiff();
    const diff = sb.computeLineDiff(null, undefined);
    assert.ok(Array.isArray(diff));
});

// ---- prettyJson ----

test('prettyJson: valid JSON gets 2-space indent', () => {
    const sb = loadPerfDiff();
    assert.equal(sb.prettyJson('{"a":1,"b":[2,3]}'),
        '{\n  "a": 1,\n  "b": [\n    2,\n    3\n  ]\n}');
});

test('prettyJson: invalid JSON returns input unchanged', () => {
    const sb = loadPerfDiff();
    assert.equal(sb.prettyJson('not json {'), 'not json {');
    assert.equal(sb.prettyJson(''), '');
});

test('prettyJson: pretty-print normalises whitespace differences', () => {
    const sb = loadPerfDiff();
    const a = sb.prettyJson('{"a":1, "b":2}');
    const b = sb.prettyJson('{ "a"  : 1, "b" :2 }');
    assert.equal(a, b);
});

// ---- formatMs ----

test('formatMs: <10 ms → 2 decimals', () => {
    const sb = loadPerfDiff();
    assert.equal(sb.formatMs(1.234), '1.23 ms');
    assert.equal(sb.formatMs(0), '0.00 ms');
    assert.equal(sb.formatMs(9.99), '9.99 ms');
});

test('formatMs: 10..100 ms → 1 decimal', () => {
    const sb = loadPerfDiff();
    assert.equal(sb.formatMs(10), '10.0 ms');
    assert.equal(sb.formatMs(99.9), '99.9 ms');
});

test('formatMs: 100..1000 ms → integer ms', () => {
    const sb = loadPerfDiff();
    assert.equal(sb.formatMs(100), '100 ms');
    assert.equal(sb.formatMs(999), '999 ms');
});

test('formatMs: >=1000 ms → seconds with 2 decimals', () => {
    const sb = loadPerfDiff();
    assert.equal(sb.formatMs(1000), '1.00 s');
    assert.equal(sb.formatMs(2500), '2.50 s');
});
