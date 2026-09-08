// #95 — unit tests for the Header Library's pure half: scope parsing,
// scope matching, which sets are active for a request, and the merge
// precedence between library sets and the request's own header rows.
//
// The fragment's state half (load/persist) reads localStorage and wsKey
// out of the enclosing IIFE; those arrive as hoisted stubs in the
// appended block, so the whole fragment compiles and coverage lands on
// the real file.

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { compileFragment } from './_load-fragment.mjs';

const load = compileFragment(
    '../../../src/Kuestenlogik.Bowire/wwwroot/js/header-library.js',
    ['store'],
    `
    function wsKey(k) { return 'ws_' + k; }
    function markSaved() {}
    var localStorage = {
        getItem: function (k) { return store && k in store ? store[k] : null; },
        setItem: function (k, v) { if (store) store[k] = v; },
    };
    return {
        parseHeaderScope, parseHeaderScopeToString, headerScopeHost,
        headerScopeMatches, activeHeaderSets,
        composeHeaderRows, composeHeaderObject,
        sanitiseHeaderLibrary, newHeaderSetId,
        loadHeaderLibrary, persistHeaderLibrary,
        library: () => headerLibrary,
        setLibrary: (v) => { headerLibrary = v; },
    };
    `
);

const api = load({ store: {} });

const set = (id, name, scope, headers) => ({ id, name, scope, headers });
const row = (key, value, extra) => Object.assign({ key, value }, extra || {});

// ---- parseHeaderScope ----

test('scope forms parse into kind + value', () => {
    assert.deepEqual(api.parseHeaderScope('global'), { kind: 'global', value: '' });
    assert.deepEqual(api.parseHeaderScope('url:api.example.com'),
        { kind: 'url', value: 'api.example.com' });
    assert.deepEqual(api.parseHeaderScope('service:UserService'),
        { kind: 'service', value: 'UserService' });
    assert.deepEqual(api.parseHeaderScope('method:UserService.GetUser'),
        { kind: 'method', value: 'UserService.GetUser' });
});

test('an unreadable scope degrades to global rather than vanishing', () => {
    // A set nobody can reach is worse than one that shows up everywhere:
    // the operator can at least see it and fix the scope.
    for (const bad of ['', '   ', null, undefined, 'nonsense', 'weird:x']) {
        assert.equal(api.parseHeaderScope(bad).kind, 'global', `for ${JSON.stringify(bad)}`);
    }
});

test('a known kind with no value yet keeps its kind and matches nothing', () => {
    // The editor is in this state the moment you pick "URL host" and have
    // not typed the host. Collapsing it to global would make an unfinished
    // set apply everywhere — the opposite of what was asked for.
    assert.deepEqual(api.parseHeaderScope('url:'), { kind: 'url', value: '' });
    assert.equal(api.parseHeaderScopeToString('url:'), 'url:');
    assert.equal(api.headerScopeMatches('url:', { url: 'https://api.example.com' }), false);
    assert.equal(api.headerScopeMatches('service:', { service: 'UserService' }), false);
    assert.equal(api.headerScopeMatches('method:', { service: 'S', method: 'M' }), false);
});

test('an incomplete set is off by default rather than on everywhere', () => {
    const half = set('h', 'half-written', 'url:', [row('X', '1')]);
    assert.deepEqual(
        api.activeHeaderSets([half], { url: 'https://api.example.com' }).map(s => s.id), []);
});

test('scopes round-trip through the canonical string form', () => {
    assert.equal(api.parseHeaderScopeToString(' URL:API.example.com '), 'url:API.example.com');
    assert.equal(api.parseHeaderScopeToString('rubbish'), 'global');
});

// ---- headerScopeHost ----

test('host extraction survives schemes, ports, paths, credentials', () => {
    const cases = {
        'api.example.com': 'api.example.com',
        'https://api.example.com': 'api.example.com',
        'https://api.example.com:8443/v1/pets?x=1': 'api.example.com',
        'grpc://user:pw@API.Example.com:443': 'api.example.com',
        'http://localhost:5080/': 'localhost',
        '': '',
    };
    for (const [input, want] of Object.entries(cases)) {
        assert.equal(api.headerScopeHost(input), want, `for ${JSON.stringify(input)}`);
    }
});

test('an IPv6 literal keeps its brackets and loses its port', () => {
    assert.equal(api.headerScopeHost('http://[::1]:5080/x'), '[::1]');
});

// ---- headerScopeMatches ----

test('global matches anything, including a context with nothing in it', () => {
    assert.equal(api.headerScopeMatches('global', {}), true);
    assert.equal(api.headerScopeMatches('global', null), true);
});

test('url scope matches on host, ignoring scheme, port and path', () => {
    const ctx = { url: 'https://api.example.com:8443/v1/pets' };
    assert.equal(api.headerScopeMatches('url:api.example.com', ctx), true);
    assert.equal(api.headerScopeMatches('url:https://api.example.com', ctx), true);
    assert.equal(api.headerScopeMatches('url:other.example.com', ctx), false);
});

test('url scope never matches when the request has no URL', () => {
    assert.equal(api.headerScopeMatches('url:api.example.com', { url: '' }), false);
});

test('service and method scopes compare case-insensitively', () => {
    const ctx = { service: 'UserService', method: 'GetUser' };
    assert.equal(api.headerScopeMatches('service:userservice', ctx), true);
    assert.equal(api.headerScopeMatches('method:USERSERVICE.getuser', ctx), true);
    assert.equal(api.headerScopeMatches('method:UserService.Other', ctx), false);
});

test('a dotted (package-qualified) service name still parses in a method scope', () => {
    const ctx = { service: 'acme.users.v1.UserService', method: 'GetUser' };
    assert.equal(
        api.headerScopeMatches('method:acme.users.v1.UserService.GetUser', ctx), true);
});

test('service and method scopes fail on a freeform request with no service', () => {
    const ctx = { url: 'https://api.example.com' };
    assert.equal(api.headerScopeMatches('service:UserService', ctx), false);
    assert.equal(api.headerScopeMatches('method:UserService.GetUser', ctx), false);
});

// ---- activeHeaderSets ----

const LIB = [
    set('a', 'defaults', 'url:api.example.com', [row('Accept', 'application/json')]),
    set('b', 'trace', 'global', [row('X-Trace', '1')]),
    set('c', 'user-svc', 'service:UserService', [row('X-Svc', 'users')]),
];

test('scope decides which sets are on by default', () => {
    const on = api.activeHeaderSets(LIB, { url: 'https://api.example.com' });
    assert.deepEqual(on.map(s => s.id), ['a', 'b']);
});

test('a per-request override switches a scoped set off', () => {
    const on = api.activeHeaderSets(LIB, { url: 'https://api.example.com' }, { a: false });
    assert.deepEqual(on.map(s => s.id), ['b']);
});

test('a per-request override switches a non-matching set on for one call', () => {
    const on = api.activeHeaderSets(LIB, { url: 'https://other.example.com' }, { c: true });
    assert.deepEqual(on.map(s => s.id), ['b', 'c']);
});

test('active sets come back in library order, which is what precedence means', () => {
    const on = api.activeHeaderSets(LIB, {}, { a: true, c: true });
    assert.deepEqual(on.map(s => s.id), ['a', 'b', 'c']);
});

test('a set with no id is skipped rather than crashing the strip', () => {
    const on = api.activeHeaderSets([{ name: 'broken', scope: 'global' }, LIB[1]], {});
    assert.deepEqual(on.map(s => s.id), ['b']);
});

// ---- composeHeaderRows ----

test('library headers ship when the request names none of them', () => {
    const { rows } = api.composeHeaderRows([], [LIB[0], LIB[1]]);
    assert.deepEqual(rows.map(r => [r.key, r.value]),
        [['Accept', 'application/json'], ['X-Trace', '1']]);
    assert.deepEqual(rows.map(r => r.source), ['library', 'library']);
});

test("the request's own row always beats a library row", () => {
    const { rows } = api.composeHeaderRows(
        [row('Accept', 'text/csv')], [LIB[0]]);
    assert.deepEqual(rows.map(r => [r.key, r.value, r.source]),
        [['Accept', 'text/csv', 'request']]);
});

test('between two library sets, the later one wins', () => {
    const first = set('x', 'first', 'global', [row('X-Api-Version', '1')]);
    const second = set('y', 'second', 'global', [row('X-Api-Version', '2')]);
    const { rows } = api.composeHeaderRows([], [first, second]);
    assert.deepEqual(rows.map(r => [r.key, r.value]), [['X-Api-Version', '2']]);
});

test('header names match case-insensitively but keep their first spelling', () => {
    const lower = set('x', 'lower', 'global', [row('accept', 'application/json')]);
    const { rows } = api.composeHeaderRows([row('Accept', 'text/csv')], [lower]);
    assert.equal(rows.length, 1, 'accept and Accept are one header, not two');
    assert.equal(rows[0].key, 'accept', 'the first spelling seen is the one that ships');
    assert.equal(rows[0].value, 'text/csv', 'the request still wins on value');
});

test('an overridden header is reported as a conflict, winner and losers named', () => {
    const { conflicts } = api.composeHeaderRows([row('Accept', 'text/csv')], [LIB[0]]);
    assert.equal(conflicts.length, 1);
    assert.equal(conflicts[0].key, 'Accept');
    assert.equal(conflicts[0].winner.source, 'request');
    assert.deepEqual(conflicts[0].losers.map(l => l.setName), ['defaults']);
});

test('a header nobody contests produces no conflict', () => {
    const { conflicts } = api.composeHeaderRows([row('X-One', '1')], [LIB[1]]);
    assert.deepEqual(conflicts, []);
});

test('an unticked row neither ships nor shadows the library value', () => {
    const { rows } = api.composeHeaderRows(
        [row('Accept', 'text/csv', { enabled: false })], [LIB[0]]);
    assert.deepEqual(rows.map(r => [r.key, r.value, r.source]),
        [['Accept', 'application/json', 'library']]);
});

test('an unticked library row is left out too', () => {
    const s = set('x', 'x', 'global', [row('X-Off', 'no', { enabled: false })]);
    assert.deepEqual(api.composeHeaderRows([], [s]).rows, []);
});

test('rows with a blank or missing name are dropped', () => {
    const { rows } = api.composeHeaderRows(
        [row('   ', 'v'), row('', 'v'), { value: 'v' }], []);
    assert.deepEqual(rows, []);
});

test('values pass through the substitution hook, keys do not', () => {
    const s = set('x', 'x', 'global', [row('X-{{raw}}', '{{token}}')]);
    const { rows } = api.composeHeaderRows([], [s], {
        substitute: (v) => v.replace('{{token}}', 'abc123'),
    });
    assert.deepEqual(rows.map(r => [r.key, r.value]), [['X-{{raw}}', 'abc123']]);
});

test('substituteLibrary resolves library values without touching request rows', () => {
    // Some execute paths substitute their own rows and some do not; #95 is
    // not the ticket that changes that, so a library-only hook exists.
    const s = set('x', 'x', 'global', [row('X-Lib', '{{token}}')]);
    const { rows } = api.composeHeaderRows([row('X-Req', '{{token}}')], [s], {
        substituteLibrary: (v) => v.replace('{{token}}', 'abc123'),
    });
    assert.deepEqual(rows.map(r => [r.key, r.value]),
        [['X-Lib', 'abc123'], ['X-Req', '{{token}}']]);
});

test('a lone substitute hook still covers both sources', () => {
    const s = set('x', 'x', 'global', [row('X-Lib', '{{token}}')]);
    const { rows } = api.composeHeaderRows([row('X-Req', '{{token}}')], [s], {
        substitute: (v) => v.replace('{{token}}', 'abc123'),
    });
    assert.deepEqual(rows.map(r => r.value), ['abc123', 'abc123']);
});

test('composeHeaderObject flattens to what an execute path already expects', () => {
    const out = api.composeHeaderObject([row('Accept', 'text/csv')], [LIB[0], LIB[1]]);
    assert.deepEqual(out, { Accept: 'text/csv', 'X-Trace': '1' });
});

test('no sets and no rows compose to nothing, not to undefined', () => {
    assert.deepEqual(api.composeHeaderObject(null, null), {});
    assert.deepEqual(api.composeHeaderRows(undefined, undefined).rows, []);
});

// ---- sanitiseHeaderLibrary ----

test('a hand-edited import is repaired rather than trusted', () => {
    const out = api.sanitiseHeaderLibrary([
        { name: 'no id', scope: 'global', headers: [row('A', '1')] },
        { id: 'dup', name: 'one', scope: 'url:x.example.com', headers: [row('B', '2')] },
        { id: 'dup', name: 'two', scope: 'nonsense', headers: 'not an array' },
        'not an object',
        null,
    ]);
    assert.equal(out.length, 3);
    assert.ok(out[0].id, 'a missing id is generated');
    assert.notEqual(out[1].id, out[2].id, 'a duplicate id is re-generated');
    assert.equal(out[2].scope, 'global', 'an unreadable scope becomes global');
    assert.deepEqual(out[2].headers, [], 'a non-array headers field becomes empty');
});

test('sanitising fills the row defaults and drops nameless rows', () => {
    const out = api.sanitiseHeaderLibrary([
        { id: 'a', name: ' padded ', scope: 'global',
          headers: [row('A', '1'), { key: '  ', value: 'x' }, { value: 'y' }] },
    ]);
    assert.equal(out[0].name, 'padded');
    assert.deepEqual(out[0].headers, [
        { key: 'A', value: '1', description: '', enabled: true },
    ]);
});

test('a set with no name gets a placeholder rather than an empty chip', () => {
    const out = api.sanitiseHeaderLibrary([{ id: 'a', scope: 'global', headers: [] }]);
    assert.equal(out[0].name, 'Untitled set');
});

test('non-array input sanitises to an empty library', () => {
    assert.deepEqual(api.sanitiseHeaderLibrary(null), []);
    assert.deepEqual(api.sanitiseHeaderLibrary({ not: 'an array' }), []);
});

test('generated ids do not collide', () => {
    const ids = new Set(Array.from({ length: 200 }, () => api.newHeaderSetId()));
    assert.equal(ids.size, 200);
});

// ---- load / persist ----

test('the library persists to a workspace-scoped key and loads back', () => {
    const store = {};
    const inst = load({ store });
    inst.setLibrary(api.sanitiseHeaderLibrary([
        { id: 'a', name: 'defaults', scope: 'url:api.example.com', headers: [row('Accept', 'x')] },
    ]));
    inst.persistHeaderLibrary();
    assert.ok('ws_bowire_header_library' in store, 'stored under wsKey(), not a global key');

    const reopened = load({ store });
    reopened.loadHeaderLibrary();
    assert.deepEqual(reopened.library().map(s => s.name), ['defaults']);
});

test('a corrupt stored value loads as an empty library instead of throwing', () => {
    const inst = load({ store: { ws_bowire_header_library: '{ not json' } });
    inst.loadHeaderLibrary();
    assert.deepEqual(inst.library(), []);
});

test('loading sanitises, so a hand-edited store cannot wedge the strip', () => {
    const inst = load({
        store: { ws_bowire_header_library: JSON.stringify([{ name: 'no id', headers: [] }]) },
    });
    inst.loadHeaderLibrary();
    assert.equal(inst.library().length, 1);
    assert.ok(inst.library()[0].id);
});
