// #48 — schema-watch diff unit tests.
//
// Targets the pure helpers behind "+ added, − removed, ~ changed":
//   * schemaMethodSignature  — what counts as a shape change
//   * schemaSnapshot         — service/method map keyed for comparison
//   * schemaDiff             — the set diff itself (null when nothing moved)
//   * schemaDeltaSummary     — the one-line sidebar wording
//   * schemaWatchSeconds     — reads the Settings interval, clamped
//
// The rename case is the reason this file exists. The previous
// implementation subtracted counts, so renaming a method produced
// `methodDelta === 0` and reported nothing — the single change most
// likely to break a saved request was the one change it could not see.
//
// Same wrap trick as coverage.test.mjs: the fragment is concatenated
// into one shared IIFE in production, so the wrapper declares the
// host-provided names the fragment closes over.

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, resolve } from 'node:path';

const __dirname = dirname(fileURLToPath(import.meta.url));
const SRC = readFileSync(
    resolve(__dirname, '../../../src/Kuestenlogik.Bowire/wwwroot/js/api.js'),
    'utf8'
);

function loadWatch() {
    const prelude = `
        var _ls = {};
        var localStorage = {
            getItem: function (k) { return Object.prototype.hasOwnProperty.call(_ls, k) ? _ls[k] : null; },
            setItem: function (k, v) { _ls[k] = String(v); },
            removeItem: function (k) { delete _ls[k]; }
        };
        // Host globals api.js closes over. The diff helpers touch none of
        // them; they exist so the fragment parses and its top-level
        // declarations run.
        var services = [];
        var protocols = [];
        var serverUrls = [];
        var connectionStatuses = {};
        var discoveryErrors = {};
        var discoveryAttempts = {};
        var discoveryHints = {};
        var discoveryDiagnosticsOpen = new Set();
        var expandedServices = new Set();
        var isLoadingServices = false;
        var selectedMethod = null;
        var config = { prefix: '' };
        var window = (typeof globalThis.window !== 'undefined') ? globalThis.window : {};
        var document = { getElementById: function () { return null; }, querySelector: function () { return null; } };
        function render() {}
        function toast() {}
        function addConsoleEntry() {}
        function el() { return null; }
        function persistExpandedServices() {}
        function getFilteredServices() { return []; }
        function serverUrlParam() { return ''; }
        function urlMatchesService() { return false; }
        function svgIcon() { return ''; }
        function fetch() { return Promise.reject(new Error('no network in unit tests')); }
    `;
    const postlude = `
        return {
            schemaMethodSignature: schemaMethodSignature,
            schemaSnapshot: schemaSnapshot,
            schemaDiff: schemaDiff,
            schemaDeltaSummary: schemaDeltaSummary,
            schemaWatchSeconds: schemaWatchSeconds,
            stopSchemaWatch: stopSchemaWatch,
            startSchemaWatch: startSchemaWatch,
            isSchemaWatchActive: isSchemaWatchActive,
            schemaWatchPollUsable: schemaWatchPollUsable,
            schemaIndexDelta: schemaIndexDelta,
            schemaWatchMarkerFor: schemaWatchMarkerFor,
            schemaServiceDelta: schemaServiceDelta,
            _publishDelta: function (d) { schemaWatchDelta = d ? schemaIndexDelta(d) : null; },
            _setDiscoveryErrors: function (e) { discoveryErrors = e; },
            _setUrls: function (u, st) { serverUrls = u; connectionStatuses = st || {}; },
            _setInterval: function (v) { if (v === null) delete _ls['bowire_watch_interval']; else localStorage.setItem('bowire_watch_interval', v); }
        };
    `;
    return new Function(prelude + '\n' + SRC + '\n' + postlude)();
}

// Minimal service/method shapes matching what /api/services returns.
function method(name, opts) {
    opts = opts || {};
    return {
        name: name,
        fullName: opts.fullName || name,
        methodType: opts.methodType || 'Unary',
        httpMethod: opts.httpMethod || 'GET',
        httpPath: opts.httpPath || ('/' + name),
        clientStreaming: !!opts.clientStreaming,
        serverStreaming: !!opts.serverStreaming,
        deprecated: !!opts.deprecated,
        inputType: opts.inputType || { name: 'In', fields: [] },
        outputType: opts.outputType || { name: 'Out', fields: [] }
    };
}

function service(name, methods) {
    return { name: name, source: 'rest', methods: methods };
}

// ---- schemaWatchSeconds ----

test('schemaWatchSeconds: defaults to 15 when nothing is stored', () => {
    const sb = loadWatch();
    assert.equal(sb.schemaWatchSeconds(), 15);
});

test('schemaWatchSeconds: reads the Settings value', () => {
    const sb = loadWatch();
    sb._setInterval('45');
    assert.equal(sb.schemaWatchSeconds(), 45);
});

test('schemaWatchSeconds: clamps to the bounds the input advertises', () => {
    const sb = loadWatch();
    sb._setInterval('1');
    assert.equal(sb.schemaWatchSeconds(), 5, 'below the minimum');
    sb._setInterval('99999');
    assert.equal(sb.schemaWatchSeconds(), 300, 'above the maximum');
});

test('schemaWatchSeconds: junk in localStorage falls back to the default', () => {
    const sb = loadWatch();
    sb._setInterval('not-a-number');
    assert.equal(sb.schemaWatchSeconds(), 15);
});

// ---- schemaDiff ----

test('schemaDiff: an unchanged schema produces no delta at all', () => {
    const sb = loadWatch();
    const list = [service('Orders', [method('List'), method('Get')])];
    assert.equal(sb.schemaDiff(sb.schemaSnapshot(list), sb.schemaSnapshot(list)), null);
});

test('schemaDiff: a renamed method is one add and one remove, not silence', () => {
    // The regression this whole file exists for. Method count before: 2.
    // After: 2. The old count subtraction saw zero and said nothing.
    const sb = loadWatch();
    const before = sb.schemaSnapshot([service('Users', [method('GetUser'), method('ListUsers')])]);
    const after = sb.schemaSnapshot([service('Users', [method('FetchUser'), method('ListUsers')])]);

    const d = sb.schemaDiff(before, after);
    assert.ok(d, 'a rename must be reported');
    assert.equal(d.addedMethods.length, 1);
    assert.equal(d.removedMethods.length, 1);
    assert.match(d.addedMethods[0].key, /FetchUser/);
    assert.match(d.removedMethods[0].key, /GetUser/);
    assert.equal(sb.schemaDeltaSummary(d), '+1 method, −1 method');
});

test('schemaDiff: an added method is reported against its service', () => {
    const sb = loadWatch();
    const before = sb.schemaSnapshot([service('Orders', [method('List')])]);
    const after = sb.schemaSnapshot([service('Orders', [method('List'), method('Cancel')])]);

    const d = sb.schemaDiff(before, after);
    assert.equal(d.addedMethods.length, 1);
    assert.equal(d.addedMethods[0].service, 'Orders');
    assert.equal(d.removedMethods.length, 0);
    assert.equal(d.changedMethods.length, 0);
});

test('schemaDiff: a whole new service is one line, not one per method', () => {
    const sb = loadWatch();
    const before = sb.schemaSnapshot([service('Orders', [method('List')])]);
    const after = sb.schemaSnapshot([
        service('Orders', [method('List')]),
        service('Invoices', [method('List'), method('Get'), method('Void')])
    ]);

    const d = sb.schemaDiff(before, after);
    assert.deepEqual(d.addedServices, ['Invoices']);
    assert.equal(d.addedMethods.length, 0, 'a new service does not also list its methods');
    assert.equal(sb.schemaDeltaSummary(d), '+1 service');
});

test('schemaDiff: a removed service is reported and its methods are not double-counted', () => {
    const sb = loadWatch();
    const before = sb.schemaSnapshot([
        service('Orders', [method('List')]),
        service('Legacy', [method('Old')])
    ]);
    const after = sb.schemaSnapshot([service('Orders', [method('List')])]);

    const d = sb.schemaDiff(before, after);
    assert.deepEqual(d.removedServices, ['Legacy']);
    assert.equal(d.removedMethods.length, 0);
});

// ---- ~ changed ----

test('schemaDiff: a new request field changes the signature under a stable name', () => {
    const sb = loadWatch();
    const before = sb.schemaSnapshot([service('Berths', [
        method('Get', { inputType: { name: 'In', fields: [{ name: 'id', type: 'string', source: 'path', required: true }] } })
    ])]);
    const after = sb.schemaSnapshot([service('Berths', [
        method('Get', { inputType: { name: 'In', fields: [
            { name: 'id', type: 'string', source: 'path', required: true },
            { name: 'includeVessel', type: 'boolean', source: 'query' }
        ] } })
    ])]);

    const d = sb.schemaDiff(before, after);
    assert.equal(d.changedMethods.length, 1);
    assert.equal(d.addedMethods.length, 0, 'the name did not move, so this is not an add');
    assert.equal(sb.schemaDeltaSummary(d), '~1 changed');
});

test('schemaDiff: a field turning required is a change', () => {
    const sb = loadWatch();
    const opt = { inputType: { name: 'In', fields: [{ name: 'q', type: 'string', source: 'query' }] } };
    const req = { inputType: { name: 'In', fields: [{ name: 'q', type: 'string', source: 'query', required: true }] } };
    const d = sb.schemaDiff(
        sb.schemaSnapshot([service('S', [method('Find', opt)])]),
        sb.schemaSnapshot([service('S', [method('Find', req)])]));
    assert.equal(d.changedMethods.length, 1);
});

test('schemaDiff: an HTTP verb or path move is a change', () => {
    const sb = loadWatch();
    const d = sb.schemaDiff(
        sb.schemaSnapshot([service('S', [method('Save', { httpMethod: 'POST', httpPath: '/save' })])]),
        sb.schemaSnapshot([service('S', [method('Save', { httpMethod: 'PUT', httpPath: '/save' })])]));
    assert.equal(d.changedMethods.length, 1);
});

test('schemaDiff: a description-only edit is NOT a change', () => {
    // Prose moves constantly in a schema under development. Reporting it
    // would train the operator to ignore the marker.
    const sb = loadWatch();
    const a = method('Get');
    const b = method('Get');
    b.summary = 'A much better summary';
    b.description = 'Now with prose.';
    assert.equal(sb.schemaMethodSignature(a), sb.schemaMethodSignature(b));
});

test('schemaDiff: streaming direction is part of the signature', () => {
    const sb = loadWatch();
    const d = sb.schemaDiff(
        sb.schemaSnapshot([service('S', [method('Feed')])]),
        sb.schemaSnapshot([service('S', [method('Feed', { serverStreaming: true })])]));
    assert.equal(d.changedMethods.length, 1);
});

test('schemaMethodSignature: a self-referencing type terminates', () => {
    // A tree node whose children are the same type. An unbounded walk
    // would never return; the signature is depth-bounded on purpose.
    const sb = loadWatch();
    const node = { name: 'Node', fields: [] };
    node.fields.push({ name: 'child', type: 'Node', messageType: node });
    const sig = sb.schemaMethodSignature(method('Walk', { inputType: node }));
    assert.equal(typeof sig, 'string');
    assert.ok(sig.length > 0);
});

// ---- summary wording ----

test('schemaDeltaSummary: every bucket in one line', () => {
    const sb = loadWatch();
    const before = sb.schemaSnapshot([
        service('A', [method('Keep'), method('Drop'), method('Morph')]),
        service('Gone', [method('X')])
    ]);
    const after = sb.schemaSnapshot([
        service('A', [method('Keep'), method('Morph', { httpMethod: 'POST' }), method('Fresh')]),
        service('New', [method('Y')])
    ]);

    const d = sb.schemaDiff(before, after);
    assert.equal(sb.schemaDeltaSummary(d),
        '+1 service, −1 service, +1 method, −1 method, ~1 changed');
});

// ---- failed polls must not produce a delta ----

test('schemaWatchPollUsable: a clean poll is usable', () => {
    const sb = loadWatch();
    sb._setUrls(['http://a'], { 'http://a': 'connected' });
    sb._setDiscoveryErrors({});
    assert.equal(sb.schemaWatchPollUsable(), true);
});

test('schemaWatchPollUsable: a discovery error makes the poll unusable', () => {
    // Reproduces what a live run showed: the workbench answered 502,
    // `services` emptied, and the diff reported "−2 services" as though
    // the API had been deleted. The observation was real; the conclusion
    // was not.
    const sb = loadWatch();
    sb._setUrls(['http://a'], { 'http://a': 'connected' });
    sb._setDiscoveryErrors({ 'http://a': 'HTTP 502 Bad Gateway' });
    assert.equal(sb.schemaWatchPollUsable(), false);
});

test('schemaWatchPollUsable: a URL in error state makes the poll unusable', () => {
    const sb = loadWatch();
    sb._setUrls(['http://a', 'http://b'], { 'http://a': 'connected', 'http://b': 'error' });
    sb._setDiscoveryErrors({});
    assert.equal(sb.schemaWatchPollUsable(), false);
});

test('schemaWatchPollUsable: an embedded-discovery failure counts too', () => {
    // fetchServices files the embedded probe under '(embedded)', which is
    // not in serverUrls — so the URL loop alone would miss it.
    const sb = loadWatch();
    sb._setUrls([], {});
    sb._setDiscoveryErrors({ '(embedded)': 'discovery timed out' });
    assert.equal(sb.schemaWatchPollUsable(), false);
});

// ---- the delta index (render-path cost) ----

test('schemaIndexDelta: marker lookup is a set membership test, not a scan', () => {
    const sb = loadWatch();
    const before = sb.schemaSnapshot([service('S', [method('Keep'), method('Drop'), method('Morph')])]);
    const after = sb.schemaSnapshot([service('S', [
        method('Keep'), method('Morph', { httpMethod: 'POST' }), method('Fresh')])]);
    const d = sb.schemaDiff(before, after);
    sb._publishDelta(d);

    assert.ok(d.addedKeys instanceof Set);
    assert.ok(d.changedKeys instanceof Set);
    assert.equal(sb.schemaWatchMarkerFor('S', method('Fresh')), 'added');
    assert.equal(sb.schemaWatchMarkerFor('S', method('Morph')), 'changed');
    assert.equal(sb.schemaWatchMarkerFor('S', method('Keep')), null);
});

test('schemaIndexDelta: every method of a new service is marked added', () => {
    const sb = loadWatch();
    const d = sb.schemaDiff(
        sb.schemaSnapshot([]),
        sb.schemaSnapshot([service('Fresh', [method('A'), method('B')])]));
    sb._publishDelta(d);
    assert.equal(sb.schemaWatchMarkerFor('Fresh', method('A')), 'added');
});

test('schemaServiceDelta: tallies come from the index', () => {
    const sb = loadWatch();
    const before = sb.schemaSnapshot([service('S', [method('Keep'), method('Drop'), method('Morph')])]);
    const after = sb.schemaSnapshot([service('S', [
        method('Keep'), method('Morph', { httpMethod: 'POST' }), method('Fresh')])]);
    sb._publishDelta(sb.schemaDiff(before, after));

    const t = sb.schemaServiceDelta('S');
    assert.equal(t.label, '+1 −1 ~1');
    assert.equal(sb.schemaServiceDelta('Untouched'), null);
});

test('stopSchemaWatch: drops the delta so it stops taxing later renders', () => {
    // It used to survive: the marker lookup runs once per method row on the
    // render path, so a sticky delta kept every render paying for a watch
    // that had been switched off.
    const sb = loadWatch();
    sb._publishDelta(sb.schemaDiff(
        sb.schemaSnapshot([service('S', [method('A')])]),
        sb.schemaSnapshot([service('S', [method('A'), method('B')])])));
    assert.equal(sb.schemaWatchMarkerFor('S', method('B')), 'added');

    sb.stopSchemaWatch({ quiet: true });
    assert.equal(sb.schemaWatchMarkerFor('S', method('B')), null);
    assert.equal(sb.schemaServiceDelta('S'), null);
});
