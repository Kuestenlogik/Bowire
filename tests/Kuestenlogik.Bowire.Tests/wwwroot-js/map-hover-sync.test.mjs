// The JSON → map half of the hover-sync: the response-tree decorator
// the map bundle registers stamps `data-bowire-coord-path` on the
// viewer's lat/lon spans, and a mouseover on a stamped span asks every
// mounted map widget to highlight that path.
//
// The tree's `data-json-path` is the dot-only chain form
// (`situationObjects.13.…`); the effective annotations carry the
// bracket form (`situationObjects[13].…`). The decorator has to bring
// the two together, or nothing under an array index is ever stamped —
// which is how the hover stayed dead for every multi-entity frame
// until this test existed.

import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { compileFragment } from './_load-fragment.mjs';

const SRC = '../../../src/Kuestenlogik.Bowire.Map/wwwroot/js/widgets/map.js';

function makeSpan(jsonPath) {
    const attrs = { 'data-json-path': jsonPath };
    const span = {
        getAttribute: (k) => (k in attrs ? attrs[k] : null),
        setAttribute: (k, v) => { attrs[k] = v; },
        classList: { add() {}, remove() {} },
        closest(selector) { return selector === '[data-bowire-coord-path]' && 'data-bowire-coord-path' in attrs ? span : null; },
        attrs,
    };
    return span;
}

function makeTree(spans) {
    const listeners = {};
    return {
        spans,
        querySelectorAll: (selector) => (selector === '[data-json-path]' ? spans : []),
        addEventListener: (evt, cb) => { (listeners[evt] = listeners[evt] || []).push(cb); },
        fire: (evt, e) => (listeners[evt] || []).forEach((cb) => cb(e)),
    };
}

function load({ annotations }) {
    let decorator = null;
    const win = {
        __BOWIRE_CONFIG__: { mapBasemap: 'none' },
        __bowireExtFramework: {
            register() {}, markBuiltIn() {},
            effectiveCacheFor: () => annotations,
        },
        __bowireMapWidgets: [],
    };
    win.BowireExtensions = {
        registerResponseTreeDecorator: (fn) => { decorator = fn; },
        registerResponseTreeMenuContributor() {},
    };
    const document = {
        head: { appendChild() {}, children: [] }, currentScript: null,
        createElement: () => ({ style: {}, appendChild() {}, setAttribute() {}, classList: { add() {} } }),
        getElementById() { return null; }, querySelectorAll() { return []; },
        addEventListener() {}, removeEventListener() {},
        documentElement: { getAttribute() { return 'dark'; } },
    };
    compileFragment(SRC, ['window', 'document', 'Image'], '')({ window: win, document, Image: class {} });
    assert.ok(decorator, 'the map bundle registers a response-tree decorator');
    return { win, decorator };
}

const settle = async () => { for (let i = 0; i < 4; i++) await new Promise((r) => setTimeout(r, 0)); };

// What /api/semantics/effective says for the TacticalAPI sample: one
// pair per vertex, bracket form, `$.` prefix.
const ANNOTATIONS = [
    { semantic: 'coordinate.latitude', jsonPath: '$.situationObjects[0].symbol.location.content.point.geoPoint.latitudeCoordinate' },
    { semantic: 'coordinate.longitude', jsonPath: '$.situationObjects[0].symbol.location.content.point.geoPoint.longitudeCoordinate' },
    { semantic: 'coordinate.latitude', jsonPath: '$.situationObjects[13].symbol.location.content.line.points[1].latitudeCoordinate' },
    { semantic: 'coordinate.longitude', jsonPath: '$.situationObjects[13].symbol.location.content.line.points[1].longitudeCoordinate' },
];

describe('map widget — JSON → map hover-sync decorator', { concurrency: 1 }, () => {

    it('stamps the coord path on the tree\'s dotted spans, under an array index too', async () => {
        const { decorator } = load({ annotations: ANNOTATIONS });
        const tree = makeTree([
            makeSpan('header.success'),
            makeSpan('situationObjects.0.symbol.location.content.point.geoPoint'),
            makeSpan('situationObjects.0.symbol.location.content.point.geoPoint.latitudeCoordinate'),
            makeSpan('situationObjects.13.symbol.location.content.line.points.1.latitudeCoordinate'),
            makeSpan('situationObjects.13.symbol.location.content.line.points.1.longitudeCoordinate'),
            makeSpan('situationObjects.13.symbol.location.content.line.points'),
        ]);
        decorator({ treeRoot: tree, service: 'Situation', method: 'SubscribeSituationObjectEvents', explicitRoot: {} });
        await settle();

        const stamped = tree.spans.map((s) => s.attrs['data-bowire-coord-path'] || null);
        assert.deepEqual(stamped, [
            null,
            // parent and lat of the pin's pair → the pair's lat path, bracket form
            'situationObjects[0].symbol.location.content.point.geoPoint.latitudeCoordinate',
            'situationObjects[0].symbol.location.content.point.geoPoint.latitudeCoordinate',
            // both halves of the vertex's pair
            'situationObjects[13].symbol.location.content.line.points[1].latitudeCoordinate',
            'situationObjects[13].symbol.location.content.line.points[1].latitudeCoordinate',
            // the points array itself is nobody's parent
            null,
        ]);
    });

    it('asks every mounted widget to highlight the hovered path and clears it on leave', async () => {
        const { win, decorator } = load({ annotations: ANNOTATIONS });
        const calls = [];
        win.__bowireMapWidgets.push({ highlightByPath: (p) => calls.push(['on', p]), clearHighlight: () => calls.push(['off']) });
        const vertex = makeSpan('situationObjects.13.symbol.location.content.line.points.1.latitudeCoordinate');
        const tree = makeTree([vertex]);
        decorator({ treeRoot: tree, service: 'Situation', method: 'SubscribeSituationObjectEvents', explicitRoot: {} });
        await settle();

        tree.fire('mouseover', { target: vertex });
        tree.fire('mouseout', { target: vertex, relatedTarget: null });
        assert.deepEqual(calls, [
            ['on', 'situationObjects[13].symbol.location.content.line.points[1].latitudeCoordinate'],
            ['off'],
        ]);
    });
});
