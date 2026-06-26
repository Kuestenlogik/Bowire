// #289 / #266-#268 / #291 / #293 — request-builder.js unit tests.
//
// Targets:
//   * _activeKvCount + _kvToObject — KV-table helpers driving the
//     parameter / header tab badges + the final wire-request payload.
//   * _composeUrlWithParams — merges Parameter rows onto the URL's
//     existing query string + preserves hash. Last-write-wins.
//   * isHoppRequest — detector for the request-builder shape.
//   * ensureHoppState — seeds the per-protocol scratch bag.
//   * rbSetProtocol — switches active protocol + re-anchors activeTab.
//   * registerRequestBuilderLayout — plugin entry point.
//   * loadHoppHistory / pushHoppHistoryEntry — history round-trip + cap.

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, resolve } from 'node:path';

const __dirname = dirname(fileURLToPath(import.meta.url));
const SRC = readFileSync(
    resolve(__dirname, '../../../src/Kuestenlogik.Bowire/wwwroot/js/request-builder.js'),
    'utf8'
);

function loadRequestBuilder(state) {
    state = state || {};
    const prelude = `
        var _ls = {};
        var localStorage = {
            getItem: function (k) { return Object.prototype.hasOwnProperty.call(_ls, k) ? _ls[k] : null; },
            setItem: function (k, v) { _ls[k] = String(v); },
            removeItem: function (k) { delete _ls[k]; }
        };
        function wsKey(k) { return 'ws::' + k; }
        function el() { return { appendChild: function () {}, classList: { add: function () {} } }; }
        function render() {}
        function toast() {}
        function markSaved() {}
        function recordAction() {}
        function substituteVars(s) { return s; }
        function substituteMetadata(m) { return m; }
        function executeRequest() {}
        function activeWorkspace() { return null; }
        var workspaceId = 'ws1';
        var freeformRequest = {};
        var responseLastJson = null;
        // Persistence callback the implementation will invoke.
        function persistHoppHistory() {
            try { localStorage.setItem(wsKey(RB_HISTORY_KEY), JSON.stringify(rbHistoryList || [])); } catch (_) {}
        }
        // The fragment registers two top-level document.addEventListener
        // calls (Ctrl+L global shortcut + outside-click closer). Node
        // has no document, so we stub the minimum surface they touch.
        var document = {
            addEventListener: function () {},
            removeEventListener: function () {},
            getElementById: function () { return null; },
            querySelector: function () { return null; },
            querySelectorAll: function () { return []; },
            createElement: function () { return { appendChild: function () {} }; },
            body: { appendChild: function () {}, removeChild: function () {} },
            activeElement: null
        };
    `;
    const postlude = `
        return {
            _activeKvCount: _activeKvCount,
            _kvToObject: _kvToObject,
            _composeUrlWithParams: _composeUrlWithParams,
            isHoppRequest: isHoppRequest,
            ensureHoppState: ensureHoppState,
            rbSetProtocol: rbSetProtocol,
            rbProtoState: rbProtoState,
            rbActiveLayout: rbActiveLayout,
            registerRequestBuilderLayout: registerRequestBuilderLayout,
            loadHoppHistory: loadHoppHistory,
            pushHoppHistoryEntry: pushHoppHistoryEntry,
            clearHoppHistory: clearHoppHistory,
            _newHoppState: _newHoppState,
            _getLayouts: function () { return rbLayouts; },
            _setHistoryList: function (v) { rbHistoryList = v; },
            _getHistoryList: function () { return rbHistoryList; },
            _setStorage: function (k, v) { _ls[k] = v; }
        };
    `;
    const body = prelude + '\n' + SRC + '\n' + postlude;
    const fn = new Function('state', body);
    return fn(state);
}

// ---- _activeKvCount + _kvToObject ----

test('_activeKvCount: empty list → 0', () => {
    const sb = loadRequestBuilder();
    assert.equal(sb._activeKvCount([]), 0);
    assert.equal(sb._activeKvCount(null), 0);
    assert.equal(sb._activeKvCount(undefined), 0);
});

test('_activeKvCount: only counts enabled rows with non-empty keys', () => {
    const sb = loadRequestBuilder();
    const rows = [
        { key: 'a', value: '1', enabled: true },
        { key: 'b', value: '2', enabled: false },   // disabled
        { key: '',  value: '3', enabled: true },    // empty key
        { key: '   ', value: '4', enabled: true }, // whitespace key
        { key: 'd', value: '5' }                    // enabled omitted → treated as enabled
    ];
    assert.equal(sb._activeKvCount(rows), 2); // 'a' + 'd'
});

test('_kvToObject: builds a string-valued map of enabled rows', () => {
    const sb = loadRequestBuilder();
    const rows = [
        { key: 'X', value: '1', enabled: true },
        { key: 'Y', value: '2' },
        { key: 'Z', value: '3', enabled: false }
    ];
    const obj = sb._kvToObject(rows);
    assert.deepEqual(obj, { X: '1', Y: '2' });
});

test('_kvToObject: null value coerces to empty string', () => {
    const sb = loadRequestBuilder();
    const rows = [{ key: 'a', value: null }];
    assert.deepEqual(sb._kvToObject(rows), { a: '' });
});

test('_kvToObject: trims surrounding whitespace from keys', () => {
    const sb = loadRequestBuilder();
    const rows = [{ key: '  k  ', value: 'v' }];
    assert.deepEqual(sb._kvToObject(rows), { k: 'v' });
});

// ---- _composeUrlWithParams ----

test('_composeUrlWithParams: empty base + empty rows → empty', () => {
    const sb = loadRequestBuilder();
    assert.equal(sb._composeUrlWithParams('', []), '');
    assert.equal(sb._composeUrlWithParams(null, null), '');
});

test('_composeUrlWithParams: no rows + no query → URL unchanged', () => {
    const sb = loadRequestBuilder();
    assert.equal(sb._composeUrlWithParams('https://a/b', []), 'https://a/b');
});

test('_composeUrlWithParams: rows append onto bare URL', () => {
    const sb = loadRequestBuilder();
    const out = sb._composeUrlWithParams('https://a/b', [
        { key: 'q', value: 'one', enabled: true },
        { key: 'r', value: 'two', enabled: true }
    ]);
    assert.equal(out, 'https://a/b?q=one&r=two');
});

test('_composeUrlWithParams: rows MERGE onto an existing query string', () => {
    const sb = loadRequestBuilder();
    const out = sb._composeUrlWithParams('https://a/b?existing=v', [
        { key: 'q', value: 'new', enabled: true }
    ]);
    // existing comes first, then the row.
    assert.equal(out, 'https://a/b?existing=v&q=new');
});

test('_composeUrlWithParams: URL-encodes both keys + values', () => {
    const sb = loadRequestBuilder();
    const out = sb._composeUrlWithParams('https://a', [
        { key: 'q val', value: 'hello world', enabled: true }
    ]);
    assert.equal(out, 'https://a?q%20val=hello%20world');
});

test('_composeUrlWithParams: disabled rows are dropped', () => {
    const sb = loadRequestBuilder();
    const out = sb._composeUrlWithParams('https://a', [
        { key: 'on', value: '1', enabled: true },
        { key: 'off', value: '2', enabled: false }
    ]);
    assert.equal(out, 'https://a?on=1');
});

test('_composeUrlWithParams: preserves hash fragment', () => {
    const sb = loadRequestBuilder();
    const out = sb._composeUrlWithParams('https://a/b#anchor', [
        { key: 'q', value: '1', enabled: true }
    ]);
    assert.equal(out, 'https://a/b?q=1#anchor');
});

test('_composeUrlWithParams: empty value emits key-only param (no =)', () => {
    const sb = loadRequestBuilder();
    const out = sb._composeUrlWithParams('https://a', [
        { key: 'q', value: '', enabled: true }
    ]);
    assert.equal(out, 'https://a?q');
});

test('_composeUrlWithParams: empty-key rows are skipped', () => {
    const sb = loadRequestBuilder();
    const out = sb._composeUrlWithParams('https://a', [
        { key: '   ', value: 'x', enabled: true },
        { key: '',    value: 'y', enabled: true }
    ]);
    assert.equal(out, 'https://a');
});

// ---- isHoppRequest ----

test('isHoppRequest: true iff _requestBuilder is truthy', () => {
    const sb = loadRequestBuilder();
    assert.equal(sb.isHoppRequest({ _requestBuilder: {} }), true);
    assert.equal(sb.isHoppRequest({}), false);
    assert.equal(sb.isHoppRequest(null), false);
    assert.equal(sb.isHoppRequest(undefined), false);
});

// ---- ensureHoppState ----

test('ensureHoppState: seeds _requestBuilder on bare request', () => {
    const sb = loadRequestBuilder();
    const fr = {};
    sb.ensureHoppState(fr);
    assert.ok(fr._requestBuilder);
    assert.equal(fr._requestBuilder.protocol, 'rest');
    assert.equal(fr._requestBuilder.activeTab, 'parameter');
    assert.ok(Array.isArray(fr._requestBuilder.params));
    assert.ok(Array.isArray(fr._requestBuilder.headers));
    assert.equal(fr._requestBuilder.bodyMode, 'json');
    assert.equal(fr._requestBuilder.authKind, 'none');
});

test('ensureHoppState: idempotent — does not overwrite existing edits', () => {
    const sb = loadRequestBuilder();
    const fr = { _requestBuilder: { protocol: 'mqtt', params: [{ key: 'k' }] } };
    sb.ensureHoppState(fr);
    assert.equal(fr._requestBuilder.protocol, 'mqtt');
    assert.equal(fr._requestBuilder.params.length, 1);
});

test('ensureHoppState: migrates pre-protocol request to "rest"', () => {
    const sb = loadRequestBuilder();
    const fr = { _requestBuilder: { params: [], headers: [], protocol: null } };
    sb.ensureHoppState(fr);
    assert.equal(fr._requestBuilder.protocol, 'rest');
});

test('ensureHoppState: no-op on null fr', () => {
    const sb = loadRequestBuilder();
    sb.ensureHoppState(null);
    sb.ensureHoppState(undefined);
    // Did not throw.
});

// ---- _newHoppState ----

test('_newHoppState: returns a fresh skeleton with sensible defaults', () => {
    const sb = loadRequestBuilder();
    const s = sb._newHoppState();
    assert.equal(s.protocol, 'rest');
    assert.equal(s.activeTab, 'parameter');
    assert.deepEqual(s.params, []);
    assert.deepEqual(s.headers, []);
    assert.equal(s.bodyMode, 'json');
    assert.equal(s.preScript, '');
    assert.equal(s.postScript, '');
    assert.equal(s.authKind, 'none');
    assert.deepEqual(s.authData, {});
    assert.deepEqual(s.byProtocol, {});
});

// ---- registerRequestBuilderLayout ----

test('registerRequestBuilderLayout: stores by id', () => {
    const sb = loadRequestBuilder();
    sb.registerRequestBuilderLayout({ id: 'custom', subTabs: () => [] });
    const layouts = sb._getLayouts();
    assert.ok(layouts.custom);
});

test('registerRequestBuilderLayout: ignores layouts without an id', () => {
    const sb = loadRequestBuilder();
    const before = Object.keys(sb._getLayouts()).length;
    sb.registerRequestBuilderLayout({ subTabs: () => [] });
    sb.registerRequestBuilderLayout(null);
    const after = Object.keys(sb._getLayouts()).length;
    assert.equal(after, before);
});

// ---- rbSetProtocol ----

test('rbSetProtocol: rejects unknown protocol id', () => {
    const sb = loadRequestBuilder();
    const fr = {};
    sb.ensureHoppState(fr);
    const beforeProto = fr._requestBuilder.protocol;
    sb.rbSetProtocol(fr, 'mystery-proto');
    assert.equal(fr._requestBuilder.protocol, beforeProto);  // unchanged
});

test('rbSetProtocol: known protocol switches + may re-anchor activeTab', () => {
    const sb = loadRequestBuilder();
    sb.registerRequestBuilderLayout({
        id: 'custom',
        subTabs: () => [{ id: 'overview' }, { id: 'logs' }],
        defaults: () => ({ key: 'value' })
    });
    const fr = {};
    sb.ensureHoppState(fr);
    fr._requestBuilder.activeTab = 'parameter'; // not in custom layout
    sb.rbSetProtocol(fr, 'custom');
    assert.equal(fr._requestBuilder.protocol, 'custom');
    assert.equal(fr._requestBuilder.activeTab, 'overview');  // re-anchored
    // Defaults landed in the per-protocol bag.
    assert.deepEqual(fr._requestBuilder.byProtocol.custom, { key: 'value' });
});

// ---- loadHoppHistory / pushHoppHistoryEntry / clear ----

test('loadHoppHistory: empty array on a fresh workspace', () => {
    const sb = loadRequestBuilder();
    assert.deepEqual(sb.loadHoppHistory(), []);
});

test('loadHoppHistory: tolerates corrupt JSON in storage', () => {
    const sb = loadRequestBuilder();
    sb._setStorage('ws::bowire_request_builder_history', '{not json');
    sb._setHistoryList(null); // reset lazy-load cache
    const list = sb.loadHoppHistory();
    assert.deepEqual(list, []);
});

test('clearHoppHistory: empties the buffer', () => {
    const sb = loadRequestBuilder();
    sb._setHistoryList([{ id: 'a' }, { id: 'b' }]);
    sb.clearHoppHistory();
    assert.deepEqual(sb._getHistoryList(), []);
});
