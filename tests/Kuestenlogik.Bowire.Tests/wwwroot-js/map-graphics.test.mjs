// Tactical graphics on the map — the multipoint symbols a frame carries
// as a geometry rather than a point: boundaries, phase lines, areas,
// axes of advance, corridors, range fans, ellipses. The WGS84 detector
// hands the widget one coordinate per vertex; the widget puts the
// vertices back together from the paths they arrived under and asks
// mil-sym-ts to draw the graphic for the current view.
//
// mil-sym-ts itself is stubbed: it measures text through a canvas node
// does not have, and what these tests pin is the widget's side of the
// contract — what is grouped, what is sent, how the answer is drawn, and
// what stands in when there is no answer.

import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { compileFragment } from './_load-fragment.mjs';

const SRC = '../../../src/Kuestenlogik.Bowire.Map/wwwroot/js/widgets/map.js';

// ---------------------------------------------------------------
// Stubs (same shape as map-symbols.test.mjs, plus a 2d canvas)
// ---------------------------------------------------------------

function makeElement(tag) {
    const el = {
        tagName: String(tag || 'div').toUpperCase(),
        style: {}, dataset: {}, children: [], parentNode: null,
        clientWidth: 800, clientHeight: 600,
        textContent: '', className: '', value: '', checked: false,
        type: '', placeholder: '', title: '', hidden: false, selected: false,
        listeners: {},
        appendChild(c) { this.children.push(c); c.parentNode = this; return c; },
        removeChild(c) {
            const i = this.children.indexOf(c);
            if (i >= 0) this.children.splice(i, 1);
            c.parentNode = null; return c;
        },
        addEventListener(e, cb) { (this.listeners[e] = this.listeners[e] || []).push(cb); },
        querySelectorAll() { return []; },
        classList: { add() {}, remove() {} },
    };
    if (el.tagName === 'CANVAS') {
        el.width = 0; el.height = 0;
        el.getContext = () => ({
            font: '', textBaseline: '', textAlign: '', lineJoin: '', lineWidth: 0,
            strokeStyle: '', fillStyle: '',
            measureText: (text) => ({ width: text.length * 10 }),
            strokeText() {}, fillText() {},
            getImageData: (x, y, w, h) => ({ width: w, height: h, data: new Uint8ClampedArray(w * h * 4) }),
        });
    }
    return el;
}

function makeDocument() {
    const head = makeElement('head');
    return {
        head, currentScript: null,
        createElement: makeElement,
        getElementById() { return null; },
        querySelectorAll() { return []; },
        addEventListener() {}, removeEventListener() {},
        dispatchEvent() { return true; },
        documentElement: { getAttribute() { return 'dark'; } },
    };
}

function makeImage() {
    return class Image {
        constructor(w, h) { this.width = w; this.height = h; }
        set src(v) { this._src = v; setTimeout(() => this.onload && this.onload(), 0); }
        get src() { return this._src; }
    };
}

function makeMapLibre(recorder) {
    class LngLatBounds {
        constructor(a, b) { this.sw = a; this.ne = b; }
        extend() { return this; }
    }
    function Map_(opts) {
        const container = opts && opts.container;
        const self = {
            _loadCbs: [], _handlers: {},
            on(evt, cbOrLayer, maybeCb) {
                if (evt === 'load') self._loadCbs.push(cbOrLayer);
                const cb = typeof cbOrLayer === 'function' ? cbOrLayer : maybeCb;
                (self._handlers[evt] = self._handlers[evt] || []).push(cb);
            },
            fire(evt, e) { (self._handlers[evt] || []).forEach((cb) => cb(e)); },
            hasImage(name) { return recorder.images.has(name); },
            addImage(name, img, opts) { recorder.images.set(name, { img, opts }); },
            removeImage(name) { recorder.images.delete(name); },
            addControl(ctrl) { if (ctrl && typeof ctrl.onAdd === 'function') ctrl.onAdd(self); },
            addSource(id, spec) { recorder.sources[id] = spec.data; },
            addLayer(spec) { recorder.layers.push(spec); },
            getSource(id) {
                if (!(id in recorder.sources)) return null;
                return { setData(data) { recorder.sources[id] = data; recorder.renders++; } };
            },
            getContainer() { return container; },
            getBounds() {
                const b = recorder.bounds;
                return {
                    getWest: () => b[0], getSouth: () => b[1], getEast: () => b[2], getNorth: () => b[3],
                };
            },
            setLayoutProperty() {},
            fitBounds() {}, flyTo() {}, resize() {}, remove() {},
        };
        recorder.map = self;
        setTimeout(() => self._loadCbs.forEach((cb) => cb()), 0);
        return self;
    }
    return {
        Map: Map_, LngLatBounds,
        NavigationControl: function () { return { onAdd: () => makeElement('div'), onRemove() {} }; },
        Marker: function () { return { setLngLat: () => ({ addTo: () => ({ on() {} }) }) }; },
    };
}

function makePipe() {
    const queue = [];
    let wake = null;
    const iterator = {
        [Symbol.asyncIterator]() { return iterator; },
        next() {
            if (queue.length) return Promise.resolve({ value: queue.shift(), done: false });
            return new Promise((r) => { wake = r; });
        },
    };
    return {
        iterator,
        push(v) {
            if (wake) { const w = wake; wake = null; w({ value: v, done: false }); }
            else queue.push(v);
        },
    };
}

const settle = async (n = 8) => {
    for (let i = 0; i < n; i++) await new Promise((r) => setTimeout(r, 0));
};

/**
 * A stand-in for mil-sym-ts's WebRenderer: records every call and
 * answers with what the real one answers with — a GeoJSON string of
 * strokes, fills, labels and a trailing metadata polygon with no
 * coordinates — or with its error envelope when told to refuse.
 */
function makeC5Ren(recorder, { refuse } = {}) {
    return {
        WebRenderer: {
            OUTPUT_FORMAT_GEOJSON: 2,
            RenderSymbol2D(id, name, description, symbolCode, controlPoints, width, height, bbox, modifiers, attributes, format) {
                const call = {
                    id, name, symbolCode, controlPoints, width, height, bbox, format,
                    modifiers: Object.fromEntries(modifiers), attributes: Object.fromEntries(attributes),
                };
                recorder.calls.push(call);
                if (refuse && refuse(call)) {
                    return JSON.stringify({ type: 'error', error: 'Basic ID: 25150200 requires a minimum of 3 points. 2 are present.' });
                }
                const pts = controlPoints.split(' ').map((p) => p.split(',').map(Number));
                const hostile = symbolCode.charAt(3) === '6';
                const features = [
                    {
                        type: 'Feature',
                        geometry: { type: 'MultiLineString', coordinates: [pts] },
                        properties: { strokeColor: hostile ? '#FF0000' : '#000000', strokeWidth: 2, lineOpacity: 1 },
                    },
                    {
                        type: 'Feature',
                        geometry: { type: 'MultiLineString', coordinates: [pts] },
                        properties: { strokeColor: '#000000', strokeWidth: 2, lineOpacity: 1, strokeDasharray: [6, 6] },
                    },
                    {
                        type: 'Feature',
                        geometry: { type: 'Point', coordinates: pts[0] },
                        properties: {
                            label: 'PL ' + (modifiers.get('T_UNIQUE_DESIGNATION_1') || ''),
                            fontColor: '#000000', fontSize: '12pt', fontFamily: 'arial, sans-serif', fontWeight: 'bold',
                            labelAlign: 'right', anchorOffsetX: -6, anchorOffsetY: 0, angle: 21.5,
                            labelOutlineColor: '#FFFFFF', labelOutlineWidth: 4,
                        },
                    },
                    {
                        type: 'Feature',
                        geometry: { type: 'Point', coordinates: pts[0] },
                        properties: { label: 'Min Alt: ', fontColor: '#000000', fontSize: '12pt', labelAlign: 'center' },
                    },
                    {
                        type: 'Feature',
                        geometry: { type: 'Polygon', coordinates: [] },
                        properties: { id, symbolID: symbolCode, wasClipped: 'false' },
                    },
                ];
                if (pts.length >= 3) {
                    features.push({
                        type: 'Feature',
                        geometry: { type: 'Polygon', coordinates: [pts.concat([pts[0]])] },
                        properties: { strokeColor: '#557788', strokeWidth: 2, lineOpacity: 1, fillColor: '#557788', fillOpacity: 0.25 },
                    });
                }
                return JSON.stringify({ type: 'FeatureCollection', features });
            },
        },
    };
}

async function mountMap({ interpretations, withC5Ren = true, refuse } = {}) {
    const recorder = {
        sources: {}, layers: [], images: new Map(), renders: 0, calls: [],
        bounds: [10.0, 53.8, 12.0, 54.5],
    };
    const document = makeDocument();
    const win = {
        __BOWIRE_CONFIG__: { mapBasemap: 'none' },
        maplibregl: makeMapLibre(recorder),
    };
    if (withC5Ren) win.C5Ren = makeC5Ren(recorder, { refuse });
    let registered = null;
    win.__bowireExtFramework = { register(s) { registered = s; }, markBuiltIn() {} };
    compileFragment(SRC, ['window', 'document', 'Image'], '')({
        window: win, document, Image: makeImage(),
    });

    const frames = makePipe();
    const selection = makePipe();
    const store = (bag) => ({
        get: (k, d) => (k in bag ? bag[k] : d),
        set: (k, v) => { bag[k] = v; return true; },
    });
    const prefs = store({});
    prefs.method = store({});

    const container = makeElement('div');
    const unmount = await registered.viewer.mount(container, {
        frames$: frames.iterator,
        selection$: selection.iterator,
        interpretations,
        theme: { mode: 'dark', accent: '#4f46e5' },
        viewport: { width: 800, height: 600, on() { return () => {}; } },
        prefs,
    });
    await settle(2);

    const handle = (win.__bowireMapWidgets || []).find((h) => h.container === container) || {};
    return {
        recorder, frames, selection, unmount, handle, win, document,
        points: () => recorder.sources['bowire-points'],
        lines: () => recorder.sources['bowire-graphics-lines'],
        fills: () => recorder.sources['bowire-graphics-fill'],
        labels: () => recorder.sources['bowire-graphics-labels'],
        labelImages: () => [...recorder.images.keys()].filter((k) => k.startsWith('bowire-lbl-')),
    };
}

// ---------------------------------------------------------------
// Frames in the TacticalAPI sample's shape
// ---------------------------------------------------------------

const geo = (lat, lon) => ({ latitudeCoordinate: lat, longitudeCoordinate: lon });
const pairAt = (prefix) => ({
    'coordinate.latitude': `${prefix}.latitudeCoordinate`,
    'coordinate.longitude': `${prefix}.longitudeCoordinate`,
});
const numericId = (sidc, asStrings) => ({
    symbolCatalog: 'SYMBOL_CATALOG_MIL2525_D',
    numericIdentifier: asStrings
        ? { firstTenDigits: sidc.slice(0, 10), secondTenDigits: sidc.slice(10) }
        : { firstTenDigits: Number(sidc.slice(0, 10)), secondTenDigits: Number(sidc.slice(10)) },
});
const symbol = (uuid, identifier, name, location) => ({
    symbol: {
        identity: { uuidIdentity: uuid },
        name: { content: name },
        symbolIdentifier: { content: identifier },
        location: { content: location },
    },
});

const ASSEMBLY_AREA = '10032500001502000000';
const PHASE_LINE = '10032500001403000000';
const AIR_CORRIDOR = '10032500001701000000';
const RANGE_FAN = '10032500002422000000';
const DEFENDED_AREA_HOSTILE = '10062500002002010000';
const FRIEND_TRACK_C = 'SFGPUCV---*****';

// Object 0: a moving track (point). Object 1: an assembly area with
// four vertices. The detector paired every geoPoint it saw.
const TRACK_AND_AREA_PATHS = [
    pairAt('$.situationObjects[0].symbol.location.content.point.geoPoint'),
    ...[0, 1, 2, 3].map((i) => pairAt(`$.situationObjects[1].symbol.location.content.polygon.points[${i}]`)),
];
const AREA_POINTS = [geo(54.11, 10.16), geo(54.115, 10.22), geo(54.09, 10.25), geo(54.07, 10.16)];
function trackAndAreaFrame(id, asStrings = false) {
    return {
        id,
        situationObjects: [
            symbol('t', { symbolCatalog: 'SYMBOL_CATALOG_MIL2525_C', stringIdentifier: FRIEND_TRACK_C }, 'Convoy',
                { point: { geoPoint: geo(54.09, 10.20) } }),
            symbol('aa', numericId(ASSEMBLY_AREA, asStrings), 'BUCHE',
                { polygon: { points: AREA_POINTS } }),
        ],
    };
}

describe('map widget — tactical graphics via mil-sym-ts', { concurrency: 1 }, () => {

    it('registers the graphics layers under the trajectory and the pins', async () => {
        const m = await mountMap({ interpretations: TRACK_AND_AREA_PATHS });
        const ids = m.recorder.layers.map((l) => l.id);
        const graphicsIds = ['bowire-graphics-fill-layer', 'bowire-graphics-casing-layer',
            'bowire-graphics-lines-layer', 'bowire-graphics-dashed-layer', 'bowire-graphics-labels-layer'];
        for (const id of graphicsIds) assert.ok(ids.includes(id), id);
        const lastGraphic = Math.max(...graphicsIds.map((id) => ids.indexOf(id)));
        assert.ok(lastGraphic < ids.indexOf('bowire-lines-layer'), 'graphics paint under the trajectory');
        assert.ok(lastGraphic < ids.indexOf('bowire-points-layer'), 'graphics paint under the pins');
        // Labels are sprites — no text-field, the offline lockdown allows no glyphs.
        const labels = m.recorder.layers.find((l) => l.id === 'bowire-graphics-labels-layer');
        assert.equal(labels.layout['text-field'], undefined);
        assert.deepEqual(labels.layout['icon-image'], ['get', 'sprite']);
        m.unmount();
    });

    it('turns the vertices of an area into one graphic and leaves the track a pin', async () => {
        const m = await mountMap({ interpretations: TRACK_AND_AREA_PATHS });
        m.frames.push(trackAndAreaFrame('f1'));
        await settle();

        // One pin — the convoy. The four vertices never became pins.
        const pins = m.points().features;
        assert.equal(pins.length, 1);
        assert.equal(pins[0].properties.sidc, FRIEND_TRACK_C);

        const g = m.handle.graphics();
        assert.equal(g.library, 'loaded');
        assert.equal(g.items.length, 1);
        assert.deepEqual(g.items[0], {
            key: '$.situationObjects[1].symbol.location.content.polygon.points',
            kind: 'polygon', sidc: ASSEMBLY_AREA, points: 4, designation: 'BUCHE', drawn: true,
        });

        // The renderer got the vertices in order, as "lon,lat", the
        // designation as T, the view as pixel size and bbox.
        assert.equal(m.recorder.calls.length, 1);
        const call = m.recorder.calls[0];
        assert.equal(call.symbolCode, ASSEMBLY_AREA);
        assert.equal(call.controlPoints, '10.16,54.11 10.22,54.115 10.25,54.09 10.16,54.07');
        assert.equal(call.modifiers.T_UNIQUE_DESIGNATION_1, 'BUCHE');
        assert.equal(call.width, 800);
        assert.equal(call.height, 600);
        assert.equal(call.bbox, '10,53.8,12,54.5');
        assert.equal(call.attributes.LINEWIDTH, '2');
        assert.equal(call.format, 2);
        m.unmount();
    });

    it('reads the twenty-digit code from the two ten-digit halves, as numbers or as strings', async () => {
        for (const asStrings of [false, true]) {
            const m = await mountMap({ interpretations: TRACK_AND_AREA_PATHS });
            m.frames.push(trackAndAreaFrame('f1', asStrings));
            await settle();
            assert.equal(m.handle.graphics().items[0].sidc, ASSEMBLY_AREA, `as strings: ${asStrings}`);
            m.unmount();
        }
    });

    it('routes strokes, fills and labels to their sources and drops what is not drawable', async () => {
        const m = await mountMap({ interpretations: TRACK_AND_AREA_PATHS });
        m.frames.push(trackAndAreaFrame('f1'));
        await settle();

        // Two strokes from the renderer, one solid and one dashed, plus
        // the filled polygon's outline; the metadata polygon with no
        // coordinates is gone.
        const lines = m.lines().features;
        assert.equal(lines.length, 3);
        assert.deepEqual(lines.map((f) => f.properties.dashed), ['no', 'yes', 'no']);
        assert.equal(lines[0].properties.color, '#000000');
        assert.equal(lines[0].properties.width, 2);
        assert.equal(lines[0].properties.parentPath, '$.situationObjects[1].symbol.location.content.polygon.points[0]');
        assert.equal(lines[2].geometry.type, 'MultiLineString');

        const fills = m.fills().features;
        assert.equal(fills.length, 1);
        assert.equal(fills[0].properties.color, '#557788');
        assert.equal(fills[0].properties.opacity, 0.25);

        // One label survived: the caption with nothing after its colon
        // ("Min Alt: ") did not. It is a sprite drawn once, placed with
        // the renderer's alignment, offset and angle.
        const labels = m.labels().features;
        assert.equal(labels.length, 1);
        const label = labels[0].properties;
        assert.equal(label.anchor, 'right');
        assert.deepEqual(label.offset, [-6, 0]);
        assert.equal(label.angle, 21.5);
        assert.deepEqual(m.labelImages(), [label.sprite]);
        assert.ok(label.sprite.includes('PL BUCHE'));
        assert.deepEqual(m.recorder.images.get(label.sprite).opts, { pixelRatio: 2 });
        m.unmount();
    });

    it('draws a graphic again when the camera comes to rest — the renderer answers for one view', async () => {
        const m = await mountMap({ interpretations: TRACK_AND_AREA_PATHS });
        m.frames.push(trackAndAreaFrame('f1'));
        await settle();
        assert.equal(m.recorder.calls.length, 1);

        m.recorder.bounds = [10.1, 54.0, 10.4, 54.2];
        m.recorder.map.fire('moveend');
        await new Promise((r) => setTimeout(r, 60));
        await settle();
        assert.equal(m.recorder.calls.length, 2);
        assert.equal(m.recorder.calls[1].bbox, '10.1,54,10.4,54.2');
        m.unmount();
    });

    it('fetches mil-sym-ts only once a graphic appears, and draws the bare geometry until it lands', async () => {
        const m = await mountMap({ interpretations: TRACK_AND_AREA_PATHS, withC5Ren: false });
        const scriptFor = () => m.document.head.children.find(
            (c) => c.tagName === 'SCRIPT' && String(c.src).endsWith('/mil-sym-ts.js'));
        assert.equal(scriptFor(), undefined, 'not fetched on mount');

        // A frame of pins only: still not fetched.
        m.frames.push({ id: 'f0', situationObjects: [trackAndAreaFrame('x').situationObjects[0]] });
        await settle();
        assert.equal(scriptFor(), undefined, 'not fetched for pins');

        m.frames.push(trackAndAreaFrame('f1'));
        await settle();
        const script = scriptFor();
        assert.ok(script, 'fetched for the first graphic');
        assert.equal(m.handle.graphics().library, 'loading');
        assert.equal(m.handle.graphics().items[0].drawn, false);

        // The fallback: the vertices as a closed ring in the affinity colour.
        const lines = m.lines().features;
        assert.equal(lines.length, 1);
        assert.equal(lines[0].geometry.type, 'LineString');
        assert.equal(lines[0].geometry.coordinates.length, 5);
        assert.deepEqual(lines[0].geometry.coordinates[4], lines[0].geometry.coordinates[0]);
        assert.equal(lines[0].properties.color, '#22d3ee');
        assert.equal(m.labels().features.length, 0);

        // The script lands: the renderer takes over.
        m.win.C5Ren = makeC5Ren(m.recorder);
        script.onload();
        await settle();
        assert.equal(m.handle.graphics().library, 'loaded');
        assert.equal(m.handle.graphics().items[0].drawn, true);
        assert.equal(m.recorder.calls.length, 1);
        assert.equal(m.labels().features.length, 1);
        m.unmount();
    });

    it('falls back to the bare geometry when the renderer refuses, and does not ask again for the same shape', async () => {
        const m = await mountMap({ interpretations: TRACK_AND_AREA_PATHS, refuse: () => true });
        m.frames.push(trackAndAreaFrame('f1'));
        await settle();
        assert.equal(m.handle.graphics().items[0].drawn, false);
        assert.equal(m.lines().features.length, 1);
        assert.equal(m.lines().features[0].properties.color, '#22d3ee');
        assert.equal(m.recorder.calls.length, 1);

        m.recorder.map.fire('moveend');
        await new Promise((r) => setTimeout(r, 60));
        await settle();
        assert.equal(m.recorder.calls.length, 1, 'a refused shape is not retried per move');
        m.unmount();
    });

    it('makes a corridor from its centreline and width, a fan from its ranges and azimuths, an ellipse from its three points', async () => {
        const paths = [
            ...[0, 1, 2].map((i) => pairAt(`$.situationObjects[0].symbol.location.content.corridor.points[${i}]`)),
            pairAt('$.situationObjects[1].symbol.location.content.fan.vertexPoint'),
            pairAt('$.situationObjects[2].symbol.location.content.ellipse.centerPoint'),
            pairAt('$.situationObjects[2].symbol.location.content.ellipse.firstConjugateDiameterPoint'),
            pairAt('$.situationObjects[2].symbol.location.content.ellipse.secondConjugateDiameterPoint'),
        ];
        const m = await mountMap({ interpretations: paths });
        m.frames.push({
            id: 'f1',
            situationObjects: [
                symbol('ac', numericId(AIR_CORRIDOR), 'KITE', {
                    corridor: { points: [geo(54.20, 11.20), geo(54.10, 11.35), geo(54.04, 11.46)], width: 2000 },
                }),
                symbol('fan', numericId(RANGE_FAN), 'WISMAR', {
                    fan: {
                        vertexPoint: geo(54.0, 11.5),
                        minimumRangeDimension: 1000, maximumRangeDimension: 12000,
                        orientationAngle: 300, sectorSizeAngle: 90,
                    },
                }),
                symbol('da', numericId(DEFENDED_AREA_HOSTILE), 'NORD', {
                    ellipse: {
                        centerPoint: geo(54.12, 11.70),
                        // ~3.9 km east, ~2.8 km north
                        firstConjugateDiameterPoint: geo(54.12, 11.76),
                        secondConjugateDiameterPoint: geo(54.145, 11.70),
                    },
                }),
            ],
        });
        await settle();

        assert.equal(m.points().features.length, 0, 'none of the seven coordinates is a pin');
        const byCode = Object.fromEntries(m.recorder.calls.map((c) => [c.symbolCode, c]));

        const corridor = byCode[AIR_CORRIDOR];
        assert.equal(corridor.controlPoints, '11.2,54.2 11.35,54.1 11.46,54.04');
        assert.equal(corridor.modifiers.AM_DISTANCE, '2000');
        assert.equal(corridor.modifiers.T_UNIQUE_DESIGNATION_1, 'KITE');

        const fan = byCode[RANGE_FAN];
        assert.equal(fan.controlPoints, '11.5,54');
        assert.equal(fan.modifiers.AM_DISTANCE, '1000,12000');
        assert.equal(fan.modifiers.AN_AZIMUTH, '300,30');

        const ellipse = byCode[DEFENDED_AREA_HOSTILE];
        assert.equal(ellipse.controlPoints, '11.7,54.12');
        const [major, minor] = ellipse.modifiers.AM_DISTANCE.split(',').map(Number);
        assert.ok(major > 3800 && major < 4000, `major ${major}`);
        assert.ok(minor > 2700 && minor < 2900, `minor ${minor}`);
        // The major axis runs east — 0° in the standard's convention.
        assert.equal(ellipse.modifiers.AN_AZIMUTH, '0');
        // A hostile graphic comes back red and stays red.
        const red = m.lines().features.filter((f) => f.properties.color === '#FF0000');
        assert.ok(red.length >= 1);
        m.unmount();
    });

    it('keeps coordinates that are not a tactical graphic exactly as they were: pins', async () => {
        // Two named points with no symbol code above them — a leg with a
        // start and an end — and an array of points without a code: a
        // GPS trace. Neither is a graphic.
        const paths = [
            pairAt('$.start'), pairAt('$.end'),
            ...[0, 1, 2].map((i) => pairAt(`$.trace[${i}]`)),
        ];
        const m = await mountMap({ interpretations: paths });
        m.frames.push({
            id: 'f1',
            start: geo(54.0, 11.0), end: geo(54.1, 11.1),
            trace: [geo(54.2, 11.2), geo(54.3, 11.3), geo(54.4, 11.4)],
        });
        await settle();
        assert.equal(m.points().features.length, 5);
        assert.equal(m.handle.graphics().items.length, 0);
        assert.equal(m.recorder.calls.length, 0);
        m.unmount();
    });

    it('lets a selected frame\'s graphic stand out and the others step back', async () => {
        const m = await mountMap({ interpretations: TRACK_AND_AREA_PATHS });
        m.frames.push(trackAndAreaFrame('f1'));
        await settle();
        assert.equal(m.lines().features[0].properties.width, 2);
        assert.equal(m.lines().features[0].properties.opacity, 1);

        m.selection.push({ selectedFrameIds: ['f1'] });
        await settle();
        assert.equal(m.lines().features[0].properties.width, 4);
        assert.equal(m.lines().features[0].properties.opacity, 1);

        m.selection.push({ selectedFrameIds: ['some-other-frame'] });
        await settle();
        assert.equal(m.lines().features[0].properties.width, 2);
        assert.equal(m.lines().features[0].properties.opacity, 0.45);
        assert.equal(m.labels().features[0].properties.opacity, 0.45);
        m.unmount();
    });

    it('takes the latest frame for a graphic the snapshot carries again', async () => {
        const m = await mountMap({ interpretations: TRACK_AND_AREA_PATHS });
        m.frames.push(trackAndAreaFrame('f1'));
        await settle();
        const moved = trackAndAreaFrame('f2');
        moved.situationObjects[1].symbol.location.content.polygon.points[0] = geo(54.2, 10.1);
        m.frames.push(moved);
        await settle();
        assert.equal(m.handle.graphics().items.length, 1, 'one graphic, not one per frame');
        const last = m.recorder.calls[m.recorder.calls.length - 1];
        assert.ok(last.controlPoints.startsWith('10.1,54.2 '), last.controlPoints);
        m.unmount();
    });
});
