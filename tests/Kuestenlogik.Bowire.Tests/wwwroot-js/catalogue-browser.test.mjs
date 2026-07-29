// #537 — the catalogue browser's PURE helpers.
//
// catalogue.test.mjs owns the fetch/merge lifecycle. This file covers
// the functions the renderers call straight from the render path:
// catalogueEntryUrl (protocol-hint composition), catalogueFilterEntries,
// catalogueAllTags, catalogueEntryByUrl, and the read accessors that
// drive whether the catalogue-first affordance appears at all.
//
// They are pinned separately because a regression in any of them is a
// render-path regression: render() has no try/catch, and an accessor
// that mutates or throws blanks the whole workbench. Every assertion
// here is "call it twice, get the same answer, change nothing".

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, resolve } from 'node:path';

const __dirname = dirname(fileURLToPath(import.meta.url));
const FRAGMENT = (name) => readFileSync(
    resolve(__dirname, '../../../src/Kuestenlogik.Bowire/wwwroot/js/' + name), 'utf8');
const SRC = FRAGMENT('catalogue.js');

// Same sandbox shape as catalogue.test.mjs, minus the fetch stub: none
// of the helpers under test touch the network.
function loadHelpers(state) {
    state = state || {};
    const prelude = `
        var serverUrls = (state.serverUrls || []).slice();
        var connectionStatuses = {};
        var config = { prefix: '/bowire' };
        function render() {}
        var fetch = function () { return Promise.reject(new Error('not stubbed')); };
    `;
    const postlude = `
        if (state.info) catalogueInfo = state.info;
        if (state.entries) catalogueEntries = state.entries;
        return {
            catalogueEntryUrl: catalogueEntryUrl,
            catalogueFilterEntries: catalogueFilterEntries,
            catalogueAllTags: catalogueAllTags,
            catalogueEntryByUrl: catalogueEntryByUrl,
            catalogueIsAvailable: catalogueIsAvailable,
            catalogueHasEntries: catalogueHasEntries,
            catalogueEntryCount: catalogueEntryCount,
            catalogueProviderLabel: catalogueProviderLabel,
            catalogueVisibility: catalogueVisibility,
            catalogueProviderIds: catalogueProviderIds,
            catalogueOriginFor: catalogueOriginFor,
            isCatalogueUrl: isCatalogueUrl,
            _entries: function () { return catalogueEntries; },
            _serverUrls: function () { return serverUrls; }
        };
    `;
    return new Function('state', prelude + '\n' + SRC + '\n' + postlude)(state);
}

// ---- catalogueEntryUrl ----

test('catalogueEntryUrl: prefixes the first declared protocol', () => {
    const sb = loadHelpers();
    assert.equal(
        sb.catalogueEntryUrl({ url: 'http://h:1/graphql', protocols: ['graphql', 'rest'] }),
        'graphql@http://h:1/graphql');
});

test('catalogueEntryUrl: leaves an already-hinted URL alone', () => {
    const sb = loadHelpers();
    assert.equal(
        sb.catalogueEntryUrl({ url: 'grpcweb@http://h:1', protocols: ['grpc'] }),
        'grpcweb@http://h:1');
});

test('catalogueEntryUrl: no protocols → the bare URL', () => {
    const sb = loadHelpers();
    assert.equal(sb.catalogueEntryUrl({ url: 'https://h' }), 'https://h');
    assert.equal(sb.catalogueEntryUrl({ url: 'https://h', protocols: [] }), 'https://h');
});

test('catalogueEntryUrl: a scheme is not mistaken for a hint', () => {
    // 'https://…' must not be read as a "https@" style prefix — the ':'
    // rejects it before the '@' probe can fire.
    const sb = loadHelpers();
    assert.equal(sb.catalogueEntryUrl({ url: 'https://h/p', protocols: ['rest'] }),
        'rest@https://h/p');
});

test('catalogueEntryUrl: missing / empty entry → empty string', () => {
    const sb = loadHelpers();
    assert.equal(sb.catalogueEntryUrl(null), '');
    assert.equal(sb.catalogueEntryUrl({}), '');
    assert.equal(sb.catalogueEntryUrl({ url: '' }), '');
});

// ---- catalogueFilterEntries ----

const ENTRIES = [
    { url: 'https://pay.example.com', name: 'Payments', protocols: ['grpc'], tags: ['env:prod', 'team:pay'] },
    { url: 'https://pay-staging.example.com', name: 'Payments (staging)', protocols: ['grpc'], tags: ['env:staging', 'team:pay'] },
    { url: 'https://shop.example.com/graphql', name: 'Shop', protocols: ['graphql'], tags: ['env:prod'] },
    { url: 'https://nameless.example.com' }
];

test('catalogueFilterEntries: empty query + no tag → everything', () => {
    const sb = loadHelpers({ entries: ENTRIES });
    assert.equal(sb.catalogueFilterEntries('', null).length, 4);
    assert.equal(sb.catalogueFilterEntries(null, null).length, 4);
});

test('catalogueFilterEntries: matches name case-insensitively', () => {
    const sb = loadHelpers({ entries: ENTRIES });
    const hits = sb.catalogueFilterEntries('PAYMENTS', null);
    assert.deepEqual(hits.map(e => e.name), ['Payments', 'Payments (staging)']);
});

test('catalogueFilterEntries: matches URL substring', () => {
    const sb = loadHelpers({ entries: ENTRIES });
    assert.deepEqual(sb.catalogueFilterEntries('graphql', null).map(e => e.name), ['Shop']);
});

test('catalogueFilterEntries: matches protocol', () => {
    const sb = loadHelpers({ entries: ENTRIES });
    assert.equal(sb.catalogueFilterEntries('grpc', null).length, 2);
});

test('catalogueFilterEntries: tag is exact membership, not substring', () => {
    const sb = loadHelpers({ entries: ENTRIES });
    assert.equal(sb.catalogueFilterEntries('', 'env:prod').length, 2);
    // 'env' alone is not a tag anyone carries.
    assert.equal(sb.catalogueFilterEntries('', 'env').length, 0);
});

test('catalogueFilterEntries: query and tag compose', () => {
    const sb = loadHelpers({ entries: ENTRIES });
    const hits = sb.catalogueFilterEntries('payments', 'env:staging');
    assert.deepEqual(hits.map(e => e.name), ['Payments (staging)']);
});

test('catalogueFilterEntries: drops entries with no url', () => {
    const sb = loadHelpers({ entries: [{ name: 'orphan' }, { url: 'https://ok' }] });
    assert.equal(sb.catalogueFilterEntries('', null).length, 1);
});

test('catalogueFilterEntries: is side-effect free', () => {
    const sb = loadHelpers({ entries: ENTRIES, serverUrls: ['https://kept'] });
    sb.catalogueFilterEntries('pay', 'env:prod');
    assert.equal(sb._entries().length, 4);
    assert.deepEqual(sb._serverUrls(), ['https://kept']);
});

// ---- catalogueAllTags ----

test('catalogueAllTags: deduped + sorted union', () => {
    const sb = loadHelpers({ entries: ENTRIES });
    assert.deepEqual(sb.catalogueAllTags(), ['env:prod', 'env:staging', 'team:pay']);
});

test('catalogueAllTags: no tags anywhere → []', () => {
    const sb = loadHelpers({ entries: [{ url: 'https://a' }] });
    assert.deepEqual(sb.catalogueAllTags(), []);
});

// ---- catalogueEntryByUrl ----

test('catalogueEntryByUrl: resolves by the COMPOSED url', () => {
    // This is the morphdom contract in one assertion: a click handler
    // captures only the composed string and looks the entry back up, so
    // the lookup has to key on the composed form.
    const sb = loadHelpers({ entries: ENTRIES });
    const hit = sb.catalogueEntryByUrl('graphql@https://shop.example.com/graphql');
    assert.equal(hit && hit.name, 'Shop');
});

test('catalogueEntryByUrl: an entry that left the catalogue resolves to null', () => {
    const sb = loadHelpers({ entries: ENTRIES });
    assert.equal(sb.catalogueEntryByUrl('rest@https://gone.example.com'), null);
});

// ---- read accessors ----

test('accessors: default state reads as "no catalogue"', () => {
    const sb = loadHelpers();
    assert.equal(sb.catalogueIsAvailable(), false);
    assert.equal(sb.catalogueHasEntries(), false);
    assert.equal(sb.catalogueEntryCount(), 0);
    assert.equal(sb.catalogueProviderLabel(), null);
    assert.equal(sb.catalogueVisibility(), 'editable');
    assert.deepEqual(sb.catalogueProviderIds(), []);
});

test('accessors: providerLabel prefers the name, falls back to the id', () => {
    assert.equal(loadHelpers({ info: { available: true, providerId: 'consul', providerName: 'Consul' } })
        .catalogueProviderLabel(), 'Consul');
    assert.equal(loadHelpers({ info: { available: true, providerId: 'consul' } })
        .catalogueProviderLabel(), 'consul');
});

test('accessors: visibility falls back to editable on an older host', () => {
    // A host that predates the visibility field must not accidentally
    // read as 'hidden' and suppress URL management.
    assert.equal(loadHelpers({ info: { available: true } }).catalogueVisibility(), 'editable');
    assert.equal(loadHelpers({ info: { available: true, visibility: 'readonly' } })
        .catalogueVisibility(), 'readonly');
});

test('accessors: providerIds is [] when the host omits the field', () => {
    // Empty means "can't tell" — the Settings picker keeps every row
    // enabled rather than greying out providers it has no data about.
    assert.deepEqual(loadHelpers({ info: { available: true } }).catalogueProviderIds(), []);
    assert.deepEqual(
        loadHelpers({ info: { available: true, providers: [{ id: 'local' }, { id: 'http' }, {}] } })
            .catalogueProviderIds(),
        ['local', 'http']);
});

test('accessors: catalogueOriginFor tolerates a missing url', () => {
    const sb = loadHelpers();
    assert.equal(sb.catalogueOriginFor(null), null);
    assert.equal(sb.catalogueOriginFor('https://never-seen'), null);
});

test('accessors: isCatalogueUrl is false before anything was merged', () => {
    // prologue.js's persistServerUrls calls this, and prologue.js is
    // concatenated long before catalogue.js — so it must answer, not
    // throw, at every point in the bundle's evaluation.
    const sb = loadHelpers();
    assert.equal(sb.isCatalogueUrl('https://x'), false);
    assert.equal(sb.isCatalogueUrl(null), false);
});

// ---- boot sequencing (init.js) ----

test('init.js runs the catalogue load BEFORE the first discovery fan-out', () => {
    // Regression guard for a bug that was invisible in unit tests and
    // obvious in the browser: with the two kicked off in parallel,
    // fetchServices() fanned out over serverUrls as it was BEFORE the
    // catalogue merged, so every catalogue row sat at "Disconnected ·
    // 0 svcs" until the operator hit Retry on each one.
    const init = FRAGMENT('init.js');
    assert.match(init, /initialCatalogueLoad\(\)\.then\(fetchServices,\s*fetchServices\)/,
        'catalogue load must be sequenced before the boot fetchServices()');
    // The old parallel shape must be gone — a bare initialCatalogueLoad()
    // followed by an unconditional fetchServices() is the broken pattern.
    assert.doesNotMatch(init, /initialCatalogueLoad\(\);\s*\n\s*\}\s*\n\s*fetchServices\(\);/,
        'the parallel boot shape is back');
});

test('catalogue.js does not call fetchServices from the boot loader', () => {
    // Two overlapping fetchServices() runs both write isLoadingServices
    // and the workbench wedges on the loading spinner. The manual
    // refresh path (refreshCatalogueNow) may call it — nothing else is
    // in flight there.
    const boot = SRC.slice(SRC.indexOf('async function initialCatalogueLoad'));
    const end = boot.indexOf('\n    }');
    // Strip line comments first — the function's own doc comment names
    // the symbol it must not call, and a naive substring check would
    // pass on any bundle that merely mentions it.
    const code = boot.slice(0, end).replace(/\/\/.*$/gm, '');
    assert.doesNotMatch(code, /fetchServices\(/);
});

test('accessors: reading twice changes nothing', () => {
    const sb = loadHelpers({ entries: ENTRIES, info: { available: true, providerId: 'local' } });
    const before = JSON.stringify(sb._entries());
    for (let i = 0; i < 3; i++) {
        sb.catalogueHasEntries();
        sb.catalogueEntryCount();
        sb.catalogueVisibility();
        sb.catalogueAllTags();
        sb.catalogueProviderIds();
    }
    assert.equal(JSON.stringify(sb._entries()), before);
});
