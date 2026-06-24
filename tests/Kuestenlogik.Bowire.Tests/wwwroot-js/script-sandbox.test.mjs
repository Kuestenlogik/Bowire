// #126 — Pre/post-script sandbox unit tests.
//
// Loads wwwroot/js/scripts.js, wraps it in a synthetic IIFE so the
// fragment's declarations behave the same way they do inside the
// concatenated bundle, and exercises the runtime via the
// window.__bowireScripts handle.
//
// The fragment is normally concatenated INSIDE prologue.js's IIFE,
// so it can't be `import`'d directly. We read the source, wrap it
// in `(function () { ... })()`, and eval it against a fake `window`
// — same trick the contract tests in this directory use.

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
    // Wrap the fragment in a stand-alone IIFE that gives the
    // declarations a fresh module scope, exposing them via the
    // synthetic `window` object the script registers against.
    const fakeWindow = {};
    const wrap = '(function (window) {\n' + SCRIPTS_SRC + '\n})(__fakeWindow__);';
    // Use indirect eval so the wrap evaluates at module scope of
    // the new Function, not at the test's scope.
    const fn = new Function('__fakeWindow__', wrap);
    fn(fakeWindow);
    if (!fakeWindow.__bowireScripts) {
        throw new Error('scripts.js did not register window.__bowireScripts — wrap broken?');
    }
    return fakeWindow.__bowireScripts;
}

test('detectProtocolShape maps known protocol ids', () => {
    const sb = loadSandbox();
    assert.equal(sb.detectProtocolShape('rest'), 'rest');
    assert.equal(sb.detectProtocolShape('graphql'), 'rest');
    assert.equal(sb.detectProtocolShape('odata'), 'rest');
    assert.equal(sb.detectProtocolShape('grpc'), 'grpc');
    assert.equal(sb.detectProtocolShape('grpc-web'), 'grpc');
    assert.equal(sb.detectProtocolShape('mqtt'), 'mqtt');
    assert.equal(sb.detectProtocolShape('websocket'), 'websocket');
    assert.equal(sb.detectProtocolShape('socket.io'), 'websocket');
    assert.equal(sb.detectProtocolShape(null, 'rest'), 'rest');
    assert.equal(sb.detectProtocolShape('', 'grpc'), 'grpc');
    assert.equal(sb.detectProtocolShape(null, null), 'rest');
});

test('REST pre-script can mutate request headers + query', () => {
    const sb = loadSandbox();
    const req = { body: '{}', headers: {}, query: {} };
    const env = { TOKEN: 'abc' };
    const captured = {};
    const r = sb.runPreScript({
        source: [
            'ctx.request.headers.set("Authorization", "Bearer " + ctx.env.get("TOKEN"));',
            'ctx.request.query.set("trace", "xyz");',
            'ctx.vars.captured.startTime = 12345;'
        ].join('\n'),
        protocolShape: 'rest',
        service: 'S', method: 'M',
        request: req, env, captured
    });
    assert.ok(r.ok, r.error);
    assert.equal(req.headers.Authorization, 'Bearer abc');
    assert.equal(req.query.trace, 'xyz');
    assert.equal(captured.startTime, 12345);
});

test('gRPC pre-script can set metadata + deadline', () => {
    const sb = loadSandbox();
    const req = { body: '{}', headers: {}, query: {} };
    const r = sb.runPreScript({
        source: [
            'ctx.metadata.set("authorization", "Bearer t");',
            'ctx.deadline.setSeconds(7);'
        ].join('\n'),
        protocolShape: 'grpc',
        service: 'S', method: 'M',
        request: req, env: {}, captured: {}
    });
    assert.ok(r.ok, r.error);
    assert.equal(req.headers.authorization, 'Bearer t');
    assert.equal(req.headers['grpc-timeout'], '7S');
    assert.equal(req.deadlineSeconds, 7);
});

test('MQTT pre-script can set QoS + retain on the publish struct', () => {
    const sb = loadSandbox();
    const req = { body: 'msg', headers: {}, query: {} };
    const r = sb.runPreScript({
        source: 'ctx.publish.qos = 2; ctx.publish.retain = true;',
        protocolShape: 'mqtt',
        service: 'S', method: 'M',
        request: req, env: {}, captured: {}
    });
    assert.ok(r.ok, r.error);
    assert.equal(req.publish.qos, 2);
    assert.equal(req.publish.retain, true);
});

test('ctx.env.set invokes the persistence callback', () => {
    const sb = loadSandbox();
    const calls = [];
    const env = { EXISTING: 'old' };
    const r = sb.runPreScript({
        source: 'ctx.env.set("token", "new-token");',
        protocolShape: 'rest',
        service: 'S', method: 'M',
        request: { body: '{}', headers: {}, query: {} },
        env, captured: {},
        onEnvSet: (k, v) => calls.push([k, v])
    });
    assert.ok(r.ok);
    assert.equal(env.token, 'new-token');
    assert.deepEqual(calls, [['token', 'new-token']]);
});

test('ctx.assert.equal failure surfaces ok:false + assertFail flag', () => {
    const sb = loadSandbox();
    const r = sb.runPreScript({
        source: 'ctx.assert.equal(1, 2, "intentional");',
        protocolShape: 'rest',
        service: 'S', method: 'M',
        request: { body: '{}', headers: {}, query: {} },
        env: {}, captured: {}
    });
    assert.equal(r.ok, false);
    assert.equal(r.assertFail, true);
    assert.ok(r.error.includes('assert.equal failed'), r.error);
    assert.ok(r.error.includes('intentional'), r.error);
});

test('ctx.assert.deepEqual passes on equivalent shapes, fails on diff', () => {
    const sb = loadSandbox();
    const okR = sb.runPreScript({
        source: 'ctx.assert.deepEqual({ a: 1, b: [2, 3] }, { a: 1, b: [2, 3] });',
        protocolShape: 'rest',
        service: 'S', method: 'M',
        request: { body: '{}', headers: {}, query: {} },
        env: {}, captured: {}
    });
    assert.ok(okR.ok, okR.error);
    const badR = sb.runPreScript({
        source: 'ctx.assert.deepEqual({ a: 1 }, { a: 2 });',
        protocolShape: 'rest',
        service: 'S', method: 'M',
        request: { body: '{}', headers: {}, query: {} },
        env: {}, captured: {}
    });
    assert.equal(badR.ok, false);
    assert.equal(badR.assertFail, true);
});

test('ctx.log() writes to per-method ring buffer', () => {
    const sb = loadSandbox();
    sb.runPreScript({
        source: 'ctx.log("hello", { x: 1 });',
        protocolShape: 'rest',
        service: 'Svc', method: 'Mth',
        request: { body: '{}', headers: {}, query: {} },
        env: {}, captured: {}
    });
    const entries = sb.getScriptConsole('Svc', 'Mth');
    assert.equal(entries.length, 1);
    assert.equal(entries[0].phase, 'pre');
    assert.equal(entries[0].level, 'log');
    assert.equal(entries[0].message, 'hello {"x":1}');
});

test('ctx.log() ring buffer caps at SCRIPT_CONSOLE_CAP', () => {
    const sb = loadSandbox();
    const N = sb.SCRIPT_CONSOLE_CAP + 50;
    sb.runPreScript({
        source: 'for (var i = 0; i < ' + N + '; i++) ctx.log("row", i);',
        protocolShape: 'rest',
        service: 'CapSvc', method: 'CapMth',
        request: { body: '{}', headers: {}, query: {} },
        env: {}, captured: {}
    });
    const entries = sb.getScriptConsole('CapSvc', 'CapMth');
    assert.equal(entries.length, sb.SCRIPT_CONSOLE_CAP);
    // Newest entries survive; the oldest 50 should have been dropped.
    assert.ok(entries[entries.length - 1].message.endsWith(String(N - 1)));
});

test('post-script sees ctx.response with body / status / durationMs + json()', () => {
    const sb = loadSandbox();
    let result = null;
    const r = sb.runPostScript({
        source: [
            'var b = ctx.response.json();',
            'ctx.vars.captured.id = b && b.id;',
            'ctx.vars.captured.status = ctx.response.status;',
            'ctx.assert.equal(ctx.response.status, "OK");'
        ].join('\n'),
        protocolShape: 'rest',
        service: 'S', method: 'M',
        request: { headers: { ContentType: 'json' }, query: {}, body: '{}' },
        response: {
            body: '{"id":42}',
            status: 'OK',
            durationMs: 17,
            headers: {}
        },
        env: {}, captured: result = {}
    });
    assert.ok(r.ok, r.error);
    assert.equal(result.id, 42);
    assert.equal(result.status, 'OK');
});

test('post-script ctx.request is frozen (REST)', () => {
    const sb = loadSandbox();
    const r = sb.runPostScript({
        source: 'try { ctx.request.headers.foo = "bar"; ctx.vars.captured.threw = false; } catch (e) { ctx.vars.captured.threw = true; }',
        protocolShape: 'rest',
        service: 'S', method: 'M',
        request: { headers: { x: 'y' }, query: {}, body: '{}' },
        response: { body: '{}', status: 'OK', durationMs: 0, headers: {} },
        env: {}, captured: {}
    });
    assert.ok(r.ok, r.error);
});

test('lintScriptForShape flags wrong-protocol surfaces (REST)', () => {
    const sb = loadSandbox();
    const warns = sb.lintScriptForShape(
        'ctx.metadata.set("x", "y"); ctx.publish.qos = 1;',
        'rest'
    );
    const members = warns.map(w => w.member).sort();
    assert.deepEqual(members, ['ctx.metadata', 'ctx.publish']);
});

test('lintScriptForShape flags wrong-protocol surfaces (gRPC)', () => {
    const sb = loadSandbox();
    const warns = sb.lintScriptForShape(
        'ctx.request.headers.set("x", "y"); ctx.publish.qos = 1;',
        'grpc'
    );
    const members = warns.map(w => w.member).sort();
    assert.deepEqual(members, ['ctx.publish', 'ctx.request.headers']);
});

test('lintScriptForShape flags wrong-protocol surfaces (MQTT)', () => {
    const sb = loadSandbox();
    const warns = sb.lintScriptForShape(
        'ctx.metadata.set("x", "y"); ctx.request.headers.set("a", "b");',
        'mqtt'
    );
    const members = warns.map(w => w.member).sort();
    assert.deepEqual(members, ['ctx.metadata', 'ctx.request.headers']);
});

test('lintScriptForShape lets always-available surface through unwarned', () => {
    const sb = loadSandbox();
    const warns = sb.lintScriptForShape(
        'ctx.log("hi"); ctx.env.set("K", "V"); ctx.vars.captured.X = 1; ctx.assert.equal(1, 1);',
        'rest'
    );
    assert.deepEqual(warns, []);
});

test('runtime error in pre-script is caught and reported (not thrown out)', () => {
    const sb = loadSandbox();
    const r = sb.runPreScript({
        source: 'undefinedFunction();',
        protocolShape: 'rest',
        service: 'E', method: 'E',
        request: { body: '{}', headers: {}, query: {} },
        env: {}, captured: {}
    });
    assert.equal(r.ok, false);
    assert.equal(r.assertFail, false);
    assert.ok(r.error);
});

test('empty source is a no-op success (no work, no console)', () => {
    const sb = loadSandbox();
    const r = sb.runPreScript({
        source: '   ',
        protocolShape: 'rest',
        service: 'E2', method: 'E2',
        request: { body: '{}', headers: {}, query: {} },
        env: {}, captured: {}
    });
    assert.ok(r.ok);
    const entries = sb.getScriptConsole('E2', 'E2');
    assert.equal(entries.length, 0);
});

test('clearScriptConsole drops the buffer for a given method', () => {
    const sb = loadSandbox();
    sb.runPreScript({
        source: 'ctx.log("a"); ctx.log("b");',
        protocolShape: 'rest',
        service: 'CS', method: 'CSM',
        request: { body: '{}', headers: {}, query: {} },
        env: {}, captured: {}
    });
    assert.equal(sb.getScriptConsole('CS', 'CSM').length, 2);
    sb.clearScriptConsole('CS', 'CSM');
    assert.equal(sb.getScriptConsole('CS', 'CSM').length, 0);
});
