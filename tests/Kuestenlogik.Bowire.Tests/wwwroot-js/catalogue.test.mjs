// #136 — catalogue.js unit tests.
//
// The fragment owns module-scope state (catalogueInfo, catalogueEntries,
// catalogueOriginByUrl) and exposes:
//   * applyCatalogueToServerUrls — merges a snapshot into serverUrls
//     and tags origin per-URL. Pure data shape, easy to test.
//   * fetchCatalogueInfo / fetchCatalogueEntries / refreshCatalogueEntries
//     — wrappers around `fetch`, easy to test with a stub.
//   * initialCatalogueLoad — orchestrates the above; no-op when the
//     host hasn't called AddBowireCatalogue().

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { compileFragment } from './_load-fragment.mjs';

// Host stubs live in the appended block (hoisted); the fragment is
// compiled alone with its real filename so V8 attributes coverage to
// catalogue.js (#367).
const _prelude = `
    var serverUrls = (state.serverUrls || []).slice();
    var connectionStatuses = Object.assign({}, state.connectionStatuses || {});
    var config = { prefix: '/bowire' };
    function render() {}
    // fetch is provided by the test (per-call). Tests override it
    // by reassigning _fetchImpl before calling the loader fns.
    var _fetchImpl = function () { return Promise.reject(new Error('fetch not stubbed')); };
    var fetch = function () { return _fetchImpl.apply(this, arguments); };
    // #537 — initialCatalogueLoad now kicks discovery for the URLs it
    // merged in. Counted, not stubbed away, so the tests can pin it.
    var _fetchServicesCalls = 0;
    function fetchServices() { _fetchServicesCalls++; }
`;
const _postlude = `
        return {
            applyCatalogueToServerUrls: applyCatalogueToServerUrls,
            fetchCatalogueInfo: fetchCatalogueInfo,
            fetchCatalogueEntries: fetchCatalogueEntries,
            refreshCatalogueEntries: refreshCatalogueEntries,
            initialCatalogueLoad: initialCatalogueLoad,
            // #537 — pure read accessors + helpers the renderers call
            // straight from the render path. Every one must stay
            // side-effect free, which is exactly what these tests pin.
            catalogueIsAvailable: catalogueIsAvailable,
            catalogueHasEntries: catalogueHasEntries,
            catalogueEntryCount: catalogueEntryCount,
            catalogueProviderLabel: catalogueProviderLabel,
            catalogueVisibility: catalogueVisibility,
            catalogueProviderIds: catalogueProviderIds,
            catalogueOriginFor: catalogueOriginFor,
            isCatalogueUrl: isCatalogueUrl,
            catalogueEntryUrl: catalogueEntryUrl,
            catalogueFilterEntries: catalogueFilterEntries,
            catalogueAllTags: catalogueAllTags,
            catalogueEntryByUrl: catalogueEntryByUrl,
            _setFetch: function (f) { _fetchImpl = f; },
            _fetchServicesCalls: function () { return _fetchServicesCalls; },
            _getServerUrls: function () { return serverUrls; },
            _getConnStatuses: function () { return connectionStatuses; },
            _getOriginByUrl: function () { return catalogueOriginByUrl; },
            _getCatalogueInfo: function () { return catalogueInfo; },
            _setCatalogueInfo: function (i) { catalogueInfo = i; },
            _getCatalogueEntries: function () { return catalogueEntries; }
        };
    `;
const _loadCatalogue = compileFragment(
    '../../../src/Kuestenlogik.Bowire/wwwroot/js/catalogue.js',
    ['state'],
    _prelude + '\n' + _postlude
);

function loadCatalogue(state) {
    return _loadCatalogue({ state: state || {} });
}

// ---- applyCatalogueToServerUrls ----

test('applyCatalogueToServerUrls: null / undefined payload → 0', () => {
    const sb = loadCatalogue();
    assert.equal(sb.applyCatalogueToServerUrls(null), 0);
    assert.equal(sb.applyCatalogueToServerUrls(undefined), 0);
    assert.equal(sb.applyCatalogueToServerUrls({}), 0);
});

test('applyCatalogueToServerUrls: empty entries[] → 0', () => {
    const sb = loadCatalogue();
    assert.equal(sb.applyCatalogueToServerUrls({ entries: [] }), 0);
});

test('applyCatalogueToServerUrls: appends new URLs + tags origin', () => {
    const sb = loadCatalogue({ serverUrls: ['https://existing'] });
    const added = sb.applyCatalogueToServerUrls({
        providerId: 'consul',
        providerName: 'Consul',
        entries: [
            { url: 'https://a', name: 'A', protocols: ['rest'] },
            { url: 'https://b', name: 'B', tags: ['v2'] }
        ]
    });
    assert.equal(added, 2);
    // #537 — the first declared protocol becomes a `protocol@` hint, so
    // an entry whose surface lives under a path is probed the way its
    // provider meant it to be. Everything (serverUrls, the origin map)
    // is keyed by that same composed string.
    assert.deepEqual(sb._getServerUrls(), ['https://existing', 'rest@https://a', 'https://b']);
    const origin = sb._getOriginByUrl();
    assert.equal(origin['rest@https://a'].providerId, 'consul');
    assert.equal(origin['rest@https://a'].providerName, 'Consul');
    assert.equal(origin['rest@https://a'].name, 'A');
    assert.deepEqual(origin['rest@https://a'].protocols, ['rest']);
    assert.deepEqual(origin['https://b'].tags, ['v2']);
});

test('applyCatalogueToServerUrls: composes the protocol hint for pathed URLs', () => {
    // The regression this fixes: `{ url: 'http://h:5183/graphql',
    // protocols: ['graphql'] }` used to land in serverUrls as the bare
    // URL, so the workbench probed the host root and found nothing —
    // while CI's smoke-samples.mjs, which composes the hint itself,
    // reported the sample as fine.
    const sb = loadCatalogue();
    sb.applyCatalogueToServerUrls({
        entries: [{ url: 'http://h:5183/graphql', protocols: ['graphql'] }]
    });
    assert.deepEqual(sb._getServerUrls(), ['graphql@http://h:5183/graphql']);
    assert.equal(sb._getConnStatuses()['graphql@http://h:5183/graphql'], 'disconnected');
});

test('applyCatalogueToServerUrls: never double-prefixes an already-hinted URL', () => {
    const sb = loadCatalogue();
    sb.applyCatalogueToServerUrls({
        entries: [{ url: 'grpcweb@http://h:5184', protocols: ['grpc'] }]
    });
    // 'grpc@grpcweb@http://…' is claimed by no plugin at all.
    assert.deepEqual(sb._getServerUrls(), ['grpcweb@http://h:5184']);
});

test('applyCatalogueToServerUrls: merged URLs are marked adopted, pre-existing ones are not', () => {
    // persistServerUrls filters on isCatalogueUrl, so this flag is what
    // decides whether a row survives in localStorage. A URL the operator
    // had already typed must keep its local persistence even when the
    // catalogue happens to name the same target.
    const sb = loadCatalogue({ serverUrls: ['https://typed-by-hand'] });
    sb.applyCatalogueToServerUrls({
        entries: [{ url: 'https://typed-by-hand' }, { url: 'https://from-catalogue' }]
    });
    assert.equal(sb.isCatalogueUrl('https://from-catalogue'), true);
    assert.equal(sb.isCatalogueUrl('https://typed-by-hand'), false);
});

test('applyCatalogueToServerUrls: existing URL preserved + origin unset for it', () => {
    const sb = loadCatalogue({ serverUrls: ['https://existing'] });
    sb.applyCatalogueToServerUrls({
        providerId: 'p',
        entries: [{ url: 'https://existing' }, { url: 'https://new' }]
    });
    // Existing URL already in serverUrls — applyCatalogueToServerUrls
    // detects it and bumps the origin/index for the NEW one only.
    assert.equal(sb._getServerUrls().length, 2);
});

test('applyCatalogueToServerUrls: ignores entries with no url', () => {
    const sb = loadCatalogue();
    const added = sb.applyCatalogueToServerUrls({
        providerId: 'p',
        entries: [{ name: 'no-url' }, null, { url: 'https://ok' }]
    });
    assert.equal(added, 1);
    assert.deepEqual(sb._getServerUrls(), ['https://ok']);
});

test('applyCatalogueToServerUrls: seeds connectionStatuses[url] = disconnected', () => {
    const sb = loadCatalogue();
    sb.applyCatalogueToServerUrls({ entries: [{ url: 'https://a' }] });
    assert.equal(sb._getConnStatuses()['https://a'], 'disconnected');
});

test('applyCatalogueToServerUrls: duplicate URLs in the payload only added once', () => {
    const sb = loadCatalogue();
    const added = sb.applyCatalogueToServerUrls({
        entries: [
            { url: 'https://a', name: 'A1' },
            { url: 'https://a', name: 'A2' }  // duplicate
        ]
    });
    assert.equal(added, 1);
    assert.deepEqual(sb._getServerUrls(), ['https://a']);
    // First write wins for the origin tag.
    assert.equal(sb._getOriginByUrl()['https://a'].name, 'A1');
});

test('applyCatalogueToServerUrls: stores entries on module state', () => {
    const sb = loadCatalogue();
    sb.applyCatalogueToServerUrls({ entries: [{ url: 'https://a' }, { url: 'https://b' }] });
    assert.equal(sb._getCatalogueEntries().length, 2);
});

// ---- fetchCatalogueInfo / fetchCatalogueEntries / refreshCatalogueEntries ----

test('fetchCatalogueInfo: returns parsed JSON on 200', async () => {
    const sb = loadCatalogue();
    sb._setFetch(() => Promise.resolve({
        ok: true,
        json: () => Promise.resolve({ available: true, providerId: 'consul' })
    }));
    const r = await sb.fetchCatalogueInfo();
    assert.deepEqual(r, { available: true, providerId: 'consul' });
});

test('fetchCatalogueInfo: returns null on non-2xx', async () => {
    const sb = loadCatalogue();
    sb._setFetch(() => Promise.resolve({ ok: false, json: () => Promise.resolve({}) }));
    const r = await sb.fetchCatalogueInfo();
    assert.equal(r, null);
});

test('fetchCatalogueInfo: returns null on network error', async () => {
    const sb = loadCatalogue();
    sb._setFetch(() => Promise.reject(new Error('network')));
    const r = await sb.fetchCatalogueInfo();
    assert.equal(r, null);
});

test('fetchCatalogueEntries: hits /api/catalogue/entries', async () => {
    const sb = loadCatalogue();
    let calledUrl = null;
    sb._setFetch((url) => {
        calledUrl = url;
        return Promise.resolve({ ok: true, json: () => Promise.resolve({ entries: [] }) });
    });
    await sb.fetchCatalogueEntries();
    assert.equal(calledUrl, '/bowire/api/catalogue/entries');
});

test('refreshCatalogueEntries: POSTs to /api/catalogue/refresh', async () => {
    const sb = loadCatalogue();
    let method = null;
    sb._setFetch((url, opts) => {
        method = opts && opts.method;
        return Promise.resolve({ ok: true, json: () => Promise.resolve({ entries: [] }) });
    });
    await sb.refreshCatalogueEntries();
    assert.equal(method, 'POST');
});

// ---- initialCatalogueLoad ----

test('initialCatalogueLoad: info.available=false → no entries fetch', async () => {
    const sb = loadCatalogue();
    let calls = 0;
    sb._setFetch((url) => {
        calls++;
        return Promise.resolve({
            ok: true,
            json: () => Promise.resolve({ available: false })
        });
    });
    await sb.initialCatalogueLoad();
    assert.equal(calls, 1); // only the info call; no entries fetch
});

test('initialCatalogueLoad: available=true → fetch entries + apply', async () => {
    const sb = loadCatalogue();
    let fetchCount = 0;
    sb._setFetch((url) => {
        fetchCount++;
        if (url.endsWith('/info')) {
            return Promise.resolve({
                ok: true,
                json: () => Promise.resolve({ available: true, providerId: 'p' })
            });
        }
        return Promise.resolve({
            ok: true,
            json: () => Promise.resolve({ providerId: 'p', entries: [{ url: 'https://x' }] })
        });
    });
    await sb.initialCatalogueLoad();
    assert.equal(fetchCount, 2);
    assert.deepEqual(sb._getServerUrls(), ['https://x']);
    assert.equal(sb._getCatalogueInfo().available, true);
});

test('initialCatalogueLoad: tolerates a null info response', async () => {
    const sb = loadCatalogue();
    sb._setFetch(() => Promise.resolve({ ok: false, json: () => Promise.resolve({}) }));
    await sb.initialCatalogueLoad();
    assert.equal(sb._getCatalogueInfo().available, false);
});

test('initialCatalogueLoad: never starts a second discovery run itself', async () => {
    // #537 — init.js sequences the ONE boot fetchServices() after this
    // promise settles. Firing a second one from in here overlapped with
    // the boot run: both write isLoadingServices, and the workbench
    // wedged on "Discovering services…" forever. Measured in the
    // browser, not theorised.
    const sb = loadCatalogue();
    sb._setFetch((url) => Promise.resolve({
        ok: true,
        json: () => Promise.resolve(url.endsWith('/info')
            ? { available: true, providerId: 'p' }
            : { providerId: 'p', entries: [{ url: 'https://x' }] })
    }));
    await sb.initialCatalogueLoad();
    assert.equal(sb._fetchServicesCalls(), 0);
    // …but the URL IS merged, which is what makes sequencing pay off.
    assert.deepEqual(sb._getServerUrls(), ['https://x']);
});

test('initialCatalogueLoad: resolves (so a caller can sequence on it) even with no catalogue', async () => {
    const sb = loadCatalogue();
    sb._setFetch(() => Promise.resolve({ ok: true, json: () => Promise.resolve({ available: false }) }));
    const result = sb.initialCatalogueLoad();
    assert.ok(result && typeof result.then === 'function',
        'init.js does `initialCatalogueLoad().then(fetchServices, fetchServices)` — it must return a thenable');
    await result;
});
