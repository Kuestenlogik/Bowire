// #551 — what the sidebar is allowed to BUILD per render.
//
// `renderSidebar` used to run its `for (const m of filteredMethods)` loop
// for every service, expanded or not, and let the CSS hide the collapsed
// lists with `display: none`. Collapsing the tree bought paint and
// nothing else: the DOM subtree, the morphdom diff and the per-row
// coverage / favourite / cross-feature lookups were all still paid.
//
// The rows now come from `buildServiceMethodList`, which returns an
// empty container when the group is collapsed. These tests count
// elements and lookups rather than timing them, so they stay meaningful
// on any machine.

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { compileFragment } from './_load-fragment.mjs';

// render-sidebar.js is one fragment of the shared IIFE. It declares no
// top-level side effects beyond a `window`-guarded rail table, so it
// loads standalone once the collaborators the row builder reads at
// BUILD time (not the ones it only reads inside event handlers) are
// stubbed in the appended block. Compiled alone with its real filename so
// V8 attributes coverage to render-sidebar.js (#367).
const _prelude = `
        var window = { __BOWIRE_CONFIG__: { rails: [] } };

        var _elCalls = 0;
        var _lookups = { coverage: 0, favorite: 0, job: 0, watchMark: 0 };

        // Countable stand-in for helpers.js' el(). Keeps just enough
        // shape for the builders: a class name and a child list.
        function el(tag, props) {
            _elCalls++;
            var rest = Array.prototype.slice.call(arguments, 2);
            var node = {
                tag: tag,
                className: (props && props.className) || '',
                children: rest.filter(function (c) { return c != null; })
            };
            node.appendChild = function (c) {
                if (c != null) node.children.push(c);
                return c;
            };
            return node;
        }

        var selectedMethod = null;
        function svgIcon() { return ''; }
        function methodDirection() { return 'neutral'; }
        function methodBadgeType() { return 'unary'; }
        function methodBadgeText() { return 'U'; }
        function isJobActive() { _lookups.job++; return false; }
        function isFavorite() { _lookups.favorite++; return false; }
        function renderCoverageChip() {
            _lookups.coverage++;
            return el('span', { className: 'bowire-coverage-chip' });
        }
        function schemaWatchMarkerFor() { _lookups.watchMark++; return null; }
`;
const _postlude = `
        return {
            buildServiceMethodList: buildServiceMethodList,
            buildMethodRow: buildMethodRow,
            _elCalls: function () { return _elCalls; },
            _lookups: function () { return _lookups; },
            _reset: function () {
                _elCalls = 0;
                _lookups.coverage = 0;
                _lookups.favorite = 0;
                _lookups.job = 0;
                _lookups.watchMark = 0;
            }
        };
    `;
const _load = compileFragment(
    '../../../src/Kuestenlogik.Bowire/wwwroot/js/render-sidebar.js',
    [],
    _prelude + '\n' + _postlude
);

function load() {
    return _load();
}

// A catalogue of `services` services with `methods` methods each.
function catalogue(services, methods) {
    const out = [];
    for (let s = 0; s < services; s++) {
        const svc = { name: 'demo.Svc' + s, source: 'grpc', methods: [] };
        for (let m = 0; m < methods; m++) {
            svc.methods.push({
                name: 'M' + m,
                fullName: svc.name + '::M' + m,
                methodType: 'Unary'
            });
        }
        out.push(svc);
    }
    return out;
}

// Replays what renderSidebar's service loop does per group: hand each
// service its filtered method list plus its expansion state, and keep
// whatever comes back.
function renderTree(sb, services, expandedNames) {
    const lists = [];
    for (const svc of services) {
        lists.push(sb.buildServiceMethodList(
            svc, svc.methods, expandedNames.has(svc.name), new Map()));
    }
    return lists;
}

const SERVICES = 40;
const METHODS = 30;

test('collapsed services build no rows at all', () => {
    const sb = load();
    const services = catalogue(SERVICES, METHODS);

    sb._reset();
    const lists = renderTree(sb, services, new Set(['demo.Svc7']));

    const rows = lists.reduce((n, l) => n + l.children.length, 0);
    assert.equal(rows, METHODS,
        `only the expanded service may contribute rows; got ${rows} for a catalogue of ${SERVICES * METHODS} methods`);

    // Every service still gets its (empty) list container, so the
    // group's DOM shape — and morphdom's keyed match on it — is unchanged.
    assert.equal(lists.length, SERVICES);
    for (const l of lists) assert.equal(l.tag, 'div');
});

test('element count is proportional to the expanded service, not the catalogue', () => {
    const sb = load();
    const services = catalogue(SERVICES, METHODS);

    // Cost of one row, measured rather than assumed.
    sb._reset();
    sb.buildMethodRow(services[0], services[0].methods[0], new Map());
    const perRow = sb._elCalls();
    assert.ok(perRow > 1, 'a row is more than one element');

    sb._reset();
    renderTree(sb, services, new Set(['demo.Svc7']));
    const oneExpanded = sb._elCalls();

    sb._reset();
    renderTree(sb, services, new Set(services.map(s => s.name)));
    const allExpanded = sb._elCalls();

    // One container per service either way; rows only for what is open.
    assert.equal(oneExpanded, SERVICES + METHODS * perRow);
    assert.equal(allExpanded, SERVICES + SERVICES * METHODS * perRow);

    // The whole point: the collapsed catalogue does not scale with it.
    assert.ok(oneExpanded * 10 < allExpanded,
        `one open group cost ${oneExpanded} elements against ${allExpanded} for the whole tree`);
});

test('per-row localStorage-backed lookups follow the rows', () => {
    // coverage / favourites are the reads #551 measured at ~0.1-1.7 ms
    // each. Building a collapsed row paid them for nothing on screen.
    const sb = load();
    const services = catalogue(SERVICES, METHODS);

    sb._reset();
    renderTree(sb, services, new Set(['demo.Svc7']));
    const one = sb._lookups();

    assert.equal(one.coverage, METHODS);
    assert.equal(one.job, METHODS);
    assert.equal(one.watchMark, METHODS);
    // Two per row: the star, plus the favourites entry in the row's
    // context menu is built lazily so it does not count here.
    assert.equal(one.favorite, METHODS);
});

test('an expanded service still builds every one of its rows', () => {
    const sb = load();
    const services = catalogue(3, 12);
    const lists = renderTree(sb, services, new Set(services.map(s => s.name)));

    for (let i = 0; i < lists.length; i++) {
        assert.equal(lists[i].children.length, 12);
        for (const row of lists[i].children) {
            assert.ok(row.className.indexOf('bowire-method-item') === 0,
                'rows keep their class so CSS + the screenshot harness still match');
        }
    }
});

test('the container keeps the expanded/collapsed class either way', () => {
    // The CSS contract is unchanged: `.bowire-method-list.expanded`
    // shows, the bare class hides. Only the contents are conditional.
    const sb = load();
    const svc = catalogue(1, 4)[0];

    const open = sb.buildServiceMethodList(svc, svc.methods, true, new Map());
    const shut = sb.buildServiceMethodList(svc, svc.methods, false, new Map());

    assert.match(open.className, /bowire-method-list expanded/);
    assert.match(shut.className, /bowire-method-list/);
    assert.doesNotMatch(shut.className, /expanded/);
});

test('a filtered method list only builds what survived the filter', () => {
    // renderSidebar forces isExpanded true for any group a search
    // matched, and passes the already-filtered list — so a search still
    // paints its hits, and only its hits.
    const sb = load();
    const svc = catalogue(1, 50)[0];
    const hits = svc.methods.filter(m => m.name === 'M3' || m.name === 'M17');

    const list = sb.buildServiceMethodList(svc, hits, true, new Map());
    assert.equal(list.children.length, 2);
});
