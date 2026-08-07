// #182 — side-by-side service version diff unit tests.
//
// service-compare.js is NOT self-contained: its schema-level diff reuses
// the #185 pure helpers in api.js (schemaMethodRecord / schemaChangeDetail
// / schemaMessageShape) and its response diff reuses perf-diff.js
// (diffJsonStructured). In production all three are one concatenated IIFE,
// so the harness loads api.js + perf-diff.js + service-compare.js into one
// compiled scope — the realistic wiring — with the host globals both
// upstream fragments close over stubbed in the appended block.

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { compileFragment, readFragment } from './_load-fragment.mjs';

// service-compare.js is NOT self-contained (see header): its diff reuses
// pure helpers from api.js + perf-diff.js. Those two are covered by their
// own suites, so for #367 the target fragment goes FIRST (line-aligned,
// filename = service-compare.js) and the upstream fragments + host stubs
// are appended after it — the appended lines land beyond the file and are
// dropped by Codecov, while service-compare.js's own lines stay accurate.
const JS = (f) => '../../../src/Kuestenlogik.Bowire/wwwroot/js/' + f;
const _upstream = readFragment(JS('api.js')) + '\n' + readFragment(JS('perf-diff.js'));

function load() {
    const prelude = `
        var _ls = {};
        var localStorage = {
            getItem: function (k) { return Object.prototype.hasOwnProperty.call(_ls, k) ? _ls[k] : null; },
            setItem: function (k, v) { _ls[k] = String(v); },
            removeItem: function (k) { delete _ls[k]; }
        };
        // Host globals the upstream fragments close over.
        var services = [];
        var protocols = [];
        var serverUrls = [];
        var serverUrlAliases = {};
        var connectionStatuses = {};
        var discoveryErrors = {};
        var discoveryAttempts = {};
        var discoveryHints = {};
        var discoveryDiagnosticsOpen = new Set();
        var expandedServices = new Set();
        var protocolFilter = new Set();
        var urlFilter = new Set();
        var isLoadingServices = false;
        var selectedService = null, selectedMethod = null;
        var responseSnapshots = {};
        var MAX_RESPONSE_SNAPSHOTS = 5;
        var benchmark = {};
        var railMode = 'discover', sidebarView = 'services', sourceMode = 'url';
        var config = { prefix: '' };
        var serviceCompareOpen = false, serviceCompareState = null;
        var window = (typeof globalThis.window !== 'undefined') ? globalThis.window : {};
        var document = { getElementById: function () { return null; }, querySelector: function () { return null; }, createElement: function () { return {}; }, body: { appendChild: function () {} } };
        function render() {}
        function toast() {}
        function addConsoleEntry() {}
        function el() { return { appendChild: function () {}, className: '' }; }
        function stopBenchmark() {}
        function runBenchmark() {}
        function persistExpandedServices() {}
        function getFilteredServices() { return services; }
        function serverUrlParam(prefix, url) { return url ? ('?serverUrl=' + encodeURIComponent(url)) : ''; }
        function serverUrlParamForService(svc, prefix) { return svc && svc.originUrl ? ('?serverUrl=' + encodeURIComponent(svc.originUrl)) : ''; }
        function truncateMiddle(s, n) { return s; }
        function svgIcon() { return ''; }
        function downloadTextFile(content, name, mime) { _lastDownload = { content: content, name: name, mime: mime }; }
        var _lastDownload = null;
        var _fetchImpl = function () { return Promise.reject(new Error('no fetch stubbed')); };
        function fetch(url, opts) { return _fetchImpl(url, opts); }
    `;
    const postlude = `
        return {
            _stripVersionMarkers: _stripVersionMarkers,
            compareMethodAlignKey: compareMethodAlignKey,
            alignCompareMethods: alignCompareMethods,
            compareMethodPair: compareMethodPair,
            _compareIsUnaryPair: _compareIsUnaryPair,
            computeServiceSchemaDiff: computeServiceSchemaDiff,
            compareSchemaSummary: compareSchemaSummary,
            buildCompareMarkdown: buildCompareMarkdown,
            compareSideLabel: compareSideLabel,
            discoverForCompare: discoverForCompare,
            invokeForCompare: invokeForCompare,
            _setFetch: function (f) { _fetchImpl = f; },
            _lastDownload: function () { return _lastDownload; }
        };
    `;
    // Target fragment first (line-aligned); upstream fragments + stubs
    // appended (hoisted, so service-compare.js's function bodies still see
    // them). Compiled per call because the whole blob is cheap here.
    return compileFragment(
        JS('service-compare.js'),
        [],
        _upstream + '\n' + prelude + '\n' + postlude
    )();
}

// Minimal method / service shapes matching /api/services.
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
        summary: opts.summary || '',
        inputType: opts.inputType || { name: 'In', fields: [] },
        outputType: opts.outputType || { name: 'Out', fields: [] }
    };
}
function service(name, methods, opts) {
    opts = opts || {};
    return { name: name, source: opts.source || 'rest', originUrl: opts.originUrl || null, methods: methods };
}

const flush = () => new Promise((r) => setTimeout(r, 0));

// ---- version stripping / alignment ----

test('stripVersionMarkers: strips /v2/ path segment, .v2. package, _v2 suffix', () => {
    const sb = load();
    assert.equal(sb._stripVersionMarkers('GET /v2/users'), 'GET /users');
    assert.equal(sb._stripVersionMarkers('user.v2.UserService/Get'), 'user.UserService/Get');
    assert.equal(sb._stripVersionMarkers('GetUser_v2'), 'GetUser');
    assert.equal(sb._stripVersionMarkers('GET /v10/x'), 'GET /x');
});

test('stripVersionMarkers: leaves non-version digits alone', () => {
    const sb = load();
    assert.equal(sb._stripVersionMarkers('GET /ipv4/status'), 'GET /ipv4/status');
    assert.equal(sb._stripVersionMarkers('Base64Decode'), 'Base64Decode');
    assert.equal(sb._stripVersionMarkers('GET /oauth2/token'), 'GET /oauth2/token');
});

test('alignCompareMethods: exact names pair, extras land in onlyA / onlyB', () => {
    const sb = load();
    const a = service('S', [method('List'), method('Get'), method('Legacy')]);
    const b = service('S', [method('List'), method('Get'), method('Fresh')]);
    const al = sb.alignCompareMethods(a, b);
    assert.equal(al.paired.length, 2);
    assert.deepEqual(al.onlyA.map(m => m.name), ['Legacy']);
    assert.deepEqual(al.onlyB.map(m => m.name), ['Fresh']);
});

test('alignCompareMethods: a _v2 twin aligns with its base method (fuzzy)', () => {
    const sb = load();
    const a = service('UserService', [method('GetUser', { fullName: 'GetUser' })]);
    const b = service('UserService_v2', [method('GetUser', { fullName: 'GetUser_v2' })]);
    const al = sb.alignCompareMethods(a, b);
    assert.equal(al.paired.length, 1, 'GetUser aligns with GetUser_v2');
    assert.equal(al.onlyA.length, 0);
    assert.equal(al.onlyB.length, 0);
});

test('alignCompareMethods: a /v1/ vs /v2/ REST path aligns', () => {
    const sb = load();
    const a = service('API', [method('list', { fullName: 'GET /v1/users' })]);
    const b = service('API', [method('list', { fullName: 'GET /v2/users' })]);
    assert.equal(sb.alignCompareMethods(a, b).paired.length, 1);
});

test('computeServiceSchemaDiff: a pure /v1/ → /v2/ path bump is NOT a route change (review #1)', () => {
    // The align key strips the version segment, so the route facet must
    // too — otherwise every method of a path-versioned REST API is
    // wrongly flagged as a signature change and the real ones drown.
    const sb = load();
    const a = service('API', [method('list', { fullName: 'GET /v1/users', httpMethod: 'GET', httpPath: '/v1/users' })]);
    const b = service('API', [method('list', { fullName: 'GET /v2/users', httpMethod: 'GET', httpPath: '/v2/users' })]);
    const d = sb.computeServiceSchemaDiff(a, b);
    assert.equal(d.changed.length, 0, 'a pure version bump is not a route change');
    assert.equal(d.unchanged.length, 1);
});

test('computeServiceSchemaDiff: a real verb change under a versioned path IS still reported', () => {
    const sb = load();
    const a = service('API', [method('save', { fullName: 'GET /v1/users', httpMethod: 'GET', httpPath: '/v1/users' })]);
    const b = service('API', [method('save', { fullName: 'POST /v2/users', httpMethod: 'POST', httpPath: '/v2/users' })]);
    const d = sb.computeServiceSchemaDiff(a, b);
    assert.equal(d.changed.length, 1);
    assert.match(d.changed[0].detail, /route GET \/users → POST \/users/);
});

test('alignCompareMethods: GetUser + GetUser_v2 in ONE service both survive (review #2)', () => {
    // First-occurrence-wins used to drop GetUser_v2 (same stripped key).
    const sb = load();
    const a = service('S', [method('GetUser', { fullName: 'GetUser' }), method('GetUser_v2', { fullName: 'GetUser_v2' })]);
    const b = service('S', [method('GetUser', { fullName: 'GetUser' })]);
    const al = sb.alignCompareMethods(a, b);
    assert.equal(al.paired.length, 1, 'GetUser pairs');
    assert.deepEqual(al.onlyA.map(m => m.name), ['GetUser_v2'], 'GetUser_v2 is reported as removed, not silently dropped');
});

test('_compareIsUnaryPair: streaming pairs are excluded from invoke-both (review #5)', () => {
    const sb = load();
    const unary = { a: method('U'), b: method('U') };
    const stream = { a: method('S', { serverStreaming: true, methodType: 'ServerStreaming' }), b: method('S', { serverStreaming: true, methodType: 'ServerStreaming' }) };
    assert.equal(sb._compareIsUnaryPair(unary), true);
    assert.equal(sb._compareIsUnaryPair(stream), false);
});

// ---- schema diff ----

test('computeServiceSchemaDiff: added / removed / changed split', () => {
    const sb = load();
    const a = service('S', [
        method('Keep'),
        method('Morph', { httpMethod: 'GET', httpPath: '/morph' }),
        method('Drop')
    ]);
    const b = service('S', [
        method('Keep'),
        method('Morph', { httpMethod: 'POST', httpPath: '/morph' }),
        method('Add')
    ]);
    const d = sb.computeServiceSchemaDiff(a, b);
    assert.deepEqual(d.removed.map(m => m.name), ['Drop']);
    assert.deepEqual(d.added.map(m => m.name), ['Add']);
    assert.equal(d.changed.length, 1);
    assert.equal(d.changed[0].a.name, 'Morph');
    assert.match(d.changed[0].detail, /route GET \/morph → POST \/morph/);
    assert.equal(d.unchanged.length, 1);
});

test('computeServiceSchemaDiff: a request-shape change is a signature change', () => {
    const sb = load();
    const a = service('S', [method('Find', { inputType: { name: 'In', fields: [] } })]);
    const b = service('S', [method('Find', { inputType: { name: 'In', fields: [{ name: 'q', type: 'string' }] } })]);
    const d = sb.computeServiceSchemaDiff(a, b);
    assert.equal(d.changed.length, 1);
    assert.match(d.changed[0].detail, /request shape changed/);
});

test('computeServiceSchemaDiff: a deprecation flip is a change', () => {
    const sb = load();
    const a = service('S', [method('Old')]);
    const b = service('S', [method('Old', { deprecated: true })]);
    const d = sb.computeServiceSchemaDiff(a, b);
    assert.equal(d.changed.length, 1);
    assert.match(d.changed[0].detail, /marked deprecated/);
});

test('computeServiceSchemaDiff: a prose-only edit is unchanged (noteOnly)', () => {
    const sb = load();
    const a = service('S', [method('Get', { summary: 'old' })]);
    const b = service('S', [method('Get', { summary: 'new prose' })]);
    const d = sb.computeServiceSchemaDiff(a, b);
    assert.equal(d.changed.length, 0);
    assert.equal(d.unchanged.length, 1);
    assert.equal(d.unchanged[0].noteOnly, true);
});

test('compareSchemaSummary: identical schemas say so', () => {
    const sb = load();
    const a = service('S', [method('Get')]);
    const d = sb.computeServiceSchemaDiff(a, service('S', [method('Get')]));
    assert.equal(sb.compareSchemaSummary(d), 'schema identical');
});

// ---- markdown export ----

test('buildCompareMarkdown: sections for removed / added / changed + response diffs', () => {
    const sb = load();
    const a = service('Users', [method('List'), method('GetV1', { fullName: 'Get' }), method('Drop')], { originUrl: 'http://a' });
    const b = service('Users', [method('List'), method('GetV1', { fullName: 'Get', httpMethod: 'POST' }), method('Add')], { originUrl: 'http://b' });
    const state = {
        sides: { a: { url: 'http://a', serviceName: 'Users', service: a }, b: { url: 'http://b', serviceName: 'Users', service: b } },
        schemaDiff: sb.computeServiceSchemaDiff(a, b),
        responses: {
            'get': { label: 'Get', fieldDiff: { kind: 'json', entries: [{ path: '$.id', change: 'kind-changed', aKind: 'string', bKind: 'number', aText: '"7"', bText: '7' }] } }
        }
    };
    const md = sb.buildCompareMarkdown(state);
    assert.match(md, /# Service comparison/);
    assert.match(md, /## Removed methods/);
    assert.match(md, /## Added methods/);
    assert.match(md, /## Signature changes/);
    assert.match(md, /## Response diffs/);
    assert.match(md, /type string → number/);
});

// ---- headless IO ----

test('invokeForCompare: routes to the service originUrl and unwraps the envelope', async () => {
    const sb = load();
    let seenUrl = null, seenBody = null;
    sb._setFetch((url, opts) => {
        seenUrl = url; seenBody = JSON.parse(opts.body);
        return Promise.resolve({ ok: true, json: () => Promise.resolve({ response: '{"x":1}', status: 'OK', duration_ms: 5 }) });
    });
    const r = await sb.invokeForCompare(service('S', [], { originUrl: 'http://target', source: 'rest' }), 'Get', '{}', null);
    assert.equal(r.ok, true);
    assert.equal(r.response, '{"x":1}');
    assert.match(seenUrl, /serverUrl=http%3A%2F%2Ftarget/);
    assert.equal(seenBody.service, 'S');
    assert.equal(seenBody.protocol, 'rest');
});

test('invokeForCompare: a problem+json title becomes an error result, not a throw', async () => {
    const sb = load();
    sb._setFetch(() => Promise.resolve({ ok: false, json: () => Promise.resolve({ title: 'boom', status: 'ERR' }) }));
    const r = await sb.invokeForCompare(service('S', [], { originUrl: 'http://x' }), 'Get', '{}', null);
    assert.equal(r.ok, false);
    assert.equal(r.error, 'boom');
});

test('discoverForCompare: unwraps both the array and the envelope shape, stamps originUrl', async () => {
    const sb = load();
    sb._setFetch(() => Promise.resolve({ ok: true, json: () => Promise.resolve({ services: [{ name: 'S', methods: [] }] }) }));
    const list = await sb.discoverForCompare('http://a');
    assert.equal(list.length, 1);
    assert.equal(list[0].originUrl, 'http://a');
});
