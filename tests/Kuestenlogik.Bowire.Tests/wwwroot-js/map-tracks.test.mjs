// #240 — per-entity tracks via a configurable track-id field.
//
// The two stream shapes this has to serve pull in opposite directions,
// and both ship as samples:
//
//   TacticalAPI — N entities in ONE frame (`situationObjects[0..2]`).
//   The track path must resolve RELATIVE to each coordinate's parent, or
//   all N collapse onto one track.
//
//   DIS — one entity per frame, many frames. The path resolves
//   ABSOLUTELY against the frame, and the grouping only appears across
//   frames.
//
// Fixtures below reproduce both against the real `viewer.mount`.

import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { compileFragment } from './_load-fragment.mjs';

const SRC = '../../../src/Kuestenlogik.Bowire.Map/wwwroot/js/widgets/map.js';

// ---------------------------------------------------------------
// Stubs (same shape as map-trajectory.test.mjs)
// ---------------------------------------------------------------

function makeElement(tag) {
    return {
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
}

function makeDocument() {
    const head = makeElement('head');
    return {
        head, currentScript: null,
        createElement: makeElement,
        getElementById() { return null; },
        querySelectorAll() { return []; },
        addEventListener() {}, removeEventListener() {},
        documentElement: { getAttribute() { return 'dark'; } },
    };
}

function makeMapLibre(recorder) {
    class LngLatBounds {
        constructor(a, b) { this.sw = a; this.ne = b; }
        extend() { return this; }
    }
    function Map_() {
        const self = {
            _loadCbs: [],
            on(evt, cb) { if (evt === 'load') self._loadCbs.push(cb); },
            hasImage() { return true; }, addImage() {},
            addControl(ctrl) {
                recorder.controls.push(ctrl);
                if (ctrl && typeof ctrl.onAdd === 'function') ctrl.onAdd(self);
            },
            addSource(id, spec) { recorder.sources[id] = spec.data; },
            addLayer(spec) {
                recorder.layers.push(spec);
                recorder.layerVisibility[spec.id] =
                    (spec.layout && spec.layout.visibility) || 'visible';
            },
            getSource(id) {
                if (!(id in recorder.sources)) return null;
                return { setData(data) { recorder.sources[id] = data; } };
            },
            setLayoutProperty(l, p, v) { if (p === 'visibility') recorder.layerVisibility[l] = v; },
            fitBounds() {}, flyTo() {}, resize() {}, remove() {},
        };
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

const settle = async (n = 6) => {
    for (let i = 0; i < n; i++) await new Promise((r) => setTimeout(r, 0));
};

/**
 * @param interpretations  the (lat, lon) path pairs the framework folded
 *                         in — an array for the multi-entity shape.
 */
async function mountMap({ interpretations, methodSeed = {}, seed = {} } = {}) {
    const recorder = { sources: {}, layers: [], layerVisibility: {}, controls: [] };
    const win = {
        __BOWIRE_CONFIG__: { mapBasemap: 'none' },
        maplibregl: makeMapLibre(recorder),
    };
    let registered = null;
    win.__bowireExtFramework = { register(s) { registered = s; }, markBuiltIn() {} };
    compileFragment(SRC, ['window', 'document'], '')({ window: win, document: makeDocument() });

    const frames = makePipe();
    const selection = makePipe();
    const wide = { ...seed };
    const perMethod = { ...methodSeed };
    const store = (bag) => ({
        get: (k, d) => (k in bag ? bag[k] : d),
        set: (k, v) => { bag[k] = v; return true; },
    });
    const prefs = store(wide);
    prefs.method = store(perMethod);

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

    const handleOf = () => (win.__bowireMapWidgets || [])
        .find((h) => h.container === container);
    // The widget registers on `window.__bowireMapWidgets`; the stub window
    // is per-mount, so this cannot pick up a neighbour's handle.
    const handle = handleOf() || {};

    return {
        recorder, frames, selection, unmount, handle, wide, perMethod,
        points: () => recorder.sources['bowire-points'],
        lines: () => recorder.sources['bowire-lines'],
    };
}

// --- TacticalAPI RadarSweep: three tracks, all in one frame -----------
const TAC_PATHS = [
    { 'coordinate.latitude': '$.situationObjects[0].lat', 'coordinate.longitude': '$.situationObjects[0].lon' },
    { 'coordinate.latitude': '$.situationObjects[1].lat', 'coordinate.longitude': '$.situationObjects[1].lon' },
    { 'coordinate.latitude': '$.situationObjects[2].lat', 'coordinate.longitude': '$.situationObjects[2].lon' },
];
const tacFrame = (id, step) => ({
    id,
    situationObjects: [
        { uuid: 'moewe', name: 'Patrol Möwe', lat: 54.0 + step * 0.01, lon: 11.5 },
        { uuid: 'hostile', name: 'Surface Contact', lat: 54.0, lon: 11.5 + step * 0.01 },
        { uuid: 'hanse', name: 'Cargo Hanse', lat: 54.0 - step * 0.01, lon: 11.5 },
    ],
});

// --- DIS convoy: one entity per frame ---------------------------------
const DIS_PATHS = { 'coordinate.latitude': '$.lat', 'coordinate.longitude': '$.lon' };
const disFrame = (id, marking, lat, lon) => ({ id, entityMarking: marking, lat, lon });

describe('map widget — per-entity tracks (#240)', { concurrency: 1 }, () => {

    it('splits one frame into a track per entity (TacticalAPI shape)', async () => {
        const m = await mountMap({
            interpretations: TAC_PATHS,
            methodSeed: { trackIdPath: 'uuid' },
        });
        m.frames.push(tacFrame('f1', 1));
        m.frames.push(tacFrame('f2', 2));
        await settle();

        const tracks = m.handle.tracks();
        assert.equal(tracks.length, 3, 'three entities, three tracks');
        assert.deepEqual(tracks.map((t) => t.trackId).sort(),
            ['hanse', 'hostile', 'moewe']);
        for (const t of tracks) assert.equal(t.count, 2, 'two frames each');

        // The relative path is what makes this work: 'uuid' resolves
        // against situationObjects[0], [1], [2] in turn.
        const colours = new Set(tracks.map((t) => t.color));
        assert.equal(colours.size, 3, 'each entity gets its own colour');
        m.unmount();
    });

    it('groups across frames when each frame carries one entity (DIS shape)', async () => {
        const m = await mountMap({
            interpretations: DIS_PATHS,
            methodSeed: { trackIdPath: 'entityMarking' },
        });
        m.frames.push(disFrame('p1', 'BOWLINE01', 54.1, 11.1));
        m.frames.push(disFrame('p2', 'DRONE07', 54.4, 11.9));
        m.frames.push(disFrame('p3', 'BOWLINE01', 54.11, 11.11));
        m.frames.push(disFrame('p4', 'DRONE07', 54.41, 11.91));
        await settle();

        const tracks = m.handle.tracks();
        assert.equal(tracks.length, 2);
        const byId = Object.fromEntries(tracks.map((t) => [t.trackId, t.count]));
        assert.deepEqual(byId, { BOWLINE01: 2, DRONE07: 2 });
        m.unmount();
    });

    it('draws one trajectory per entity, not one across them', async () => {
        const m = await mountMap({
            interpretations: TAC_PATHS,
            methodSeed: { trackIdPath: 'uuid', },
            seed: { trajectory: true },
        });
        m.frames.push(tacFrame('f1', 1));
        m.frames.push(tacFrame('f2', 2));
        m.frames.push(tacFrame('f3', 3));
        await settle();

        const feats = m.lines().features;
        assert.equal(feats.length, 3);
        for (const f of feats) {
            assert.equal(f.geometry.coordinates.length, 3);
        }
        assert.deepEqual([...new Set(feats.map((f) => f.properties.trackId))].sort(),
            ['hanse', 'hostile', 'moewe']);
        m.unmount();
    });

    it('falls back to the #238 grouping when no path is set', async () => {
        // The "no regression" criterion. What must survive is not the pin
        // colour but the multi-entity separation #238 established.
        const m = await mountMap({ interpretations: TAC_PATHS, seed: { trajectory: true } });
        m.frames.push(tacFrame('f1', 1));
        m.frames.push(tacFrame('f2', 2));
        await settle();

        assert.equal(m.handle.trackIdPath(), '');
        assert.equal(m.handle.tracks().length, 3,
            'parentPath still separates the three slots');
        assert.equal(m.lines().features.length, 3);
        m.unmount();
    });

    it('re-keys the pins already on the map when the path changes', async () => {
        // A stream that has already finished is exactly when an operator
        // reaches for this setting, so applying it only to future frames
        // would make it do nothing in its main use.
        const m = await mountMap({ interpretations: TAC_PATHS, seed: { trajectory: true } });
        m.frames.push(tacFrame('f1', 1));
        m.frames.push(tacFrame('f2', 2));
        await settle();
        assert.equal(m.handle.tracks().every((t) => t.trackId === ''), true,
            'no ids yet — the fallback keyed these');

        m.handle.setTrackIdPath('uuid');
        const tracks = m.handle.tracks();
        assert.equal(tracks.length, 3);
        assert.deepEqual(tracks.map((t) => t.trackId).sort(),
            ['hanse', 'hostile', 'moewe'],
            'pins keep the id they were stamped with at arrival');
        assert.equal(m.perMethod.trackIdPath, 'uuid', 'and the choice is persisted per method');
        m.unmount();
    });

    it('hides a track without losing it', async () => {
        const m = await mountMap({
            interpretations: TAC_PATHS,
            methodSeed: { trackIdPath: 'uuid' },
            seed: { trajectory: true },
        });
        m.frames.push(tacFrame('f1', 1));
        m.frames.push(tacFrame('f2', 2));
        await settle();
        assert.equal(m.points().features.length, 6);

        const hostile = m.handle.tracks().find((t) => t.trackId === 'hostile');
        m.handle.setTrackHidden(hostile.key, true);

        assert.equal(m.points().features.length, 4, 'its pins stop rendering');
        assert.equal(m.lines().features.length, 2, 'and so does its path');
        const still = m.handle.tracks().find((t) => t.trackId === 'hostile');
        assert.equal(still.count, 2, 'but the track keeps its history');
        assert.equal(still.hidden, true);

        m.handle.setTrackHidden(hostile.key, false);
        assert.equal(m.points().features.length, 6, 'and comes back unchanged');
        m.unmount();
    });

    it('trims the loudest track, not the quiet one', async () => {
        // #240 asks for a per-track quota of CAP/N. A global budget that
        // always takes from the longest track gives the same protection
        // without shrinking every existing track's allowance the moment
        // an N+1th track appears.
        const m = await mountMap({
            interpretations: DIS_PATHS,
            methodSeed: { trackIdPath: 'entityMarking' },
        });
        // One quiet track of 10 pins, then a chatty one that blows the cap.
        for (let i = 0; i < 10; i++) m.frames.push(disFrame('q' + i, 'QUIET', 54, 11 + i * 0.001));
        for (let i = 0; i < 5100; i++) m.frames.push(disFrame('c' + i, 'CHATTY', 55, 12 + i * 0.0001));
        await settle(40);

        assert.equal(m.points().features.length, 5000, 'the global cap still holds');
        const quiet = m.points().features.filter(
            (f) => f.properties.trackId === 'QUIET').length;
        assert.equal(quiet, 10,
            'the quiet track keeps every pin — it was never the longest');
        m.unmount();
    });

    it('offers candidate fields from a frame it has seen', async () => {
        const m = await mountMap({ interpretations: TAC_PATHS });
        m.frames.push(tacFrame('f1', 1));
        await settle();

        const candidates = m.handle.trackCandidates();
        assert.ok(candidates.includes('uuid'), 'the id field is offered');
        assert.ok(candidates.includes('name'), 'and so is a human label');
        assert.ok(!candidates.includes('lat') && !candidates.includes('lon'),
            'coordinates identify a position, not an entity — one track per ping');
        m.unmount();
    });

    it('ignores a path that resolves to an object', async () => {
        // "[object Object]" would merge every entity into one track and
        // look like the feature working until someone counts the rows.
        const m = await mountMap({
            interpretations: DIS_PATHS,
            methodSeed: { trackIdPath: 'nested' },
        });
        m.frames.push({ id: 'a', nested: { id: 1 }, lat: 54, lon: 11 });
        m.frames.push({ id: 'b', nested: { id: 2 }, lat: 55, lon: 12 });
        await settle();

        const tracks = m.handle.tracks();
        assert.equal(tracks.every((t) => t.trackId === ''), true,
            'falls back rather than grouping on a stringified object');
        m.unmount();
    });
});
