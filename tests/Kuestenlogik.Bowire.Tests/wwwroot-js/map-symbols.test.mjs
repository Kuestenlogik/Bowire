// MIL-STD-2525 symbols on the map — a pin that carries a SIDC is drawn
// as the standard's own symbol, rendered by milsymbol into a sprite per
// distinct code, with the four affinity shapes as the fallback while
// the library is still loading (or could not be loaded at all).
//
// The stub window carries the REAL vendored milsymbol, so what these
// tests register is what the browser registers; only the SVG decode
// (`Image`) is stubbed, since node has no rasteriser.

import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { createRequire } from 'node:module';
import { compileFragment } from './_load-fragment.mjs';

const SRC = '../../../src/Kuestenlogik.Bowire.Map/wwwroot/js/widgets/map.js';
const require = createRequire(import.meta.url);
const milsymbol = require('../../../src/Kuestenlogik.Bowire.Map/wwwroot/milsymbol/milsymbol.js');

// ---------------------------------------------------------------
// Stubs (same shape as map-tracks.test.mjs)
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

// An Image that "decodes" on the next tick — the widget only ever
// reads onload / onerror / src off it.
function makeImage(recorder) {
    return class Image {
        constructor(w, h) { this.width = w; this.height = h; }
        set src(v) {
            this._src = v;
            recorder.decoded.push(v);
            setTimeout(() => this.onload && this.onload(), 0);
        }
        get src() { return this._src; }
    };
}

function makeMapLibre(recorder) {
    class LngLatBounds {
        constructor(a, b) { this.sw = a; this.ne = b; }
        extend() { return this; }
    }
    function Map_() {
        const self = {
            _loadCbs: [], _handlers: {},
            on(evt, cb) {
                if (evt === 'load') self._loadCbs.push(cb);
                (self._handlers[evt] = self._handlers[evt] || []).push(cb);
            },
            fire(evt, e) { (self._handlers[evt] || []).forEach((cb) => cb(e)); },
            hasImage(name) { return recorder.images.has(name); },
            addImage(name, img, opts) {
                recorder.images.set(name, { img, opts });
            },
            removeImage(name) { recorder.images.delete(name); },
            addControl(ctrl) {
                if (ctrl && typeof ctrl.onAdd === 'function') ctrl.onAdd(self);
            },
            addSource(id, spec) { recorder.sources[id] = spec.data; },
            addLayer(spec) { recorder.layers.push(spec); },
            getSource(id) {
                if (!(id in recorder.sources)) return null;
                return { setData(data) { recorder.sources[id] = data; recorder.renders++; } };
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

const settle = async (n = 6) => {
    for (let i = 0; i < n; i++) await new Promise((r) => setTimeout(r, 0));
};

/**
 * @param withMilSymbol  whether `window.ms` is already there at mount —
 *                       the second widget in a session, or the first
 *                       one before the script has landed.
 */
async function mountMap({ interpretations, withMilSymbol = true } = {}) {
    const recorder = { sources: {}, layers: [], images: new Map(), decoded: [], renders: 0 };
    const document = makeDocument();
    const win = {
        __BOWIRE_CONFIG__: { mapBasemap: 'none' },
        maplibregl: makeMapLibre(recorder),
    };
    if (withMilSymbol) win.ms = milsymbol;
    let registered = null;
    win.__bowireExtFramework = { register(s) { registered = s; }, markBuiltIn() {} };
    compileFragment(SRC, ['window', 'document', 'Image'], '')({
        window: win, document, Image: makeImage(recorder),
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
    const symbolLayer = recorder.layers.find((l) => l.id === 'bowire-points-layer');
    return {
        recorder, frames, unmount, handle, win, document, symbolLayer,
        points: () => recorder.sources['bowire-points'],
        sidcImages: () => [...recorder.images.keys()].filter((k) => k.startsWith('bowire-sidc-')),
    };
}

// The sample's shape: TacticalAPI situation objects with the SIDC
// under symbol.symbolIdentifier, the coordinate five levels down.
const PATHS = [0, 1].map((i) => ({
    'coordinate.latitude': `$.situationObjects[${i}].symbol.location.content.point.geoPoint.latitudeCoordinate`,
    'coordinate.longitude': `$.situationObjects[${i}].symbol.location.content.point.geoPoint.longitudeCoordinate`,
}));
const entity = (uuid, sidc, lat, lon) => ({
    uuid,
    symbol: {
        symbolIdentifier: { content: { stringIdentifier: sidc } },
        location: { content: { point: { geoPoint: { latitudeCoordinate: lat, longitudeCoordinate: lon } } } },
    },
});
const FRIEND_C = 'SFSPCLCC-------';       // 2525C, friend, sea surface, cruiser
const HOSTILE_C = 'SHAPMF---------';      // 2525C, hostile, air, fixed wing
const FRIEND_D = '10031000001211000000';  // 2525D, friend, land unit
const HOSTILE_D = '10061000001211000000'; // 2525D, hostile, land unit

describe('map widget — MIL-2525 symbols via milsymbol', { concurrency: 1 }, () => {

    it('asks for the pin\'s own symbol first and the affinity shape as the fallback', async () => {
        const m = await mountMap({ interpretations: PATHS });
        const icon = m.symbolLayer.layout['icon-image'];
        assert.equal(icon[0], 'coalesce');
        assert.deepEqual(icon[1], ['image', ['concat', 'bowire-sidc-', ['get', 'sidc']]]);
        assert.equal(icon[2][0], 'image');
        assert.equal(icon[2][1][0], 'match');
        // The four affinity sprites are still registered up front.
        for (const a of ['friend', 'hostile', 'neutral', 'unknown']) {
            assert.ok(m.recorder.images.has('bowire-affinity-' + a), a);
        }
        m.unmount();
    });

    it('registers one sprite per distinct SIDC, rendered by milsymbol at 2x', async () => {
        const m = await mountMap({ interpretations: PATHS });
        m.frames.push({ id: 'f1', situationObjects: [entity('a', FRIEND_C, 54, 11), entity('b', HOSTILE_C, 54.1, 11)] });
        m.frames.push({ id: 'f2', situationObjects: [entity('a', FRIEND_C, 54.01, 11), entity('b', HOSTILE_C, 54.11, 11)] });
        await settle();

        assert.deepEqual(m.sidcImages().sort(), ['bowire-sidc-' + FRIEND_C, 'bowire-sidc-' + HOSTILE_C].sort());
        const friend = m.recorder.images.get('bowire-sidc-' + FRIEND_C);
        assert.deepEqual(friend.opts, { pixelRatio: 2 });
        assert.ok(friend.img.width > 0 && friend.img.height > 0);
        // What was decoded is milsymbol's SVG, not one of the affinity shapes.
        const decoded = m.recorder.decoded.filter((d) => d.includes('baseProfile'));
        assert.equal(decoded.length, 2);
        assert.deepEqual(m.handle.symbolIcons(), { [FRIEND_C]: 'ok', [HOSTILE_C]: 'ok' });

        const pins = m.points().features;
        assert.equal(pins.length, 4);
        assert.deepEqual(pins.map((f) => f.properties.affinity), ['friend', 'hostile', 'friend', 'hostile']);
        assert.deepEqual(pins.map((f) => f.properties.sidc), [FRIEND_C, HOSTILE_C, FRIEND_C, HOSTILE_C]);
        m.unmount();
    });

    it('repaints once a sprite lands, so pins already on the map switch over', async () => {
        const m = await mountMap({ interpretations: PATHS });
        m.frames.push({ id: 'f1', situationObjects: [entity('a', FRIEND_C, 54, 11), entity('b', HOSTILE_C, 54.1, 11)] });
        await settle();
        // One render for the frame, one per sprite that landed after it.
        assert.equal(m.recorder.renders, 3);
        m.unmount();
    });

    it('reads a 2525D code too — twenty digits, identity at position 4', async () => {
        const m = await mountMap({ interpretations: PATHS });
        m.frames.push({ id: 'f1', situationObjects: [entity('a', FRIEND_D, 54, 11), entity('b', HOSTILE_D, 54.1, 11)] });
        await settle();

        const pins = m.points().features;
        assert.deepEqual(pins.map((f) => f.properties.sidc), [FRIEND_D, HOSTILE_D]);
        assert.deepEqual(pins.map((f) => f.properties.affinity), ['friend', 'hostile']);
        assert.deepEqual(m.sidcImages().sort(), ['bowire-sidc-' + FRIEND_D, 'bowire-sidc-' + HOSTILE_D].sort());
        m.unmount();
    });

    it('draws the frame for a function id milsymbol has no icon for, and nothing for a dimension it does not know', async () => {
        // The sample carries `SFGPUCT` — friendly ground unit, function
        // id without an icon. The frame alone is worth drawing. A code
        // whose battle dimension milsymbol cannot place (`Z`) is not:
        // that pin keeps the affinity shape.
        const m = await mountMap({ interpretations: PATHS });
        m.frames.push({ id: 'f1', situationObjects: [entity('a', 'SFGPUCT---*****', 54, 11), entity('b', 'SFZPUCA---*****', 54.1, 11)] });
        await settle();
        assert.deepEqual(m.handle.symbolIcons(), { 'SFGPUCT---*****': 'ok', 'SFZPUCA---*****': 'failed' });
        assert.deepEqual(m.sidcImages(), ['bowire-sidc-SFGPUCT---*****']);
        m.unmount();
    });

    it('leaves a pin without a code on the affinity shape and registers nothing', async () => {
        const m = await mountMap({
            interpretations: { 'coordinate.latitude': '$.lat', 'coordinate.longitude': '$.lon' },
        });
        m.frames.push({ id: 'f1', lat: 54, lon: 11, name: 'buoy' });
        await settle();

        assert.equal(m.points().features[0].properties.sidc, '');
        assert.equal(m.points().features[0].properties.affinity, 'unknown');
        assert.deepEqual(m.sidcImages(), []);
        assert.deepEqual(m.handle.symbolIcons(), {});
        m.unmount();
    });

    it('fetches milsymbol next to MapLibre and catches up when it lands', async () => {
        const m = await mountMap({ interpretations: PATHS, withMilSymbol: false });
        const script = m.document.head.children.find(
            (c) => c.tagName === 'SCRIPT' && String(c.src).endsWith('/milsymbol.js'));
        assert.ok(script, 'milsymbol script tag injected');

        // Pins before the library: affinity shapes, codes parked.
        m.frames.push({ id: 'f1', situationObjects: [entity('a', FRIEND_C, 54, 11)] });
        await settle();
        assert.deepEqual(m.sidcImages(), []);
        assert.deepEqual(m.handle.symbolIcons(), { [FRIEND_C]: 'waiting' });

        // The script lands.
        m.win.ms = milsymbol;
        script.onload();
        await settle();
        assert.deepEqual(m.sidcImages(), ['bowire-sidc-' + FRIEND_C]);
        assert.deepEqual(m.handle.symbolIcons(), { [FRIEND_C]: 'ok' });
        m.unmount();
    });

    it('draws a sprite again when MapLibre reports it missing', async () => {
        const m = await mountMap({ interpretations: PATHS });
        m.frames.push({ id: 'f1', situationObjects: [entity('a', FRIEND_C, 54, 11)] });
        await settle();
        assert.deepEqual(m.sidcImages(), ['bowire-sidc-' + FRIEND_C]);

        // A style swap drops every image; MapLibre asks for the ones
        // its layers still reference.
        m.recorder.images.delete('bowire-sidc-' + FRIEND_C);
        const before = m.recorder.decoded.length;
        m.recorder.map.fire('styleimagemissing', { id: 'bowire-sidc-' + FRIEND_C });
        await settle();
        assert.deepEqual(m.sidcImages(), ['bowire-sidc-' + FRIEND_C]);
        assert.equal(m.recorder.decoded.length, before + 1);
        m.unmount();
    });
});
