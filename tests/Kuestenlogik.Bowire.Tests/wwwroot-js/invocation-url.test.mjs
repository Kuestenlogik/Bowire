// #253 — invocation URL override unit tests.
//
// The resolver is the pure, cross-cutting core: given a (service, method)
// and the per-method override store, it returns the raw invocation URL,
// honouring schema / source / inline modes with #252's rename-tolerant
// source drift. Same new Function wrap-trick harness as the sibling suites.

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { compileFragment } from './_load-fragment.mjs';

// Host stubs live in the appended block (hoisted); the fragment is
// compiled alone with its real filename so V8 attributes coverage to
// invocation-url.js (#367).
const _prelude = `
    var _ls = {};
    var localStorage = {
        getItem: function (k) { return Object.prototype.hasOwnProperty.call(_ls, k) ? _ls[k] : null; },
        setItem: function (k, v) { _ls[k] = String(v); },
        removeItem: function (k) { delete _ls[k]; }
    };
    var serverUrls = opts.serverUrls || [];
    var _saved = [];
    function markSaved(what) { _saved.push(what); }
    function wsKey(k) { return 'bowire_ws_t_' + String(k).replace(/^bowire_/, ''); }
    function methodStateKey(svc, m) { return String(svc) + '::' + String(m); }
    var console = { warn: function () { _warns.push(Array.prototype.join.call(arguments, ' ')); } };
    var _warns = [];
    // Minimal {{var}} substituter — expands {{host}} to the test value.
    var _vars = opts.vars || {};
    function substituteVars(s) {
        return String(s == null ? '' : s).replace(/\\{\\{([^}]+)\\}\\}/g, function (m, k) {
            return Object.prototype.hasOwnProperty.call(_vars, k.trim()) ? _vars[k.trim()] : m;
        });
    }
`;
const _postlude = `
    return {
        getInvocationOverride: getInvocationOverride,
        setInvocationOverride: setInvocationOverride,
        resolveSourceUrl: resolveSourceUrl,
        resolveInvocationUrl: resolveInvocationUrl,
        invocationUrlFor: invocationUrlFor,
        resolveItemInvocationUrl: resolveItemInvocationUrl,
        _setServerUrls: function (u) { serverUrls = u; },
        _warns: function () { return _warns; },
        _raw: function () { return _ls['bowire_ws_t_method_invocation_url']; }
    };
`;
const _load = compileFragment(
    '../../../src/Kuestenlogik.Bowire/wwwroot/js/invocation-url.js',
    ['opts'],
    _prelude + '\n' + _postlude
);

function load(opts) {
    return _load({ opts: opts || {} });
}

const svc = (name, originUrl) => ({ name: name, originUrl: originUrl });
const method = (name) => ({ name: name });

// ---- store ----

test('override store: absent by default; set then get; schema mode clears', () => {
    const sb = load();
    assert.equal(sb.getInvocationOverride('Svc', 'Get'), null);
    sb.setInvocationOverride('Svc', 'Get', { mode: 'inline', url: 'http://x' });
    assert.deepEqual(sb.getInvocationOverride('Svc', 'Get'), { mode: 'inline', url: 'http://x' });
    // Setting back to schema deletes the entry (default costs no storage).
    sb.setInvocationOverride('Svc', 'Get', { mode: 'schema' });
    assert.equal(sb.getInvocationOverride('Svc', 'Get'), null);
});

test('override store: keyed per service::method', () => {
    const sb = load();
    sb.setInvocationOverride('A', 'm', { mode: 'inline', url: 'http://a' });
    sb.setInvocationOverride('B', 'm', { mode: 'inline', url: 'http://b' });
    assert.equal(sb.getInvocationOverride('A', 'm').url, 'http://a');
    assert.equal(sb.getInvocationOverride('B', 'm').url, 'http://b');
});

// ---- resolveInvocationUrl ----

test('resolve: no override → schema url (service.originUrl)', () => {
    const sb = load();
    assert.equal(sb.resolveInvocationUrl(svc('S', 'http://schema'), method('m')), 'http://schema');
});

test('resolve: inline override wins over the schema url', () => {
    const sb = load();
    sb.setInvocationOverride('S', 'm', { mode: 'inline', url: 'http://live-api' });
    assert.equal(sb.resolveInvocationUrl(svc('S', 'http://schema'), method('m')), 'http://live-api');
});

test('resolve: an empty inline url falls back to the schema url', () => {
    const sb = load();
    sb.setInvocationOverride('S', 'm', { mode: 'inline', url: '   ' });
    assert.equal(sb.resolveInvocationUrl(svc('S', 'http://schema'), method('m')), 'http://schema');
});

test('resolve: source override resolves against the live workspace list', () => {
    const sb = load({ serverUrls: ['http://prod', 'http://staging'] });
    sb.setInvocationOverride('S', 'm', { mode: 'source', url: 'http://staging' });
    assert.equal(sb.resolveInvocationUrl(svc('S', 'http://schema'), method('m')), 'http://staging');
});

test('resolve: source override drifts to the first Source with a warning when retired', () => {
    const sb = load({ serverUrls: ['http://prod'] });
    sb.setInvocationOverride('S', 'm', { mode: 'source', url: 'http://gone' });
    assert.equal(sb.resolveInvocationUrl(svc('S', 'http://schema'), method('m')), 'http://prod');
    assert.ok(sb._warns().some(w => /no longer in workspace/.test(w)));
});

test('resolve: a friendly-name rename keeps the raw url, so the binding survives', () => {
    // The store keys on the raw url; renaming only changes the alias — the
    // ref still matches, no drift, no warning.
    const sb = load({ serverUrls: ['http://prod', 'http://staging'] });
    sb.setInvocationOverride('S', 'm', { mode: 'source', url: 'http://staging' });
    assert.equal(sb.resolveInvocationUrl(svc('S', 'http://schema'), method('m')), 'http://staging');
    assert.equal(sb._warns().length, 0);
});

test('resolve: no method (compare / benchmark path) → plain schema url, ignores overrides', () => {
    const sb = load();
    sb.setInvocationOverride('S', 'm', { mode: 'inline', url: 'http://override' });
    assert.equal(sb.resolveInvocationUrl(svc('S', 'http://schema'), null), 'http://schema');
});

// ---- invocationUrlFor: {{var}} substitution for the satellites (review) ----

test('invocationUrlFor: substitutes {{vars}} in an inline override url', () => {
    // The pre-script / header lookup / recorder must see the substituted
    // host, not the raw template — else a signature / lookup / label misses.
    const sb = load({ vars: { host: 'prod.api' } });
    sb.setInvocationOverride('S', 'm', { mode: 'inline', url: 'https://{{host}}/v1' });
    assert.equal(sb.invocationUrlFor(svc('S', 'http://schema'), method('m')), 'https://prod.api/v1');
});

test('invocationUrlFor: leaves an override-free url as the (substituted) schema url', () => {
    const sb = load({ vars: { host: 'prod.api' } });
    assert.equal(sb.invocationUrlFor(svc('S', 'https://{{host}}/spec'), method('m')), 'https://prod.api/spec');
});

// ---- resolveItemInvocationUrl (saved collection item) ----

test('item resolve: same-as-schema / absent → the item schema url', () => {
    const sb = load();
    assert.equal(sb.resolveItemInvocationUrl({ invocationUrlMode: 'same-as-schema' }, 'http://schema'), 'http://schema');
    assert.equal(sb.resolveItemInvocationUrl({}, 'http://schema'), 'http://schema', 'a pre-#253 item has no override');
});

test('item resolve: inline override on a saved item', () => {
    const sb = load();
    assert.equal(sb.resolveItemInvocationUrl({ invocationUrlMode: 'inline', invocationUrl: 'http://api' }, 'http://schema'), 'http://api');
});

test('item resolve: source override re-checks the live list', () => {
    const sb = load({ serverUrls: ['http://prod'] });
    assert.equal(sb.resolveItemInvocationUrl({ invocationUrlMode: 'source', invocationUrl: 'http://gone' }, 'http://schema'), 'http://prod');
});
