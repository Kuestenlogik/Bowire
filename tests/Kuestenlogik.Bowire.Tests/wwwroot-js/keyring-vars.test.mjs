// #208 Phase 5 — tests for the OS-keyring variable source's client half
// in prologue.js: the synchronous cache read (resolveKeyringVar) and the
// async batch prefetch (prefetchKeyringVars). The prefetch closes over
// isModuleEnabled / config / fetch / the two module-scope caches, so we
// snip both functions out of the fragment and run them against injected
// stand-ins — exactly the shape they see in the bundle.

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, resolve } from 'node:path';

const __dirname = dirname(fileURLToPath(import.meta.url));
const SRC = readFileSync(
    resolve(__dirname, '../../../src/Kuestenlogik.Bowire/wwwroot/js/prologue.js'),
    'utf8'
);

// Brace-match the function body, but skip braces that live inside string
// literals, regex literals (e.g. /\{\{keyring\.…\}\}/), and comments —
// prefetchKeyringVars carries a regex whose escaped braces would otherwise
// throw off a naive depth counter.
function extractFn(name, keyword = 'function') {
    const needle = `${keyword} ${name}(`;
    const start = SRC.indexOf(needle);
    assert.ok(start >= 0, `${needle} not found`);
    const open = SRC.indexOf('{', start);
    let depth = 0, i = open, prevSig = '';
    for (; i < SRC.length; i++) {
        const c = SRC[i], n = SRC[i + 1];
        if (c === '/' && n === '/') { i = SRC.indexOf('\n', i); if (i < 0) i = SRC.length; continue; }
        if (c === '/' && n === '*') { i = SRC.indexOf('*/', i + 2) + 1; continue; }
        if (c === '"' || c === "'" || c === '`') { i = skipString(SRC, i, c); prevSig = c; continue; }
        if (c === '/' && '=(,:[!&|?{;'.includes(prevSig)) { i = skipRegex(SRC, i); prevSig = '/'; continue; }
        if (c === '{') depth++;
        else if (c === '}') { depth--; if (depth === 0) { i++; break; } }
        if (!/\s/.test(c)) prevSig = c;
    }
    return SRC.slice(start, i);
}

function skipString(s, i, quote) {
    for (i++; i < s.length; i++) {
        if (s[i] === '\\') { i++; continue; }
        if (s[i] === quote) return i;
    }
    return s.length;
}

function skipRegex(s, i) {
    let inClass = false;
    for (i++; i < s.length; i++) {
        if (s[i] === '\\') { i++; continue; }
        if (s[i] === '[') inClass = true;
        else if (s[i] === ']') inClass = false;
        else if (s[i] === '/' && !inClass) return i;
    }
    return s.length;
}

// Build a harness exposing resolveKeyringVar + prefetchKeyringVars over
// injected module-scope state (caches, module-enabled flag, fetch).
function makeHarness({ moduleEnabled, fetchImpl }) {
    const body = `
        var _keyringVarsCache = {};
        var _keyringVarsInflight = {};
        var config = { prefix: '' };
        var fetch = fetchImpl;
        function isModuleEnabled(id) { return moduleEnabled; }
        ${extractFn('resolveKeyringVar')}
        ${extractFn('prefetchKeyringVars', 'async function')}
        return {
            resolveKeyringVar: resolveKeyringVar,
            prefetchKeyringVars: prefetchKeyringVars,
            cache: _keyringVarsCache,
        };
    `;
    return new Function('moduleEnabled', 'fetchImpl', body)(moduleEnabled, fetchImpl);
}

test('resolveKeyringVar reads the session cache, null on miss', () => {
    const h = makeHarness({ moduleEnabled: true, fetchImpl: null });
    assert.equal(h.resolveKeyringVar('svc/acct'), null);
    h.cache['svc/acct'] = 'secret-1';
    assert.equal(h.resolveKeyringVar('svc/acct'), 'secret-1');
});

test('prefetch scans both refs, batches one call, populates the cache', async () => {
    let seenBody = null;
    const fetchImpl = (url, opts) => {
        seenBody = JSON.parse(opts.body);
        return Promise.resolve({
            ok: true,
            json: () => Promise.resolve({
                enabled: true,
                backend: 'wincred',
                values: { 'a/one': 'v1', 'b/two': 'v2' },
                errors: {},
            }),
        });
    };
    const h = makeHarness({ moduleEnabled: true, fetchImpl });

    await h.prefetchKeyringVars('token={{keyring.a/one}} and ${keyring.b/two}');

    assert.deepEqual(seenBody.refs.sort(), ['a/one', 'b/two']);
    assert.equal(h.resolveKeyringVar('a/one'), 'v1');
    assert.equal(h.resolveKeyringVar('b/two'), 'v2');
});

test('prefetch is a no-op when the module is disabled (no fetch)', async () => {
    let called = false;
    const fetchImpl = () => { called = true; return Promise.resolve({ ok: false }); };
    const h = makeHarness({ moduleEnabled: false, fetchImpl });

    await h.prefetchKeyringVars('{{keyring.svc/acct}}');

    assert.equal(called, false);
    assert.equal(h.resolveKeyringVar('svc/acct'), null);
});

test('prefetch skips refs already cached', async () => {
    let callCount = 0;
    const fetchImpl = () => {
        callCount++;
        return Promise.resolve({
            ok: true,
            json: () => Promise.resolve({ enabled: true, values: {}, errors: {} }),
        });
    };
    const h = makeHarness({ moduleEnabled: true, fetchImpl });
    h.cache['svc/acct'] = 'already';

    await h.prefetchKeyringVars('{{keyring.svc/acct}}');

    assert.equal(callCount, 0); // nothing wanted → no request
    assert.equal(h.resolveKeyringVar('svc/acct'), 'already');
});

test('prefetch tolerates a failed response without throwing', async () => {
    const fetchImpl = () => Promise.resolve({ ok: false });
    const h = makeHarness({ moduleEnabled: true, fetchImpl });

    await h.prefetchKeyringVars('{{keyring.svc/acct}}');

    assert.equal(h.resolveKeyringVar('svc/acct'), null); // placeholder survives
});
