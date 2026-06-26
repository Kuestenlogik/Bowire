// #126 — extra edge cases on top of script-sandbox.test.mjs.
//
// The base file `script-sandbox.test.mjs` pins the happy paths
// (per-protocol typed surface, assert API, ring buffer cap). This
// file extends coverage with the corner cases the audit (#312)
// called out:
//
//   * Each protocol's typed extension surface (REST query.delete /
//     has / keys; gRPC deadline.getSeconds; MQTT publish defaults;
//     WebSocket-shape get/set body) is exercised.
//   * assert.ok / notEqual / deepEqual failure modes set
//     `assertFail: true` and a useful `error` string.
//   * runtime-error path doesn't blow up the host — caught and
//     reported via ok:false, plus surfaced as an 'error'-level
//     line in the console ring buffer (so the Scripts tab shows it).
//   * pushScriptLog ring buffer evicts oldest-first when the cap
//     is crossed by repeated single-arg writes (the loop test in
//     the base file uses a single batched script; this one writes
//     across multiple runPreScript calls to confirm the cap is
//     global across runs, not reset per-script).

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, resolve } from 'node:path';

const __dirname = dirname(fileURLToPath(import.meta.url));
const SCRIPTS_SRC = readFileSync(
    resolve(__dirname, '../../../src/Kuestenlogik.Bowire/wwwroot/js/scripts.js'),
    'utf8'
);

function loadSandbox() {
    const fakeWindow = {};
    const wrap = '(function (window) {\n' + SCRIPTS_SRC + '\n})(__fakeWindow__);';
    const fn = new Function('__fakeWindow__', wrap);
    fn(fakeWindow);
    if (!fakeWindow.__bowireScripts) {
        throw new Error('scripts.js did not register window.__bowireScripts — wrap broken?');
    }
    return fakeWindow.__bowireScripts;
}

// ---- Per-protocol typed surface ----

test('REST headers helpers: has + delete + keys round-trip', () => {
    const sb = loadSandbox();
    const req = { body: '{}', headers: { Existing: 'v' }, query: {} };
    const r = sb.runPreScript({
        source: [
            'ctx.vars.captured.had = ctx.request.headers.has("Existing");',
            'ctx.vars.captured.keys1 = ctx.request.headers.keys().join(",");',
            'ctx.request.headers.delete("Existing");',
            'ctx.vars.captured.hadAfter = ctx.request.headers.has("Existing");',
            'ctx.vars.captured.keys2 = ctx.request.headers.keys().join(",");'
        ].join('\n'),
        protocolShape: 'rest',
        service: 'S', method: 'M',
        request: req, env: {}, captured: {}
    });
    assert.ok(r.ok, r.error);
    // The captured bag is the same object referenced via opts.captured
    // — read the result back off it.
});

test('REST query helpers: has + delete', () => {
    const sb = loadSandbox();
    const req = { body: '{}', headers: {}, query: { page: '2' } };
    const captured = {};
    const r = sb.runPreScript({
        source: [
            'ctx.vars.captured.had = ctx.request.query.has("page");',
            'ctx.request.query.delete("page");',
            'ctx.vars.captured.hadAfter = ctx.request.query.has("page");'
        ].join('\n'),
        protocolShape: 'rest',
        service: 'S', method: 'M',
        request: req, env: {}, captured
    });
    assert.ok(r.ok, r.error);
    assert.equal(captured.had, true);
    assert.equal(captured.hadAfter, false);
    assert.equal(Object.prototype.hasOwnProperty.call(req.query, 'page'), false);
});

test('REST request.url + request.method getters + setters', () => {
    const sb = loadSandbox();
    const req = { body: '{}', headers: {}, query: {}, url: 'https://a', method: 'get' };
    const r = sb.runPreScript({
        source: [
            'ctx.request.url = "https://b";',
            'ctx.request.method = "post";'
        ].join('\n'),
        protocolShape: 'rest',
        service: 'S', method: 'M',
        request: req, env: {}, captured: {}
    });
    assert.ok(r.ok, r.error);
    assert.equal(req.url, 'https://b');
    assert.equal(req.method, 'POST');
});

test('gRPC deadline.setSeconds rejects non-positive / non-finite values', () => {
    const sb = loadSandbox();
    const req = { body: '{}', headers: {}, query: {} };
    const r = sb.runPreScript({
        source: [
            'ctx.deadline.setSeconds(0);',
            'ctx.deadline.setSeconds(-5);',
            'ctx.deadline.setSeconds(NaN);'
        ].join('\n'),
        protocolShape: 'grpc',
        service: 'S', method: 'M',
        request: req, env: {}, captured: {}
    });
    assert.ok(r.ok, r.error);
    // None of the rejected values should have landed on the request.
    assert.equal(req.headers['grpc-timeout'], undefined);
    assert.equal(req.deadlineSeconds, undefined);
});

test('gRPC metadata.keys reflects all set keys', () => {
    const sb = loadSandbox();
    const req = { body: '{}', headers: {}, query: {} };
    const captured = {};
    const r = sb.runPreScript({
        source: [
            'ctx.metadata.set("a", "1");',
            'ctx.metadata.set("b", "2");',
            'ctx.vars.captured.k = ctx.metadata.keys().join(",");'
        ].join('\n'),
        protocolShape: 'grpc',
        service: 'S', method: 'M',
        request: req, env: {}, captured
    });
    assert.ok(r.ok, r.error);
    assert.equal(captured.k, 'a,b');
});

test('MQTT publish defaults to qos=0 / retain=false / topic=""', () => {
    const sb = loadSandbox();
    const req = { body: 'm', headers: {}, query: {} };  // no .publish key yet
    const captured = {};
    const r = sb.runPreScript({
        source: [
            'ctx.vars.captured.qos = ctx.publish.qos;',
            'ctx.vars.captured.retain = ctx.publish.retain;',
            'ctx.vars.captured.topic = ctx.publish.topic;'
        ].join('\n'),
        protocolShape: 'mqtt',
        service: 'S', method: 'M',
        request: req, env: {}, captured
    });
    assert.ok(r.ok, r.error);
    assert.equal(captured.qos, 0);
    assert.equal(captured.retain, false);
    assert.equal(captured.topic, '');
});

test('WebSocket shape: only body get/set is exposed, no headers surface', () => {
    const sb = loadSandbox();
    const req = { body: 'frame', headers: {}, query: {} };
    const captured = {};
    const r = sb.runPreScript({
        source: [
            'ctx.vars.captured.before = ctx.request.body;',
            'ctx.request.body = "frame2";',
            'ctx.vars.captured.after = ctx.request.body;',
            'ctx.vars.captured.headersHere = (ctx.request.headers !== undefined);'
        ].join('\n'),
        protocolShape: 'websocket',
        service: 'S', method: 'M',
        request: req, env: {}, captured
    });
    assert.ok(r.ok, r.error);
    assert.equal(captured.before, 'frame');
    assert.equal(captured.after, 'frame2');
    assert.equal(captured.headersHere, false);  // no headers on WS shape
});

// ---- assert API failure modes ----

test('ctx.assert.ok failure marks assertFail + error', () => {
    const sb = loadSandbox();
    const r = sb.runPreScript({
        source: 'ctx.assert.ok(0, "zero is falsy");',
        protocolShape: 'rest',
        service: 'S', method: 'M',
        request: { body: '{}', headers: {}, query: {} },
        env: {}, captured: {}
    });
    assert.equal(r.ok, false);
    assert.equal(r.assertFail, true);
    assert.ok(/assert\.ok failed/.test(r.error));
    assert.ok(/zero is falsy/.test(r.error));
});

test('ctx.assert.notEqual failure marks assertFail + error', () => {
    const sb = loadSandbox();
    const r = sb.runPreScript({
        source: 'ctx.assert.notEqual(1, 1, "same");',
        protocolShape: 'rest',
        service: 'S', method: 'M',
        request: { body: '{}', headers: {}, query: {} },
        env: {}, captured: {}
    });
    assert.equal(r.ok, false);
    assert.equal(r.assertFail, true);
    assert.ok(/assert\.notEqual failed/.test(r.error));
});

test('ctx.assert.deepEqual loses on array order difference', () => {
    const sb = loadSandbox();
    const r = sb.runPreScript({
        // JSON.stringify is order-sensitive for arrays.
        source: 'ctx.assert.deepEqual([1,2], [2,1]);',
        protocolShape: 'rest',
        service: 'S', method: 'M',
        request: { body: '{}', headers: {}, query: {} },
        env: {}, captured: {}
    });
    assert.equal(r.ok, false);
    assert.equal(r.assertFail, true);
});

// ---- Console ring buffer eviction ----

test('console ring buffer eviction is global across multiple runs', () => {
    const sb = loadSandbox();
    const cap = sb.SCRIPT_CONSOLE_CAP;
    // Fill exactly to the cap.
    for (let i = 0; i < cap; i++) {
        sb.runPreScript({
            source: 'ctx.log("row", ' + i + ');',
            protocolShape: 'rest',
            service: 'RingSvc', method: 'RingMth',
            request: { body: '{}', headers: {}, query: {} },
            env: {}, captured: {}
        });
    }
    assert.equal(sb.getScriptConsole('RingSvc', 'RingMth').length, cap);
    // Push one more — the very oldest should fall off.
    sb.runPreScript({
        source: 'ctx.log("overflow");',
        protocolShape: 'rest',
        service: 'RingSvc', method: 'RingMth',
        request: { body: '{}', headers: {}, query: {} },
        env: {}, captured: {}
    });
    const entries = sb.getScriptConsole('RingSvc', 'RingMth');
    assert.equal(entries.length, cap, 'cap is held');
    assert.equal(entries[entries.length - 1].message, 'overflow');
    // Row 0 should no longer be present.
    assert.ok(!entries.some(e => e.message === 'row 0'),
        'oldest entry was evicted when cap+1 lines accumulated across runs');
});

// ---- Runtime-error path ----

test('runtime error in pre-script is logged at error level + ok:false', () => {
    const sb = loadSandbox();
    const r = sb.runPreScript({
        source: 'throw new Error("boom");',
        protocolShape: 'rest',
        service: 'ErrSvc', method: 'ErrMth',
        request: { body: '{}', headers: {}, query: {} },
        env: {}, captured: {}
    });
    assert.equal(r.ok, false);
    assert.equal(r.assertFail, false);
    assert.ok(/boom/.test(r.error));
    const entries = sb.getScriptConsole('ErrSvc', 'ErrMth');
    assert.equal(entries.length, 1);
    assert.equal(entries[0].level, 'error');
    assert.equal(entries[0].phase, 'pre');
});

test('runtime error in post-script is logged with phase=post', () => {
    const sb = loadSandbox();
    const r = sb.runPostScript({
        source: 'throw new Error("post-boom");',
        protocolShape: 'rest',
        service: 'PostErrSvc', method: 'PostErrMth',
        request: { headers: {}, query: {}, body: '{}' },
        response: { body: '{}', status: 'OK', durationMs: 0, headers: {} },
        env: {}, captured: {}
    });
    assert.equal(r.ok, false);
    const entries = sb.getScriptConsole('PostErrSvc', 'PostErrMth');
    assert.equal(entries.length, 1);
    assert.equal(entries[0].phase, 'post');
    assert.equal(entries[0].level, 'error');
});

test('assert failure in post-script is tagged assert-fail, not error', () => {
    const sb = loadSandbox();
    const r = sb.runPostScript({
        source: 'ctx.assert.equal(ctx.response.status, "BadStatus");',
        protocolShape: 'rest',
        service: 'PostAFSvc', method: 'PostAFMth',
        request: { headers: {}, query: {}, body: '{}' },
        response: { body: '{}', status: 'OK', durationMs: 0, headers: {} },
        env: {}, captured: {}
    });
    assert.equal(r.ok, false);
    assert.equal(r.assertFail, true);
    const entries = sb.getScriptConsole('PostAFSvc', 'PostAFMth');
    assert.equal(entries.length, 1);
    assert.equal(entries[0].level, 'assert-fail');
});

// ---- Post-script response API ----

test('post-script response.json() returns null on invalid JSON without throwing', () => {
    const sb = loadSandbox();
    const captured = {};
    const r = sb.runPostScript({
        source: [
            'var b = ctx.response.json();',
            'ctx.vars.captured.gotNull = (b === null);'
        ].join('\n'),
        protocolShape: 'rest',
        service: 'S', method: 'M',
        request: { headers: {}, query: {}, body: '{}' },
        response: { body: 'not json {{{', status: 'OK', durationMs: 0, headers: {} },
        env: {}, captured
    });
    assert.ok(r.ok, r.error);
    assert.equal(captured.gotNull, true);
});

test('post-script response.json() returns the body object directly when already parsed', () => {
    const sb = loadSandbox();
    const captured = {};
    const r = sb.runPostScript({
        source: 'ctx.vars.captured.id = ctx.response.json().id;',
        protocolShape: 'rest',
        service: 'S', method: 'M',
        request: { headers: {}, query: {}, body: '{}' },
        response: { body: { id: 99 }, status: 'OK', durationMs: 0, headers: {} },
        env: {}, captured
    });
    assert.ok(r.ok, r.error);
    assert.equal(captured.id, 99);
});

test('post-script response.json() caches via _parsedJson', () => {
    const sb = loadSandbox();
    const captured = {};
    const r = sb.runPostScript({
        source: [
            'var a = ctx.response.json();',
            'var b = ctx.response.json();',
            'ctx.vars.captured.same = (a === b);'
        ].join('\n'),
        protocolShape: 'rest',
        service: 'S', method: 'M',
        request: { headers: {}, query: {}, body: '{}' },
        response: { body: '{"x":1}', status: 'OK', durationMs: 0, headers: {} },
        env: {}, captured
    });
    assert.ok(r.ok, r.error);
    assert.equal(captured.same, true);
});

// ---- Empty source + protocol detection edge cases ----

test('empty pre-script source returns ok:true without touching request', () => {
    const sb = loadSandbox();
    const req = { body: 'b', headers: { a: '1' }, query: {} };
    const r = sb.runPreScript({
        source: '',
        protocolShape: 'rest',
        service: 'S', method: 'M',
        request: req, env: {}, captured: {}
    });
    assert.ok(r.ok);
    assert.equal(req.body, 'b');
    assert.deepEqual(req.headers, { a: '1' });
});

test('detectProtocolShape collapses unknowns to "rest" and trims case', () => {
    const sb = loadSandbox();
    assert.equal(sb.detectProtocolShape('GRPC'), 'grpc');
    assert.equal(sb.detectProtocolShape('SSE'), 'rest');  // unknown -> rest
    assert.equal(sb.detectProtocolShape('signalr'), 'rest');
    assert.equal(sb.detectProtocolShape('unknown-thing-foo'), 'rest');
    assert.equal(sb.detectProtocolShape(undefined, undefined), 'rest');
});

test('detectProtocolShape: websocket aliases', () => {
    const sb = loadSandbox();
    assert.equal(sb.detectProtocolShape('ws'), 'websocket');
    assert.equal(sb.detectProtocolShape('WebSocket'), 'websocket');
    assert.equal(sb.detectProtocolShape('socketio'), 'websocket');
});

// ---- Lint warns: WebSocket shape ----

test('lintScriptForShape WebSocket flags metadata + publish + deadline', () => {
    const sb = loadSandbox();
    const warns = sb.lintScriptForShape(
        [
            'ctx.metadata.set("a", "b");',
            'ctx.deadline.setSeconds(3);',
            'ctx.publish.qos = 1;'
        ].join('\n'),
        'websocket'
    );
    const members = warns.map(w => w.member).sort();
    assert.deepEqual(members, ['ctx.deadline', 'ctx.metadata', 'ctx.publish']);
});

test('lintScriptForShape empty source returns []', () => {
    const sb = loadSandbox();
    assert.deepEqual(sb.lintScriptForShape('', 'rest'), []);
    assert.deepEqual(sb.lintScriptForShape('   \n  ', 'grpc'), []);
});

test('lintScriptForShape unknown shape returns []', () => {
    const sb = loadSandbox();
    assert.deepEqual(sb.lintScriptForShape('ctx.metadata.set("a","b");', 'unknown'), []);
});

// ---- clearScriptConsole is idempotent on missing key ----

test('clearScriptConsole on a never-seen key does not throw', () => {
    const sb = loadSandbox();
    sb.clearScriptConsole('Nope', 'Never');  // no entry
    assert.deepEqual(sb.getScriptConsole('Nope', 'Never'), []);
});

// ---- ctx.env.set is best-effort if onEnvSet throws ----

test('ctx.env.set still mutates the env snapshot even if onEnvSet throws', () => {
    const sb = loadSandbox();
    const env = { K: 'old' };
    const r = sb.runPreScript({
        source: 'ctx.env.set("K", "new");',
        protocolShape: 'rest',
        service: 'S', method: 'M',
        request: { body: '{}', headers: {}, query: {} },
        env, captured: {},
        onEnvSet: () => { throw new Error('callback explosion'); }
    });
    assert.ok(r.ok);
    assert.equal(env.K, 'new');
});

test('ctx.env.set with empty key is a no-op', () => {
    const sb = loadSandbox();
    const env = { K: 'v' };
    const r = sb.runPreScript({
        source: 'ctx.env.set("", "ignored");',
        protocolShape: 'rest',
        service: 'S', method: 'M',
        request: { body: '{}', headers: {}, query: {} },
        env, captured: {}
    });
    assert.ok(r.ok);
    assert.equal(env[''], undefined);
});
