// #943 — collections.js's runCollectionItem judges a call by what
// /api/invoke says about it. A failed call comes back as HTTP 200 with
// status "Error" and the reason in metadata.error; it must fail the item,
// not pass it.

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { compileFragment } from './_load-fragment.mjs';

const _host = `
        var config = { prefix: '' };
        var serverUrls = [];
        function substituteVars(s) { return s; }
        function resolveSourceUrl(u) { return u; }
        function resolveItemInvocationUrl(item, schemaUrl) { return schemaUrl; }
        var localStorage = { getItem: function () { return null; }, setItem: function () {} };
        var fetch = function () {
            return Promise.resolve({ ok: __harness.ok, json: function () { return Promise.resolve(__harness.body); } });
        };
        return { run: runCollectionItem };
`;

const load = compileFragment('../../../src/Kuestenlogik.Bowire/wwwroot/js/collections.js', ['__harness'], _host);
const item = { id: 'ci_1', protocol: 'rest', service: 'http://127.0.0.1:9/x', method: 'GET', methodType: 'Unary', serverUrl: 'http://127.0.0.1:9/x' };

test('a call /api/invoke reports as failed fails the item, with the reason', async () => {
    const f = load({ __harness: { ok: true, body: { status: 'Error', duration_ms: 0, metadata: { error: 'No OpenAPI document found at http://127.0.0.1:9/x' } } } });
    const r = await f.run(item);
    assert.equal(r.pass, false);
    assert.equal(r.status, 'Error');
    assert.equal(r.error, 'No OpenAPI document found at http://127.0.0.1:9/x');
});

test('a call that went through passes, whatever business status it carries', async () => {
    const ok = load({ __harness: { ok: true, body: { status: 'OK', duration_ms: 3, response: '{}' } } });
    assert.equal((await ok.run(item)).pass, true);
    const notFound = load({ __harness: { ok: true, body: { status: 'NotFound', duration_ms: 3, response: '{}' } } });
    assert.equal((await notFound.run(item)).pass, true);
});

test('a problem response still fails the item', async () => {
    const f = load({ __harness: { ok: false, body: { title: 'Target not allowed' } } });
    const r = await f.run(item);
    assert.equal(r.pass, false);
    assert.equal(r.error, 'Target not allowed');
});
