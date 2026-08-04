// #185 — schema-change log unit tests.
//
// Targets the pure/stateful helpers behind the statusbar pill, the
// Discover-rail attention dot and the click-through navigation:
//   * _schemaChangeEntriesFromDelta — delta → change-log entries
//   * schemaChangeUnreadCount / _schemaChangePillLabel — watermark math
//   * _schemaChangePrune — client-side 7-day retention mirror
//   * schemaChangeLogRecord / schemaChangeMarkRead — server sync
//   * navigateToSchemaChange — rail switch + openTab hand-off
//
// Same wrap trick as schema-watch.test.mjs: the fragment is
// concatenated into one shared IIFE in production, so the wrapper
// declares the host-provided names the fragment closes over.

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, resolve } from 'node:path';

const __dirname = dirname(fileURLToPath(import.meta.url));
const SRC = readFileSync(
    resolve(__dirname, '../../../src/Kuestenlogik.Bowire/wwwroot/js/schema-changes.js'),
    'utf8'
);

function load(opts) {
    // opts.dom swaps in a minimal DOM (fake nodes + a pill anchor) so
    // the dropdown toggle — and its opening-marks-read semantic — can
    // run outside a browser.
    const domShim = (opts && opts.dom) ? `
        function _mkNode() {
            var n = {
                children: [], style: {}, className: '', id: '', innerHTML: '', textContent: '', title: '',
                appendChild: function (c) { if (c) n.children.push(c); return c; },
                remove: function () { n.removed = true; },
                contains: function () { return false; },
                querySelector: function () { return null; },
                querySelectorAll: function () { return []; },
                getBoundingClientRect: function () { return { left: 10, right: 120, top: 700, bottom: 720 }; },
                addEventListener: function () {},
                removeEventListener: function () {}
            };
            return n;
        }
        el = function (tag, props) {
            var n = _mkNode();
            if (props) { for (var k in props) { n[k] = props[k]; } }
            for (var i = 2; i < arguments.length; i++) {
                if (arguments[i]) n.appendChild(arguments[i]);
            }
            return n;
        };
        var _domPill = _mkNode();
        document = {
            body: _mkNode(),
            getElementById: function (id) {
                return id === 'bowire-schema-changes-pill' ? _domPill : null;
            },
            addEventListener: function () {},
            removeEventListener: function () {}
        };
    ` : '';
    const prelude = `
        var _ls = {};
        var localStorage = {
            getItem: function (k) { return Object.prototype.hasOwnProperty.call(_ls, k) ? _ls[k] : null; },
            setItem: function (k, v) { _ls[k] = String(v); },
            removeItem: function (k) { delete _ls[k]; }
        };
        var config = { prefix: '' };
        var activeWorkspaceId = 'ws1';
        function activeWorkspace() { return { id: 'ws1', storageRoot: null }; }
        var services = [];
        var railMode = 'home';
        var sidebarView = '';
        var expandedServices = new Set();
        var persistedExpanded = 0;
        function persistExpandedServices() { persistedExpanded++; }
        var openedTabs = [];
        function openTab(svc, m) { openedTabs.push({ svc: svc, m: m }); }
        var toasts = [];
        function toast(msg, kind) { toasts.push({ msg: msg, kind: kind }); }
        var renders = 0;
        function render() { renders++; }
        function el() { return { appendChild: function () {}, style: {} }; }
        function svgIcon() { return ''; }
        var document = { getElementById: function () { return null; }, body: { appendChild: function () {} } };
        var window = (typeof globalThis.window !== 'undefined') ? globalThis.window : { innerWidth: 1440, innerHeight: 900 };
        var fetchCalls = [];
        var fetchResponder = function () {
            return Promise.resolve({ ok: true, json: function () { return Promise.resolve({ entries: [], lastReadAt: null }); } });
        };
        function fetch(url, opts) { fetchCalls.push({ url: url, opts: opts }); return fetchResponder(url, opts); }
        var _storageMode = 'disk';
        function getWorkspaceStorageMode() { return _storageMode; }
    ` + domShim;
    const postlude = `
        return {
            _schemaChangeEntriesFromDelta: _schemaChangeEntriesFromDelta,
            _schemaChangePrune: _schemaChangePrune,
            _schemaChangePillLabel: _schemaChangePillLabel,
            schemaChangeUnreadCount: schemaChangeUnreadCount,
            schemaChangeLogRecord: schemaChangeLogRecord,
            schemaChangeMarkRead: schemaChangeMarkRead,
            ensureSchemaChangeLogLoaded: ensureSchemaChangeLogLoaded,
            navigateToSchemaChange: navigateToSchemaChange,
            toggleSchemaChangesDropdown: toggleSchemaChangesDropdown,
            _setLog: function (entries, readAt) {
                schemaChangeLog = entries; schemaChangeLastReadAt = readAt || null;
                _schemaChangeHydrated = true;
                _schemaChangeWsCache = (typeof activeWorkspaceId === 'string' && activeWorkspaceId)
                    ? activeWorkspaceId : '';
            },
            _getLog: function () { return schemaChangeLog; },
            _getLastReadAt: function () { return schemaChangeLastReadAt; },
            _setServices: function (list) { services = list; },
            _setWorkspace: function (id) { activeWorkspaceId = id; },
            _setStorageMode: function (m) { _storageMode = m; },
            _setFetchResponder: function (f) { fetchResponder = f; },
            _calls: function () { return fetchCalls; },
            _env: function () {
                return { railMode: railMode, sidebarView: sidebarView,
                         expanded: expandedServices, railLs: _ls['bowire_rail_mode'],
                         openedTabs: openedTabs, toasts: toasts, renders: renders };
            }
        };
    `;
    return new Function(prelude + '\n' + SRC + '\n' + postlude)();
}

const flush = () => new Promise((r) => setTimeout(r, 0));
const iso = (msAgo) => new Date(Date.now() - msAgo).toISOString();

// ---- delta → entries ----

test('entriesFromDelta: every delta bucket maps to its change type', () => {
    const sb = load();
    const at = new Date('2026-08-04T10:00:00Z');
    const entries = sb._schemaChangeEntriesFromDelta({
        at,
        addedServices: ['Fresh'],
        removedServices: ['Legacy'],
        addedMethods: [{ service: 'Orders', key: 'Orders Orders/Cancel' }],
        removedMethods: [{ service: 'Orders', key: 'Orders Orders/Void' }],
        changedMethods: [
            { service: 'Orders', key: 'Orders GET /orders', type: 'signature', detail: 'request shape changed' },
            { service: 'Orders', key: 'Orders GET /old', type: 'deprecation', detail: 'marked deprecated' }
        ],
        annotatedMethods: [{ service: 'Orders', key: 'Orders GET /docs', detail: 'description updated' }]
    });

    assert.deepEqual(entries.map((e) => e.type),
        ['added', 'removed', 'added', 'removed', 'signature', 'deprecation', 'annotation']);
    assert.equal(entries[2].method, 'Orders/Cancel');
    assert.equal(entries[4].detail, 'request shape changed');
    assert.ok(entries.every((e) => e.at === at.toISOString()));
});

test('entriesFromDelta: the method part survives a service name containing spaces', () => {
    // The key is '<service> <fullName>' and service names (OpenAPI
    // tags) may contain spaces — a split(' ') would truncate.
    const sb = load();
    const entries = sb._schemaChangeEntriesFromDelta({
        at: new Date(),
        addedMethods: [{ service: 'My Pet API', key: 'My Pet API GET /pets' }]
    });
    assert.equal(entries[0].service, 'My Pet API');
    assert.equal(entries[0].method, 'GET /pets');
});

// ---- unread watermark ----

test('unreadCount: entries newer than the watermark are unread', () => {
    const sb = load();
    sb._setLog([
        { at: iso(3 * 60000), type: 'added', service: 'A' },
        { at: iso(2 * 60000), type: 'removed', service: 'B' },
        { at: iso(1 * 60000), type: 'signature', service: 'C' }
    ], iso(2.5 * 60000));
    assert.equal(sb.schemaChangeUnreadCount(), 2);
});

test('unreadCount: no watermark means everything is unread', () => {
    const sb = load();
    sb._setLog([{ at: iso(60000), type: 'added', service: 'A' }], null);
    assert.equal(sb.schemaChangeUnreadCount(), 1);
});

test('pill label: unread changes lead with the count and the oldest unread time', () => {
    const sb = load();
    const oldest = new Date(Date.now() - 3 * 60000);
    sb._setLog([
        { at: oldest.toISOString(), type: 'added', service: 'A' },
        { at: iso(60000), type: 'removed', service: 'B' }
    ], null);
    const hh = (oldest.getHours() < 10 ? '0' : '') + oldest.getHours();
    const mm = (oldest.getMinutes() < 10 ? '0' : '') + oldest.getMinutes();
    assert.equal(sb._schemaChangePillLabel(), '2 changes since ' + hh + ':' + mm);
});

test('pill label: with everything read it decays to a quiet total', () => {
    const sb = load();
    sb._setLog([{ at: iso(60000), type: 'added', service: 'A' }], iso(0));
    assert.equal(sb._schemaChangePillLabel(), '1 change · 7d');
});

// ---- retention ----

test('prune: entries older than seven days fall out, fresh ones stay', () => {
    const sb = load();
    const pruned = sb._schemaChangePrune([
        { at: iso(8 * 24 * 3600 * 1000), type: 'added', service: 'Old' },
        { at: iso(6 * 24 * 3600 * 1000), type: 'added', service: 'Fresh' },
        { at: 'garbage', type: 'added', service: 'Broken' }
    ], Date.now());
    assert.deepEqual(pruned.map((e) => e.service), ['Fresh']);
});

// ---- server sync ----

test('record: appends locally and posts the entries to the workspace log', async () => {
    const sb = load();
    sb._setLog([], null);
    sb.schemaChangeLogRecord({
        at: new Date(),
        addedMethods: [{ service: 'Orders', key: 'Orders Orders/Cancel' }]
    });
    assert.equal(sb._getLog().length, 1, 'local log ticks before the server answers');

    await flush();
    const post = sb._calls().find((c) => c.opts && c.opts.method === 'POST');
    assert.ok(post, 'a POST must go out');
    assert.equal(post.url, '/api/schema-changes?workspaceId=ws1');
    assert.equal(JSON.parse(post.opts.body).entries.length, 1);
});

test('record: adopts the server envelope as the new truth', async () => {
    const sb = load();
    sb._setLog([], null);
    sb._setFetchResponder(() => Promise.resolve({
        ok: true,
        json: () => Promise.resolve({
            entries: [
                { at: iso(60000), type: 'added', service: 'FromOtherClient' },
                { at: iso(1000), type: 'added', service: 'Orders' }
            ],
            lastReadAt: null
        })
    }));
    sb.schemaChangeLogRecord({ at: new Date(), addedServices: ['Orders'] });
    await flush();
    assert.equal(sb._getLog().length, 2, 'the merged server view wins');
});

test('record: an unreachable server leaves the session-local log standing', async () => {
    const sb = load();
    sb._setLog([], null);
    sb._setFetchResponder(() => Promise.reject(new Error('offline')));
    sb.schemaChangeLogRecord({ at: new Date(), addedServices: ['Orders'] });
    await flush();
    assert.equal(sb._getLog().length, 1);
});

test('markRead: zeroes the unread count immediately and tells the server', async () => {
    const sb = load();
    sb._setLog([{ at: iso(1000), type: 'added', service: 'A' }], null);
    assert.equal(sb.schemaChangeUnreadCount(), 1);
    sb.schemaChangeMarkRead();
    assert.equal(sb.schemaChangeUnreadCount(), 0, 'optimistic — the pulse dies on open');
    await flush();
    const read = sb._calls().find((c) => c.url.indexOf('/api/schema-changes/read') === 0);
    assert.ok(read, 'the watermark must persist server-side');
});

test('hydrate: fetches exactly once no matter how often the pill renders', async () => {
    const sb = load();
    sb.ensureSchemaChangeLogLoaded();
    sb.ensureSchemaChangeLogLoaded();
    await flush();
    assert.equal(sb._calls().length, 1);
});

test('workspace scoping: no active workspace posts without a query string', async () => {
    const sb = load();
    sb._setWorkspace('');
    sb.schemaChangeLogRecord({ at: new Date(), addedServices: ['Orders'] });
    await flush();
    assert.equal(sb._calls()[0].url, '/api/schema-changes');
});

// ---- navigation ----

test('navigate: lands on the affected method in Discover', () => {
    const sb = load();
    const m = { name: 'Cancel', fullName: 'Orders/Cancel' };
    const svc = { name: 'Orders', methods: [m] };
    sb._setServices([svc]);
    sb.navigateToSchemaChange({ type: 'signature', service: 'Orders', method: 'Orders/Cancel' });

    const env = sb._env();
    assert.equal(env.railMode, 'discover');
    assert.equal(env.sidebarView, 'services');
    assert.equal(env.railLs, 'discover', 'the rail choice must persist');
    assert.equal(env.openedTabs.length, 1);
    assert.equal(env.openedTabs[0].m, m, 'openTab gets the LIVE objects, not names');
});

test('navigate: a vanished method falls back to its service group', () => {
    const sb = load();
    sb._setServices([{ name: 'Orders', methods: [] }]);
    sb.navigateToSchemaChange({ type: 'signature', service: 'Orders', method: 'Orders/Gone' });

    const env = sb._env();
    assert.equal(env.openedTabs.length, 0);
    assert.ok(env.expanded.has('Orders'), 'the service group expands instead');
    assert.equal(env.toasts.length, 1);
});

test('navigate: a vanished service explains itself instead of doing nothing', () => {
    const sb = load();
    sb._setServices([]);
    sb.navigateToSchemaChange({ type: 'removed', service: 'Legacy' });
    assert.match(sb._env().toasts[0].msg, /Legacy/);
});

// ---- response-race guards (#185 review findings) ----

test('adopt: a stale hydrate GET must not clobber a newer local append', async () => {
    const sb = load();
    let resolveGet;
    sb._setFetchResponder((url, opts) => {
        if (!opts || !opts.method) return new Promise((res) => { resolveGet = res; });
        return Promise.resolve({ ok: true, json: () => Promise.resolve({
            entries: [{ at: iso(0), type: 'added', service: 'Orders' }], lastReadAt: null }) });
    });
    sb.ensureSchemaChangeLogLoaded();               // GET hangs in flight
    sb.schemaChangeLogRecord({ at: new Date(), addedServices: ['Orders'] });
    await flush();
    assert.equal(sb._getLog().length, 1);

    // The slow GET finally answers with its pre-append (empty) snapshot.
    resolveGet({ ok: true, json: () => Promise.resolve({ entries: [], lastReadAt: null }) });
    await flush();
    assert.equal(sb._getLog().length, 1, 'the stale snapshot must be discarded');
});

test('adopt: the read watermark never moves backwards', async () => {
    const sb = load();
    sb._setLog([{ at: iso(1000), type: 'added', service: 'A' }], null);
    // The server answers the /read POST with an OLDER watermark than
    // the optimistic local one (client clock ahead of the server).
    sb._setFetchResponder(() => Promise.resolve({ ok: true, json: () => Promise.resolve({
        entries: [], lastReadAt: iso(10 * 60000) }) }));
    sb.schemaChangeMarkRead();
    assert.equal(sb.schemaChangeUnreadCount(), 0);
    await flush();
    assert.equal(sb.schemaChangeUnreadCount(), 0,
        'the older server watermark must not flip the entry back to unread');
});

test('markRead: the response is adopted watermark-only — entries survive', async () => {
    const sb = load();
    sb._setLog([{ at: iso(1000), type: 'added', service: 'A' }], null);
    // A mark-read snapshot taken before a concurrent append comes back
    // with an empty entries list; adopting it would vanish the change.
    sb._setFetchResponder(() => Promise.resolve({ ok: true, json: () => Promise.resolve({
        entries: [], lastReadAt: iso(0) }) }));
    sb.schemaChangeMarkRead();
    await flush();
    assert.equal(sb._getLog().length, 1);
});

// ---- workspace lifecycle ----

test('an in-place workspace switch drops the cache and re-hydrates', async () => {
    // deleteWorkspace / createWorkspace switch activeWorkspaceId
    // WITHOUT a reload — workspace A's pulse must not survive into B,
    // and opening B's log must not mark A's entries read.
    const sb = load();
    sb._setLog([{ at: iso(1000), type: 'added', service: 'A' }], null);
    assert.equal(sb.schemaChangeUnreadCount(), 1);

    sb._setWorkspace('ws2');
    assert.equal(sb.schemaChangeUnreadCount(), 0, 'ws1 state must not leak into ws2');
    sb.ensureSchemaChangeLogLoaded();
    await flush();
    const hydrates = sb._calls().filter((c) => !c.opts || !c.opts.method);
    assert.equal(hydrates.length, 1);
    assert.match(hydrates[0].url, /workspaceId=ws2/);
});

// ---- #212 browser-only storage gate ----

test('a browser-only workspace produces zero server traffic', async () => {
    const sb = load();
    sb._setStorageMode('browser-only');
    sb.ensureSchemaChangeLogLoaded();
    sb.schemaChangeLogRecord({ at: new Date(), addedServices: ['Orders'] });
    sb.schemaChangeMarkRead();
    await flush();
    assert.equal(sb._calls().length, 0, 'no GET, no POST — the log stays session-local');
    assert.equal(sb._getLog().length, 1, 'the session-local log still ticks');
});

// ---- retention at read time ----

test('an entry aged past the window stops counting even without new deltas', () => {
    const sb = load();
    sb._setLog([{ at: iso(8 * 24 * 3600 * 1000), type: 'added', service: 'Old' }], null);
    assert.equal(sb.schemaChangeUnreadCount(), 0,
        'a tab idling past day 7 must not pulse for an entry the dropdown will not show');
});

// ---- opening the log IS reading it ----

test('opening the dropdown marks the log read and persists the watermark', async () => {
    const sb = load({ dom: true });
    sb._setLog([{ at: iso(1000), type: 'signature', service: 'Orders', method: 'GET /orders' }], null);
    assert.equal(sb.schemaChangeUnreadCount(), 1);

    sb.toggleSchemaChangesDropdown();
    assert.equal(sb.schemaChangeUnreadCount(), 0, 'opening is reading');
    await flush();
    assert.ok(sb._calls().some((c) => c.url.indexOf('/api/schema-changes/read') === 0),
        'the watermark must persist server-side');
});
