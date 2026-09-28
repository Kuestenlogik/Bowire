// #752 — one /api/protocols answer, taken into the workbench.
//
// The fetch used to be `if (resp.ok) protocols = …` inside a bare catch: any
// refusal left the list empty and said nothing, so the Protocols page read
// exactly like "no plugins installed". The browser test beside this one shows
// what the page does with a 429. What is pinned here is the rule underneath,
// including the half a browser test cannot reach cleanly: a refusal after a
// good pass keeps the list that pass loaded.

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, resolve } from 'node:path';

const __dirname = dirname(fileURLToPath(import.meta.url));
const api = readFileSync(
    resolve(__dirname, '../../../src/Kuestenlogik.Bowire/wwwroot/js/api.js'), 'utf8');

/**
 * applyProtocolsResponse with its two globals made local, so each test starts
 * from a known list and reads back what the function left.
 */
function load() {
    const sig = 'async function applyProtocolsResponse(resp) {';
    const start = api.indexOf(sig);
    assert.ok(start >= 0, 'applyProtocolsResponse not found in api.js');
    let depth = 0, i = api.indexOf('{', start);
    for (; i < api.length; i++) {
        if (api[i] === '{') depth++;
        else if (api[i] === '}') { depth--; if (depth === 0) { i++; break; } }
    }
    return new Function(`
        var protocols = [], protocolsLoadError = null;
        ${api.slice(start, i)}
        return {
            set(list, err) { protocols = list; protocolsLoadError = err; },
            async apply(resp) { await applyProtocolsResponse(resp); return { protocols, protocolsLoadError }; },
        };
    `)();
}

/** A fetch Response, as much of one as the function reads. */
function response(status, { body = null, retryAfter = null } = {}) {
    return {
        ok: status >= 200 && status < 300,
        status,
        headers: { get: name => (name === 'Retry-After' && retryAfter !== null) ? String(retryAfter) : null },
        json: async () => (typeof body === 'string' ? JSON.parse(body) : body),
    };
}

const LOADED = [{ id: 'rest' }, { id: 'grpc' }];

test('a good answer replaces the list and clears an earlier error', async () => {
    const f = load();
    f.set([], { status: 429, retryAfter: 30 });
    const r = await f.apply(response(200, { body: LOADED }));
    assert.deepEqual(r.protocols, LOADED);
    assert.equal(r.protocolsLoadError, null);
});

test('a 429 after a good pass keeps the list, and records the wait', async () => {
    // The half the browser test cannot reach: one refused request is not
    // news that the plugins went away.
    const f = load();
    f.set(LOADED, null);
    const r = await f.apply(response(429, { retryAfter: 42 }));
    assert.deepEqual(r.protocols, LOADED);
    assert.deepEqual(r.protocolsLoadError, { status: 429, retryAfter: 42 });
});

test('a 429 without Retry-After still names the throttle, just not the wait', async () => {
    const f = load();
    const r = await f.apply(response(429));
    assert.deepEqual(r.protocolsLoadError, { status: 429, retryAfter: null });
});

test('a server error is recorded with its status', async () => {
    const f = load();
    f.set(LOADED, null);
    const r = await f.apply(response(503));
    assert.deepEqual(r.protocols, LOADED);
    assert.deepEqual(r.protocolsLoadError, { status: 503, retryAfter: null });
});

test('no answer at all is status 0, not an empty success', async () => {
    const f = load();
    f.set(LOADED, null);
    const r = await f.apply(null);
    assert.deepEqual(r.protocols, LOADED);
    assert.deepEqual(r.protocolsLoadError, { status: 0, retryAfter: null });
});

test('a 200 that is not a list counts as no answer — half a list is not a list', async () => {
    const f = load();
    f.set(LOADED, null);
    const broken = await f.apply(response(200, { body: '{"oops":' }));
    assert.deepEqual(broken.protocols, LOADED);
    assert.equal(broken.protocolsLoadError.status, 0);

    const object = await f.apply(response(200, { body: { protocols: [] } }));
    assert.deepEqual(object.protocols, LOADED);
    assert.equal(object.protocolsLoadError.status, 0);
});

test('an empty list is a real answer: no plugins, and no error', async () => {
    // The only case in which the page may say "no protocol plugins".
    const f = load();
    f.set(LOADED, { status: 429, retryAfter: 5 });
    const r = await f.apply(response(200, { body: [] }));
    assert.deepEqual(r.protocols, []);
    assert.equal(r.protocolsLoadError, null);
});
