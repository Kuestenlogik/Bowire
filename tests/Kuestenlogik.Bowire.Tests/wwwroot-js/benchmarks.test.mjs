// #231 / #233 / #234 — benchmarks.js unit tests.
//
// The fragment runs inside prologue.js's IIFE in production; many
// helpers reference free identifiers (services, collectionsList,
// recordingsList, render, markSaved, performance, fetch, …) but
// they're only touched when the network / UI surfaces are entered.
// The pure-data helpers — envelope migration, percentile, diff,
// CSV / k6 / OTLP exporters — never reach those identifiers, so a
// minimal stub bag at the wrapper scope is enough.

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, resolve } from 'node:path';

const __dirname = dirname(fileURLToPath(import.meta.url));
const SRC = readFileSync(
    resolve(__dirname, '../../../src/Kuestenlogik.Bowire/wwwroot/js/benchmarks.js'),
    'utf8'
);

function loadBenchmarks() {
    const prelude = `
        // Module-shared host state — only the names the fragment touches
        // at parse time + the pure-data helpers' call paths.
        var benchmarksList = null;
        var benchmarksSelectedId = null;
        var benchmarkDiffBannerExpanded = {};
        var railMode = 'design';
        var services = [];
        var collectionsList = [];
        var recordingsList = [];
        // In-memory localStorage shim so loadBenchmarks() doesn't blow up.
        var _ls = {};
        var localStorage = {
            getItem: function (k) { return Object.prototype.hasOwnProperty.call(_ls, k) ? _ls[k] : null; },
            setItem: function (k, v) { _ls[k] = String(v); },
            removeItem: function (k) { delete _ls[k]; }
        };
        function wsKey(k) { return 'ws::' + k; }
        function markSaved() {}
        function render() {}
        function toast() {}
        function el() { return { appendChild: function () {} }; }
        // Used by per-call invoke path (NOT touched by these tests).
        function substituteVars(s) { return s; }
        function substituteMetadata(m) { return m; }
        function serverUrlParamForService() { return ''; }
        function _downloadEnvelopeArtifact() {}
        function _pickEnvelopeJsonFile() {}
        function getBenchmarkSpec(id) {
            return (benchmarksList || []).find(function (s) { return s.id === id; }) || null;
        }
        function importEnvelopeFromArtillery() { return null; }
        function importEnvelopeFromPostman() { return null; }
        var config = { prefix: '' };
    `;
    const postlude = `
        return {
            defaultEnvelopePhase: defaultEnvelopePhase,
            targetFromLegacySeed: targetFromLegacySeed,
            migrateEnvelope: migrateEnvelope,
            syncEnvelopeLegacyFields: syncEnvelopeLegacyFields,
            _percentileSorted: _percentileSorted,
            _envelopeSidebarMeta: _envelopeSidebarMeta,
            _diffBenchmarkRuns: _diffBenchmarkRuns,
            _diffGlyph: _diffGlyph,
            _diffSign: _diffSign,
            _formatMs: _formatMs,
            _formatPct: _formatPct,
            _csvCell: _csvCell,
            _csvRow: _csvRow,
            _collectStatusKeys: _collectStatusKeys,
            exportRunAsCsv: exportRunAsCsv,
            exportRunAsK6Summary: exportRunAsK6Summary,
            exportRunAsOtlpJson: exportRunAsOtlpJson,
            _otlpBucketCounts: _otlpBucketCounts,
            _otlpHistogramBuckets: _otlpHistogramBuckets,
            _otlpHistogramPoint: _otlpHistogramPoint,
            _otlpAttr: _otlpAttr,
            createBenchmarkSpec: createBenchmarkSpec,
            _setBenchmarksList: function (l) { benchmarksList = l; }
        };
    `;
    const body = prelude + '\n' + SRC + '\n' + postlude;
    const fn = new Function(body);
    return fn();
}

// ---- defaultEnvelopePhase ----

test('defaultEnvelopePhase: empty seed gives sensible defaults', () => {
    const sb = loadBenchmarks();
    const p = sb.defaultEnvelopePhase();
    assert.equal(p.vus, 4);
    assert.equal(p.totalIterations, 100);
    assert.equal(p.rampToVus, null);
    assert.equal(p.arrivalRate, null);
    assert.equal(p.durationMs, null);
});

test('defaultEnvelopePhase: seed.n + seed.concurrency wire through to legacy mirror', () => {
    const sb = loadBenchmarks();
    const p = sb.defaultEnvelopePhase({ n: 250, concurrency: 8 });
    assert.equal(p.vus, 8);
    assert.equal(p.totalIterations, 250);
});

test('defaultEnvelopePhase: floors totalIterations at 1 for negative seed', () => {
    const sb = loadBenchmarks();
    // vus falls back to 4 when seed.vus is 0 (parseInt returns 0 → falsy).
    // totalIterations is gated on `!== undefined` so an explicit -3 hits
    // Math.max(1, …) and lands at 1.
    const p = sb.defaultEnvelopePhase({ vus: 0, totalIterations: -3 });
    assert.equal(p.vus, 4);          // 0 → fallback chain → default 4
    assert.equal(p.totalIterations, 1);
});

// ---- targetFromLegacySeed ----

test('targetFromLegacySeed: collection seed → collection-ref target', () => {
    const sb = loadBenchmarks();
    const t = sb.targetFromLegacySeed({ kind: 'collection', sourceId: 'col_123' });
    assert.equal(t.type, 'collection-ref');
    assert.equal(t.collectionId, 'col_123');
    assert.equal(t.itemIndex, null);
});

test('targetFromLegacySeed: recording seed → recording-ref target', () => {
    const sb = loadBenchmarks();
    const t = sb.targetFromLegacySeed({ kind: 'recording', sourceId: 'rec_42' });
    assert.equal(t.type, 'recording-ref');
    assert.equal(t.recordingId, 'rec_42');
});

test('targetFromLegacySeed: default → method-shape target', () => {
    const sb = loadBenchmarks();
    const t = sb.targetFromLegacySeed({ service: 'PetService', method: 'getPet' });
    assert.equal(t.type, 'method');
    assert.equal(t.service, 'PetService');
    assert.equal(t.method, 'getPet');
});

// ---- migrateEnvelope ----

test('migrateEnvelope: legacy {kind,service,method,n,concurrency} → v2 envelope', () => {
    const sb = loadBenchmarks();
    const legacy = { id: 'b1', name: 'old', service: 'S', method: 'M', n: 100, concurrency: 4 };
    const spec = sb.migrateEnvelope(legacy);
    assert.ok(Array.isArray(spec.targets));
    assert.ok(Array.isArray(spec.phases));
    assert.equal(spec.targets[0].type, 'method');
    assert.equal(spec.targets[0].service, 'S');
    assert.equal(spec.phases[0].vus, 4);
    assert.equal(spec.phases[0].totalIterations, 100);
});

test('migrateEnvelope: already-v2 envelope is idempotent', () => {
    const sb = loadBenchmarks();
    const v2 = {
        id: 'b1',
        targets: [{ type: 'method', service: 'S', method: 'M' }],
        phases: [{ vus: 1, totalIterations: 1 }],
        mode: 'sequential'
    };
    const before = JSON.stringify(v2);
    sb.migrateEnvelope(v2);
    const after = JSON.stringify(v2);
    // runs[] gets added (forward-compat), but other fields untouched.
    assert.ok(Array.isArray(v2.runs));
    assert.equal(v2.targets.length, 1);
    assert.equal(v2.phases.length, 1);
});

test('migrateEnvelope: lastRun bring-forward into runs[]', () => {
    const sb = loadBenchmarks();
    const v2 = {
        id: 'b1',
        targets: [{ type: 'method', service: 'S', method: 'M' }],
        phases: [{ vus: 1, totalIterations: 1 }],
        mode: 'sequential',
        lastRun: { ranAt: 1, total: 5 }
    };
    sb.migrateEnvelope(v2);
    assert.equal(v2.runs.length, 1);
    assert.equal(v2.runs[0].total, 5);
});

// ---- _percentileSorted ----

test('_percentileSorted: empty array → 0', () => {
    const sb = loadBenchmarks();
    assert.equal(sb._percentileSorted([], 50), 0);
    assert.equal(sb._percentileSorted(null, 95), 0);
});

test('_percentileSorted: single value array → that value', () => {
    const sb = loadBenchmarks();
    assert.equal(sb._percentileSorted([42], 50), 42);
    assert.equal(sb._percentileSorted([42], 99), 42);
});

test('_percentileSorted: nearest-rank semantics', () => {
    const sb = loadBenchmarks();
    const xs = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10];
    assert.equal(sb._percentileSorted(xs, 50), 5);
    assert.equal(sb._percentileSorted(xs, 90), 9);
    assert.equal(sb._percentileSorted(xs, 95), 10);
    assert.equal(sb._percentileSorted(xs, 99), 10);
    assert.equal(sb._percentileSorted(xs, 100), 10);
});

// ---- _envelopeSidebarMeta (#231) ----

test('_envelopeSidebarMeta: no phases → em-dash', () => {
    const sb = loadBenchmarks();
    assert.equal(sb._envelopeSidebarMeta({ phases: [] }), '—');
    assert.equal(sb._envelopeSidebarMeta({}), '—');
});

test('_envelopeSidebarMeta: single iteration-bounded phase → "100×"', () => {
    const sb = loadBenchmarks();
    const meta = sb._envelopeSidebarMeta({ phases: [{ totalIterations: 100 }] });
    assert.equal(meta, '100×');
});

test('_envelopeSidebarMeta: multi-phase iteration-bounded sums', () => {
    const sb = loadBenchmarks();
    const meta = sb._envelopeSidebarMeta({
        phases: [{ totalIterations: 100 }, { totalIterations: 250 }]
    });
    assert.equal(meta, '350×');
});

test('_envelopeSidebarMeta: any time-bounded phase forces phase-count', () => {
    const sb = loadBenchmarks();
    const meta = sb._envelopeSidebarMeta({
        phases: [{ durationMs: 10000 }, { totalIterations: 50 }]
    });
    assert.equal(meta, '2 phases');
});

// ---- _diffBenchmarkRuns (#233) ----

test('_diffBenchmarkRuns: null when either side missing', () => {
    const sb = loadBenchmarks();
    assert.equal(sb._diffBenchmarkRuns(null, {}), null);
    assert.equal(sb._diffBenchmarkRuns({ stats: {} }, null), null);
    assert.equal(sb._diffBenchmarkRuns({ stats: {} }, { stats: null }), null);
});

test('_diffBenchmarkRuns: flat when delta below threshold', () => {
    const sb = loadBenchmarks();
    const curr = {
        stats: { p50: 100, p95: 102, p99: 105, avg: 100, throughput: 500 },
        statusCounts: { '200': 100 }, failure: 0
    };
    const prev = {
        stats: { p50: 100, p95: 100, p99: 100, avg: 100, throughput: 500 },
        statusCounts: { '200': 100 }, failure: 0
    };
    const d = sb._diffBenchmarkRuns(curr, prev, 5);
    assert.equal(d.p95.dir, 'flat'); // 2% delta < 5% threshold
    assert.equal(d.throughput.dir, 'flat');
});

test('_diffBenchmarkRuns: p95 regression flagged as up + pct +50', () => {
    const sb = loadBenchmarks();
    const curr = {
        stats: { p50: 100, p95: 150, p99: 150, avg: 110, throughput: 500 },
        statusCounts: { '200': 100 }, failure: 0
    };
    const prev = {
        stats: { p50: 100, p95: 100, p99: 100, avg: 100, throughput: 500 },
        statusCounts: { '200': 100 }, failure: 0
    };
    const d = sb._diffBenchmarkRuns(curr, prev, 5);
    assert.equal(d.p95.dir, 'up');
    assert.equal(Math.round(d.p95.pct), 50);
    assert.equal(d.p95.delta, 50);
});

test('_diffBenchmarkRuns: throughput drop flagged as down + negative pct', () => {
    const sb = loadBenchmarks();
    const curr = {
        stats: { p50: 100, p95: 100, p99: 100, avg: 100, throughput: 250 },
        statusCounts: { '200': 100 }, failure: 0
    };
    const prev = {
        stats: { p50: 100, p95: 100, p99: 100, avg: 100, throughput: 500 },
        statusCounts: { '200': 100 }, failure: 0
    };
    const d = sb._diffBenchmarkRuns(curr, prev, 5);
    assert.equal(d.throughput.dir, 'down');
    assert.equal(Math.round(d.throughput.pct), -50);
});

test('_diffBenchmarkRuns: status bucket deltas split by 2xx/4xx/5xx', () => {
    const sb = loadBenchmarks();
    const curr = {
        stats: { p50: 10, p95: 10, p99: 10, avg: 10, throughput: 100 },
        statusCounts: { '200': 90, '404': 5, '500': 5 }, failure: 10
    };
    const prev = {
        stats: { p50: 10, p95: 10, p99: 10, avg: 10, throughput: 100 },
        statusCounts: { '200': 100, '404': 0, '500': 0 }, failure: 0
    };
    const d = sb._diffBenchmarkRuns(curr, prev, 5);
    assert.equal(d.statusBuckets['2xx'], -10);
    assert.equal(d.statusBuckets['4xx'], 5);
    assert.equal(d.statusBuckets['5xx'], 5);
    assert.equal(d.errors.delta, 10);
});

test('_diffBenchmarkRuns: prev zero → pct=null + delta=raw', () => {
    const sb = loadBenchmarks();
    const curr = {
        stats: { p50: 100, p95: 100, p99: 100, avg: 100, throughput: 500 },
        statusCounts: {}, failure: 0
    };
    const prev = {
        stats: { p50: 0, p95: 0, p99: 0, avg: 0, throughput: 0 },
        statusCounts: {}, failure: 0
    };
    const d = sb._diffBenchmarkRuns(curr, prev, 5);
    assert.equal(d.p50.pct, null);
    assert.equal(d.p50.delta, 100);
    assert.equal(d.p50.dir, 'flat');
});

// ---- _diffGlyph + _diffSign + _formatMs + _formatPct ----

test('_diffGlyph: up/down/flat', () => {
    const sb = loadBenchmarks();
    assert.equal(sb._diffGlyph('up'), '▲');
    assert.equal(sb._diffGlyph('down'), '▼');
    assert.equal(sb._diffGlyph('flat'), '·');
    assert.equal(sb._diffGlyph('?'), '·');
});

test('_diffSign: positive prefixed with +; negative + zero left blank', () => {
    const sb = loadBenchmarks();
    assert.equal(sb._diffSign(5), '+');
    assert.equal(sb._diffSign(-5), '');  // negative already prints its sign
    assert.equal(sb._diffSign(0), '');
    assert.equal(sb._diffSign(null), '');
});

test('_formatMs: 1 decimal place + em-dash on missing', () => {
    const sb = loadBenchmarks();
    assert.equal(sb._formatMs(0.05), '0.1 ms');
    assert.equal(sb._formatMs(123.45), '123.5 ms');
    assert.equal(sb._formatMs(null), '—');
    assert.equal(sb._formatMs(NaN), '—');
});

test('_formatPct: positive sign prefix + 1 decimal + empty on missing', () => {
    const sb = loadBenchmarks();
    assert.equal(sb._formatPct(5.25), '+5.3%');
    assert.equal(sb._formatPct(-3.4), '-3.4%');
    assert.equal(sb._formatPct(0), '0%');     // strictly > 0 gets the +
    assert.equal(sb._formatPct(null), '');
    assert.equal(sb._formatPct(NaN), '');
});

// ---- CSV exporters (#234) ----

test('_csvCell: quotes cells containing ; " \\n \\r', () => {
    const sb = loadBenchmarks();
    assert.equal(sb._csvCell('plain'), 'plain');
    assert.equal(sb._csvCell('a;b'), '"a;b"');
    assert.equal(sb._csvCell('a"b'), '"a""b"');
    assert.equal(sb._csvCell('a\nb'), '"a\nb"');
    assert.equal(sb._csvCell(null), '');
    assert.equal(sb._csvCell(undefined), '');
    assert.equal(sb._csvCell(42), '42');
});

test('_csvRow: joins with ;', () => {
    const sb = loadBenchmarks();
    assert.equal(sb._csvRow(['a', 'b', 'c']), 'a;b;c');
    assert.equal(sb._csvRow(['a', 'b;c']), 'a;"b;c"');
});

test('_collectStatusKeys: union of overall + per-target, sorted', () => {
    const sb = loadBenchmarks();
    const keys = sb._collectStatusKeys({
        statusCounts: { '200': 10, '500': 1 },
        targetStats: { t1: { statusCounts: { '404': 2 } }, t2: { statusCounts: { '200': 3 } } }
    });
    assert.deepEqual(keys, ['200', '404', '500']);
});

test('exportRunAsCsv: returns empty string when no lastRun', () => {
    const sb = loadBenchmarks();
    assert.equal(sb.exportRunAsCsv({ id: 'b1' }), '');
    assert.equal(sb.exportRunAsCsv(null), '');
});

test('exportRunAsCsv: per-method default — headers + envelope synthesised row', () => {
    const sb = loadBenchmarks();
    const spec = {
        name: 'env1',
        lastRun: {
            total: 50, failure: 2,
            stats: { p50: 10, p95: 20, p99: 30, min: 5, max: 35, avg: 12, throughput: 100, totalSeconds: 0.5 },
            statusCounts: { '200': 48, '500': 2 },
            targetStats: {}  // empty → synthesised single row
        }
    };
    const csv = sb.exportRunAsCsv(spec, 'per-method');
    const lines = csv.trim().split('\n');
    assert.equal(lines[0].includes('method;count;errors;p50_ms'), true);
    assert.equal(lines[0].includes('status_200'), true);
    assert.equal(lines[0].includes('status_500'), true);
    assert.equal(lines.length, 2); // header + 1 synthesised row
    assert.ok(lines[1].startsWith('(envelope);50;2'));
});

test('exportRunAsCsv: per-iteration mode dumps each call', () => {
    const sb = loadBenchmarks();
    const spec = {
        name: 'env1',
        lastRun: {
            iterations: [
                { i: 0, ranAt: 0, target: 't', targetKey: 'k', status: '200', pass: true, durationMs: 10 },
                { i: 1, ranAt: 1, target: 't', targetKey: 'k', status: '500', pass: false, durationMs: 15 }
            ]
        }
    };
    const csv = sb.exportRunAsCsv(spec, 'per-iteration');
    const lines = csv.trim().split('\n');
    assert.equal(lines.length, 3); // header + 2 iterations
    assert.equal(lines[0].includes('i;ranAt;target;targetKey;status;pass;durationMs'), true);
    // ranAt is ISO-formatted
    assert.ok(/T/.test(lines[1]));
});

// ---- k6 summary export ----

test('exportRunAsK6Summary: returns null with no lastRun', () => {
    const sb = loadBenchmarks();
    assert.equal(sb.exportRunAsK6Summary({ id: 'b1' }), null);
});

test('exportRunAsK6Summary: emits required k6 sections', () => {
    const sb = loadBenchmarks();
    const spec = {
        id: 'b1', name: 'env1', concurrency: 4,
        phases: [{ vus: 4, durationMs: 1000 }],
        lastRun: {
            total: 100, failure: 5, success: 95,
            stats: { avg: 10, min: 5, max: 30, p50: 9, p90: 15, p95: 20, p99: 25, throughput: 100, totalSeconds: 1 },
            durations: [5, 10, 15, 20],
            statusCounts: { '200': 95, '500': 5 },
            targetStats: { t1: { key: 't1', label: 'T1', count: 100, errors: 5, durations: [5, 10, 15, 20], statusCounts: { '200': 95, '500': 5 } } }
        }
    };
    const k6 = sb.exportRunAsK6Summary(spec);
    assert.ok(k6.metrics);
    assert.ok(k6.metrics.http_req_duration);
    assert.equal(k6.metrics.http_req_duration.type, 'trend');
    assert.equal(k6.metrics.http_req_duration.values['p(95)'], 20);
    assert.equal(k6.metrics.http_reqs.values.count, 100);
    assert.equal(k6.metrics.http_req_failed.values.passes, 5);  // failed:passes carries failCount
    assert.equal(k6.metrics.checks.values.rate, 0.95);
    assert.equal(k6.metrics.vus.values.value, 4);
    assert.ok(Array.isArray(k6.root_group.checks));
    assert.equal(k6.bowire.envelope, 'env1');
    assert.equal(k6.bowire.targets.length, 1);
});

// ---- OTLP export ----

test('_otlpHistogramBuckets: returns 13 boundaries (1..10000ms)', () => {
    const sb = loadBenchmarks();
    const b = sb._otlpHistogramBuckets();
    assert.equal(b.length, 13);
    assert.equal(b[0], 1);
    assert.equal(b[b.length - 1], 10000);
});

test('_otlpBucketCounts: every value lands in exactly one bucket', () => {
    const sb = loadBenchmarks();
    const bounds = sb._otlpHistogramBuckets();
    const counts = sb._otlpBucketCounts([0.5, 1, 1.5, 2, 7, 50, 500, 50000], bounds);
    assert.equal(counts.length, bounds.length + 1); // +overflow bucket
    const total = counts.reduce((a, b) => a + b, 0);
    assert.equal(total, 8); // all values placed
    // 50000 lands in the overflow bucket (last index)
    assert.equal(counts[counts.length - 1], 1);
});

test('_otlpAttr: string / number / boolean shapes', () => {
    const sb = loadBenchmarks();
    assert.deepEqual(sb._otlpAttr('a', 'x'), { key: 'a', value: { stringValue: 'x' } });
    assert.deepEqual(sb._otlpAttr('b', 12), { key: 'b', value: { intValue: '12' } });
    assert.deepEqual(sb._otlpAttr('c', true), { key: 'c', value: { boolValue: true } });
    assert.deepEqual(sb._otlpAttr('d', null), { key: 'd', value: { stringValue: '' } });
});

test('exportRunAsOtlpJson: returns null with no lastRun', () => {
    const sb = loadBenchmarks();
    assert.equal(sb.exportRunAsOtlpJson({ id: 'b1' }), null);
});

test('exportRunAsOtlpJson: emits resourceMetrics + scopeMetrics + three metrics', () => {
    const sb = loadBenchmarks();
    const spec = {
        id: 'b1', name: 'env1',
        lastRun: {
            total: 10, failure: 1, success: 9,
            stats: { avg: 12, min: 5, max: 30, p50: 10, p95: 20, p99: 25, throughput: 5, totalSeconds: 2 },
            durations: [5, 10, 15],
            ranAtWallClock: 1700000000000,
            targetStats: { t1: { key: 't1', label: 'T1', count: 10, errors: 1, durations: [5, 10, 15], statusCounts: { '200': 9, '500': 1 } } }
        }
    };
    const otlp = sb.exportRunAsOtlpJson(spec);
    assert.ok(Array.isArray(otlp.resourceMetrics));
    assert.equal(otlp.resourceMetrics.length, 1);
    const scope = otlp.resourceMetrics[0].scopeMetrics[0];
    assert.equal(scope.scope.name, 'Kuestenlogik.Bowire.Benchmarks');
    assert.equal(scope.metrics.length, 3);
    const histo = scope.metrics.find(m => m.histogram);
    assert.ok(histo);
    assert.ok(histo.histogram.dataPoints.length >= 2); // per-target + envelope rollup
});
