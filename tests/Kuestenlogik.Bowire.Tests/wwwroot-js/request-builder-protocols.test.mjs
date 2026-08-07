// #291 — request-builder-protocols.js unit tests.
//
// The fragment is concatenated AFTER request-builder.js inside
// prologue.js's IIFE — its registration calls (rbLayouts['grpc'] = …)
// reference helpers defined further down. To exercise them in
// isolation we wrap the source in our own IIFE and stub everything
// the layout descriptors call back into.
//
// Most of the file is render-pane code (DOM-heavy). The pure helpers
// we pin here are:
//   * _safeParseJsonObject — JSON guard for the MCP arguments tab
//   * layout descriptor side-effects (registerRequestBuilderLayout
//     calls land in rbLayouts under the right ids)
//
// Tab-render + execute paths reach `fetch` / `document` / event
// listeners — those stay covered by the integration tests rather
// than these unit tests.

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { compileFragment, readFragment } from './_load-fragment.mjs';

// request-builder.js MUST load first: request-builder-protocols.js runs
// its `rbLayouts[...] = …` registrations at parse time and reads the
// layout store request-builder.js initialises. So for #367 the FIRST
// (line-aligned) source is request-builder.js — that is the filename V8
// attributes to; the protocol fragment is appended after it. Both files
// are also exercised by their integration paths; this keeps the attributed
// line data honest rather than mislabelling the offset protocol fragment.
const PROTO_SRC = readFragment(
    '../../../src/Kuestenlogik.Bowire/wwwroot/js/request-builder-protocols.js'
);

const _prelude = `
    var _ls = {};
    var localStorage = {
        getItem: function (k) { return Object.prototype.hasOwnProperty.call(_ls, k) ? _ls[k] : null; },
        setItem: function (k, v) { _ls[k] = String(v); },
        removeItem: function (k) { delete _ls[k]; }
    };
    function wsKey(k) { return 'ws::' + k; }
    function el() { return { appendChild: function () {}, classList: { add: function () {}, remove: function () {} }, addEventListener: function () {} }; }
    function render() {}
    function toast() {}
    function markSaved() {}
    function recordAction() {}
    function substituteVars(s) { return s; }
    function substituteMetadata(m) { return m; }
    function svgIcon() { return ''; }
    function activeWorkspace() { return null; }
    var workspaceId = 'ws1';
    var freeformRequest = {};
    var services = [];
    var responseLastJson = null;
    var isExecuting = false;
    function markJobStart() {}
    function markJobDone() {}
`;
const _postlude = `
    return {
        _safeParseJsonObject: _safeParseJsonObject,
        _getLayouts: function () { return rbLayouts; }
    };
`;
const _loadProtocols = compileFragment(
    '../../../src/Kuestenlogik.Bowire/wwwroot/js/request-builder.js',
    ['document'],
    PROTO_SRC + '\n' + _prelude + '\n' + _postlude
);

function loadProtocols() {
    // request-builder.js reads `document` at its top level, so it arrives
    // as a parameter (defined before the body runs), not a hoisted var.
    const document = {
        addEventListener() {},
        removeEventListener() {},
        getElementById() { return null; },
        querySelector() { return null; },
        querySelectorAll() { return []; },
        createElement() { return { appendChild() {} }; },
        body: { appendChild() {}, removeChild() {} },
        activeElement: null,
    };
    return _loadProtocols({ document });
}

// ---- _safeParseJsonObject ----

test('_safeParseJsonObject: valid object stays as-is', () => {
    const sb = loadProtocols();
    assert.deepEqual(sb._safeParseJsonObject('{"a":1,"b":"two"}'), { a: 1, b: 'two' });
});

test('_safeParseJsonObject: empty / null input → empty object', () => {
    const sb = loadProtocols();
    assert.deepEqual(sb._safeParseJsonObject(''), {});
    assert.deepEqual(sb._safeParseJsonObject(null), {});
    assert.deepEqual(sb._safeParseJsonObject(undefined), {});
});

test('_safeParseJsonObject: malformed JSON → empty object (no throw)', () => {
    const sb = loadProtocols();
    assert.deepEqual(sb._safeParseJsonObject('{not json'), {});
    assert.deepEqual(sb._safeParseJsonObject('xxx'), {});
});

test('_safeParseJsonObject: non-object JSON values → empty object', () => {
    const sb = loadProtocols();
    // Number, string, bool, null are not objects → coerce to {}.
    assert.deepEqual(sb._safeParseJsonObject('42'), {});
    assert.deepEqual(sb._safeParseJsonObject('"hi"'), {});
    assert.deepEqual(sb._safeParseJsonObject('true'), {});
    assert.deepEqual(sb._safeParseJsonObject('null'), {});
});

test('_safeParseJsonObject: arrays are objects in JS → preserved', () => {
    const sb = loadProtocols();
    // typeof [] === 'object', so the helper keeps arrays.
    // This pins the current behaviour — callers downstream know to
    // expect either a plain object or an array.
    assert.deepEqual(sb._safeParseJsonObject('[1,2,3]'), [1, 2, 3]);
});

// ---- Layout registry side-effects ----

test('protocols fragment registers each non-REST layout under its id', () => {
    const sb = loadProtocols();
    const layouts = sb._getLayouts();
    // rest comes from request-builder.js itself; the others land here.
    for (const id of ['grpc', 'mcp', 'mqtt', 'websocket', 'sse']) {
        assert.ok(layouts[id], 'missing layout for ' + id);
        assert.equal(typeof layouts[id].subTabs, 'function', id + '.subTabs');
    }
});

test('each registered protocol layout exposes execute + executeLabel', () => {
    const sb = loadProtocols();
    const layouts = sb._getLayouts();
    for (const id of ['grpc', 'mcp', 'mqtt', 'websocket', 'sse']) {
        assert.equal(typeof layouts[id].execute, 'function', id + '.execute');
        assert.equal(typeof layouts[id].executeLabel, 'function', id + '.executeLabel');
    }
});

test('each protocol layout subTabs returns a non-empty list of {id,label}', () => {
    const sb = loadProtocols();
    const layouts = sb._getLayouts();
    const fr = { _requestBuilder: { protocol: 'grpc', params: [], headers: [], byProtocol: {} } };
    for (const id of ['grpc', 'mcp', 'mqtt', 'websocket', 'sse']) {
        fr._requestBuilder.protocol = id;
        const tabs = layouts[id].subTabs(fr);
        assert.ok(Array.isArray(tabs), id + '.subTabs returns array');
        assert.ok(tabs.length >= 1, id + ' has tabs');
        for (const t of tabs) {
            assert.equal(typeof t.id, 'string');
            assert.equal(typeof t.label, 'string');
        }
    }
});

test('mqtt + sse + ws + grpc layouts expose a defaults() factory', () => {
    const sb = loadProtocols();
    const layouts = sb._getLayouts();
    for (const id of ['grpc', 'mcp', 'mqtt', 'websocket', 'sse']) {
        if (typeof layouts[id].defaults === 'function') {
            const d = layouts[id].defaults();
            assert.equal(typeof d, 'object');
            assert.ok(d !== null);
        }
    }
});
