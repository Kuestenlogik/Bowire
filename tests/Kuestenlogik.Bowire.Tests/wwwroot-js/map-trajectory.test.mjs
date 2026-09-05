// #238 — the map widget's trajectory layer.
//
// The interesting behaviour is not "does a LineString appear". It is
// WHICH pins get threaded onto WHICH line: the widget has always
// supported two stream shapes, and the obvious grouping (by
// discriminator) is correct for one and produces a zigzag across
// unrelated entities in the other. So these tests drive the real
// `viewer.mount` from the shipped bundle — stubbed MapLibre, stubbed
// DOM, real frames — and read the GeoJSON the widget hands to
// `setData`.
//
// map.js is not a core wwwroot/js fragment: it ships from the
// Kuestenlogik.Bowire.Map package as its own <script>, outside the core
// IIFE. compileFragment still loads it, because the shape is the same —
// a body that reads host names out of an enclosing scope. What it reads
// here is `window` and `document`, so those are the two stubs that
// matter.

import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { compileFragment } from './_load-fragment.mjs';

const SRC = '../../../src/Kuestenlogik.Bowire.Map/wwwroot/js/widgets/map.js';

// ---------------------------------------------------------------
// Stubs
// ---------------------------------------------------------------

// Just enough element for the widget's inline styling, the toggle
// control it builds, and the style tag the IIFE injects at load.
function makeElement(tag) {
    return {
        tagName: String(tag || 'div').toUpperCase(),
        style: {},
        dataset: {},
        children: [],
        parentNode: null,
        clientWidth: 800,
        clientHeight: 600,
        textContent: '',
        className: '',
        checked: false,
        type: '',
        listeners: {},
        appendChild(child) {
            this.children.push(child);
            child.parentNode = this;
            return child;
        },
        removeChild(child) {
            const i = this.children.indexOf(child);
            if (i >= 0) this.children.splice(i, 1);
            child.parentNode = null;
            return child;
        },
        addEventListener(evt, cb) {
            (this.listeners[evt] = this.listeners[evt] || []).push(cb);
        },
        querySelectorAll() { return []; },
        classList: { add() {}, remove() {} },
    };
}

function makeDocument() {
    const head = makeElement('head');
    return {
        head,
        currentScript: null,
        createElement: makeElement,
        getElementById() { return null; },
        querySelectorAll() { return []; },
        addEventListener() {},
        removeEventListener() {},
        documentElement: { getAttribute() { return 'dark'; } },
    };
}

// MapLibre stub. `sources` records the last payload each source was
// handed, which is what every assertion below reads.
function makeMapLibre(recorder) {
    class LngLatBounds {
        constructor(a, b) { this.sw = a; this.ne = b; }
        extend() { return this; }
    }
    function Map_() {
        const self = {
            _loadCbs: [],
            on(evt, cb) { if (evt === 'load') self._loadCbs.push(cb); },
            // The affinity sprites are reported as already present so
            // the mount never needs an Image decode; that path is not
            // what these tests are about.
            hasImage() { return true; },
            addImage() {},
            addControl(ctrl) {
                recorder.controls.push(ctrl);
                if (ctrl && typeof ctrl.onAdd === 'function') ctrl.onAdd(self);
            },
            addSource(id, spec) {
                recorder.sources[id] = spec.data;
                recorder.sourceOrder.push(id);
            },
            addLayer(spec) {
                recorder.layers.push(spec);
                recorder.layerVisibility[spec.id] =
                    (spec.layout && spec.layout.visibility) || 'visible';
            },
            getSource(id) {
                if (!(id in recorder.sources)) return null;
                return { setData(data) { recorder.sources[id] = data; } };
            },
            setLayoutProperty(layerId, prop, value) {
                if (prop === 'visibility') recorder.layerVisibility[layerId] = value;
            },
            fitBounds() {}, flyTo() {}, resize() {}, remove() {},
        };
        // `map.on('load', resolve)` is awaited by the mount; fire it on
        // the next turn so the await actually suspends first.
        setTimeout(() => self._loadCbs.forEach((cb) => cb()), 0);
        return self;
    }
    return {
        Map: Map_,
        LngLatBounds,
        NavigationControl: function () { return { onAdd: () => makeElement('div'), onRemove() {} }; },
        Marker: function () { return { setLngLat: () => ({ addTo: () => ({ on() {} }) }) }; },
    };
}

// A pushable async iterable, matching the shape the extension host
// hands widgets for frames$ / selection$.
function makePipe() {
    const queue = [];
    let resolveNext = null;
    const iterator = {
        [Symbol.asyncIterator]() { return iterator; },
        next() {
            if (queue.length > 0) return Promise.resolve({ value: queue.shift(), done: false });
            return new Promise((r) => { resolveNext = r; });
        },
    };
    return {
        iterator,
        push(v) {
            if (resolveNext) { const r = resolveNext; resolveNext = null; r({ value: v, done: false }); }
            else queue.push(v);
        },
    };
}

// Let the widget's stream loop drain what we pushed. The loop awaits
// between frames, so yielding to the microtask queue a few times is
// what actually advances it.
async function settle(times = 6) {
    for (let i = 0; i < times; i++) await new Promise((r) => setTimeout(r, 0));
}

/**
 * Mount the real viewer against the stubs and return handles to poke it.
 * `prefsSeed` primes ctx.prefs so the "remembers the toggle" case can
 * mount with it already on.
 */
async function mountMap({ prefsSeed = {} } = {}) {
    const recorder = {
        sources: {}, sourceOrder: [], layers: [], layerVisibility: {}, controls: [],
    };
    const win = {
        __BOWIRE_CONFIG__: { mapBasemap: 'none' },
        maplibregl: makeMapLibre(recorder),
    };
    const doc = makeDocument();

    let registered = null;
    win.__bowireExtFramework = {
        register(spec) { registered = spec; },
        markBuiltIn() {},
    };

    const load = compileFragment(SRC, ['window', 'document'], '');
    load({ window: win, document: doc });
    assert.ok(registered, 'the bundle must register its viewer');

    const frames = makePipe();
    const selection = makePipe();
    const prefStore = { ...prefsSeed };
    const container = makeElement('div');

    const ctx = {
        frames$: frames.iterator,
        selection$: selection.iterator,
        interpretations: {
            'coordinate.latitude': '$.lat',
            'coordinate.longitude': '$.lon',
        },
        theme: { mode: 'dark', accent: '#4f46e5' },
        viewport: { width: 800, height: 600, on() { return () => {}; } },
        prefs: {
            get: (k, d) => (k in prefStore ? prefStore[k] : d),
            set: (k, v) => { prefStore[k] = v; return true; },
        },
    };

    const unmount = await registered.viewer.mount(container, ctx);
    await settle(2);

    return {
        recorder, frames, selection, prefStore, unmount, container,
        lines: () => recorder.sources['bowire-lines'],
        points: () => recorder.sources['bowire-points'],
        toggle: (on) => {
            // Drive the checkbox the operator would click, not the
            // internal setter — that is the surface #238 specifies.
            const ctrl = recorder.controls.find(
                (c) => c && c._container && c._container.className
                    && c._container.className.indexOf('bowire-map-trajectory-ctrl') >= 0);
            assert.ok(ctrl, 'the trajectory control must be registered');
            const label = ctrl._container.children[0];
            const box = label.children[0];
            box.checked = on;
            box.listeners.change.forEach((cb) => cb());
            return box;
        },
    };
}

// Frame helpers. `sidc` present => entity identity; absent => the
// widget falls back to the coord's parent path.
const frameAt = (id, lat, lon, extra = {}) => ({ id, lat, lon, ...extra });

describe('map widget — trajectory layer (#238)', { concurrency: 1 }, () => {

    it('draws nothing until the operator asks for it', async () => {
        const m = await mountMap();
        m.frames.push(frameAt('f1', 10, 20));
        m.frames.push(frameAt('f2', 11, 21));
        await settle();

        assert.equal(m.points().features.length, 2, 'pins still render as before');
        assert.equal(m.lines().features.length, 0,
            'the default must be the pre-#238 behaviour: no trajectory');
        assert.equal(m.recorder.layerVisibility['bowire-lines-layer'], 'none');
        m.unmount();
    });

    it('paints the line under the pins', async () => {
        const m = await mountMap();
        const ids = m.recorder.layers.map((l) => l.id);
        assert.ok(ids.indexOf('bowire-lines-layer') < ids.indexOf('bowire-points-halo'),
            'the line layer must be registered before the pin layers so pins stay on top');
        assert.ok(ids.indexOf('bowire-lines-layer') < ids.indexOf('bowire-points-layer'));
        m.unmount();
    });

    it('threads one track in stream order', async () => {
        const m = await mountMap();
        m.toggle(true);
        m.frames.push(frameAt('f1', 10, 20));
        m.frames.push(frameAt('f2', 11, 21));
        m.frames.push(frameAt('f3', 12, 22));
        await settle();

        const feats = m.lines().features;
        assert.equal(feats.length, 1);
        assert.equal(feats[0].geometry.type, 'LineString');
        assert.deepEqual(feats[0].geometry.coordinates,
            [[20, 10], [21, 11], [22, 12]],
            'coordinates follow arrival order, lon-first per GeoJSON');
        m.unmount();
    });

    it('does not thread unrelated entities of one frame onto one line', async () => {
        // The failure #238 rules out. Two entities distinguished only by
        // their SIDC arrive in every frame; keying on discriminator
        // alone would zigzag between them.
        const m = await mountMap();
        m.toggle(true);
        m.frames.push(frameAt('f1', 10, 20, { sidc: 'SFGPU----------' }));
        m.frames.push(frameAt('f2', 50, 60, { sidc: 'SHGPU----------' }));
        m.frames.push(frameAt('f3', 11, 21, { sidc: 'SFGPU----------' }));
        m.frames.push(frameAt('f4', 51, 61, { sidc: 'SHGPU----------' }));
        await settle();

        const feats = m.lines().features;
        assert.equal(feats.length, 2, 'one path per entity, not one shared path');
        for (const f of feats) {
            assert.equal(f.geometry.coordinates.length, 2);
        }
        const all = feats.map((f) => f.geometry.coordinates);
        assert.ok(all.some((c) => c[0][0] === 20 && c[1][0] === 21));
        assert.ok(all.some((c) => c[0][0] === 60 && c[1][0] === 61));
        m.unmount();
    });

    it('leaves a single-pin track without a LineString', async () => {
        // Two positions is the minimum GeoJSON accepts, and one pin has
        // no path to draw regardless.
        const m = await mountMap();
        m.toggle(true);
        m.frames.push(frameAt('f1', 10, 20, { sidc: 'SFGPU----------' }));
        m.frames.push(frameAt('f2', 50, 60, { sidc: 'SHGPU----------' }));
        m.frames.push(frameAt('f3', 11, 21, { sidc: 'SFGPU----------' }));
        await settle();

        const feats = m.lines().features;
        assert.equal(feats.length, 1, 'only the track that actually moved');
        assert.equal(feats[0].geometry.coordinates.length, 2);
        m.unmount();
    });

    it('lights the whole track a selected frame belongs to', async () => {
        const m = await mountMap();
        m.toggle(true);
        m.frames.push(frameAt('f1', 10, 20, { sidc: 'SFGPU----------' }));
        m.frames.push(frameAt('f2', 11, 21, { sidc: 'SFGPU----------' }));
        m.frames.push(frameAt('f3', 50, 60, { sidc: 'SHGPU----------' }));
        m.frames.push(frameAt('f4', 51, 61, { sidc: 'SHGPU----------' }));
        await settle();

        m.selection.push({ selectedFrameIds: ['f1'] });
        await settle();

        const feats = m.lines().features;
        assert.equal(feats.length, 2);
        const selected = feats.filter((f) => f.properties.selected === 'yes');
        const dimmed = feats.filter((f) => f.properties.selected === 'no-but-others-are');
        assert.equal(selected.length, 1,
            'selecting one frame lights the entire path that frame sits on');
        assert.equal(dimmed.length, 1, 'the other track steps back');
        assert.deepEqual(selected[0].geometry.coordinates, [[20, 10], [21, 11]]);
        m.unmount();
    });

    it('inherits the pin cap rather than keeping its own', async () => {
        // The toggle stays off while the frames land, so the O(pins)
        // rebuild runs once at the end instead of 5001 times.
        const m = await mountMap();
        for (let i = 0; i < 5100; i++) m.frames.push(frameAt('f' + i, 10, 20 + i * 0.001));
        await settle(40);
        assert.equal(m.points().features.length, 5000, 'pin cap still holds');

        m.toggle(true);
        const total = m.lines().features
            .reduce((n, f) => n + f.geometry.coordinates.length, 0);
        assert.equal(total, 5000,
            'a vertex cannot outlive the pin it was derived from');
        m.unmount();
    });

    it('remembers the toggle through ctx.prefs', async () => {
        const m = await mountMap();
        m.toggle(true);
        assert.equal(m.prefStore.trajectory, true, 'turning it on is persisted');
        m.toggle(false);
        assert.equal(m.prefStore.trajectory, false, 'and so is turning it off');
        m.unmount();

        const again = await mountMap({ prefsSeed: { trajectory: true } });
        assert.equal(again.recorder.layerVisibility['bowire-lines-layer'], 'visible',
            'a remounted widget comes back with the operator choice already applied');
        again.frames.push(frameAt('g1', 10, 20));
        again.frames.push(frameAt('g2', 11, 21));
        await settle();
        assert.equal(again.lines().features.length, 1,
            'and starts drawing without being toggled again');
        again.unmount();
    });

    it('drops the geometry when switched back off', async () => {
        // A hidden layer over a populated source still holds every
        // vertex; a long stream the operator turned the trajectory off
        // for is exactly when that memory is not wanted.
        const m = await mountMap();
        m.toggle(true);
        m.frames.push(frameAt('f1', 10, 20));
        m.frames.push(frameAt('f2', 11, 21));
        await settle();
        assert.equal(m.lines().features.length, 1);

        m.toggle(false);
        assert.equal(m.lines().features.length, 0);
        assert.equal(m.recorder.layerVisibility['bowire-lines-layer'], 'none');
        assert.equal(m.points().features.length, 2, 'the pins are untouched');
        m.unmount();
    });

    it('mounts against a host that has no ctx.prefs', async () => {
        // ctx.prefs is a v1.1 additive; the contract says a widget
        // reaching for a missing field falls back gracefully.
        const m = await mountMap();
        m.unmount();

        const recorder = {
            sources: {}, sourceOrder: [], layers: [], layerVisibility: {}, controls: [],
        };
        const win = {
            __BOWIRE_CONFIG__: { mapBasemap: 'none' },
            maplibregl: makeMapLibre(recorder),
        };
        let registered = null;
        win.__bowireExtFramework = { register(s) { registered = s; }, markBuiltIn() {} };
        compileFragment(SRC, ['window', 'document'], '')({ window: win, document: makeDocument() });

        const frames = makePipe();
        const unmount = await registered.viewer.mount(makeElement('div'), {
            frames$: frames.iterator,
            interpretations: {
                'coordinate.latitude': '$.lat',
                'coordinate.longitude': '$.lon',
            },
            theme: { mode: 'dark' },
            viewport: { width: 800, height: 600, on() { return () => {}; } },
            // no prefs, no selection$
        });
        await settle(2);
        frames.push(frameAt('f1', 10, 20));
        await settle();
        assert.equal(recorder.sources['bowire-points'].features.length, 1,
            'the widget still mounts and still plots');
        assert.equal(recorder.sources['bowire-lines'].features.length, 0);
        unmount();
    });
});
