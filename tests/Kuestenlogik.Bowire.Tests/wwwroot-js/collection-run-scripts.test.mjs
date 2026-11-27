// #1060 — the workbench's collection runner runs the pre/post-request
// scripts a saved item carries, through the same scripts.js a tab uses:
// the pre-request script changes the request that goes to /api/invoke, a
// failing one stops the item before the call, a failing post-response
// script fails the item, and ctx.vars.captured is the session's bag.

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { compileFragment, readFragment } from './_load-fragment.mjs';
import { loadBowireQuickJS, sandboxFetch } from './_quickjs.mjs';

// #1062 — the scripts run where the workbench runs them: in QuickJS, through
// script-sandbox.js, with scripts.js beside it as in the bundle.
const SCRIPTS_SRC = readFragment('../../../src/Kuestenlogik.Bowire/wwwroot/js/scripts.js');
const SANDBOX_SRC = readFragment('../../../src/Kuestenlogik.Bowire/wwwroot/js/script-sandbox.js');
const BowireQuickJS = loadBowireQuickJS();

const _host = SCRIPTS_SRC + SANDBOX_SRC + `
        var config = { prefix: '', version: 'test' };
        var document = {};
        var serverUrls = [];
        function substituteVars(s) { return s; }
        function resolveSourceUrl(u) { return u; }
        function resolveItemInvocationUrl(item, schemaUrl) { return schemaUrl; }
        function getMergedVars() { return { apiKey: 'k-1' }; }
        function writeScriptEnvVar(key, value) { __harness.envWrites[key] = value; }
        var localStorage = { getItem: function () { return null; }, setItem: function () {} };
        var fetch = __sandboxFetch(function (url, init) {
            __harness.calls.push({ url: url, body: JSON.parse(init.body) });
            return Promise.resolve({ ok: true, json: function () { return Promise.resolve(__harness.body); } });
        });
        return { run: runCollectionItem };
`;

const compiled = compileFragment('../../../src/Kuestenlogik.Bowire/wwwroot/js/collections.js', ['__harness', 'window', '__sandboxFetch'], _host);
const load = (host) => compiled(Object.assign({ __sandboxFetch: sandboxFetch, window: { BowireQuickJS } }, host, { window: { BowireQuickJS } }));

function harness(body) {
    return { calls: [], envWrites: {}, body: body || { status: 'OK', duration_ms: 2, response: '{"id":"o-7","state":"open"}', metadata: {} } };
}

const base = {
    id: 'ci_1', protocol: 'rest', service: '', method: 'POST', methodType: 'Unary',
    serverUrl: 'http://api.local/orders', messages: ['{"item":"x"}'], metadata: { Accept: 'application/json' },
};

test('the pre-request script changes the request that goes out', async () => {
    const h = harness();
    const f = load({ __harness: h, window: {} });
    const r = await f.run(Object.assign({}, base, {
        preScript: "ctx.request.headers.set('X-Key', ctx.env.get('apiKey')); ctx.request.query.set('dry', 'true');"
            + " var b = JSON.parse(ctx.request.body); b.signed = true; ctx.request.body = JSON.stringify(b);",
    }));
    assert.equal(r.pass, true);
    assert.equal(h.calls.length, 1);
    assert.equal(h.calls[0].body.metadata['X-Key'], 'k-1');
    assert.equal(h.calls[0].body.metadata.Accept, 'application/json');
    assert.deepEqual(JSON.parse(h.calls[0].body.messages[0]), { item: 'x', signed: true });
    assert.equal(decodeURIComponent(h.calls[0].url.split('serverUrl=')[1]), 'http://api.local/orders?dry=true');
});

test('a failing pre-request script stops the item before the call', async () => {
    const h = harness();
    const f = load({ __harness: h, window: {} });
    const r = await f.run(Object.assign({}, base, { preScript: "throw new Error('no key');" }));
    assert.equal(r.pass, false);
    assert.equal(r.status, 'ScriptError');
    assert.equal(r.error, 'Pre-request script error: no key');
    assert.equal(h.calls.length, 0);
});

test('the post-response script reads the response; its failure fails the item', async () => {
    const ok = harness();
    const bag = {};
    const passing = await load({ __harness: ok, window: {} }).run(Object.assign({}, base, {
        postScript: "ctx.assert.equal(ctx.response.json().state, 'open'); ctx.vars.captured.orderId = ctx.response.json().id; ctx.env.set('last', 'o');",
    }), { capturedBag: bag });
    assert.equal(passing.pass, true);
    assert.equal(bag.orderId, 'o-7');
    assert.equal(ok.envWrites.last, 'o');

    const failing = await load({ __harness: harness(), window: {} }).run(Object.assign({}, base, {
        postScript: "ctx.assert.equal(ctx.response.json().state, 'closed', 'state');",
    }));
    assert.equal(failing.pass, false);
    assert.match(failing.error, /^Post-response script error: assert\.equal failed: state/);
});

test('an item without scripts goes out unchanged', async () => {
    const h = harness();
    const r = await load({ __harness: h, window: {} }).run(base);
    assert.equal(r.pass, true);
    assert.deepEqual(h.calls[0].body.metadata, { Accept: 'application/json' });
    assert.equal(decodeURIComponent(h.calls[0].url.split('serverUrl=')[1]), 'http://api.local/orders');
});
