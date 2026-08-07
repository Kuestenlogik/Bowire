// #48 — schema-watch diff unit tests.
//
// Targets the pure helpers behind "+ added, − removed, ~ changed":
//   * schemaMethodRecord     — what counts as a shape change (#185: faceted)
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
import { compileFragment } from './_load-fragment.mjs';

// The fragment is compiled alone with its real filename so V8 attributes
// coverage to api.js (#367). Host stubs live in the appended block
// (hoisted). The optional wsKey shim is a per-call variant, so this loader
// compiles on demand.
const _RELPATH = '../../../src/Kuestenlogik.Bowire/wwwroot/js/api.js';

function loadWatch(opts) {
    // #185 — the per-workspace interval override rides wsKey(); the
    // production wsKey lives in prologue.js, so tests inject a
    // deterministic stand-in only when they exercise the override.
    const wsKeyDecl = (opts && opts.withWsKey)
        ? "function wsKey(k) { return 'bowire_ws_t1_' + String(k).replace(/^bowire_/, ''); }"
        : '';
    const prelude = wsKeyDecl + `
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
            schemaMethodRecord: schemaMethodRecord,
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
            _setInterval: function (v) { if (v === null) delete _ls['bowire_watch_interval']; else localStorage.setItem('bowire_watch_interval', v); },
            _setWsInterval: function (v) { if (v === null) delete _ls['bowire_ws_t1_watch_interval']; else localStorage.setItem('bowire_ws_t1_watch_interval', v); }
        };
    `;
    return compileFragment(_RELPATH, [], prelude + '\n' + postlude)();
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

test('schemaDiff: a description-only edit does not touch the callable facets', () => {
    // Prose moves constantly in a schema under development. Reporting it
    // as a shape change would train the operator to ignore the marker;
    // it lives in the separate `note` facet (annotation bucket, #185).
    const sb = loadWatch();
    const a = method('Get');
    const b = method('Get');
    b.summary = 'A much better summary';
    b.description = 'Now with prose.';
    const ra = sb.schemaMethodRecord(a);
    const rb = sb.schemaMethodRecord(b);
    assert.equal(ra.route, rb.route);
    assert.equal(ra.kind, rb.kind);
    assert.equal(ra.input, rb.input);
    assert.equal(ra.output, rb.output);
    assert.equal(ra.deprecated, rb.deprecated);
    assert.notEqual(ra.note, rb.note, 'the prose difference is visible to the change log');
});

test('schemaDiff: streaming direction is part of the signature', () => {
    const sb = loadWatch();
    const d = sb.schemaDiff(
        sb.schemaSnapshot([service('S', [method('Feed')])]),
        sb.schemaSnapshot([service('S', [method('Feed', { serverStreaming: true })])]));
    assert.equal(d.changedMethods.length, 1);
});

test('schemaDiff: reordering fields is NOT a change — required flips still are', () => {
    // #185 acceptance criterion, verbatim: the diff is AST-level, "so
    // reordering a field doesn't show as a change but moving it from
    // optional to required does." swagger-gen / protoc rebuilds
    // reorder properties constantly; alarming on that would drown the
    // real signal. Nested messages reorder too.
    const sb = loadWatch();
    const fieldA = { name: 'alpha', type: 'string', source: 'query' };
    const fieldB = { name: 'beta', type: 'int32', source: 'query', messageType: {
        name: 'Inner', fields: [
            { name: 'x', type: 'string' },
            { name: 'y', type: 'bool' }
        ] } };
    const fieldBReordered = { name: 'beta', type: 'int32', source: 'query', messageType: {
        name: 'Inner', fields: [
            { name: 'y', type: 'bool' },
            { name: 'x', type: 'string' }
        ] } };
    const before = sb.schemaSnapshot([service('S', [
        method('Find', { inputType: { name: 'In', fields: [fieldA, fieldB] } })])]);
    const reordered = sb.schemaSnapshot([service('S', [
        method('Find', { inputType: { name: 'In', fields: [fieldBReordered, fieldA] } })])]);
    assert.equal(sb.schemaDiff(before, reordered), null, 'same set, different order — silence');

    const required = sb.schemaSnapshot([service('S', [
        method('Find', { inputType: { name: 'In', fields: [
            { name: 'alpha', type: 'string', source: 'query', required: true }, fieldB] } })])]);
    const d = sb.schemaDiff(before, required);
    assert.equal(d.changedMethods.length, 1, 'optional → required must still be reported');
});

test('schemaMethodRecord: a self-referencing type terminates', () => {
    // A tree node whose children are the same type. An unbounded walk
    // would never return; the shape facets are depth-bounded on purpose.
    const sb = loadWatch();
    const node = { name: 'Node', fields: [] };
    node.fields.push({ name: 'child', type: 'Node', messageType: node });
    const rec = sb.schemaMethodRecord(method('Walk', { inputType: node }));
    assert.equal(typeof rec.input, 'string');
    assert.ok(rec.input.length > 0);
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

// ---- #185 — change classification (signature / deprecation / annotation) ----

test('schemaDiff: a shape change is a signature change with a facet detail', () => {
    const sb = loadWatch();
    const d = sb.schemaDiff(
        sb.schemaSnapshot([service('S', [method('Find', { inputType: { name: 'In', fields: [] } })])]),
        sb.schemaSnapshot([service('S', [method('Find', {
            inputType: { name: 'In', fields: [{ name: 'q', type: 'string', source: 'query' }] }
        })])]));
    assert.equal(d.changedMethods.length, 1);
    assert.equal(d.changedMethods[0].type, 'signature');
    assert.match(d.changedMethods[0].detail, /request shape changed/);
    assert.equal(d.callableMoved, true);
});

test('schemaDiff: an HTTP verb move names the old and new route', () => {
    const sb = loadWatch();
    const d = sb.schemaDiff(
        sb.schemaSnapshot([service('S', [method('Save', { httpMethod: 'POST', httpPath: '/save' })])]),
        sb.schemaSnapshot([service('S', [method('Save', { httpMethod: 'PUT', httpPath: '/save' })])]));
    assert.equal(d.changedMethods[0].type, 'signature');
    assert.match(d.changedMethods[0].detail, /route POST \/save → PUT \/save/);
});

test('schemaDiff: a deprecation flip alone is a deprecation change, not a signature change', () => {
    const sb = loadWatch();
    const d = sb.schemaDiff(
        sb.schemaSnapshot([service('S', [method('Get')])]),
        sb.schemaSnapshot([service('S', [method('Get', { deprecated: true })])]));
    assert.equal(d.changedMethods.length, 1);
    assert.equal(d.changedMethods[0].type, 'deprecation');
    assert.equal(d.changedMethods[0].detail, 'marked deprecated');
    // Still a callable-surface event: it marks the row and toasts.
    assert.equal(d.callableMoved, true);
    assert.equal(sb.schemaDeltaSummary(d), '~1 changed');
});

test('schemaDiff: removing the deprecated flag says so', () => {
    const sb = loadWatch();
    const d = sb.schemaDiff(
        sb.schemaSnapshot([service('S', [method('Get', { deprecated: true })])]),
        sb.schemaSnapshot([service('S', [method('Get')])]));
    assert.equal(d.changedMethods[0].detail, 'deprecation removed');
});

test('schemaDiff: a description-only edit lands in the annotation bucket without moving the callable surface', () => {
    const sb = loadWatch();
    const a = method('Get');
    const b = method('Get');
    b.summary = 'A much better summary';
    const d = sb.schemaDiff(
        sb.schemaSnapshot([service('S', [a])]),
        sb.schemaSnapshot([service('S', [b])]));
    assert.ok(d, 'the change log wants to see prose edits');
    assert.equal(d.callableMoved, false, 'but the toast / banner / markers must not fire');
    assert.equal(d.changedMethods.length, 0);
    assert.equal(d.annotatedMethods.length, 1);
    assert.equal(sb.schemaDeltaSummary(d), '±1 note');
});

test('schemaDiff: an annotation riding along with a signature change stays one changed entry', () => {
    const sb = loadWatch();
    const after = method('Get', { httpMethod: 'POST' });
    after.summary = 'also new prose';
    const d = sb.schemaDiff(
        sb.schemaSnapshot([service('S', [method('Get')])]),
        sb.schemaSnapshot([service('S', [after])]));
    assert.equal(d.changedMethods.length, 1);
    assert.equal(d.changedMethods[0].type, 'signature');
    assert.equal(d.annotatedMethods.length, 0, 'no double count for the same method');
});

test('schemaIndexDelta: a deprecation change still marks the method row', () => {
    const sb = loadWatch();
    const d = sb.schemaDiff(
        sb.schemaSnapshot([service('S', [method('Get')])]),
        sb.schemaSnapshot([service('S', [method('Get', { deprecated: true })])]));
    sb._publishDelta(d);
    assert.equal(sb.schemaWatchMarkerFor('S', method('Get')), 'changed');
});

// ---- #185 — per-workspace interval override ----

test('schemaWatchSeconds: the per-workspace override wins over the global setting', () => {
    const sb = loadWatch({ withWsKey: true });
    sb._setInterval('45');
    assert.equal(sb.schemaWatchSeconds(), 45, 'global applies while no override exists');
    sb._setWsInterval('90');
    assert.equal(sb.schemaWatchSeconds(), 90, 'the workspace override wins');
    sb._setWsInterval(null);
    assert.equal(sb.schemaWatchSeconds(), 45, 'clearing the override falls back to the global');
});

test('schemaWatchSeconds: the per-workspace override is clamped like the global', () => {
    const sb = loadWatch({ withWsKey: true });
    sb._setWsInterval('99999');
    assert.equal(sb.schemaWatchSeconds(), 300);
});
