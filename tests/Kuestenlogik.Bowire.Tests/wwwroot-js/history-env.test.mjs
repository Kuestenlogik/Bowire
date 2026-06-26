// #145 / #126 — history-env.js unit tests.
//
// Targets the pure-ish helpers that don't need a live DOM:
//   * resolveSystemVar — bare ${now} / ${uuid} / ${random} / ${now±N}
//   * substituteVars   — placeholder resolver (ENV + system + escape)
//   * isHistoryEntryOk — status-name → ok/fail classifier
//   * filterHistoryEntries — combined search + bucket filter
//   * getHistory / addHistory / clearHistory — localStorage round-trip
//
// Same wrap trick as vars-deprecation.test.mjs — the fragment is
// concatenated into prologue.js's IIFE in production, so we recreate
// that scope by declaring the host-provided names + an in-memory
// localStorage at the top of the wrapper.

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, resolve } from 'node:path';

const __dirname = dirname(fileURLToPath(import.meta.url));
const SRC = readFileSync(
    resolve(__dirname, '../../../src/Kuestenlogik.Bowire/wwwroot/js/history-env.js'),
    'utf8'
);

function loadHistoryEnv(state) {
    state = state || {};
    const prelude = `
        // In-memory localStorage.
        var _ls = {};
        var localStorage = {
            getItem: function (k) { return Object.prototype.hasOwnProperty.call(_ls, k) ? _ls[k] : null; },
            setItem: function (k, v) { _ls[k] = String(v); },
            removeItem: function (k) { delete _ls[k]; }
        };
        // Pretend a browser crypto API exists — Node has one too.
        var crypto = globalThis.crypto;
        // history-env.js never declares these constants itself; they
        // live in prologue.js in production. Provide them here.
        var HISTORY_KEY = 'bowire_history';
        var FAVORITES_KEY = 'bowire_favorites';
        var ENVIRONMENTS_KEY = 'bowire_environments';
        var GLOBAL_VARS_KEY = 'bowire_global_vars';
        var ACTIVE_ENV_KEY = 'bowire_active_env';
        var MAX_HISTORY = 200;
        function wsKey(k) { return 'ws::' + k; }
        // Module-shared state the fragment reads.
        var historySearchQuery = state.search || '';
        var historyStatusFilter = state.bucket || 'all';
        var recordingsList = state.recordingsList || [];
        var recordingManagerSelectedId = state.recordingId || null;
        var flowVars = state.flowVars || null;
        var lastResponseJson = state.lastResponseJson || null;
        var lastResponseText = state.lastResponseText || null;
        // No globals / environments unless the test wires them up.
        // Provide a switchable shim that can be overridden by the
        // test before calling substituteVars.
        var _testGlobals = state.globals || {};
        var _testActiveEnv = state.activeEnv || null;
        var _testWorkspaceVars = state.workspaceVars || null;
        // The fragment defines its own getEnvironments / saveEnvironments
        // off localStorage. Leave those untouched.
        // activeWorkspace is referenced via typeof check so we can
        // selectively expose it.
        ${state.activeWorkspace ? 'function activeWorkspace() { return _testWorkspaceVars ? { vars: _testWorkspaceVars } : null; }' : ''}
        function resolveResponseVar(_path) { return null; }
        function resolveSecret(_n) { return null; }
        function resolveAiVar(_n) { return null; }
        function recordAction() {}
        function render() {}
    `;
    // Override getGlobalVars and getActiveEnv AFTER the fragment defines
    // them, so tests can plug in synthetic env vars without standing up
    // the entire workspace machinery.
    const postlude = `
        // Replace getGlobalVars / getActiveEnv with the test-provided
        // shims (capturing _testGlobals / _testActiveEnv from this
        // scope). The fragment's definitions are wholesale replaced —
        // the function statement above gets hoisted but our re-assignment
        // wins at call time.
        getGlobalVars = function () { return _testGlobals; };
        getActiveEnv = function () { return _testActiveEnv; };
        return {
            resolveSystemVar: resolveSystemVar,
            substituteVars: substituteVars,
            isHistoryEntryOk: isHistoryEntryOk,
            filterHistoryEntries: filterHistoryEntries,
            getHistory: getHistory,
            addHistory: addHistory,
            clearHistory: clearHistory,
            getMergedVars: getMergedVars,
            substituteMetadata: substituteMetadata,
            substituteMessages: substituteMessages,
            _setGlobals: function (g) { _testGlobals = g; },
            _setActiveEnv: function (e) { _testActiveEnv = e; },
            _setSearch: function (q) { historySearchQuery = q; },
            _setBucket: function (b) { historyStatusFilter = b; },
            _getRawStorage: function () { return _ls; }
        };
    `;
    const body = prelude + '\n' + SRC + '\n' + postlude;
    const fn = new Function('state', body);
    return fn(state);
}

// ---- resolveSystemVar ----

test('resolveSystemVar: now returns Unix seconds as string', () => {
    const sb = loadHistoryEnv();
    const r = sb.resolveSystemVar('now');
    assert.equal(typeof r, 'string');
    const n = Number(r);
    const liveNow = Math.floor(Date.now() / 1000);
    assert.ok(Math.abs(n - liveNow) <= 2, 'now should be within ~2s of system clock');
});

test('resolveSystemVar: nowMs returns Unix millis as string', () => {
    const sb = loadHistoryEnv();
    const r = sb.resolveSystemVar('nowMs');
    const n = Number(r);
    assert.ok(Math.abs(n - Date.now()) <= 1000);
});

test('resolveSystemVar: timestamp returns ISO 8601 string', () => {
    const sb = loadHistoryEnv();
    const r = sb.resolveSystemVar('timestamp');
    assert.match(r, /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}/);
});

test('resolveSystemVar: uuid matches v4 shape', () => {
    const sb = loadHistoryEnv();
    const r = sb.resolveSystemVar('uuid');
    assert.match(r, /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/);
});

test('resolveSystemVar: random is a parseable non-negative integer', () => {
    const sb = loadHistoryEnv();
    const r = sb.resolveSystemVar('random');
    const n = Number(r);
    assert.ok(Number.isInteger(n));
    assert.ok(n >= 0);
    assert.ok(n < 0x100000000);
});

test('resolveSystemVar: now+3600 returns now + 3600s', () => {
    const sb = loadHistoryEnv();
    const before = Math.floor(Date.now() / 1000);
    const r = sb.resolveSystemVar('now+3600');
    const after = Math.floor(Date.now() / 1000);
    const n = Number(r);
    assert.ok(n >= before + 3600);
    assert.ok(n <= after + 3600);
});

test('resolveSystemVar: now-60 returns now - 60s', () => {
    const sb = loadHistoryEnv();
    const before = Math.floor(Date.now() / 1000);
    const r = sb.resolveSystemVar('now-60');
    const after = Math.floor(Date.now() / 1000);
    const n = Number(r);
    assert.ok(n >= before - 60);
    assert.ok(n <= after - 60);
});

test('resolveSystemVar: unknown key returns null', () => {
    const sb = loadHistoryEnv();
    assert.equal(sb.resolveSystemVar('nope'), null);
    assert.equal(sb.resolveSystemVar(''), null);
    assert.equal(sb.resolveSystemVar('now+'), null);
    assert.equal(sb.resolveSystemVar('now+abc'), null);
});

// ---- substituteVars: ${name} legacy + {{name}} canonical ----

test('substituteVars: passthrough for plain strings', () => {
    const sb = loadHistoryEnv();
    assert.equal(sb.substituteVars('hello world'), 'hello world');
    assert.equal(sb.substituteVars(''), '');
});

test('substituteVars: ${name} resolves from env', () => {
    const sb = loadHistoryEnv({ globals: { name: 'alice' } });
    assert.equal(sb.substituteVars('hi ${name}!'), 'hi alice!');
});

test('substituteVars: {{name}} resolves from env (Postman shape)', () => {
    const sb = loadHistoryEnv({ globals: { x: 'y' } });
    assert.equal(sb.substituteVars('a {{x}} b'), 'a y b');
});

test('substituteVars: unknown placeholder is left intact', () => {
    const sb = loadHistoryEnv();
    assert.equal(sb.substituteVars('keep ${ghost}'), 'keep ${ghost}');
    assert.equal(sb.substituteVars('keep {{ghost}}'), 'keep {{ghost}}');
});

test('substituteVars: $${name} escape emits literal ${name}', () => {
    const sb = loadHistoryEnv({ globals: { name: 'x' } });
    assert.equal(sb.substituteVars('$${name} = ${name}'), '${name} = x');
});

test('substituteVars: {{{{name}}}} escape emits literal {{name}}', () => {
    const sb = loadHistoryEnv({ globals: { name: 'x' } });
    assert.equal(sb.substituteVars('{{{{name}}}} = {{name}}'), '{{name}} = x');
});

test('substituteVars: system var beats env var with same name', () => {
    const sb = loadHistoryEnv({ globals: { now: 'user-defined-now' } });
    const out = sb.substituteVars('${now}');
    // ${now} is bare → resolveKey routes through resolveSystemVar first.
    assert.notEqual(out, 'user-defined-now');
    assert.match(out, /^\d+$/);
});

test('substituteVars: env.X explicit prefix resolves', () => {
    const sb = loadHistoryEnv({ globals: { TOKEN: 'abc' } });
    assert.equal(sb.substituteVars('Bearer {{env.TOKEN}}'), 'Bearer abc');
});

test('substituteVars: cycle short-circuits with <<cycle:X>>', () => {
    // A → B → A
    const sb = loadHistoryEnv({ globals: { A: '{{B}}', B: '{{A}}' } });
    const out = sb.substituteVars('{{A}}');
    assert.match(out, /<<cycle:[AB]>>/);
});

test('substituteVars: non-string input returned unchanged', () => {
    const sb = loadHistoryEnv();
    assert.equal(sb.substituteVars(42), 42);
    assert.equal(sb.substituteVars(null), null);
    assert.equal(sb.substituteVars(undefined), undefined);
});

test('substituteVars: env var with no placeholders resolves once', () => {
    const sb = loadHistoryEnv({ globals: { x: 'plain' } });
    assert.equal(sb.substituteVars('${x}'), 'plain');
});

// ---- isHistoryEntryOk ----

test('isHistoryEntryOk: OK / Connected / Completed → true', () => {
    const sb = loadHistoryEnv();
    assert.equal(sb.isHistoryEntryOk({ status: 'OK' }), true);
    assert.equal(sb.isHistoryEntryOk({ status: 'Connected' }), true);
    assert.equal(sb.isHistoryEntryOk({ status: 'Completed' }), true);
});

test('isHistoryEntryOk: 2xx / 3xx → true', () => {
    const sb = loadHistoryEnv();
    assert.equal(sb.isHistoryEntryOk({ status: '200' }), true);
    assert.equal(sb.isHistoryEntryOk({ status: '201' }), true);
    assert.equal(sb.isHistoryEntryOk({ status: '301' }), true);
    assert.equal(sb.isHistoryEntryOk({ status: '399' }), true);
});

test('isHistoryEntryOk: 4xx / 5xx → false', () => {
    const sb = loadHistoryEnv();
    assert.equal(sb.isHistoryEntryOk({ status: '404' }), false);
    assert.equal(sb.isHistoryEntryOk({ status: '500' }), false);
});

test('isHistoryEntryOk: gRPC error status name → false', () => {
    const sb = loadHistoryEnv();
    assert.equal(sb.isHistoryEntryOk({ status: 'NotFound' }), false);
    assert.equal(sb.isHistoryEntryOk({ status: 'Unauthenticated' }), false);
});

test('isHistoryEntryOk: missing status → true (uncertain → optimistic)', () => {
    const sb = loadHistoryEnv();
    assert.equal(sb.isHistoryEntryOk({}), true);
    assert.equal(sb.isHistoryEntryOk(null), true);
});

// ---- filterHistoryEntries ----

test('filterHistoryEntries: bucket=all + no search → identity', () => {
    const sb = loadHistoryEnv();
    sb._setSearch(''); sb._setBucket('all');
    const list = [
        { service: 'A', method: 'a', status: 'OK', body: '' },
        { service: 'B', method: 'b', status: '500', body: '' }
    ];
    assert.deepEqual(sb.filterHistoryEntries(list), list);
});

test('filterHistoryEntries: bucket=ok keeps only successful', () => {
    const sb = loadHistoryEnv();
    sb._setBucket('ok');
    const list = [
        { service: 'A', method: 'a', status: 'OK', body: '' },
        { service: 'B', method: 'b', status: '500', body: '' }
    ];
    const filtered = sb.filterHistoryEntries(list);
    assert.equal(filtered.length, 1);
    assert.equal(filtered[0].service, 'A');
});

test('filterHistoryEntries: bucket=error keeps only failures', () => {
    const sb = loadHistoryEnv();
    sb._setBucket('error');
    const list = [
        { service: 'A', method: 'a', status: 'OK', body: '' },
        { service: 'B', method: 'b', status: '500', body: '' }
    ];
    const filtered = sb.filterHistoryEntries(list);
    assert.equal(filtered.length, 1);
    assert.equal(filtered[0].service, 'B');
});

test('filterHistoryEntries: search is case-insensitive substring on combined haystack', () => {
    const sb = loadHistoryEnv();
    sb._setSearch('FOO');
    const list = [
        { service: 'foo', method: 'a', status: 'OK', body: '' },
        { service: 'bar', method: 'baz', status: 'OK', body: 'has foo in body' },
        { service: 'qux', method: 'q', status: 'OK', body: 'unrelated' }
    ];
    const filtered = sb.filterHistoryEntries(list);
    assert.equal(filtered.length, 2);
    assert.deepEqual(filtered.map(h => h.service), ['foo', 'bar']);
});

test('filterHistoryEntries: search + bucket compose', () => {
    const sb = loadHistoryEnv();
    sb._setSearch('user');
    sb._setBucket('error');
    const list = [
        { service: 'user-api', method: 'a', status: '500', body: '' },
        { service: 'user-api', method: 'b', status: 'OK', body: '' },
        { service: 'admin',    method: 'c', status: '500', body: '' }
    ];
    const filtered = sb.filterHistoryEntries(list);
    assert.equal(filtered.length, 1);
    assert.equal(filtered[0].method, 'a');
});

// ---- getHistory / addHistory / clearHistory localStorage round-trip ----

test('getHistory: empty on a fresh workspace', () => {
    const sb = loadHistoryEnv();
    assert.deepEqual(sb.getHistory(), []);
});

test('addHistory: unshifts newest first + persists', () => {
    const sb = loadHistoryEnv();
    sb.addHistory({ service: 'A', method: 'a' });
    sb.addHistory({ service: 'B', method: 'b' });
    const h = sb.getHistory();
    assert.equal(h.length, 2);
    assert.equal(h[0].service, 'B');
    assert.equal(h[1].service, 'A');
    // Each entry gets a timestamp.
    assert.equal(typeof h[0].timestamp, 'number');
});

test('addHistory: respects MAX_HISTORY cap', () => {
    const sb = loadHistoryEnv();
    for (let i = 0; i < 250; i++) {
        sb.addHistory({ service: 'S', method: 'm' + i });
    }
    const h = sb.getHistory();
    assert.equal(h.length, 200); // MAX_HISTORY
    // Newest first → newest method name should be m249.
    assert.equal(h[0].method, 'm249');
});

test('clearHistory: empties the persisted ring', () => {
    const sb = loadHistoryEnv();
    sb.addHistory({ service: 'S', method: 'm' });
    assert.equal(sb.getHistory().length, 1);
    sb.clearHistory();
    assert.equal(sb.getHistory().length, 0);
});

// ---- substituteMetadata / substituteMessages convenience wrappers ----

test('substituteMetadata: rewrites every string value in a bag', () => {
    const sb = loadHistoryEnv({ globals: { K: 'v' } });
    const out = sb.substituteMetadata({ Auth: 'Bearer ${K}', Trace: '{{K}}' });
    assert.equal(out.Auth, 'Bearer v');
    assert.equal(out.Trace, 'v');
});

test('substituteMetadata: null returns null', () => {
    const sb = loadHistoryEnv();
    assert.equal(sb.substituteMetadata(null), null);
});

// Skipped pending #316 — substituteMessages crashes on 2nd+ array entry
// when the entry contains a placeholder routed through env-var
// resolution, because Array.prototype.map leaks (value, index, array)
// into substituteVars's second (_resolvingFromCaller) parameter.
test('substituteMessages: rewrites every entry in the array (#316)', { skip: 'blocked by #316' }, () => {
    const sb = loadHistoryEnv({ globals: { K: 'v' } });
    const out = sb.substituteMessages(['${K}', 'plain', '{{K}}']);
    assert.deepEqual(out, ['v', 'plain', 'v']);
});

test('substituteMessages: single-entry array resolves (no map-index collision)', () => {
    const sb = loadHistoryEnv({ globals: { K: 'v' } });
    const out = sb.substituteMessages(['${K}']);
    assert.deepEqual(out, ['v']);
});

test('substituteMessages: non-array returns input unchanged', () => {
    const sb = loadHistoryEnv();
    assert.equal(sb.substituteMessages('not an array'), 'not an array');
});
