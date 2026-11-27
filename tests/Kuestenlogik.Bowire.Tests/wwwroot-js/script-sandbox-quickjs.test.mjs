// #1062 — script-sandbox.js: the workbench runs a script in QuickJS, never in
// the page. The conformance fixtures run through it as a third engine (beside
// scripts.js in Node and Jint on the server); the limits stop a runaway
// script; the result lands on the caller's objects; a sandbox that did not
// load is a failure that says why.

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, resolve } from 'node:path';
import { compileFragment } from './_load-fragment.mjs';
import { loadBowireQuickJS, sandboxFetch, SCRIPTS_JS } from './_quickjs.mjs';

const __dirname = dirname(fileURLToPath(import.meta.url));
const fixtures = JSON.parse(readFileSync(resolve(__dirname, '../Fixtures/scripts/conformance.json'), 'utf8'));
const BowireQuickJS = loadBowireQuickJS();

// The page's side: script-sandbox.js, with scripts.js beside it for the
// console it logs into, as in the bundle.
const load = compileFragment(
    '../../../src/Kuestenlogik.Bowire/wwwroot/js/script-sandbox.js',
    ['window', 'fetch', 'config'],
    SCRIPTS_JS + `
        var document = { head: { appendChild: function () { throw new Error('no script tag in this test'); } }, createElement: function () { return {}; } };
        return { ensure: ensureScriptSandbox, run: runSandboxedScript, consoleOf: getScriptConsole };`);

async function sandbox(config, fetchImpl) {
    const api = load({
        window: { BowireQuickJS },
        fetch: fetchImpl || sandboxFetch(),
        config: Object.assign({ prefix: '', version: 'test' }, config || {}),
    });
    const ready = await api.ensure();
    return { api, ready };
}

const { api } = await sandbox();

fixtures.cases.forEach((c, index) => {
    if (c.serverOnly) return;
    test(`conformance in QuickJS: ${c.name}`, () => {
        const request = structuredClone(c.request);
        const env = structuredClone(c.env || {});
        const captured = structuredClone(c.captured || {});
        const svc = 'conformance-qjs', mth = String(index);
        const result = api.run(c.phase, {
            source: c.source, protocolShape: c.shape, service: svc, method: mth,
            request, response: c.response ? structuredClone(c.response) : undefined, env, captured,
        });
        const e = c.expect;
        assert.equal(!!result.ok, e.ok);
        if ('assertFail' in e) assert.equal(!!result.assertFail, e.assertFail);
        if ('error' in e) assert.equal(result.error, e.error);
        if ('errorContains' in e) assert.match(result.error || '', new RegExp(e.errorContains));
        if (c.phase === 'pre') {
            for (const [field, value] of Object.entries(e.request || {})) {
                assert.deepEqual(request[field], value, `request.${field}`);
            }
        }
        if (e.env) assert.deepEqual(env, e.env);
        if (e.captured) assert.deepEqual(captured, e.captured);
        const log = api.consoleOf(svc, mth).map((x) => x.message);
        if (e.log) assert.deepEqual(log.slice(0, e.log.length), e.log);
    });
});

test('a script cannot reach the page: no window of the page, no fetch, no storage', () => {
    const r = api.run('pre', {
        source: "ctx.log(typeof fetch, typeof localStorage, typeof document, typeof XMLHttpRequest, typeof window.BowireQuickJS);",
        protocolShape: 'rest', service: 'reach', method: 'm', request: { headers: {}, query: {} }, env: {}, captured: {},
    });
    assert.equal(r.ok, true);
    assert.deepEqual(api.consoleOf('reach', 'm').map((x) => x.message), ['undefined undefined undefined undefined undefined']);
});

test('env writes go through onEnvSet, the captured bag changes in place', () => {
    const writes = [];
    const captured = { keep: 1 };
    const bag = captured;
    const r = api.run('post', {
        source: "ctx.env.set('token', 'abc'); ctx.vars.captured.id = ctx.response.json().id; delete ctx.vars.captured.keep;",
        protocolShape: 'rest', service: 's', method: 'm',
        request: { headers: {}, query: {} }, response: { body: '{"id":"o-1"}', status: 'OK', headers: {} },
        env: { token: 'old' }, captured, onEnvSet: (k, v) => writes.push([k, v]),
    });
    assert.equal(r.ok, true);
    assert.deepEqual(writes, [['token', 'abc']]);
    assert.equal(bag, captured);
    assert.deepEqual(captured, { id: 'o-1' });
});

test('a runaway loop is stopped at the configured time', async () => {
    const { api: fast } = await sandbox({ scriptLimits: { timeoutMs: 200, memoryBytes: 64 * 1024 * 1024 } });
    const started = Date.now();
    const r = fast.run('pre', { source: 'for (;;) {}', service: 'loop', method: 'm', request: { headers: {}, query: {} } });
    assert.equal(r.ok, false);
    assert.equal(r.error, 'the script ran longer than 0.2 s and was stopped');
    assert.ok(Date.now() - started < 5000);
});

test('an allocation storm is stopped at the memory ceiling', async () => {
    const { api: small } = await sandbox({ scriptLimits: { timeoutMs: 5000, memoryBytes: 8 * 1024 * 1024 } });
    const r = small.run('pre', {
        // Doubling an array reaches 8 MB in a couple of dozen steps, so the
        // memory ceiling is what stops it, never the clock. (A doubling
        // string hits QuickJS's string length limit first.)
        source: "var a = [1]; for (;;) { a = a.concat(a); }",
        service: 'mem', method: 'm', request: { headers: {}, query: {} },
    });
    assert.equal(r.ok, false);
    assert.match(r.error, /out of memory|allocated more than 8 MB and was stopped/);
});

test('unbounded recursion fails the script; after the handshake the next one runs, without fetching again', async () => {
    // A sandbox of its own: the interpreter may be replaced after this script.
    let fetches = 0;
    const counting = sandboxFetch();
    const { api: own } = await sandbox({}, (url, init) => { fetches++; return counting(url, init); });
    assert.equal(fetches, 2);

    const r = own.run('pre', { source: 'function f() { return f() + 1; } f();', service: 'rec', method: 'm', request: { headers: {}, query: {} } });
    assert.equal(r.ok, false);
    assert.match(r.error, /stack overflow|recursed too deeply/);

    // The callers' handshake: await the sandbox, then run.
    assert.ok(await own.ensure());
    const next = own.run('pre', { source: "ctx.log('fine');", service: 'rec2', method: 'm', request: { headers: {}, query: {} } });
    assert.equal(next.ok, true);
    assert.equal(fetches, 2);
});

test('a sandbox that did not load fails the script and says why', async () => {
    const { api: broken, ready } = await sandbox({}, () => Promise.resolve({ ok: false, status: 503 }));
    assert.equal(ready, null);
    const r = broken.run('pre', { source: "ctx.log('x');", service: 'nl', method: 'm', request: { headers: {}, query: {} } });
    assert.equal(r.ok, false);
    assert.equal(r.error, 'The script sandbox could not be loaded: quickjs.wasm: HTTP 503');
    assert.deepEqual(broken.consoleOf('nl', 'm').map((x) => x.message), [r.error]);
});

test('an empty script is not run', () => {
    assert.deepEqual(api.run('pre', { source: '   ', request: {} }), { ok: true });
});
