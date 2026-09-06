// #239 — the time cursor and playback.
//
// The cursor decides which frames count as "already arrived"; the pin
// layer and the trajectory both read that one answer. So the interesting
// assertions are about what the map STOPS showing, and about the two
// states that are easy to get wrong: a live stream must not let the
// operator fight an arriving frame, and a scrub must not destroy a
// selection.

import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { compileFragment } from './_load-fragment.mjs';

const SRC = '../../../src/Kuestenlogik.Bowire.Map/wwwroot/js/widgets/map.js';

// ---------------------------------------------------------------
// Stubs
// ---------------------------------------------------------------

function makeElement(tag) {
    const el = {
        tagName: String(tag || 'div').toUpperCase(),
        style: {}, dataset: {}, children: [], parentNode: null,
        clientWidth: 800, clientHeight: 600,
        textContent: '', className: '', value: '', min: '', max: '', step: '',
        checked: false, disabled: false, selected: false, hidden: false,
        type: '', placeholder: '', title: '',
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
    return el;
}

function makeDocument() {
    return {
        head: makeElement('head'), currentScript: null,
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
            addLayer(spec) { recorder.layers.push(spec); },
            getSource(id) {
                if (!(id in recorder.sources)) return null;
                return { setData(data) { recorder.sources[id] = data; } };
            },
            setLayoutProperty() {},
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
    let done = false;
    const iterator = {
        [Symbol.asyncIterator]() { return iterator; },
        next() {
            if (queue.length) return Promise.resolve({ value: queue.shift(), done: false });
            if (done) return Promise.resolve({ value: undefined, done: true });
            return new Promise((r) => { wake = r; });
        },
    };
    return {
        iterator,
        push(v) {
            if (wake) { const w = wake; wake = null; w({ value: v, done: false }); }
            else queue.push(v);
        },
        close() {
            done = true;
            if (wake) { const w = wake; wake = null; w({ value: undefined, done: true }); }
        },
    };
}

const settle = async (n = 6) => {
    for (let i = 0; i < n; i++) await new Promise((r) => setTimeout(r, 0));
};

const PATHS = { 'coordinate.latitude': '$.lat', 'coordinate.longitude': '$.lon' };

async function mountMap({ seed = {} } = {}) {
    const recorder = { sources: {}, layers: [], controls: [] };
    const win = {
        __BOWIRE_CONFIG__: { mapBasemap: 'none' },
        maplibregl: makeMapLibre(recorder),
    };
    let registered = null;
    win.__bowireExtFramework = { register(s) { registered = s; }, markBuiltIn() {} };
    compileFragment(SRC, ['window', 'document'], '')({ window: win, document: makeDocument() });

    const frames = makePipe();
    const selection = makePipe();
    const bagW = { ...seed }, bagM = {};
    const store = (b) => ({ get: (k, d) => (k in b ? b[k] : d), set: (k, v) => { b[k] = v; return true; } });
    const prefs = store(bagW); prefs.method = store(bagM);

    const container = makeElement('div');
    const unmount = await registered.viewer.mount(container, {
        frames$: frames.iterator,
        selection$: selection.iterator,
        interpretations: PATHS,
        theme: { mode: 'dark', accent: '#4f46e5' },
        viewport: { width: 800, height: 600, on() { return () => {}; } },
        prefs,
    });
    await settle(2);

    const handle = (win.__bowireMapWidgets || []).find((h) => h.container === container) || {};

    // The bar is built into the container, not registered as a control.
    const bar = container.children.find(
        (c) => c.className === 'bowire-map-playback');

    return {
        recorder, frames, selection, unmount, handle, bar, bagW,
        points: () => recorder.sources['bowire-points'],
        lines: () => recorder.sources['bowire-lines'],
    };
}

const frameAt = (i, extra = {}) => ({
    id: 'f' + i, lat: 54 + i * 0.01, lon: 11.5, ...extra,
});

describe('map widget — time cursor and playback (#239)', { concurrency: 1 }, () => {

    it('stays out of the way until there is something to scrub', async () => {
        const m = await mountMap();
        assert.ok(m.bar, 'the bar is built with the widget');
        assert.equal(m.bar.style.display, 'none');

        m.frames.push(frameAt(0));
        await settle();
        assert.equal(m.bar.style.display, 'none', 'one frame is not a timeline');

        m.frames.push(frameAt(1));
        await settle();
        assert.equal(m.bar.style.display, 'flex');
        m.unmount();
    });

    it('pins the cursor to now while the stream is live', async () => {
        // The operator must not be able to fight an arriving frame: a
        // cursor holding a position while the tail moves would flicker
        // between the two.
        const m = await mountMap();
        for (let i = 0; i < 4; i++) m.frames.push(frameAt(i));
        await settle();

        const state = m.handle.playback();
        assert.equal(state.state, 'live');
        assert.equal(state.atTail, true);
        assert.equal(m.points().features.length, 4, 'everything that arrived is shown');
        m.unmount();
    });

    it('hands over to the scrubber when the stream ends', async () => {
        const m = await mountMap();
        for (let i = 0; i < 4; i++) m.frames.push(frameAt(i));
        await settle();
        m.frames.close();
        await settle();

        const state = m.handle.playback();
        assert.equal(state.streamEnded, true);
        assert.equal(state.state, 'paused', 'scrubbing is now available');
        assert.equal(state.atTail, true, 'and the picture has not moved');
        assert.equal(m.points().features.length, 4);
        m.unmount();
    });

    it('hides what had not happened yet', async () => {
        const m = await mountMap({ seed: { trajectory: true } });
        for (let i = 0; i < 5; i++) m.frames.push(frameAt(i));
        await settle();
        m.frames.close();
        await settle();

        m.handle.setCursorIndex(2);
        assert.equal(m.points().features.length, 3,
            'frames after the cursor are not on the map');
        assert.equal(m.lines().features[0].geometry.coordinates.length, 3,
            'and the trajectory stops there too — a path drawn past the '
            + 'moment being looked at shows the future');

        m.handle.setCursorIndex(4);
        assert.equal(m.points().features.length, 5, 'and comes back on the way forward');
        m.unmount();
    });

    it('counts what is on the map, not what arrived', async () => {
        // A browser run showed every legend row reading 46 next to
        // sixteen visible pins — the legend disagreeing with the map it
        // describes.
        const m = await mountMap();
        for (let i = 0; i < 6; i++) m.frames.push(frameAt(i));
        await settle();
        m.frames.close();
        await settle();

        const total = m.handle.tracks()[0];
        assert.equal(total.count, 6, 'the track keeps its full history');

        m.handle.setCursorIndex(2);
        const row = m.handle.visibleTrackCounts();
        assert.equal(row[total.key], 3, 'but the legend shows what is drawn');

        m.handle.setPlaybackState('live');
        assert.equal(m.handle.visibleTrackCounts()[total.key], 6,
            'and returns to the total when the cursor does');
        m.unmount();
    });

    it('keeps a selection the cursor has rewound past', async () => {
        // Rewinding must not be destructive: the operator scrubs
        // precisely to look around the frame they selected.
        const m = await mountMap();
        for (let i = 0; i < 5; i++) m.frames.push(frameAt(i));
        await settle();
        m.frames.close();
        await settle();

        m.selection.push({ selectedFrameIds: ['f4'] });
        await settle();

        m.handle.setCursorIndex(1);
        assert.equal(m.points().features.length, 2, 'the selected pin is out of view');

        m.handle.setCursorIndex(4);
        const selected = m.points().features.filter((f) => f.properties.selected === 'yes');
        assert.equal(selected.length, 1, 'and is still selected when the cursor passes it');
        assert.equal(selected[0].properties.frameId, 'f4');
        m.unmount();
    });

    it('rewinds before playing from the end', async () => {
        // Play at the tail has nothing to advance to; without the rewind
        // the button looks broken.
        const m = await mountMap();
        for (let i = 0; i < 5; i++) m.frames.push(frameAt(i));
        await settle();
        m.frames.close();
        await settle();
        assert.equal(m.handle.playback().atTail, true);

        m.handle.setPlaybackState('playing');
        const state = m.handle.playback();
        assert.equal(state.index, 0, 'playback starts from the beginning');
        m.handle.setPlaybackState('paused');
        m.unmount();
    });

    it('advances while playing and stops at the end', async () => {
        // Frames a few ms apart, so the whole run is over quickly.
        const m = await mountMap();
        for (let i = 0; i < 5; i++) m.frames.push(frameAt(i, { timestamp: i * 20 }));
        await settle();
        m.frames.close();
        await settle();

        m.handle.setPlaybackState('playing');
        await new Promise((r) => setTimeout(r, 600));

        const state = m.handle.playback();
        assert.equal(state.index, 4, 'it reached the end');
        assert.equal(state.state, 'paused',
            'and stopped rather than looping — a silent restart makes a '
            + 'long stream impossible to read');
        m.unmount();
    });

    it('returns to following the stream', async () => {
        const m = await mountMap();
        for (let i = 0; i < 5; i++) m.frames.push(frameAt(i));
        await settle();
        m.frames.close();
        await settle();

        m.handle.setCursorIndex(1);
        assert.equal(m.points().features.length, 2);

        m.handle.setPlaybackState('live');
        assert.equal(m.handle.playback().atTail, true);
        assert.equal(m.points().features.length, 5);
        m.unmount();
    });

    it('falls back to frame order when the stream carries no time', async () => {
        // A stream with no usable timestamp still needs an axis; arrival
        // order is the only thing the scrubber actually requires.
        const m = await mountMap();
        for (let i = 0; i < 4; i++) m.frames.push(frameAt(i));
        await settle();
        m.frames.close();
        await settle();

        assert.equal(m.handle.playback().frames, 4);
        m.handle.setCursorIndex(1);
        assert.equal(m.points().features.length, 2);
        m.unmount();
    });

    it('remembers the speed but not the position', async () => {
        // Speed is a property of the operator; where they happened to
        // stop looking is not, and #239 puts cursor persistence out of
        // scope explicitly.
        const m = await mountMap();
        for (let i = 0; i < 3; i++) m.frames.push(frameAt(i));
        await settle();
        m.frames.close();
        await settle();

        const speed = m.bar.children.find((c) => c.tagName === 'SELECT');
        speed.value = '4';
        speed.listeners.change.forEach((cb) => cb());
        assert.equal(m.bagW.playbackSpeed, 4);
        assert.equal(m.handle.playback().speed, 4);
        assert.ok(!('cursor' in m.bagW), 'the cursor position is not persisted');
        m.unmount();

        const again = await mountMap({ seed: { playbackSpeed: 4 } });
        assert.equal(again.handle.playback().speed, 4);
        again.unmount();
    });

    it('stops playing when the widget goes away', async () => {
        const m = await mountMap();
        for (let i = 0; i < 5; i++) m.frames.push(frameAt(i, { timestamp: i * 1000 }));
        await settle();
        m.frames.close();
        await settle();

        m.handle.setPlaybackState('playing');
        const before = m.handle.playback().index;
        m.unmount();
        await new Promise((r) => setTimeout(r, 400));
        assert.equal(m.handle.playback().index, before,
            'an unmounted widget must not keep walking its cursor');
    });
});
